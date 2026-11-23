using System;
using System.Text;
using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Core;
using CX.Services.Inventory;
using CX.Services.NuiItems;
using CX.Services.Quickbar;
using CX.Services.Radial;

namespace CX.Ui;

/// <summary>The popup band's approved examples (E6-E11, E28-E32): a frame's 2D order as letters, bottom to top, from the
/// owners' own windows, and a press routed as the input pump routes it. Oracle: the letters and the order each example
/// states, and the pixel rows worked by hand from a 400 x 300 chat window at 100,500 (28 px strip and rows, at 772).</summary>
internal static class PopupBandApprovedExamples
{
    /// <summary>Q then E for the game's quickbar and Examine panels.</summary>
    private const string Panels = "QE";

    /// <summary>O then M for the overlay scene and its cursor.</summary>
    private const string Overlays = "OM";

    /// <summary>The SDL keycode Escape arrives as.</summary>
    private const uint Esc = 0x1B;   // not-an-offset: SDL key/event code

    /// <summary>One frame of the windows <paramref name="setup"/> makes, as letters; <paramref name="alloy"/> false draws
    /// the Alloy-off order.</summary>
    private static string Frame(Action<StringBuilder> setup, bool alloy = true)
    {
        var drawn = new StringBuilder();
        string f = "";
        Alone(() =>
        {
            setup(drawn);
            f = alloy ? FrameLetters.Alloy(drawn, Panels, Overlays) : FrameLetters.Gl(drawn, Panels, Overlays);
        });
        return f;
    }

    /// <summary>True when <paramref name="top"/> and <paramref name="under"/> both draw and <paramref name="top"/> draws later.</summary>
    private static bool Over(string f, char top, char under)
    {
        int t = f.IndexOf(top, StringComparison.Ordinal), u = f.IndexOf(under, StringComparison.Ordinal);
        return t >= 0 && u >= 0 && t > u;
    }

    /// <summary>A chat window at 100,500 of 400 x 300 whose tab sends on Talk, Shout and Whisper, typing, so its strip
    /// shows; <paramref name="open"/> presses the channel button the way the player opens the list.</summary>
    private static ChatWindow ChatModel(bool open)
    {
        var m = new ChatWindow(100f, 500f, 400f, 300f, new[] { new Tab("Local", 0x00D) }) { FooterVisible = true };
        m.ClampSendChannel();
        if (open) m.MouseDown(105f, 777f);
        return m;
    }

    /// <summary>The chatbox's own window with <paramref name="m"/> as its one chat window and a module up: c for the box
    /// and l for its channel list, drawn where the chatbox draws it, the model's hit-test and press, and the chatbox's
    /// own answer to whether a list is open. Run inside <see cref="Alone"/>, which puts the chatbox back.</summary>
    private static Window ChatWin(StringBuilder drawn, ChatWindow m)
    {
        ChatService.UseLayout(new ChatLayout(m));
        GameGate.InGame = true;
        Window w = ChatService.NewWindow();
        w.DrawContent = () => { drawn.Append('c'); if (m.ChannelListOpen) drawn.Append('l'); };
        w.HitTestOverride = m.HitTestFull;
        w.OnMouseButton = (down, _, x, y) => down && m.MouseDown(x, y);
        return w;
    }

    /// <summary><see cref="UiHost.Alone"/>, with the chatbox's layout and the in-game gate put back after.</summary>
    private static void Alone(Action body)
    {
        bool inGame = GameGate.InGame;
        ChatLayout? was = ChatService.UseLayout(null);
        try { UiHost.Alone(body); }
        finally { ChatService.UseLayout(was); GameGate.InGame = inGame; }
    }

