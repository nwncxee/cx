using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Core.Model;
using CX.Ui;
using static CX.Services.Chat.ChatExamplesKit;
using Row = CX.Ui.KnobPanel.Row;

namespace CX.Services.Chat;

/// <summary>Approved examples of where typing goes and of click-through windows that already held before either
/// changed (E12, E17, E22, E23, E24), kept as regressions. Oracle: window rects and channel bits written by hand.</summary>
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
            float tabX = 500f + second.TabW * 1.5f, tabY = 2f;
            int hit = layout.WindowAt(tabX, tabY);
            if (hit >= 0 && layout.Windows[hit].MouseDown(tabX, tabY)) layout.FocusedIndex = hit;
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
            float now = ChatAnim.FadeAlpha(System.DateTime.UtcNow, System.DateTime.UtcNow.AddSeconds(-6), System.DateTime.MinValue, true, win.HoldSec, win.FadeSec);
            check.Expect(System.MathF.Abs(now - 0.5f) < 0.01f, "E17 six seconds on, a 5 s hold and 2 s fade leave it half faded");
        }

        // Given the player opens the Main window's gear
        // When they read the window's options
        // Then there is no Click-through option for Main
        {
            var main = Window(0, 0);
            var layout = new ChatLayout(main);
            layout.AddWindow(Window(500, 0));
            var s = new SettingsWindow();
            s.OpenAt(main, 1280, 800, 20f, PanelFace(), PanelFace(), 0, 2, layout.Windows);
            check.Expect(s.Rows.Find(r => r.Act == SetAct.ClickThrough || (r.Act == SetAct.ClickThroughOf && r.Key == "0")) == null,
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
            check.Expect(Build(win, atlas, false, hover: true).Count > Build(win, atlas, false).Count, "E23 hovering draws its tab bar");
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

        if (check.Ok) log.Info("ChatWindowRegressions.SelfCheck: passed");
    }

    /// <summary>A panel face with every printable glyph, so the settings rows lay out.</summary>
    internal static GlyphAtlas PanelFace()
    {
        var a = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        for (int cp = 0x20; cp < 0x180; cp++) a.SetTestGlyph(cp, cp == ' ' ? 6f : 11f);
        a.SetTestGlyph(0x25BE, 11f);
        return a;
    }

    /// <summary>Press a settings row the way a player clicks it: scrolled into the list, then on its label.</summary>
    internal static void PressRow(SettingsWindow s, ChatWindow w, Row r)
    {
        s.Panel.List.Offset = r.Top - 0.4f * s.Panel.Em;
        s.Panel.List.Clamp();
        s.MouseDown(s.Panel.List.X + 5, s.Panel.RowTop(r) + 2, w);
        s.MouseUp();
    }
}
