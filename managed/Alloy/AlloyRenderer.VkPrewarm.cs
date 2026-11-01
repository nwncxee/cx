using System;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// The loading hold's pipeline prewarm on Vulkan: every frame the hold is up, each colour-bracket program the area can
/// draw with asks for its pipeline into every target set its batches use, for every arena format holding a mesh, so the
/// first frame past the loading screen finds them built. It never keeps the hold up; a pairing it misses shows by name
/// on the backend's after-the-loading-screen line.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The target sets a stock program's batches draw into: the issue loop's own pairings, declared once here.</summary>
    private static ReadOnlySpan<VkTargets> VkPrewarmSets(VkPrograms.Id id) => id switch
    {
        VkPrograms.Id.Lit or VkPrograms.Id.Fullbright => LitSets,
        VkPrograms.Id.LitDeferred => GbufSets,
        VkPrograms.Id.Depth or VkPrograms.Id.DepthKey => PrepassSets,
        VkPrograms.Id.DepthFeat => FeatDepthSets,
        VkPrograms.Id.Decal => DecalSets,
        VkPrograms.Id.HighlightMask => MaskSets,
        VkPrograms.Id.Motion => VelocitySets,
        _ => default,
    };

    /// <summary>A content program's sets, by the variant it was built as.</summary>
    private static ReadOnlySpan<VkTargets> VkPrewarmSets(VkProg p) => p.Feat == 0 ? VkPrewarmSets(p.Id) : p.Variant switch
    {
        ContentVariant.Forward => LitSets,
        ContentVariant.Deferred => GbufSets,
        ContentVariant.Depth => PrepassSets,
        _ => default,
    };

    private static readonly VkTargets[] LitSets =
        [VkTargets.Colour, VkTargets.ColourRefl, VkTargets.Forward, VkTargets.Oit, VkTargets.Cover, VkTargets.Probe];
    private static readonly VkTargets[] GbufSets = [VkTargets.Colour, VkTargets.ColourRefl];
    private static readonly VkTargets[] PrepassSets = [VkTargets.Prepass];
    private static readonly VkTargets[] FeatDepthSets = [VkTargets.Prepass, VkTargets.ForwardDepth];
    private static readonly VkTargets[] DecalSets = [VkTargets.Decal];
    private static readonly VkTargets[] MaskSets = [VkTargets.Mask];
    private static readonly VkTargets[] VelocitySets = [VkTargets.Velocity];

    /// <summary>Every pipeline the hold should leave built, under the hold: the stock programs, the optional passes'
    /// programs (built here if their pass has not run yet) and every content program the area's batches built.</summary>
    private static void VkPrewarm(VkBackend vk, int w, int h, ExtenderLog log)
    {
        if (!LightmapBake.Held) return;
        VkColourState s = _vkc;
        Span<bool> live = stackalloc bool[MeshArena.Formats];
        int formats = 0;
        for (int f = 0; f < MeshArena.Formats; f++) formats += (live[f] = !MeshArena.VkVbo((MeshArena.Fmt)f).IsNull && !s.Layouts[f].IsNull) ? 1 : 0;
        if (formats == 0) return;
        bool rate = vk.ShadingRateTexel > 0;
        foreach (VkProg? p in s.Progs) if (p != null) VkPrewarmProgram(p, live, rate);
        VkDecalState d = _vkDecals;
        if (d.Dev != vk) _vkDecals = d = new VkDecalState { Dev = vk };
        if (VkDecalWriter(vk, d, log)) VkPrewarmProgram(d.Prog!, live, rate);
        VkMvState mv = _vkMv;
        if (mv.Dev != vk) _vkMv = mv = new VkMvState { Dev = vk };
        if (VkMvEnsure(vk, mv, log)) VkPrewarmProgram(mv.Prog!, live, rate);
        VkHlState hl = _vkHl;
        if (hl.Dev != vk) _vkHl = hl = new VkHlState { Dev = vk };
        if (VkHlEnsure(vk, hl, w, h, log)) VkPrewarmProgram(hl.Mask!, live, rate);
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
