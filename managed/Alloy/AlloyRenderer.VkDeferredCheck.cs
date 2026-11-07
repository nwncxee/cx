using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Every set-0 binding the deferred dispatches may declare, by kind, written out by hand from
    /// <c>cx_deferred_cs</c> and <c>cx_defshadow_cs</c> -- the contract <see cref="VkDefBindSet"/> implements. A
    /// stage that decorates one not listed here is a descriptor the host leaves unwritten.</summary>
    private static readonly (VkBindKind Kind, uint[] Bindings)[] VkDefContract =
    [
        (VkBindKind.UniformBlock, [UboSlots.Pass, UboSlots.ProbeSh, UboSlots.Deferred, VkGlsl.LooseBinding]),
        (VkBindKind.StorageBlock, [SsboSlots.Draw, SsboSlots.DrawIdx, SsboSlots.Lights, SsboSlots.ClusterCount,
                                   SsboSlots.ClusterIdx, SsboSlots.Movers, SsboSlots.Chart,
                                   SsboSlots.DeferredStats]),
        (VkBindKind.Sampled, [TextureSlots.LocalShadow, TextureSlots.Gbuf0, TextureSlots.Gbuf1, TextureSlots.Gbuf2,
                              TextureSlots.GbufDepth, TextureSlots.ShadowVis, TextureSlots.ShadowKey,
                              TextureSlots.LocalShadowDyn, TextureSlots.LocalShadowRaw, TextureSlots.LocalShadowDynRaw,
                              LightmapBake.UnitA, LightmapBake.UnitB, LightmapBake.UnitC]),
        (VkBindKind.StorageImage, [0, 1]),
    ];

    /// <summary>The deferred path's three compute stages translated and compiled off the device. Oracles: shaderc's
    /// own verdict on each text; <see cref="VkDefContract"/>, a hand-written binding list, against what the
    /// translator gathered; the GLSL's own image-format qualifiers against the formats the pass allocates; and the
    /// compiled module's loose block against the translator's member list, a second artifact.</summary>
    internal static void VkDeferredSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VkDeferredSelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) { check.Skip("on-device: the Vulkan deferred path builds its stages at its first frame"); return; }
        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        if (!staged) { check.Skip("the staged shaderc did not load"); return; }

        string def = ShaderStages.Source(DeferredLights.ShdCs), sh = ShaderStages.Source(DeferredShadows.ShdCs);
        check.Expect(def.Contains("layout(rgba16f, binding = 0) uniform image2D uHdr;")
                  && def.Contains("layout(rgba32ui, binding = 1) uniform writeonly uimage2D uLightRec;"),
            "the walk stores into an rgba16f scene colour and an rgba32ui light record, the formats the pass allocates");
        check.Expect(sh.Contains("layout(rgba8, binding = 0) uniform writeonly image2D uOutVis;")
                  && sh.Contains("layout(rg32ui, binding = 1) uniform writeonly uimage2D uOutKey;"),
            "the record writer stores into an rgba8 visibility lane and an rg32ui key, the formats the pass allocates");

        foreach ((string Ref, string? Define, string Label) s in (ReadOnlySpan<(string, string?, string)>)
                 [(DeferredLights.ShdCs, null, "deferred walk"), (DeferredLights.ShdCs, "CX_FILM_PASS 1", "deferred film"),
                  (DeferredShadows.ShdCs, null, "deferred shadow record"),
                  (MoverDebt.ShdCs, null, "mover debt"), (MoverDebt.ApplyCs, null, "mover debt apply")])
        {
            if (VkDefText(s.Ref, s.Define, s.Label, log) is not ({ } t, { } src)) { check.Expect(false, $"{s.Label}: its record contract holds"); continue; }
            if (t.Error != null) { check.Expect(false, $"{s.Label} translates -- {t.Error}"); continue; }
            byte[]? spirv = VulkanToolchain.Compile(VkBackend.Translate(t.Cs), s.Label, ShaderKind.ComputeShader, out string e);
            check.Expect(spirv != null, $"{s.Label} compiles for Vulkan -- {e}");
            if (spirv == null) continue;
            foreach (VkOpaque o in t.Opaques)
            {
                if (o.Set != 0) continue;
                uint[]? listed = Array.Find(VkDefContract, r => r.Kind == o.Kind).Bindings;
                check.Expect(listed != null && Array.IndexOf(listed, o.Binding) >= 0,
                    $"{s.Label}: {o.Name} declares {o.Kind} binding {o.Binding}, which VkDefBindSet has no row for");
            }
            // the apply stage walks no light, so the lighting libraries' loose terms are not its to carry
            if (s.Ref == MoverDebt.ApplyCs) continue;
            var names = new List<string>(t.Loose.Count);
            foreach (VkLoose u in t.Loose) names.Add(u.Name);
            var block = VkSpirv.Block(spirv, 0, VulkanToolchain.UniformBase + VkGlsl.LooseBinding, names);
            check.Expect(block is { } b && b.Members.Count == names.Count,
                $"{s.Label}'s compiled loose block carries every one of the {names.Count} members the translator gathered");
            foreach (string term in (ReadOnlySpan<string>)["uLocalCubes", "uMaterial", "uLightOcc", "uLmMovers"])
                check.Expect(names.Contains(term), $"{s.Label}'s loose block carries {term}, which the per-frame fill writes");
            if (s.Ref == DeferredShadows.ShdCs)
                foreach (string term in (ReadOnlySpan<string>)["uRect", "uInvVp", "uProj", "uShSize", "uShRes"])
                    check.Expect(names.Contains(term), $"{s.Label}'s loose block carries {term}, which the per-frame fill writes");
            else
                check.Expect(t.Opaques.Exists(o => o.Set == 0 && o.Kind == VkBindKind.UniformBlock && o.Binding == UboSlots.Deferred),
                    $"{s.Label} reads the CxDeferred block the pass stages");
        }
        CX.Rhi.BufferId lights = new(1), counts = new(2), idx = new(3), movers = new(4);
        check.Expect(VkLitTable(SsboSlots.Lights, false, lights, counts, idx, movers) == lights
                  && VkLitTable(SsboSlots.ClusterCount, false, lights, counts, idx, movers) == counts
                  && VkLitTable(SsboSlots.ClusterIdx, false, lights, counts, idx, movers) == idx
                  && VkLitTable(SsboSlots.Movers, false, lights, counts, idx, movers) == movers,
                     "a forward-lit stage of the world frame walks the frame's lights and froxel table");
        check.Expect(VkLitTable(SsboSlots.Lights, true, lights, counts, idx, movers) == null
                  && VkLitTable(SsboSlots.ClusterCount, false, lights, default, idx, movers) == null
                  && VkLitTable(SsboSlots.Draw, false, lights, counts, idx, movers) == null,
                     "a capture, a frame with no table and every other binding keep the stand-in");
        log.Info(check.Ok ? "AlloyRenderer.VkDeferredSelfCheck: passed" : "AlloyRenderer.VkDeferredSelfCheck: FAILED");
    }
}
