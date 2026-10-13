using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CX.Alloy;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Radial;

/// <summary>
/// The part of an icon sheet its art fills, read from the art itself: the 32-texel cells holding every visible texel,
/// counted from the sheet's top-left the way the engine's icon crop counts them. Read once per resref from the file the
/// texture registry would load (DDS, then TGA; PLT for a tinted layer), so the art decides its shape, not its base item.
/// </summary>
internal static unsafe class IconFrames
{
    /// <summary>A sheet <see cref="Tw"/> x <see cref="Th"/> whose art fills its top-left <see cref="Cw"/> x <see cref="Ch"/>
    /// texels. Zero cells = unknown, and the caller keeps its own crop.</summary>
    internal readonly struct Frame(int tw, int th, int cw, int ch)
    {
        public readonly short Tw = (short)tw, Th = (short)th, Cw = (short)cw, Ch = (short)ch;
        public bool Known => Cw > 0 && Ch > 0 && Tw > 0 && Th > 0;
    }

    /// <summary>Texels of one inventory cell on a stock icon sheet.</summary>
    internal const int CellPx = 32;
    /// <summary>An alpha (or, with no alpha channel, a brightest channel) at or under this is background.</summary>
    private const byte Faint = 8;
    /// <summary>The PLT intensity every icon palette's skin and leather rows leave transparent: the background value of
    /// every stock icon PLT.</summary>
    private const byte PltClear = 255;
    private const ushort TgaResType = 3, PltResType = 6, DdsResType = 0x7f1;   // not-an-offset: restype ids
    /// <summary>Keeps a PLT and a plain texture of one name apart in the cache.</summary>
    private const ulong PltSalt = 0x9e3779b97f4a7c15UL;   // not-an-offset: hash salt
    private const int MaxSide = 1024;
    /// <summary>Frames the cache holds; a full cache is emptied and refilled by the next asks.</summary>
    private const int MaxCached = 4096;

    /// <summary>Every frame read this session, keyed by resref, misses included.</summary>
    private sealed class IconFramesState
    {
        public readonly Dictionary<ulong, Frame> ByName = new();
        public ExtenderLog? Log;
    }

    private static readonly IconFramesState _s = new();

    internal static void Init(ExtenderLog log) => _s.Log = log;

    /// <summary>The frame of an engine <c>CGuiIcon*</c>: every layer's, the first layer's sheet setting the size.</summary>
    internal static Frame Of(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return default;
        Span<byte> resref = new CGuiIcon(icon).Resref;
        switch (RadialIcons.Classify(icon))
        {
            case IconKind.Layered:
                return Read(resref, true);
            case IconKind.Composite:
                var composite = new CCompositeIcon(icon);
                return Union(Union(Read(resref, false), Read(composite.Resref2, false)), Read(composite.Resref3, false));
            case IconKind.Armor:
                Span<byte> names = new CArmorIcon(icon).Resrefs;
                Frame f = default;
                for (int slot = 0; slot < EngineLayout.ArmorIconLayers; slot++)
                {
                    Span<byte> name = names.Slice(slot * EngineLayout.ArmorIconResRefStride, EngineLayout.ArmorIconResRefStride);
                    if (name[0] != 0) f = Union(f, Read(name, true));
                }
                return f;
            default:
                return Read(resref, false);
        }
    }

    /// <summary>The frame of one plain or PLT resref, read on the first ask and cached, a miss included.</summary>
    internal static Frame Read(ReadOnlySpan<byte> resref, bool plt)
    {
        byte* name = stackalloc byte[RadialIcons.ResRefLen + 1];
        if (RadialIcons.CName(resref, name) == 0) return default;
        ulong key = ResLocate.Key(name) ^ (plt ? PltSalt : 0);
        IconFramesState s = _s;
        if (s.ByName.TryGetValue(key, out Frame f)) return f;
        string n = Marshal.PtrToStringAnsi((IntPtr)name) ?? "";
        f = Load(n, plt, out string? why);
        if (s.ByName.Count >= MaxCached) s.ByName.Clear();
        s.ByName[key] = f;
        if (f.Known) s.Log?.Info($"IconFrames: {n} is {f.Tw}x{f.Th}, its art fills {f.Cw}x{f.Ch}");
        else s.Log?.Info($"IconFrames: {n} -- {why ?? "no visible texel"}; it keeps its cell crop");
        return f;
    }

