using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>ao</c> group (spec §4.3): the storage <see cref="SsaoPass"/> reads. No master -- the
/// game's own Ambient Occlusion video option is the switch; <see cref="On"/> is the chat-only bypass for an
/// A/B. <c>radius</c> is world-space (NWN units are metres), so the 0.6 default is contact occlusion rather
/// than room-scale gloom, and <c>direct</c> 1 is what makes the effect visible in a lamp-lit interior.</summary>
internal static class Ao
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<float> Radius, Strength, Direct, Cutout, MaxPx;
    internal static readonly Knob<int> Dirs, Steps;
    internal static readonly Knob<bool> Blur, On;

    static Ao()
    {
        G = KnobGroup.Declare("ao", "Graphics", "Lighting", order: 314, title: "Ambient occlusion",
            header: "The game's own Ambient Occlusion video option turns this on and off.");
        G.Status = SsaoPass.Describe;

        Radius = G.Slider("radius", "Radius", 0.1f, 2f, 0.05f, 0.6f, "m",
                          help: "How far occlusion reaches: 0.6 darkens corners and feet; larger is a room-wide gloom.")
                  .Ladder(Quality.Ao, 0.5f, 0.6f, 0.6f, 0.8f);
        Strength = G.Slider("strength", "Strength", 0.5f, 4f, 0.1f, 2.2f,
                            help: "Multiplied by the game's AO intensity slider.");
        Dirs = G.Slider("dirs", "Slices", 1, 8, 1, 1, tier: Tier.Advanced,
                        help: "Directions each pixel searches for nearby surfaces. More is smoother and slower.")
                .Ladder(Quality.Ao, 1, 1, 1, 2);
        Steps = G.Slider("steps", "Steps", 1, 8, 1, 3, tier: Tier.Advanced,
                         help: "Samples along each direction. More reaches further out accurately, and costs that much more.")
                 .Ladder(Quality.Ao, 2, 2, 3, 4);
        Direct = G.Slider("direct", "Darken lit surfaces", 0f, 1f, 0.05f, 1f, tier: Tier.ChatOnly,
                          help: "0 = ambient only (invisible indoors).");
        Cutout = G.Slider("cutout", "On cutout surfaces", 0f, 1f, 0.05f, 1f, tier: Tier.ChatOnly,
                          help: "How much occlusion leaf and grass cards take: 1 = full (a card stamps its silhouette on the card behind it).");
        MaxPx = G.Slider("maxpx", "Screen radius cap", 16f, 256f, 8f, 96f, "px", Tier.ChatOnly)
                 .Ladder(Quality.Ao, 48f, 64f, 96f, 128f);
        Blur = G.Toggle("blur", "Blur", true, Tier.ChatOnly);
        On = G.Toggle("on", "on", true, Tier.ChatOnly);
    }
}
