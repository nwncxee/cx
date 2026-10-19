using System;
using System.Globalization;

namespace CX.Alloy.Db;

/// <summary>The arithmetic half of loading a font CX-side: the font-name token parse, the TTF atlas geometry,
/// the per-glyph UV derivation and the outline filter. Every number is transcribed from the engine, and nothing
/// here touches the engine, the resource chain or GL. There being no <c>CAurFont</c> to bind to, CX bakes the
/// atlas itself through the client's own <c>stb_truetype</c> at the engine's parameters.</summary>
public static class TextFontLoad
{
    /// <summary>Resource types the font path reads: TTF and TXI. From the key table's restype column.</summary>
    public const ushort ResTtf = 2074, ResTxi = 2022;

    /// <summary>The engine's atlas padding and oversampling, read off the <c>stbtt_PackBegin</c> call site at
    /// nm 0x1132b21 (<c>push $0x0</c> for the allocator, <c>mov $0x3,%r9d</c> for the padding) and the
    /// <c>stbtt_PackSetOversampling(spc, 1, 1)</c> that follows. **No** oversampling, 3 texels of padding.</summary>
    public const int AtlasPadding = 3, Oversample = 1;

    /// <summary>Glyph slots a TTF atlas always carries: <c>CAurTextureBasic::LoadFromTTF</c> @0x4875b0 forces
    /// <c>fontinfo.numchars = 0x100</c> before it builds the codepoint array.</summary>
    public const int TtfGlyphs = 256;

    /// <summary>The ctor defaults of <c>CAurFontInfo</c> @0x485640, which a <c>.txi</c> may override. Verified
    /// against the rodata symbols <c>initContrast</c>/<c>initOutlineBrightness</c>/<c>initOutlineOpacity</c>.</summary>
    public const float DefaultContrast = 1.0f, DefaultOutlineBrightness = 0.1f, DefaultOutlineOpacity = 0.35f;

    /// <summary>A parsed font name. <c>CAurTextureBasic::ParseTTFTokens</c> @0x485930 is
    /// <c>sscanf(name, "%s %s %d")</c> and takes the TTF branch only when the first token is exactly
    /// <c>"ttf"</c> and all three fields were read; everything else is a bitmap font named by the whole string.</summary>
    public readonly record struct FontName(bool Ttf, string Resref, int PointSize);

