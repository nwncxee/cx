using System;
using System.Diagnostics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// The loading hold's pipeline prewarm on Vulkan: every frame the hold is up, each colour-bracket program asks for its
/// pipeline into the sets <see cref="VkPrewarmSets"/> pairs it with, per arena format holding a mesh, and the passes
/// drawn beside them make theirs. Blend 0 alone covers every blend mode where blend is dynamic state and only the
/// unblended one where it is baked. It never keeps the hold up.
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

    /// <summary>Every pipeline the hold should leave built, under the hold: the materials', then those of the passes
    /// that do not draw there -- the probe, which stands down, and CX's windows, which only the cover draws. Grass and
    /// particles draw under the hold, so they build their own.</summary>
    private static void VkPrewarm(VkBackend vk, ExtenderLog log)
    {
        if (!vk.UnderHold) return;
        long since = Stopwatch.GetTimestamp();
        if (!VkPrewarmMaterials(vk, log, since) || VkPrewarmSpent(since)) return;
        // ponytail: the probe's three stages build in one frame, past the budget on a slow compiler; split it if one stalls
        if (EnvProbe.On) VkProbeEnsure(vk, VkProbeOwner(vk), log);
        if (!VkPrewarmSpent(since)) GuiPassVk.PrewarmWindows(vk, since, log);
    }

    /// <summary>The stock programs, the optional passes' programs (built here if their pass has not run yet; none of
    /// their sized targets) and every content program the area's batches built. False once the frame has spent its
    /// budget since <paramref name="since"/>.</summary>
    private static bool VkPrewarmMaterials(VkBackend vk, ExtenderLog log, long since)
    {
        VkColourState s = _vkc;
        Span<bool> live = stackalloc bool[MeshArena.Formats];
        int formats = 0;
        for (int f = 0; f < MeshArena.Formats; f++) formats += (live[f] = !MeshArena.VkVbo((MeshArena.Fmt)f).IsNull && !s.Layouts[f].IsNull) ? 1 : 0;
        if (formats == 0) return true;
        bool rate = vk.ShadingRateTexel > 0;
        foreach (VkProg? p in s.Progs) if (p != null && !VkPrewarmProgram(p, live, rate, since)) return false;
        VkDecalState d = VkDecalOwner(vk);
        if (VkDecalWriter(vk, d, log) && !VkPrewarmProgram(d.Prog!, live, rate, since)) return false;
        VkMvState mv = VkMvOwner(vk);
        if (VkMvEnsure(vk, mv, log) && !VkPrewarmProgram(mv.Prog!, live, rate, since)) return false;
        VkHlState hl = VkHlOwner(vk);
        if (VkHlPrograms(vk, hl, log) && !VkPrewarmProgram(hl.Mask!, live, rate, since)) return false;
        foreach (VkProg? p in s.Content.Values) if (p != null && !VkPrewarmProgram(p, live, rate, since)) return false;
        return true;
    }

    /// <summary>The hold frame has spent <see cref="VkPrewarmBudgetMs"/> since <paramref name="since"/>.</summary>
    internal static bool VkPrewarmSpent(long since) => Stopwatch.GetElapsedTime(since).TotalMilliseconds > VkPrewarmBudgetMs;

    /// <summary>Milliseconds of building one hold frame spends before the rest waits for the next, so a device that
    /// compiles whole pipelines keeps the loading screen drawing.</summary>
    private const double VkPrewarmBudgetMs = 25;

    /// <summary>Program <paramref name="p"/> into each of its sets, for each live format; the colour sets both rated and
    /// not on a device with a rate image, since a pass binds only the one its own verdict names. False once the frame
    /// has spent its budget since <paramref name="since"/>.</summary>
    private static bool VkPrewarmProgram(VkProg p, ReadOnlySpan<bool> live, bool rate, long since)
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
                if (VkPrewarmSpent(since)) return false;
            }
        }
        return true;
    }
}
