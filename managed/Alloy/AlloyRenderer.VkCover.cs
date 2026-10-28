using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The alpha-to-coverage layer (<see cref="PassId.CoverageOwned"/>) on Vulkan: <see cref="CoverageLayer"/>'s run --
/// every sample seeded from the lit frame and its depth, the <c>NWN_ALPHA_MODE 2</c> tail drawn forward with coverage
/// from its alpha, then the box-resolved colour and the half-covered depth written back -- scissored to the tail's rect.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The multisampled pair, the three full-screen programs and their pipelines on one device.</summary>
    private sealed class VkCoverState
    {
        public VkBackend? Dev;
        public ImageId Col, Dep;
        public int W, H, Samples, Idling;
        public GuiVkProg? Seed, Resolve, DepthBack;
        public PipelineId SeedPipe, ResolvePipe, DepthPipe;
        public int USamples = -1;
        public bool Tried, Live;
        /// <summary>This frame's run: armed, and its viewport-space rectangle.</summary>
        public bool Armed;
        public int Rx, Ry, Rw, Rh;
    }

    private static VkCoverState _vkCover = new();

    /// <summary>Does the coverage tail wait for the layer this frame? GL's gate -- a tail, no capture, the layer armed --
    /// with the targets made at the render size. False leaves the tail in the cutout pass, cut.</summary>
    private static bool VkCoverArm(VkBackend vk, in Matrix4x4 vp, int* viewport, ExtenderLog log)
    {
        VkCoverState c = _vkCover;
        if (c.Dev != vk) _vkCover = c = new VkCoverState { Dev = vk };
        c.Armed = false;
        if (_capturing) return false;
        if (_gA2cStart >= _gOpaqueEnd || !CoverageLayer.Armed)
        {
            if (!c.Col.IsNull && ++c.Idling >= CoverIdleFrames) VkCoverRelease(vk, c);
            return false;
        }
        if (!VkCoverEnsure(vk, c, viewport[2], viewport[3], log)) return false;
        Matrix4x4 m = vp;
        CoverageRect(&m, viewport, out c.Rx, out c.Ry, out c.Rw, out c.Rh);
        c.Rx -= viewport[0]; c.Ry -= viewport[1];
        c.Idling = 0;
        c.Armed = c.Rw > 0 && c.Rh > 0;
        return true;
    }

    /// <summary>Frames with no coverage tail before the multisampled pair goes, as GL's layer frees its own.</summary>
    private const int CoverIdleFrames = 600;

    /// <summary>The run, after the deferred walk lit the frame it seeds from: seed and tail in one multisampled pass,
    /// then the resolve and the depth write-back into the frame's own pair. False only when the uniform arena is full.</summary>
    private static bool VkCoverPass(CommandList cl, VkColourState s, in Matrix4x4 vp, int* viewport, float near, float far,
                                    bool indirect, ExtenderLog log)
    {
        VkCoverState c = _vkCover;
        if (!c.Armed) return true;
        int w = viewport[2], h = viewport[3];
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = s.ArenaUsed;
        VkPassStart(side, w, h);
        side.SetScissor(c.Rx, c.Ry, c.Rw, c.Rh);
        if (!VkCoverDraw(side, s, c.Seed!, c.SeedPipe, s.Hdr, s.Depth, 0)) return false;
        cl.ReadImages([s.Hdr, s.Depth]);
        VkHandOverReads(cl, s);
        var b = new VkBound { Fmt = -1, Tex = -1 };
        var cover = new VkRange(false, true, false, false, VkTargets.Cover, false, VkDecalFold);
        if (!VkIssue(side, ref b, _gA2cStart, _gOpaqueEnd, cover, vp, viewport, near, far, indirect)) return false;
        ReadOnlySpan<ColorAttachment> ms = [new(c.Col, 0, 0, LoadOp.DontCare, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(ms, new DepthAttachment(c.Dep, 0, 0, LoadOp.DontCare, StoreOp.Store, 1f)));

        side.Reset();
        arena0 = s.ArenaUsed;
        side.SetViewport(0, 0, w, h);
        side.SetScissor(c.Rx, c.Ry, c.Rw, c.Rh);
        if (!VkCoverDraw(side, s, c.Resolve!, c.ResolvePipe, c.Col, default, 0)) return false;
        if (!VkCoverDraw(side, s, c.DepthBack!, c.DepthPipe, c.Dep, default, c.Samples)) return false;
        cl.ReadImages([c.Col, c.Dep]);
        ReadOnlySpan<ColorAttachment> frame = [new(s.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(frame, new DepthAttachment(s.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));
        CoverageLayer.CountVk(c.Samples, (long)c.Rw * c.Rh, (long)w * h);
        if (!c.Live)
        {
            c.Live = true;
            log.Always($"AlloyRenderer: the Vulkan coverage layer is LIVE -- {c.Samples}x at {w}x{h}, seeded from the lit frame, "
                     + "box-resolved with the half-covered depth written back"
                     + (Renderer.A2cMask.V ? "; alloy.a2cmask's shaped mask is not on Vulkan, the hardware's coverage draws it" : ""));
        }
        return true;
    }

    /// <summary>One full-screen triangle of <paramref name="p"/>: its samplers bound to <paramref name="a"/> then
    /// <paramref name="b"/> in declaration order, its <c>uSamples</c> set to <paramref name="samples"/>.</summary>
    private static bool VkCoverDraw(CommandList side, VkColourState s, GuiVkProg p, PipelineId pipe, ImageId a, ImageId b,
                                    int samples)
    {
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        if (p == _vkCover.DepthBack) LooseBlock.Set(_vkCover.USamples, samples);
        LooseBlock.EndCapture();
        int at = p.Block.Length == 0 ? 0 : VkArena(p.Block);
        if (at < 0) return false;
        Span<BufferBinding> ubo = stackalloc BufferBinding[1];
        Span<ImageBinding> img = stackalloc ImageBinding[2];
        int nu = 0, ni = 0;
        foreach (VkOpaque op in p.Opaques)
        {
            if (op.Set != 0) continue;
            if (op.Kind == VkBindKind.UniformBlock && nu < ubo.Length) ubo[nu++] = new BufferBinding(op.Binding, s.Arena, at, p.Block.Length);
            else if (op.Kind == VkBindKind.Sampled && ni < img.Length) { img[ni] = new ImageBinding(op.Binding, ni == 0 ? a : b, s.Raw); ni++; }
        }
        side.SetPipeline(pipe);
        side.BindSet(0, new BindSet(ubo[..nu], default, img[..ni], default));
        side.Draw(3);
        return true;
    }

    /// <summary>The programs once per device, and the pair (with the seed's pipeline, which bakes the sample count) at
    /// the render size and the knob's count. A refusal is said once and the tail draws cut in the cutout pass.</summary>
    private static bool VkCoverEnsure(VkBackend vk, VkCoverState c, int w, int h, ExtenderLog log)
    {
        if (!c.Tried)
        {
            c.Tried = true;
            c.Seed = PostChainVk.Program(vk, PostChain.FullscreenVs, CoverageLayer.SeedFs, "a2c seed", _ => { }, log);
            c.Resolve = PostChainVk.Program(vk, PostChain.FullscreenVs, CoverageLayer.ResolveFs, "a2c resolve", _ => { }, log);
            c.DepthBack = PostChainVk.Program(vk, PostChain.FullscreenVs, CoverageLayer.DepthFs, "a2c depth", q => c.USamples = q.Loc("uSamples"), log);
            if (c.Seed != null && c.Resolve != null && c.DepthBack != null)
            {
                c.ResolvePipe = VkCoverPipe(vk, c.Resolve, 1, new DepthState(), ColorMask.None, "vk a2c resolve");
                c.DepthPipe = VkCoverPipe(vk, c.DepthBack, 1, new DepthState(true, true, CompareOp.Less), ColorMask.All, "vk a2c depth");
            }
            if (c.ResolvePipe.IsNull || c.DepthPipe.IsNull)
            {
                c.Seed = null;
                log.Always("AlloyRenderer: the Vulkan coverage layer's programs were REFUSED (logged above) -- NWN_ALPHA_MODE 2 "
                         + "parts draw as cutouts on Vulkan this session");
            }
        }
        if (c.Seed == null) return false;
        int samples = Renderer.A2c.V;
        while (samples > vk.Caps.MaxSamples && samples > 2) samples >>= 1;
        if (samples > vk.Caps.MaxSamples)
        {
            c.Seed = null;
            log.Always($"AlloyRenderer: the Vulkan device has no multisampled targets (max {vk.Caps.MaxSamples}) -- NWN_ALPHA_MODE 2 "
                     + "parts draw as cutouts on Vulkan this session");
            return false;
        }
        if (!c.Col.IsNull && c.W == w && c.H == h && c.Samples == samples) return true;
        VkCoverRelease(vk, c);
        c.Col = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, samples,
                                             ImageUse.Sampled | ImageUse.ColorTarget, "vk a2c colour"));
        c.Dep = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.D32F, w, h, 1, 1, 1, samples,
                                             ImageUse.Sampled | ImageUse.DepthTarget, "vk a2c depth"));
        c.SeedPipe = VkCoverPipe(vk, c.Seed, samples, new DepthState(true, true, CompareOp.Always), ColorMask.None, "vk a2c seed");
        if (c.Col.IsNull || c.Dep.IsNull || c.SeedPipe.IsNull)
        {
            VkCoverRelease(vk, c);
            c.Seed = null;
            log.Always($"AlloyRenderer: the Vulkan coverage layer's {samples}x {w}x{h} pair was REFUSED (logged above) -- "
                     + "NWN_ALPHA_MODE 2 parts draw as cutouts on Vulkan this session");
            return false;
        }
        c.W = w; c.H = h; c.Samples = samples;
        log.Info($"AlloyRenderer: the Vulkan coverage layer is {samples}x RGBA16F + D32F at {w}x{h} "
               + $"({(long)w * h * samples * 12 / (1024 * 1024)} MB)");
        return true;
    }

    /// <summary>A full-screen pipeline into the frame's colour-and-depth shape at <paramref name="samples"/>.</summary>
    private static PipelineId VkCoverPipe(VkBackend vk, GuiVkProg p, int samples, DepthState depth, ColorMask masked, string label)
    {
        var d = new PipelineDesc { Vs = p.Vs, Fs = p.Fs, Depth = depth, Raster = new RasterState(CullMode.None), Label = label };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = Format.Rgba16F;
        d.Targets.Depth = Format.D32F;
        d.Targets.Samples = samples;
        d.Blend[0] = new AttachmentBlend(Masked: masked);
        return vk.CreatePipeline(in d);
    }

    private static void VkCoverRelease(VkBackend vk, VkCoverState c)
    {
        vk.Destroy(c.Col); vk.Destroy(c.Dep); vk.Destroy(c.SeedPipe);
        c.Col = c.Dep = default; c.SeedPipe = default;
        c.W = c.H = c.Samples = c.Idling = 0;
    }
}
