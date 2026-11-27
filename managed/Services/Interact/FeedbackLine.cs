using System;
using System.Diagnostics;
using System.Threading;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Interact;

/// <summary>The line an Interact press shows when nothing qualifies: red text at the top centre that holds, then fades.
/// A new line takes the old one's place and restarts the hold. Baked once per change; the draw allocates nothing.</summary>
internal static class FeedbackLine
{
    /// <summary>The line on screen, set on the input thread and drawn on the present thread.</summary>
    private sealed class LineState
    {
        internal readonly VertexBatch Batch = new(0f, 0f);
        internal string? Text;
        /// <summary>The Stopwatch timestamp of the press that showed <see cref="Text"/>; 0 while nothing shows.</summary>
        internal long ShownAt;
        internal string? BakedText;
        internal float BakedW, BakedScale;
        internal int BakedEpoch = -1;
    }

    private static readonly LineState S = new();

    /// <summary>Seconds the line holds at full strength, then the seconds it fades over.</summary>
    internal const float Hold = 2.5f, Fade = 0.6f;
    /// <summary>The text size, the box's top and height and the shadow's offset, at GUI scale 1.</summary>
    private const float Px = 24f, Top = 250f, BoxH = 44f, Shadow = 1f;
    private const float Red = 1f, Green = 0x30 / 255f, Blue = 0x30 / 255f;
    /// <summary>The game's main text face; the default typeface when this install has no such face.</summary>
    private const string Typeface = "NWN_Main_Text";

    /// <summary>Puts the line's window in the UI layer and asks for its face. Cold: service start.</summary>
    internal static void Register()
    {
        new Window(0, 0, 0, 0) { Focusable = false, DrawContent = Draw };
        TextRig.Face(Typeface, Px * Metrics.Scale);
    }

    /// <summary>Shows <paramref name="text"/> from now, in place of any line still up. Cold: a press.</summary>
    internal static void Show(string text)
    {
        float px = Px * Metrics.Scale;
        if (TextRig.BakedFace(Typeface, px) == null) TextRig.Face(Typeface, px);
        Volatile.Write(ref S.Text, text);
        Volatile.Write(ref S.ShownAt, Stopwatch.GetTimestamp());
    }

    /// <summary>The line's strength <paramref name="age"/> seconds after its press: 1 through the hold, then down to 0.</summary>
    internal static float Alpha(float age) => age < Hold ? 1f : age >= Hold + Fade ? 0f : 1f - (age - Hold) / Fade;

    /// <summary>Per-frame draw (present thread, hot).</summary>
    private static void Draw()
    {
        long at = Volatile.Read(ref S.ShownAt);
        if (at == 0 || !GameGate.InGame) return;
        float a = Alpha((float)Stopwatch.GetElapsedTime(at).TotalSeconds);
        if (a <= 0f) { Interlocked.CompareExchange(ref S.ShownAt, 0, at); return; }
        string? text = Volatile.Read(ref S.Text);
        float w = TextRig.ViewportW, scale = Metrics.Scale;
        GlyphAtlas? atlas = TextRig.BakedFace(Typeface, Px * scale);
        if (text == null || atlas == null || w <= 0f) return;
        if (!ReferenceEquals(text, S.BakedText) || w != S.BakedW || scale != S.BakedScale || TextRig.AtlasEpoch != S.BakedEpoch)
            Bake(atlas, text, w, scale);
        TextRig.DrawBatch(S.Batch.AsSpan(), S.Batch.VertexCount, a);
    }

    /// <summary>Centres <paramref name="text"/> in the box, a black copy one shadow step down and right under the red.</summary>
    private static void Bake(GlyphAtlas atlas, string text, float w, float scale)
    {
        S.BakedText = text; S.BakedW = w; S.BakedScale = scale; S.BakedEpoch = TextRig.AtlasEpoch;
        S.Batch.Reset(atlas.SolidU, atlas.SolidV);
        float x0 = (w - UiText.Measure(atlas, text)) * 0.5f, off = Shadow * scale;
        float baseline = Top * scale + (BoxH * scale - atlas.LineHeight) * 0.5f + atlas.Ascent;
        Run(atlas, text, x0 + off, baseline + off, 0f, 0f, 0f);
        Run(atlas, text, x0, baseline, Red, Green, Blue);
    }

    private static void Run(GlyphAtlas atlas, string text, float x, float baseline, float r, float g, float b)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (atlas.TryGet(text[i], out Glyph gl)) { S.Batch.Glyph(gl, x, baseline, r, g, b, 1f); x += gl.Advance; }
            else x += atlas.AdvanceOf(new System.Text.Rune(text[i]));
        }
    }
}
