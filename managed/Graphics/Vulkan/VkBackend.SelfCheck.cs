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

    /// <summary>The present mode per swap interval and the ported passes' stages, proven off-device where shaderc loads.
    /// Oracles: the interval-to-mode table written by hand; shaderc's acceptance of the translated text; SPIR-V binding
    /// numbers read by the specification's opcodes against hand-written ones; format tokens from vulkan_core.h by hand.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Mode(0) == VkPresentModeKHR.Immediate && Mode(1) == VkPresentModeKHR.Fifo && Mode(2) == VkPresentModeKHR.Fifo
                  && Mode(-1) == VkPresentModeKHR.FifoRelaxed, "a swap interval did not pick its present mode");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero)
        {
            check.Skip("on-device: the pilot's stages compile at the device's first pipeline, not at boot");
            return;
        }
        check.Expect(Translate("int i = gl_VertexID;") == "int i = gl_VertexIndex;", "GL's vertex index is spelled Vulkan's");
        check.Expect((int)Of(Format.R8) == 9 && (int)Of(Format.Rg16F) == 83 && (int)Of(Format.R32F) == 100
                  && (int)Of(Format.Rgba8) == 37 && (int)Of(Format.Rgba8Srgb) == 43 && (int)Of(Format.Rgba16F) == 97,
            "the seam's formats are vulkan_core.h's R8_UNORM 9, R16G16_SFLOAT 83, R32_SFLOAT 100, R8G8B8A8_UNORM 37, _SRGB 43, R16G16B16A16_SFLOAT 97");
        check.Expect((int)Winding(false) == 1 && (int)Winding(true) == 0,
            "GL's CCW front is vulkan_core.h's FRONT_FACE_CLOCKWISE 1 and its CW COUNTER_CLOCKWISE 0: Vulkan's area carries the opposite sign");
        check.Expect((int)Of(Format.D32F) == 126 && (int)Of(Format.Bc1) == 131 && (int)Of(Format.Bc1A) == 133
                  && (int)Of(Format.Bc3) == 137 && (int)Of(Format.Bc5) == 141 && (int)Of(Format.Bc7) == 145,
            "depth and BC formats are vulkan_core.h's D32_SFLOAT 126, BC1_RGB 131, BC1_RGBA 133, BC3 137, BC5 141, BC7 145");
        check.Expect((int)AttribFormat(new VertexAttrib(4, 4, VertexType.Packed1010102Norm, 0)) == 65
                  && (int)AttribFormat(new VertexAttrib(8, 4, VertexType.U8, 0)) == 39
                  && (int)AttribFormat(new VertexAttrib(5, 4, VertexType.U8Norm, 0)) == 37
                  && (int)AttribFormat(new VertexAttrib(0, 3, VertexType.Float, 0)) == 106,
            "the arena's attribute shapes are A2B10G10R10_SNORM_PACK32 65, R8G8B8A8_USCALED 39, _UNORM 37, R32G32B32_SFLOAT 106");
        var bc1 = new ImageDesc(ImageKind.Tex2D, Format.Bc1, 64, 64, 1, 7, 1, 1, ImageUse.Sampled, "");
        var bc3 = new ImageDesc(ImageKind.Tex2D, Format.Bc3, 5, 3, 1, 1, 1, 1, ImageUse.Sampled, "");
        var rgba = new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 7, 5, 1, 3, 1, 1, ImageUse.Sampled, "");
        check.Expect(LevelBytes(bc1, 0) == 2048 && LevelBytes(bc1, 6) == 8 && LevelBytes(bc3, 0) == 32 && LevelBytes(rgba, 1) == 24,
            "a level's bytes: BC1 64x64 is 16x16 blocks of 8 (2048), its 1x1 mip one block (8); BC3 5x3 is 2x1 blocks of 16 (32); RGBA8 7x5's mip 1 is 3x2 texels (24)");
        bool staged = true;
        foreach (string leaf in VulkanToolchain.Libraries) staged &= VulkanToolchain.Load(leaf);
        if (!staged) { check.Skip("the staged shaderc did not load"); return; }
        Stage(check, "cx_fullscreen_v", ShaderKind.VertexShader, []);
        Stage(check, "cx_post_vs", ShaderKind.VertexShader, []);
        // a uniform block's GL point lands at 48 + point, a storage block's at 64 + point, a sampler on its unit
        Stage(check, "cx_ssao_fs", ShaderKind.FragmentShader, [0, 51]);
        Stage(check, "cx_ssaoblur_fs", ShaderKind.FragmentShader, [0, 1, 51]);
        Stage(check, "cx_cull_c", ShaderKind.ComputeShader, [12, 51, 72, 73, 74, 75, 76, 77, 78]);
        Stage(check, "cx_cullsort_c", ShaderKind.ComputeShader, [51, 72, 74, 75, 77]);
        check.Expect(CX.Alloy.ShaderSource.Get("cx_cull_c.glsl").Contains($"layout(binding = {CX.Alloy.CullPass.PyramidUnit}) uniform sampler2D uPyramid;"),
            "the cull stage's pyramid sampler sits on the unit both backends bind it to");
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
