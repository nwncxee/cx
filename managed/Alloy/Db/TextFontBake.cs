using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy.Db;

/// <summary>Load a font CX-side, with no <c>CAurFont</c> and no engine render layer: read the resources, bake
/// a TTF atlas with the client's <b>own</b> statically linked <c>stb_truetype</c> at the engine's parameters,
/// and hand the pixels to <c>TextAtlas</c> for the GL upload inside Alloy's bracket. The arithmetic lives in
/// <see cref="TextFontLoad"/>; a font is baked once, on the first <c>SetFont</c> that names it.</summary>
public static unsafe class TextFontBake
{
    private static ExtenderLog? _log;
    private static Handles? _handles;
    private static int _said;

    /// <summary>Atlases baked, bitmap fonts loaded, and fonts refused -- for <c>/alloy</c>.</summary>
    public static long Baked, Bitmaps, Refused;

    /// <summary>Pixels waiting for the GL upload, drained by <c>TextAtlas.Pump</c> inside Alloy's bracket.
    /// A font is usable for LAYOUT the moment it is baked; it just does not draw until its atlas lands.</summary>
    public static readonly System.Collections.Generic.List<PendingAtlas> Pending = new(4);

    /// <summary>One baked atlas: RGBA8 pixels in native memory, and the font they belong to.</summary>
    public sealed class PendingAtlas
    {
        public TextFont Font = null!;
        public IntPtr Pixels;
        public int Width, Height;
        /// <summary>A bitmap font's texture resref instead of baked pixels; its atlas comes from the texture
        /// registry rather than from here.</summary>
        public string? Resref;
        /// <summary>Frames this font has waited for that texture.</summary>
        public int Waited;
        /// <summary>The live font this re-bake replaces; <c>TextAtlas.Pump</c> swaps the new atlas into it.</summary>
        public TextFont? Replaces;
    }

    public static void Install(ExtenderLog log) => _log = log;

    /// <summary>Resolve a font name to a loaded <see cref="TextFont"/>, loading it on the first ask. Null when
    /// the font cannot be built -- said once, loudly, with the reason, and counted. Never a quiet hole and
    /// never a substitute font: the engine's own <c>SetFont</c> answers -1 for an unresolvable name and the
    /// string then measures and draws nothing, which is what the caller must see.</summary>
    public static TextFont? Resolve(string name)
    {
        TextFont? had = TextFonts.Get(name);
        if (had != null) return had;

        string key = TextFonts.Remap(name);
        TextFontLoad.FontName n = TextFontLoad.ParseName(key);
        TextFont? f = n.Ttf ? Ttf(key, n) : Bitmap(key, n.Resref);
        if (f == null) { Refused++; return null; }
        TextFonts.Register(f);
        return f;
    }

    /// <summary><c>ComputeGUIScaleAndUpdate</c>'s refresh gate: a scale change this small re-bakes nothing.</summary>
    private const float RefreshDelta = 0.09f;

    /// <summary>Re-bake every TTF atlas baked at a UI scale <see cref="RefreshDelta"/> or more from the live one,
    /// the engine's <c>AurTextureRequestRefreshTTF</c> edge. The old atlas draws until the new one lands.
    /// Game thread, once a frame: a float compare per font on every frame without a change.</summary>
    public static void FollowScale()
    {
        float s = GuiScale();
        for (int i = 0; i < TextFonts.Count; i++)
        {
            TextFont? f = TextFonts.At(i);
            if (f == null || !f.Ttf || f.Name.Contains('\u0000') || MathF.Abs(f.BakedScale - s) < RefreshDelta) continue;
            f.BakedScale = s;
            TextFont? fresh = Ttf(f.Name, TextFontLoad.ParseName(f.Name));
            if (fresh == null) continue;
            Pending[^1].Replaces = f;
            _log?.Info($"TextFontBake: \"{f.Name}\" re-baked for game UI scale {s:0.##}");
        }
    }

    // ---- TTF ----

