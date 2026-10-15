using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Ui;

/// <summary>A face to bake: TTF bytes (keyed by their path) at a pixel height. <paramref name="IconsOnly"/> bakes
/// only the <c>icon</c> rows -- a sprite face at 100+ px, where a full text face would eat the whole sheet; such
/// a face is its own icon source and takes no part in the coverage fills.</summary>
public readonly record struct FaceRequest(string Typeface, byte[] Font, float Px, bool IconsOnly = false);

/// <summary>
/// The one shared single-channel glyph bitmap every face lives in. Baked cold with the client's
/// stb_truetype exports: every face's glyph boxes are measured, shelf-packed into one square, and only
/// then rasterised; on overflow the side doubles and packing retries up to <see cref="MaxSide"/>. A sheet
/// is immutable once baked -- a new face set is a new sheet, swapped in whole by <c>TextRig</c>.
/// </summary>
public sealed unsafe class AtlasSheet
{
    /// <summary>ponytail: opaque stbtt_fontinfo is ~180B; over-allocate. Ceiling: bind the real struct.</summary>
    private const int FontInfoBytes = 1024;
    /// <summary>Mip levels the sheet carries (0..2), the stated cap: animated text minifies from its
    /// peak face by at most ~4x, and each extra level costs another doubling of <see cref="Pad"/>.</summary>
    public const int MipLevels = 3;
    /// <summary>Inter-glyph gutter, in level-0 texels. With an unaligned 2x2 box filter, at level L a glyph's edge
    /// block straddles up to 2^L-1 texels into the gutter and bilinear reaches another 2^L, so pad >= 2^(L+1):
    /// 8 for the deepest level (2). Level 0 alone needed only 1.</summary>
    public const int Pad = 1 << MipLevels;
    /// <summary>Side of the solid-white block chrome rects sample. Bilinear at the block centre is 1.0 with
    /// 1.5 texels of margin, so vertex rounding cannot pull in a transparent neighbour.</summary>
    private const int SolidSide = 4;
    /// <summary>Bitmap side ceiling: GL 3.3 guarantees 1024 and every target driver gives 16384; 4096 (16 MB)
    /// holds ~30 faces at 18px. Above it a bake is a refusal, not a partial sheet.</summary>
    public const int MaxSide = 4096;

    public byte[] Pixels = Array.Empty<byte>();
    public int Side;
    /// <summary>Levels 1..<see cref="MipLevels"/>-1, box-filtered from <see cref="Pixels"/> on the CPU (no
    /// glGenerateMipmap: it respecifies the texture, the AMD/Mesa mip trap). <c>Mips[0]</c> is level 1.</summary>
    public byte[][] Mips = Array.Empty<byte[]>();
    /// <summary>Bytes of every level at R8: side² × (1 + 1/4 + 1/16).</summary>
    public int Bytes { get { int n = 0; for (int l = 0; l < MipLevels; l++) n += (Side >> l) * (Side >> l); return n; } }
    private readonly Dictionary<(string, float), GlyphAtlas> _faces = new();

    /// <summary>The face the first request baked -- what <c>TextRig.Atlas</c> hands a caller that asks for nothing.</summary>
    public GlyphAtlas Default = null!;

    public bool TryFace(string typeface, float px, out GlyphAtlas face) => _faces.TryGetValue((typeface, px), out face!);
    public int FaceCount => _faces.Count;

    /// <summary>Row-shelf packer over a <see cref="Side"/>² square. Pure: the headless self-check drives it with
    /// synthetic boxes and the bake drives it with measured glyph boxes.</summary>
    internal struct Shelf
    {
        private readonly int _side;
        private int _x, _y, _rowH;
        public Shelf(int side) { _side = side; _x = 0; _y = 0; _rowH = 0; }
        public bool TryPlace(int w, int h, out int x, out int y)
        {
            if (_x + w > _side) { _x = 0; _y += _rowH + Pad; _rowH = 0; }
            x = _x; y = _y;
            if (w > _side || _y + h > _side) return false;
            _x += w + Pad;
            if (h > _rowH) _rowH = h;
            return true;
        }
    }


