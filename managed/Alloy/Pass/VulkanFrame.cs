using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The world frame under <c>alloy.backend vk</c>: Vulkan culls, draws the colour bracket into its own scene
/// targets and encodes them into the composite's back image; the present image flips to it, and the GUI and the
/// engine's present carry on over it as over GL's. Inside the 4.6 bracket.</summary>
internal static class VulkanFrame
{
    private sealed class FrameState
    {
        public CommandList? Cmds;
        public bool Live;
        public int Frames;
    }

    /// <summary>Frames between pipeline-cache writes: each is a no-op unless a pipeline was made since the last.</summary>
    private const int CacheSavePeriod = 600;

    private static readonly FrameState _s = new();

    /// <summary>The frame list's first arena: past the cull's source and sphere uploads in a typical area, so the
    /// growth line is the exception.</summary>
    private const int FrameArenaBytes = 4 << 20;

    /// <summary>Frames between two GPU-time lines.</summary>
    private const int GpuPeriod = 300;

    /// <summary>Render this frame into the back image and present it. A frame that cannot -- no backend, no present
    /// image, a refused submit -- fails Alloy loudly: the screen would otherwise hold a stale front.</summary>
    internal static unsafe void Run(ExtenderLog? log, in Matrix4x4 vp, in Matrix4x4 view, in Matrix4x4 proj,
                                    int* viewport, int* display, float near, float far,
                                    bool lightingOn, bool perspective, int groups)
    {
        // a failed Alloy records nothing more: every submit after it would stage uploads no recording ever commits
        if (log == null || AlloyGate.Failed) return;
        if (PresentTarget.Tex == 0 || AlloyDevice.Vulkan(log) is not VkBackend vk)
        {
            AlloyGate.Fail("the Vulkan frame has no backend or no composite image (logged above)");
            return;
        }
        CommandList cl = _s.Cmds ??= new CommandList("vulkan frame", FrameArenaBytes, log);
        cl.Reset();
        if (!CullPassVk.Record(vk, cl, log))
        {
            AlloyGate.Fail("the Vulkan cull did not record (logged above) -- Alloy has no visibility test");
            return;
        }
        if (!AlloyRenderer.VkRecordFrame(vk, cl, vk.CompositeBack, vp, view, proj, viewport, display, near, far,
                                         lightingOn, perspective, groups, log))
        {
            AlloyGate.Fail("the Vulkan colour bracket did not record (logged above)");
            return;
        }
        vk.TimeNextSubmit = true;
        if (!vk.Submit(cl))
        {
            AlloyGate.Fail("the Vulkan frame's submit was refused (logged above)");
            return;
        }
        AlloyRenderer.VkParityRead(vk);
        CasterPass.VkProbe(vk, log);
        AlloyRenderer.VkFrameTap(vk);
        if (++_s.Frames % CacheSavePeriod == 0) vk.SavePipelineCache();
        if (_s.Frames % GpuPeriod == 0 && vk.TakeGpuMs(out int n) is var ms && n > 0)
            log.Always($"VulkanFrame: [vkgpu] {ms:F2} ms GPU per frame over {n} frames (bake, cull, prepass, hi-Z, AO, "
                     + "decals, G-buffer, opaque, cutout, sky, the deferred dispatches, blended)");
        PresentTarget.Flip(vk.Flip());
        PostChain.Painted();
        VkStoodDown.Note(log);
        if (!_s.Live)
        {
            _s.Live = true;
            log.Always("VulkanFrame: LIVE -- Vulkan bakes the shadow maps, culls, draws the prepass with its vis-ID "
                     + "target, the AO estimate, the decal layer, the G-buffer, the sky and the opaque, cutout and blended "
                     + "batches, and shades the deferred point lights; the engine's context presents the composite");
        }
    }
}
