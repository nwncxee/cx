using System.Text;
using CX.Framework;

namespace CX.Services.Chat.Core;

/// <summary>
/// Parses the Aurora inline color grammar into <see cref="ColorRun"/>s once at ingest.
/// Grammar (live-confirmed): <c>&lt;c</c> + 3 raw RGB bytes + <c>&gt;</c> opens a color;
/// <c>&lt;/c&gt;</c> closes. Colors nest on a fixed stack; channel value = byte / 255.
/// Input is a Latin1-decoded string, so each char's low byte is the original raw byte.
/// </summary>
public static class ColorRunParser
{
    /// <summary>Aurora nests colors this deep. ponytail: deeper opens are ignored (push+color skipped
    /// together so pops stay paired); lift by growing the stack if a real line ever exceeds it.</summary>
    internal const int MaxDepth = 16;

    private const float DefaultR = 1f, DefaultG = 1f, DefaultB = 1f;

    /// <summary>Parse <paramref name="s"/> into color runs. Text outside any tag gets the default color. Engine text
    /// (<paramref name="engine"/>) has each run decoded for display by <see cref="Native.EngineText"/>.</summary>
    public static ColorRun[] Parse(string s, bool engine = true)
    {
        if (string.IsNullOrEmpty(s)) return System.Array.Empty<ColorRun>();

        var runs = new List<ColorRun>(4);
        var sb = new StringBuilder(s.Length);
        Span<(float r, float g, float b)> stack = stackalloc (float, float, float)[MaxDepth];
        int depth = 0;
        float cr = DefaultR, cg = DefaultG, cb = DefaultB;

        void Flush()
        {
            if (sb.Length == 0) return;
            runs.Add(new ColorRun(engine ? Native.EngineText(sb.ToString()) : sb.ToString(), cr, cg, cb));
            sb.Clear();
        }

        int i = 0, len = s.Length;
        while (i < len)
        {
            // open: '<' 'c' r g b '>'  (6 chars, RGB bytes are opaque -- match positionally, not by '>')
            if (i + 5 < len && s[i] == '<' && s[i + 1] == 'c' && s[i + 5] == '>')
            {
                Flush();
                float r = s[i + 2] / 255f, g = s[i + 3] / 255f, b = s[i + 4] / 255f;
                if (depth < MaxDepth) { stack[depth++] = (r, g, b); cr = r; cg = g; cb = b; }
                i += 6;
            }
            else if (i + 3 < len && s[i] == '<' && s[i + 1] == '/' && s[i + 2] == 'c' && s[i + 3] == '>')
            {
                Flush();
                if (depth > 0) depth--;
                (cr, cg, cb) = depth > 0 ? stack[depth - 1] : (DefaultR, DefaultG, DefaultB);
                i += 4;
            }
            else { sb.Append(s[i]); i++; }
        }
        Flush();
        return runs.ToArray();
    }

    /// <summary>Runnable self-check: parses the spec example bytes + a nested case and asserts runs/RGB.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;

        // spec example: 8f 7f ff colors "hi"
        string sample = "pre<c" + (char)0x8f + (char)0x7f + (char)0xff + ">hi</c>post";
        var r = Parse(sample);
        ok &= r.Length == 3;
        ok &= r[0].Text == "pre" && r[0].R == 1f;
        ok &= r[1].Text == "hi"
              && r[1].R == 0x8f / 255f && r[1].G == 0x7f / 255f && r[1].B == 0xff / 255f;
        ok &= r[2].Text == "post" && r[2].R == 1f;

        // nested: outer 10/20/30, inner 40/50/60 -- closing inner must restore outer
        string nested = "<c" + (char)0x10 + (char)0x20 + (char)0x30 + ">A"
                      + "<c" + (char)0x40 + (char)0x50 + (char)0x60 + ">B</c>C</c>D";
        var n = Parse(nested);
        ok &= n.Length == 4;
        ok &= n[0].Text == "A" && n[0].R == 0x10 / 255f && n[0].G == 0x20 / 255f && n[0].B == 0x30 / 255f;
        ok &= n[1].Text == "B" && n[1].R == 0x40 / 255f && n[1].G == 0x50 / 255f && n[1].B == 0x60 / 255f;
        ok &= n[2].Text == "C" && n[2].R == 0x10 / 255f;   // popped back to outer
        ok &= n[3].Text == "D" && n[3].R == 1f;            // popped back to default

        log.Info(ok ? "ColorRunParser.SelfCheck: passed" : "ColorRunParser.SelfCheck: FAILED");
    }
}
