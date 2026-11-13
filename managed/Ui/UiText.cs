using System;
using System.Text;

namespace CX.Ui;

/// <summary>One-line string draw + measure over a <see cref="GlyphAtlas"/>, shared by the settings panel and the
/// primitives (scroll column, dropdown, tooltip). Zero-alloc: a rune walk over the string, no LINQ, no buffers --
/// safe on the render thread.</summary>
public static class UiText
{
    /// <summary>Draw one string clipped at <paramref name="clipX"/>, returning the pen x it ended at.</summary>
    public static float Draw(VertexBatch b, GlyphAtlas atlas, string s, float startX, float baseline,
                             uint rgb, float clipX, float alpha = 1f)
        => Draw(b, atlas, s, 0, startX, baseline, rgb, clipX, alpha);

    /// <summary>As <see cref="Draw(VertexBatch, GlyphAtlas, string, float, float, uint, float, float)"/>, from the
    /// UTF-16 index <paramref name="from"/> on: a text box shows the tail it is typing into.</summary>
    public static float Draw(VertexBatch b, GlyphAtlas atlas, string s, int from, float startX, float baseline,
                             uint rgb, float clipX, float alpha = 1f)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float penX = startX;
        foreach (Rune ru in s.AsSpan(from).EnumerateRunes())
        {
            if (penX >= clipX) break;
            if (atlas.TryGet(ru.Value, out Glyph gl)) { b.Glyph(gl, penX, baseline, r, g, bl, alpha); penX += gl.Advance; }
            else penX += atlas.AdvanceOf(ru);
        }
        return penX;
    }

    /// <summary>
    /// As <see cref="Draw(VertexBatch, GlyphAtlas, string, float, float, uint, float, float)"/>, laid down the way a NUI skin
    /// lays its text: <paramref name="spacing"/> extra px after each glyph, and with <paramref name="snap"/> every glyph and
    /// the baseline on a whole pixel, so a bitmap glyph is sampled texel for pixel and stays sharp.
    /// </summary>
    public static float Draw(VertexBatch b, GlyphAtlas atlas, string s, float startX, float baseline, uint rgb, float clipX,
                             float alpha, bool snap, float spacing)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float penX = startX, y = snap ? MathF.Round(baseline) : baseline;
        foreach (Rune ru in s.EnumerateRunes())
        {
            if (penX >= clipX) break;
            if (atlas.TryGet(ru.Value, out Glyph gl)) { b.Glyph(gl, snap ? MathF.Round(penX) : penX, y, r, g, bl, alpha); penX += gl.Advance; }
            else penX += atlas.AdvanceOf(ru);
            penX += spacing;
        }
        return penX;
    }

    /// <summary>Advance width of a string with <paramref name="spacing"/> extra px after each glyph (no draw).</summary>
    public static float Measure(GlyphAtlas atlas, string s, float spacing)
    {
        float w = 0f;
        foreach (Rune ru in s.EnumerateRunes()) w += atlas.AdvanceOf(ru) + spacing;
        return w;
    }

    /// <summary>The first UTF-16 index of the longest tail of <paramref name="s"/> that fits in <paramref name="width"/>
    /// (0 when all of it fits); never splits a surrogate pair.</summary>
    public static int TailFrom(GlyphAtlas atlas, string s, float width)
    {
        float w = 0f;
        int i = s.Length;
        while (i > 0)
        {
            int j = i - 1;
            if (j > 0 && char.IsLowSurrogate(s[j]) && char.IsHighSurrogate(s[j - 1])) j--;
            float a = Rune.TryGetRuneAt(s, j, out Rune ru) ? atlas.AdvanceOf(ru) : 0f;
            if (w + a > width) break;
            w += a; i = j;
        }
        return i;
    }

    /// <summary>Advance width of a string (no draw).</summary>
    public static float Measure(GlyphAtlas atlas, string s)
    {
        float w = 0f;
        foreach (Rune ru in s.EnumerateRunes()) w += atlas.AdvanceOf(ru);
        return w;
    }
}
