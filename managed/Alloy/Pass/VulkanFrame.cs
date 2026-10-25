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

    /// <summary>Render this frame into the back image and present it. A frame that cannot -- no backend, no present
    /// image, a refused submit -- fails Alloy loudly: the screen would otherwise hold a stale front.</summary>
    internal static unsafe void Run(ExtenderLog? log, in Matrix4x4 vp, int* viewport, int* display, float near, float far,
                                    bool lightingOn, bool perspective, int groups)
    {
        if (log == null) return;
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
        if (!AlloyRenderer.VkRecordFrame(vk, cl, vk.CompositeBack, vp, viewport, display, near, far, lightingOn, perspective, groups, log))
        {
            AlloyGate.Fail("the Vulkan colour bracket did not record (logged above)");
            return;
        }
        if (!vk.Submit(cl))
        {
            AlloyGate.Fail("the Vulkan frame's submit was refused (logged above)");
            return;
        }
        AlloyRenderer.VkParityRead(vk);
        if (++_s.Frames % CacheSavePeriod == 0) vk.SavePipelineCache();
        PresentTarget.Flip(vk.Flip());
        PostChain.Painted();
        if (!_s.Live)
        {
            _s.Live = true;
            log.Always("VulkanFrame: LIVE -- Vulkan culls, draws the prepass, G-buffer, opaque, cutout and blended batches, "
                     + "and the engine's context presents the composite");
        }
    }
}
