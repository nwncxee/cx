using System;
using System.Diagnostics;
using System.Text;
using CX.Framework;

namespace CX.Ui;

/// <summary>
/// CX's tooltip: the owner says what the pointer rests on (<see cref="Hover"/>) and when it leaves; after the
/// <c>ui.tipdelay</c> dwell the text fades in, in a box under the anchor wrapped to <see cref="MaxW"/>, and fades out
/// after <see cref="Leave"/>. A new key while one shows skips the dwell. No input of its own: hovering changes only pixels.
/// </summary>
public sealed class Tooltip
{
    /// <summary>Default dwell before the text shows, and the fade times; <c>ui.tipdelay</c>/<c>tipfadein</c>/<c>tipfadeout</c> set them.</summary>
    public const float DelaySec = 0.45f, FadeInSec = 0.12f, FadeOutSec = 0.15f;
    /// <summary>The live timings in seconds, from the <c>ui</c> group; the self-checks substitute fixed ones.</summary>
    internal static Func<(float Delay, float In, float Out)> Timing = () => (UiKnobs.TipDelay.V, UiKnobs.TipFadeIn.V, UiKnobs.TipFadeOut.V);
    /// <summary>Seconds that replace the player's dwell and fades for this tooltip alone; below 0 keeps the player's.</summary>
    public float DelayOverride = -1f, FadeInOverride = -1f, FadeOutOverride = -1f;
    public float Delay => DelayOverride >= 0f ? DelayOverride : Timing().Delay;
    private float FadeIn => FadeInOverride >= 0f ? FadeInOverride : Timing().In;
    private float FadeOut => FadeOutOverride >= 0f ? FadeOutOverride : Timing().Out;
    /// <summary>The marker text drawn beside a label.</summary>
    public const string Marker = "(?)";
    /// <summary>Marker alpha: faint, so it reads as an affordance, not a label.</summary>
    public const float MarkerAlpha = 0.45f;
    /// <summary>Widest text box before wrapping.</summary>
    public static float MaxW => 280f * Metrics.Scale;
    public static float Pad => 6f * Metrics.Scale;
    private const uint FrameRgb = 0x8C8C8C;  // not-an-offset: a colour

    /// <summary>Time source; the self-check substitutes a fake clock. Stopwatch ticks.</summary>
    internal static Func<long> Clock = Stopwatch.GetTimestamp;

    private object? _key;
    private string _text = "";
    private float _x, _top, _bottom;
    private long _since, _leftAt;
    private float _leftAlpha;
    private bool _shownAtTick;

    /// <summary>What the pointer rests on, or null.</summary>
    public object? Key => _key;
    public string Text => _text;
    public bool Hovering => _key != null;
    /// <summary>True once the dwell has elapsed on the current key.</summary>
    public bool Visible => _key != null && Clock() - _since >= Ticks(Delay);
    /// <summary>The box's opacity now: 0 in the dwell, rising over the fade-in, falling over the fade-out after a leave.</summary>
    public float Alpha
    {
        get
        {
            long now = Clock();
            if (_key == null)
                return _leftAlpha <= 0f ? 0f : FadeOut <= 0f ? 0f : Math.Max(0f, _leftAlpha * (1f - (float)(now - _leftAt) / Ticks(FadeOut)));
            long t = now - _since - Ticks(Delay);
            return t < 0 ? 0f : FadeIn <= 0f ? 1f : Math.Min(1f, (float)t / Ticks(FadeIn));
        }
    }

    private static long Ticks(float sec) => (long)(sec * Stopwatch.Frequency);

