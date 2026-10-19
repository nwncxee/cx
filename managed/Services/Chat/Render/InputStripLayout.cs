using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Chat.Render;

/// <summary>
/// The one geometry of the chat input strip's edit text: where it starts, how wide it wraps, which wrapped row
/// a caret sits on, and which UTF-16 index a pointer or an Up/Down key lands on -- the renderer draws from it
/// and the input thread hit-tests against it, so a click can never disagree with the drawn caret. Every entry
/// point takes the atlas the strip DRAWS with (the UI face, NOT the body face, which measures a different wrap).
/// </summary>
public static class InputStripLayout
{
    /// <summary>The counter switches to <c>n/max</c> in a warning colour at this fraction of the line cap, so the
    /// ceiling shows before the buffer starts refusing input.</summary>
    private const float CounterWarnFrac = 0.9f;

    /// <summary>Line length at which the counter shows <c>n/max</c> in the warning colour.</summary>
    internal const int CounterWarn = (int)(EditBuffer.MaxLength * CounterWarnFrac);

    /// <summary>The edit area's left x and wrap width for this window: right of the channel chip, minus the
    /// counter's reserve when <paramref name="counter"/> is on. Pass the UI face atlas (see the type
    /// summary).</summary>
    internal static void EditArea(ChatWindow win, GlyphAtlas atlas, int textLength, bool counter,
                                  out float startX, out float editW)
    {
        startX = win.X + ChatWindow.DropdownW + ChatWindow.Pad;
        editW = win.X + win.W - ChatWindow.Pad - startX - CounterReserve(atlas, textLength, counter);
    }

    /// <summary>Width (px) the counter holds back from the wrap: the widest string its current mode can show --
    /// the cap's digit count, doubled with a <c>/</c> past <see cref="CounterWarn"/> -- so the wrap does not
    /// jitter as the count's own width changes per keystroke.</summary>
    private static float CounterReserve(GlyphAtlas atlas, int textLength, bool counter)
    {
        if (!counter) return 0f;
        int digits = 1;
        for (int m = EditBuffer.MaxLength; m >= 10; m /= 10) digits++;
        float digit = 0f;
        for (int d = '0'; d <= '9'; d++)
        {
            float a = Advance(atlas, new Rune(d));
            if (a > digit) digit = a;
        }
        float w = digits * digit;
        if (textLength >= CounterWarn) w += Advance(atlas, new Rune('/')) + digits * digit;
        return w + ChatWindow.Pad;
    }

    /// <summary>True when (<paramref name="x"/>,<paramref name="y"/>) is inside the visible strip and right of the
    /// channel chip -- where a click places the caret rather than opening the channel list.</summary>
    public static bool InEditArea(ChatWindow win, float x, float y)
    {
        if (!win.FooterVisible) return false;
        float top = win.StripTop;
        return y >= top && y < top + win.FooterHeight
            && x >= win.X + ChatWindow.DropdownW && x < win.X + win.W;
    }

    /// <summary>Pointer → UTF-16 index: the nearest caret boundary, or with <paramref name="charUnder"/> the index
    /// of the char the pointer is over (what double-click word selection needs). The row comes from
    /// <see cref="ChatWindow.StripTop"/>, x is clamped into that row. Pass the UI face atlas.</summary>
    public static int HitTest(ChatWindow win, GlyphAtlas atlas, string text, float x, float y, bool charUnder = false)
        => HitTest(win, atlas, text, x, y, ChatKnobs.Counter.V, charUnder);

    /// <summary>The knob-free form, so a self-check's geometry does not shift with the player's counter setting.</summary>
    internal static int HitTest(ChatWindow win, GlyphAtlas atlas, string text, float x, float y,
                                bool counter, bool charUnder)
    {
        var runes = new List<Rune>();   // input thread: never the renderer's scratch, a build may be running
        Layout(win, atlas, text, runes, counter, out List<TextWrapper.LineSpan> wraps, out float startX, out _);
        int row = (int)((y - win.StripTop) / ChatWindow.FooterH);
        if (row < 0) row = 0;
        else if (row >= wraps.Count) row = wraps.Count - 1;
        TextWrapper.LineSpan span = wraps[row];
        if (charUnder)
        {
            float px = startX;
            for (int k = span.Start; k < span.End; k++)
            {
                float adv = Advance(atlas, runes[k]);
                if (x < px + adv) return Utf16Index(runes, k);
                px += adv;
            }
            return Utf16Index(runes, span.End > span.Start ? span.End - 1 : span.Start);
        }
        return Utf16Index(runes, NearestOnRow(atlas, runes, span, startX, x));
    }

