using CX.Framework;
using CX.Ui;

namespace CX.Services.Radial;

/// <summary>The <c>radial</c> group (ADR 0025): the master that hands right-click menus to CX's dropdown, off by
/// default until the CRadialMenu layout has been probed live -- the row sits on every right-click, and a wrong offset
/// there is a crash, not a glitch. The look rows are read per frame by the draw (swatches are RRGGBBAA;
/// <see cref="Opacity"/> multiplies every alpha); <see cref="Layout"/> is the layout bag <see cref="RadialPins"/> owns.</summary>
internal static class RadialKnobs
{
    internal const string ModeCascade = "cascade", ModeDrill = "drill";
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Lock, Anim;
    internal static readonly Knob<int> Dwell, AnimMs;
    internal static readonly Knob<float> Opacity;
    internal static readonly Knob<string> Mode;
    /// <summary>The menu's own text size / typeface / outline (<c>radial.font</c>, <c>radial.typeface</c>, <c>radial.outline</c>).</summary>
    internal static readonly TextStyle Font;
    internal static readonly KnobBag Layout;

    static RadialKnobs()
    {
        G = KnobGroup.Declare("radial", "Interface", "HUD", master: "Enable the CX radial menu", masterDefault: true,
            masterHelp: "Right-click menus as a nested dropdown drawn by CX. Off, the stock radial menu runs.",
            order: 232, title: "Radial menu",
            header: "Drag a row to reorder it, or drag a deep row onto the top list to keep it there; right-click a row for the same edits, Hide and Reset.");
        Enabled = G.Master!;
        Lock = G.Toggle("lock", "Lock the layout", false, help: "Rows cannot be dragged and right-clicking one opens no edit popup. Turn off to rearrange again.");
        Mode = G.Dropdown("mode", "Submenus", new[] { ModeCascade, ModeDrill }, ModeCascade, new[] { "Cascade beside the row", "Drill down in place" },
            help: "Cascade: a submenu opens beside its row, on hover. Drill down: it replaces the list where it is, on click, with a Back row on top -- the pointer never travels.");
        Dwell = G.Slider("dwell", "Submenu delay", 0, 600, 10, (int)CascadeMenu.DefaultDwellMs, "ms", Tier.Advanced, help: "Cascade only: how long the pointer rests on a row before its submenu opens; 0 opens it at once.");
        G.Section("Look");
        Opacity = G.Slider("opacity", "Opacity", 0.2f, 1f, 0.05f, 1f, help: "The whole menu's opacity. Its colours follow Interface > Look.");
        G.Section("Text");
        Font = new TextStyle(G, "the menu", tier: Tier.ChatOnly);
        G.Section("Animation");
        Anim = G.Toggle("anim", "Animate", true, help: "Lists fade and slide in as they open.");
        AnimMs = G.Slider("animms", "Animation length", 40, 400, 10, 120, "ms", Tier.Advanced, help: "How long a list takes to appear.");
        Layout = G.Bag("p");
    }
}
