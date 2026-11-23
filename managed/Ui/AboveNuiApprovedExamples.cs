using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Core;
using CX.Services.Dialog;
using CX.Services.Inventory;
using CX.Services.Quickbar;
using static CX.Services.Chat.ChatExamplesKit;

namespace CX.Ui;

/// <summary>Draw above NUI: approved examples E12-E16 and E33-E38. Oracle: the frame's letters (N for NUI) and the
/// window each press and key should reach, written by hand; the windows are the services' own, from their factories.</summary>
internal static class AboveNuiApprovedExamples
{
    /// <summary>The setting's name inside its group, and inside a chat window's <c>w&lt;N&gt;.</c> keys.</summary>
    private const string Setting = "abovenui";

    /// <summary>The stand-in window's square, (100, 100) to (500, 500), which the examples' NUI window overlaps.</summary>
    private static bool InSquare(float px, float py) => px >= 100 && px < 500 && py >= 100 && py < 500;

    /// <summary>SDL's digit 5, the key the examples press.</summary>
    private const uint KeyFive = 0x35;   // not-an-offset: SDLK_5

    /// <summary>A setting's knob, the player's value of it and its line in the loaded table, taken before an example
    /// changes any of them so <see cref="Restore"/> can put back exactly that.</summary>
    private readonly struct Held(string key, Knob? knob, string? stored, string? raw)
    {
        internal Knob? Knob => knob;

        internal static Held Of(string key)
        {
            Knob? k = KnobGroup.Find(key);
            return new Held(key, k, k?.FormatStored(), Settings.Raw(key));
        }

        internal void Restore()
        {
            if (stored != null) knob?.Parse(stored);
            if (raw != null) Settings.Read(key + "=" + raw + "\n");
            else Settings.Forget(key);
        }
    }

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AboveNuiApprovedExamples.SelfCheck");
        ChatExamples(check);
        InventoryExamples(check);
        DialogAndQuickbar(check);
        if (check.Ok) log.Info("AboveNuiApprovedExamples.SelfCheck: passed");
    }

    private static string KeyOf(KnobGroup g) => g.Name + "." + Setting;

    private static string ChatKey(int n) => "w" + n.ToString(CultureInfo.InvariantCulture) + "." + Setting;

    private static bool Over(string f, char c) => f.IndexOf(c, StringComparison.Ordinal) > f.IndexOf('N', StringComparison.Ordinal);

    private static bool Under(string f, char c)
        => f.IndexOf(c, StringComparison.Ordinal) >= 0 && f.IndexOf(c, StringComparison.Ordinal) < f.IndexOf('N', StringComparison.Ordinal);

    /// <summary>The window a left press at the point lands on when the input pump routes it with a NUI window under
    /// the pointer, or null when the press goes to NUI.</summary>
    private static Window? TakenBeforeNui(int x, int y)
    {
        Window? hit = UiHost.HitTest(x, y), took = null;
        if (hit == null) return null;
        Func<bool, int, int, int, bool>? was = hit.OnMouseButton;
        hit.OnMouseButton = (down, _, _, _) => { if (down) took = hit; return true; };
        try
        {
            UiInputService.RoutePress(UiInputService.SdlButtonLeft, x, y, new UiInputService.PointerUnder(true, false, false));
            UiHost.DispatchMouseButton(false, UiInputService.SdlButtonLeft, x, y);
        }
        finally { hit.OnMouseButton = was; }
        return took;
    }

    /// <summary>The setting <paramref name="key"/> held for the example and set to <paramref name="value"/>; its knob is
    /// null when nothing declares it.</summary>
    private static Held Set(Checker check, string key, string value, string example)
    {
        Held h = Held.Of(key);
        check.Expect(h.Knob != null, $"{example} a setting {key} is declared");
        h.Knob?.Parse(value);
        return h;
    }

    /// <summary>Write <paramref name="k"/> to the file, put it back to its default, and bind it again from what the
    /// file says: a quit and a fresh start, through the one read path.</summary>
    private static bool Restart(Knob k)
    {
        string? line = LineOf(KnobFile.Compose(), k.Key);
        k.Reset();
        if (line == null) return false;
        Settings.Read(line + "\n");
        string? raw = Settings.Raw(k.Key);
        if (raw != null) k.Parse(raw);
        return true;
    }

    private static string? LineOf(string file, string key)
    {
        foreach (string l in file.Split('\n'))
            if (l.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) return l;
        return null;
    }

    /// <summary>Press the chat gear's toggle for <paramref name="win"/> whose press saves the window's
    /// <see cref="Setting"/> as 1, pressing each other toggle back off; false when the panel has none.</summary>
    private static bool SwitchOn(ChatLayout layout, ChatWindow win)
    {
        var s = new SettingsWindow();
        GlyphAtlas face = PanelFace();
        s.OpenAt(win, 1280, 800, 20f, face, face, 0, layout.Windows);
        int at = Array.IndexOf(layout.Windows, win);
        if (at > 0) PickWindow(s, layout.Windows[0], at);
        bool on = false;
        for (int i = 0; i < s.Rows.Count && !on; i++)
        {
            if (s.Rows[i].Act == SetAct.None || s.Rows[i].Ctl != KnobKind.Toggle) continue;
            PressRow(s, win, s.Rows[i]);
            on = SavedSetting(win) == "1";
            if (!on) PressRow(s, win, s.Rows[i]);
        }
        s.Reset();
        return on;
    }

    /// <summary>The value <paramref name="win"/>'s saved chat config gives <see cref="Setting"/>, or null when it has none.</summary>
    private static string? SavedSetting(ChatWindow win)
    {
        var c = new ChatConfig();
        c.Windows.Add(WindowConfig.Capture(win));
        foreach (var kv in c.Pairs())
            if (string.Equals(kv.Key, ChatKey(0), StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>Run <paramref name="body"/> with <paramref name="layout"/> as the chatbox's and a module up, the chat
    /// window registered alone; everything put back after.</summary>
    private static void WithChat(ChatLayout layout, Action body)
    {
        bool inGame = GameGate.InGame;
        ChatLayout? was = ChatService.UseLayout(layout);
        GameGate.InGame = true;
        try { UiHost.Alone(() => { _ = ChatService.NewSurfaces(); body(); }); }
        finally { ChatService.UseLayout(was); GameGate.InGame = inGame; }
    }

    /// <summary><paramref name="w"/> draws <paramref name="letter"/> into <paramref name="letters"/> in place of its content.</summary>
    private static void Letter(StringBuilder letters, Window w, char letter) => w.DrawContent = () => letters.Append(letter);

    /// <summary>One frame's letters into <paramref name="letters"/>, bottom to top: each window's, and N for NUI.</summary>
    private static string Drawn(StringBuilder letters) => FrameLetters.Alloy(letters, "", "");

    private static void ChatExamples(Checker check)
    {
        // Given Draw above NUI switched on in the CX chat window's settings
        // When a NUI window overlaps the chat window
        // Then the chat window draws over the NUI window and takes clicks where they overlap
        {
            ChatWindow main = Window(100, 100);
            var layout = new ChatLayout(main);
            bool row = SwitchOn(layout, main);
            check.Expect(row, $"E12 the chat window's settings offer a toggle that saves its {Setting}=1");
            WithChat(layout, () =>
            {
                Window? w = UiHost.HitTest(300, 250);
                check.Expect(w is { Layer: UiLayer.AboveNui }, $"E12 the chat window draws above NUI (layer {w?.Layer})");
                check.Expect(w != null && TakenBeforeNui(300, 250) == w, "E12 a press where they overlap lands on the chat window before NUI");
            });
        }

        // Given a fresh install where nobody has changed Draw above NUI
        // When a NUI window overlaps the CX chat window
        // Then the NUI window draws over the chat window
        {
            ChatWindow main = Window(100, 100);
            var layout = new ChatLayout(main);
            WithChat(layout, () =>
            {
                Window? w = UiHost.HitTest(300, 250);
                check.Expect(w is { Layer: UiLayer.BelowNui }, $"E13 the chat window draws under NUI by default (layer {w?.Layer})");
                check.Expect(w != null && TakenBeforeNui(300, 250) == null, "E13 a press where they overlap is NUI's");
            });
        }

        // Given two CX chat windows, one with Draw above NUI switched on
        // When a NUI window overlaps both
        // Then only that one draws over the NUI window
        {
            ChatWindow main = Window(0, 0);
            var layout = new ChatLayout(main);
            ChatWindow second = layout.AddWindow(Window(600, 0))!;
            bool row = SwitchOn(layout, second);
            check.Expect(row, $"E14 the second chat window's settings offer a toggle that saves its {Setting}=1");
            WithChat(layout, () =>
            {
                Window? a = UiHost.HitTest(200, 150), b = UiHost.HitTest(800, 150);
                check.Expect(b is { Layer: UiLayer.AboveNui }, $"E14 the window switched on draws above NUI (layer {b?.Layer})");
                check.Expect(a is { Layer: UiLayer.BelowNui }, $"E14 the other stays under NUI (layer {a?.Layer})");
                check.Expect(TakenBeforeNui(800, 150) == b && TakenBeforeNui(200, 150) == null,
                             "E14 only the window switched on takes a press before NUI");
            });
        }

        // Given two CX chat windows, Draw above NUI switched on for the second only
        // When the player quits and starts the game again
        // Then only the second draws over a NUI window that overlaps both
        {
            ChatWindow main = Window(0, 0);
            var layout = new ChatLayout(main);
            ChatWindow second = layout.AddWindow(Window(600, 0))!;
            bool row = SwitchOn(layout, second);
            check.Expect(row, $"E35 the second chat window's settings offer a toggle that saves its {Setting}=1");
            var saved = new ChatConfig();
            saved.Windows.Add(WindowConfig.Capture(main));
            saved.Windows.Add(WindowConfig.Capture(second));
            List<KeyValuePair<string, string>> reread = RestartChat(saved.Pairs());
            check.Expect(reread.Exists(kv => string.Equals(kv.Key, ChatKey(1), StringComparison.OrdinalIgnoreCase) && kv.Value == "1"),
                         $"E35 the file carries {ChatKey(1)}=1 across the restart");
            check.Expect(!reread.Exists(kv => string.Equals(kv.Key, ChatKey(0), StringComparison.OrdinalIgnoreCase) && kv.Value == "1"),
                         "E35 ...and not for Main");
            ChatConfig back = ChatConfig.From(reread);
            ChatWindow main2 = Window(0, 0), second2 = Window(600, 0);
            if (back.Windows.Count > 1) { back.Windows[0].ApplyTo(main2); back.Windows[1].ApplyTo(second2); }
            var layout2 = new ChatLayout(main2);
            layout2.AddWindow(second2);
            WithChat(layout2, () =>
            {
                Window? a = UiHost.HitTest(200, 150), b = UiHost.HitTest(800, 150);
                check.Expect(b is { Layer: UiLayer.AboveNui } && a is { Layer: UiLayer.BelowNui },
                             $"E35 after a restart only the second draws above NUI (Main {a?.Layer}, second {b?.Layer})");
            });
        }

        // Given Draw above NUI switched on for the CX chat window, and a NUI window over its input line
        // When the player presses Enter and types
        // Then chat opens and the keys go into its input line
        {
            ChatWindow main = Window(100, 100);
            var layout = new ChatLayout(main);
            bool row = SwitchOn(layout, main);
            check.Expect(row, $"E37 the chat window's settings offer a toggle that saves its {Setting}=1");
            check.Expect(!ChatService.HiddenAt(main, nuiCovers: true),
                         "E37 a NUI window over a chat window drawn above NUI leaves its line seen, so Enter opens it and typing stays open");
        }

        // Given Draw above NUI switched on for the CX inventory
        // When the chat channel dropdown opens over the inventory
        // Then the dropdown draws over the inventory
        {
            Held h = Set(check, KeyOf(InventoryKnobs.G), "1", "E16");
            try
            {
                ChatWindow main = Window(100, 400);
                main.FooterVisible = true;
                main.ChannelListOpen = true;
                float x = main.X + 5f, iy = main.ChannelItemTop(0) + 2f;
                check.Expect(main.HitTestFull(x, iy), "E16 the open dropdown's first channel is at the point");
                var layout = new ChatLayout(main);
                WithChat(layout, () =>
                {
                    Window inv = InventoryService.NewWindow();
                    inv.HitTestOverride = (px, py) => px >= x - 2f && px < x + 40f && py >= iy - 10f && py < iy + 10f;
                    Window? top = UiHost.HitTest(x, iy);
                    check.Expect(top != null && top != inv, $"E16 the dropdown, not the inventory, is under the pointer (layer {top?.Layer})");
                    if (top != null)
                    {
                        var letters = new StringBuilder();
                        Letter(letters, inv, 'i');
                        Letter(letters, top, 'd');
                        string f = Drawn(letters);
                        check.Expect(f.IndexOf('d', StringComparison.Ordinal) > f.IndexOf('i', StringComparison.Ordinal),
                                     $"E16 the dropdown draws after the inventory: {f}");
                    }
                });
            }
            finally { h.Restore(); }
        }
    }

    /// <summary>Put <paramref name="pairs"/> in the chat bag, compose the file, and read back the bag's keys through the
    /// one read path; the bag and the loaded table's lines for it are put back after, as they were.</summary>
    private static List<KeyValuePair<string, string>> RestartChat(List<KeyValuePair<string, string>> pairs)
    {
        KnobBag bag = ChatKnobs.Windows;
        KeyValuePair<string, string>[] before = bag.Pairs;
        var loaded = new List<KeyValuePair<string, string>>();
        foreach (var kv in Settings.Pairs)
            if (bag.Owns(kv.Key)) loaded.Add(kv);
        var read = new List<string>();
        var back = new List<KeyValuePair<string, string>>();
        try
        {
            foreach (var kv in loaded) Settings.Forget(kv.Key);
            bag.Replace(pairs);
            var text = new StringBuilder();
            foreach (string l in KnobFile.Compose().Split('\n'))
            {
                int eq = l.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0 && bag.Owns(l[..eq])) { text.Append(l).Append('\n'); read.Add(l[..eq]); }
            }
            bag.Replace(Array.Empty<KeyValuePair<string, string>>());
            Settings.Read(text.ToString());
            foreach (var kv in Settings.Pairs)
                if (bag.Owns(kv.Key)) back.Add(new(kv.Key[(bag.Group.Name.Length + 1)..], kv.Value));
        }
        finally
        {
            foreach (string key in read) Settings.Forget(key);
            var text = new StringBuilder();
            foreach (var kv in loaded) text.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
            Settings.Read(text.ToString());
            bag.Replace(before);
        }
        return back;
    }

    private static void InventoryExamples(Checker check)
    {
        string key = KeyOf(InventoryKnobs.G);

        // Given Draw above NUI switched on for the CX inventory
        // When the player quits and starts the game again
        // Then the inventory still draws above NUI
        {
            Held h = Set(check, key, "1", "E15");
            try
            {
                bool written = h.Knob != null && Restart(h.Knob);
                check.Expect(written, $"E15 {key} is written to cx.cfg");
                UiHost.Alone(() =>
                {
                    Window inv = InventoryService.NewWindow();
                    check.Expect(inv.Layer == UiLayer.AboveNui, $"E15 after a restart the inventory draws above NUI (layer {inv.Layer})");
                });
            }
            finally { h.Restore(); }
        }

        // Given Draw above NUI switched on for the CX inventory, drawn over a NUI window from the server
        // When the player presses Esc
        // Then the inventory closes and the NUI window stays open
        {
            Held h = Set(check, key, "1", "E33");
            try
            {
                using var nk = new CX.Services.Nui.NuiScratch();
                UiHost.Alone(() =>
                {
                    var model = new InventoryWindow();
                    model.Act = a => { if (a.Kind == InvActKind.Close) model.Hide(); };
                    model.Layout(1f, false, true, 4000, 4000);
                    model.X = 100; model.Y = 100;
                    model.Show();
                    Window inv = InventoryService.NewWindow(model);
                    IntPtr ctx = EscOrderApprovedExamples.NuiContext(nk);
                    IntPtr nui = EscOrderApprovedExamples.NuiWindow(nk, ctx, EscOrderApprovedExamples.Json + "quest",
                                                                    EscOrderApprovedExamples.Closable, 150, 150, 200, 150);
                    bool taken = EscOrderApprovedExamples.Press(ctx);
                    uint flags = EscOrderApprovedExamples.Flags(nui);
                    check.Expect(inv.Layer == UiLayer.AboveNui, $"E33 the inventory draws above NUI (layer {inv.Layer})");
                    check.Expect(taken && !model.Open && flags == EscOrderApprovedExamples.Closable,
                      $"E33 Esc closes the inventory drawn over the NUI window and leaves the NUI window open: taken={taken} open={model.Open} nui=0x{flags:x}");
                });
            }
            finally { h.Restore(); }
        }

        // Given Draw above NUI switched on for the CX inventory
        // When the player presses the gold button
        // Then the gold prompt draws over the inventory and takes the typed amount
        {
            Held h = Set(check, key, "1", "E34");
            try
            {
                UiHost.Alone(() =>
                {
                    Window inv = InventoryService.NewWindow(), gold = InventoryService.NewGoldWindow();
                    inv.HitTestOverride = InSquare;
                    gold.HitTestOverride = (px, py) => px >= 200 && px < 400 && py >= 250 && py < 350;
                    gold.SuppressChords = true;
                    Window? keyed = null;
                    inv.OnKey = (_, _) => { keyed ??= inv; return true; };
                    gold.OnKey = (_, _) => { keyed ??= gold; return true; };
                    var letters = new StringBuilder();
                    Letter(letters, inv, 'i');
                    Letter(letters, gold, 'g');
                    string f = Drawn(letters);
                    check.Expect(inv.Layer == UiLayer.AboveNui, $"E34 the inventory draws above NUI (layer {inv.Layer})");
                    check.Expect(f.IndexOf('g', StringComparison.Ordinal) > f.IndexOf('i', StringComparison.Ordinal),
                                 $"E34 the gold prompt draws over the inventory: {f}");
                    check.Expect(UiHost.HitTest(300, 300) == gold && TakenBeforeNui(300, 300) == gold,
                                 "E34 a press on the prompt lands on the prompt");
                    UiHost.DispatchKey(KeyFive, 0);
                    check.Expect(keyed == gold, "E34 the typed amount goes to the gold prompt");
                });
            }
            finally { h.Restore(); }
        }

        // Given the CX inventory under a NUI window
        // When the player switches Draw above NUI on, and later off again
        // Then the inventory moves over the NUI window at once, and back under it at once, with no restart
        {
            Held h = Held.Of(key);
            Knob? k = h.Knob;
            check.Expect(k != null, $"E38 a setting {key} is declared");
            try
            {
                UiHost.Alone(() =>
                {
                    Window inv = InventoryService.NewWindow();
                    inv.HitTestOverride = InSquare;
                    var letters = new StringBuilder();
                    Letter(letters, inv, 'i');
                    string before = Drawn(letters);
                    check.Expect(Under(before, 'i'), $"E38 the inventory starts under NUI: {before}");
                    k?.Parse("1");
                    string on = Drawn(letters);
                    check.Expect(Over(on, 'i') && TakenBeforeNui(300, 300) == inv, $"E38 switched on, it draws over NUI and takes the press at once: {on}");
                    k?.Parse("0");
                    string off = Drawn(letters);
                    check.Expect(Under(off, 'i') && TakenBeforeNui(300, 300) == null, $"E38 switched off, it goes back under at once: {off}");
                });
            }
            finally { h.Restore(); }
        }
    }

    private static void DialogAndQuickbar(Checker check)
    {
        // Given Draw above NUI switched on for the CX conversation window, and separately for the CX quickbar
        // When a NUI window overlaps either
        // Then that window draws over the NUI window and takes clicks where they overlap
        Each(check, KeyOf(DialogKnobs.G), "the conversation window", DialogService.NewWindow);
        Each(check, KeyOf(QuickbarKnobs.G), "the quickbar", QuickbarService.NewWindow);
    }

    private static void Each(Checker check, string key, string what, Func<Window> make)
    {
        Held h = Set(check, key, "1", "E36");
        try
        {
            UiHost.Alone(() =>
            {
                Window w = make();
                w.HitTestOverride = InSquare;
                var letters = new StringBuilder();
                Letter(letters, w, 'w');
                string f = Drawn(letters);
                check.Expect(Over(f, 'w'), $"E36 {what} draws over NUI: {f}");
                check.Expect(TakenBeforeNui(300, 300) == w, $"E36 {what} takes a press where it overlaps NUI");
            });
        }
        finally { h.Restore(); }
    }
}
