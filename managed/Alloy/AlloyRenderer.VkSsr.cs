using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The screen-space reflections (<see cref="PassId.Reflections"/>) on Vulkan: the colour pass writes <see cref="Ssr"/>'s
/// record pair beside the scene colour, then after the deferred walk the hi-Z pyramid and the colour copy's mips are
/// built by compute, the half-res trace runs, and the composite adds the result on. No probe or mirror is on Vulkan,
/// so the trace takes back no probe term and a miss adds nothing.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The pass's programs and targets on one device, replaced whole with the device.</summary>
    private sealed class VkSsrState
    {
        public VkBackend? Dev;
        public VkDefStage? HiZ, Mip;
        public GuiVkProg? Trace, Comp;
        public bool Tried, Live, Armed;
        public ImageId RecN, RecW, Pyr, Col, Out;
        public int W, H, Levels, ColLevels, ColShift, Ow, Oh;
        public SamplerId Point, Trilinear;
        public PipelineId TracePipe, CompPipe;
        public int HLevel = -1, HNf = -1, MLevel = -1;
        public int TProj = -1, TInvProj = -1, TView = -1, TInvView = -1, TNf = -1, TSsr = -1, TViz = -1, TPlane = -1, TColShift = -1;
        public int CCap = -1, CViz = -1;
    }

    private static VkSsrState _vkSsr = new();

    /// <summary>Ahead of the colour pass: the programs and targets at the frame's size, armed when the reflections are
    /// on and the pass writes the G-buffer -- the record pair rides that attachment set. False = no records this frame.</summary>
    private static bool VkSsrArm(VkBackend vk, int w, int h, bool gbuf, int viz, ExtenderLog log)
    {
        VkSsrState s = _vkSsr;
        if (s.Dev != vk) _vkSsr = s = new VkSsrState { Dev = vk };
        s.Armed = false;
        if (!gbuf || viz != 0 || _capturing || !Lighting.Ssr.V || !VkSsrEnsure(vk, s, log)) return false;
        int shift = Math.Min(Lighting.SsrDetail.V, 1);
        if (!VkSsrTargets(vk, s, w, h, shift, log)) return false;
        s.Armed = true;
        return true;
    }

    /// <summary>The record pair's two colour attachments, cleared: a zero view depth is "no record".</summary>
    private static void VkSsrAttachments(Span<ColorAttachment> colors)
    {
        VkSsrState s = _vkSsr;
        colors[SsrRecFirst] = new ColorAttachment(s.RecN, 0, 0, LoadOp.Clear, StoreOp.Store, default);
        colors[SsrRecFirst + 1] = new ColorAttachment(s.RecW, 0, 0, LoadOp.Clear, StoreOp.Store, default);
    }

    /// <summary>After the deferred walk: the pyramid, the colour copy's chain, the trace and the composite into the scene
    /// colour. Nothing unless <see cref="VkSsrArm"/> armed this frame.</summary>
    private static void VkSsrRun(CommandList cl, in Matrix4x4 view, in Matrix4x4 proj, int* viewport, float near, float far,
                                 ExtenderLog log)
    {
        VkSsrState s = _vkSsr;
        if (!s.Armed) return;
        s.Armed = false;
        if (!Matrix4x4.Invert(proj, out Matrix4x4 invProj) || !Matrix4x4.Invert(view, out Matrix4x4 invView)) return;
        VkColourState c = _vkc;
        VkDefStage hz = s.HiZ!, mp = s.Mip!;
        int arena0 = c.ArenaUsed;
        Span<int> hzAt = stackalloc int[s.Levels];
        Span<int> mpAt = stackalloc int[s.ColLevels];
        for (int i = 0; i < s.Levels; i++)
        {
            Array.Clear(hz.Block);
            VkUniformShim.Capture(hz.Members, hz.Block);
            VkUniformShim.Set(s.HLevel, i);
            VkUniformShim.Set(s.HNf, [near, far]);
            VkUniformShim.EndCapture();
            if ((hzAt[i] = VkArena(hz.Block)) < 0) return;
        }
        for (int i = 0; i < s.ColLevels; i++)
        {
            Array.Clear(mp.Block);
            VkUniformShim.Capture(mp.Members, mp.Block);
            VkUniformShim.Set(s.MLevel, i);
            VkUniformShim.EndCapture();
            if ((mpAt[i] = VkArena(mp.Block)) < 0) return;
        }
        GuiVkProg tr = s.Trace!, cp = s.Comp!;
        int fine = Lighting.SsrDetail.V;
        Array.Clear(tr.Block);
        VkUniformShim.Capture(tr.Members, tr.Block);
        VkUniformShim.Set(s.TProj, proj);
        VkUniformShim.Set(s.TInvProj, invProj);
        VkUniformShim.Set(s.TView, view);
        VkUniformShim.Set(s.TInvView, invView);
        VkUniformShim.Set(s.TNf, [near, far]);
        VkUniformShim.Set(s.TSsr, [Lighting.SsrRough.V, Ssr.Steps[fine], 0f, fine]);
        VkUniformShim.Set(s.TViz, 0);
        VkUniformShim.Set(s.TColShift, s.ColShift);
        VkUniformShim.Set(s.TPlane, [0f, 0f, 0f, 0f]);
        VkUniformShim.EndCapture();
        int trAt = VkArena(tr.Block);
        Array.Clear(cp.Block);
        VkUniformShim.Capture(cp.Members, cp.Block);
        VkUniformShim.Set(s.CCap, Lighting.SsrRough.V);
        VkUniformShim.Set(s.CViz, 0);
        VkUniformShim.EndCapture();
        int cpAt = VkArena(cp.Block);
        if (trAt < 0 || cpAt < 0) return;
        int len = (c.ArenaUsed - arena0 + 3) & ~3;
        cl.UpdateBuffer(c.Arena, arena0, c.ArenaBytes.AsSpan(arena0, Math.Min(len, ArenaCap - arena0)));

        cl.SetPipeline(hz.Pipe);
        for (int i = 0; i < s.Levels; i++)
        {
            int lw = Math.Max(s.W >> i, 1), lh = Math.Max(s.H >> i, 1);
            VkSsrCompute(cl, hz, hzAt[i], c.Depth, s.Point, s.Pyr, i, Math.Max(i - 1, 0));
            cl.Dispatch((lw + SkyView.Group - 1) / SkyView.Group, (lh + SkyView.Group - 1) / SkyView.Group, 1);
        }
        cl.SetPipeline(mp.Pipe);
        for (int i = 0; i < s.ColLevels; i++)
        {
            int lw = Math.Max((s.W >> s.ColShift) >> i, 1), lh = Math.Max((s.H >> s.ColShift) >> i, 1);
            VkSsrCompute(cl, mp, mpAt[i], c.Hdr, c.Linear, s.Col, i, Math.Max(i - 1, 0));
            cl.Dispatch((lw + SkyView.Group - 1) / SkyView.Group, (lh + SkyView.Group - 1) / SkyView.Group, 1);
        }
        ReadOnlySpan<ImageId> read = [s.Pyr, s.Col, s.RecN, s.RecW, c.Depth];
        cl.ReadImages(read);
        VkSsrDraw(cl, tr, s.TracePipe, trAt, s.Out, LoadOp.DontCare, s.Ow, s.Oh);
        VkSsrDraw(cl, cp, s.CompPipe, cpAt, c.Hdr, LoadOp.Load, viewport[2], viewport[3]);
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan screen-space reflections are LIVE -- {s.W}x{s.H} records, a {s.Levels}-level "
                     + $"hi-Z, the colour copy at 1/{1 << s.ColShift} with {s.ColLevels} mips, trace {s.Ow}x{s.Oh} (no probe "
                     + "or mirror on Vulkan: a miss adds nothing)");
        }
    }

    /// <summary>One pyramid level: the source sampled for level 0, level <paramref name="prev"/> through its storage
    /// view above it, level <paramref name="mip"/> stored.</summary>
    private static void VkSsrCompute(CommandList cl, VkDefStage st, int at, ImageId src, SamplerId smp, ImageId img, int mip, int prev)
    {
        VkColourState c = _vkc;
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<ImageBinding> sampled = stackalloc ImageBinding[4];
        Span<ImageBinding> images = stackalloc ImageBinding[2];
        int nu = 0, nt = 0, ni = 0;
        foreach (VkOpaque o in st.Opaques)
        {
            if (o.Set != 0) continue;
            if (o.Kind == VkBindKind.UniformBlock)
                ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, at, st.Block.Length)
                                                        : new BufferBinding(o.Binding, c.ZeroUbo);
            else if (o.Kind == VkBindKind.StorageImage)
                images[ni++] = new ImageBinding(o.Binding, img, default, o.Binding == 0 ? mip : prev);
            else if (o.Kind == VkBindKind.Sampled) sampled[nt++] = new ImageBinding(o.Binding, src, smp);
        }
        cl.BindSet(0, new BindSet(ubo[..nu], default, sampled[..nt], images[..ni]));
    }

    /// <summary>One full-screen triangle of <paramref name="p"/> into <paramref name="into"/>, each sampler on the image its
    /// name says, the probe on the stand-in cube and the mirror on the stand-in.</summary>
    private static void VkSsrDraw(CommandList cl, GuiVkProg p, PipelineId pipe, int at, ImageId into, LoadOp load, int w, int h)
    {
        if (pipe.IsNull) return;
        VkSsrState s = _vkSsr;
        VkColourState c = _vkc;
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<ImageBinding> img = stackalloc ImageBinding[10];
        int nu = 0, n = 0;
        foreach (VkOpaque o in p.Opaques)
        {
            if (o.Set != 0) continue;
            if (o.Kind == VkBindKind.UniformBlock)
            {
                ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, at, p.Block.Length)
                                                        : new BufferBinding(o.Binding, c.ZeroUbo);
                continue;
            }
            if (o.Kind != VkBindKind.Sampled || n == img.Length) continue;
            img[n++] = o.Name switch
            {
                "uDepth" => new ImageBinding(o.Binding, c.Depth, s.Point),
                "uColor" => new ImageBinding(o.Binding, s.Col, s.Trilinear),
                "uHiZ" => new ImageBinding(o.Binding, s.Pyr, s.Point),
                "uRecN" => new ImageBinding(o.Binding, s.RecN, s.Point),
                "uRecW" => new ImageBinding(o.Binding, s.RecW, s.Point),
                "uSrc" => new ImageBinding(o.Binding, s.Out, s.Point),
                "uProbe" => new ImageBinding(o.Binding, c.TexCube, c.Linear),
                _ => new ImageBinding(o.Binding, c.Tex2D, c.Linear),
            };
        }
        ReadOnlySpan<ColorAttachment> colors = [new(into, 0, 0, load, StoreOp.Store, default)];
        cl.BeginRenderPass(new RenderPassDesc(colors));
        cl.SetPipeline(pipe);
        cl.SetViewport(0, 0, w, h);
        cl.BindSet(0, new BindSet(ubo[..nu], default, img[..n], default));
        cl.Draw(3);
        cl.EndRenderPass();
    }

    /// <summary>The four programs, two samplers and two pipelines, once per device; a refusal is said once and the
    /// reflections sit the session out on Vulkan.</summary>
    private static bool VkSsrEnsure(VkBackend vk, VkSsrState s, ExtenderLog log)
    {
        if (s.Tried) return s.Comp != null;
        s.Tried = true;
        s.HiZ = VkDefBuild(vk, SsrHiZ, "CX_SSR_STORAGE", "vk ssr hi-Z", log);
        s.Mip = VkDefBuild(vk, SsrHiZ, "CX_SSR_COLOUR", "vk ssr colour chain", log);
        s.Trace = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_ssrtrace_f.glsl"), "ssr trace", p =>
        {
            s.TProj = p.Loc("uProj"); s.TInvProj = p.Loc("uInvProj"); s.TView = p.Loc("uView"); s.TInvView = p.Loc("uInvView");
            s.TNf = p.Loc("uNf"); s.TSsr = p.Loc("uSsr"); s.TViz = p.Loc("uViz"); s.TPlane = p.Loc("uPlane");
            s.TColShift = p.Loc("uColShift");
        }, log);
        s.Comp = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_ssrcomp_f.glsl"), "ssr composite",
                                     p => { s.CCap = p.Loc("uCap"); s.CViz = p.Loc("uViz"); }, log);
        if (s.HiZ != null)
            VkUniformShim.Resolve(s.HiZ.Names, () =>
            {
                s.HLevel = GlProgram.Loc(VkUniformShim.ProgramBase, "uLevel");
                s.HNf = GlProgram.Loc(VkUniformShim.ProgramBase, "uNf");
            });
        if (s.Mip != null)
            VkUniformShim.Resolve(s.Mip.Names, () => s.MLevel = GlProgram.Loc(VkUniformShim.ProgramBase, "uLevel"));
        s.Point = vk.CreateSampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.Nearest, Wrap.Clamp));
        s.Trilinear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Linear, Wrap.Clamp));
        if (s.Trace != null) s.TracePipe = VkSsrPipe(vk, s.Trace, false);
        if (s.Comp != null) s.CompPipe = VkSsrPipe(vk, s.Comp, true);
        if (s.HiZ == null || s.Mip == null || s.Trace == null || s.Comp == null || s.TracePipe.IsNull || s.CompPipe.IsNull
            || s.Point.IsNull || s.Trilinear.IsNull)
        {
            s.Comp = null;
            log.Always("AlloyRenderer: a Vulkan screen-space reflection program or pipeline was REFUSED (logged above) -- "
                     + "surfaces keep their own lighting alone on Vulkan this session");
            return false;
        }
        return true;
    }

    /// <summary>The trace's stage, or the composite's adding one, into a half-float colour image.</summary>
    private static PipelineId VkSsrPipe(VkBackend vk, GuiVkProg p, bool add)
    {
        var d = new PipelineDesc { Vs = p.Vs, Fs = p.Fs, Label = $"vk {p.Label}" };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = Format.Rgba16F;
        d.Targets.Samples = 1;
        if (add) d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One);
        return vk.CreatePipeline(in d);
    }

    /// <summary>The records and pyramid at the frame's size, the colour copy from level <paramref name="shift"/> of it with
    /// its full chain, and the trace output at half of it, remade on a resize.</summary>
    private static bool VkSsrTargets(VkBackend vk, VkSsrState s, int w, int h, int shift, ExtenderLog log)
    {
        if (!s.Out.IsNull && s.W == w && s.H == h && s.ColShift == shift) return true;
        vk.Destroy(s.RecN); vk.Destroy(s.RecW); vk.Destroy(s.Pyr); vk.Destroy(s.Col); vk.Destroy(s.Out);
        int cw = Math.Max(w >> shift, 1), ch = Math.Max(h >> shift, 1);
        s.Levels = SsrLevels(w, h);
        s.ColLevels = SsrLevels(cw, ch);
        s.Ow = (w + 1) / 2; s.Oh = (h + 1) / 2;
        const ImageUse attach = ImageUse.Sampled | ImageUse.ColorTarget, store = ImageUse.Sampled | ImageUse.Storage;
        s.RecN = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1, attach, "vk ssr record n"));
        s.RecW = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R11G11B10F, w, h, 1, 1, 1, 1, attach, "vk ssr record w"));
        s.Pyr = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, w, h, 1, s.Levels, 1, 1, store, "vk ssr hi-Z"));
        s.Col = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, cw, ch, 1, s.ColLevels, 1, 1, store, "vk ssr colour"));
        s.Out = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, s.Ow, s.Oh, 1, 1, 1, 1, attach, "vk ssr trace"));
        s.W = w; s.H = h; s.ColShift = shift;
        if (!s.RecN.IsNull && !s.RecW.IsNull && !s.Pyr.IsNull && !s.Col.IsNull && !s.Out.IsNull) return true;
        log.Always($"AlloyRenderer: a {w}x{h} Vulkan screen-space reflection target was REFUSED (logged above) -- no reflections this frame");
        s.Out = default;
        return false;
    }

    /// <summary>Levels down to 1x1 from <paramref name="w"/> x <paramref name="h"/>, as GL's pyramid counts them.</summary>
    private static int SsrLevels(int w, int h)
    {
        int levels = 1;
        while ((w >> levels) > 0 || (h >> levels) > 0) levels++;
        return levels;
    }

    /// <summary>The hi-Z stage's resref, both Vulkan variants of which build off it.</summary>
    private const string SsrHiZ = "cx_ssrhiz_c";

    /// <summary>The first of the colour pass's two attachments the record pair rides: draw buffers 2 and 3, as on GL.</summary>
    private const int SsrRecFirst = 2;
}
