using CX.Framework;
using CX.Ui;

namespace CX.Services.Dialog;

/// <summary>The <c>dialog</c> group, read by <see cref="DialogService"/>: the master (off = the stock panel from
/// the next conversation node on, since it is live per node), the User rows, the Advanced fade and
/// <see cref="Text"/> rows (the window's own size / typeface / outline), and the rect as Hidden state, with
/// <see cref="Placed"/> false until a first move or resize so a fresh install takes the auto layout.</summary>
internal static class DialogKnobs
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Lock, Justify, Anim, Placed;
    internal static readonly Knob<int> MaxHeight, FadeIn, FadeOut;
    internal static readonly Knob<float> X, Y, W;
    internal static readonly TextStyle Text;

    /// <summary>Breakpoint range (px): the content height past which the window stops stretching and scrolls.</summary>
    internal const int MaxHMin = 120, MaxHMax = 800;
    /// <summary>Rect coordinates are screen px; the window clamps itself on-screen at every layout.</summary>
    private const float RectMax = 16384f;

    static DialogKnobs()
    {
        G = KnobGroup.Declare("dialog", "Interface", "Game windows", master: "Enable the CX dialog window",
            masterHelp: "Conversations in CX's own window: resizable, scrollable, portrait, animated. Off -- or while the HUD is hidden, as in a cutscene -- the stock dialog panel runs.",
            order: 240, title: "Dialog");
        Enabled = G.Master!;
        Lock = G.Toggle("lock", "Lock position and size", false, help: "The window can't be dragged or resized.");
        MaxHeight = G.Slider("maxheight", "Height before scrolling", MaxHMin, MaxHMax, 10, 320, "px",
                             help: "The window grows with the text up to this height, then scrolls. The resize grip sets it too.");
        Justify = G.Toggle("justify", "Justify text", false, help: "Stretch lines to the window width. Off is ragged right.",
                           changed: DialogService.Relayout);
        Anim = G.Toggle("anim", "Animations", true, help: "Text fades in and out between nodes. Off, it snaps.");
        FadeIn = G.Slider("fadein", "Fade in", 0, 500, 10, 180, "ms", Tier.Advanced);
        FadeOut = G.Slider("fadeout", "Fade out", 0, 500, 10, 140, "ms", Tier.Advanced);
        G.Section("Text");
        Text = new TextStyle(G, "the dialog window", changed: DialogService.Relayout, tier: Tier.Advanced);
        X = G.Slider("x", "x", 0f, RectMax, 1f, 0f, tier: Tier.Hidden);
        Y = G.Slider("y", "y", 0f, RectMax, 1f, 0f, tier: Tier.Hidden);
        W = G.Slider("w", "w", 0f, RectMax, 1f, 0f, tier: Tier.Hidden);
        Placed = G.Toggle("placed", "placed", false, Tier.Hidden);
    }
}
