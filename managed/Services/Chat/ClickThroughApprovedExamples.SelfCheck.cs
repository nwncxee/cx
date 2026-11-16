using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;
using static CX.Services.Chat.ChatExamplesKit;

namespace CX.Services.Chat;

/// <summary>Click-through windows: approved examples E14-E16 and E18-E21. Oracle: window rects written by hand, a click
/// and wheel at 700,650 inside the 500,500-900,800 window, and its tab bar as the full-width 28 px band at its top. E14
/// draws the worst case, Auto-hide header and outline both off; E20 saves Main as window 0, as the shell does.</summary>
internal static class ClickThroughApprovedExamples
{
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ClickThroughApprovedExamples.SelfCheck");
        var atlas = Atlas();

        // Given a chat window set to click-through, its messages faded out
        // When the player moves the pointer over it
        // Then no tab bar, outline, gear, resize grip or scrollbar shows and no faded message comes back
        {
            var layout = new ChatLayout(Window(0, 0));
            var win = layout.AddWindow(Window(500, 500))!;
            win.ClickThrough = true;
            win.AutoHideHeader = false; win.AutoHideFrame = false;
            win.ContentH = 2000f; win.ViewH = 270f;
            System.DateTime faded = win.LastScroll;
            check.Expect(layout.WindowAt(700, 650) != 1, "E14 the pointer over a click-through window is not over that window");
            check.Expect(BackgroundOnly(Build(win, atlas, false, hover: true), 500, 500, 400, 300),
                         "E14 hovering draws only the background: no tab bar, outline, gear, grip or scrollbar");
            ChatService.Wheel(layout, 700, 650, 1, _ => 20f, out _);
            check.Expect(win.LastScroll == faded, "E14 nothing over it resets its fade clock, so faded messages stay faded");
        }

        // Given a creature standing behind a click-through chat window
        // When the player clicks the creature through the window
        // Then the creature is selected as if the window were not there
        {
            var layout = new ChatLayout(Window(0, 0));
            var win = layout.AddWindow(Window(500, 500))!;
            win.ClickThrough = true;
            layout.FocusedIndex = 1;
            check.Expect(layout.WindowAt(700, 650) == -1 && !win.HitTestFull(700, 650), "E15 the click is over no chat window");
            check.Expect(!win.MouseDown(700, 650), "E15 the window does not keep the press, so the game gets it");
            win.MouseUp(700, 650);
        }

        // Given a click-through chat window with history to scroll
        // When the player turns the mouse wheel over it
        // Then the camera zooms and the window's messages do not move
        {
            var layout = new ChatLayout(Window(0, 0));
            var win = layout.AddWindow(Window(500, 500))!;
            win.ClickThrough = true;
            win.ContentH = 2000f; win.ViewH = 270f;
            int hit = ChatService.Wheel(layout, 700, 650, 1, _ => 20f, out float moved);
            check.Expect(hit == -1, "E16 the wheel over a click-through window belongs to the game");
            check.Expect(moved == 0f && win.Active.ScrollPx == 0f, "E16 the window's messages do not move");
        }

        // Given a click-through chat window with a tell tab that is not showing
        // When a tell arrives on that tab
        // Then the window's tab bar stays hidden
        {
            var win = new ChatWindow(500, 500, 400, 300, new[] { new Tab("DM", 0x400), new Tab("ANA", 0x002) { PinnedName = "Ana" } })
                      { ClickThrough = true };
            win.Tabs[1].Unread = true;
            check.Expect(!TabBar(Build(win, atlas, false), 500, 500), "E18 an unread tell tab leaves the tab bar hidden");
        }

        // Given a window that can send on Talk, clicked last and then set to click-through
        // When the player presses Enter
        // Then the typing bar opens in the Main window
        {
            var layout = new ChatLayout(Window(0, 0));
            var win = layout.AddWindow(new ChatWindow(500, 0, 400, 300, new[] { new Tab("Talk", 0x001) }))!;
            layout.FocusedIndex = 1;
            win.ClickThrough = true;
            check.Expect(layout.TypingWindow() == 0, $"E19 Enter opens in Main, never the click-through window (got {layout.TypingWindow()})");
        }

        // Given a chat window set to click-through
        // When the player quits and starts the game again
        // Then that window is still click-through
        {
            static bool Restarted(bool clickThrough)
            {
                var win = Window(500, 0);
                win.ClickThrough = clickThrough;
                var saved = new ChatConfig();
                saved.Windows.Add(WindowConfig.Capture(Window(0, 0)));
                saved.Windows.Add(WindowConfig.Capture(win));
                var again = Window(500, 0);
                ChatConfig.From(saved.Pairs()).Windows[1].ApplyTo(again);
                return again.ClickThrough;
            }
            check.Expect(Restarted(true), "E20 a click-through window is click-through after a restart");
            check.Expect(!Restarted(false), "E20 an ordinary window stays ordinary after a restart");
        }

        // Given a click-through chat window
        // When the player unticks Click-through for it from the Main window's gear
        // Then pointing at it shows its tab bar again and a click on it lands on the window
        {
            var main = Window(0, 0);
            var layout = new ChatLayout(main);
            var win = layout.AddWindow(Window(500, 500))!;
            win.ClickThrough = true;
            var s = new SettingsWindow();
            var face = PanelFace();
            s.OpenAt(main, 1280, 800, 20f, face, face, 0, layout.Windows);
            PickWindow(s, main, 1);
            var row = s.Rows.Find(r => r.Act == SetAct.ClickThroughOf && r.Key == "1");
            check.Expect(row != null, "E21 Main's gear reaches window 2's Click-through through its window list");
            if (row != null) PressRow(s, win, row);
            s.Reset();
            check.Expect(!win.ClickThrough && layout.WindowAt(700, 650) == 1, "E21 unticked, the pointer over it is over the window again");
            check.Expect(TabBar(Build(win, atlas, false, hover: true), 500, 500), "E21 unticked, hovering shows its tab bar");
            check.Expect(win.MouseDown(700, 650), "E21 unticked, a click lands on the window");
            win.MouseUp(700, 650);
        }

        if (check.Ok) log.Info("ClickThroughApprovedExamples.SelfCheck: passed");
    }
}