    private static TextFont? Ttf(string key, in TextFontLoad.FontName n)
    {
        if (EngineFn.StbttPackBegin == null || EngineFn.StbttPackFontRanges == null
            || EngineFn.StbttPackSetOversampling == null || EngineFn.StbttPackEnd == null)
            return Refuse(key, "the client's stb_truetype pack exports are unresolved");

        byte[]? ttf = Res(n.Resref, TextFontLoad.ResTtf);
        if (ttf == null) return Refuse(key, $"{n.Resref}.ttf is not in the resource chain");

        var f = new TextFont { Name = key, Ttf = true, NumChars = TextFontLoad.TtfGlyphs };
        f.FontHeight = n.PointSize * 0.01f;
        float contrast = TextFontLoad.DefaultContrast;
        float outBright = TextFontLoad.DefaultOutlineBrightness;
        float outOpacity = TextFontLoad.DefaultOutlineOpacity;
        // The .txi the engine reads for a TTF font is the TTF RESREF's, not the texture's: it carries the
        // outline filter's three terms and spacingR.
        byte[]? txi = Res(n.Resref, TextFontLoad.ResTxi);
        if (txi != null && TextFonts.ParseTxiInto(f, System.Text.Encoding.Latin1.GetString(txi),
                                                  ref contrast, ref outBright, ref outOpacity))
        { /* fontheight stays the name's; the .txi carries spacingR/spacingB and the three ttf terms */ }
        f.FontHeight = n.PointSize * 0.01f;
        f.NumChars = TextFontLoad.TtfGlyphs;

        float scale = GuiScale();
        float px = TextFontLoad.PixelSize(scale, n.PointSize);
        if (!(px > 0f) || px > 1024f) return Refuse(key, $"a bake pixel size of {px} is out of range");

        if (!Pack(f, ttf, px, contrast, outBright, outOpacity, scale, key, out IntPtr pixels, out int w, out int h))
            return null;

        f.Epoch = ++_epoch;
        f.BakedScale = scale;
        Pending.Add(new PendingAtlas { Font = f, Pixels = pixels, Width = w, Height = h });
        Baked++;
        if (_said++ < 4)
            _log?.Info($"TextFontBake: baked \"{key}\" at {px:0.#} px -> {w}x{h} atlas, "
                     + $"baseline {f.BaselineHeight / 0.01f:0.#} px, texturewidth {f.TextureWidth:0.####}");
        return f;
    }

    private static int _epoch;