    /// <summary>The pointer rests on <paramref name="key"/>'s marker: left edge <paramref name="x"/>, its row
    /// band <paramref name="top"/>..<paramref name="bottom"/> (the box hangs under the band, or sits above it
    /// when it would run past the draw's clamp). A new key restarts the dwell; the same key just tracks the
    /// anchor. Returns true when the key changed.</summary>
    public bool Hover(object key, string text, float x, float top, float bottom)
    {
        if (ReferenceEquals(key, _key)) { _x = x; _top = top; _bottom = bottom; return false; }
        float shown = Alpha;
        _x = x; _top = top; _bottom = bottom;
        _key = key; _text = text; _shownAtTick = false; _leftAlpha = 0f;
        _since = Clock() - (shown > 0f ? Ticks(Delay) + (long)(shown * Ticks(FadeIn)) : 0);
        return true;
    }

    /// <summary>What the pointer rests on is now <paramref name="key"/>, for an owner that swaps its content under a pointer that has not moved:
    /// the dwell and the fade carry on as they were. Does nothing unless something is hovered.</summary>
    public void Retarget(object key)
    {
        if (_key != null) _key = key;
    }

    /// <summary>The pointer left the anchor: a shown box fades out (<see cref="Cut"/> drops it at once). Returns true
    /// when something was being hovered.</summary>
    public bool Leave()
    {
        if (_key == null) return false;
        _leftAlpha = Alpha; _leftAt = Clock();
        _key = null; _shownAtTick = false;
        return true;
    }

    /// <summary>Hide now, no fade: a press, a closed window.</summary>
    public void Cut() { Leave(); _leftAlpha = 0f; }

    /// <summary>Per-frame poll for owners that rebuild on change: true exactly once, on the frame the dwell
    /// elapses and the text becomes due. Zero-alloc.</summary>
    public bool Tick()
    {
        if (_shownAtTick || !Visible) return false;
        _shownAtTick = true;
        return true;
    }

    /// <summary>Advance width of the marker in this face.</summary>
    public static float MarkerW(GlyphAtlas atlas) => UiText.Measure(atlas, Marker);

    /// <summary>Draw the faint marker with its left edge at <paramref name="x"/>.</summary>
    public static void DrawMarker(VertexBatch b, GlyphAtlas atlas, float x, float baseline, uint rgb)
        => UiText.Draw(b, atlas, Marker, x, baseline, rgb, float.MaxValue, MarkerAlpha);

    /// <summary>Draw the text box under the anchor at <see cref="Alpha"/>: word-wrapped to <see cref="MaxW"/>,
    /// kept left of <paramref name="clampRight"/>, flipped above the row when it would pass
    /// <paramref name="clampBottom"/>. <paramref name="baseDrop"/> is the baseline offset from a line's top.
    /// Zero-alloc: the rune walk wraps in place.</summary>
    public void Draw(VertexBatch b, GlyphAtlas atlas, float baseDrop,
                     (float r, float g, float b, float a) bg, uint textRgb,
                     float clampRight = float.MaxValue, float clampBottom = float.MaxValue)
    {
        float alpha = Alpha;
        if (alpha <= 0f || _text.Length == 0) return;
        float maxW = MaxW, pad = Pad, lh = atlas.LineHeight;
        int lines = 0; float widest = 0f;
        Wrap(atlas, _text, maxW, null, 0f, 0f, 0f, 0, ref lines, ref widest);
        if (lines == 0) return;
        float w = widest + 2f * pad, h = lines * lh + 2f * pad;
        float x0 = _x, y0 = _bottom + 2f;
        if (y0 + h > clampBottom && _top - 2f - h >= 0f) y0 = _top - 2f - h;
        if (x0 + w > clampRight) x0 = clampRight - w;
        if (x0 < 0f) x0 = 0f;
        float fr = ((FrameRgb >> 16) & 0xFF) / 255f, fg = ((FrameRgb >> 8) & 0xFF) / 255f, fb = (FrameRgb & 0xFF) / 255f;
        b.Rect(x0 - 1f, y0 - 1f, x0 + w + 1f, y0 + h + 1f, fr, fg, fb, alpha);
        b.Rect(x0, y0, x0 + w, y0 + h, bg.r, bg.g, bg.b, 0.98f * alpha);
        int drawn = 0; float _ = 0f;
        Wrap(atlas, _text, maxW, b, x0 + pad, y0 + pad + baseDrop, lh, textRgb, ref drawn, ref _, alpha);
    }

