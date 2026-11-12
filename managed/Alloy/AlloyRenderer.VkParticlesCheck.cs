using System.Collections.Generic;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Off-device: the particle pass's storage binds and texture sampler. Oracles: the storage blocks the
    /// translated particle stages declare (the embedded GLSL, a second artifact) and hand-made buffer and sampler ids.</summary>
    internal static void VkParticlesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VkParticlesSelfCheck");
        BufferId recs = new(1), idx = new(2), lights = new(3), counts = new(4), clIdx = new(5), movers = new(6);

        VkProgramText t = VkPartText();
        check.Expect(t.Error == null, $"the particle stages translate -- {t.Error}");
        var light = new List<uint>();
        foreach (VkOpaque o in t.Opaques)
        {
            if (o.Set != 0 || o.Kind != VkBindKind.StorageBlock) continue;
            check.Expect(VkPartStorage(o.Binding, false, recs, idx, lights, counts, clIdx, movers) != null,
                $"the particle stages declare storage {o.Name} at {o.Binding}, which the pass leaves on the zero stand-in");
            if (o.Binding is SsboSlots.Lights or SsboSlots.ClusterCount or SsboSlots.ClusterIdx) light.Add(o.Binding);
        }
        check.Expect(light.Count == 3, $"the lit vertex stage walks the light table and both froxel lists, found {light.Count}");
        check.Expect(VkPartStorage(8, false, recs, idx, lights, counts, clIdx, movers) == lights
                  && VkPartStorage(9, false, recs, idx, lights, counts, clIdx, movers) == counts
                  && VkPartStorage(10, false, recs, idx, lights, counts, clIdx, movers) == clIdx,
            "storage 8, 9 and 10 carry the frame's lights, froxel counts and froxel indices");
        check.Expect(VkPartStorage(8, true, recs, idx, lights, counts, clIdx, movers) == null,
            "a capture's view is not the one binned, so its lit particles keep the stand-in");

        SamplerId pass = new(7), own = new(8);
        check.Expect(VkPartSampler(false, pass, own) == own,
            "a particle texture is read through its own sampler, so its mip levels and wrap mode reach the draw");
        check.Expect(VkPartSampler(true, pass, own) == pass, "particles.sampler on reads every one through the pass's clamp sampler");
        check.Expect(VkPartSampler(false, pass, default) == pass, "the stand-in, which has no sampler of its own, takes the pass's");

        log.Info(check.Ok ? "AlloyRenderer.VkParticlesSelfCheck: passed" : "AlloyRenderer.VkParticlesSelfCheck: FAILED");
    }
}
