using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The DDS files the engine's reader cannot answer -- a <c>DX10</c> header, a DXT3 or BC4U/BC5U-style FourCC, a
/// real cube map. <c>GetCompressedRaster</c> still demands such a file and returns 1, with 0 channels and the
/// data pointer at <c>header + 128</c> even behind the 20-byte DX10 extension, so vanilla uploads garbage. Here
/// the parse is Alloy's alone, and a format only Alloy uploads is served only while it owns the upload.
/// </summary>
internal static unsafe partial class DdsReader
{
    /// <summary>Set by <see cref="RasterReader"/> around its getter call: the answer is decoded on the CPU and
    /// released at once, so it is never padded, a cube keeps all six faces (packed into the engine's strip by
    /// <see cref="CubeStrip"/>; anywhere else the engine sees face 0), and no upload has to be owned.</summary>
    internal static bool ForUnpack;
    /// <summary>What each live answer that needs it was parsed as, keyed by the pointer handed out; dropped by the release.</summary>
    private static readonly Dictionary<nint, Info> _answers = new();
    private static long _beyond, _beyondRefused;
    /// <summary>Formats the driver probe accepted, as a bit per <see cref="Fmt"/>; the engine's four are not probed.</summary>
    private static int _probed = -1;

    /// <summary>What <paramref name="data"/> (a pointer this reader handed out and has not seen released) was parsed as.</summary>
    internal static bool Lookup(byte* data, out Info info) => _answers.TryGetValue((nint)data, out info);

    /// <summary>A format only Alloy can upload, and this session's driver said it can.</summary>
    private static bool Uploadable(Fmt f, out string? no)
    {
        no = !OwnedTextures.OwnsUploads ? "Alloy does not own the upload (alloy.ownedtex/texsingle off, or no 4.6 context yet)"
           : _probed < 0 ? "the driver probe has not run yet"
           : (_probed & (1 << (int)f)) == 0 ? "the driver refused it at the probe"
           : null;
        return no == null;
    }

    /// <summary>The answer for a file the engine's reader gets wrong. The engine's buffer is always released here.</summary>
    private static byte* Beyond(IntPtr name, byte* data, ReadOnlySpan<byte> bytes, in Info i, int* container, int* w, int* h, int* ch, int* size, float* alpha)
    {
        int c = *container;
        if (!ForUnpack && !EngineUploads(i.Format) && !Uploadable(i.Format, out string? no))
            return Drop(name, data, c, w, h, ch, size, $"{Describe(i)}: {no}");
        // The forced path decodes level 0 of every face straight from the copy; a 2D answer is the padded chain the engine walks.
        long n = !ForUnpack ? i.ChainBytes : i.Faces == 6 ? 5 * i.FaceStride + i.Level0 : i.Level0;
        byte* mem = (byte*)NativeMemory.Alloc((nuint)(PrefixBytes + n));
        byte* ours = (byte*)Headroom<DdsMsPrefix>.PayloadOf(mem);
        if (ForUnpack) bytes.Slice(i.DataOffset, (int)n).CopyTo(new Span<byte>(ours, (int)n));
        else CopyChain(bytes, i, new Span<byte>(ours, (int)n));
        var tag = new DdsTag(ours, c == ContainerMs);
        tag.Type = OurTag; tag.Owner = IntPtr.Zero;
        _engRelease(data, *w, *h, *ch, *size, c);
        *w = i.W; *h = i.H; *ch = i.Channels; *size = i.Level0; *alpha = i.AlphaMean;
        _answers[(nint)ours] = i;
        if (++_beyond <= LogCap)
            _log?.Info($"DdsReader: {Marshal.PtrToStringAnsi(name)} is {Describe(i)} -- read by Alloy (the engine's reader knows only DXT1/DXT5/ATI1/ATI2 behind one header)"
                       + (EngineUploads(i.Format) || ForUnpack ? "" : "; Alloy uploads it"));
        return ours;
    }

    /// <summary>Release the engine's buffer and answer nothing, loudly: the engine's fallbacks (16-bit, TGA, its
    /// failed-texture flag) take over.</summary>
    private static byte* Drop(IntPtr name, byte* data, int c, int* w, int* h, int* ch, int* size, string why)
    {
        _engRelease(data, *w, *h, *ch, *size, c);
        if (++_beyondRefused <= LogCap)
            _log?.Always($"DdsReader: {Marshal.PtrToStringAnsi(name)} NOT LOADED -- {why} (the engine's own read of it is garbage)");
        return null;
    }

    private static string Describe(in Info i)
        => $"{i.Format}{(i.Srgb ? " sRGB (sampled as UNORM)" : "")}{(i.Faces == 6 ? " cube" : "")} {i.W}x{i.H}";

