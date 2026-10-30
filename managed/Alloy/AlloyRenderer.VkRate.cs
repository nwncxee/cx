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
        public ShaderId Cs;
        public PipelineId Prog;
        public ImageId Image;
        /// <summary>The render size <see cref="Image"/> covers, in pixels.</summary>
        public int W, H;
    }

    private static VkRateState _vkRate = new();

    /// <summary>The rate image this frame's G-buffer pass reads, sized for a <paramref name="w"/> x <paramref name="h"/>
    /// render and cleared to one invocation per pixel when made; null on a device without one, or when it was refused.</summary>
    private static ImageId VkRateImage(VkBackend vk, CommandList cl, int w, int h, ExtenderLog log)
    {
        if (_vkRate.Dev != vk) _vkRate = new VkRateState { Dev = vk };
        VkRateState s = _vkRate;
        int t = vk.ShadingRateTexel;
        if (t == 0 || s.Failed) return default;
        if (s.Prog.IsNull)
        {
            s.Cs = vk.CreateShader(new ShaderDesc(ShaderStage.Compute, null, ShaderSource.Get("cx_vrs_cs.glsl"), "shading rate"));
            s.Prog = s.Cs.IsNull ? default : vk.CreatePipeline(new PipelineDesc { Cs = s.Cs, Label = "shading rate" });
        }
        if (!s.Image.IsNull && s.W == w && s.H == h) return s.Image;
        vk.Destroy(s.Image);
        s.Image = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8Ui, (w + t - 1) / t, (h + t - 1) / t, 1, 1, 1, 1,
                                               ImageUse.Storage | ImageUse.CopyDst | ImageUse.ShadingRate, "shading rate"));
        if (s.Prog.IsNull || s.Image.IsNull)
        {
            s.Failed = true;
            log.Always("AlloyRenderer: the device REFUSED the shading-rate stage or image (logged above) -- the G-buffer pass shades every pixel");
            return default;
        }
        s.W = w; s.H = h;
        cl.ClearImage(s.Image);
        return s.Image;
    }

    /// <summary>Next frame's rates off <paramref name="scene"/>, this frame's lit colour, when <c>alloy.vrs</c> is on
    /// and the G-buffer pass read a rate image this frame. shortcut: a one-frame lag with no reprojection; a fast
    /// turn shades one frame's edges at the old tiles' rates.</summary>
    private static void VkRatePass(CommandList cl, ImageId scene, bool ran)
    {
        VkRateState s = _vkRate;
        if (!ran || !Renderer.Vrs.V || s.Image.IsNull) return;
        int t = s.Dev!.ShadingRateTexel;
        cl.ReadImages([scene]);
        cl.SetPipeline(s.Prog);
        cl.BindSet(0, new BindSet(default, default, [new ImageBinding(0, scene)], [new ImageBinding(0, s.Image)]));
        cl.PushConstants(0, MemoryMarshal.AsBytes((ReadOnlySpan<int>)[BitConverter.SingleToInt32Bits(Renderer.VrsContrast.V), t]));
        cl.Dispatch((s.W + t - 1) / t, (s.H + t - 1) / t);
    }
}
