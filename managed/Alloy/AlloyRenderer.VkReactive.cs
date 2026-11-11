using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The reactive mask, written where the effect is: the beam, particle, halo and weather passes carry an R8 target
/// beside the scene colour and each reports the share of the pixel it took (<c>CxReactive</c> in
/// <c>cx_reactive.glsl</c>), accumulating over the four. FSR 3.1 takes it as its reactive input and the TAA resolve
/// floors the current frame's share with it, so an effect's history does not drag along the ground's velocity.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>This frame's reactive mask -- the four passes' accumulated coverage, zero where none of them drew.</summary>
    internal static ImageId VkReactiveMask => _capturing ? default : _vkc.React;

    /// <summary>The mask attachment the four passes draw into beside the scene colour: loaded, because each of them
    /// adds to what the ones before it left.</summary>
    private static ColorAttachment VkReactiveAttach(VkColourState c)
        => new(c.React, 0, 0, LoadOp.Load, StoreOp.Store, default);

    /// <summary>The mask attachment's blend on a pipeline <paramref name="p"/> draws with: coverage over coverage, so
    /// two effects on one pixel land between the larger of them and 1 and neither can take it past 1. A stage
    /// declaring no <c>fragReact</c> -- a content override leaving the library out -- has every channel masked, so
    /// the mask keeps what the passes before it wrote and not an output that stage never assigned.</summary>
    private static AttachmentBlend ReactiveBlend(VkProg p)
        => p.React
            ? new AttachmentBlend(true, BlendFactor.One, BlendFactor.OneMinusSrcColor, BlendFactor.One, BlendFactor.OneMinusSrcColor)
            : new AttachmentBlend(Masked: ColorMask.All);

    /// <summary>The mask back to zero for this frame, before the first pass that writes it. One clear, outside any
    /// pass: the four passes blend into it and none of them covers the whole frame.</summary>
    private static void VkReactiveBegin(CommandList cl, VkColourState c, ExtenderLog log)
    {
        cl.ClearImage(c.React);
        if (c.ReactLive) return;
        c.ReactLive = true;
        log.Always($"AlloyRenderer: the Vulkan reactive mask is LIVE at {c.W}x{c.H} -- the share of each pixel the beam, "
                 + "particle, halo and weather passes took, written where they draw and fed to FSR 3.1 and to the TAA resolve");
    }
}
