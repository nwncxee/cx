using CX.Framework;

namespace CX.Alloy;

/// <summary>The <c>grass</c> group (spec §4.3), and the storage <see cref="GrassField"/> reads: one
/// declaration per dial, bound from <c>cx.cfg</c> on first touch, set by <c>/grass</c> or the panel. The
/// Restart rows are read through <c>Boot</c>. Touched from <see cref="RenderCommands.Register"/> so the group
/// declares at Initialize, on the main thread, whether or not the pass arms.</summary>
internal static class Grass
{
    /// <summary><see cref="Style"/>'s values: procedural blades (default), or the atlas cards.</summary>
    internal const int StyleBlades = 0, StyleTextures = 1;

    internal static readonly KnobGroup G;
    internal static readonly Knob<bool> Enabled;
    internal static readonly Knob<float> Density, Far, Wind, Push, WindSpeed, Lod, LodMin, LodWidth, LodHeight, Shade,
                                         CastWidth, PushRadius, PushExtent, CullShrink, CullMax, Edge, RootAo, GTint,
                                         SnowThin, SnowFall, SnowLook, Backlight, Sheen,
                                         BladeDensity, Width, Clump, BladeLod;
    internal static readonly Knob<int> Style, Shadow, Tuft, GViz;
    internal static readonly Knob<bool> Cull, VLight;

