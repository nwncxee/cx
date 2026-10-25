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

    /// <summary>The composite hand-over and the ported passes' stages, proven off-device where shaderc loads. Oracles: a
    /// hand-written semaphore schedule for the hand-over; shaderc's acceptance of the translated text; SPIR-V binding
    /// numbers read by the specification's opcodes against hand-written ones; the format tokens vulkan_core.h and
    /// glcorearb.h assign, written by hand.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        HandoverSchedule(log);
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
        Stage(check, "cx_cull_c", ShaderKind.ComputeShader, [12, 19, 40, 41, 42, 43, 44, 45, 46]);
        Stage(check, "cx_cullsort_c", ShaderKind.ComputeShader, [19, 40, 42, 43, 45]);
        check.Expect(CX.Alloy.ShaderSource.Get("cx_cull_c.glsl").Contains($"layout(binding = {CX.Alloy.CullPass.PyramidUnit}) uniform sampler2D uPyramid;"),
            "the cull stage's pyramid sampler sits on the unit both backends bind it to");
        log.Info(check.Ok ? "VkBackend.SelfCheck: passed" : "VkBackend.SelfCheck: FAILED");
    }

    /// <summary>One composite image over a fresh session, a frame, a flip and a second take: GL signals once per take
    /// that Vulkan waits on, never twice before a wait, and never on an image Vulkan already holds a release for.</summary>
    private static void HandoverSchedule(ExtenderLog log)
    {
        var check = new Checker(log);
        var h = new Handover();
        check.Expect(h.Take(), "a fresh image is GL's: the first take has GL signal its release first");
        h.Returned();
        check.Expect(h.Flip(), "the frame after: the image leaves the screen and GL releases it");
        check.Expect(!h.Flip(), "a second flip of a released image signals nothing -- a binary semaphore takes one signal per wait");
        check.Expect(!h.Take(), "the take after a flip waits on that release and has GL signal nothing more");
        h.Returned();
        check.Expect(h.Take(), "a take with GL holding the image again (a second submit, no flip) has GL signal first");
        log.Info(check.Ok ? "VkBackend.HandoverSchedule: passed" : "VkBackend.HandoverSchedule: FAILED");
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
