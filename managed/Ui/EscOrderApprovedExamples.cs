using System.Collections.Generic;
using System.Diagnostics;
using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Core;
using CX.Services.Inventory;

namespace CX.Ui;

/// <summary>Esc closes the window on top before the game menu opens: the CX side of each press, driven through
/// the arbiter's live key path. Oracle: hand-written literals of what each press closes and whether the engine
/// still receives Esc (it does exactly when no CX window consumed it at the pump or in dispatch).</summary>
internal static class EscOrderApprovedExamples
{
    /// <summary>The SDL keycode Escape arrives as.</summary>
    private const uint Esc = 0x1B;   // not-an-offset: SDL key/event code

    /// <summary>The chest the loot pane shows.</summary>
    private const uint Chest = 0x500;   // not-an-offset: an object id

    /// <summary>A CX inventory registered with the arbiter the way its service wires it: its key handler is the
    /// window's, and a Close action hides it as the service's close does.</summary>
    private sealed class Inventory
    {
        public readonly InventoryWindow Inv = new();
        public readonly List<InvAction> Acts = new();
        public readonly Window Host;

        public Inventory(bool open)
        {
            Inv.Act = a => { Acts.Add(a); if (a.Kind == InvActKind.Close) Inv.Hide(); };
            Inv.Layout(1f, false, true, 4000, 4000);
            if (open) Inv.Show();
            Host = new Window(0, 0, 0, 0) { Focusable = false, OnKey = (k, m) => Inv.Key(k, m) };
        }

        public string Seen => string.Join(", ", Acts);
    }

    /// <summary>One Esc press in the live order, the pump's step then the key-down hook's, no NUI edit box focused.
    /// True when CX took it, so the engine never sees it and its menu cannot open.</summary>
    private static bool Press() => UiInputService.PumpKeyDown(Esc, 0) || UiInputService.RouteKeyDown(Esc, 0, nuiEdit: false);

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
            _ = ChatService.NewWindow();
            bool taken = Press();
            check.Expect(!taken && inv.Acts.Count == 0, $"E18 with nothing open Esc reaches the engine: taken={taken} acts=[{inv.Seen}]");
        });

        // Given the stock character sheet open and nothing drawn over it
        // When the player presses Esc
        // Then the character sheet closes and the game menu does not open
        UiHost.Alone(() =>
        {
            var inv = new Inventory(open: false);
            bool taken = Press();
            check.Expect(!taken && inv.Acts.Count == 0,
              $"E19 no closed CX window takes Esc from the stock panel, which the engine closes first: taken={taken} acts=[{inv.Seen}]");
        });

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
                    Window chat = ChatService.NewWindow();
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

        // Given only the stock area map open
        // When the player presses Esc
        // Then the game menu opens and the area map stays open, as in the stock client
        UiHost.Alone(() =>
        {
            var inv = new Inventory(open: false);
            bool taken = Press();
            check.Expect(!taken && inv.Acts.Count == 0, $"E42 with only the area map up Esc reaches the engine, which opens its menu: taken={taken} acts=[{inv.Seen}]");
        });

        // Given the chat window's gear panel open, no field being edited
        // When the player presses Esc
        // Then the gear panel closes, the chat window stays open and the game menu does not open
        {
            var win = new ChatWindow(100, 100, 400, 300, new[] { new Tab("All", 0xffffffff) });
            var gear = new SettingsWindow();
            gear.OpenAt(win, 1280, 800);
            bool open = gear.Open, editing = gear.Editing;
            bool taken = ChatService.TakesKey(Esc, false, gear);
            check.Expect(open && !editing && taken,
              $"E44 chat takes Esc while its gear panel is open, so the engine never sees it: open={open} editing={editing} taken={taken}");
        }

        if (check.Ok) log.Info("EscOrderApprovedExamples.SelfCheck: passed");
    }
}
