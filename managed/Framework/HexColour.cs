using System;

namespace CX.Framework;

/// <summary>
/// The one parser for a colour written as text: <c>#RRGGBB</c> (opaque) or <c>#RRGGBBAA</c>, as 0xRRGGBBAA. Every server colour
/// that takes the hex form reads it here.
/// </summary>
internal static class HexColour
{
    /// <summary>True, and <paramref name="rgba"/> set, for exactly <c>#</c> and six or eight hex digits (either case). Anything else is false: no sign, space, <c>0x</c> or short form.</summary>
    public static bool TryParse(string? s, out uint rgba)
    {
        rgba = 0;
        if (s is null || s.Length is not (7 or 9) || s[0] != '#') return false;
        uint v = 0;
        for (int i = 1; i < s.Length; i++)
        {
            int d = Digit(s[i]);
            if (d < 0) return false;
            v = v << 4 | (uint)d;
        }
        rgba = s.Length == 7 ? v << 8 | 0xFF : v;
        return true;
    }

    /// <summary>The channels of <paramref name="rgba"/> as 0-1 floats, red first and alpha last, into the first four of <paramref name="into"/>.</summary>
    public static void Channels(uint rgba, Span<float> into)
    {
        for (int i = 0; i < 4; i++) into[i] = (rgba >> (24 - 8 * i) & 0xFF) / 255f;
    }

    /// <summary>The value of the hex digit <paramref name="c"/>, either case, or -1.</summary>
    internal static int Digit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>Oracle: hand-written colours and refusals, none computed from the parser.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(TryParse("#112233", out uint c) && c == 0x112233FFu, "#RRGGBB is opaque");
        check.Expect(TryParse("#11223344", out c) && c == 0x11223344u, "#RRGGBBAA keeps its alpha");
        check.Expect(TryParse("#aBcDeF", out c) && c == 0xABCDEFFFu, "digits are either case");
        check.Expect(TryParse("#000000", out c) && c == 0x000000FFu, "black is opaque, not clear");
        string[] bad = { "", "#", "#12345", "#1234567", "#123456789", "#GGGGGG", "#12 456", "#+12345", "-12345", "112233", "0x112233", " #112233" };
        foreach (string b in bad)
            check.Expect(!TryParse(b, out c) && c == 0, $"'{b}' is refused");
        check.Expect(!TryParse(null, out _), "null is refused");
        Span<float> ch = stackalloc float[4];
        Channels(0xFF000080u, ch);
        check.Expect(ch[0] == 1f && ch[1] == 0f && ch[2] == 0f && Math.Abs(ch[3] - 0.5019608f) < 1e-6f, "channels are 0-1, red first, alpha last");
        log.Info(check.Ok ? "HexColour.SelfCheck: passed" : "HexColour.SelfCheck: FAILED");
    }
}