    /// <summary>Up/Down between wrapped rows: the UTF-16 index on the row above (<paramref name="dir"/> -1) or
    /// below (+1) nearest the caret's current x, or -1 when the caret already sits on the first/last row -- the
    /// caller then walks the sent-line history instead. Pass the UI face atlas.</summary>
    public static int VerticalMove(ChatWindow win, GlyphAtlas atlas, string text, int caret, int dir)
        => VerticalMove(win, atlas, text, caret, dir, ChatKnobs.Counter.V);

    /// <summary>The knob-free form, so a self-check's geometry does not shift with the player's counter setting.</summary>
    internal static int VerticalMove(ChatWindow win, GlyphAtlas atlas, string text, int caret, int dir, bool counter)
    {
        var runes = new List<Rune>();
        Layout(win, atlas, text, runes, counter, out List<TextWrapper.LineSpan> wraps, out float startX, out _);
        int caretR = RuneIndex(runes, caret);
        int from = RowOf(wraps, caretR), to = from + (dir < 0 ? -1 : 1);
        if (to < 0 || to >= wraps.Count) return -1;
        float x = RowAlign.Measure(atlas, runes, wraps[from].Start, caretR, startX);
        return Utf16Index(runes, NearestOnRow(atlas, runes, wraps[to], startX, x));
    }

    /// <summary>Wrap the edit text into <paramref name="runes"/> (cleared first) and the returned spans at this
    /// window's edit width -- the one layout the strip draws and every hit-test reads. Always at least one row.</summary>
    internal static void Layout(ChatWindow win, GlyphAtlas atlas, string text, List<Rune> runes,
                               out List<TextWrapper.LineSpan> wraps, out float startX, out float editW)
        => Layout(win, atlas, text, runes, ChatKnobs.Counter.V, out wraps, out startX, out editW);

    /// <summary>The knob-free form; the counter reserve is the only thing the flag changes.</summary>
    internal static void Layout(ChatWindow win, GlyphAtlas atlas, string text, List<Rune> runes, bool counter,
                               out List<TextWrapper.LineSpan> wraps, out float startX, out float editW)
    {
        EditArea(win, atlas, text.Length, counter, out startX, out editW);
        runes.Clear();
        foreach (Rune r in text.EnumerateRunes()) runes.Add(r);
        wraps = TextWrapper.WrapRunes(runes, editW, atlas.AdvanceOfCached);
        if (wraps.Count == 0) wraps.Add(new TextWrapper.LineSpan(0, 0));
    }

    /// <summary>Does the caret at rune index <paramref name="caretR"/> draw on row <paramref name="row"/>? At a
    /// row's End it belongs to the NEXT row only on a contiguous hard break (no consumed space); when a space was
    /// consumed, End stays on this row.</summary>
    internal static bool CaretOnRow(List<TextWrapper.LineSpan> wraps, int row, int caretR)
    {
        TextWrapper.LineSpan span = wraps[row];
        return caretR >= span.Start && caretR <= span.End
            && !(caretR == span.End && row < wraps.Count - 1 && wraps[row + 1].Start == span.End);
    }

    /// <summary>The wrapped row the caret draws on, per <see cref="CaretOnRow"/>.</summary>
    internal static int RowOf(List<TextWrapper.LineSpan> wraps, int caretR)
    {
        for (int w = 0; w < wraps.Count; w++) if (CaretOnRow(wraps, w, caretR)) return w;
        return wraps.Count - 1;
    }

    /// <summary>Rune index of the caret boundary in <paramref name="span"/> nearest pen position
    /// <paramref name="x"/>; x outside the row clamps to its ends. Ties keep the earlier boundary.</summary>
    private static int NearestOnRow(GlyphAtlas atlas, List<Rune> runes, TextWrapper.LineSpan span,
                                    float startX, float x)
    {
        float px = startX, bestD = System.Math.Abs(x - px);
        int best = span.Start;
        for (int k = span.Start; k < span.End; k++)
        {
            px += Advance(atlas, runes[k]);
            float d = System.Math.Abs(x - px);
            if (d < bestD) { bestD = d; best = k + 1; }
        }
        return best;
    }

    /// <summary>Glyph advance where the atlas has it, the fallback advance otherwise -- what
    /// <see cref="RowAlign.Measure"/> sums, for a single rune.</summary>
    private static float Advance(GlyphAtlas atlas, Rune r)
        => atlas.TryGet(r.Value, out Glyph g) ? g.Advance : atlas.AdvanceOf(r);