    /// <summary>Parse a font name the way <c>ParseTTFTokens</c> does. The resref is truncated to 31 bytes
    /// (<c>strncpy(this+0xa8, tok, 0x1f)</c>).</summary>
    public static FontName ParseName(string name)
    {
        string[] t = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (t.Length >= 3 && t[0] == "ttf"
            && int.TryParse(t[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int px))
            return new FontName(true, t[1].Length > 31 ? t[1][..31] : t[1], px);
        return new FontName(false, name, 0);
    }

    /// <summary>The atlas for <paramref name="glyphs"/> glyphs at <paramref name="pixelSize"/>, as
    /// <c>CAuroraTTFTexture::Load</c> @0x11329d0 sizes it: height pow2((int)px + 6), width
    /// pow2(glyphs*3 + 3 + (int)(glyphs * px)) -- no square root on the taken path. Cropped by <see cref="CropWidth"/>.</summary>
    public static void AtlasSize(float pixelSize, int glyphs, out int width, out int height)
    {
        height = NextPow2((int)(long)pixelSize + 6);
        width = NextPow2(glyphs * 3 + 3 + (int)(long)((float)glyphs * pixelSize));
    }

    /// <summary>The engine's doubling loop: 1 for anything below 2, else the first power of two at or above
    /// <paramref name="v"/>.</summary>
    public static int NextPow2(int v)
    {
        if ((uint)v < 2) return 1;
        int p = 1;
        while ((uint)p < (uint)v) p *= 2;
        return p;
    }

    /// <summary>How far the packed atlas can be cropped: the largest shift <c>s</c> for which every glyph's
    /// right edge still fits in <c>width &gt;&gt; s</c>. <c>Load</c>'s loop walks <c>s</c> up while
    /// <c>maxX1 &lt; width &gt;&gt; (s+1)</c> holds, then copies each row down to the narrower stride.</summary>
    public static int CropWidth(int width, int maxX1)
    {
        int s = 0;
        while (maxX1 < width >> (s + 1)) s++;
        return width >> s;
    }

    /// <summary>One glyph's packed box, the fields of <c>stbtt_packedchar</c> (28 bytes) the UV derivation
    /// reads.</summary>
    public readonly record struct Packed(ushort X0, ushort Y0, ushort X1, ushort Y1,
                                         float XOff, float YOff, float XAdvance, float XOff2, float YOff2);

    /// <summary>Turn one packed glyph into the engine's two coordinate <c>Vector</c>s, as <c>Load</c>'s loop at
    /// nm 0x1132d08 does. The v range is extrapolated out to the FULL line box -- which is why <c>TextOut</c>
    /// gives every glyph the same vertical span -- and the u range includes one padding texel each side;
    /// <c>ul.z</c> is the left bearing, <c>lr.z</c> the advance, both in u.</summary>
    public static void GlyphCoords(in Packed p, int width, int height, int baseline, float pixelSize,
                                   out float ulU, out float ulV, out float ulZ,
                                   out float lrU, out float lrV, out float lrZ)
    {
        float w = width, h = height, b = baseline;
        ulU = (p.X0 - 0.5f) / w;
        ulV = (p.Y0 - (p.YOff + b - 0.5f)) / h;
        ulZ = (p.XOff - 0.5f) / w;
        lrU = (p.X1 + 1.5f) / w;
        lrV = (pixelSize - b - p.YOff2 + 0.5f + p.Y1) / h;
        lrZ = p.XAdvance / w;
    }

    /// <summary>Texels of <see cref="GlyphCoords"/>'s v range that fall outside the glyph's own cell, above and
    /// below. The packer leaves <see cref="AtlasPadding"/> blank texels between rows and the outline filter
    /// spreads ink one texel into them, so one texel either side still belongs to this glyph. Zero when the
    /// range stops inside the cell; the caller turns the texels into GUI units with the font's own scale.</summary>
    public static void GlyphTrim(in Packed p, int height, float ulV, float lrV,
                                 out float topTexels, out float botTexels)
    {
        const int Halo = 1;
        float top = ulV * height, bottom = lrV * height;
        topTexels = MathF.Max(0f, p.Y0 - (AtlasPadding - Halo) - top);
        botTexels = MathF.Max(0f, bottom - (p.Y1 + AtlasPadding - Halo));
    }

    /// <summary><c>CAuroraTTFTexture::EnhanceCharacter</c> @0x1132810: the coverage bitmap becomes a luminance
    /// and an alpha, with a 3x3 blur standing in for an outline. Three quirks are load-bearing and reproduced:
    /// the taps are guarded on the FLAT index only, so the horizontal ones wrap across rows; the height is
    /// passed and never used; and the rounding term is <c>+0.49</c>, not <c>+0.5</c>.</summary>
    public static void Enhance(ReadOnlySpan<byte> src, int total, int width, int index,
                               float contrast, float outlineBrightness, float outlineOpacity,
                               out byte lum, out byte alpha)
    {
        const float Diag = 0.70710677f;
        uint i = (uint)index, w = (uint)width, n = (uint)total;
        float c = src[index];
        float s = c;
        if (w + 1 <= i) s += src[(int)(i - w - 1)] * Diag;
        if (w <= i) s += src[(int)(i - w)];
        if (w - 1 <= i) s += src[(int)(i - w + 1)] * Diag;
        if (i != 0) s += src[(int)(i - 1)];
        if (i + 1 < n) s += src[(int)(i + 1)];
        uint d = i + w;
        if (d - 1 < n) s += src[(int)(d - 1)] * Diag;
        if (d < n) s += src[(int)d];
        if (d + 1 < n) s += src[(int)(d + 1)] * Diag;

        float blur = s * contrast / 255f;
        float core = contrast * c / 255f;
        if (blur > 1f) blur = 1f;
        float inv;
        if (core <= 1f) inv = 1f - core;
        else { core = 1f; inv = 0f; }
        float outline = blur * outlineOpacity * inv;
        float a = core + outline;
        float l = a <= 0f ? 0f : core / a;
        float o = outline <= 0f ? 0f : outline * outlineBrightness / a;
        lum = (byte)(int)((l + o) * 255f + 0.49f);
        alpha = (byte)(int)(a * 255f + 0.49f);
    }

    /// <summary>The metrics <c>LoadFromTTF</c> writes back onto the font once the atlas exists. The GUI scale
    /// divides back out because the atlas was baked at <c>scale * pointSize</c> pixels while GUI units are
    /// scale-independent; <c>caretindent</c> is derived from glyph 0's width.</summary>
    public static void Metrics(TextFont f, int atlasWidth, int baseline, float guiScale)
    {
        f.TextureWidth = atlasWidth * 0.01f / guiScale;
        f.BaselineHeight = baseline * 0.01f / guiScale;
        f.CaretIndent = -(f.LrU[0] - f.UlU[0]) * 0.499f * f.TextureWidth + 0.01f;
    }

    /// <summary>The pixel height a TTF atlas bakes at: <c>GetGUIScale() * pointSize</c>. The engine spells it
    /// <c>scale * fontheight / 0.01</c>, and <c>fontheight</c> is <c>pointSize * 0.01</c>.</summary>
    public static float PixelSize(float guiScale, int pointSize) => guiScale * pointSize;
}
