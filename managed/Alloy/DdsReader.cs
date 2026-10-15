using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Alloy's reader of both DDS containers the engine accepts: the legacy 20-byte BioWare header (container 1) and
/// the 124-byte Microsoft one behind <c>'DDS '</c> (container 2). The <c>AurGetCompressedImage</c> and
/// <c>AurReleaseRaster</c> slots are swapped as a pair, and the getter wraps the engine's, so the format race and
/// the <c>CExoResMan</c> demand stay the engine's. <c>alloy.ddsread</c> turns the whole thing off.
/// </summary>
internal static unsafe partial class DdsReader
{
    /// <summary>The release tag's distance before level 0 for each DDS container: the header, then the
    /// engine's <c>{short restype; void* owner;}</c> (<c>ReleaseRaster</c> @0x1123180 reads -0x1e / -0x8a).</summary>
    private const int TagBytes = 10;
    private static int HeaderOf(int container) => container == ContainerMs ? MsHeader : LegacyHeader;
    private static int TagOff(int container) => HeaderOf(container) + TagBytes;
    /// <summary>What a buffer of ours carries at its tag slot: none of the engine's (0x7f1, 0x819, 0x80b, 3), so
    /// the engine's release refuses it with a logged error rather than calling through its null owner.</summary>
    private const short OurTag = 0x0cd5;   // not-an-offset: a tag value
    /// <summary>Headroom ahead of level 0 in our buffers: past the widest tag slot, 16-byte aligned.</summary>
    private const int Prefix = MsHeader + 16;
    /// <inheritdoc cref="Prefix"/>
    internal const int PrefixBytes = Prefix;

    // Data-symbol slots, not functions: the generator's EngineFn resolves a symbol as code, so these are typed here.
    private static delegate* unmanaged<IntPtr, int*, int*, int*, int*, int*, float*, byte*> _engGet;
    private static delegate* unmanaged<byte*, int, int, int, int, int, void> _engRelease;
    private static IntPtr* _getSlot, _releaseSlot;
    private static bool _armed;
    private static ExtenderLog? _log;
    private static long _agreed, _padded, _refused, _mismatched, _swaps;
    private const int LogCap = 8;

