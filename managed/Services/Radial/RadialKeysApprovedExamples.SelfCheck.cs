using System;
using CX.Framework;
using CX.Services.Nui;
using CX.Services.NuiItems;
using CX.Ui;

namespace CX.Services.Radial;

public sealed unsafe partial class RadialService
{
    /// <summary>The SDL code of the Down arrow.</summary>
    private const uint KeyDownArrow = 0x40000051;  // not-an-offset: SDL key/event code

    /// <summary>True when a key-down reaches the game: no window takes it at the input pump, where the engine queues its keybinds.</summary>
    internal static bool GameGets(uint key) => !UiHost.DispatchKeyPumpSwallow(key, 0);

    /// <summary>Runs <paramref name="body"/> with the cascade open on <paramref name="items"/> in a window of its own, alone in
    /// the UI host, as an open on a creature leaves it; no engine root is held, so no engine call is reached.</summary>
    private static void WithMenuOpen(ExtenderLog log, CascadeItem[] items, Action body)
    {
        var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
        for (char ch = 'a'; ch <= 'z'; ch++) atlas.SetTestGlyph(ch, 6f);
        Window? saved = _window;
        bool wasEnabled = RadialKnobs.Enabled.V, wasLocked = RadialKnobs.Lock.V;
        var savedLog = _log;
        _log = null;
        RadialKnobs.Enabled.V = true;   // the fields, not Set(): no Changed, no writer armed
        RadialKnobs.Lock.V = false;
        try
        {
            UiHost.Alone(() =>
            {
                _window = NewWindow();
                Array.Clear(_levelMenu); Array.Clear(_levelPath); Array.Clear(_levelFolder);
                _ctx.Close(); _ctxLevel = _ctxRow = -1; _naming = null;
                _pressOwned = false; _deferLevel = _deferRow = -1; _deferClose = null;
                _atlas = atlas; _vpW = 1920f; _vpH = 1080f; _kind = RootKind.Creature;
                _menu.DragEnabled = !RadialKnobs.Lock.V;
                _menu.OpenAt(items, 100f, 100f, atlas, 1920f, 1080f);
                _window.SuppressChords = true;
                try { body(); }
                finally { Close(CloseEsc); }
            });
        }
        finally { _window = saved; _log = savedLog; RadialKnobs.Enabled.V = wasEnabled; RadialKnobs.Lock.V = wasLocked; }
    }

    /// <summary>Rows of a creature's menu: Attack, a submenu, and a third leaf.</summary>
    private static CascadeItem[] CreatureRows() => new[]
    {
        new CascadeItem("attack", "ir_attack", tag: 2), new CascadeItem("cast", "ir_cast", hasChildren: true, tag: 1), new CascadeItem("talk", tag: 3),
    };

    /// <summary>Approved examples RK1, RK2, RK5 and RK6: the CX right-click menu passes on every key it does not use.
    /// Oracle: the keys and rows typed here, and the menu state each example names.</summary>
    internal static void KeysApprovedExamples(ExtenderLog log)
    {
        var check = new Checker(log, "RadialKeysApprovedExamples.SelfCheck");

        // Given the CX right-click menu is open on a creature
        // When the player presses W, bound to walking forward
        // Then the character walks forward and the menu stays open
        WithMenuOpen(log, CreatureRows(), () =>
            check.Expect(GameGets('w') && _menu.Open, $"RK1 W reaches the game and the menu stays open (open={_menu.Open})"));

        // Given the CX right-click menu is open on a creature, with the mouse over its second row
        // When the player presses the Down arrow
        // Then the Down arrow does what it is bound to in the game, and the highlight stays on the second row
        WithMenuOpen(log, CreatureRows(), () =>
        {
            _menu.Level(0).Hover = 1;
            bool gets = GameGets(KeyDownArrow);
            check.Expect(gets && _menu.Level(0).Hover == 1, $"RK2 Down reaches the game and the highlight stays on row 2 (game={gets}, hover={_menu.Level(0).Hover})");
        });

        // Given a row's options popup is open in the CX right-click menu
        // When the player presses W
        // Then the character walks forward
        WithMenuOpen(log, CreatureRows(), () =>
        {
            int rowY = (int)_menu.RowTop(0, 0) + 1;
            OnMouseButton(true, 3, 110, rowY); OnMouseButton(false, 3, 110, rowY);
            check.Expect(_ctx.Open, "RK5 precondition: a right press on a row opens its options popup");
            check.Expect(GameGets('w') && _ctx.Open, "RK5 W reaches the game with the options popup open");
        });

        // Given the CX right-click menu is open on a creature
        // When the player presses I, bound to the inventory
        // Then the inventory opens
        WithMenuOpen(log, CreatureRows(), () =>
            check.Expect(GameGets('i') && _menu.Open, "RK6 I reaches the game"));

        log.Info(check.Ok ? "RadialKeysApprovedExamples.SelfCheck: passed" : "RadialKeysApprovedExamples.SelfCheck: FAILED");
    }
}

/// <summary>Approved example RK7: the CX item menu passes on every key it does not use. Oracle: the menu lines and keys
/// typed here.</summary>
internal static class ItemMenuKeysApprovedExamples
{
    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "ItemMenuKeysApprovedExamples.SelfCheck");
        var send = ServerPolicy.Send;
        ServerPolicy.Send = static (_, _) => { };
        try
        {
            UiHost.Alone(() =>
            {
                if (ItemMenuService.MenuWindow is { } w) UiHost.Register(w);
                var atlas = new GlyphAtlas { LineHeight = 20, Ascent = 15 };
                var m = new ItemMenuModel();
                m.Set("row.1", "use;Use;;1");
                m.Set("open", "200;200");

                // Given the CX item menu is open over an item in a server's window
                // When the player presses W
                // Then the character walks forward and the menu stays open
                ItemMenuService.Show(m, atlas, 1920, 1080);
                check.Expect(ItemMenuService.IsOpen, "RK7 precondition: the item menu opens");
                check.Expect(RadialService.GameGets('w') && ItemMenuService.IsOpen, "RK7 W reaches the game and the item menu stays open");
                ItemMenuService.Show(new ItemMenuModel(), atlas, 1920, 1080);
            });
        }
        finally { ServerPolicy.Send = send; }
        log.Info(check.Ok ? "ItemMenuKeysApprovedExamples.SelfCheck: passed" : "ItemMenuKeysApprovedExamples.SelfCheck: FAILED");
    }
}