    /// <summary>The bake proper: the client's own packer, at the engine's padding (3), oversampling (1,1) and
    /// atlas geometry, over the 256 codepoints the active codepage maps the byte values to. Then the engine's
    /// horizontal crop, its UV derivation and its outline filter.</summary>
    private static bool Pack(TextFont f, byte[] ttf, float px, float contrast, float outBright,
                             float outOpacity, float scale, string key, out IntPtr rgba, out int width, out int height)
    {
        rgba = IntPtr.Zero; width = height = 0;
        int glyphs = TextFontLoad.TtfGlyphs;
        TextFontLoad.AtlasSize(px, glyphs, out int w, out int h);

        // stbtt keeps a pointer into the font bytes for the whole pack, so they live in native memory for it.
        IntPtr font = Marshal.AllocHGlobal(ttf.Length);
        IntPtr cov = Marshal.AllocHGlobal(w * h);
        IntPtr chars = Marshal.AllocHGlobal(glyphs * sizeof(StbPackedChar));
        IntPtr cps = Marshal.AllocHGlobal(glyphs * sizeof(int));
        IntPtr spc = Marshal.AllocHGlobal(PackContextBytes);
        IntPtr range = Marshal.AllocHGlobal(sizeof(StbPackRange));
        try
        {
            Marshal.Copy(ttf, 0, font, ttf.Length);
            new Span<byte>((void*)cov, w * h).Clear();
            new Span<byte>((void*)spc, PackContextBytes).Clear();
            new Span<byte>((void*)range, sizeof(StbPackRange)).Clear();

            int* cp = (int*)cps;
            for (int i = 0; i < glyphs; i++) cp[i] = Codepoint((byte)i);

            var r = (StbPackRange*)range;
            r->FontSize = px;
            r->Codepoints = cps;
            r->NumChars = glyphs;
            r->CharData = chars;

            EngineFn.StbttPackBegin((byte*)spc, (byte*)cov, w, h, 0, TextFontLoad.AtlasPadding, null);
            EngineFn.StbttPackSetOversampling((byte*)spc, TextFontLoad.Oversample, TextFontLoad.Oversample);
            EngineFn.StbttPackFontRanges((byte*)spc, (byte*)font, 0, (byte*)r, 1);
            EngineFn.StbttPackEnd((byte*)spc);

            int baseline = BaseLine(font, px);
            var pc = (StbPackedChar*)chars;
            int maxX1 = 0;
            for (int i = 0; i < glyphs; i++)
            {
                int x1 = pc[i].X1;
                if (x1 > maxX1) maxX1 = x1;
            }
            int cropW = TextFontLoad.CropWidth(w, maxX1);

            for (int i = 0; i < glyphs; i++)
            {
                StbPackedChar g = pc[i];
                var p = new TextFontLoad.Packed(g.X0, g.Y0, g.X1, g.Y1, g.XOff, g.YOff, g.XAdvance, g.XOff2, g.YOff2);
                TextFontLoad.GlyphCoords(p, cropW, h, baseline, px,
                    out f.UlU[i], out f.UlV[i], out f.UlZ[i], out f.LrU[i], out f.LrV[i], out f.LrZ[i]);
                // The extrapolated part of the v range is trimmed off both the range and the quad by the same
                // amount, so the glyph's pixels land exactly where they did and the quad can no longer reach
                // the glyph packed beyond its cell. One v texel is one line-box texel, and the line box is
                // f.FontHeight GUI units over px of them.
                TextFontLoad.GlyphTrim(p, h, f.UlV[i], f.LrV[i], out float topTexels, out float botTexels);
                float unit = px > 0f ? f.FontHeight / px : 0f;
                f.TopTrim[i] = topTexels * unit;
                f.BotTrim[i] = botTexels * unit;
                f.UlV[i] += topTexels / h;
                f.LrV[i] -= botTexels / h;
            }

            // Crop in place (rows move down to the narrower stride), then expand to RGBA through the filter.
            var src = new Span<byte>((void*)cov, w * h);
            if (cropW != w)
                for (int y = 0, dst = 0, s = 0; y < h; y++, dst += cropW, s += w)
                    src.Slice(s, cropW).CopyTo(src[dst..]);
            int total = cropW * h;
            rgba = Marshal.AllocHGlobal(total * 4);
            byte* o = (byte*)rgba;
            var crop = src[..total];
            for (int i = 0; i < total; i++)
            {
                TextFontLoad.Enhance(crop, total, cropW, i, contrast, outBright, outOpacity, out byte lum, out byte a);
                o[i * 4] = lum; o[i * 4 + 1] = lum; o[i * 4 + 2] = lum; o[i * 4 + 3] = a;
            }

            TextFontLoad.Metrics(f, cropW, baseline, scale);
            width = cropW; height = h;
            return true;
        }
        catch (Exception e)
        {
            if (rgba != IntPtr.Zero) { Marshal.FreeHGlobal(rgba); rgba = IntPtr.Zero; }
            Refuse(key, $"the bake threw: {e.Message}");
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(font); Marshal.FreeHGlobal(cov); Marshal.FreeHGlobal(chars);
            Marshal.FreeHGlobal(cps); Marshal.FreeHGlobal(spc); Marshal.FreeHGlobal(range);
        }
    }

    /// <summary><c>CAuroraTTFTexture::GetBaseLine</c> @0x1132710: the scaled ascender, rounded.</summary>
    private static int BaseLine(IntPtr font, float px)
    {
        if (EngineFn.StbttInitFont == null || EngineFn.StbttGetFontVMetrics == null
            || EngineFn.StbttScaleForPixelHeight == null) return 0;
        byte* info = stackalloc byte[FontInfoBytes];
        new Span<byte>(info, FontInfoBytes).Clear();
        if (EngineFn.StbttInitFont(info, (byte*)font, 0) == 0) return 0;
        int asc = 0;
        EngineFn.StbttGetFontVMetrics(info, &asc, null, null);
        return (int)(EngineFn.StbttScaleForPixelHeight(info, px) * asc + 0.5f);
    }