    /// <summary>Greedy word wrap: one pass counts lines and the widest line (no batch), the same pass with a
    /// batch draws them. Breaks at spaces; a word wider than the box is cut at the box.</summary>
    private static void Wrap(GlyphAtlas atlas, string s, float maxW, VertexBatch? b, float x, float baseline, float lh,
                             uint rgb, ref int lines, ref float widest, float alpha = 1f)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float lineW = 0f, wordW = 0f;
        int lineStart = 0, wordStart = 0, i = 0;
        // Walk runes; a line is [lineStart, i) in char indices, committed when the next word would overflow.
        foreach (Rune ru in s.EnumerateRunes())
        {
            int len = ru.Utf16SequenceLength;
            float adv = atlas.AdvanceOf(ru);
            if (ru.Value == ' ')
            {
                lineW += wordW + adv; wordW = 0f; wordStart = i + len;
            }
            else
            {
                if (lineW + wordW + adv > maxW && lineW > 0f)
                {
                    Emit(atlas, s, lineStart, wordStart, b, x, baseline + lines * lh, r, g, bl, alpha, ref widest, lineW);
                    lines++;
                    lineStart = wordStart; lineW = 0f;
                }
                else if (lineW + wordW + adv > maxW)
                {
                    Emit(atlas, s, lineStart, i, b, x, baseline + lines * lh, r, g, bl, alpha, ref widest, wordW);
                    lines++;
                    lineStart = i; wordStart = i; wordW = 0f;
                }
                wordW += adv;
            }
            i += len;
        }
        if (i > lineStart)
        {
            Emit(atlas, s, lineStart, i, b, x, baseline + lines * lh, r, g, bl, alpha, ref widest, lineW + wordW);
            lines++;
        }
    }

    private static void Emit(GlyphAtlas atlas, string s, int from, int to, VertexBatch? b, float x, float baseline,
                             float r, float g, float bl, float a, ref float widest, float w)
    {
        while (to > from && s[to - 1] == ' ') { w -= atlas.SpaceAdvance; to--; }
        if (w > widest) widest = w;
        if (b == null) return;
        float pen = x;
        foreach (Rune ru in s.AsSpan(from, to - from).EnumerateRunes())
        {
            if (atlas.TryGet(ru.Value, out Glyph gl)) { b.Glyph(gl, pen, baseline, r, g, bl, a); pen += gl.Advance; }
            else pen += atlas.AdvanceOf(ru);
        }
    }

    /// <summary>Off-device proof against hand-worked times (dwell 0.5 s, fade in 0.2 s, out 0.4 s, a fake clock): the
    /// dwell, the fade ramps, a neighbour skipping the dwell, Leave's fade-out and Cut, and a zero-alloc wrapped draw.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        long now = 0, f = Stopwatch.Frequency;
        Func<long> real = Clock;
        var realTiming = Timing;
        Clock = () => now;
        Timing = () => (0.5f, 0.2f, 0.4f);
        long delay = f / 2, full = delay + f / 5;
        static bool Near(float v, float want) => Math.Abs(v - want) < 0.01f;
        try
        {
            var t = new Tooltip();
            object a = new object(), bKey = new object(), c = new object();
            check.Expect(!t.Hovering && !t.Visible && !t.Tick() && t.Alpha == 0f, "idle: nothing hovered, nothing due");
            check.Expect(t.Hover(a, "Help A", 10f, 20f, 40f) && t.Hovering && !t.Visible, "hover starts the dwell, not yet visible");
            check.Expect(!t.Hover(a, "Help A", 12f, 20f, 40f), "same key: no change reported");
            now = delay - 1;
            check.Expect(!t.Visible && !t.Tick() && t.Alpha == 0f, "just under the dwell: still hidden");
            now = delay;
            check.Expect(t.Visible && t.Tick() && !t.Tick(), "at the dwell: visible, Tick fires exactly once");
            now = delay + f / 10;
            check.Expect(Near(t.Alpha, 0.5f), $"half way through the 0.2 s fade-in: alpha 0.5 ({t.Alpha})");
            now = full;
            check.Expect(t.Alpha == 1f, "the fade-in done: opaque");
            check.Expect(t.Hover(bKey, "Help B", 0f, 0f, 20f) && t.Visible && t.Alpha == 1f && t.Text == "Help B", "a neighbour while one shows: no dwell, no fade");
            check.Expect(t.Leave() && !t.Hovering && !t.Visible && t.Alpha == 1f && !t.Leave(), "Leave clears the key once; the box starts its fade-out");
            now += f / 5;
            check.Expect(Near(t.Alpha, 0.5f), $"half way through the 0.4 s fade-out: alpha 0.5 ({t.Alpha})");
            now += f / 5;
            check.Expect(t.Alpha == 0f, "the fade-out done: gone");
            check.Expect(t.Hover(c, "Help C", 0f, 0f, 20f) && !t.Visible, "after the fade-out a new key waits the dwell again");
            now += full;
            t.Cut();
            check.Expect(t.Alpha == 0f && !t.Hovering, "Cut hides at once");

            // Wrap + zero-alloc draw.
            var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15, SpaceAdvance = 4f };
            atlas.SetTestGlyph(' ', 4f);
            for (char ch = 'a'; ch <= 'z'; ch++) atlas.SetTestGlyph(ch, 10f);
            var vb = new VertexBatch(atlas.SolidU, atlas.SolidV);
            string help = "the quick brown fox jumps over the lazy dog again and again until the box wraps it";
            t.Hover(a, help, 100f, 100f, 120f); now += full;
            check.Expect(t.Visible, "visible for the draw");
            int lines = 0; float widest = 0f;
            Wrap(atlas, help, MaxW, null, 0f, 0f, 0f, 0, ref lines, ref widest);
            check.Expect(lines >= 3 && widest <= MaxW, $"the help wraps into several lines within MaxW (lines={lines}, widest={widest})");
            t.Draw(vb, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), 0xFFFFFF, 2000f);
            check.Expect(vb.VertexCount >= 12, "the draw emitted the box");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); t.Draw(vb, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), 0xFFFFFF, 2000f); }
            long deltaB = GC.GetAllocatedBytesForCurrentThread() - before;
            check.Expect(deltaB == 0, $"100 tooltip draws allocate nothing ({deltaB} bytes)");
            // Clamp: a box anchored at the right edge slides left to stay on-screen; one at the bottom flips above its row.
            t.Hover(bKey, "abc", 1990f, 10f, 30f); now += full;
            vb.Clear(); t.Draw(vb, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), 0xFFFFFF, 2000f);
            float[] v = vb.ToArray();
            check.Expect(vb.VertexCount > 0 && v[0] >= 0f && v[1 * VertexBatch.Floats] <= 2000f + 1.01f, "clamped draw stays left of the right edge");
            t.Hover(a, "abc", 100f, 980f, 1000f); now += full;
            vb.Clear(); t.Draw(vb, atlas, 15f, (0.1f, 0.1f, 0.1f, 1f), 0xFFFFFF, 2000f, 1000f);
            v = vb.ToArray();
            check.Expect(vb.VertexCount > 0 && v[1] < 980f, "a box past the bottom clamp flips above its row");
            t.Leave();
        }
        finally { Clock = real; Timing = realTiming; }

        log.Info(check.Ok ? "Tooltip.SelfCheck: passed" : "Tooltip.SelfCheck: FAILED");
    }
}
