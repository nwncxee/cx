using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;
using static CX.Services.Chat.ChatExamplesKit;

namespace CX.Services.Chat;

/// <summary>The settings panel's window list: approved examples E30-E32 and E34. Oracle: the window names and the windows each
/// panel edits, written by hand; a pick is the shell's focus move, which every later press then edits.</summary>
internal static class WindowPickerApprovedExamples
{
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "WindowPickerApprovedExamples.SelfCheck");
        var face = PanelFace();

        // Given two chat windows and the settings panel open for Main
        // When the player picks Window 2 in the window list left of Copy this window
        // Then the panel shows Window 2's settings, titled Window 2
        {
            var main = Window(0, 0);
            var layout = new ChatLayout(main);
            var second = layout.AddWindow(Window(500, 0))!;
            second.Locked = true;
            var s = new SettingsWindow();
            s.OpenAt(main, 1280, 800, 20f, face, face, 0, layout.Windows);
            s.Panel.HeaderChoiceRect(out float lx0, out _, out _, out _);
            s.Panel.HeaderButtonRect(out float bx0, out _, out _, out _);
            PickWindow(s, main, 1);
            var lockRow = s.Rows.Find(r => r.Act == SetAct.LockWin);
            check.Expect(lx0 < bx0 && s.Panel.Title == "Window 2" && s.WindowIndex == 1 && s.WindowPicked == 1,
                         $"E30 the list left of Copy this window switches the panel to Window 2 (title \"{s.Panel.Title}\")");
            check.Expect(lockRow != null && lockRow.On, "E30 the panel shows Window 2's own settings: its Lock window is on");
            s.Reset();
        }

        // Given Window 2 is click-through and the settings panel is open for Main
        // When the player picks Window 2 in the window list and unticks Click-through
        // Then pointing at Window 2 shows its tab bar again and a click on it lands on the window
        {
            var main = Window(0, 0);
            var layout = new ChatLayout(main);
            var second = layout.AddWindow(Window(500, 500))!;
            second.ClickThrough = true;
            var s = new SettingsWindow();
            s.OpenAt(main, 1280, 800, 20f, face, face, 0, layout.Windows);
            PickWindow(s, main, 1);
            var row = s.Rows.Find(r => r.Act == SetAct.ClickThroughOf);
            if (row != null) PressRow(s, second, row);
            s.Reset();
            check.Expect(row != null && !second.ClickThrough && layout.WindowAt(700, 650) == 1, "E31 unticked from Main's list, Window 2 takes the pointer again");
            check.Expect(TabBar(Build(second, Atlas(), false, hover: true), 500, 500, 400), "E31 pointing at it shows its tab bar");
            check.Expect(second.MouseDown(700, 650), "E31 a click on it lands on the window");
            second.MouseUp(700, 650);
        }

        // Given the settings panel switched from Main to Window 2 through the window list
        // When the player ticks Lock window
        // Then Window 2 is locked and Main is not
        {
            var main = Window(0, 0);
            var layout = new ChatLayout(main);
            var second = layout.AddWindow(Window(500, 0))!;
            var s = new SettingsWindow();
            s.OpenAt(main, 1280, 800, 20f, face, face, 0, layout.Windows);
            PickWindow(s, main, 1);
            layout.FocusedIndex = s.WindowPicked;
            var lockRow = s.Rows.Find(r => r.Act == SetAct.LockWin);
            if (lockRow != null) PressRow(s, layout.Focused, lockRow);
            s.Reset();
            check.Expect(second.Locked && !main.Locked, "E32 Lock window after the switch locks Window 2, not Main");
        }

        // Given three chat windows and the settings panel open for Window 3
        // When the player opens the window list
        // Then it offers Main, Window 2 and Window 3
        {
            var layout = new ChatLayout(Window(0, 0));
            layout.AddWindow(Window(500, 0));
            var third = layout.AddWindow(Window(500, 400))!;
            var s = new SettingsWindow();
            s.OpenAt(third, 1280, 800, 20f, face, face, 2, layout.Windows);
            OpenWindowList(s, third);
            string[] offered = s.Panel.Drop.Open ? s.Panel.Drop.Choices : System.Array.Empty<string>();
            check.Expect(offered.Length == 3 && offered[0] == "Main" && offered[1] == "Window 2" && offered[2] == "Window 3",
                         $"E34 Window 3's list offers Main, Window 2 and Window 3 ({string.Join(", ", offered)})");
            s.Reset();
        }

        if (check.Ok) log.Info("WindowPickerApprovedExamples.SelfCheck: passed");
    }
}

partial class ChatService
{
    /// <summary>Runs <see cref="WindowPickerApprovedExamples"/> from the chat self-check list.</summary>
    static partial void WindowPickerExamples(ExtenderLog log) => WindowPickerApprovedExamples.SelfCheck(log);
}
