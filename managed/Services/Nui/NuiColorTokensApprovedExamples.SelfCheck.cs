using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>NWN colour tokens in NUI text, approved examples E1 to E14; E10 and E14 are the game's to show, so they are
/// hand checks. Oracle: each label's text, its pieces, their colours as nk_color words and their x, typed by hand here;
/// the font is a stand-in ten pixels wide per byte.</summary>
internal static unsafe class NuiColorTokensApprovedExamples
{
    /// <summary>The label's own colour, (200, 200, 200, 255) as an nk_color word.</summary>
    internal const uint Own = 0xFFC8C8C8; // not-an-offset: an nk_color
    /// <summary>(254, 212, 1) at the label's alpha.</summary>
    private const uint Gold = 0xFF01D4FE; // not-an-offset: an nk_color
    /// <summary>(254, 1, 1) at the label's alpha.</summary>
    private const uint Red = 0xFF0101FE; // not-an-offset: an nk_color
    /// <summary>(1, 254, 1) at the label's alpha.</summary>
    private const uint Green = 0xFF01FE01; // not-an-offset: an nk_color
    /// <summary>(111, 111, 108), the letters o, o and l, at the label's alpha.</summary>
    private const uint Cool = 0xFF6C6F6F; // not-an-offset: an nk_color

    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiColorTokensApprovedExamples.SelfCheck");
        bool nui = NuiKnobs.Enabled.V, on = NuiKnobs.ColorTokens.V;
        NuiKnobs.Enabled.Set(true);
        NuiKnobs.ColorTokens.Set(true);
        GoldSpan(check);
        NoToken(check);
        CoolToken(check);
        Nesting(check);
        Unclosed(check);
        StrayClose(check);
        NoRoom(check);
        WrapsAlike(check);
        CarriesAcrossLines(check);
        EditField(check);
        SettingOff(check);
        Untokened(check);
        NuiKnobs.ColorTokens.Set(on);
        NuiKnobs.Enabled.Set(nui);
        log.Info(check.Ok ? "NuiColorTokensApprovedExamples.SelfCheck: passed" : "NuiColorTokensApprovedExamples.SelfCheck: FAILED");
    }

    // Given a server's NUI window with a label reading: Gold: [gold token]1500[close] gp
    // When the window opens
    // Then 1500 shows in gold, Gold: and gp show in the label's own colour, and none of the token characters show
    private static void GoldSpan(Checker check) =>
        Expect(check, "E1", "Gold: " + Tok(254, 212, 1) + "1500</c> gp", "Gold: |Own|0 1500|Gold|60  gp|Own|100");

    // Given a label reading: Press <Enter> when x < y
    // When the window opens
    // Then it shows exactly that text, in the label's own colour
    private static void NoToken(Checker check) =>
        Expect(check, "E2", "Press <Enter> when x < y", null);

    // Given a label reading: <cool> stuff
    // When the window opens
    // Then <cool> does not show, and stuff shows in the grey its three letters o, o, l make (111, 111, 108), as a native GUI label shows the same text
    private static void CoolToken(Checker check) =>
        Expect(check, "E3", "<cool> stuff", " stuff|Cool|0");

    // Given a label reading: [red]A [green]B[close] C[close] D
    // When the window opens
    // Then A is red, B is green, C is red again, and D is in the label's own colour
    private static void Nesting(Checker check) =>
        Expect(check, "E4", Tok(254, 1, 1) + "A " + Tok(1, 254, 1) + "B</c> C</c> D", "A |Red|0 B|Green|20  C|Red|30  D|Own|50");

    // Given a label reading: [red]Warning with no close, and below it a second label reading: Plain
    // When the window opens
    // Then Warning is red and Plain is in its own label's colour
    private static void Unclosed(Checker check)
    {
        Expect(check, "E5 first label", Tok(254, 1, 1) + "Warning", "Warning|Red|0");
        Expect(check, "E5 second label", "Plain", null);
    }

    // Given a label reading: Text[close] more
    // When the window opens
    // Then it shows Text more in the label's own colour, and no close token shows
    private static void StrayClose(Checker check) =>
        Expect(check, "E6", "Text</c> more", "Text|Own|0  more|Own|40");

    /// <summary>nuklear centres a label on the width its font reports, then draws from the left edge it worked out.
    /// Oracle: Title is five bytes, 50 px in the stand-in font, and its first piece starts at that edge.</summary>
    // Given two centred labels of the same width, one reading [red]Title[close] and one reading Title
    // When the window opens
    // Then both words sit at the same place
    private static void NoRoom(Checker check)
    {
        float w = NuiColorText.Width(Utf8(Tok(254, 1, 1) + "Title</c>"), Font);
        check.Expect(w == 50f, $"E7 the tokened title measures {w} px, not the 50 px of Title alone");
        Expect(check, "E7", Tok(254, 1, 1) + "Title</c>", "Title|Red|0");
    }

    /// <summary>The box breaks a line where the width its font reports for the text so far passes the box's width, so
    /// every prefix that ends on a word must measure what the same words measure without tokens. Oracle: ten pixels
    /// per shown byte.</summary>
    // Given a NUI text box whose long paragraph holds several colour tokens, and a second box of the same size holding the same paragraph without them
    // When the window opens
    // Then both boxes break their lines after the same words
    private static void WrapsAlike(Checker check)
    {
        string tokened = "The " + Tok(254, 1, 1) + "red</c> fox and the " + Tok(1, 254, 1) + "green " + Tok(254, 212, 1) + "gold</c> hound</c> ran";
        string[] words = { "The", "red", "fox", "and", "the", "green", "gold", "hound", "ran" };
        byte[] bytes = Utf8(tokened);
        int shown = 0, at = 0;
        foreach (string word in words)
        {
            at = IndexAfter(tokened, word, at);
            shown += word.Length + (shown == 0 ? 0 : 1);
            float w = NuiColorText.Width(bytes.AsSpan(0, Encoding.UTF8.GetByteCount(tokened[..at])), Font);
            check.Expect(w == shown * 10f, $"E8 the text up to '{word}' measures {w} px, where the same words alone measure {shown * 10} px");
        }
        PartToken(check, "The <c", 40f);
        PartToken(check, "The <c" + (char)254, 40f);
        PartToken(check, "The <c" + (char)254 + (char)1 + (char)1, 40f);
        PartToken(check, "The " + Tok(254, 1, 1) + "red</", 70f);
    }

    /// <summary>nuklear's clamp measures each glyph prefix too, so a prefix that stops inside a token measures only its
    /// shown text. Oracle: <paramref name="want"/>, ten pixels per shown byte, typed by the caller.</summary>
    private static void PartToken(Checker check, string prefix, float want)
    {
        float w = NuiColorText.Width(Utf8(prefix), Font);
        check.Expect(w == want, $"E8 a prefix stopping inside a token, [{prefix}], measures {w} px, where its shown text measures {want} px");
    }

    // Given a NUI text box whose red span is long enough to wrap onto the next line
    // When the window opens
    // Then the words of that span on the next line are red too, and the text after its close is in the box's own colour
    private static void CarriesAcrossLines(Checker check)
    {
        NuiColorText.BeginBlock();
        Expect(check, "E9 first line", "Once " + Tok(254, 1, 1) + "upon a", "Once |Own|0 upon a|Red|50");
        Expect(check, "E9 second line", "time</c> there", "time|Red|0  there|Own|40");
        NuiColorText.EndBlock();
        NuiColorText.BeginBlock();
        Expect(check, "E9 first of three lines", Tok(254, 1, 1) + "a very", "a very|Red|0");
        Expect(check, "E9 a middle line with no token", "long red", "long red|Red|0");
        Expect(check, "E9 last line", "span</c> ends", "span|Red|0  ends|Own|40");
        NuiColorText.EndBlock();
        Expect(check, "E9 the next label", "after", null);
    }

    /// <summary>Oracle: the field's text measures all of its thirteen bytes, as typed; the same text in a label
    /// measures Hi alone.</summary>
    // Given a NUI text edit field the server filled with: [red]Hi[close]
    // When the player clicks into it and moves the cursor
    // Then the field shows its text the way the stock client does, so every character the player edits is one they can see
    private static void EditField(Checker check)
    {
        string text = Tok(254, 1, 1) + "Hi</c>";
        NuiColorText.EnterEdit();
        Expect(check, "E11 in the edit field", text, null);
        float edit = NuiColorText.Width(Utf8(text), Font);
        NuiColorText.LeaveEdit();
        check.Expect(edit == 130f, $"E11 the edit field measures {edit} px, not the 130 px of all its bytes");
        Expect(check, "E11 the same text in a label", text, "Hi|Red|0");
    }

    // Given the setting Color tokens in NUI text switched off (or Enable CX NUI fixes switched off)
    // When the window from E1 opens
    // Then the label shows exactly what the stock client shows
    private static void SettingOff(Checker check)
    {
        string e1 = "Gold: " + Tok(254, 212, 1) + "1500</c> gp";
        NuiKnobs.ColorTokens.Set(false);
        Expect(check, "E12 setting off", e1, null);
        check.Expect(NuiColorText.Width(Utf8(e1), Font) == 250f, "E12 setting off: the label measures other than all its 25 bytes");
        NuiKnobs.ColorTokens.Set(true);
        NuiKnobs.Enabled.Set(false);
        Expect(check, "E12 NUI fixes off", e1, null);
        NuiKnobs.Enabled.Set(true);
        Expect(check, "E12 both on", e1, "Gold: |Own|0 1500|Gold|60  gp|Own|100");
    }

    // Given a server window with no colour tokens anywhere
    // When it opens with the setting on
    // Then every element looks and sits exactly as in the stock client
    private static void Untokened(Checker check)
    {
        Expect(check, "E13", "Plain words, no tokens", null);
        check.Expect(NuiColorText.Width(Utf8("Plain words, no tokens"), Font) == 220f, "E13 untokened text measures other than its 22 bytes");
        Expect(check, "E13 positive control", "Plain " + Tok(254, 1, 1) + "words", "Plain |Own|0 words|Red|60");
    }

    /// <summary>Lays <paramref name="text"/> out and compares it with <paramref name="want"/>: pieces as
    /// <c>text|colour|x</c> separated by one space, or null for the draw going ahead unchanged.</summary>
    private static void Expect(Checker check, string example, string text, string? want)
    {
        byte[] bytes = Utf8(text);
        Span<ColorPiece> pieces = stackalloc ColorPiece[NuiColorText.MaxPieces];
        int n = NuiColorText.Plan(bytes, Own, Font, pieces);
        string got = n < 0 ? "unchanged" : Describe(bytes, pieces[..n]);
        check.Expect(got == (want ?? "unchanged"), $"{example}: got [{got}], want [{want ?? "unchanged"}]");
    }

    private static string Describe(byte[] bytes, ReadOnlySpan<ColorPiece> pieces)
    {
        var parts = new List<string>();
        foreach (var p in pieces)
            parts.Add($"{Encoding.UTF8.GetString(bytes, p.Start, p.Length)}|{Name(p.Color)}|{p.X:0.##}");
        return string.Join(" ", parts);
    }

    private static string Name(uint c) => c switch
    {
        Own => "Own", Gold => "Gold", Red => "Red", Green => "Green", Cool => "Cool", _ => $"0x{c:X8}"
    };

    /// <summary>An open token as a script writes it, three codepage bytes, carried as the UTF-8 NUI text reaches nuklear in.</summary>
    private static string Tok(int r, int g, int b) => "<c" + (char)r + (char)g + (char)b + ">";

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static int IndexAfter(string s, string word, int from) => s.IndexOf(word, from, StringComparison.Ordinal) + word.Length;

    /// <summary>The stand-in font: ten pixels per byte.</summary>
    internal static TextMeasure Font => new(IntPtr.Zero, 12f, &TenPerByte);

    [UnmanagedCallersOnly]
    private static float TenPerByte(IntPtr userData, float height, byte* text, int length) => length * 10f;
}
