using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Linq;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Alloy's decoders of the uncompressed texture formats, proved against the engine's before either takes
/// anything over. A TGA is decoded HERE FIRST, because <c>CResTGA::OnResourceServiced</c> mutates the resource
/// bytes in place, and the engine's pixels are compared after; a PLT reaches <c>ImageDecode</c> as raw
/// {index, layer} pairs. <c>alloy.rasterread</c>: 1 checks beside the engine, 2 owns the decode.
/// </summary>
public sealed unsafe partial class RasterReader : IExtenderService
{
    /// <summary>The <c>{pad[4]; short tag; void* owner;}</c> tail <c>CAuroraTexture::LoadTexture</c> writes past an
    /// RLE TGA's pixels; <c>ReadUnmappedRLETGA</c> allocates it (<c>new[](size + 0xe)</c>).</summary>
    internal const int TgaTagTail = 14;

    private static TgaServicedFn _serviced;
    private static ImageDecodeFn _decode;
    private static OperatorNewArrayFn _znam;
    private static bool _armed, _own;
    private static ExtenderLog? _log;
    private static long _tgaAgreed, _tgaMismatched, _tgaUnchecked, _pltAgreed, _pltMismatched, _pltUnchecked;
    private static long _tgaOurs, _tgaEngine, _pltOurs, _pltEngine;
    private const int LogCap = 8;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        int mode = Renderer.RasterRead.Boot;
        if (mode == 0) { log.Info("RasterReader: off (alloy.rasterread=0) -- the engine alone decodes TGA and PLT"); return; }
        _znam = EngineFn.OperatorNewArray;
        if (mode == 2 && _znam == null)
        {
            log.Always("RasterReader: REFUSED -- the engine's operator new[] is unresolved, so Alloy cannot hand it a buffer it will free; the engine alone decodes TGA and PLT");
            return;
        }
        _serviced = hooks.InstallTgaServiced(&OnServiced);
        _decode = hooks.InstallImageDecode(&OnDecode);
        _armed = _serviced != null && _decode != null;
        _own = _armed && mode == 2;
        if (_own) InstallForced(log, hooks);
        log.Info(!_armed ? "RasterReader: REFUSED -- CResTGA::OnResourceServiced or ImageDecode unresolved; the engine alone decodes TGA and PLT"
            : _own ? "RasterReader: OWNING -- Alloy decodes every TGA the engine parses and every PLT it composites; the engine's decoders run only for shapes Alloy does not reproduce (alloy.rasterread=1 checks instead)"
            : "RasterReader: armed -- every TGA the engine parses and every PLT it composites is decoded beside it and compared byte for byte (alloy.rasterread=2 owns, 0 disables)");
    }

    internal static string Note()
        => _own ? $"tga: {_tgaOurs} Alloy, {_tgaEngine} engine; plt: {_pltOurs} Alloy, {_pltEngine} engine" + ForcedNote()
            : _armed
            ? $"tga: {_tgaAgreed} agreed, {_tgaMismatched} MISMATCHED, {_tgaUnchecked} unchecked; plt: {_pltAgreed} agreed, {_pltMismatched} MISMATCHED, {_pltUnchecked} unchecked"
            : "tga/plt: engine";

    /// <summary>A TGA's decode, whoever runs it. It sits INSIDE <c>CRes::Demand</c>, which is where the read is
    /// timed, so the read bracket there subtracts this one.</summary>
    [UnmanagedCallersOnly]
    private static int OnServiced(IntPtr res)
    {
        long t0 = FrameZones.Now;
        int r = Serviced(res);
        FrameZones.Add(FrameZones.Zone.TexDecode, t0);
        return r;
    }

    private static int Serviced(IntPtr res)
    {
        var tga = new CResTGA(res);
        var cres = new CRes(res);
        byte* raw = (byte*)cres.Data;
        // Parsed, or state the engine refuses on sight: nothing is decoded, so nothing to check.
        if (!_armed || raw == null || tga.Parsed != 0 || tga.Header != IntPtr.Zero || tga.Pixels != IntPtr.Zero || tga.ColorMap != IntPtr.Zero)
            return _serviced(res);
        var bytes = new ReadOnlySpan<byte>(raw, cres.Size);
        Verdict v = ParseTga(bytes, out Tga t, out string? why);
        if (_own) return Own(res, raw, bytes, v, t, why);
        if (v == Verdict.Pixels && t.Beyond)
        {
            v = Verdict.Unchecked;
            why = t.Cmap != 0 ? "colour-mapped: Alloy expands the map, the engine uploads the raw indices -- not compared"
                              : "greyscale RLE: the engine refuses type 11, Alloy decodes it -- not compared";
        }
        byte* ours = null;
        if (v == Verdict.Pixels)
        {
            ours = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, t.Bytes));
            DecodeTga(bytes, t, new Span<byte>(ours, t.Bytes));
        }
        int r = _serviced(res);
        if (v == Verdict.Unchecked)
        {
            if (++_tgaUnchecked <= LogCap) _log?.Info($"RasterReader: TGA {Name(res)}: {why} -- unchecked");
        }
        else if (v == Verdict.Rejects ? r == 0
                 : r == 1 && FieldsAgree(tga, raw, bytes.Length, t)
                   && new ReadOnlySpan<byte>((byte*)tga.Pixels, t.Bytes).SequenceEqual(new ReadOnlySpan<byte>(ours, t.Bytes)))
            _tgaAgreed++;
        else if (++_tgaMismatched <= LogCap)
            _log?.Always($"RasterReader: TGA {Name(res)}: " + (v == Verdict.Rejects ? $"the engine parsed what this reader refuses ({why})"
                : r == 0 ? "the engine refused what this reader decodes"
                : !FieldsAgree(tga, raw, bytes.Length, t) ? $"parse state differs (size {tga.Size}, srcSize {tga.SrcSize}, compressed {tga.Compressed}, owned {tga.Owned}, colorMapped {tga.ColorMapped}, pixels at +{(byte*)tga.Pixels - raw})"
                : $"pixels differ from byte {FirstDiff((byte*)tga.Pixels, ours, t.Bytes)} of {t.Bytes} ({t.W}x{t.H}, type {t.Type}, {t.Px} B/px)") + " -- the engine's decode is used");
        if (ours != null) NativeMemory.Free(ours);
        return r;
    }

    /// <summary>Whether the engine's parse state is what <see cref="Own"/> writes for this file.</summary>
    private static bool FieldsAgree(CResTGA tga, byte* raw, int length, in Tga t)
        => tga.Header == (IntPtr)raw && tga.ColorMap == (IntPtr)(&raw[TgaHeaderBytes + raw[TgaIdLen]]) && tga.Parsed == 1
           && (t.Type == 10
               ? tga.Size == t.Bytes && tga.SrcSize == length - t.Offset && tga.Compressed == 1 && tga.Owned == 1
               : tga.Size == length - t.Offset && tga.Pixels == (IntPtr)(&raw[t.Offset]) && tga.Compressed == 0 && tga.Owned == 0
                 && tga.ColorMapped == 0);

    /// <summary><c>OnResourceServiced</c>'s success, done by Alloy: the same fields the engine's branch for the
    /// type writes, the pixels decoded in place or into an engine <c>new[]</c> its own <c>delete[]</c> frees. A
    /// colour-mapped file comes out expanded and its header's depth says so; a greyscale RLE one is written as
    /// type 10's branch would, at one byte per pixel (<c>LoadTexture</c> reads only w, h and depth after).</summary>
    private static int Own(IntPtr res, byte* raw, ReadOnlySpan<byte> bytes, Verdict v, in Tga t, string? why)
    {
        if (v != Verdict.Pixels)
        {
            if (++_tgaEngine <= LogCap) _log?.Info($"RasterReader: TGA {Name(res)}: {why} -- the engine's decode is used");
            return _serviced(res);
        }
        var tga = new CResTGA(res);
        bool fresh = t.Rle || t.Cmap != 0;
        byte* px = fresh ? (byte*)_znam((nuint)(t.Bytes + TgaTagTail)) : &raw[t.Offset];
        if (fresh) tga.Owned = 1;
        if (t.Type == 9 || t.Rle) { tga.SrcSize = bytes.Length - t.Offset; tga.Compressed = 1; }
        if (t.Cmap != 0) tga.ColorMapped = 1;
        DecodeTga(bytes, t, new Span<byte>(px, t.Bytes));
        // GetTGAAttrib reads the depth off the header: the engine must see the RGB(A) this is, not the 8-bit indices.
        if (t.Cmap != 0) raw[TgaBits] = (byte)(t.Px * 8);
        tga.Header = (IntPtr)raw;
        tga.ColorMap = (IntPtr)(&raw[TgaHeaderBytes + raw[TgaIdLen]]);
        tga.Pixels = (IntPtr)px;
        tga.Size = fresh ? t.Bytes : bytes.Length - t.Offset;
        tga.Parsed = 1;
        _tgaOurs++;
        return 1;
    }

    /// <summary>What <c>CAuroraInterface::GetRaster</c> @0x1123080 reports for a TGA: w, h, and bytes per pixel.</summary>
    internal readonly record struct Raster(int W, int H, int Px)
    {
        public int Bytes => W * H * Px;
    }

    /// <summary>Worker-safe: a TGA decoded into a buffer of ours, shaped as <c>GetRaster</c>'s answer -- the
    /// pixels, then the <see cref="TgaTagTail"/> the release reads its tag from. Null with a reason when the engine
    /// should read it instead: a shape this decoder does not reproduce, a colour-mapped or greyscale-RLE file while
    /// the engine's decode is the reference, or a 2-byte pixel, which <c>ReleaseRaster</c> takes for a 4PC.</summary>
    internal static byte* PrepareTga(ReadOnlySpan<byte> raw, out Raster r, out string? why)
    {
        r = default;
        Verdict v = ParseTga(raw, out Tga t, out why);
        if (v != Verdict.Pixels) { why ??= "the engine refuses it"; return null; }
        if (t.Beyond && !_own) { why = "decoded differently by the engine while it owns TGA decoding"; return null; }
        if (t.Px != 1 && t.Px != 3 && t.Px != 4) { why = $"{t.Px} bytes/pixel"; return null; }
        r = new Raster(t.W, t.H, t.Px);
        byte* px = (byte*)NativeMemory.Alloc((nuint)(r.Bytes + TgaTagTail));
        DecodeTga(raw, t, new Span<byte>(px, r.Bytes));
        return px;
    }

    /// <summary>The PLT composite is decode from end to end -- its pixels were read with the resource.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnDecode(byte* px, int w, int h, byte* palette, int paletteHeight, int* rows, int layers)
    {
        long t0 = FrameZones.Now;
        IntPtr r = Decode(px, w, h, palette, paletteHeight, rows, layers);
        FrameZones.Add(FrameZones.Zone.TexDecode, t0);
        return r;
    }

    private static IntPtr Decode(byte* px, int w, int h, byte* palette, int paletteHeight, int* rows, int layers)
    {
        if (!_armed || w <= 0 || h <= 0) return _decode(px, w, h, palette, paletteHeight, rows, layers);
        int n = w * h, top = 0;
        string? why = layers > PltMaxLayers ? $"{layers} layers overrun the engine's 15-row cache" : null;
        for (int i = 0; why == null && i < layers; i++)
            if (rows[i] < 0) why = $"layer {i} names palette row {rows[i]}";
            else top = Math.Max(top, rows[i]);
        var pairs = new ReadOnlySpan<byte>(px, n * 2);
        int stray = why == null ? PltStrays(pairs, layers) : -1;
        if (stray >= 0) why = $"pixel {stray} names layer {px[2 * stray + 1]} of {layers}";
        if (why != null)
        {
            if (++(_own ? ref _pltEngine : ref _pltUnchecked) <= LogCap)
                _log?.Info($"RasterReader: PLT {w}x{h}: {why} (the engine reads memory it does not own there) -- " + (_own ? "the engine's composite is used" : "unchecked"));
            return _decode(px, w, h, palette, paletteHeight, rows, layers);
        }
        var pal = new ReadOnlySpan<byte>(palette, (top + 1) * PltRowBytes);
        var rowSpan = new ReadOnlySpan<int>(rows, layers);
        if (_own)
        {
            // g_nImagesDecoded, the engine's only other effect, has no reader in either binary.
            uint* buf = (uint*)_znam((nuint)n * 4);
            DecodePlt(pairs, pal, rowSpan, new Span<uint>(buf, n));
            _pltOurs++;
            return (IntPtr)buf;
        }
        byte* engine = (byte*)_decode(px, w, h, palette, paletteHeight, rows, layers);
        if (engine == null) return IntPtr.Zero;
        uint* ours = (uint*)NativeMemory.Alloc((nuint)n * 4);
        DecodePlt(pairs, pal, rowSpan, new Span<uint>(ours, n));
        if (new ReadOnlySpan<byte>(engine, n * 4).SequenceEqual(new ReadOnlySpan<byte>(ours, n * 4))) _pltAgreed++;
        else if (++_pltMismatched <= LogCap)
            _log?.Always($"RasterReader: PLT {w}x{h}, {layers} layers: pixels differ from byte {FirstDiff(engine, (byte*)ours, n * 4)} -- the engine's composite is used");
        NativeMemory.Free(ours);
        return (IntPtr)engine;
    }

    private static int FirstDiff(byte* a, byte* b, int n)
    {
        int i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    /// <summary>The resref off the resource's key entry, for the log.</summary>
    private static string Name(IntPtr res)
    {
        var key = new CKeyTableEntry(new CRes(res).KeyEntry);
        return key.IsNull ? $"CRes 0x{res:x}" : Encoding.ASCII.GetString(key.ResRef).TrimEnd('\0');
    }

    /// <summary>Off-device: each TGA path (swap, greyscale, id/colour-map skip, RLE runs and raw packets across
    /// rows, a clipped last packet), each refusal and unchecked shape, and the PLT composite.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        static byte[] Tga(int type, int w, int h, int bits, int idLen, params byte[] body)
        {
            var b = new byte[TgaHeaderBytes + idLen + body.Length];
            b[TgaIdLen] = (byte)idLen; b[TgaType] = (byte)type; b[TgaBits] = (byte)bits;
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(TgaW), (ushort)w);
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(TgaH), (ushort)h);
            body.CopyTo(b, TgaHeaderBytes + idLen);
            return b;
        }
        bool Decodes(byte[] file, params byte[] want)
        {
            if (ParseTga(file, out Tga t, out _) != Verdict.Pixels) return false;
            var got = new byte[t.Bytes];
            DecodeTga(file, t, got);
            return got.AsSpan().SequenceEqual(want);
        }

        // uncompressed BGR -> RGB, one id byte skipped, no flip; and the same decoded in place
        ok &= Decodes(Tga(2, 2, 1, 24, 1, 1, 2, 3, 4, 5, 6), 3, 2, 1, 6, 5, 4);
        var inPlace = Tga(2, 2, 1, 24, 1, 1, 2, 3, 4, 5, 6);
        ok &= ParseTga(inPlace, out Tga ip, out _) == Verdict.Pixels;
        DecodeTga(inPlace, ip, inPlace.AsSpan(ip.Offset, ip.Bytes));
        ok &= inPlace.AsSpan(ip.Offset).SequenceEqual(new byte[] { 3, 2, 1, 6, 5, 4 });
        // greyscale verbatim
        ok &= Decodes(Tga(3, 3, 1, 8, 0, 7, 8, 9), 7, 8, 9);
        // RLE 32 bpp, 2x2: a run of 3 crossing the row, then a raw packet of 1
        ok &= Decodes(Tga(10, 2, 2, 32, 0, 0x82, 1, 2, 3, 4, 0x00, 5, 6, 7, 8),
                      3, 2, 1, 4, 3, 2, 1, 4, 3, 2, 1, 4, 7, 6, 5, 8);
        // RLE 24 bpp whose run of 4 overshoots 1x2: clipped at the pixels
        ok &= Decodes(Tga(10, 1, 2, 24, 0, 0x83, 1, 2, 3), 3, 2, 1, 3, 2, 1);
        // RLE source that runs out, refusals, and shapes left unchecked
        ok &= ParseTga(Tga(10, 2, 1, 24, 0, 0x00, 1, 2, 3), out _, out _) == Verdict.Unchecked;
        ok &= ParseTga(Tga(10, 2, 1, 24, 0, 0x81, 1, 2), out _, out _) == Verdict.Unchecked;
        ok &= ParseTga(Tga(10, 1, 1, 8, 0, 0x00, 1), out _, out _) == Verdict.Rejects;
        ok &= ParseTga(Tga(5, 1, 1, 24, 0, 1, 2, 3), out _, out _) == Verdict.Rejects;
        ok &= ParseTga(Tga(2, 1, 1, 16, 0, 1, 2), out _, out _) == Verdict.Unchecked;
        ok &= ParseTga(Tga(2, 2, 2, 24, 0, 1, 2, 3), out _, out _) == Verdict.Unchecked;

        // colour-mapped: two BGR entries from index 2; RLE (a run of 2, a raw 1) and uncompressed; an index off the map
        static byte[] Mapped(int type, int w, params byte[] indices)
        {
            var b = Tga(type, w, 1, 8, 0, new byte[] { 1, 2, 3, 4, 5, 6 }.Concat(indices).ToArray());
            b[TgaCmapType] = 1; b[TgaCmapFirst] = 2; b[TgaCmapLen] = 2; b[TgaCmapBits] = 24;
            return b;
        }
        ok &= Decodes(Mapped(9, 3, 0x81, 3, 0x00, 2), 6, 5, 4, 6, 5, 4, 3, 2, 1);
        ok &= Decodes(Mapped(1, 2, 3, 2), 6, 5, 4, 3, 2, 1);
        ok &= ParseTga(Mapped(9, 1, 0x00, 4), out _, out _) == Verdict.Unchecked;
        ok &= ParseTga(Mapped(1, 2, 2), out _, out _) == Verdict.Unchecked;

        // greyscale RLE, 3x1: a run of 2 then a raw 1, no swap; a 16-bit one is refused as the engine refuses it
        ok &= Decodes(Tga(11, 3, 1, 8, 0, 0x81, 7, 0x00, 9), 7, 7, 9);
        ok &= ParseTga(Tga(11, 1, 1, 16, 0, 0x80, 1, 2), out _, out _) == Verdict.Rejects;
        ok &= ParseTga(Tga(11, 3, 1, 8, 0, 0x81, 7), out _, out _) == Verdict.Unchecked;

        // PLT: two palette rows; layer 0 -> row 1, layer 1 -> row 0
        var pal = new byte[2 * PltRowBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(pal.AsSpan(PltRowBytes + 3 * 4), 0xAABBCCDD);
        BinaryPrimitives.WriteUInt32LittleEndian(pal.AsSpan(5 * 4), 0x11223344);
        var outp = new uint[2];
        DecodePlt(new byte[] { 3, 0, 5, 1 }, pal, new[] { 1, 0 }, outp);
        ok &= PltStrays(new byte[] { 3, 0, 5, 1 }, 2) == -1 && outp[0] == 0xAABBCCDD && outp[1] == 0x11223344;
        ok &= PltStrays(new byte[] { 3, 0, 5, 2 }, 2) == 1;

        // DDS unpack: a DXT1 block with c0 <= c1 is three-colour (the half-way grey, then black), clipped to 2x1;
        // a DXT5 block's colour is always four-colour and its alpha the BC4 ramp
        var rgb = new byte[6];
        BcnDecode.Unpack(new byte[] { 0, 0, 0xff, 0xff, 0b1001, 0, 0, 0 }, 2, 1, DdsReader.Fmt.Bc1, rgb);
        ok &= rgb.AsSpan().SequenceEqual(new byte[] { 255, 255, 255, 127, 127, 127 });
        var rgba = new byte[4];
        BcnDecode.Unpack(new byte[] { 255, 0, 1, 0, 0, 0, 0, 0, 0xff, 0xff, 0, 0, 0, 0, 0, 0 }, 1, 1, DdsReader.Fmt.Bc3, rgba);
        ok &= rgba.AsSpan().SequenceEqual(new byte[] { 255, 255, 255, 0 });
        // BC2: texel 0's four alpha bits 0x7 widen by 17
        BcnDecode.Unpack(new byte[] { 7, 0, 0, 0, 0, 0, 0, 0, 0xff, 0xff, 0, 0, 0, 0, 0, 0 }, 1, 1, DdsReader.Fmt.Bc2, rgba);
        ok &= rgba.AsSpan().SequenceEqual(new byte[] { 255, 255, 255, 119 });
        // BC7 mode 6: endpoints 0 and 127|p1 -> 0 and 255; the anchor texel's 3-bit index 7 weighs 30/64, the rest 15 -> 64/64.
        // Pillow decodes this block to 120,120,120,120 then 255s.
        UInt128 bc7 = 0x40;   // not-an-offset: the mode-6 prefix bit
        foreach (int at in (ReadOnlySpan<int>)[14, 28, 42, 56]) bc7 |= (UInt128)127 << at;
        bc7 |= ((UInt128)1 << 64) | ((UInt128)7 << 65) | (UInt128.MaxValue << 68);
        var blk = new byte[16];
        BinaryPrimitives.WriteUInt128LittleEndian(blk, bc7);
        var two = new byte[8];
        BcnDecode.Unpack(blk, 2, 1, DdsReader.Fmt.Bc7, two);
        ok &= two.AsSpan().SequenceEqual(new byte[] { 120, 120, 120, 120, 255, 255, 255, 255 });
        ok &= CubeStrip.SelfCheck();

        log.Info(ok ? "RasterReader.SelfCheck: passed" : "RasterReader.SelfCheck: FAILED");
        return ok;
    }
}
