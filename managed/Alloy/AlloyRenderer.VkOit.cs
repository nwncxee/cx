using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Order-independent transparency on Vulkan: <see cref="OitPass"/>'s weighted pair -- the blended class unsorted into
/// it, one composite over the scene colour -- then the batches that sat it out and the depth the class lays.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The pair and the resolve on one device.</summary>
    private sealed class VkOitState
    {
        public VkBackend? Dev;
        public ImageId Accum, Reveal;
        public int W, H;
        public GuiVkProg? Resolve;
        /// <summary>The composite's blend, and the plain one a <c>/alloy oitviz</c> view draws with.</summary>
        public PipelineId Blended, Plain;
        public int Viz = -1, Mode = -1, Net = -1;
        public bool Tried, Live;
    }

    private static VkOitState _vkOit = new();

    /// <summary>Does the blended range go through the pair this frame? GL's gate -- something blended, no capture,
    /// <see cref="OitPass.OrderFree"/> -- and the pair and resolve made at the render size.</summary>
    private static bool VkOitArm(VkBackend vk, int w, int h, ExtenderLog log)
    {
        VkOitState o = _vkOit;
        if (o.Dev != vk) _vkOit = o = new VkOitState { Dev = vk };
        if (_gBlendEnd <= _gOpaqueEnd || _capturing || !OitPass.OrderFree) return false;
        if (!o.Tried)
        {
            o.Tried = true;
            o.Resolve = PostChainVk.Program(vk, PostChain.FullscreenVs, OitPass.OitResolveFs, "oit resolve", q =>
            {
                o.Viz = q.Loc("uViz"); o.Mode = q.Loc("uMode"); o.Net = q.Loc("uNet");
            }, log);
            if (o.Resolve != null)
            {
                o.Blended = VkOitPipe(vk, o.Resolve, true);
                o.Plain = VkOitPipe(vk, o.Resolve, false);
            }
            if (o.Blended.IsNull || o.Plain.IsNull)
            {
                o.Resolve = null;
                log.Always("AlloyRenderer: the Vulkan OIT resolve was REFUSED (logged above) -- the blended class draws in "
                         + "its route's order on Vulkan this session, which is not back to front");
            }
            else if (OitPass.Mode == 2)
                log.Always("AlloyRenderer: alloy.oit 2's per-pixel lists are not on Vulkan -- the blended class composites "
                         + "through the weighted pair (mode 1), and a faded object's parts thicken it");
        }
        if (o.Resolve == null) return false;
        if (o.Accum.IsNull || o.W != w || o.H != h)
        {
            vk.Destroy(o.Accum); vk.Destroy(o.Reveal);
            // 32-bit accumulation: stacked emissive layers overflow fp16 to inf, as OitPass measured on GL
            o.Accum = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32F, w, h, 1, 1, 1, 1,
                                                   ImageUse.Sampled | ImageUse.ColorTarget, "vk oit accum"));
            o.Reveal = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R16F, w, h, 1, 1, 1, 1,
                                                    ImageUse.Sampled | ImageUse.ColorTarget, "vk oit reveal"));
            o.W = w; o.H = h;
        }
        return !o.Accum.IsNull && !o.Reveal.IsNull;
    }

    /// <summary>The resolve into the scene colour: weighted mean under ONE_MINUS_SRC_ALPHA / SRC_ALPHA, or unblended.</summary>
    private static PipelineId VkOitPipe(VkBackend vk, GuiVkProg p, bool blend)
    {
        var d = new PipelineDesc { Vs = p.Vs, Fs = p.Fs, Label = blend ? "vk oit resolve" : "vk oit resolve viz" };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = Format.Rgba16F;
        d.Targets.Samples = 1;
        if (blend)
            d.Blend[0] = new AttachmentBlend(true, BlendFactor.OneMinusSrcAlpha, BlendFactor.SrcAlpha,
                                             BlendFactor.OneMinusSrcAlpha, BlendFactor.SrcAlpha);
        return vk.CreatePipeline(in d);
    }

    /// <summary>The accumulation's own pass: the pair cleared to (0, 1), the scene depth tested and not written.</summary>
    private static void VkOitFlush(CommandList cl, CommandList side, VkColourState s, int arena0)
    {
        VkOitState o = _vkOit;
        ReadOnlySpan<ColorAttachment> pair =
        [
            new(o.Accum, 0, 0, LoadOp.Clear, StoreOp.Store, Vector4.Zero),
            new(o.Reveal, 0, 0, LoadOp.Clear, StoreOp.Store, Vector4.One),
        ];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(pair, new DepthAttachment(s.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));
    }

    /// <summary>The composite over the scene colour, in a pass of its own. False only when the uniform arena is full.</summary>
    private static bool VkOitResolve(CommandList cl, CommandList side, VkColourState s, int w, int h, ExtenderLog log)
    {
        VkOitState o = _vkOit;
        GuiVkProg p = o.Resolve!;
        side.Reset();
        int arena0 = s.ArenaUsed;
        Array.Clear(p.Block);
        VkUniformShim.Capture(p.Members, p.Block);
        VkUniformShim.Set(o.Viz, OitPass.Viz);
        VkUniformShim.Set(o.Mode, 1);
        // the pair is written every frame, so the lists' overflow net has nothing to say
        VkUniformShim.Set(o.Net, 1);
        VkUniformShim.EndCapture();
        int at = p.Block.Length == 0 ? 0 : VkArena(p.Block);
        if (at < 0) return false;
        Span<BufferBinding> ubo = stackalloc BufferBinding[1];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[4];
        Span<ImageBinding> img = stackalloc ImageBinding[4];
        int nu = 0, ns = 0, ni = 0;
        foreach (VkOpaque op in p.Opaques)
        {
            if (op.Set != 0) continue;
            if (op.Kind == VkBindKind.UniformBlock && nu < ubo.Length) ubo[nu++] = new BufferBinding(op.Binding, s.Arena, at, p.Block.Length);
            else if (op.Kind == VkBindKind.StorageBlock && ns < ssbo.Length) ssbo[ns++] = new BufferBinding(op.Binding, s.ZeroSsbo);
            else if (op.Kind == VkBindKind.Sampled && ni < img.Length)
                img[ni++] = new ImageBinding(op.Binding, op.Name == "uAccum" ? o.Accum : o.Reveal, s.Linear);
        }
        side.SetPipeline(OitPass.Viz == 0 ? o.Blended : o.Plain);
        side.SetViewport(0, 0, w, h);
        side.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], img[..ni], default));
        side.Draw(3);
        ReadOnlySpan<ImageId> read = [o.Accum, o.Reveal];
        cl.ReadImages(read);
        ReadOnlySpan<ColorAttachment> colour = [new(s.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colour));
        if (!o.Live)
        {
            o.Live = true;
            log.Always($"AlloyRenderer: the Vulkan OIT is LIVE at {w}x{h} -- the blended class unsorted into the weighted "
                     + $"pair, composited once; {s.OitOut} batch(es) sat it out and drew after, then the class's depth");
        }
        return true;
    }

    /// <summary>The depth the blended class lays once the composite is down, as GL's sweep lays it: the colour phase's
    /// draws through the depth-feature program, colour masked, inside the forward pass.</summary>
    private static void VkBlendDepth(CommandList side, ref VkBound b, in Matrix4x4 vp, int* viewport, float near, float far,
                                     bool indirect)
    {
        if (!_blendDepth || !_prepassFeat) return;
        VkColourState s = _vkc;
        VkProg p = s.Progs[(int)VkPrograms.Id.DepthFeat]!;
        BufferId slots = indirect ? CullPassVk.Slots(CullPassVk.PhaseColour) : s.Slots;
        var depth = new DepthState(true, true, CompareOp.LessEqual);
        var push = new VkBatchPush();
        for (int g = _gOpaqueEnd; g < _gBlendEnd; g++)
        {
            if (!BlendLaysDepth(g)) continue;
            push.AlphaCut = AlloyClasses.Discard(_gCls[g]);
            VkBatch(side, ref b, p, g, VkTargets.ForwardDepth, 0, depth, VkRaster(g, false), slots, 0, ref push, vp, viewport,
                    near, far, CullPassVk.PhaseColour, indirect);
        }
    }
}
