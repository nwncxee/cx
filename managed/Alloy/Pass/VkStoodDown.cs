using System;
using System.Text;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// What the Vulkan frame does NOT draw, said out loud. Every term in <see cref="VkParity.StoodDown"/> is a feature the
/// player can switch on and whose pass Vulkan has no port of yet; the stage then reads the term's zero and falls back,
/// which is silent at every one of its sources. This names the switched-on ones once, and again only if the set changes
/// -- so turning a feature on mid-session says so rather than doing nothing.
/// </summary>
internal static class VkStoodDown
{
    /// <summary>One stood-down term: the loose uniform <see cref="VkParity.StoodDown"/> names it by, the knob a player
    /// switches it on with, and what the Vulkan frame draws in its place.</summary>
    private readonly record struct Term(string Name, string Feature, string Instead);

    /// <summary>A row per term. A term with no row fails <see cref="SelfCheck"/>, so the two lists cannot drift.</summary>
    private static readonly Term[] Terms =
    [
        new("uLmPages", "lightmap (baked static lighting)", "every surface takes the live rig"),
        new("uSkyTerm", "light.sky (the sky-visibility term)", "the area's one ambient reaches everywhere"),
        new("uWeather", "weather (wetness, snow and puddles)", "every surface stays dry"),
        new("uVFog", "vfog (volumetric fog)", "every stage fogs on the distance ramp"),
        new("uLightRecOn", "light.deferred (the blended class's light record)", "blended parts take the forward walk"),
        new("uCapture", "light.probe (the environment probe and the planar reflection)", "no captured reflection"),
        new("uOit", "alloy.oit (order-free transparency)", "the blended class draws in its sorted order"),
    ];

    /// <summary>Is the feature behind <paramref name="name"/> switched on? A term with no row reads as off: the
    /// self-check is what refuses one, not a line every frame.</summary>
    private static bool On(string name) => name switch
    {
        "uLmPages" => Lightmap.G.Master!.V,
        "uSkyTerm" => SkyView.Wanted,
        "uWeather" => Weather.Enabled.V,
        "uVFog" => VolumetricFog.Enabled.V,
        "uLightRecOn" => Lighting.Deferred.V,
        "uCapture" => Lighting.Probe.V,
        "uOit" => Renderer.Oit.V > 0,
        _ => false,
    };

    /// <summary>The set last said, as one bit per <see cref="Terms"/> row; -1 = nothing said yet.</summary>
    private sealed class StoodDownState { public long Said = -1; }

    private static readonly StoodDownState _s = new();

    /// <summary>Say what this frame's switched-on features do not reach on Vulkan. Cold in the steady state: a bit per
    /// row is gathered and compared, and nothing is built or logged unless the set moved.</summary>
    internal static void Note(ExtenderLog log)
    {
        long on = 0;
        for (int i = 0; i < Terms.Length; i++)
            if (Array.IndexOf(VkParity.StoodDown, Terms[i].Name) >= 0 && On(Terms[i].Name)) on |= 1L << i;
        if (on == _s.Said) return;
        _s.Said = on;
        if (on == 0)
        {
            log.Always("VulkanFrame: every switched-on feature is drawn on Vulkan");
            return;
        }
        var sb = new StringBuilder("VulkanFrame: NOT DRAWN on Vulkan -- ");
        for (int i = 0, n = 0; i < Terms.Length; i++)
        {
            if ((on & 1L << i) == 0) continue;
            if (n++ > 0) sb.Append("; ");
            sb.Append(Terms[i].Feature).Append(": ").Append(Terms[i].Instead);
        }
        sb.Append(". These are switched on and the Vulkan frame has no port of their passes yet, so each stage reads the "
                + "term at zero; alloy.backend gl draws them.");
        log.Always(sb.ToString());
    }

    /// <summary>Oracle: <see cref="VkParity.StoodDown"/>, a second artifact -- every term it lists has a row here, so a
    /// port that leaves a term standing cannot drop it from this line by forgetting, and every row names a live knob.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkStoodDown.SelfCheck");
        foreach (string term in VkParity.StoodDown)
            check.Expect(Array.Exists(Terms, t => t.Name == term),
                $"{term} stands down on Vulkan and VkStoodDown has no row saying what is not drawn for it");
        foreach (Term t in Terms)
            check.Expect(t.Feature.Length > 0 && t.Instead.Length > 0, $"{t.Name}'s row names a feature and what runs instead");
        log.Info(check.Ok ? $"VkStoodDown.SelfCheck: passed ({VkParity.StoodDown.Length} terms, {Terms.Length} rows)"
                          : "VkStoodDown.SelfCheck: FAILED");
    }
}
