using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Off-device: the particle pass's storage binds, its texture sampler and its velocity arm. Oracles: the
    /// storage blocks the translated stages declare (the embedded GLSL), hand-made buffer and sampler ids, the motion
    /// define in our own stage text, and shaderc compiling the velocity stages.</summary>
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

        VkProgramText m = VkPartMotionText();
        check.Expect(m.Error == null, $"the particle velocity stages translate -- {m.Error}");
        check.Expect(m.Vs.Contains("#define CX_MOTION 1\n") && m.Fs.Contains("#define CX_MOTION 1\n")
                  && !t.Vs.Contains("#define CX_MOTION") && !t.Fs.Contains("#define CX_MOTION"),
            "both velocity stages, and neither colour stage, compile the motion arm in");
        var names = new List<string>();
        foreach (VkLoose u in m.Loose) names.Add(u.Name);
        foreach (string term in (string[])["uPrevVP", "uJitter", "uSize", "uView", "uProj", "uAdditive"])
            check.Expect(names.Contains(term), $"the particle velocity stages carry {term}, which the velocity block writes");
        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        if (!staged) { check.Skip("the staged shaderc did not load, so the velocity arm is not compiled here"); return; }
        if (m.Error == null)
        {
            byte[]? vs = VulkanToolchain.Compile(VkBackend.Translate(m.Vs), "particle velocity vs", ShaderKind.VertexShader, out string ve);
            byte[]? fs = VulkanToolchain.Compile(VkBackend.Translate(m.Fs), "particle velocity fs", ShaderKind.FragmentShader, out string fe);
            check.Expect(vs != null && fs != null, $"the particle velocity stages compile for Vulkan -- {(vs == null ? ve : fe)}");
        }
        log.Info(check.Ok ? "AlloyRenderer.VkParticlesSelfCheck: passed" : "AlloyRenderer.VkParticlesSelfCheck: FAILED");
    }
}
