using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>water</c> group (spec §4.3): the storage <see cref="FrameSnapshot"/> reads. The master is
/// ChatOnly (ADR 0030): off means no water anywhere, which is nobody's setting. Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main thread.</summary>
internal static class Water
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Rect;
    internal static readonly Knob<float> Clarity, Shore, Ripples, Shine, Surfaces;

    static Water()
    {
        G = KnobGroup.Declare("water", "Graphics", "World", master: "Water",
            masterHelp: "Draw water (the stock water shader, reflecting the finished frame). Off, no water is drawn "
                      + "at all: there is no stock water pass left behind it, so this is a cfg-only switch.",
            masterTier: Tier.ChatOnly, order: 346, title: "Water");
        Enabled = G.Master!;
        G.Status = FrameSnapshot.Describe;
        Rect = G.Toggle("rect", "rect", true, Tier.ChatOnly);
        Surfaces = G.Slider("surfaces", "Water on painted water", 0f, 1f, 0.05f, 1f, tier: Tier.Advanced,
                            help: "Ponds and streams that the game draws as a flat painted texture -- a rippling water texture, or "
                                + "one named as water -- get a water surface over the paint: moving ripples, reflections and the "
                                + "rain's rings. 0 leaves them as painted.");
        Clarity = G.Slider("clarity", "Water clarity", 0f, 1f, 0.05f, 0.6f, tier: Tier.Advanced,
                           help: "Water is clear where it is shallow, showing its bed, and takes on its own colour as it deepens. "
                               + "0 is the stock look, where water whose texture is solid shows no bed at all.");
        Shore = G.Slider("shore", "Soft shorelines and foam", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced,
                         help: "The water's edge fades into the bank instead of ending in a hard line, with a thin broken foam "
                             + "line along it. 0 is the stock hard edge.");
        Ripples = G.Slider("ripples", "Fine ripples", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced,
                           help: "Small moving ripples over the stock waves, so still water in a calm area still moves and "
                               + "catches the light. 0 is the stock surface.");
        Shine = G.Slider("shine", "Reflection strength", 0f, 1f, 0.05f, 0.35f, tier: Tier.Advanced,
                         help: "How strongly water reflects what is around it when seen head-on. Real water reflects little "
                             + "straight down and almost everything at a glancing angle; 0 is that physical amount alone.");
    }
}
