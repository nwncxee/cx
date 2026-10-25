using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The world frame under <c>alloy.backend vk</c>: Vulkan renders the composite's back image, the present
/// image flips to it, and the GUI and the engine's present carry on over it as over GL's. The cull is ported; until
/// the passes that draw its lists are, the image is a clear to <see cref="NotPorted"/>. Inside the 4.6 bracket.</summary>
internal static class VulkanFrame
{
    private sealed class FrameState
    {
        public CommandList? Cmds;
        public bool Live;
    }

    private static readonly FrameState _s = new();

    /// <summary>The frame list's first arena: past the cull's source and sphere uploads in a typical area, so the
    /// growth line is the exception.</summary>
    private const int FrameArenaBytes = 4 << 20;

    /// <summary>Dark magenta: the world's passes are not on Vulkan yet, and no engine clear colour looks like it,
    /// so a screen this colour is Vulkan's frame reaching the engine's swap.</summary>
    private static readonly Vector4 NotPorted = new(0.35f, 0f, 0.35f, 1f);

    /// <summary>Render this frame into the back image and present it. A frame that cannot -- no backend, no present
    /// image, a refused submit -- fails Alloy loudly: the screen would otherwise hold a stale front.</summary>
    internal static void Run(ExtenderLog? log)
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
        Span<ColorAttachment> colors = stackalloc ColorAttachment[1];
        colors[0] = new ColorAttachment(vk.CompositeBack, 0, 0, LoadOp.Clear, StoreOp.Store, NotPorted);
        cl.BeginRenderPass(new RenderPassDesc(colors));
        cl.EndRenderPass();
        if (!vk.Submit(cl))
        {
            AlloyGate.Fail("the Vulkan frame's submit was refused (logged above)");
            return;
        }
        PresentTarget.Flip(vk.Flip());
        PostChain.Painted();
        if (!_s.Live)
        {
            _s.Live = true;
            log.Always("VulkanFrame: LIVE -- Vulkan culls, clears the composite to dark magenta and the engine's context "
                     + "presents it; the drawing passes are not on Vulkan yet");
        }
    }
}
