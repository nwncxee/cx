using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Core;
using CX.Services.Dialog;
using CX.Services.Inventory;
using CX.Services.Nui;

namespace CX.Ui;

/// <summary>Esc closes the window on top before the game menu opens: the CX side of each press, driven through
/// the arbiter's live key path. Oracle: hand-written literals of what each press closes, of the nuklear flags a
/// close leaves, and whether the engine still receives Esc (exactly when no CX window consumed it).</summary>
internal static class EscOrderApprovedExamples
{
    /// <summary>The SDL keycode Escape arrives as.</summary>
    private const uint Esc = 0x1B;   // not-an-offset: SDL key/event code

    /// <summary>The chest the loot pane shows.</summary>
    private const uint Chest = 0x500;   // not-an-offset: an object id

    /// <summary>nuklear's window flags as this build tests them: the close button drawn, and the two bits
    /// <c>nk_window_close</c> sets (hidden, closed), which the NUI window's next update turns into its close.</summary>
    internal const uint Closable = 0x8, HiddenClosed = 0xC000;   // not-an-offset: nuklear window flags

    /// <summary>Bytes enough for a nuklear context and for one window, as the examples fake them.</summary>
    private const int CtxBytes = 0x7000, WinBytes = 0x400;   // not-an-offset: scratch sizes

    /// <summary>The prefix the client names a server NUI window's nuklear window with.</summary>
    internal const string Json = "JsonWindow::";

    /// <summary>A CX inventory at 100,100 made by its service's own factory, Draw above NUI off, and a Close action
    /// that hides it as the service's close does.</summary>
    private sealed class Inventory
    {
        public readonly InventoryWindow Inv = new();
        public readonly List<InvAction> Acts = new();
        public readonly Window Host;

        public Inventory(bool open)
        {
            Inv.Act = a => { Acts.Add(a); if (a.Kind == InvActKind.Close) Inv.Hide(); };
            Inv.Layout(1f, false, true, 4000, 4000);
            Inv.X = 100; Inv.Y = 100;
            if (open) Inv.Show();
            Host = InventoryService.NewWindow(Inv);
            Host.RaiseAboveNui = static () => false;
        }

        public string Seen => string.Join(", ", Acts);
    }

    /// <summary>An empty nuklear context in <paramref name="nk"/>.</summary>
    internal static IntPtr NuiContext(NuiScratch nk) => nk.Block(CtxBytes);

    /// <summary>A NUI window named <paramref name="name"/> with <paramref name="flags"/> at the given bounds, put on top
    /// of the context's window list (nuklear draws the list's last window topmost).</summary>
    internal static IntPtr NuiWindow(NuiScratch nk, IntPtr ctx, string name, uint flags, float x, float y, float w, float h)
    {
        IntPtr win = nk.Block(WinBytes);
        var v = new NkWindow(win);
        Encoding.ASCII.GetBytes(name, v.Name);
        v.Flags = flags;
        Span<float> b = v.Bounds;
        b[0] = x; b[1] = y; b[2] = w; b[3] = h;
        var c = new NkContext(ctx);
        if (c.WindowBegin == IntPtr.Zero) { c.WindowBegin = win; return win; }
        var last = new NkWindow(c.WindowBegin);
        while (last.Next != IntPtr.Zero) last = new NkWindow(last.Next);
        last.Next = win;
        return win;
    }

    /// <summary>The window's nuklear flags.</summary>
    internal static uint Flags(IntPtr win) => new NkWindow(win).Flags;

    /// <summary>Whether nuklear would report the window hidden and closed.</summary>
    internal static bool Closed(IntPtr win) => (Flags(win) & HiddenClosed) == HiddenClosed;