    /// <summary>Rune-list index of a UTF-16 index (they diverge only past the BMP).</summary>
    internal static int RuneIndex(List<Rune> runes, int utf16)
    {
        int u = 0;
        for (int k = 0; k < runes.Count; k++) { if (u >= utf16) return k; u += runes[k].Utf16SequenceLength; }
        return runes.Count;
    }

    /// <summary>UTF-16 index of a rune-list index -- the inverse of <see cref="RuneIndex"/>.</summary>
    internal static int Utf16Index(List<Rune> runes, int runeIdx)
    {
        int u = 0;
        for (int k = 0; k < runeIdx && k < runes.Count; k++) u += runes[k].Utf16SequenceLength;
        return u;
    }

    /// <summary>Off-device proof that the strip's hit-tests read the same wrap the strip draws: rows by y,
    /// clamping at both ends of a row, char-under vs nearest boundary, Up/Down keeping x and refusing at the
    /// edges, and the counter's reserve narrowing the wrap. Runs at <c>Metrics.Scale == 1</c>.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        const float Adv = 40f;   // wide glyphs: 296px of edit width holds 7, so a short line wraps three ways
        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        atlas.SetTestGlyph(' ', Adv);
        foreach (char ch in "abcdefghijkl") atlas.SetTestGlyph(ch, Adv);
        for (int d = '0'; d <= '9'; d++) atlas.SetTestGlyph(d, Adv);
        atlas.SetTestGlyph('/', Adv);

        var win = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        win.FooterVisible = true;
        const string Text = "abcd efgh ijkl";   // wraps to [0,4) [5,9) [10,14)
        EditArea(win, atlas, Text.Length, counter: false, out float startX, out float editW);
        check.Expect(System.Math.Abs(startX - (ChatWindow.DropdownW + ChatWindow.Pad)) < 1e-3f, "edit text starts right of the channel chip");

        var runes = new List<Rune>();
        Layout(win, atlas, Text, runes, counter: false, out List<TextWrapper.LineSpan> wraps, out _, out _);
        check.Expect(wraps.Count == 3 && wraps[1].Start == 5 && wraps[2].Start == 10, "the sample wraps into three rows");

        int Hit(float x, float y, bool charUnder = false) => HitTest(win, atlas, Text, x, y, false, charUnder);
        float row0 = win.StripTop + 1f, row1 = win.StripTop + ChatWindow.FooterH + 1f;
        float row2 = win.StripTop + ChatWindow.FooterH * 2f + 1f;
        check.Expect(Hit(startX + Adv * 2f + 2f, row0) == 2, "row 0: nearest boundary under the pointer");
        check.Expect(Hit(startX + 1f, row1) == 5, "row 1: y picks the second row's first boundary");
        check.Expect(Hit(startX - 20f, row0) == 0, "a click left of the text clamps to the row start");
        check.Expect(Hit(win.X + win.W, row2) == Text.Length, "a click past the end of the last row is the line end");

        float rightHalf = startX + Adv * 2f + Adv * 0.6f;   // inside glyph 2, past its middle
        check.Expect(Hit(rightHalf, row0) == 3, "the right half of a glyph is nearer the boundary after it");
        check.Expect(Hit(rightHalf, row0, charUnder: true) == 2, "charUnder names the glyph itself, not the nearer boundary");

        check.Expect(VerticalMove(win, atlas, Text, 2, +1, counter: false) == 7, "Down keeps x and lands on the row below");
        check.Expect(VerticalMove(win, atlas, Text, 7, -1, counter: false) == 2, "Up keeps x and lands back on the row above");
        check.Expect(VerticalMove(win, atlas, Text, 2, -1, counter: false) == -1, "Up from the first row refuses (the caller walks history)");
        check.Expect(VerticalMove(win, atlas, Text, 12, +1, counter: false) == -1, "Down from the last row refuses (the caller walks history)");

        check.Expect(InEditArea(win, startX + 5f, row0), "a click in the strip right of the chip is an edit-area click");
        check.Expect(!InEditArea(win, win.X + 5f, row0), "a click on the channel chip is not");
        check.Expect(!InEditArea(win, startX + 5f, win.StripTop - 5f), "a click above the strip is not");

        EditArea(win, atlas, 0, counter: true, out _, out float countW);
        EditArea(win, atlas, EditBuffer.MaxLength, counter: true, out _, out float capW);
        check.Expect(countW < editW, "the counter's reserve narrows the wrap width");
        check.Expect(capW < countW, "near the cap the reserve grows to hold n/max");

        win.FooterVisible = false;
        log.Info(check.Ok ? "InputStripLayout.SelfCheck PASS" : "InputStripLayout.SelfCheck had FAILURES");
    }
}