    private static Frame Load(string name, bool plt, out string? why)
    {
        why = null;
        if (!ResLocate.Ready && !ResLocate.Attach(out why)) return default;
        if (plt)
        {
            byte[]? raw = TexRegistry.ReadResourceBytes(name, PltResType, out why);
            if (raw == null) { why ??= "no PLT"; return default; }
            if (!PltImage.Parse(raw, out int w, out int h, out _, out bool rle, out why) || w > MaxSide || h > MaxSide) return default;
            byte[] pairs = new byte[w * h * PltImage.BytesPerPixel];
            if (rle) { if (!PltImage.Unrle(raw.AsSpan(PltImage.HeaderBytes), pairs, out why)) return default; }
            else raw.AsSpan(PltImage.HeaderBytes, pairs.Length).CopyTo(pairs);
            return ScanPlt(pairs, w, h);
        }
        byte[]? dds = TexRegistry.ReadResourceBytes(name, DdsResType, out why);
        if (dds != null)
        {
            if (!DdsReader.Parse(dds, out DdsReader.Info info, out why)) return default;
            if (!BcnDecode.Decodes(info.Format) || info.W > MaxSide || info.H > MaxSide) { why = $"a {info.Format} DDS"; return default; }
            if (info.Present < 1 || info.Faces != 1 || (long)dds.Length < (long)info.DataOffset + info.Level0) { why = "a DDS shorter than its first level"; return default; }
            int bpp = BcnDecode.PixelBytes(info.Format);
            byte[] px = new byte[info.W * info.H * bpp];
            BcnDecode.Unpack(dds.AsSpan(info.DataOffset), info.W, info.H, info.Format, px);
            return ScanRaster(px, info.W, info.H, bpp);
        }
        byte[]? tga = TexRegistry.ReadResourceBytes(name, TgaResType, out why);
        if (tga == null) { why ??= "no DDS or TGA"; return default; }
        if (RasterReader.ParseTga(tga, out RasterReader.Tga t, out why) != RasterReader.Verdict.Pixels) return default;
        if (t.Px is not (3 or 4) || t.W > MaxSide || t.H > MaxSide) { why = $"a {t.Px * 8}-bit TGA"; return default; }
        byte[] dst = new byte[t.Bytes];
        RasterReader.DecodeTga(tga, t, dst);
        return ScanRaster(dst, t.W, t.H, t.Px);
    }

    /// <summary>The frame of RGB or RGBA texels in file order, rows bottom-up as the engine uploads them.</summary>
    internal static Frame ScanRaster(ReadOnlySpan<byte> px, int w, int h, int bpp)
    {
        int right = 0, low = h;
        for (int row = 0; row < h; row++)
            for (int x = 0; x < w; x++)
            {
                ReadOnlySpan<byte> p = px.Slice((row * w + x) * bpp, bpp);
                bool seen = bpp == 4 ? p[3] > Faint : Math.Max(p[0], Math.Max(p[1], p[2])) > Faint;
                if (!seen) continue;
                if (x + 1 > right) right = x + 1;
                if (row < low) low = row;
            }
        return FromExtent(w, h, right, low);
    }

    /// <summary>The frame of a PLT's (intensity, layer) pairs, rows bottom-up; <see cref="PltClear"/> is background.</summary>
    internal static Frame ScanPlt(ReadOnlySpan<byte> pairs, int w, int h)
    {
        int right = 0, low = h;
        for (int row = 0; row < h; row++)
            for (int x = 0; x < w; x++)
            {
                if (pairs[(row * w + x) * 2] == PltClear) continue;
                if (x + 1 > right) right = x + 1;
                if (row < low) low = row;
            }
        return FromExtent(w, h, right, low);
    }

    /// <summary>A <paramref name="w"/> x <paramref name="h"/> sheet whose visible texels end at column <paramref name="right"/>
    /// and reach down to bottom-up row <paramref name="low"/>: the cells from the top-left that hold them.</summary>
    internal static Frame FromExtent(int w, int h, int right, int low)
    {
        if (right <= 0 || low >= h) return default;
        return new Frame(w, h, Math.Min(w, Snap(right)), Math.Min(h, Snap(h - low)));
    }

    private static int Snap(int texels) => (texels + CellPx - 1) / CellPx * CellPx;

    /// <summary>Two layers' frames as one; a layer on a sheet of another size is left out, as the composite leaves it.</summary>
    internal static Frame Union(Frame a, Frame b)
    {
        if (!a.Known) return b;
        if (!b.Known || a.Tw != b.Tw || a.Th != b.Th) return a;
        return new Frame(a.Tw, a.Th, Math.Max(a.Cw, b.Cw), Math.Max(a.Ch, b.Ch));
    }

    /// <summary>The size of <paramref name="icon"/> drawn at <paramref name="cellPx"/> pixels a cell: the cells its art fills, or
    /// <paramref name="cellsW"/> x <paramref name="cellsH"/> when the frame is unknown.</summary>
    internal static void Size(IntPtr icon, float cellPx, int cellsW, int cellsH, out float w, out float h)
    {
        Frame f = Of(icon);
        w = f.Known ? cellPx * f.Cw / CellPx : cellPx * cellsW;
        h = f.Known ? cellPx * f.Ch / CellPx : cellPx * cellsH;
    }