    /// <summary>One Esc press in the live order, the pump's step then the key-down hook's, no NUI edit box focused, over
    /// the nuklear context <paramref name="nui"/>, a stock modal and a cutscene as given. True when CX took it.</summary>
    internal static bool Press(IntPtr nui = default, bool modal = false, bool cutscene = false)
    {
        var under = new UiInputService.EscUnder(modal, cutscene, nui);
        return UiInputService.PumpKeyDown(Esc, 0, under) || UiInputService.RouteKeyDown(Esc, 0, false, under);
    }

    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "EscOrderApprovedExamples.SelfCheck");

        // Given the CX inventory open and nothing drawn over it
        // When the player presses Esc
        // Then the inventory closes and the game menu does not open
        UiHost.Alone(() =>
        {
            var inv = new Inventory(open: true);
            bool taken = Press();
            check.Expect(taken && !inv.Inv.Open && inv.Acts.Count == 1 && inv.Acts[0].Kind == InvActKind.Close,
              $"E17 Esc closes the lone inventory and the engine never sees it: taken={taken} open={inv.Inv.Open} acts=[{inv.Seen}]");
        });

        // Given no window open
        // When the player presses Esc
        // Then the game menu opens
        UiHost.Alone(() =>
        {
            var inv = new Inventory(open: false);
            _ = ChatService.NewSurfaces();
            bool taken = Press();
            check.Expect(!taken && inv.Acts.Count == 0, $"E18 with nothing open Esc reaches the engine: taken={taken} acts=[{inv.Seen}]");
        });

        // Given the stock character sheet open and nothing drawn over it
        // When the player presses Esc
        // Then the character sheet closes and the game menu does not open
        // Given only the stock area map open
        // When the player presses Esc
        // Then the game menu opens and the area map stays open, as in the stock client
        {
            ChatLayout? was = ChatService.UseLayout(new ChatLayout(ChatExamplesKit.Window(100, 600)));
            try
            {
                using var nk = new NuiScratch();
                UiHost.Alone(() =>
                {
                    var inv = new Inventory(open: false);
                    _ = ChatService.NewSurfaces();
                    _ = DialogService.NewWindow();
                    IntPtr ctx = NuiContext(nk);
                    IntPtr nui = NuiWindow(nk, ctx, Json + "board", Closable, 150, 150, 200, 150);
                    bool taken = Press(ctx);
                    check.Expect(!taken && inv.Acts.Count == 0 && Flags(nui) == Closable,
                      $"E19 E42 with every CX window closed Esc reaches the engine, which closes its own panel, and closes no NUI window: taken={taken} acts=[{inv.Seen}] nui=0x{Flags(nui):x}");
                });
            }
            finally { ChatService.UseLayout(was); }
        }

        // Given the stock character sheet open and the CX inventory over it
        // When the player presses Esc three times
        // Then the inventory closes, then the character sheet, then the game menu opens
        UiHost.Alone(() =>
        {
            var inv = new Inventory(open: true);
            bool first = Press();
            bool closedFirst = !inv.Inv.Open && inv.Acts.Count == 1 && inv.Acts[0].Kind == InvActKind.Close;
            bool second = Press(), third = Press();
            check.Expect(first && closedFirst && !second && !third && inv.Acts.Count == 1,
              $"E21 one window per press: the inventory takes the first Esc, the engine the next two: presses={first}/{second}/{third} acts=[{inv.Seen}]");
        });

        // Given the player typing in the CX chat input with the inventory open
        // When the player presses Esc
        // Then the chat input closes and the inventory stays open
        {
            bool typing = ChatService.IsTyping;
            ChatLayout? was = ChatService.UseLayout(new ChatLayout(ChatExamplesKit.Window(100, 100)));
            try
            {
                UiHost.Alone(() =>
                {
                    var inv = new Inventory(open: true);
                    Window chat = ChatService.NewSurfaces().Below;
                    chat.WantsKeyboard = true;
                    ChatService.IsTyping = true;
                    bool taken = Press();
                    check.Expect(taken && !ChatService.IsTyping && inv.Inv.Open && inv.Acts.Count == 0,
                      $"E22 Esc while typing closes the chat input and leaves the inventory open: taken={taken} typing={ChatService.IsTyping} open={inv.Inv.Open} acts=[{inv.Seen}]");
                });
            }
            finally { ChatService.IsTyping = typing; ChatService.UseLayout(was); }
        }

        // Given a chest open in the CX inventory's loot pane
        // When the player presses Esc
        // Then the chest closes the way its own close button closes it, the inventory stays open, and the game menu does not open
        UiHost.Alone(() =>
        {
            var inv = new Inventory(open: true);
            inv.Inv.Loot.Reset(Chest, Stopwatch.GetTimestamp());
            bool shown = inv.Inv.LootShown;
            bool taken = Press();
            check.Expect(shown && taken && inv.Inv.Open && inv.Acts.Count == 1 && inv.Acts[0] == new InvAction(InvActKind.CloseLoot, Other: Chest),
              $"E39 Esc closes the chest as its X does and keeps the inventory: shown={shown} taken={taken} open={inv.Inv.Open} acts=[{inv.Seen}]");
        });

        // Given the chat window's gear panel open, no field being edited
        // When the player presses Esc
        // Then the gear panel closes, the chat window stays open and the game menu does not open
        {
            bool inGame = GameGate.InGame, typing = ChatService.IsTyping;
            ChatWindow win = ChatExamplesKit.Window(100, 100);
            var layout = new ChatLayout(win);
            ChatLayout? was = ChatService.UseLayout(layout);
            try
            {
                GameGate.InGame = true;
                ChatService.IsTyping = false;
                UiHost.Alone(() =>
                {
                    _ = ChatService.NewSurfaces();
                    bool gear = UiHost.DispatchMouseButton(true, 1, (int)(win.X + win.W - 2), (int)(win.Y + 2));
                    bool first = Press(), second = Press();
                    check.Expect(gear && first && !second && layout.Count == 1 && layout.Windows[0] == win,
                      $"E44 the first Esc closes the gear panel its button opened and is kept from the engine, the next reaches it, and the chat window stays: gear={gear} presses={first}/{second} windows={layout.Count}");
                });
            }
            finally { GameGate.InGame = inGame; ChatService.IsTyping = typing; ChatService.UseLayout(was); }
        }

        // Given a NUI window from the server drawn over the CX inventory
        // When the player presses Esc
        // Then the NUI window closes and the inventory stays open
        {
            using var nk = new NuiScratch();
            UiHost.Alone(() =>
            {
                var inv = new Inventory(open: true);
                IntPtr ctx = NuiContext(nk);
                IntPtr over = NuiWindow(nk, ctx, Json + "quest", Closable, 150, 150, 200, 150);
                IntPtr far = NuiWindow(nk, ctx, Json + "far", Closable, 3000, 3000, 100, 100);
                IntPtr own = NuiWindow(nk, ctx, "options", Closable, 150, 150, 200, 150);
                bool taken = Press(ctx);
                check.Expect(taken && Closed(over) && Flags(far) == Closable && Flags(own) == Closable && inv.Inv.Open && inv.Acts.Count == 0,
                  $"E20 Esc closes the server's NUI window drawn over the inventory, not one elsewhere nor the client's own: taken={taken} over=0x{Flags(over):x} far=0x{Flags(far):x} own=0x{Flags(own):x} open={inv.Inv.Open} acts=[{inv.Seen}]");
            });
        }

        // Given a server NUI window with no close button drawn over the CX inventory
        // When the player presses Esc
        // Then the NUI window stays open, the inventory closes, and the server receives no close event
        {
            using var nk = new NuiScratch();
            UiHost.Alone(() =>
            {
                var inv = new Inventory(open: true);
                IntPtr ctx = NuiContext(nk);
                IntPtr sign = NuiWindow(nk, ctx, Json + "sign", 0, 150, 150, 200, 150);
                bool taken = Press(ctx);
                check.Expect(taken && Flags(sign) == 0 && !inv.Inv.Open && inv.Acts.Count == 1 && inv.Acts[0].Kind == InvActKind.Close,
                  $"E41 Esc passes over a NUI window with no close button, leaving its flags, and closes the inventory: taken={taken} sign=0x{Flags(sign):x} open={inv.Inv.Open} acts=[{inv.Seen}]");
            });
        }

        // Given a stock Yes/No box open while the CX inventory is open
        // When the player presses Esc
        // Then the box answers Esc as it does in the stock client, and the inventory stays open
        UiHost.Alone(() =>
        {
            var inv = new Inventory(open: true);
            bool taken = Press(modal: true);
            check.Expect(!taken && inv.Inv.Open && inv.Acts.Count == 0,
              $"E40 with a stock modal up Esc reaches the engine and the inventory stays open: taken={taken} open={inv.Inv.Open} acts=[{inv.Seen}]");
        });

        // Given a cutscene playing while a CX window that answers Esc is open
        // When the player presses Esc
        // Then the cutscene is skipped as in the stock client, and the game menu does not open
        UiHost.Alone(() =>
        {
            var inv = new Inventory(open: true);
            bool taken = Press(cutscene: true);
            check.Expect(!taken && inv.Inv.Open && inv.Acts.Count == 0,
              $"E43 in a cutscene Esc reaches the engine, which skips the cutscene, and the CX window leaves it be: taken={taken} open={inv.Inv.Open} acts=[{inv.Seen}]");
        });

        if (check.Ok) log.Info("EscOrderApprovedExamples.SelfCheck: passed");
    }
}
