using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>The colour and prepass programs on Vulkan, as the text GL compiles for them -- the same stage, the same
/// transforms, the same override and library swaps, sizing tokens and vanilla splices -- translated by
/// <see cref="VkGlsl"/>. Built without GL: Vulkan always takes the bindless path and the sky-visibility unit.</summary>
internal static class VkPrograms
{
    /// <summary>The programs the Vulkan batch pass draws through. <see cref="Id.Decal"/> is not one of
    /// <see cref="All"/>: like GL's, it is optional, built at the decal pass's first frame, and its refusal stands
    /// that pass down rather than failing the frame.</summary>
    internal enum Id { Fullbright, Lit, LitDeferred, Depth, DepthKey, DepthFeat, Decal }

    internal static readonly Id[] All = [Id.Fullbright, Id.Lit, Id.LitDeferred, Id.Depth, Id.DepthKey, Id.DepthFeat];

    /// <summary>The bindings of the samplers these stages declare without a <c>layout(binding)</c>: the units GL's
    /// program builds set by <c>glUniform1i</c> (<c>Programs.LitLocs</c>, <c>Programs.EnsureProgram</c>).</summary>
    internal static readonly IReadOnlyDictionary<string, uint> Units = MakeUnits();

    private static Dictionary<string, uint> MakeUnits()
    {
        var u = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["uTex"] = TextureSlots.Diffuse,
            ["uSunShadow"] = AlloyRenderer.SunUnit,
            ["uSunDynShadow"] = AlloyRenderer.SunDynUnit,
            ["uLocalShadow"] = AlloyRenderer.CubeUnit,
            ["uLocalDynShadow"] = AlloyRenderer.CubeDynUnit,
            ["uProbe"] = EnvProbe.ProbeUnit,
            ["uOitDepth"] = TextureSlots.OitDepth,
            ["uCoverEsm"] = TextureSlots.CoverEsm,
            ["uSsao"] = TextureSlots.Ssao,
            ["uSceneDepth"] = TextureSlots.SsaoDepth,
        };
        for (int i = 0; i < TextureSlots.MapCount; i++) u[TextureSlots.MapSamplers[i]] = TextureSlots.MapBase + (uint)i;
        return u;
    }

    /// <summary>The program's two stages as GL would compile them, before translation.</summary>
    internal static (string Vs, string Fs) GlText(Id id, ExtenderLog? log)
    {
        string what = $"VkPrograms {id}";
        switch (id)
        {
            case Id.Fullbright:
            {
                GlProgram.Texts(AlloyRenderer.ShdFullbrightVs, ShaderStages.Source(AlloyRenderer.ShdFullbrightVs),
                                AlloyRenderer.ShdFullbrightFs, ShaderStages.Source(AlloyRenderer.ShdFullbrightFs),
                                log, what, out string v, out string f);
                return (Final(v, log, what, true), Final(f, log, what, true));
            }
            case Id.Lit or Id.LitDeferred or Id.Decal:
            {
                (string v, string f) = LitTexts(log, what);
                if (id == Id.LitDeferred) f = Programs.WithDeferred(f);
                else if (id == Id.Decal) f = ShaderStages.WithDefine(f, "CX_DECAL_WRITE 1");
                return (Final(v, log, what, false), Final(f, log, what, false));
            }
            case Id.Depth:
                return (Final(AlloyRenderer.DepthVs, log, what, true), Final(AlloyRenderer.DepthFs, log, what, true));
            case Id.DepthKey:
                return (Final(AlloyRenderer.DepthKeyVs, log, what, true), Final(AlloyRenderer.DepthKeyFs, log, what, true));
            default:
            {
                (string v, _) = LitTexts(log, what);
                string f = AlloyRenderer.BindlessFs(AlloyRenderer.DepthFeatFs);
                return (Final(ShaderSource.Content(v, log), log, what, true), Final(ShaderSource.Content(f, log), log, what, true));
            }
        }
    }

    /// <summary>The program translated for Vulkan; <see cref="VkProgramText.Error"/> says why not.</summary>
    internal static VkProgramText Text(Id id, ExtenderLog? log)
    {
        (string v, string f) = GlText(id, log);
        // the device maps GL's -1..1 clip depth itself (VK_EXT_depth_clip_control), so the stages keep GL's clip z
        return VkGlsl.Translate(v, f, Units, glDepth: false);
    }

    /// <summary>Content pair <paramref name="feat"/>'s <paramref name="v"/> program translated for Vulkan, from the text
    /// its GL program compiled (library swaps, then the hosted splice); null when that variant is not built on GL.</summary>
    internal static VkProgramText? ContentText(int feat, ContentVariant v, ExtenderLog? log)
    {
        if (ContentPrograms.Texts(feat, v) is not { } t) return null;
        string what = $"VkPrograms content {feat} {v}";
        return VkGlsl.Translate(Final(GlProgram.Lib(t.Vs, log, what), log, what, true),
                                Final(GlProgram.Lib(t.Fs, log, what), log, what, true), Units, glDepth: false);
    }

    /// <summary>The lit pair after the stage overrides and library swaps, before the splice: what GL's lit build and
    /// its twins start from.</summary>
    private static (string Vs, string Fs) LitTexts(ExtenderLog? log, string what)
    {
        string fs = AlloyRenderer.BindlessFs(ShaderStages.Source(AlloyRenderer.ShdLitFs));
        GlProgram.Texts(AlloyRenderer.ShdLitVs, ShaderStages.Source(AlloyRenderer.ShdLitVs), AlloyRenderer.ShdLitFs, fs,
                        log, what, out string v, out string f);
        return (v, f);
    }

    /// <summary>A stage's compile-time text: the vanilla splice, then the sizing tokens, as GL's compile makes it.</summary>
    private static string Final(string raw, ExtenderLog? log, string what, bool host)
        => ShaderResource.Tokens(VanillaShader.Splice(raw, log, what, true, host));

    /// <summary>Every program here, translated and compiled for Vulkan 1.3, its loose block reflected. Oracles:
    /// shaderc's verdict on each stage; the push block's offsets against the hand-written 0, 4 .. 24 its seven
    /// four-byte members take; the compiled block against the translator's member list, two artifacts.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkPrograms.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) { check.Skip("on-device: the Vulkan pass builds its programs at its first frame"); return; }
        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        if (!staged) { check.Skip("the staged shaderc did not load"); return; }
        var counts = new List<string>();
        foreach (Id id in Enum.GetValues<Id>())
        {
            VkProgramText t = Text(id, log);
            var spirv = new Dictionary<ShaderKind, byte[]>();
            if (!VkGlsl.Compiles(check, $"VkPrograms {id}", t, spirv)) continue;
            counts.Add($"{id} {t.Loose.Count}");
            if (t.Loose.Count > 0)
            {
                string[] names = t.Loose.ConvertAll(l => l.Name).ToArray();
                // a stage that reads no member keeps no block at all; one that keeps it keeps every member, where both declare it
                int kept = 0;
                foreach (ShaderKind k in (ReadOnlySpan<ShaderKind>)[ShaderKind.VertexShader, ShaderKind.FragmentShader])
                    if (VkSpirv.Block(spirv[k], 0, VulkanToolchain.UniformBase + VkGlsl.LooseBinding, names) is { } b)
                        kept += b.Members.Count == names.Length ? 1 : 0;
                check.Expect(kept > 0, $"{id}'s compiled loose block carries every one of the {names.Length} members the translator gathered");
            }
            if (VkSpirv.Push(spirv[ShaderKind.VertexShader], Array.ConvertAll(VkGlsl.Push, p => p.Name)) is { } push)
            {
                int[] want = [0, 4, 8, 12, 16, 20, 24];
                bool at = push.Members.Count == want.Length;
                for (int i = 0; at && i < want.Length; i++)
                    at = push.Members.TryGetValue(VkGlsl.Push[i].Name, out var m) && m.Offset == want[i] && m.Size == 4;
                check.Expect(at && push.Bytes == 32, $"{id}'s push block sits at 0, 4 .. 24, four bytes each, 32 in all");
            }
            else check.Expect(false, $"{id}'s vertex stage declares the push block");
            // every set-0 binding a compiled stage decorates is one the host binds: a missing one is an unwritten
            // descriptor the device reads, which hangs it
            foreach (ShaderKind k in (ReadOnlySpan<ShaderKind>)[ShaderKind.VertexShader, ShaderKind.FragmentShader])
                foreach (uint bound in Bindings(spirv[k]))
                    check.Expect(t.Opaques.Exists(o => o.Set == 0 && Shifted(o) == bound),
                        $"{id}'s {k} declares set 0 binding {bound}, which no opaque the host binds names");
            foreach (VkOpaque a in t.Opaques)
                foreach (VkOpaque b in t.Opaques)
                    check.Expect(!(a.Set == b.Set && a.Binding == b.Binding && a.Kind == b.Kind && a.Type != b.Type),
                        $"{id}: {a.Name} ({a.Type}) and {b.Name} ({b.Type}) share binding {a.Binding} with different types");
        }
        log.Info(check.Ok ? $"VkPrograms.SelfCheck: passed -- loose members {string.Join(", ", counts)}" : "VkPrograms.SelfCheck: FAILED");
    }

    /// <summary>SPIR-V's OpDecorate and its DescriptorSet and Binding decorations, from the specification's tables.</summary>
    private const uint OpDecorate = 71, DecorationDescriptorSet = 34, DecorationBinding = 33;

    /// <summary>An opaque's binding as the toolchain lays the one set out: each kind shifted past the last.</summary>
    private static uint Shifted(VkOpaque o) => o.Kind switch
    {
        VkBindKind.UniformBlock => VulkanToolchain.UniformBase + o.Binding,
        VkBindKind.StorageBlock => VulkanToolchain.StorageBase + o.Binding,
        VkBindKind.StorageImage => VulkanToolchain.ImageBase + o.Binding,
        _ => VulkanToolchain.SampledBase + o.Binding,
    };

    /// <summary>The bindings a module decorates on variables in set 0 (no DescriptorSet decoration means set 0).</summary>
    private static List<uint> Bindings(byte[] spirv)
    {
        ReadOnlySpan<uint> w = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(spirv);
        var binding = new Dictionary<uint, uint>();
        var set = new Dictionary<uint, uint>();
        for (int i = 5; i < w.Length;)
        {
            uint op = w[i] & 0xFFFF, n = w[i] >> 16;
            if (n == 0) break;
            if (op == OpDecorate && n >= 4 && w[i + 2] == DecorationBinding) binding[w[i + 1]] = w[i + 3];
            if (op == OpDecorate && n >= 4 && w[i + 2] == DecorationDescriptorSet) set[w[i + 1]] = w[i + 3];
            i += (int)n;
        }
        var list = new List<uint>();
        foreach (var (id, b) in binding) if (!set.TryGetValue(id, out uint s) || s == 0) list.Add(b);
        return list;
    }
}