    /// <summary>Read the knob and resolve the two slots; the store waits for <see cref="Tick"/>.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        if (!Renderer.DdsRead.Boot) { log.Info("DdsReader: off (alloy.ddsread=0) -- the engine reads every DDS"); return; }
        _getSlot = (IntPtr*)Native.Sym(SymbolMap.AurGetCompressedImage);
        _releaseSlot = (IntPtr*)Native.Sym(SymbolMap.AurReleaseRaster);
        if (_getSlot == null || _releaseSlot == null)
        {
            log.Info("DdsReader: REFUSED -- AurGetCompressedImage or AurReleaseRaster unresolved; the engine reads every DDS");
            return;
        }
        _armed = true;
        log.Info("DdsReader: armed -- from the first Alloy frame every DDS the engine reads is re-parsed and checked, partial mip chains padded (alloy.ddsread=0 disables)");
    }

    /// <summary>Once a frame, main thread: store the pair once the <c>CAuroraInterface</c> constructor has
    /// filled the slots, and again if it ever refills them (it overwrites unconditionally). Getter last, so
    /// no buffer of ours exists before the release that frees it is in place.</summary>
    internal static void Tick()
    {
        if (!_armed) return;
        Probe();
        IntPtr get = (IntPtr)(delegate* unmanaged<IntPtr, int*, int*, int*, int*, int*, float*, byte*>)&OnGet;
        IntPtr rel = (IntPtr)(delegate* unmanaged<byte*, int, int, int, int, int, void>)&OnRelease;
        IntPtr g = *_getSlot, r = *_releaseSlot;
        if (g == get && r == rel) return;
        if (g == IntPtr.Zero || r == IntPtr.Zero) return;
        if (r != rel) _engRelease = (delegate* unmanaged<byte*, int, int, int, int, int, void>)r;
        if (g != get) _engGet = (delegate* unmanaged<IntPtr, int*, int*, int*, int*, int*, float*, byte*>)g;
        *_releaseSlot = rel;
        *_getSlot = get;
        _log?.Info($"DdsReader: decoder slots swapped (#{++_swaps})");
    }

    /// <summary>The release slot is ours, so a buffer of ours handed to the engine through any getter is freed here.</summary>
    internal static bool OwnsRelease
        => _releaseSlot != null && *_releaseSlot == (IntPtr)(delegate* unmanaged<byte*, int, int, int, int, int, void>)&OnRelease;

    internal static string Note()
        => _armed ? $"dds: {_agreed} agreed, {_padded} partial chains padded, {_mismatched} MISMATCHED, {_refused} refused{FormatsNote()}" : "dds: engine";

    /// <summary>Worker-safe: parse <paramref name="raw"/> and lay its padded mip chain out in a buffer shaped
    /// exactly as <see cref="OnGet"/>'s own answers are, with <see cref="Prefix"/> bytes of headroom for the
    /// release tag the main thread writes. Returns level 0, or null with <paramref name="why"/> set. No engine
    /// read, no GL, no shared state -- everything it needs is in the bytes it was handed.</summary>
    internal static byte* Prepare(ReadOnlySpan<byte> raw, out Info info, out string? why)
    {
        if (!Parse(raw, out info, out why)) return null;
        byte* mem = (byte*)NativeMemory.Alloc((nuint)(Prefix + info.ChainBytes));
        byte* ours = Headroom.Payload(mem, Prefix);
        CopyChain(raw, info, new Span<byte>(ours, (int)info.ChainBytes));
        return ours;
    }

    /// <summary>Release a <see cref="Prepare"/> buffer the engine never took.</summary>
    internal static void FreePrepared(byte* level0)
    {
        if (level0 != null) NativeMemory.Free(Headroom.Allocation(level0, Prefix));
    }

    /// <summary>Hand a <see cref="Prepare"/>d buffer to the engine as the getter's answer: the release tag, the
    /// out params, and the parse recorded for <see cref="Lookup"/>. Null (buffer freed) when the format is one
    /// only Alloy uploads and this session cannot; the caller then reads through the engine, which reaches
    /// <see cref="Beyond"/>'s own refusal for the same file and says so there.</summary>
    private static byte* Serve(byte* ours, in Info i, int* container, int* w, int* h, int* ch, int* size, float* alpha)
    {
        if (!EngineUploads(i.Format) && !Uploadable(i.Format, out _))
        {
            FreePrepared(ours);
            return null;
        }
        var tag = new DdsTag(ours, TagOff(i.Container));
        tag.Type = OurTag; tag.Owner = IntPtr.Zero;
        *container = i.Container; *w = i.W; *h = i.H; *ch = i.Channels; *size = i.Level0; *alpha = i.AlphaMean;
        _answers[(nint)ours] = i;
        return ours;
    }

    /// <summary>The prefetch worker's hand-back.</summary>
    private static byte* FromCache(IntPtr name, int* container, int* w, int* h, int* ch, int* size, float* alpha)
    {
        byte* ours = TexLoad.Take(name, out Info i);
        if (ours == null) return null;
        return Serve(ours, i, container, w, h, ch, size, alpha);
    }

    /// <summary>The slot's two halves timed apart -- the engine's getter is the resource READ (a DDS demand is
    /// pure I/O), everything after it is this reader's DECODE. Hot-path safe: four timestamps, no
    /// allocation.</summary>
    [UnmanagedCallersOnly]
    private static byte* OnGet(IntPtr name, int* container, int* w, int* h, int* ch, int* size, float* alpha)
    {
        // The forced-uncompressed path wants level 0 of every face raw, not a 2D chain, so it always reads.
        if (!ForUnpack)
        {
            byte* prepared = FromCache(name, container, w, h, ch, size, alpha);
            if (prepared != null) return prepared;
        }
        long tr = FrameZones.Now;
        byte* data = _engGet(name, container, w, h, ch, size, alpha);
        FrameZones.Add(FrameZones.Zone.TexRead, tr);
        if (data == null) return null;
        long td = FrameZones.Now;
        byte* r = Reparse(name, data, container, w, h, ch, size, alpha);
        FrameZones.Add(FrameZones.Zone.TexDecode, td);
        return r;
    }

    /// <summary>The engine's answer re-parsed and checked against every out param and the level-0 address: a
    /// disagreement leaves the engine's buffer standing, counted and logged; an agreed full chain is handed back as
    /// is; a partial chain -- which the engine's unbounded mip walk reads past the end of -- is copied into a
    /// padded buffer of ours, tagged so our own release frees it.</summary>
    private static byte* Reparse(IntPtr name, byte* data, int* container, int* w, int* h, int* ch, int* size, float* alpha)
    {
        int c = *container;
        if (c != ContainerLegacy && c != ContainerMs) return data;   // KTX: the engine's alone
        // The owner's CRes is found through the engine's own tag slot; its data must be where the header ends.
        IntPtr owner = new DdsTag(data, TagOff(c)).Owner;
        var res = new CRes(owner == IntPtr.Zero ? IntPtr.Zero : new CAuroraCompressedTextureDDS(owner).Res);
        if (res.IsNull || res.Data == IntPtr.Zero)
        {
            if (++_refused <= LogCap) Disagree(name, "no resource behind the pointer");
            return data;
        }
        byte* raw = (byte*)res.Data;
        var bytes = new ReadOnlySpan<byte>(raw, res.Size);
        if (!Parse(bytes, out Info i, out string? why))
        {
            if (!i.EngineReads) return Drop(name, data, c, w, h, ch, size, why!);
            if (++_refused <= LogCap) Disagree(name, why!);
            return data;
        }
        if (!i.EngineReads) return Beyond(name, data, bytes, i, container, w, h, ch, size, alpha);
        if (i.Container != c || i.W != *w || i.H != *h || i.Channels != *ch || i.Level0 != *size
            || i.AlphaMean != *alpha || Headroom.Payload(raw, i.DataOffset) != data)
        {
            if (++_mismatched <= LogCap)
                Disagree(name, $"engine c{c} {*w}x{*h} ch{*ch} l0 {*size} a{*alpha} at +{data - raw} vs ours c{i.Container} {i.W}x{i.H} ch{i.Channels} l0 {i.Level0} a{i.AlphaMean} at +{i.DataOffset}");
            return data;
        }
        _agreed++;
        // The forced path decodes level 0 and releases at once, so it is never padded, and a cube's faces follow in the resource.
        if (ForUnpack) { _answers[(nint)data] = i; return data; }
        if (i.Present == i.Levels) return data;
        byte* mem = (byte*)NativeMemory.Alloc((nuint)(Prefix + i.ChainBytes));
        byte* ours = Headroom.Payload(mem, Prefix);
        CopyChain(bytes, i, new Span<byte>(ours, (int)i.ChainBytes));
        var tag = new DdsTag(ours, TagOff(c));
        tag.Type = OurTag; tag.Owner = IntPtr.Zero;
        _engRelease(data, *w, *h, *ch, *size, c);
        if (++_padded <= LogCap)
            _log?.Info($"DdsReader: {Marshal.PtrToStringAnsi(name)} carries {i.Present} of {i.Levels} mip levels -- padded (the engine would read past the resource)");
        return ours;
    }

    [UnmanagedCallersOnly]
    private static void OnRelease(byte* data, int w, int h, int ch, int size, int container)
    {
        _answers.Remove((nint)data);
        if (size == 0 && TexLoad.FreeRaster(data, w, h, ch)) return;
        if (data != null && size != 0 && (container == ContainerLegacy || container == ContainerMs)
            && new DdsTag(data, TagOff(container)).Type == OurTag)
        { NativeMemory.Free(Headroom.Allocation(data, Prefix)); return; }
        _engRelease(data, w, h, ch, size, container);
    }

    /// <summary>The engine's answer stands; say so loudly (callers cap it, so the string is built only then).</summary>
    private static void Disagree(IntPtr name, string why)
        => _log?.Always($"DdsReader: {Marshal.PtrToStringAnsi(name)}: {why} -- the engine's read is used");

    /// <summary>Off-device: both headers, the FourCC vocabulary, the engine's alpha-mean rule, and a partial
    /// chain padded rather than overread.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        // legacy 8x8 DXT1, full chain: 8x8 = 4 blocks, 4x4 = 1, 2x2 = 1, 1x1 = 1 -> 7 blocks * 8
        byte[] leg = new byte[LegacyHeader + 56];
        BinaryPrimitives.WriteUInt32LittleEndian(leg, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(leg.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(leg.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(leg.AsSpan(12), 32);
        BinaryPrimitives.WriteSingleLittleEndian(leg.AsSpan(16), 0.75f);
        ok &= Parse(leg, out Info a, out _) && a.Container == ContainerLegacy && a.W == 8 && a.Levels == 4
              && a.Present == 4 && a.Level0 == 32 && a.ChainBytes == 56 && a.AlphaMean == 0.75f && a.DataOffset == LegacyHeader;

        // Microsoft 8x4 DXT5 with only level 0 present (a partial chain): 2 blocks * 16
        byte[] ms = new byte[MsHeader + 32];
        BinaryPrimitives.WriteUInt32LittleEndian(ms, MagicDds);
        BinaryPrimitives.WriteUInt32LittleEndian(ms.AsSpan(MsHeight), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(ms.AsSpan(MsWidth), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(ms.AsSpan(MsFourCC), FourDxt5);
        for (int i = 0; i < 32; i++) ms[MsHeader + i] = (byte)(i + 1);
        ok &= Parse(ms, out Info b, out _) && b.Container == ContainerMs && b.W == 8 && b.H == 4 && b.Channels == 4
              && b.AlphaMean == 0.5f && b.Levels == 4 && b.Present == 1 && b.ChainBytes == 32 + 16 * 3;
        var chain = new byte[b.ChainBytes];
        CopyChain(ms, b, chain);
        ok &= chain[0] == 1 && chain[31] == 32 && chain[32] == 1 && chain[47] == 16 && chain[79] == 16;

        // vocabulary: ATI1/ATI2 map to 1/2 channels and their block sizes; DXT3 is Alloy's alone (BC2, 16-byte
        // blocks); premultiplied DXT2 is refused, and the engine is not handed it either
        BinaryPrimitives.WriteUInt32LittleEndian(ms.AsSpan(MsFourCC), FourAti1);
        ok &= Parse(ms, out Info c, out _) && c.Channels == 1 && c.Level0 == 16 && c.AlphaMean == 1.0f && c.EngineReads;
        BinaryPrimitives.WriteUInt32LittleEndian(ms.AsSpan(MsFourCC), FourDxt3);
        ok &= Parse(ms, out Info d3, out _) && d3.Format == Fmt.Bc2 && d3.Channels == 4 && !d3.EngineReads;
        BinaryPrimitives.WriteUInt32LittleEndian(ms.AsSpan(MsFourCC), 0x32545844);   // not-an-offset: FourCC 'DXT2'
        ok &= !Parse(ms, out Info d2, out string? why) && why != null && !d2.EngineReads;
        ok &= !Parse(leg.AsSpan(0, LegacyHeader + 8), out _, out _);   // level 0 does not fit

        // DX10: BC7 sRGB 8x8 (4 blocks + 1 + 1 + 1), read by Alloy alone and reported as 4 channels of 16-byte blocks
        byte[] dx = new byte[Dx10Header + 7 * 16];
        ms.AsSpan(0, MsHeader).CopyTo(dx);
        BinaryPrimitives.WriteUInt32LittleEndian(dx.AsSpan(MsHeight), 8);
        BinaryPrimitives.WriteUInt32LittleEndian(dx.AsSpan(MsFourCC), FourDx10);
        BinaryPrimitives.WriteUInt32LittleEndian(dx.AsSpan(Dx10Format), 99);
        BinaryPrimitives.WriteUInt32LittleEndian(dx.AsSpan(Dx10Dim), Dim2D);
        BinaryPrimitives.WriteUInt32LittleEndian(dx.AsSpan(Dx10Array), 1);
        ok &= Parse(dx, out Info d, out _) && !d.EngineReads && d.Format == Fmt.Bc7 && d.Srgb && d.Channels == 4
              && d.DataOffset == Dx10Header && d.Present == 4 && d.Faces == 1 && !EngineUploads(d.Format);
        BinaryPrimitives.WriteUInt32LittleEndian(dx.AsSpan(Dx10Array), 2);
        ok &= !Parse(dx, out Info arr, out _) && !arr.EngineReads;   // an array is refused, and the engine must not get it either
        // a DX10 cube needs six whole face chains: the 8x8 file above holds one
        BinaryPrimitives.WriteUInt32LittleEndian(dx.AsSpan(Dx10Array), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(dx.AsSpan(Dx10Misc), MiscCube);
        ok &= !Parse(dx, out _, out _);
        byte[] cube = new byte[Dx10Header + 6 * 7 * 16];
        dx.AsSpan(0, Dx10Header).CopyTo(cube);
        BinaryPrimitives.WriteUInt32LittleEndian(cube.AsSpan(MsMips), 4);
        ok &= Parse(cube, out Info cu, out _) && cu.Faces == 6 && cu.FaceStride == 7 * 16 && cu.Present == 4;

        log.Info(ok ? "DdsReader.SelfCheck: passed" : "DdsReader.SelfCheck: FAILED");
        return ok;
    }
}