    /// <summary>The frame's art in the box at its own proportions, centred, and the UVs that crop the sheet to it
    /// (engine textures are bottom-up, so the art's top-left is v = 1). An unknown frame fills the box with the whole sheet.</summary>
    internal static void Fit(in Frame f, ref float x, ref float y, ref float w, ref float h, out float u1, out float v0)
    {
        u1 = 1f; v0 = 0f;
        if (!f.Known || w <= 0f || h <= 0f) return;
        float scale = Math.Min(w / f.Cw, h / f.Ch), fw = f.Cw * scale, fh = f.Ch * scale;
        x += (w - fw) / 2f; y += (h - fh) / 2f; w = fw; h = fh;
        u1 = f.Cw / (float)f.Tw;
        v0 = 1f - f.Ch / (float)f.Th;
    }

    /// <summary>Oracle: hand-built sheets and hand-computed rectangles, measured off the stock Large Shield (64x128, art
    /// in its top 64x96) and a 1x2 sheet.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        byte[] rgba = new byte[64 * 128 * 4];
        rgba[(41 * 64 + 57) * 4 + 3] = 255;
        rgba[(114 * 64 + 6) * 4 + 3] = 200;
        rgba[(0 * 64 + 63) * 4 + 3] = 5;
        Frame shield = ScanRaster(rgba, 64, 128, 4);
        check.Expect(shield.Known && shield.Tw == 64 && shield.Th == 128 && shield.Cw == 64 && shield.Ch == 96,
                     $"a 64x128 sheet whose art reaches row 41 from the bottom fills 64x96 ({shield.Cw}x{shield.Ch})");
        byte[] rgb = new byte[32 * 64 * 3];
        rgb[(10 * 32 + 3) * 3 + 1] = 90;
        Frame low = ScanRaster(rgb, 32, 64, 3);
        check.Expect(low.Cw == 32 && low.Ch == 64, $"a 32x64 RGB sheet with art near its bottom fills all of it ({low.Cw}x{low.Ch})");
        check.Expect(!ScanRaster(new byte[32 * 32 * 4], 32, 32, 4).Known, "a sheet with nothing visible has no frame");

        byte[] pairs = new byte[64 * 64 * 2];
        for (int i = 0; i < 64 * 64; i++) pairs[2 * i] = 255;
        pairs[(40 * 64 + 20) * 2] = 100;
        pairs[(10 * 64 + 60) * 2 + 1] = 3;
        Frame helm = ScanPlt(pairs, 64, 64);
        check.Expect(helm.Cw == 32 && helm.Ch == 32, $"a PLT texel at column 20, row 40 from the bottom fills the top-left cell ({helm.Cw}x{helm.Ch})");

        Frame u = Union(new Frame(64, 128, 64, 64), new Frame(64, 128, 32, 96));
        check.Expect(u.Cw == 64 && u.Ch == 96, $"two layers' frames join ({u.Cw}x{u.Ch})");
        Frame odd = Union(new Frame(64, 128, 64, 64), new Frame(32, 32, 32, 32));
        check.Expect(odd.Tw == 64 && odd.Cw == 64 && odd.Ch == 64, "a layer of another sheet size is left out");

        float x = 10f, y = 20f, w = 64f, h = 64f;
        Fit(new Frame(64, 128, 64, 96), ref x, ref y, ref w, ref h, out float u1, out float v0);
        check.Expect(Near(x, 20.6667f) && Near(y, 20f) && Near(w, 42.6667f) && Near(h, 64f) && Near(u1, 1f) && Near(v0, 0.25f),
                     $"64x96 of art into a 64x64 box is 42.7x64 centred, v from 0.25 ({x},{y} {w}x{h} u1 {u1} v0 {v0})");
        x = 0f; y = 0f; w = 64f; h = 64f;
        Fit(new Frame(32, 64, 32, 64), ref x, ref y, ref w, ref h, out u1, out v0);
        check.Expect(Near(x, 16f) && Near(y, 0f) && Near(w, 32f) && Near(h, 64f) && Near(u1, 1f) && Near(v0, 0f),
                     $"a 1x2 sheet into a 64x64 box is 32x64 centred ({x},{y} {w}x{h})");
        x = 5f; y = 5f; w = 64f; h = 64f;
        Fit(new Frame(64, 64, 64, 64), ref x, ref y, ref w, ref h, out u1, out v0);
        check.Expect(Near(x, 5f) && Near(y, 5f) && Near(w, 64f) && Near(h, 64f) && Near(u1, 1f) && Near(v0, 0f), "64x64 art fills a 64x64 box");
        x = 1f; y = 2f; w = 30f; h = 40f;
        Fit(default, ref x, ref y, ref w, ref h, out u1, out v0);
        check.Expect(Near(x, 1f) && Near(y, 2f) && Near(w, 30f) && Near(h, 40f) && Near(u1, 1f) && Near(v0, 0f), "an unknown frame leaves the box and the sheet whole");
        log.Info(check.Ok ? "IconFrames.SelfCheck: passed" : "IconFrames.SelfCheck: FAILED");
    }

    private static bool Near(float a, float b) => MathF.Abs(a - b) < 0.001f;
}
