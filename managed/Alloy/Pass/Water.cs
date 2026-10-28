using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>water</c> group (spec §4.3): the storage <see cref="FrameSnapshot"/> reads. The master is
/// ChatOnly (ADR 0030): off means no water anywhere, which is nobody's setting. Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main thread.</summary>
internal static class Water
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Rect;

    static Water()
    {
        G = KnobGroup.Declare("water", "Graphics", "World", master: "Water",
            masterHelp: "Draw water (the stock water shader, reflecting the finished frame). Off, no water is drawn "
                      + "at all: there is no stock water pass left behind it, so this is a cfg-only switch.",
            masterTier: Tier.ChatOnly, order: 346, title: "Water");
        Enabled = G.Master!;
        G.Status = FrameSnapshot.Describe;
        Rect = G.Toggle("rect", "rect", true, Tier.ChatOnly);
    }
}
