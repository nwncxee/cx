using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>vfog</c> group: the volumetric fog <see cref="FogVolume"/> draws in place of the <c>fog</c>
/// group's ramp. Off by default; the panel shows its switch and keeps every dial under Advanced. Every key is one a
/// server may hold (<c>docs/ui-policy-contract.md</c>). Touched from <see cref="RenderCommands.Register"/>.</summary>
internal static class VolumetricFog
{
    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled, LightShadows;
    internal static readonly Knob<string> Quality;
    internal static readonly Knob<float> Haze, Sun, Lights, Reach, Sky, Aniso, Height, Floor, Noise, NoiseSize, Wind, Top, Temporal;

    /// <summary>The <see cref="Quality"/> values, index-aligned with <see cref="Grids"/>.</summary>
    internal static readonly string[] Levels = { "low", "medium", "high", "ultra" };
    /// <summary>Froxels per axis (screen x, screen y, depth slices) at each <see cref="Levels"/> entry.</summary>
    internal static readonly (int W, int H, int D)[] Grids = { (96, 54, 48), (160, 90, 64), (192, 108, 96), (240, 135, 128) };

    static VolumetricFog()
    {
        G = KnobGroup.Declare("vfog", "Graphics", "World", master: "Volumetric fog", masterDefault: false,
            masterHelp: "Fog as a lit volume: sunlight falls through it in shafts, lamps glow in it and it drifts on the "
                      + "wind, built from the area's own fog colour and distance. Replaces the fog above; costs GPU time.",
            order: 345, title: "Volumetric fog");
        Enabled = G.Master!;
        G.Status = FogVolume.Describe;
        Quality = G.Dropdown("quality", "Fog detail", Levels, "medium", new[] { "Low", "Medium", "High", "Ultra" },
                             tier: Tier.Advanced, help: "How finely the fog volume is divided. Higher shows sharper light shafts and costs more.")
                   .Ladder(CX.Alloy.Quality.Effects, "low", "low", "medium", "high");

        G.Section("Look");
        Haze = G.Slider("haze", "Haze in the air", 0f, 0.9f, 0.01f, 0f, tier: Tier.Advanced,
                        help: "How much of the area's fog hangs in the air close by. More shows stronger shafts and glows; 0 keeps the near air clear.");
        Sun = G.Slider("sun", "Sun shafts", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced,
                       help: "How much of the fog's light falls as sunbeams: more is brighter shafts and darker shade between them. 0 is even fog.");
        Lights = G.Slider("lights", "Lamp glow", 0f, 4f, 0.05f, 0.15f, tier: Tier.Advanced, help: "How brightly torches and lamps glow in the fog.");
        Reach = G.Slider("reach", "Lamp strength by reach", 0f, 1f, 0.05f, 0.1f, tier: Tier.Advanced,
                         help: "How much brighter a lamp that reaches further glows, with a hotter core: 0 makes every lamp glow alike, 1 is twice the reach for four times the light.");
        Sky = G.Slider("sky", "Fog takes the sky colour", 0f, 1f, 0.05f, 0.5f, tier: Tier.Advanced,
                       help: "How far the fog's own light moves from the area's fog colour toward the sky's. 0 is the area's colour.");

        G.Section("Shape");
        Aniso = G.Slider("aniso", "Forward scattering", 0f, 0.9f, 0.05f, 0.4f, tier: Tier.Advanced,
                         help: "How much brighter the fog looks toward a light than away from it.");
        Height = G.Slider("height", "Haze height falloff", 0f, 0.2f, 0.005f, 0.08f, "/m", Tier.Advanced,
                          help: "How fast the haze thins above the ground you stand on; 0 = the same at every height.");
        Floor = G.Slider("floor", "Haze floor", -20f, 50f, 1f, 0f, "m", Tier.Advanced,
                         help: "Height above your feet where the haze starts to thin.");
        Noise = G.Slider("noise", "Patchiness", 0f, 1f, 0.05f, 0.6f, tier: Tier.Advanced,
                         help: "How uneven the haze is; 0 is even haze.");
        NoiseSize = G.Slider("noisesize", "Patch size", 2f, 64f, 1f, 16f, "m", Tier.Advanced, help: "Size of the haze's patches.");
        Wind = G.Slider("wind", "Drift speed", 0f, 8f, 0.1f, 1.5f, "m/s", Tier.Advanced,
                        help: "How fast the patches drift, along the area's wind.");
        Top = G.Slider("top", "Fog bank height", 2f, 100f, 1f, 15f, "m", Tier.Advanced,
                       help: "How far above the camera the fog reaches before it thins away: the sky shows above it, and tall things rise out of it.");

        G.Section("");
        Temporal = G.Slider("temporal", "temporal", 0f, 0.98f, 0.01f, 0.9f, tier: Tier.ChatOnly,
                            help: "Share of the last frames' fog kept each frame: smooths the shafts, 0 shows each frame's own samples.");
        LightShadows = G.Toggle("lightshadows", "Lamp shadows in fog", true, Tier.ChatOnly,
                                help: "Lamps cast their shadows through the fog. Off is cheaper.");
        ServerPolicy.AllowKnobs(G.Name + ".");
    }

    /// <summary>The froxel grid <see cref="Quality"/> names; Medium's for a value not in <see cref="Levels"/>.</summary>
    internal static (int W, int H, int D) Grid
    {
        get { int i = System.Array.IndexOf(Levels, Quality.V); return Grids[i < 0 ? 1 : i]; }
    }
}
