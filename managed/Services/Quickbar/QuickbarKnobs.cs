using CX.Framework;
using CX.Ui;

namespace CX.Services.Quickbar;

/// <summary>The <c>quickbar</c> group and the storage <see cref="QuickbarService"/> reads: the master (off = the stock
/// bar and compass untouched; live both ways), the CX bar (<see cref="Replace"/>: CX draws it, anywhere, its sets
/// stacked or side by side, keys bound by hovering), its flat-look colours, the stock bar's placement rows and the
/// compass. Touched from <c>QuickbarService.Initialize</c>, so the group declares whether the hook arms.</summary>
internal static class QuickbarKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Bars, Hidden, Compass, Replace, Labels, Placed, Locked, LockSlots;
    internal static readonly Knob<int> Align, Rows;
    internal static readonly Knob<string> Arrange;
    internal static readonly Knob<float> Size, GapX, GapY, X, Y;
    /// <summary>The key rows of the plain, Shift and Ctrl sets.</summary>
    internal static readonly Knob<string>[] Keys;
    /// <summary>The flat look's colours, RGBA.</summary>
    internal static readonly TextStyle Text;

    /// <summary>The <see cref="Arrange"/> values.</summary>
    internal const string Stacked = "stacked", SideBySide = "side";
    /// <summary>The stock F1..F12 row each set's keys default to, with the set's modifier in front.</summary>
    internal static readonly string[] DefaultKeys =
    {
        "F1 F2 F3 F4 F5 F6 F7 F8 F9 F10 F11 F12",
        "Shift+F1 Shift+F2 Shift+F3 Shift+F4 Shift+F5 Shift+F6 Shift+F7 Shift+F8 Shift+F9 Shift+F10 Shift+F11 Shift+F12",
        "Ctrl+F1 Ctrl+F2 Ctrl+F3 Ctrl+F4 Ctrl+F5 Ctrl+F6 Ctrl+F7 Ctrl+F8 Ctrl+F9 Ctrl+F10 Ctrl+F11 Ctrl+F12",
    };
    private const float RectMax = 16384f;

    static QuickbarKnobs()
    {
        G = KnobGroup.Declare("quickbar", "Interface", "Quickbar", master: "Quickbar and compass changes",
            masterHelp: "Off, the game's own quickbar and compass are left exactly as they are and the CX quickbar is off.",
            masterChanged: QuickbarService.LayoutChanged, order: 230, title: "Quickbar & compass",
            header: "CX can draw its own quickbar: drag it anywhere, show the Shift and Ctrl sets beside or above the plain "
                  + "one, drag buttons onto each other to swap them, and bind its keys by hovering a button and pressing a key. Turn on \"Use the CX quickbar\".");
        Enabled = G.Master!;
        G.Section("CX quickbar");
        Replace = G.Toggle("replace", "Use the CX quickbar", false, changed: QuickbarService.LayoutChanged,
                           help: "CX draws the quickbar in place of the game's: drag it by its grip, show more sets at once, bind keys on it. Clicks, right-click menus and drag and drop stay the game's own.");
        Hidden = G.Toggle("hidden", "Hide the quickbar", false, help: "Moves the quickbar off-screen, CX's or the game's. The CX quickbar's keys still work.",
                          changed: QuickbarService.LayoutChanged);
        Locked = G.Toggle("locked", "Lock its position", false, changed: QuickbarService.Relayout,
                          help: "Hides the grip on the bar's left edge, so it cannot be dragged by accident.");
        LockSlots = G.Toggle("lockslots", "Lock its buttons", false,
                             help: "Stops dragging one button onto another, so the bar's contents cannot be rearranged by accident. Dropping from the inventory or a spellbook still works.");
        G.Button("bind", "Bind keys on the bar", QuickbarService.StartBinding,
                 help: "Hover a button on the CX quickbar and press a key (with Shift, Ctrl or Alt if you like) to bind it. Backspace clears a button; Esc or a click elsewhere finishes.");
        Rows = G.Dropdown("rows", "Sets shown", new[] { 1, 2, 3 }, 1, new[] { "1 (as vanilla)", "2 (+ Shift)", "3 (+ Shift, Ctrl)" }, changed: QuickbarService.Relayout,
                          help: "More sets show the Shift and Ctrl buttons all the time instead of on the modifier press. One set swaps while Shift or Ctrl is held, as the game's bar does.");
        Arrange = G.Dropdown("arrange", "Arrange sets", new[] { Stacked, SideBySide }, Stacked, new[] { "Stacked", "Side by side" },
                             changed: QuickbarService.Relayout, help: "Stacked puts Shift above plain and Ctrl above Shift; side by side lines them up plain, Shift, Ctrl from the left.");
        Size = G.Slider("size", "Size", 0.5f, 3f, 0.05f, 1f, "x", help: "Scale over the UI font scale.", changed: QuickbarService.Relayout);
        GapX = G.Slider("gapx", "Spacing across", 0f, 24f, 1f, QuickbarLayout.GapPx, "px", Tier.Advanced, changed: QuickbarService.Relayout,
                        help: "Space between two buttons side by side, before Size scales it.");
        GapY = G.Slider("gapy", "Spacing down", 0f, 24f, 1f, QuickbarLayout.GapPx, "px", Tier.Advanced, changed: QuickbarService.Relayout,
                        help: "Space between two stacked rows of buttons, before Size scales it.");
        Labels = G.Toggle("labels", "Show key labels", true, help: "Each button's key in its corner.");
        Text = new TextStyle(G, "the CX quickbar", sized: false, outline: 1, changed: QuickbarService.Relayout);
        G.Section("CX quickbar keys");
        Keys = new Knob<string>[QuickbarLayout.Sets];
        string[] keyNames = { "keys", "shiftkeys", "ctrlkeys" }, keyLabels = { "Plain keys", "Shift set keys", "Ctrl set keys" };
        for (int set = 0; set < Keys.Length; set++)
            Keys[set] = G.Text(keyNames[set], keyLabels[set], DefaultKeys[set], Tier.Advanced, changed: QuickbarService.KeysChanged,
                               help: "Twelve keys, left to right, space-separated: SDL key names with Ctrl+, Shift+ or Alt+ in front, - for none. \"Bind keys on the bar\" writes these.");
        G.Button("resetkeys", "Reset keys to F1-F12", QuickbarService.ResetKeys, Tier.Advanced, confirm: true,
                 help: "Plain F1..F12, then the same with Shift and with Ctrl, as the game's bar.");
        G.Section("Game's quickbar");
        Align = G.Dropdown("align", "Position", new[] { (int)QbarAlign.Left, (int)QbarAlign.Center, (int)QbarAlign.Right },
                           (int)QbarAlign.Center, new[] { "left", "centre", "right" }, tier: Tier.Advanced,
                           help: "Where the game's quickbar sits along the bottom edge.", changed: QuickbarService.LayoutChanged);
        Bars = G.Toggle("bars", "Black bars", true, Tier.Advanced, help: "Full-width bars beside the game's quickbar and under the chat.",
                        changed: QuickbarService.LayoutChanged);
        G.Section("Compass");
        Compass = G.Toggle("compass", "Show the compass", true, help: "The compass panel of the stock HUD.");
        X = G.Slider("x", "x", 0f, RectMax, 1f, 0f, tier: Tier.Hidden);
        Y = G.Slider("y", "y", 0f, RectMax, 1f, 0f, tier: Tier.Hidden);
        Placed = G.Toggle("placed", "placed", false, Tier.Hidden);
    }

    /// <summary>The alignment <see cref="Align"/> stores.</summary>
    internal static QbarAlign Alignment => (QbarAlign)Align.V;

    /// <summary>The CX bar is what shows: the master and <see cref="Replace"/> on.</summary>
    internal static bool Cx => Enabled.V && Replace.V;
}