    static Grass()
    {
        G = KnobGroup.Declare("grass", "Graphics", "World", master: "Grass",
            masterHelp: "Grass on the ground: denser than the stock game's, wind-swayed, lit and shadowed. Off, no "
                      + "grass is drawn at all. The game's own Grass option still has to be on either way.",
            order: 340, title: "Grass");
        Enabled = G.Master!;
        G.Status = GrassField.Describe;

        G.Section("Field");
        Style = G.Dropdown("style", "Grass style", new[] { StyleBlades, StyleTextures }, StyleBlades,
                           new[] { "Procedural blades", "Game textures" }, changed: GrassField.Rebake,
                           help: "Procedural blades: dense, individually shaded 3D blades coloured from the area's grass. "
                               + "Game textures: the area's own grass pictures, as the stock game draws them. "
                               + "Grass textures that look like flowers stay as pictures either way.")
                .Ladder(Quality.Preset, Quality.Classic(StyleTextures, StyleBlades));
        Density = G.Slider("density", "Density", 0f, 4f, 0.25f, 2f, help: "Blades per patch relative to the stock game.",
                           changed: GrassField.Rebake)
                   .Ladder(Quality.Grass, 0.75f, 1.25f, 2f, 3f);
        Far = G.Slider("far", "Distance", 0f, 1000f, 10f, 0f, "m",
                       help: "How far grass draws. 0 follows the game's Grass Distance option (which stops at 173).");
        Push = G.Slider("push", "Parts around characters", 0f, 1f, 0.05f, 0.15f,
                        help: "Blades bend away from creatures walking through them. 0 is off.");
        G.Section("Wind");
        Wind = G.Slider("wind", "Wind strength", 0f, 4f, 0.1f, 1f, help: "How far the blades sway. 0 stands them still.");
        G.Section("Detail");
        BladeDensity = G.Slider("bladedensity", "Blade density", 1f, 32f, 1f, 8f, tier: Tier.Advanced, changed: GrassField.Rebake,
                                help: "Procedural blades per stock grass tuft, on top of Density.")
                        .Ladder(Quality.Grass, 3f, 5f, 8f, 12f);
        Lod = G.Slider("lod", "Full-density radius", 5f, 200f, 1f, 40f, "m", Tier.Advanced, help: "Every blade draws inside this.")
               .Ladder(Quality.Grass, 15f, 25f, 40f, 60f);
        BladeLod = G.Slider("bladelod", "Full-detail blade radius", 5f, 200f, 1f, 30f, "m", Tier.Advanced,
                            help: "Procedural blades inside this are smooth curves; past it each is a single triangle.")
                    .Ladder(Quality.Grass, 10f, 20f, 30f, 45f);
        Shade = G.Slider("shade", "Full-shading radius", 5f, 200f, 1f, 40f, "m", Tier.Advanced,
                         help: "Past this blades skip light and shadow taps.")
                 .Ladder(Quality.Grass, 15f, 25f, 40f, 60f);
        Shadow = G.Dropdown("shadow", "Grass casts shadows", new[] { 0, 1, 2, 3, 4 }, 1, new[] { "none", "1", "2", "3", "4" },
                            tier: Tier.Advanced,
                            help: "How many sun cascades the blades are drawn into. More is sharper far away and costlier.")
                  .Ladder(Quality.Grass, 0, 1, 1, 2);
        G.Section("");
        SnowThin = G.Slider("snowthin", "Thinning under snow", 0f, 1f, 0.05f, 0.85f, tier: Tier.ChatOnly,
                            help: "Share of the blades that vanish under full snow cover (the rest stand shorter). 0 keeps a summer meadow.");
        SnowFall = G.Slider("snowfall", "Density while snowing", 0f, 1f, 0.05f, 0.1f, tier: Tier.ChatOnly,
                            help: "Share of the blades that stand while snow is falling, whatever the cover. 1 leaves the field alone.");
        SnowLook = G.Slider("snowlook", "Winter look", 0f, 1f, 0.05f, 1f, tier: Tier.ChatOnly,
                            help: "Blades left standing go straw-coloured and take snow on their tips. 0 keeps them green.");
        WindSpeed = G.Slider("windspeed", "Wind speed", 0f, 0.01f, 0.0001f, 0.001f, tier: Tier.ChatOnly);
        LodMin = G.Slider("lodmin", "Far density floor", 0.001f, 1f, 0.001f, 0.02f, tier: Tier.ChatOnly);
        LodWidth = G.Slider("lodwidth", "Far blade widening", 0f, 1f, 0.05f, 0.5f, tier: Tier.ChatOnly);
        LodHeight = G.Slider("lodheight", "Far blade growth", 0f, 1f, 0.05f, 0.25f, tier: Tier.ChatOnly);
        CastWidth = G.Slider("castwidth", "Shadow blade width", 0.5f, 8f, 0.1f, 1f, tier: Tier.ChatOnly);
        PushRadius = G.Slider("pushradius", "Push radius", 0f, 4f, 0.1f, 1f, tier: Tier.ChatOnly);
        PushExtent = G.Slider("pushextent", "Push field extent", 8f, 128f, 8f, 64f, "m", Tier.ChatOnly);
        Cull = G.Toggle("cull", "Clear grass under objects", true, Tier.ChatOnly);
        CullShrink = G.Slider("cullshrink", "Object footprint scale", 0f, 2f, 0.05f, 0.8f, tier: Tier.ChatOnly);
        CullMax = G.Slider("cullmax", "Footprint cap", 0f, 20f, 0.5f, 0f, "m", Tier.ChatOnly);
        Tuft = G.Slider("tuft", "Blades per tuft", 1, 8, 1, 3, tier: Tier.ChatOnly);
        Edge = G.Slider("edge", "Edge ramp width", 0f, 4f, 0.1f, 2f, tier: Tier.ChatOnly);
        RootAo = G.Slider("rootao", "Root darkening", 0f, 1f, 0.05f, 0f, tier: Tier.ChatOnly);
        GTint = G.Slider("gtint", "Ground tint", 0f, 1f, 0.05f, 0.15f, tier: Tier.ChatOnly,
                         help: "Blade fades into the ground colour under its root.");
        Width = G.Slider("width", "Blade width", 0.02f, 0.4f, 0.01f, 0.05f, tier: Tier.ChatOnly,
                         help: "Procedural blade width at the root, relative to its height.");
        Clump = G.Slider("clump", "Clumping", 0f, 1f, 0.05f, 0.5f, tier: Tier.ChatOnly,
                         help: "Neighbouring procedural blades lean together and share a height, so the field grows in tufts.");
        Backlight = G.Slider("backlight", "Backlit glow", 0f, 2f, 0.05f, 0.5f, tier: Tier.ChatOnly,
                             help: "How much low sun shines through the grass when you look toward it.");
        Sheen = G.Slider("sheen", "Sheen", 0f, 1f, 0.05f, 0.1f, tier: Tier.ChatOnly, help: "Specular glint on the grass.");
        VLight = G.Toggle("vlight", "Per-vertex lighting", true, Tier.ChatOnly, help: "Off = per-pixel (costlier).")
                  .Ladder(Quality.Grass, true, true, true, false);
        GViz = G.Slider("gviz", "gviz", 0, 4, 1, 0, tier: Tier.ChatOnly);
    }
}
