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
    /// numbers read by the specification's opcodes against hand-written ones; format tokens and each layout's barrier
    /// scope as vulkan_core.h's bits, by hand.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        DepthGateSelfCheck(check);
        string? noSwap = Gate(new VkVersion(1, 3, 0), n => n != "VK_KHR_swapchain");
        check.Expect(noSwap != null && noSwap.Contains("VK_KHR_swapchain", StringComparison.Ordinal),
            $"a Vulkan 1.3 GPU with no swapchain is not refused for it ({noSwap ?? "not refused"})");
        check.Expect(Mode(0) == VkPresentModeKHR.Immediate && Mode(1) == VkPresentModeKHR.Fifo && Mode(2) == VkPresentModeKHR.Fifo
                  && Mode(-1) == VkPresentModeKHR.FifoRelaxed, "a swap interval did not pick its present mode");
        check.Expect(ScopeIs(VkImageLayout.ColorAttachmentOptimal, 0x400, 0x180) && ScopeIs(VkImageLayout.DepthStencilAttachmentOptimal, 0x300, 0x600)
                  && ScopeIs(VkImageLayout.ShaderReadOnlyOptimal, 0x8C8, 0x100000000) && ScopeIs(VkImageLayout.General, 0x8C8, 0x600000000)
                  && ScopeIs(VkImageLayout.TransferSrcOptimal, 0x1000, 0x800) && ScopeIs(VkImageLayout.TransferDstOptimal, 0x1000, 0x1000)
                  && ScopeIs(VkImageLayout.Undefined, 0, 0),
            "a layout's barrier scope: colour = COLOR_ATTACHMENT_OUTPUT 0x400 with its read|write 0x180; depth = EARLY|LATE_FRAGMENT_TESTS "
          + "0x300 with 0x600; sampled = VERTEX|GEOMETRY|FRAGMENT|COMPUTE 0x8C8 with SHADER_SAMPLED_READ 2^32; general = the same "
          + "stages with SHADER_STORAGE_READ|WRITE; transfer = ALL_TRANSFER 0x1000 with TRANSFER_READ 0x800 / WRITE 0x1000; undefined none");
        check.Expect(((ulong)WriteAccess & 0x400001500) == 0x400001500 && ((ulong)WriteAccess & 0x300000A80) == 0,
            "a barrier's source makes the writes available -- SHADER_STORAGE_WRITE 2^34, COLOR_ATTACHMENT_WRITE 0x100, "
          + "DEPTH_STENCIL_ATTACHMENT_WRITE 0x400, TRANSFER_WRITE 0x1000 -- and no read: SHADER_SAMPLED_READ 2^32, SHADER_STORAGE_READ "
          + "2^33, COLOR_ATTACHMENT_READ 0x80, DEPTH_STENCIL_ATTACHMENT_READ 0x200, TRANSFER_READ 0x800");
        check.Expect(UsageMissing(ImageUse.Sampled | ImageUse.ColorTarget, true) != null && UsageMissing(ImageUse.Storage, true) == null
                  && UsageMissing(ImageUse.ColorTarget | ImageUse.Storage, false) != null && UsageMissing(ImageUse.Sampled, false) == null,
            "a bind is refused for the usage its image lacks: a colour target bound as storage, a storage-only image sampled");
        CheckSetCache(check);
        CheckAsyncFamily(check);
        CheckTransferFamily(check);
        CheckToAsync(check);
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

    /// <summary>Oracle: hand-written binding strings and set handles. A set comes back only for the bindings it was
    /// written with -- not for a prefix, a longer string or one field changed -- two strings that share a hash both
    /// stay, and a clear forgets every set.</summary>
    private static void CheckSetCache(Checker check)
    {
        static BindingKey K(uint binding, ulong obj = 7) => new(binding, VkDescriptorType.CombinedImageSampler, VkImageLayout.ShaderReadOnlyOptimal, obj, 3, 0, 0);
        var c = new SetCache();
        BindingKey[] a = [K(1), K(2), K(3), K(4)], b = [K(1), K(2), K(3), K(4, obj: 8)];
        check.Expect(!c.TryGet(a, out _), "an empty cache held a set");
        c.Add(a, 11);
        check.Expect(c.TryGet(a, out ulong got) && got == 11, "the set written with bindings 1-4 did not come back for them");
        check.Expect(!c.TryGet(b, out _), "binding 4 on another view found the set written with the first");
        check.Expect(!c.TryGet(a.AsSpan(..3), out _) && !c.TryGet([.. a, K(5)], out _), "a prefix or an extension of bindings 1-4 found its set");
        c.Add(b, 12);
        c.AddHashed(a.AsSpan(..1), 7, 13);
        c.AddHashed(a.AsSpan(..2), 7, 14);
        check.Expect(c.TryGetHashed(a.AsSpan(..1), 7, out ulong h1) && h1 == 13 && c.TryGetHashed(a.AsSpan(..2), 7, out ulong h2) && h2 == 14,
            "two strings on one hash: one lost its set to the other");
        check.Expect(c.TryGet(a, out got) && got == 11 && c.TryGet(b, out got) && got == 12, "a later add lost an earlier set");
        c.Clear();
        check.Expect(!c.TryGet(a, out _) && !c.TryGet(b, out _) && !c.TryGetHashed(a.AsSpan(..1), 7, out _), "a cleared cache held a set");
        c.Add(b, 21);
        check.Expect(c.TryGet(b, out got) && got == 21 && !c.TryGet(a, out _), "after a clear, a new set did not come back alone");
    }

    /// <summary>Oracle: hand-written family tables shaped as an RTX 3070, an RDNA card and an Intel iGPU report them. The
    /// async queue is a family that computes without drawing; none, or one with no queues, leaves a single queue.</summary>
    private static void CheckAsyncFamily(Checker check)
    {
        static VkQueueFamilyProperties F(VkQueueFlags f, uint n) => new() { queueFlags = f, queueCount = n };
        const VkQueueFlags G = VkQueueFlags.Graphics | VkQueueFlags.Compute | VkQueueFlags.Transfer, C = VkQueueFlags.Compute | VkQueueFlags.Transfer;
        check.Expect(AsyncFamily([F(G, 16), F(VkQueueFlags.Transfer, 2), F(C, 8)]) == 2, "the 3070's compute family 2 was not the async one");
        check.Expect(AsyncFamily([F(G, 1), F(C, 4), F(VkQueueFlags.Transfer, 2)]) == 1, "the RDNA compute family 1 was not the async one");
        check.Expect(AsyncFamily([F(G, 1)]) == -1, "a device with one graphics family named an async one");
        check.Expect(AsyncFamily([F(G, 1), F(C, 0)]) == -1, "a compute family with no queues was taken");
        check.Expect(AsyncFamily([F(G, 1), F(G, 4)]) == -1, "a second graphics family was taken as the compute-only one");
    }

    /// <summary>Oracle: a hand-written RDNA family table, whose compute family (which also copies) comes before its
    /// copy-only one: the transfer queue is the copy-only family, never the async one.</summary>
    private static void CheckTransferFamily(Checker check)
    {
        VkExtent3D texel = new() { width = 1, height = 1, depth = 1 };
        VkQueueFamilyProperties F(VkQueueFlags f) => new() { queueFlags = f, queueCount = 2, minImageTransferGranularity = texel };
        check.Expect(TransferFamily([F(VkQueueFlags.Graphics | VkQueueFlags.Compute | VkQueueFlags.Transfer),
                                     F(VkQueueFlags.Compute | VkQueueFlags.Transfer), F(VkQueueFlags.Transfer)]) == 2,
            "the RDNA copy-only family 2 was not the transfer one");
    }

    /// <summary>Oracle: vulkan_core.h's bits, by hand. A barrier side on the async queue keeps its compute and transfer
    /// stages and only the accesses those make; ALL_COMMANDS keeps everything; a graphics-only side keeps nothing.</summary>
    private static void CheckToAsync(Checker check)
    {
        static (ulong, ulong) Cut(ulong stages, ulong access)
        {
            var s = (VkPipelineStageFlags2)stages;
            var a = (VkAccessFlags2)access;
            ToAsync(ref s, ref a);
            return ((ulong)s, (ulong)a);
        }
        check.Expect(Cut(0x888, 0x100000004) == (0x800, 0x100000000),
            "VERTEX|FRAGMENT|COMPUTE 0x888 with SHADER_SAMPLED_READ 2^32 and VERTEX_ATTRIBUTE_READ 4 is COMPUTE 0x800 with the sampled read alone");
        check.Expect(Cut(0x1080, 0x400001000) == (0x1000, 0x1000),
            "ALL_TRANSFER|FRAGMENT 0x1080 with SHADER_STORAGE_WRITE 2^34 and TRANSFER_WRITE 0x1000 is ALL_TRANSFER with TRANSFER_WRITE alone");
        check.Expect(Cut(0x400, 0x100) == (0, 0), "COLOR_ATTACHMENT_OUTPUT 0x400 with its write 0x100 is nothing on a compute queue");
        check.Expect(Cut(0x10000, 0x18000) == (0x10000, 0x18000), "ALL_COMMANDS 0x10000 with MEMORY_READ|WRITE 0x18000 stays whole");
    }

    private static bool ScopeIs(VkImageLayout l, ulong stages, ulong access)
        => (ulong)Scope(l).Stages == stages && (ulong)Scope(l).Access == access;

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