    /// <summary>Fills a codepoint the typeface has no glyph for (index 0) from the default typeface's face at the
    /// same px -- same sheet, so the copied UVs stay valid -- never as a .notdef box, and logs the gap per range.
    /// A hole in the default itself fills from the icon font's twin when the default is not that font (a picked
    /// system font missing Cyrillic reads in DejaVu), else stays an advance-only blank. Icon rows always route there.</summary>
    private static void FillMissing(IReadOnlyList<FaceRequest> reqs, GlyphAtlas[] faces,
                                    List<(int Face, int Cp, int Range, float Advance)> missing,
                                    Dictionary<float, int> defaultByPx, Dictionary<float, int> iconByPx,
                                    (int lo, int hi, string name, bool icon)[] ranges, ExtenderLog log)
    {
        if (missing.Count == 0) return;
        var perFace = new int[reqs.Count, ranges.Length];
        foreach ((int f, int cp, int ri, float adv) in missing)
        {
            int d = (ranges[ri].icon ? iconByPx : defaultByPx)[reqs[f].Px];
            if (!ranges[ri].icon || d == f) perFace[f, ri]++;
            if (d == f && !ranges[ri].icon) d = iconByPx[reqs[f].Px];   // the default's own gap: the embedded twin, if the default is not it
            if (d != f && faces[d].TryGet(cp, out Glyph g)) faces[f].Store(cp, g);
            else faces[f].Store(cp, new Glyph { Advance = adv });
        }
        for (int f = 0; f < reqs.Count; f++)
        {
            var sb = new System.Text.StringBuilder();
            for (int ri = 0; ri < ranges.Length; ri++)
                if (perFace[f, ri] > 0) sb.Append(sb.Length == 0 ? "" : ", ").Append($"{ranges[ri].name} {perFace[f, ri]}/{ranges[ri].hi - ranges[ri].lo + 1}");
            if (sb.Length > 0)
                log.Info($"AtlasSheet: face {reqs[f].Typeface} @ {reqs[f].Px}px missed {sb}" +
                         (defaultByPx[reqs[f].Px] != f ? $" (filled from {reqs[0].Typeface})"
                          : iconByPx[reqs[f].Px] != f ? $" (filled from {reqs[iconByPx[reqs[f].Px]].Typeface})" : " (default face: left blank)"));
        }
    }

    /// <summary>One measured glyph awaiting placement: its face, codepoint, box, and (after packing) its texel origin.</summary>
    private struct Box { public int Face, Cp, W, H, X0, Y0, X, Y; public float Advance, Scale; }

    /// <summary>Pack boxes into a square, doubling the side on overflow. Returns the side that fit, or 0 past the ceiling.
    /// The solid block is always the first placement.</summary>
    private static int Pack(Span<Box> boxes, int side, out int solidX, out int solidY)
    {
        for (; side <= MaxSide; side *= 2)
        {
            var shelf = new Shelf(side);
            if (!shelf.TryPlace(SolidSide, SolidSide, out solidX, out solidY)) continue;
            bool ok = true;
            for (int i = 0; i < boxes.Length && ok; i++) ok = shelf.TryPlace(boxes[i].W, boxes[i].H, out boxes[i].X, out boxes[i].Y);
            if (ok) return side;
        }
        solidX = solidY = 0;
        return 0;
    }

