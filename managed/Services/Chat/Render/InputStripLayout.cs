using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Chat.Render;

/// <summary>
/// The one geometry of the chat input strip's edit text: where it starts, how wide it shows, how far it is
/// scrolled, and which UTF-16 index a pointer lands on -- the renderer draws from it and the input thread
/// hit-tests against it, so a click can never disagree with the drawn caret. Every entry point takes the atlas
/// the strip DRAWS with (the UI face, NOT the body face, which measures differently).
/// </summary>
public static class InputStripLayout
{
    /// <summary>The counter switches to <c>n/max</c> in a warning colour at this fraction of the line cap, so the
    /// ceiling shows before the buffer starts refusing input.</summary>
    private const float CounterWarnFrac = 0.9f;

    /// <summary>Line length at which the counter shows <c>n/max</c> in the warning colour.</summary>
    internal const int CounterWarn = (int)(EditBuffer.MaxLength * CounterWarnFrac);

    /// <summary>The edit area's left x and shown width for this window: right of the channel chip, minus the
    /// counter's reserve when <paramref name="counter"/> is on. Pass the UI face atlas (see the type
    /// summary).</summary>
    internal static void EditArea(ChatWindow win, GlyphAtlas atlas, int textLength, bool counter,
                                  out float startX, out float editW)
    {
        startX = win.X + ChatWindow.DropdownW + ChatWindow.Pad;
        editW = win.X + win.W - ChatWindow.Pad - startX - CounterReserve(atlas, textLength, counter);
    }