    /// <summary>The codepoint a codepage byte stands for, through the engine's own
    /// <c>Encoding::SingleCharToUTF8(c, 0)</c> so the client's locale and a module's <c>encoding.2da</c>
    /// override both apply. Without the export the byte is its own codepoint (Latin-1), which is what the
    /// engine's default locale does below 0x80 and for 0xa0..0xff.</summary>
    private static int Codepoint(byte b)
        => EngineFn.EncodingSingleCharToUtf8 == null ? b : EngineFn.EncodingSingleCharToUtf8(b, 0);

    // ---- bitmap fonts ----

    /// <summary>A bitmap font is its <c>.txi</c> (the 256 UV rects verbatim -- there is no grid to derive) plus
    /// a texture of the same resref. The texture is left to the texture path; the font carries the resref until
    /// an atlas handle lands.</summary>
    private static TextFont? Bitmap(string key, string resref)
    {
        byte[]? txi = Res(resref, TextFontLoad.ResTxi);
        if (txi == null) return Refuse(key, $"{resref}.txi is not in the resource chain");
        if (!TextFonts.ParseTxi(key, System.Text.Encoding.Latin1.GetString(txi), out TextFont f))
            return Refuse(key, $"{resref}.txi declares no glyphs, so it is not a font");
        f.Epoch = ++_epoch;
        Pending.Add(new PendingAtlas { Font = f, Resref = resref });
        Bitmaps++;
        return f;
    }

    // ---- the resource chain ----

    /// <summary>One resource's bytes, read straight out of its container. Null when it is absent, or when the
    /// engine holds it RESIDENT -- in DB mode nothing demands a font resource, because the render layer that
    /// would is never constructed, so a resident font is a mixed-mode case and is refused loudly rather than
    /// read through a second path that would need its own Windows resolution.</summary>
    private static byte[]? Res(string resref, ushort type)
    {
        if (!ResLocate.Ready && !ResLocate.Attach(out _)) return null;
        Span<byte> nm = stackalloc byte[17];
        int k = 0;
        foreach (char c in resref) { if (k == 16) break; nm[k++] = (byte)char.ToLowerInvariant(c); }
        nm[k] = 0;
        fixed (byte* p = nm)
        {
            if (!ResLocate.Find(p, type, out IntPtr table, out IntPtr entry)) return null;
            uint* resId = stackalloc uint[ResLocate.ResIdWords];
            if (!ResLocate.Locate(table, entry, resId, out TexLocation loc, out _, out ResLocate.Source src))
            {
                if (src == ResLocate.Source.Resident)
                    Refuse(resref, "the engine holds it resident; CX reads fonts from their container only");
                return null;
            }
            if (!(_handles ??= new Handles()).Read(loc, out ReadOnlySpan<byte> bytes, out _)) return null;
            return bytes.ToArray();
        }
    }

    private static TextFont? Refuse(string what, string why)
    {
        if (_said++ < 8) _log?.Always($"TextFontBake: \"{what}\" is NOT LOADED -- {why} (totals on /alloy)");
        return null;
    }

    /// <summary>The engine's live GUI scale, which floors at 1. Also publishes the pixel grid every snap in
    /// <see cref="TextObjects.AlignToPixel"/> uses, so the two cannot drift apart.</summary>
    public static float GuiScale()
    {
        float s = EngineFn.GetGuiScale == null ? 1f : EngineFn.GetGuiScale();
        if (!(s >= 1f)) s = 1f;
        TextObjects.SetGuiToPixel(0.01f / s);
        return s;
    }

    /// <summary>Sizes of the opaque <c>stb_truetype</c> structs. This client's <c>stbtt_pack_context</c> is 72
    /// bytes (the stack <c>CAuroraTTFTexture::Load</c> reserves) and <c>stbtt_fontinfo</c> 168
    /// (<c>GetBaseLine</c>'s); both are over-allocated here against a future build. <c>stbtt_packedchar</c> 28
    /// and <c>stbtt_pack_range</c> 40 are exact, being read field by field.</summary>
    private const int PackContextBytes = 128, FontInfoBytes = 256;
}
