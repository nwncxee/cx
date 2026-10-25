using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Rhi;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>SPIR-V's OpDecorate and its Binding decoration, transcribed from the specification's tables.</summary>
    private const uint OpDecorate = 71, DecorationBinding = 33;

    /// <summary>The pilot's stages as the backend compiles them, proven off-device where shaderc loads. Oracles: shaderc's
    /// own acceptance of the translated text; the binding numbers read back out of the SPIR-V by the specification's
    /// opcodes, against hand-written expectations (sampler N at N, GL's uniform block 3 at 16 + 3); the format tokens
    /// vulkan_core.h and glcorearb.h assign, written by hand.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero)
        {
            check.Skip("on-device: the pilot's stages compile at the device's first pipeline, not at boot");
            return;
        }
        check.Expect(Translate("int i = gl_VertexID;") == "int i = gl_VertexIndex;", "GL's vertex index is spelled Vulkan's");
        check.Expect((int)Of(Format.R8) == 9 && (int)Of(Format.Rg16F) == 83 && (int)Of(Format.R32F) == 100
                  && (int)Of(Format.Rgba8) == 37 && (int)Of(Format.Rgba8Srgb) == 43 && (int)Of(Format.Rgba16F) == 97,
            "the seam's formats are vulkan_core.h's R8_UNORM 9, R16G16_SFLOAT 83, R32_SFLOAT 100, R8G8B8A8_UNORM 37, _SRGB 43, R16G16B16A16_SFLOAT 97");
        check.Expect(Of(Format.D32F) == VkFormat.Undefined, "no depth image is on the Vulkan backend yet");
        check.Expect(GlFormats.FromInternal(0x822F) == Format.Rg16F && GlFormats.FromInternal(0x8CAC) == Format.D32F
                  && GlFormats.FromInternal(0x1234) == Format.Unknown,
            "a GL texture's internal format finds its seam twin: RG16F 0x822F, DEPTH_COMPONENT32F 0x8CAC, nothing for 0x1234");

        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        if (!staged) { check.Skip("the staged shaderc did not load"); return; }
        Stage(check, "cx_fullscreen_v", ShaderKind.VertexShader, []);
        Stage(check, "cx_post_vs", ShaderKind.VertexShader, []);
        Stage(check, "cx_ssao_fs", ShaderKind.FragmentShader, [0, 19]);
        Stage(check, "cx_ssaoblur_fs", ShaderKind.FragmentShader, [0, 1, 19]);
        Stage(check, "cx_ssaotemp_f", ShaderKind.FragmentShader, [0, 1, 2, 3, 19]);
        log.Info(check.Ok ? "VkBackend.SelfCheck: passed" : "VkBackend.SelfCheck: FAILED");
    }

    private static void Stage(Checker check, string name, ShaderKind kind, uint[] bindings)
    {
        string src = Translate(CX.Alloy.ShaderSource.Get(name + ".glsl"));
        byte[]? spirv = VulkanToolchain.Compile(src, name, kind, out string error);
        check.Expect(spirv != null, $"{name} compiles for Vulkan ({error})");
        if (spirv == null) return;
        var got = new SortedSet<uint>();
        ReadOnlySpan<uint> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(spirv);
        for (int i = 5; i < words.Length;)
        {
            uint op = words[i] & 0xFFFF, n = words[i] >> 16;
            if (n == 0) break;
            if (op == OpDecorate && n >= 4 && words[i + 2] == DecorationBinding) got.Add(words[i + 3]);
            i += (int)n;
        }
        check.Expect(got.SetEquals(bindings), $"{name} binds [{string.Join(", ", got)}], expected [{string.Join(", ", bindings)}]");
    }
}
