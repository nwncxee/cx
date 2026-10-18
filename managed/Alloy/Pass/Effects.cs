using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>effects</c> group (spec §4.3): the sky, beam and halo passes' storage, one group because
/// each is one toggle. The halo toggle and its gain are ONE row (ADR 0030) -- <see cref="Halos"/> is the
/// brightness and 0 is the pass off -- because "halos on, at zero brightness" is a state no player means.</summary>
internal static class Effects
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Sky, Beams;
    /// <summary>Halo brightness, and the pass's switch: 0 draws none.</summary>
    internal static readonly Knob<float> Halos, HaloSize;
    internal static readonly Knob<int> HaloViz;

    /// <summary>Is the halo pass drawing at all?</summary>
    internal static bool HalosOn => Halos.V > 0f;

    static Effects()
    {
        G = KnobGroup.Declare("effects", "Graphics", "Lighting", order: 318, title: "Effects");
        G.Status = static () => $"{SkyBox.Stats()} | {CX.Alloy.Beams.Stats()} | halos {CX.Alloy.Halos.Describe()}";

        Halos = G.Slider("halos", "Light halos", 0f, 4f, 0.1f, 1f,
                         help: "Brightness of the glow around the lights that flare in vanilla. 1 is vanilla's; 0 draws no halos.")
                 .Ladder(Quality.Effects, 0f, 1f, 1f, 1f);
        G.Section("");
        Sky = G.Toggle("sky", "Alloy sky", true, Tier.ChatOnly,
                       help: "The sky dome drawn through CX (fogged horizon, HDR). Off, no sky dome is drawn.");
        Beams = G.Toggle("beams", "Alloy light beams", true, Tier.ChatOnly, help: "Light shafts drawn through CX.");
        HaloSize = G.Slider("halosize", "Halo size", 0f, 2f, 0.1f, 1f, tier: Tier.ChatOnly,
                            help: "Size of the glow as a multiple of vanilla's flare size (1 is vanilla's).");
        HaloViz = G.Slider("haloviz", "haloviz", 0, 1, 1, 0, tier: Tier.ChatOnly);
    }
}
