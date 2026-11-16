using CX.Framework;
using CX.Services.Chat.Core;
using static CX.Services.Chat.ChatExamplesKit;

namespace CX.Services.Chat;

/// <summary>Approved examples of the typing bar that already held before the bar moved inside the window (E4, E5,
/// E10), kept as regressions. Oracle: positions worked by hand from the kit's 40 px letters and a 400 px window, whose
/// line starts at x 98 (the 92 px channel button plus 6 px of padding, at scale 1).</summary>
internal static class TypingBarRegressions
{
    private const float LeftOfLine = 98f;

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TypingBarRegressions.SelfCheck");
        var atlas = Atlas();

        // Given a line that fits inside the typing bar
        // When the player types another letter
        // Then the line still starts at the left of the bar, just right of the channel button
        {
            var win = Window(0, 0);
            Build(win, atlas, true, "abc");
            var typed = Typed(Build(win, atlas, true, "abcd"));
            check.Expect(typed.Count == 4 && typed[0].X0 == LeftOfLine && Letter(typed[0]) == 'a',
                         $"E4 a short line starts just right of the channel button (first letter at {(typed.Count > 0 ? typed[0].X0 : -1)})");
        }

        // Given a long line scrolled so its start is out of sight
        // When the player presses Home
        // Then the start of the line shows again with the caret in front of it
        {
            var win = Window(0, 0);
            var edit = new EditBuffer();
            edit.Load("abcdefghijklm");
            Build(win, atlas, true, edit.Text, edit.Cursor);
            edit.Home(false);
            var home = Build(win, atlas, true, edit.Text, edit.Cursor);
            var typed = Typed(home);
            var caret = Caret(home, win);
            check.Expect(typed.Count > 0 && Letter(typed[0]) == 'a' && typed[0].X0 == LeftOfLine, "E5 Home shows the first letter at the left of the bar");
            check.Expect(caret is Quad c && c.X0 == LeftOfLine, "E5 Home puts the caret in front of the first letter");
        }

        // Given the character counter is switched on and the line is wider than the typing bar
        // When the player types one more letter
        // Then the counter still shows at the bar's right end and no text draws over it
        {
            var win = Window(0, 0);
            Build(win, atlas, true, "abcdefghijkl", counter: true);
            var f = Build(win, atlas, true, "abcdefghijklm", counter: true);
            var digits = Quads(f).FindAll(q => q.Glyph && System.MathF.Abs(q.W - 5f) < 1e-3f);
            float counterLeft = float.MaxValue;
            foreach (var d in digits) if (d.X0 < counterLeft) counterLeft = d.X0;
            bool clear = digits.Count == 2;
            foreach (var t in Typed(f)) clear &= t.X1 <= counterLeft;
            check.Expect(clear && counterLeft > win.X + win.W / 2f, $"E10 the counter (13) shows at the right end with no letter over it ({digits.Count} digits)");
        }

        if (check.Ok) log.Info("TypingBarRegressions.SelfCheck: passed");
    }
}
