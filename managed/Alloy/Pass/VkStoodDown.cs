using System;
using System.Text;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// What the Vulkan frame does NOT draw, said out loud: a whole pass with no Vulkan port, and a term a ported pass binds
/// at zero (<see cref="VkParity.StoodDown"/>). Either way a feature the player switched on silently does nothing, which
/// is what the no-silent-fallbacks rule forbids. This names the switched-on ones, and again whenever the set moves -- so
/// turning a feature on mid-session says so rather than doing nothing.
/// </summary>
internal static class VkStoodDown
{
    /// <summary>One frame-graph pass with no Vulkan port: the knob a player switches it on with, and what the Vulkan
    /// frame shows in its place.</summary>
    private readonly record struct Pass(PassId Id, string Feature, string Instead);

    /// <summary>One term a ported pass stands down: the loose uniform <see cref="VkParity.StoodDown"/> names it by, the
    /// knob behind it, and what the stage reads instead.</summary>
    private readonly record struct Term(string Name, string Feature, string Instead);

    /// <summary>A row per unported colour- or shadow-bracket pass. <see cref="SelfCheck"/> holds this list against
    /// <see cref="FrameGraph.Passes"/>, so neither a new pass nor a ported one can drift away from it.</summary>
    private static readonly Pass[] Passes =
    [
        new(PassId.RainCover, "weather (the rain cover map)", "nothing shelters from the rain"),
        new(PassId.SkyMap, "light.sky (the sky-visibility bake)", "the area's one ambient reaches everywhere"),
        new(PassId.CoverageOwned, "alloy.a2c (alpha-to-coverage foliage)", "cutouts take the hard alpha test"),
        new(PassId.GrassOwned, "grass", "no blades over the ground"),
        new(PassId.Reflections, "light.ssr (screen-space reflections)", "surfaces keep the probe alone"),
        new(PassId.ProbeCapture, "light.probe / lightmap (the probe capture and the bake)", "neither is captured"),
    ];

    /// <summary>A row per stood-down term. <see cref="SelfCheck"/> holds this list against
    /// <see cref="VkParity.StoodDown"/>, so the two cannot drift.</summary>
    private static readonly Term[] Terms =
    [
        new("uLmPages", "lightmap (baked static lighting)", "every surface takes the live rig"),
        new("uSkyTerm", "light.sky (the sky-visibility term)", "the area's one ambient reaches everywhere"),
        new("uWeather", "weather (wetness, snow and puddles)", "every surface stays dry"),
        new("uLightRecOn", "light.deferred (the blended class's light record)", "blended parts take the forward walk"),
        new("uCapture", "light.probe (the environment probe and the planar reflection)", "no captured reflection"),
        new("uOit", "alloy.oit (order-free transparency)", "the blended class draws in its sorted order"),
        new("uSplash", "weather (rain impacts on water)", "the water takes no ripples"),
    ];

    /// <summary>Is the feature behind <paramref name="id"/> switched on?</summary>
    private static bool On(PassId id) => id switch
    {
        PassId.RainCover or PassId.WeatherOwned => Weather.Enabled.V,
        PassId.SkyMap => SkyView.Wanted,
        PassId.FogVolume => VolumetricFog.Enabled.V,
        PassId.CoverageOwned => Renderer.A2c.V > 0,
        PassId.GrassOwned => Grass.Enabled.V,
        PassId.Reflections => Lighting.Ssr.V,
        PassId.FbCopy or PassId.WaterOwned => Water.Enabled.V,
        PassId.BeamsOwned => Effects.Beams.V,
        PassId.HalosOwned => Effects.HalosOn,
        PassId.ParticlesOwned => Particles.Enabled.V,
        PassId.ProbeCapture => Lighting.Probe.V || Lightmap.G.Master!.V,
        _ => false,
    };

    /// <summary>Is the feature behind term <paramref name="name"/> switched on?</summary>
    private static bool On(string name) => name switch
    {
        "uLmPages" => Lightmap.G.Master!.V,
        "uSkyTerm" => SkyView.Wanted,
        "uWeather" => Weather.Enabled.V,
        "uLightRecOn" => Lighting.Deferred.V,
        "uCapture" => Lighting.Probe.V,
        "uOit" => Renderer.Oit.V > 0,
        "uSplash" => Weather.Enabled.V,
        _ => false,
    };

