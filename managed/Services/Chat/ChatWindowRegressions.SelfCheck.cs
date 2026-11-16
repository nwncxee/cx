using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Core.Model;
using CX.Ui;
using static CX.Services.Chat.ChatExamplesKit;

namespace CX.Services.Chat;

/// <summary>Approved examples E12, E17, E22-E25, E33, E35 and E36 of where typing goes, click-through windows and the
/// window list, kept as regressions, a tab drop refused by a click-through window, and a window's gear holding no other window's click-through
/// (the window list reaches those). Oracle: window rects and channel bits written by hand.</summary>
internal static class ChatWindowRegressions
{
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "ChatWindowRegressions.SelfCheck");
        var atlas = Atlas();

        // Given the typing bar is open in Main
        // When the player clicks a tab in a second window that can send on Party
        // Then the typing bar moves to that window and the line goes out on Party
        {
            var layout = new ChatLayout(Window(0, 0));
            var second = layout.AddWindow(new ChatWindow(500, 0, 400, 300, new[] { new Tab("Talk", 0x001), new Tab("Party", 0x040) }))!;
            layout.FocusedIndex = layout.TypingWindow();
            check.Expect(layout.FocusedIndex == 0, "E12 the bar is open in Main first");
            float tabX = 500f + second.TabW * 1.5f, tabY = 2f;
            int hit = ChatService.FocusAt(layout, tabX, tabY);
            if (hit >= 0) layout.Windows[hit].MouseDown(tabX, tabY);
            second.MouseUp(tabX, tabY);
            check.Expect(layout.Focused == second && second.SendChannel == Channel.Party,
                         $"E12 a click on the Party tab of a second window moves typing there on Party (window {layout.FocusedIndex}, {second.SendChannel})");
        }

        // Given a click-through chat window whose tab shows combat messages
        // When a combat message arrives
        // Then it shows in the window and fades on the window's own timings
        {
            var win = new ChatWindow(0, 0, 400, 300, new[] { new Tab("Combat", 0xffffffff) }) { ClickThrough = true, HoldSec = 5f, FadeSec = 2f };
            win.Toggles &= ~(ChatToggle.Timestamp | ChatToggle.Portrait);
            var store = new ChatStore();
            store.Append(new ChatLine(1u, "", 0, "", new[] { new ColorRun("pq", 1, 1, 1) }, System.DateTime.UtcNow, "pq"));
            var f = Build(win, atlas, false, store: store);
            check.Expect(Quads(f).FindAll(q => Letter(q) is 'p' or 'q').Count == 2 && f.BodyEnd > f.BodyStart,
                         "E17 a click-through window still draws its new message in the fading body");
            check.Expect(f.HoldSec == 5.0 && f.FadeSec == 2.0 && f.AnimEnabled, "E17 the frame fades on the window's own 5 s hold and 2 s fade");
        }

        // Given the player opens the Main window's gear
        // When they read the window's options
        // Then there is no Click-through option for Main
        {
            var main = Window(0, 0);
            var layout = new ChatLayout(main);
            layout.AddWindow(Window(500, 0));
            var s = new SettingsWindow();
            s.OpenAt(main, 1280, 800, 20f, PanelFace(), PanelFace(), 0, layout.Windows);
            check.Expect(s.Rows.Find(r => r.Act == SetAct.ClickThrough) == null,
                         "E22 Main's gear offers no Click-through for Main");
            s.Reset();
        }

        // Given a chat window that is not click-through, with Auto-hide header ticked
        // When the player moves the pointer over it
        // Then its tab bar shows
        {
            var layout = new ChatLayout(Window(0, 0));
            var win = layout.AddWindow(Window(500, 0))!;
            win.AutoHideHeader = true;
            check.Expect(layout.WindowAt(700, 150) == 1, "E23 the pointer over an ordinary window is over that window");
            check.Expect(TabBar(Build(win, atlas, false, hover: true), 500, 0), "E23 hovering draws its tab bar");
            win.ContentH = 2000f; win.ViewH = 270f;
            System.DateTime before = win.LastScroll;
            check.Expect(ChatService.Wheel(layout, 700, 150, 1, _ => 20f, out float moved) == 1 && moved == 60f && win.LastScroll > before,
                         "the wheel over an ordinary window scrolls it 3 lines of 20 px and resets its fade clock");
        }

        // Given a click-through chat window
        // When the player copies it
        // Then the copy is not click-through, so it can be moved and placed
        {
            var src = Window(0, 0);
            src.ClickThrough = true;
            var copy = Window(30, 30);
            copy.CopySettingsFrom(src);
            check.Expect(!copy.ClickThrough && copy.TakesPointer(100, 100), "E24 a copy of a click-through window takes the pointer");
        }

        {
            var layout = new ChatLayout(Window(0, 0));
            var second = layout.AddWindow(Window(500, 0))!;
            layout.AddWindow(Window(500, 400));
            var s = new SettingsWindow();
            s.OpenAt(second, 1280, 800, 20f, PanelFace(), PanelFace(), 1, layout.Windows);
            check.Expect(s.Rows.FindAll(r => r.Act == SetAct.ClickThrough).Count == 1,
                         "a second window's gear holds only its own Click-through, not the other windows'");
            s.Reset();
        }

        // Given Main's only tab shows Combat and a second window clicked last can send on Talk
        // When the player presses Enter
        // Then the typing bar opens in the second window
        {
            var layout = new ChatLayout(new ChatWindow(0, 0, 400, 300, new[] { new Tab("COMBAT", 0x080) }));
            layout.AddWindow(new ChatWindow(500, 0, 400, 300, new[] { new Tab("TALK", 0x001) }));
            layout.FocusedIndex = 1;
            check.Expect(layout.TypingWindow() == 1, $"E25 Main unable to send: Enter opens in the second window (got {layout.TypingWindow()})");
        }

        // Given Main is the only chat window
        // When the player opens Main's gear
        // Then the panel shows no window list
        {
            var main = Window(0, 0);
            var s = new SettingsWindow();
            s.OpenAt(main, 1280, 800, 20f, PanelFace(), PanelFace(), 0, new[] { main });
            check.Expect(s.Panel.HeaderChoices.Length == 0, "E33 a lone Main has no window list");
            s.Reset();
        }

        {
            var win = Window(500, 0);
            int ordinary = win.TabBarDropIndex(510, 10);
            win.ClickThrough = true;
            check.Expect(ordinary == 0 && win.TabBarDropIndex(510, 10) == -1, "a tab dropped on a click-through window's hidden tab bar does not land there");
        }

        // Given the typing bar open in Main and Main's settings panel open
        // When the player picks Window 2 in the window list
        // Then the typing bar closes and the next setting the player changes goes to Window 2
        {
            var main = Window(0, 0);
            var layout = new ChatLayout(main);
            var second = layout.AddWindow(Window(500, 0))!;
            var s = new SettingsWindow();
            s.OpenAt(main, 1280, 800, 20f, PanelFace(), PanelFace(), 0, layout.Windows);
            PickWindow(s, main, 1);
            bool followed = ChatService.FollowPick(layout, s);
            var lockRow = s.Rows.Find(r => r.Act == SetAct.LockWin);
            if (lockRow != null) PressRow(s, layout.Focused, lockRow);
            s.Reset();
            check.Expect(followed && layout.Focused == second && second.Locked && !main.Locked,
                         "E35 a pick focuses Window 2 and the next setting goes to it (the bar closing is the shell's, on the hand check)");
        }

        // Given two chat windows
        // When the player opens Main's gear
        // Then there is no Window 2 click-through row; Window 2's Click-through is only on Window 2's own page
        {
            var main = Window(0, 0);
            var layout = new ChatLayout(main);
            var second = layout.AddWindow(Window(500, 0))!;
            var s = new SettingsWindow();
            s.OpenAt(main, 1280, 800, 20f, PanelFace(), PanelFace(), 0, layout.Windows);
            bool onMain = s.Rows.Exists(r => r.Act == SetAct.ClickThrough);
            s.Reset();
            s.OpenAt(second, 1280, 800, 20f, PanelFace(), PanelFace(), 1, layout.Windows);
            bool onOwn = s.Rows.Exists(r => r.Act == SetAct.ClickThrough);
            s.Reset();
            check.Expect(!onMain && onOwn, "E36 Main's gear has no click-through row; Window 2's own page has it");
        }

        if (check.Ok) log.Info("ChatWindowRegressions.SelfCheck: passed");
    }
}
