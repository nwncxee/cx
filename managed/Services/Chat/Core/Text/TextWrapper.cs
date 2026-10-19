using System.Text;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// UTF-8-aware line wrapping at an arbitrary pixel width. Pure Core (no native refs):
/// it measures with a caller-supplied per-<see cref="Rune"/> advance so the same logic
/// serves the real glyph atlas and the headless self-check. Iterates <see cref="Rune"/>
/// (not <c>char</c>) so multi-byte scripts count one advance per codepoint.
/// </summary>
public static class TextWrapper
{
    /// <summary>Half-open rune-index range [Start,End) of one wrapped line into the source rune list.</summary>
    public readonly record struct LineSpan(int Start, int End);

    /// <summary>
    /// Greedy word-wrap of a rune list at <paramref name="maxWidth"/> pixels. Breaks at spaces;
    /// hard-breaks a single word too long to fit; honours explicit <c>'\n'</c>. The breaking
    /// space / newline is consumed (not included in either line's span).
    /// </summary>
    public static List<LineSpan> WrapRunes(IReadOnlyList<Rune> runes, float maxWidth, Func<Rune, float> advanceOf)
    {
        var spans = new List<LineSpan>();
        int n = runes.Count;
        int lineStart = 0, lastSpace = -1;
        float curW = 0;
        int i = 0;
        while (i < n)
        {
            Rune r = runes[i];
            if (r.Value == '\n')
            {
                spans.Add(new LineSpan(lineStart, i));
                lineStart = i + 1; lastSpace = -1; curW = 0; i++;
                continue;
            }
            float aw = advanceOf(r);
            if (curW + aw > maxWidth && i > lineStart)
            {
                if (lastSpace > lineStart) { spans.Add(new LineSpan(lineStart, lastSpace)); lineStart = lastSpace + 1; }
                else                       { spans.Add(new LineSpan(lineStart, i));         lineStart = i; }
                lastSpace = -1; curW = 0; i = lineStart;   // reprocess the new line from its start
                continue;
            }
            curW += aw;
            if (r.Value == ' ') lastSpace = i;
            i++;
        }
        spans.Add(new LineSpan(lineStart, n));
        return spans;
    }

    /// <summary>String convenience over <see cref="WrapRunes"/>; the self-check's plumbing.</summary>
    public static List<string> Wrap(string text, float maxWidth, Func<Rune, float> advanceOf)
    {
        var runes = new List<Rune>();
        foreach (Rune r in text.EnumerateRunes()) runes.Add(r);
        var lines = new List<string>();
        foreach (LineSpan s in WrapRunes(runes, maxWidth, advanceOf))
        {
            var sb = new StringBuilder();
            for (int k = s.Start; k < s.End; k++) sb.Append(runes[k].ToString());
            lines.Add(sb.ToString());
        }
        return lines;
    }

    /// <summary>Runnable proof: at a fixed 10px per rune the wrapper must break at the space, hard-break a
    /// word too long to fit, and count a Cyrillic rune as one advance. Oracle: the three expected line lists
    /// are written out here by hand, not taken from the wrapper.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        static float A(Rune _) => 10f;   // every rune 10px -> width 60 fits 6, 45 fits 4
        var check = new Checker(log);
        Same(check, "word-wrap", Wrap("hello world foo", 60f, A), ["hello", "world", "foo"]);
        Same(check, "hard-break", Wrap("abcdefgh", 60f, A), ["abcdef", "gh"]);
        Same(check, "cyrillic-rune", Wrap("аб вг", 45f, A), ["аб", "вг"]);
        log.Info(check.Ok ? "TextWrapper.SelfCheck: passed" : "TextWrapper.SelfCheck: FAILED");
    }

    private static void Same(Checker check, string name, List<string> got, string[] want)
    {
        string g = string.Join('|', got), w = string.Join('|', want);
        check.Expect(g == w, $"{name}: got [{g}] want [{w}]");
    }
}