    /// <summary>Does the Vulkan frame draw <paramref name="id"/>? The two lists in <see cref="VkParity"/> say so, one
    /// per bracket, and the post chain and stack, which the A/B compares ahead of; every other bracket is the engine's.</summary>
    private static bool Drawn(PassId id, Bracket bracket)
        => bracket == Bracket.Colour ? VkParity.GlPass(id) || id is PassId.PostProcess or PassId.PostStack
                                     : bracket == Bracket.Shadow && VkParity.VkShadow(id);

    /// <summary>The set last said, one bit per row -- passes then terms; -1 = nothing said yet.</summary>
    private sealed class StoodDownState { public long Said = -1; }

    private static readonly StoodDownState _s = new();

    /// <summary>Say what this frame's switched-on features do not reach on Vulkan. Cold in the steady state: a bit per
    /// row is gathered and compared, and nothing is built or logged unless the set moved.</summary>
    internal static void Note(ExtenderLog log)
    {
        long on = 0;
        for (int i = 0; i < Passes.Length; i++)
            if (On(Passes[i].Id)) on |= 1L << i;
        for (int i = 0; i < Terms.Length; i++)
            if (Array.IndexOf(VkParity.StoodDown, Terms[i].Name) >= 0 && On(Terms[i].Name)) on |= 1L << (Passes.Length + i);
        if (on == _s.Said) return;
        _s.Said = on;
        if (on == 0)
        {
            log.Always("VulkanFrame: every switched-on feature is drawn on Vulkan");
            return;
        }
        var sb = new StringBuilder("VulkanFrame: NOT DRAWN on Vulkan, and switched on. No pass runs for: ");
        int n = 0;
        for (int i = 0; i < Passes.Length; i++)
            if ((on & 1L << i) != 0) Say(sb, ref n, Passes[i].Feature, Passes[i].Instead);
        if (n == 0) sb.Append("nothing");
        sb.Append(". Read at zero by the passes that did run: ");
        n = 0;
        for (int i = 0; i < Terms.Length; i++)
            if ((on & 1L << (Passes.Length + i)) != 0) Say(sb, ref n, Terms[i].Feature, Terms[i].Instead);
        if (n == 0) sb.Append("nothing");
        sb.Append(". A feature in both lists is missing twice over; alloy.backend gl draws all of them.");
        log.Always(sb.ToString());
    }

    private static void Say(StringBuilder sb, ref int n, string feature, string instead)
    {
        if (n++ > 0) sb.Append("; ");
        sb.Append(feature).Append(": ").Append(instead);
    }

    /// <summary>Oracles, both second artifacts: <see cref="FrameGraph.Passes"/> -- every colour- or shadow-bracket pass
    /// is drawn on Vulkan or has a row, never both -- and <see cref="VkParity.StoodDown"/>, likewise for the terms. A
    /// port cannot drop its feature from this line by forgetting, nor leave the line claiming it is still missing.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkStoodDown.SelfCheck");
        int rows = 0;
        foreach ((PassId id, Bracket bracket) in FrameGraph.Passes)
        {
            if (bracket is not (Bracket.Colour or Bracket.Shadow)) continue;
            bool row = Array.Exists(Passes, p => p.Id == id);
            if (row) rows++;
            check.Expect(row != Drawn(id, bracket), row
                ? $"{id} has a VkStoodDown row and VkParity says Vulkan draws it -- the line would claim it is missing"
                : $"{id} is not drawn on Vulkan and VkStoodDown has no row saying what is missing");
        }
        check.Expect(rows == Passes.Length, $"{Passes.Length - rows} VkStoodDown pass row(s) name no colour- or "
                                          + "shadow-bracket pass in FrameGraph");
        foreach (string term in VkParity.StoodDown)
            check.Expect(Array.Exists(Terms, t => t.Name == term),
                $"{term} stands down on Vulkan and VkStoodDown has no row saying what is not applied for it");
        foreach (Term t in Terms)
            check.Expect(Array.IndexOf(VkParity.StoodDown, t.Name) >= 0,
                $"VkStoodDown still has a row for {t.Name}, which VkParity no longer stands down");
        log.Info(check.Ok ? $"VkStoodDown.SelfCheck: passed ({Passes.Length} pass rows, {Terms.Length} term rows)"
                          : "VkStoodDown.SelfCheck: FAILED");
    }
}