    /// <summary>A left press the input pump routes with <paramref name="nui"/> saying whether a NUI window is under it;
    /// true when CX took it.</summary>
    private static bool Press(int x, int y, bool nui = false)
        => UiInputService.RoutePress(UiInputService.SdlButtonLeft, x, y, new UiInputService.PointerUnder(nui, false, false))
           == UiInputService.PointerOutcome.Taken;

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "PopupBandApprovedExamples.SelfCheck");

        // Given the chat channel dropdown open over a button of the server's NUI hotbar
        // When the player looks at the list
        // Then the whole list draws over the NUI button
        {
            ChatWindow m = ChatModel(open: true);
            string f = Frame(d => ChatWin(d, m));
            check.Expect(m.ChannelListOpen && Over(f, 'l', 'N'), $"E6 the open channel list draws over NUI: {f}");
        }

        // Given the chat channel dropdown open over a button of the server's NUI hotbar
        // When the player clicks the channel drawn over that button
        // Then chat switches to that channel and the NUI button does not fire
        {
            ChatWindow m = ChatModel(open: true);
            bool aheadOfNui = false;
            Alone(() =>
            {
                ChatWin(new StringBuilder(), m);
                aheadOfNui = Press(105, 720, nui: true);
            });
            check.Expect(aheadOfNui, "E7 the press on the Shout row is taken ahead of NUI, so the NUI button under it does not fire");
            check.Expect(m.SendChannel == Channel.Shout, $"E7 chat switches to Shout, the row pressed (sends on {m.SendChannel})");
        }

        // Given the CX radial menu open over NUI icons
        // When the player looks at the menu
        // Then the menu draws over the icons
        {
            string f = Frame(d =>
            {
                Window r = RadialService.NewWindow();
                r.SuppressChords = true;
                r.DrawContent = () => d.Append('r');
            });
            check.Expect(Over(f, 'r', 'N'), $"E8 the radial menu draws over NUI: {f}");
        }

        // Given the CX radial menu open over the CX chat window
        // When the player looks at the menu
        // Then the menu draws over the chat window
        {
            string f = Frame(d =>
            {
                ChatWin(d, ChatModel(open: false));
                Window r = RadialService.NewWindow();
                r.SuppressChords = true;
                r.DrawContent = () => d.Append('r');
            });
            check.Expect(Over(f, 'r', 'c'), $"E9 the radial menu draws over the chat window: {f}");
        }

        // Given the chat channel dropdown closed and a NUI window over part of the chat window
        // When the player looks at the chat window
        // Then the NUI window still draws over it
        {
            ChatWindow m = ChatModel(open: false);
            string f = Frame(d => ChatWin(d, m));
            check.Expect(!f.Contains('l', StringComparison.Ordinal) && Over(f, 'N', 'c'), $"E10 with the list closed NUI draws over the chat window: {f}");
        }

        // Given the CX chat window set to draw above NUI and the server's item menu open over part of it
        // When the player looks at the menu
        // Then the whole item menu draws over the chat window
        {
            string f = Frame(d =>
            {
                ChatWin(d, ChatModel(open: false)).Layer = UiLayer.AboveNui;
                Window i = ItemMenuService.NewWindow();
                i.SuppressChords = true;
                i.DrawContent = () => d.Append('i');
            });
            check.Expect(Over(f, 'c', 'N') && Over(f, 'i', 'c'), $"E28 the item menu draws over a chat window above NUI: {f}");
        }

        // Given the settings panel open under the CX inventory, and a settings dropdown open whose list reaches over the inventory
        // When the player clicks the choice drawn over the inventory
        // Then the setting takes that choice, the list draws whole over the inventory, and the inventory does not react
        {
            var panel = new KnobPanel();
            panel.OpenAt(1280f, 720f, 16f, null, null);
            Dropdown drop = panel.Drop;
            drop.OpenAt(new[] { "low", "mid", "high" }, 0, null, 200f, 200f, 224f, 120f, 24f);
            bool invReacted = false, took = false;
            var drawn = new StringBuilder();
            Alone(() =>
            {
                Window s = KnobPanelService.NewWindow(panel);
                s.SuppressChords = true;
                s.DrawContent = () => { drawn.Append('s'); if (drop.Open) drawn.Append('d'); };
                s.HitTestOverride = (x, y) => (x >= 100f && x < 300f && y >= 100f && y < 230f) || drop.Contains(x, y);
                s.OnMouseButton = (down, _, x, y) => down && drop.MouseDown(x, y, out _);
                Window v = InventoryService.NewWindow();
                v.DrawContent = () => drawn.Append('v');
                v.HitTestOverride = (x, y) => x >= 150f && x < 450f && y >= 240f && y < 500f;
                v.OnMouseButton = (_, _, _, _) => invReacted = true;
                FrameLetters.Alloy(drawn, Panels, Overlays);
                took = Press(210, 280);
            });
            string f = drawn.ToString();
            check.Expect(Over(f, 'd', 'v'), $"E29 the open settings list draws over the inventory above its panel: {f}");
            check.Expect(took && drop.Selected == 2 && !drop.Open, $"E29 the setting takes high, the choice pressed (selected {drop.Selected})");
            check.Expect(!invReacted, "E29 the inventory under the list does not react to the press");
        }

        // Given the chat channel dropdown open, with the software cursor on
        // When the player rests the pointer on a channel in the list
        // Then the cursor draws over the list
        {
            ChatWindow m = ChatModel(open: true);
            string f = Frame(d => ChatWin(d, m));
            check.Expect(Over(f, 'M', 'l') && f.EndsWith('M'), $"E30 the cursor draws over the open list, last: {f}");
        }

        // Given Alloy off and the chat channel dropdown open over a button of the server's NUI hotbar
        // When the player looks at the list
        // Then the whole list draws over the NUI button
        {
            ChatWindow m = ChatModel(open: true);
            string f = Frame(d => ChatWin(d, m), alloy: false);
            check.Expect(m.ChannelListOpen && Over(f, 'l', 'N'), $"E31 with Alloy off the open channel list draws over NUI: {f}");
        }

        // Given the CX quickbar under a NUI window, with a slot's tooltip reaching over the NUI window
        // When the player rests the pointer on that slot
        // Then the whole tooltip draws over the NUI window
        {
            string f = Frame(d =>
            {
                Window q = QuickbarService.NewWindow();
                q.DrawContent = () => d.Append('q');
                q.DrawOver = () => d.Append('t');
            });
            check.Expect(Over(f, 'N', 'q') && Over(f, 't', 'N'), $"E32 the quickbar's tooltip draws over the NUI window above the bar: {f}");
        }

        // Given the chat channel dropdown open
        // When the player presses Esc
        // Then the dropdown closes, the chat window stays open and the game menu does not open
        {
            ChatWindow m = ChatModel(open: true);
            bool was = m.ChannelListOpen, taken = false, typing = false;
            bool typed = ChatService.IsTyping;
            Alone(() =>
            {
                ChatWin(new StringBuilder(), m);
                ChatService.IsTyping = true;
                try
                {
                    taken = UiInputService.PumpKeyDown(Esc, 0) || UiInputService.RouteKeyDown(Esc, 0, nuiEdit: false);
                    typing = ChatService.IsTyping;
                }
                finally { ChatService.IsTyping = typed; }
            });
            check.Expect(was && !m.ChannelListOpen && typing && taken,
                $"E11 Esc closes the open channel list, chat keeps its line and the engine never sees Esc: was={was} open={m.ChannelListOpen} typing={typing} taken={taken}");
        }

        if (check.Ok) log.Info("PopupBandApprovedExamples.SelfCheck: passed");
    }
}
