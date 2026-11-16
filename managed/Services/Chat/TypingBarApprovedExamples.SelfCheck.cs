using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Core.Model;
using CX.Services.Chat.Render;
using static CX.Services.Chat.ChatExamplesKit;

namespace CX.Services.Chat;

/// <summary>The typing bar's approved examples (E1-E3, E6-E9). Oracle: rects and positions worked by hand from the
/// kit's 40 px letters, a 28 px bar and tab bar, and 400 x 300 windows with no outline, whose line holds 296 px
/// (7.4 letters) from x 98. The 13-letter line is 520 px, so at its end it is scrolled 224 px and h|i sits 96 px in.</summary>
internal static class TypingBarApprovedExamples
{
    private const float ScreenH = 1080f, LineLeft = 98f, LineW = 296f, BarH = 28f;
    private const string Long = "abcdefghijklm";

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TypingBarApprovedExamples.SelfCheck");
        var atlas = Atlas();

        // Given a chat window dragged down to the bottom edge of the screen
        // When the player presses Enter to chat
        // Then the whole typing bar shows on screen, along the inside of the window's bottom edge
        {
            var win = Window(100, ScreenH - 300f);
            win.AutoHideFrame = true;
            var f = Build(win, atlas, true, "abc");
            var typed = Typed(f);
            bool inBand = typed.Count == 3 && Caret(f, win) is Quad c && c.Y0 >= ScreenH - BarH && c.Y1 <= ScreenH;
            foreach (var q in typed) inBand &= q.Y0 >= ScreenH - BarH && q.Y1 <= ScreenH;
            check.Expect(inBand, "E1 the typed letters and the caret sit in the bar along the window's bottom edge");
            check.Expect(Bounds(f).Y1 <= ScreenH, "E1 nothing of the bar draws below the window, off the screen");
        }

        // Given a chat window in the middle of the screen
        // When the player presses Enter to chat
        // Then the typing bar lies inside the window's outline and no part of it reaches outside the window
        {
            var win = Window(700, 400);
            win.AutoHideFrame = true;
            var open = Bounds(Build(win, atlas, true, "abc"));
            check.Expect(open.X0 >= 700f && open.Y0 >= 400f && open.X1 <= 1100f && open.Y1 <= 700f,
                         $"E2 the open bar draws nothing outside the window's 700,400-1100,700 ({open.X0},{open.Y0}-{open.X1},{open.Y1})");
            check.Expect(InputStripLayout.InEditArea(win, 900, 700 - BarH * 0.5f), "E2 the bar's line is the inside bottom row of the window");
        }

        // Given the player has typed a line wider than the typing bar
        // When they type one more word
        // Then the bar stays one row tall, the start of the line slides out at the left and the caret stays in sight
        {
            var win = Window(0, 0);
            Build(win, atlas, true, "abcdefghij");
            var f = Build(win, atlas, true, "abcdefghij kl");
            var typed = Typed(f);
            bool oneRow = typed.Count > 0, inside = true, noA = true;
            foreach (var q in typed)
            {
                oneRow &= q.Y0 == typed[0].Y0;
                inside &= q.X0 >= LineLeft && q.X1 <= LineLeft + LineW;
                noA &= Letter(q) != 'a';
            }
            check.Expect(oneRow, "E3 the long line stays on one row");
            check.Expect(noA && inside, "E3 the start of the line is out of sight at the left, nothing drawn past the bar's ends");
            check.Expect(Caret(f, win) is Quad c && c.X0 >= LineLeft && c.X1 <= LineLeft + LineW + 2f, "E3 the caret stays in sight");
        }

        // Given a long line scrolled so its start is out of sight
        // When the player clicks between two letters they can see
        // Then the caret lands between those two letters
        {
            var win = Window(0, 0);
            Build(win, atlas, true, Long);
            int at = InputStripLayout.HitTest(win, atlas, Long, LineLeft + 96f);
            check.Expect(at == 8, $"E6 a click between the visible h and i puts the caret at 8, got {at}");
        }

        // Given a long line in the typing bar and an earlier line sent
        // When the player presses Up
        // Then the earlier sent line comes back into the bar
        {
            var edit = new EditBuffer();
            const string Earlier = "abcd efgh ijkl mnop sent";
            edit.LoadHistory(new[] { Earlier });
            edit.Load("abcd efgh ijkl mnop");
            ChatService.ArrowKey(edit, -1);
            check.Expect(edit.Text == Earlier, $"E7 Up on a long line brings back the earlier line (it starts with the typed one, as chat.histsearch asks), got \"{edit.Text}\"");
        }

        // Given the newest message sits at the bottom of a chat window
        // When the player presses Enter to chat
        // Then that message moves up above the typing bar and stays readable
        {
            var win = Window(0, 0);
            var store = new ChatStore();
            store.Append(new ChatLine(0x400u, "", 0, "", new[] { new ColorRun("pq", 1, 1, 1) }, System.DateTime.UtcNow, "pq"));
            win.Toggles &= ~(ChatToggle.Timestamp | ChatToggle.Portrait);
            var f = Build(win, atlas, true, "abc", store: store);
            var msg = Quads(f).FindAll(q => Letter(q) is 'p' or 'q');
            bool above = msg.Count == 2;
            foreach (var q in msg) above &= q.Y1 <= 300f - BarH && q.Y0 >= 0f;
            check.Expect(above, $"E8 the newest message draws above the typing bar ({msg.Count} letters)");
        }

        // Given Input on top is ticked for a chat window
        // When the player presses Enter to chat
        // Then the typing bar lies inside the window, just under its tab bar
        {
            var win = Window(0, 500);
            win.InputOnTop = true;
            win.AutoHideFrame = true;
            var f = Build(win, atlas, true, "abc");
            const float Top = 500f + 28f;
            bool under = Caret(f, win) is Quad c && c.Y0 >= Top && c.Y1 <= Top + BarH;
            foreach (var q in Typed(f)) under &= q.Y0 >= Top && q.Y1 <= Top + BarH;
            check.Expect(under, "E9 the bar's letters and caret sit in the row under the tab bar");
            check.Expect(Bounds(f).Y0 >= 500f, "E9 nothing of the bar draws above the window");
        }

        if (check.Ok) log.Info("TypingBarApprovedExamples.SelfCheck: passed");
    }
}