    /// <summary>Width (px) the counter holds back from the line: the widest string its current mode can show --
    /// the cap's digit count, doubled with a <c>/</c> past <see cref="CounterWarn"/> -- so the line does not
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
        return y >= top && y < top + ChatWindow.FooterH
            && x >= win.X + ChatWindow.DropdownW && x < win.X + win.W;
    }

    /// <summary>Pointer → UTF-16 index: the nearest caret boundary, or with <paramref name="charUnder"/> the index
    /// of the char the pointer is over (what double-click word selection needs), on the line as scrolled by
    /// <see cref="ChatWindow.InputScroll"/>; x outside the line clamps to its ends. Pass the UI face atlas.</summary>
    public static int HitTest(ChatWindow win, GlyphAtlas atlas, string text, float x, float y, bool charUnder = false)
        => HitTest(win, atlas, text, x, ChatKnobs.Counter.V, charUnder);

    /// <summary>The knob-free form, so a self-check's geometry does not shift with the player's counter setting.</summary>
    internal static int HitTest(ChatWindow win, GlyphAtlas atlas, string text, float x, bool counter, bool charUnder)
    {
        var runes = new List<Rune>();   // input thread: never the renderer's scratch, a build may be running
        Layout(win, atlas, text, runes, counter, out float startX, out _);
        float penX = startX - win.InputScroll;
        if (charUnder)
        {
            float px = penX;
            for (int k = 0; k < runes.Count; k++)
            {
                float adv = Advance(atlas, runes[k]);
                if (x < px + adv) return Utf16Index(runes, k);
                px += adv;
            }
            return Utf16Index(runes, runes.Count > 0 ? runes.Count - 1 : 0);
        }
        return Utf16Index(runes, Nearest(atlas, runes, penX, x));
    }

    /// <summary>Split the edit text into <paramref name="runes"/> (cleared first) and place the edit area: the one
    /// layout the strip draws and every hit-test reads. The line never wraps: it scrolls.</summary>
    internal static void Layout(ChatWindow win, GlyphAtlas atlas, string text, List<Rune> runes, bool counter,
                               out float startX, out float editW)
    {
        EditArea(win, atlas, text.Length, counter, out startX, out editW);
        runes.Clear();
        foreach (Rune r in text.EnumerateRunes()) runes.Add(r);
    }

    /// <summary>Scroll the line just enough to keep the caret at rune <paramref name="caretR"/> inside the
    /// <paramref name="editW"/> px it shows in, and no further than its end allows; stores and returns
    /// <see cref="ChatWindow.InputScroll"/>.</summary>
    internal static float Follow(ChatWindow win, GlyphAtlas atlas, List<Rune> runes, int caretR, float editW)
    {
        float caretX = 0f, textW = 0f;
        for (int k = 0; k < runes.Count; k++)
        {
            if (k == caretR) caretX = textW;
            textW += Advance(atlas, runes[k]);
        }
        if (caretR >= runes.Count) caretX = textW;
        float s = win.InputScroll;
        if (caretX - s > editW) s = caretX - editW;
        if (caretX < s) s = caretX;
        float most = textW > editW ? textW - editW : 0f;
        win.InputScroll = s < 0f ? 0f : (s > most ? most : s);
        return win.InputScroll;
    }

    /// <summary>Rune index of the caret boundary nearest pen position <paramref name="x"/>, the line's first glyph
    /// starting at <paramref name="penX"/>; ties keep the earlier boundary.</summary>
    private static int Nearest(GlyphAtlas atlas, List<Rune> runes, float penX, float x)
    {
        float px = penX, bestD = System.Math.Abs(x - px);
        int best = 0;
        for (int k = 0; k < runes.Count; k++)
        {
            px += Advance(atlas, runes[k]);
            float d = System.Math.Abs(x - px);
            if (d < bestD) { bestD = d; best = k + 1; }
        }
        return best;
    }

    /// <summary>Glyph advance where the atlas has it, the fallback advance otherwise.</summary>
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

    /// <summary>Off-device proof that the strip's hit-tests read the same line the strip draws: clamping at both
    /// ends, char-under vs nearest boundary, the scroll following the caret both ways, and the counter's reserve
    /// narrowing the line. Oracle: positions worked by hand from 40 px glyphs. Runs at <c>Metrics.Scale == 1</c>.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        const float Adv = 40f;   // wide glyphs: 296px of edit width holds 7.4
        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        atlas.SetTestGlyph(' ', Adv);
        foreach (char ch in "abcdefghijklm") atlas.SetTestGlyph(ch, Adv);
        for (int d = '0'; d <= '9'; d++) atlas.SetTestGlyph(d, Adv);
        atlas.SetTestGlyph('/', Adv);

        var win = new ChatWindow(0, 0, 400, 300, new[] { new Tab("All", 0xffffffff) });
        win.FooterVisible = true;
        const string Text = "abcd";
        EditArea(win, atlas, Text.Length, counter: false, out float startX, out float editW);
        check.Expect(startX == 98f && editW == 296f, "the line starts right of the channel chip and shows 296 px");

        int Hit(string t, float x, bool charUnder = false) => HitTest(win, atlas, t, x, false, charUnder);
        check.Expect(Hit(Text, startX + Adv * 2f + 2f) == 2, "nearest boundary under the pointer");
        check.Expect(Hit(Text, startX - 20f) == 0, "a click left of the text clamps to the line start");
        check.Expect(Hit(Text, win.X + win.W) == Text.Length, "a click past the end is the line end");
        float rightHalf = startX + Adv * 2f + Adv * 0.6f;   // inside glyph 2, past its middle
        check.Expect(Hit(Text, rightHalf) == 3, "the right half of a glyph is nearer the boundary after it");
        check.Expect(Hit(Text, rightHalf, charUnder: true) == 2, "charUnder names the glyph itself, not the nearer boundary");

        const string Long = "abcdefghijklm";   // 520 px
        var runes = new List<Rune>();
        Layout(win, atlas, Long, runes, false, out _, out _);
        check.Expect(Follow(win, atlas, runes, 13, editW) == 224f, "a caret at the end scrolls the line 520 - 296 = 224 px");
        check.Expect(Follow(win, atlas, runes, 10, editW) == 224f, "a caret still in sight leaves the scroll alone");
        check.Expect(Follow(win, atlas, runes, 2, editW) == 80f, "a caret left of the view scrolls back to it");
        check.Expect(Follow(win, atlas, runes, 0, editW) == 0f, "Home scrolls to the start");
        Layout(win, atlas, Text, runes, false, out _, out _);
        win.InputScroll = 200f;
        check.Expect(Follow(win, atlas, runes, 4, editW) == 0f, "a line that fits never scrolls");

        check.Expect(InEditArea(win, startX + 5f, win.StripTop + 1f), "a click in the strip right of the chip is an edit-area click");
        check.Expect(!InEditArea(win, win.X + 5f, win.StripTop + 1f), "a click on the channel chip is not");
        check.Expect(!InEditArea(win, startX + 5f, win.StripTop - 5f), "a click above the strip is not");

        EditArea(win, atlas, 0, counter: true, out _, out float countW);
        EditArea(win, atlas, EditBuffer.MaxLength, counter: true, out _, out float capW);
        check.Expect(countW < editW, "the counter's reserve narrows the line");
        check.Expect(capW < countW, "near the cap the reserve grows to hold n/max");

        win.FooterVisible = false;
        win.InputScroll = 0f;
        log.Info(check.Ok ? "InputStripLayout.SelfCheck PASS" : "InputStripLayout.SelfCheck had FAILURES");
    }
}
