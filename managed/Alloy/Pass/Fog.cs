using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>fog</c> group (spec §4.3): the storage <see cref="SceneFog"/> and the cull's fog gate
/// read. The master is live: off, every dial reads as 0 through <see cref="SceneFog"/>'s accessors, which is
/// the area author's flat fog exactly as vanilla draws it. Touched from
/// <see cref="RenderCommands.Register"/> so the group declares at Initialize on the main thread.</summary>
internal static class Fog
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, Origin, Cull;
    internal static readonly Knob<float> Sky, Sun, SunExp, SkyBand, Height, Floor, CullZ;

    static Fog()
    {
        G = KnobGroup.Declare("fog", "Graphics", "World", master: "Fog",
            masterHelp: "Fog that takes the sky's colour, glows toward the sun and thins with height. Off, every dial "
                      + "below reads 0 and the area author's own flat fog colour draws, as the stock game shows it.",
            order: 344, title: "Fog");
        Enabled = G.Master!.Ladder(Quality.Preset, Quality.Classic(false, true));
        G.Status = SceneFog.Describe;

        G.Section("Colour");
        Sky = G.Slider("sky", "Fog takes the sky colour", 0f, 1f, 0.05f, 0.75f,
                       help: "How far the fog colour moves from the area's flat colour toward the sky. 0 is the stock look.");
        Sun = G.Slider("sun", "Sun glow in fog", 0f, 2f, 0.05f, 0.35f, tier: Tier.Advanced, help: "Fog brightens toward the sun.");
        SunExp = G.Slider("sunexp", "Sun glow tightness", 1f, 64f, 1f, 8f, tier: Tier.Advanced,
                          help: "Big = tight glow, small = half-sky wash.");
        SkyBand = G.Slider("skyband", "Horizon band", 0f, 1f, 0.05f, 0.15f, tier: Tier.Advanced,
                           help: "How far above the horizon the sky dome is fogged.");
        G.Section("Density");
        Height = G.Slider("height", "Height falloff", 0f, 1f, 0.005f, 0f, tier: Tier.Advanced,
                          help: "Fog thins with height above the floor; 0 = off.");
        Floor = G.Slider("floor", "Layer floor", 0f, 50f, 1f, 0f, "m", Tier.Advanced,
                         help: "Offset above your feet where thinning starts.");
        G.Section("");
        Origin = G.Toggle("origin", "Measure from the player", true, Tier.ChatOnly,
                          help: "Off = stock view-depth fog (zooming out pushes the fog).");
        Cull = G.Toggle("cull", "Cull fully-fogged geometry", true, Tier.ChatOnly);
        CullZ = G.Slider("cullz", "cullz", 0f, 500f, 1f, 30f, tier: Tier.ChatOnly);
    }
}
