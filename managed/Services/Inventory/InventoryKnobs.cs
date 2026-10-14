using CX.Framework;
using CX.Ui;

namespace CX.Services.Inventory;

/// <summary>The <c>inventory</c> group: the master (off = the stock panel), the look, size and page count, the
/// window's own typeface and outline (<see cref="Text"/>, Chat-only, its text size following <see cref="Size"/>), and
/// the window position as Hidden state (<see cref="Placed"/> false until the player first drags it, so a fresh install
/// centres). Every row may be held by a server; the overrides no knob exposes are in <c>docs/ui-policy-contract.md</c>.</summary>
internal static class InventoryKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, DragDrop, Search, Kilograms, Placed;
    internal static readonly Knob<string> Mode;
    internal static readonly Knob<float> Size, X, Y;
    internal static readonly TextStyle Text;

    /// <summary>The <see cref="Mode"/> values: vanilla page tabs, or one view scrolling through every page.</summary>
    internal const string ModeTabbed = "tabbed", ModeScrolled = "scrolled";
    private const float RectMax = 16384f;

    static InventoryKnobs()
    {
        G = KnobGroup.Declare("inventory", "Interface", "Game windows", master: "Enable the CX inventory window",
            masterHelp: "The inventory in CX's own window: the vanilla layout, sharp at any resolution, tabs or one view scrolling through every page, search. Off, the stock panel runs.",
            masterDefault: false, masterChanged: InventoryService.MasterChanged, order: 242, title: "Inventory");
        Enabled = G.Master!;
        Size = G.Slider("size", "Size", 0.75f, 3f, 0.05f, 1f, "x",
                        help: "Scale over the UI font scale. The window shrinks itself to fit the screen.", changed: InventoryService.Relayout);
        Mode = G.Dropdown("mode", "Pages", new[] { ModeTabbed, ModeScrolled }, ModeScrolled, new[] { "Tabs", "Scroll" },
                          help: "Tabs is the vanilla layout (a tab per page); Scroll replaces the tabs with a scrollbar that glides through every page, dividers marking where each begins.",
                          changed: InventoryService.Relayout);
        Search = G.Toggle("search", "Search box", true, help: "A box in the title bar that dims every item whose name doesn't match.",
                          changed: InventoryService.Relayout);
        Kilograms = G.Toggle("kilograms", "Kilograms tooltip", false,
                             help: "Hovering the weight shows it in kilograms (1 lb = 0.4536 kg). The window itself keeps the game's pounds.");
        DragDrop = G.Toggle("dropground", "Drag out to drop", true,
                            help: "Releasing a dragged item over the world acts as the stock panel does: on the ground it drops there, on another creature it offers a trade, on you or your associate a potion is drunk. Off: only quickbar drops leave the window.");
        G.Section("Text");
        Text = new TextStyle(G, "the inventory", sized: false, changed: InventoryService.Relayout, tier: Tier.ChatOnly);
        X = G.Slider("x", "x", 0f, RectMax, 1f, 0f, tier: Tier.Hidden);
        Y = G.Slider("y", "y", 0f, RectMax, 1f, 0f, tier: Tier.Hidden);
        Placed = G.Toggle("placed", "placed", false, Tier.Hidden);
    }
}
