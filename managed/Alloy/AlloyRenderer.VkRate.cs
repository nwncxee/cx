using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The G-buffer pass's shading-rate image and the stage that fills it, on one device.</summary>
    private sealed class VkRateState
    {
        public VkBackend? Dev;
        public bool Failed;
        public PipelineId Prog;
        public ImageId Image;
        /// <summary>The render size <see cref="Image"/> covers in pixels, and its size in tiles.</summary>
        public int W, H, Tx, Ty;
    }

    private static VkRateState _vkRate = new();

    /// <summary><c>cx_vrs_cs</c>'s push block.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct VkRatePush
    {
        public float Contrast;
        public int Tile;
    }

    /// <summary>The rate image this frame's G-buffer pass reads, sized for a <paramref name="w"/> x <paramref name="h"/>
    /// render and cleared to one invocation per pixel when made; null on a device without one, or when it was refused.
    /// Made whether <c>alloy.vrs</c> is on or not, so the switch is live and costs no pipeline build.</summary>
    private static ImageId VkRateImage(VkBackend vk, CommandList cl, int w, int h, ExtenderLog log)
    {
        if (_vkRate.Dev != vk) _vkRate = new VkRateState { Dev = vk };
        VkRateState s = _vkRate;
        int t = vk.ShadingRateTexel;
        if (t == 0 || s.Failed) return default;
        if (s.Prog.IsNull) s.Prog = vk.CreateCompute(ShaderSource.Get("cx_vrs_cs.glsl"), "shading rate");
        if (!s.Image.IsNull && s.W == w && s.H == h) return s.Image;
        vk.Destroy(s.Image);
        s.Tx = (w + t - 1) / t;
        s.Ty = (h + t - 1) / t;
        s.Image = s.Prog.IsNull ? default : vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8Ui, s.Tx, s.Ty, 1, 1, 1, 1,
                                               ImageUse.Storage | ImageUse.CopyDst | ImageUse.ShadingRate, "shading rate"));
        if (s.Image.IsNull)
        {
            s.Failed = true;
            log.Always("AlloyRenderer: the device REFUSED the shading-rate stage or image (logged above) -- the G-buffer pass shades every pixel");
            return default;
        }
        s.W = w; s.H = h;
        cl.ClearImage(s.Image);
        return s.Image;
    }

    /// <summary>Next frame's rates off <paramref name="scene"/>, this frame's lit colour, after a G-buffer pass that
    /// shaded at the tile rates. ponytail: a one-frame lag with no reprojection; a fast turn shades one frame's
    /// edges at the old tiles' rates -- reproject the rate image by the frame's motion if that shows.</summary>
    private static void VkRatePass(CommandList cl, ImageId scene, bool rated)
    {
        VkRateState s = _vkRate;
        if (!rated) return;
        var push = new VkRatePush { Contrast = Renderer.VrsContrast.V, Tile = s.Dev!.ShadingRateTexel };
        cl.ReadImages([scene]);
        cl.SetPipeline(s.Prog);
        cl.BindSet(0, new BindSet(default, default, [new ImageBinding(0, scene)], [new ImageBinding(0, s.Image)]));
        cl.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<VkRatePush>(in push)));
        cl.Dispatch(s.Tx, s.Ty);
    }
}