    /// <summary>Once, main thread, engine context, after the first Alloy frame has probed VRAM: ask the driver
    /// about every format only Alloy uploads -- supported, stored compressed, block shape -- and, for BC7, measure
    /// the free-VRAM drop across a 2048x2048 chain, since no query can tell a driver that decompresses behind the
    /// enum. A format refused here is refused at the getter.</summary>
    private static void Probe()
    {
        if (_probed < 0 && Renderer.OnVulkan)
        {
            // the device was refused without textureCompressionBC, which carries every BC format in the probe list
            _probed = 0;
            foreach (Fmt f in (ReadOnlySpan<Fmt>)[Fmt.Bc2, Fmt.Bc4S, Fmt.Bc5S, Fmt.Bc6U, Fmt.Bc6S, Fmt.Bc7]) _probed |= 1 << (int)f;
            _log?.Info("DdsReader: [formats] Vulkan -- BC2/4/5/6H/7 sampled natively (textureCompressionBC, required of the device)");
            return;
        }
        if (_probed >= 0 || !GlContextService.Available || !AlloyRenderer.VramProbed || Gl.GetInternalformativ == null) return;
        _probed = 0;
        var line = new System.Text.StringBuilder("DdsReader: [formats]");
        foreach (Fmt f in (ReadOnlySpan<Fmt>)[Fmt.Bc2, Fmt.Bc4S, Fmt.Bc5S, Fmt.Bc6U, Fmt.Bc6S, Fmt.Bc7])
        {
            uint gf = GlFormat(f);
            int sup = 0, comp = 0, bw = 0, bh = 0, bs = 0;
            Gl.GetInternalformativ(Gl.TEXTURE_2D, gf, Gl.INTERNALFORMAT_SUPPORTED, 1, &sup);
            Gl.GetInternalformativ(Gl.TEXTURE_2D, gf, Gl.TEXTURE_COMPRESSED, 1, &comp);
            Gl.GetInternalformativ(Gl.TEXTURE_2D, gf, Gl.TEXTURE_COMPRESSED_BLOCK_WIDTH, 1, &bw);
            Gl.GetInternalformativ(Gl.TEXTURE_2D, gf, Gl.TEXTURE_COMPRESSED_BLOCK_HEIGHT, 1, &bh);
            Gl.GetInternalformativ(Gl.TEXTURE_2D, gf, Gl.TEXTURE_COMPRESSED_BLOCK_SIZE, 1, &bs);
            bool ok = sup != 0 && comp != 0;
            if (ok) _probed |= 1 << (int)f;
            line.Append($" {f}={(ok ? "yes" : "NO")}(supported {sup}, compressed {comp}, block {bw}x{bh}/{bs})");
            if (f == Fmt.Bc7 && ok) line.Append(VramDelta(gf));
        }
        Gl.DrainErrors(8);
        _log?.Info(line.ToString());
    }

    /// <summary>Side of the probe's measured chain: 4 MiB of BC7 at level 0, 16 MiB if the driver expands it to RGBA8.</summary>
    private const int ProbeSide = 2048;

    /// <summary>Allocate and fill a <see cref="ProbeSide"/> BC7 chain, wait for the GPU, read the free pool before
    /// and after. The ratio against the compressed size is the verdict: ~1 native, ~4 decompressed.</summary>
    private static string VramDelta(uint gf)
    {
        long before = AlloyRenderer.VramFreeKibNow();
        if (before < 0) return ", vram delta not measured (no memory query)";
        int levels = 0;
        for (int s = ProbeSide; s > 0; s >>= 1) levels++;
        long chain = 0;
        for (int s = ProbeSide; s > 0; s >>= 1) chain += (long)Math.Max(1, s / 4) * Math.Max(1, s / 4) * 16;
        byte* zero = (byte*)NativeMemory.AllocZeroed((nuint)(ProbeSide / 4 * (ProbeSide / 4) * 16));
        int svUnpackBuf; Gl.GetIntegerv(Gl.PIXEL_UNPACK_BUFFER_BINDING, &svUnpackBuf);
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, 0);
        uint t; Gl.CreateTextures(Gl.TEXTURE_2D, 1, &t);
        Gl.TextureStorage2D(t, levels, gf, ProbeSide, ProbeSide);
        for (int l = 0, s = ProbeSide; l < levels; l++, s >>= 1)
            Gl.CompressedTextureSubImage2D(t, l, 0, 0, s, s, gf, Math.Max(1, s / 4) * Math.Max(1, s / 4) * 16, zero);
        if (svUnpackBuf != 0) Gl.BindBuffer(Gl.PIXEL_UNPACK_BUFFER, (uint)svUnpackBuf);
        IntPtr sync = Gl.FenceSync(Gl.SYNC_GPU_COMMANDS_COMPLETE, 0);
        Gl.ClientWaitSync(sync, Gl.SYNC_FLUSH_COMMANDS_BIT, ProbeWaitNs);
        Gl.DeleteSync(sync);
        long after = AlloyRenderer.VramFreeKibNow();
        Gl.DeleteTextures(1, &t);
        NativeMemory.Free(zero);
        long used = before - after;
        return $", vram {used} KiB for a {chain / 1024} KiB chain ({used * 1024.0 / chain:F1}x{(used * 1024 > chain * 2 ? " -- the driver looks to DECOMPRESS it" : "")})";
    }

    /// <summary>The probe's one GPU wait: 100 ms, far past a 5 MiB upload.</summary>
    private const ulong ProbeWaitNs = 100_000_000;

    private static string FormatsNote() => $"; dds beyond the engine: {_beyond} read, {_beyondRefused} NOT LOADED";
}
