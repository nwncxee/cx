using System;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// The loading hold's pipeline prewarm on Vulkan: every frame the hold is up, each colour-bracket program asks for its
/// pipeline into the target sets <see cref="VkPrewarmSets"/> pairs it with, for every arena format holding a mesh, so the
/// first frame past the loading screen finds them built. It asks for blend 0 alone, which covers every blend mode where
/// blend is dynamic state and only the unblended one where it is baked. It never keeps the hold up.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The target sets a program's batches draw into. A copy of the pairings the issue loop makes at its call
    /// sites, kept honest by the backend's after-the-loading-screen line, which names any material pipeline it missed.
    /// Water and grass are not on it: their pipelines count among the line's others.</summary>
    private static ReadOnlySpan<VkTargets> VkPrewarmSets(VkProg p) => (p.Feat == 0, p.Id, p.Variant) switch
    {
        (true, VkPrograms.Id.Lit or VkPrograms.Id.Fullbright, _) or (false, _, ContentVariant.Forward) =>
            LitSets,
        (true, VkPrograms.Id.LitDeferred, _) or (false, _, ContentVariant.Deferred) => GbufSets,
        (true, VkPrograms.Id.Depth or VkPrograms.Id.DepthKey, _) or (false, _, ContentVariant.Depth) => PrepassSets,
        (true, VkPrograms.Id.DepthFeat, _) => FeatDepthSets,
        (true, VkPrograms.Id.Decal, _) => DecalSets,
        (true, VkPrograms.Id.HighlightMask, _) => MaskSets,
        (true, VkPrograms.Id.Motion, _) => VelocitySets,
        _ => default,
    };

    /// <summary>The sets, made once: the switch above runs every frame the hold is up.</summary>
    private static readonly VkTargets[] LitSets =
        [VkTargets.Colour, VkTargets.ColourRefl, VkTargets.Forward, VkTargets.Oit, VkTargets.Cover, VkTargets.Probe],
        GbufSets = [VkTargets.Colour, VkTargets.ColourRefl], PrepassSets = [VkTargets.Prepass],
        FeatDepthSets = [VkTargets.Prepass, VkTargets.ForwardDepth], DecalSets = [VkTargets.Decal], MaskSets = [VkTargets.Mask],
        VelocitySets = [VkTargets.Velocity];

    /// <summary>Every pipeline the hold should leave built, under the hold: the stock programs, the optional passes'
    /// programs (built here if their pass has not run yet; none of their sized targets) and every content program the
    /// area's batches built.</summary>
    private static void VkPrewarm(VkBackend vk, ExtenderLog log)
    {
        if (!vk.UnderHold) return;
        VkColourState s = _vkc;
        Span<bool> live = stackalloc bool[MeshArena.Formats];
        int formats = 0;
        for (int f = 0; f < MeshArena.Formats; f++) formats += (live[f] = !MeshArena.VkVbo((MeshArena.Fmt)f).IsNull && !s.Layouts[f].IsNull) ? 1 : 0;
        if (formats == 0) return;
        bool rate = vk.ShadingRateTexel > 0;
        foreach (VkProg? p in s.Progs) if (p != null) VkPrewarmProgram(p, live, rate);
        VkDecalState d = VkDecalOwner(vk);
        if (VkDecalWriter(vk, d, log)) VkPrewarmProgram(d.Prog!, live, rate);
        VkMvState mv = VkMvOwner(vk);
        if (VkMvEnsure(vk, mv, log)) VkPrewarmProgram(mv.Prog!, live, rate);
        VkHlState hl = VkHlOwner(vk);
        if (VkHlPrograms(vk, hl, log)) VkPrewarmProgram(hl.Mask!, live, rate);
        foreach (VkProg? p in s.Content.Values) if (p != null) VkPrewarmProgram(p, live, rate);
    }

    /// <summary>Program <paramref name="p"/> into each of its sets, for each live format; the colour sets both rated and
    /// not on a device with a rate image, since a pass binds only the one its own verdict names.</summary>
    private static void VkPrewarmProgram(VkProg p, ReadOnlySpan<bool> live, bool rate)
    {
        ReadOnlySpan<VkTargets> sets = VkPrewarmSets(p);
        for (int f = 0; f < live.Length; f++)
        {
            if (!live[f]) continue;
            foreach (VkTargets t in sets)
            {
                // the coverage pass's sample count is its own, known once it has run
                if (t == VkTargets.Cover && _vkCover.Samples <= 0) continue;
                VkPipeline(p, f, t, 0, rated: false);
                if (rate && t is VkTargets.Colour or VkTargets.ColourRefl) VkPipeline(p, f, t, 0, rated: true);
            }
        }
    }
}
