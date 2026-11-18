using CX.Framework;
using CX.Services.Nui;
using CX.Services.NuiItems;
using CX.Ui;

namespace CX.Services.Radial;

public sealed unsafe partial class RadialService
{
    /// <summary>Examples RK3, RK4, RK9, RK10 and RK11, which held before the menu passed keys on: the menu's own keys stay
    /// its own. A regression test, not an approved one. Oracle: the keys, rows and name typed here.</summary>
    internal static void KeysRegressions(ExtenderLog log)
    {
        var check = new Checker(log, "RadialKeysRegressions.SelfCheck");

        // Given the CX right-click menu is open on a creature
        // When the player presses Escape
        // Then the menu closes and the game's options menu does not open
        WithMenuOpen(log, CreatureRows(), () =>
            check.Expect(!GameGets(KeyEscape) && !_menu.Open, "RK3 Escape closes the menu and never reaches the game"));

        // Given the player is typing a new name for a row in the CX right-click menu
        // When the player presses W
        // Then a w is typed into the name and the character does not move
        WithMenuOpen(log, CreatureRows(), () =>
        {
            BeginName("", static _ => { });
            bool gets = GameGets('w');
            string label = _ctx.Open ? _ctx.Level(0).Items[0].Label : "";
            check.Expect(!gets && label == "Name: w_", $"RK4 W types into the name and never reaches the game (game={gets}, \"{label}\")");
        });

        // Given the CX right-click menu is switched off and the game's own radial menu is open
        // When the player presses W
        // Then the character walks forward
        //
        // Off the device only CX's half shows: with the CX menu off no window of ours takes the key; the game's own radial
        // letting it through is the hand check's.
        bool wasEnabled = RadialKnobs.Enabled.V;
        RadialKnobs.Enabled.V = false;
        UiHost.Alone(() =>
        {
            Window? saved = _window;
            _window = NewWindow();
            check.Expect(GameGets('w'), "RK9 with the CX menu off, W reaches the game");
            _window = saved;
        });
        RadialKnobs.Enabled.V = wasEnabled;

        // Given the action camera is on and the player is in game
        // When the player opens the CX right-click menu
        // Then the cursor shows and moving the mouse moves the cursor, not the camera
        //
        // The action camera lets go while any window withholds chords, so the open menu must keep doing so.
        WithMenuOpen(log, CreatureRows(), () =>
            check.Expect(UiHost.AnySuppressChords, "RK10 the open menu holds the action camera off"));

        // Given the CX right-click menu is open on a creature, with the mouse over Attack
        // When the player presses Enter
        // Then the character attacks the creature and the menu closes
        //
        // No engine root is held off the device, so the attack itself is the hand check's; Enter must stay the menu's.
        WithMenuOpen(log, CreatureRows(), () =>
        {
            _menu.Level(0).Hover = 0;
            check.Expect(!GameGets(KeyReturn) && !_menu.Open, "RK11 Enter picks Attack, closes the menu and never reaches the game");
        });

        log.Info(check.Ok ? "RadialKeysRegressions.SelfCheck: passed" : "RadialKeysRegressions.SelfCheck: FAILED");
    }
}

/// <summary>Example RK8, which held before the item menu passed keys on: Escape stays the menu's. A regression test, not
/// an approved one. Oracle: the menu lines, the key and the wire text typed here.</summary>
internal static class ItemMenuKeysRegressions
{
    /// <summary>The SDL code of Escape.</summary>
    private const uint KeyEscape = 0x1B;  // not-an-offset: SDL key/event code

    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "ItemMenuKeysRegressions.SelfCheck");
        var sent = new System.Collections.Generic.List<string>();
        var send = ServerPolicy.Send;
        ServerPolicy.Send = (_, ascii) => sent.Add(ascii);
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
                // When the player presses Escape
                // Then the menu closes and the game's options menu does not open
                ItemMenuService.Show(m, atlas, 1920, 1080);
                check.Expect(ItemMenuService.IsOpen, "RK8 precondition: the item menu opens");
                bool gets = RadialService.GameGets(KeyEscape);
                check.Expect(!gets && !ItemMenuService.IsOpen && sent.Count == 1 && sent[0] == "menu;",
                    $"RK8 Escape closes the item menu with menu; and never reaches the game (game={gets}, sent={sent.Count})");
                ItemMenuService.Show(new ItemMenuModel(), atlas, 1920, 1080);
            });
        }
        finally { ServerPolicy.Send = send; }
        log.Info(check.Ok ? "ItemMenuKeysRegressions.SelfCheck: passed" : "ItemMenuKeysRegressions.SelfCheck: FAILED");
    }
}