    /// <summary>
    /// Bake every request (first = default) over the given codepoint ranges into one sheet, starting at
    /// <paramref name="startSide"/>. Rows flagged <c>icon</c> come from <paramref name="icon"/> for EVERY face;
    /// when the default typeface IS that font (the same byte array) its twins are the source. Cold. Null -- the
    /// caller keeps its old sheet -- if stbtt isn't linked in (headless), a font fails to init, or the side overflows.
    /// </summary>
    public static AtlasSheet? Bake(IReadOnlyList<FaceRequest> reqs, (int lo, int hi, string name, bool icon)[] ranges,
        (string Name, byte[]? Font) icon, int startSide, ExtenderLog log)
    {
        if (EngineFn.StbttInitFont == null || EngineFn.StbttScaleForPixelHeight == null || EngineFn.StbttMakeCodepointBitmap == null)
        {
            log.Info("AtlasSheet: stbtt exports unresolved (headless?); skipping bake");
            return null;
        }
        if (reqs.Count == 0) return null;
        var list = reqs.DistinctBy(r => (r.Typeface, r.Px)).ToList();   // SetFont may land the default on an extra face's size
        // Every extra face gets a default-typeface twin at its px, the source of its coverage fills.
        var defaultByPx = new Dictionary<float, int>();
        for (int i = 0; i < list.Count; i++) if (!list[i].IconsOnly && list[i].Typeface == list[0].Typeface) defaultByPx[list[i].Px] = i;
        for (int i = 0; i < list.Count; i++)
            if (!list[i].IconsOnly && !defaultByPx.ContainsKey(list[i].Px)) { defaultByPx[list[i].Px] = list.Count; list.Add(list[0] with { Px = list[i].Px }); }
        // the icon rows' source face per px -- the default's twins when the default is the icon font, else
        // an icon-font twin per px (only a font.default= override pays for these).
        Dictionary<float, int> iconByPx = defaultByPx;
        if (icon.Font != null && !ReferenceEquals(icon.Font, list[0].Font))
        {
            iconByPx = new Dictionary<float, int>();
            for (int i = 0; i < list.Count; i++) if (list[i].Typeface == icon.Name) iconByPx[list[i].Px] = i;
            foreach (float px in defaultByPx.Keys)
                if (!iconByPx.ContainsKey(px)) { iconByPx[px] = list.Count; list.Add(new FaceRequest(icon.Name, icon.Font, px)); }
        }
        reqs = list;
        var missing = new List<(int Face, int Cp, int Range, float Advance)>();

        var sheet = new AtlasSheet();
        var faces = new GlyphAtlas[reqs.Count];
        var boxes = new List<Box>(reqs.Count * 512);
        var infos = new IntPtr[reqs.Count];
        var fonts = new IntPtr[reqs.Count];
        try
        {
            for (int f = 0; f < reqs.Count; f++)
            {
                FaceRequest r = reqs[f];
                byte* fi = (byte*)(infos[f] = Marshal.AllocHGlobal(FontInfoBytes));
                new Span<byte>(fi, FontInfoBytes).Clear();   // InitFont fills what it uses; zero the rest to be version-proof
                byte* fn = (byte*)(fonts[f] = Marshal.AllocHGlobal(r.Font.Length));   // stbtt keeps a pointer into this
                Marshal.Copy(r.Font, 0, fonts[f], r.Font.Length);
                if (EngineFn.StbttInitFont(fi, fn, 0) == 0) { log.Info($"AtlasSheet: stbtt_InitFont failed for {r.Typeface}"); return null; }
                float scale = EngineFn.StbttScaleForPixelHeight(fi, r.Px);

                var face = faces[f] = new GlyphAtlas();
                int asc = 0, desc = 0, gap = 0;
                if (EngineFn.StbttGetFontVMetrics != null) EngineFn.StbttGetFontVMetrics(fi, &asc, &desc, &gap);
                face.Ascent = asc * scale;
                face.LineHeight = (asc - desc + gap) * scale;
                int adv = 0, lsb = 0;
                if (EngineFn.StbttGetCodepointHMetrics != null) { EngineFn.StbttGetCodepointHMetrics(fi, ' ', &adv, &lsb); face.SpaceAdvance = adv * scale; }

                int iconSrc = r.IconsOnly ? f : iconByPx[r.Px];
                for (int ri = 0; ri < ranges.Length; ri++)
                    for (int cp = ranges[ri].lo; cp <= ranges[ri].hi; cp++)
                    {
                        if (r.IconsOnly && !ranges[ri].icon) continue;
                        int ax = 0, al = 0;
                        if (EngineFn.StbttGetCodepointHMetrics != null) EngineFn.StbttGetCodepointHMetrics(fi, cp, &ax, &al);
                        float advance = ax * scale;
                        if (ranges[ri].icon && f != iconSrc) { missing.Add((f, cp, ri, advance)); continue; }   // routed, not measured
                        if (EngineFn.StbttFindGlyphIndex != null && EngineFn.StbttFindGlyphIndex(fi, cp) == 0)
                        {
                            if (r.IconsOnly) { face.Store(cp, new Glyph { Advance = advance }); continue; }   // no fill source: blank
                            missing.Add((f, cp, ri, advance)); continue;
                        }
                        int x0 = 0, y0 = 0, x1 = 0, y1 = 0;
                        if (EngineFn.StbttGetCodepointBitmapBox != null) EngineFn.StbttGetCodepointBitmapBox(fi, cp, scale, scale, &x0, &y0, &x1, &y1);
                        int gw = x1 - x0, gh = y1 - y0;
                        if (gw <= 0 || gh <= 0) { face.Store(cp, new Glyph { Advance = advance }); continue; }
                        boxes.Add(new Box { Face = f, Cp = cp, W = gw, H = gh, X0 = x0, Y0 = y0, Advance = advance, Scale = scale });
                    }
            }

            Span<Box> bs = CollectionsMarshal.AsSpan(boxes);
            int side = Pack(bs, startSide, out int sx, out int sy);
            if (side == 0)
            {
                log.Info($"AtlasSheet: REFUSED -- {reqs.Count} face(s) / {boxes.Count} glyphs do not fit {MaxSide}²; previous atlas kept");
                return null;
            }
            sheet.Side = side;
            sheet.Pixels = new byte[side * side];
            for (int y = 0; y < SolidSide; y++) for (int x = 0; x < SolidSide; x++) sheet.Pixels[(sy + y) * side + sx + x] = 255;
            float su = (sx + SolidSide * 0.5f) / side, sv = (sy + SolidSide * 0.5f) / side;

            foreach (ref Box b in bs)
                {
                    fixed (byte* px = sheet.Pixels.AsSpan(b.Y * side + b.X))
                        EngineFn.StbttMakeCodepointBitmap((byte*)infos[b.Face], px, b.W, b.H, side, b.Scale, b.Scale, b.Cp);
                    faces[b.Face].Store(b.Cp, new Glyph
                    {
                        U0 = b.X / (float)side, V0 = b.Y / (float)side,
                        U1 = (b.X + b.W) / (float)side, V1 = (b.Y + b.H) / (float)side,
                        W = b.W, H = b.H, XOff = b.X0, YOff = b.Y0, Advance = b.Advance,
                    });
                }

            sheet.Mips = BuildMips(sheet.Pixels, side);
            FillMissing(reqs, faces, missing, defaultByPx, iconByPx, ranges, log);

            for (int f = 0; f < reqs.Count; f++)
            {
                faces[f].SolidU = su; faces[f].SolidV = sv;
                sheet._faces[(reqs[f].Typeface, reqs[f].Px)] = faces[f];
                log.Info($"AtlasSheet: face {reqs[f].Typeface} @ {reqs[f].Px}px: {faces[f].Count} glyphs (lineH={faces[f].LineHeight:F1})");
            }
            sheet.Default = faces[0];
            log.Info($"AtlasSheet: {reqs.Count} face(s) packed into {side}²");
            return sheet;
        }
        finally
        {
            foreach (IntPtr p in infos) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            foreach (IntPtr p in fonts) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>2x2 box filter, level by level, down to <see cref="MipLevels"/>-1. Pure: the self-check drives it.</summary>
    internal static byte[][] BuildMips(byte[] level0, int side)
    {
        var mips = new byte[MipLevels - 1][];
        byte[] src = level0; int s = side;
        for (int l = 0; l < mips.Length; l++)
        {
            int d = s >> 1;
            var dst = new byte[d * d];
            for (int y = 0; y < d; y++)
                for (int x = 0; x < d; x++)
                {
                    int i = (2 * y) * s + 2 * x;
                    dst[y * d + x] = (byte)((src[i] + src[i + 1] + src[i + s] + src[i + s + 1] + 2) >> 2);
                }
            mips[l] = dst; src = dst; s = d;
        }
        return mips;
    }

    /// <summary>Test-only: register a face without a native bake.</summary>
    internal void AddTestFace(string typeface, float px, GlyphAtlas face)
    {
        _faces[(typeface, px)] = face;
        Default ??= face;
    }

    /// <summary>pack-and-grow (a box set that overflows 64² fits 128², one past the ceiling is refused,
    /// the solid block is placed first) and the face lookup, with no GL context and no stbtt.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var boxes = new Box[100];
        for (int i = 0; i < boxes.Length; i++) { boxes[i].W = 10; boxes[i].H = 10; }
        int side = Pack(boxes, 64, out int sx, out int sy);
        check.Expect(side == 256, $"grow: expected 256, got {side}");
        check.Expect(sx == 0 && sy == 0, "solid block is the first placement");
        check.Expect(boxes[0].X == SolidSide + Pad && boxes[0].Y == 0, "first glyph sits right of the solid block");
        var seen = new HashSet<(int, int)>();
        foreach (Box b in boxes) check.Expect(seen.Add((b.X, b.Y)) && b.X + b.W <= side && b.Y + b.H <= side, "placements unique and in bounds");

        // the gutter's side cost on a realistic set (one 18px face is ~600 glyph boxes), and the box filter.
        var dense = new Box[600];
        for (int i = 0; i < dense.Length; i++) { dense[i].W = 12; dense[i].H = 14; }
        int padded = Pack(dense, 64, out _, out _);
        int unpadded = (int)System.Math.Ceiling(System.Math.Sqrt(600 * 13 * 15));
        log.Info($"AtlasSheet.SelfCheck: 600 12x14 boxes pack into {padded}² at Pad={Pad} (~{unpadded}² of area at a 1px gutter)");
        check.Expect(padded > 0, "padded pack fits under the ceiling");
        var lv0 = new byte[8 * 8];
        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++) lv0[i * 8 + j] = 255;   // top-left 4x4 solid
        byte[][] mips = BuildMips(lv0, 8);
        check.Expect(mips.Length == MipLevels - 1 && mips[0].Length == 16 && mips[1].Length == 4, "level sizes 4² and 2²");
        check.Expect(mips[0][0] == 255 && mips[0][1] == 255 && mips[0][2] == 0 && mips[0][4] == 255 && mips[0][5] == 255, "level 1 keeps the solid 2x2 and zero elsewhere");
        check.Expect(mips[1][0] == 255 && mips[1][1] == 0 && mips[1][3] == 0, "level 2 is one solid texel");
        lv0[1] = 0; lv0[8] = 0; lv0[9] = 0;
        check.Expect(BuildMips(lv0, 8)[0][0] == 64, "box filter averages with rounding (255/4 -> 64)");

        var big = new Box[1]; big[0].W = big[0].H = MaxSide + 1;
        check.Expect(Pack(big, 64, out _, out _) == 0, "past the ceiling is a refusal");

        var sheet = new AtlasSheet();
        var a = new GlyphAtlas { LineHeight = 18 }; var b2 = new GlyphAtlas { LineHeight = 14 };
        sheet.AddTestFace("ui.ttf", 18f, a); sheet.AddTestFace("ui.ttf", 14f, b2);
        check.Expect(sheet.Default == a, "first face is the default");
        check.Expect(sheet.TryFace("ui.ttf", 14f, out GlyphAtlas f) && f == b2, "face lookup by (typeface, px)");
        check.Expect(!sheet.TryFace("ui.ttf", 20f, out _), "unbaked face misses");

        // icon rows fill from the icon font's face, text rows from the default's; a routed icon is no coverage gap.
        var rq = new[] { new FaceRequest("arial", new byte[1], 18f), new FaceRequest("embedded", new byte[1], 18f), new FaceRequest("extra", new byte[1], 18f) };
        var fc = new[] { new GlyphAtlas(), new GlyphAtlas(), new GlyphAtlas() };
        fc[0].Store('a', new Glyph { W = 3, Advance = 3 });
        fc[1].Store(0x2699, new Glyph { W = 5, Advance = 7 });
        var rg = new[] { (0x0061, 0x0061, "a", false), (0x2699, 0x2699, "gear", true) };
        var miss = new List<(int, int, int, float)> { (0, 0x2699, 1, 1f), (2, 0x2699, 1, 1f), (2, 'a', 0, 1f) };
        FillMissing(rq, fc, miss, new() { [18f] = 0 }, new() { [18f] = 1 }, rg, log);
        check.Expect(fc[0].TryGet(0x2699, out Glyph g0) && g0.W == 5 && g0.Advance == 7, "the default face takes its gear from the icon font");
        check.Expect(fc[2].TryGet(0x2699, out Glyph g2) && g2.W == 5, "an extra face takes its gear from the icon font too");
        check.Expect(fc[2].TryGet('a', out Glyph ga) && ga.W == 3, "a text gap still fills from the default face");

        log.Info(check.Ok ? "AtlasSheet.SelfCheck OK" : "AtlasSheet.SelfCheck FAILED");
    }
}
