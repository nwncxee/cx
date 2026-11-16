using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Core.Model;
using CX.Services.Chat.Render;
using static CX.Services.Chat.ChatExamplesKit;

namespace CX.Services.Chat;

/// <summary>The typing bar's approved examples (E1-E3, E6-E9). Oracle: positions worked by hand from the kit's 40 px
/// letters and a 400 px window, whose line holds 296 px (7.4 letters) right of the channel button.</summary>
partial class ChatService
{
    static partial void TypingBarApprovedExamples(ExtenderLog log)
    {
        var check = new Checker(log, "TypingBarApprovedExamples.SelfCheck");
        var atlas = Atlas();
        const float ScreenH = 1080f, LineLeft = 98f, LineW = 296f;

        // Given a chat window dragged down to the bottom edge of the screen
        // When the player presses Enter to chat
        // Then the whole typing bar shows on screen, along the inside of the window's bottom edge
        {
            var win = Window(100, ScreenH - 300f);
            var f = Build(win, atlas, true, "abc");
            var typed = Typed(f, win);
            bool inBand = typed.Count == 3 && Caret(f, win) is Quad c && c.Y0 >= ScreenH - ChatWindow.FooterH && c.Y1 <= ScreenH;
            foreach (var q in typed) inBand &= q.Y0 >= ScreenH - ChatWindow.FooterH && q.Y1 <= ScreenH;
            check.Expect(inBand, "E1 the typed letters and the caret sit in the bar along the window's bottom edge");
            check.Expect(Bounds(f).Y1 <= Bounds(Build(win, atlas, false)).Y1, "E1 nothing of the bar draws below the window, off the screen");
        }

        // Given a chat window in the middle of the screen
        // When the player presses Enter to chat
        // Then the typing bar lies inside the window's outline and no part of it reaches outside the window
        {
            var win = Window(700, 400);
            var closed = Bounds(Build(win, atlas, false));
            var open = Bounds(Build(win, atlas, true, "abc"));
            check.Expect(open.X0 >= closed.X0 && open.Y0 >= closed.Y0 && open.X1 <= closed.X1 && open.Y1 <= closed.Y1,
                         $"E2 the open bar draws nothing outside the window ({open.Y1} vs {closed.Y1})");
            check.Expect(InputStripLayout.InEditArea(win, 900, 700 - ChatWindow.FooterH * 0.5f), "E2 the bar's line is the inside bottom row of the window");
        }

        // Given the player has typed a line wider than the typing bar
        // When they type one more word
        // Then the bar stays one row tall, the start of the line slides out at the left and the caret stays in sight
        {
            var win = Window(0, 0);
            Build(win, atlas, true, "abcdefghij");
            var f = Build(win, atlas, true, "abcdefghij kl");
            var typed = Typed(f, win);
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
            const string Long = "abcdefghijklm";   // 520 px in a 296 px line: scrolled 224 px, so h|i sits 96 px in
            Build(win, atlas, true, Long);
            int at = InputStripLayout.HitTest(win, atlas, Long, LineLeft + 96f, win.Y + win.H - ChatWindow.FooterH * 0.5f);
            check.Expect(at == 8, $"E6 a click between the visible h and i puts the caret at 8, got {at}");
        }

        // Given a long line in the typing bar and an earlier line sent
        // When the player presses Up
        // Then the earlier sent line comes back into the bar
        {
            var win = Window(0, 0);
            var edit = new EditBuffer();
            edit.LoadHistory(new[] { "earlier" });
            edit.Load("abcd efgh ijkl mnop");
            ArrowKey(edit, win, atlas, -1, false);
            check.Expect(edit.Text == "earlier", $"E7 Up on a long line brings back the earlier line, got \"{edit.Text}\"");
        }

        // Given the newest message sits at the bottom of a chat window
        // When the player presses Enter to chat
        // Then that message moves up above the typing bar and stays readable
        {
            var win = Window(0, 0);
            var store = new ChatStore();
            store.Append(new ChatLine(1u, "", 0, "", new[] { new ColorRun("pq", 1, 1, 1) }, System.DateTime.UtcNow, "pq"));
            win.Toggles &= ~(ChatToggle.Timestamp | ChatToggle.Portrait);
            var f = Build(win, atlas, true, "abc", store: store);
            var msg = Quads(f).FindAll(q => Letter(q) is 'p' or 'q');
            float barTop = win.Y + win.H - ChatWindow.FooterH;
            bool above = msg.Count == 2;
            foreach (var q in msg) above &= q.Y1 <= barTop && q.Y0 >= win.Y;
            check.Expect(above, $"E8 the newest message draws above the typing bar ({msg.Count} letters)");
        }

        // Given Input on top is ticked for a chat window
        // When the player presses Enter to chat
        // Then the typing bar lies inside the window, just under its tab bar
        {
            var win = Window(0, 500);
            win.InputOnTop = true;
            var closed = Bounds(Build(win, atlas, false));
            var f = Build(win, atlas, true, "abc");
            float top = 500f + ChatWindow.TabBarH;
            bool under = Caret(f, win) is Quad c && c.Y0 >= top && c.Y1 <= top + ChatWindow.FooterH;
            foreach (var q in Typed(f, win)) under &= q.Y0 >= top && q.Y1 <= top + ChatWindow.FooterH;
            check.Expect(under, "E9 the bar's letters and caret sit in the row under the tab bar");
            check.Expect(Bounds(f).Y0 >= closed.Y0, "E9 nothing of the bar draws above the window");
        }

        if (check.Ok) log.Info("TypingBarApprovedExamples.SelfCheck: passed");
    }
}
