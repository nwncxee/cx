using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The screen-space reflections (<see cref="PassId.Reflections"/>) on Vulkan: the colour pass writes <see cref="Ssr"/>'s
/// record pair, then after the deferred walk compute builds the hi-Z pyramid and colour mips, the trace finds a hit per
/// block, the full-res resolve weighs its own and its neighbours' hits against the probe and the planar mirror, and the
/// temporal pass blends that over its own history and adds it onto the scene colour.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The pass's programs and targets on one device, replaced whole with the device.</summary>
    private sealed class VkSsrState
    {
        public VkBackend? Dev;
        public VkDefStage? HiZ, Mip;
        public GuiVkProg? Trace, Res, Hist;
        public bool Tried, Live, Armed;
        public ImageId RecN, RecW, Pyr, Col, Out, Resolved;
        public int W, H, Levels, ColLevels, ColShift, BlkShift, Ow, Oh;
        /// <summary>The history pair, <see cref="HistAt"/> the one last frame wrote; <see cref="HistValid"/> = it holds
        /// that frame's, seen through <see cref="PrevVp"/>.</summary>
        public ImageId Hist0, Hist1;
        public int HistAt;
        public bool HistValid;
        public Matrix4x4 PrevVp;
        /// <summary>The mirror: its draw target and depth, the mip chain the trace samples, at <see cref="PlW"/> x
        /// <see cref="PlH"/>; <see cref="PlOn"/> = this frame's chain holds the mirror at <see cref="PlZ"/>.</summary>
        public ImageId PlCol, PlDepth, PlMip;
        public int PlW, PlH, PlLevels;
        public bool PlOn, PlFailed, PlLive;
        public float PlZ;
        public SamplerId Point, Trilinear;
        public PipelineId TracePipe, ResPipe, HistPipe;
        public int HLevel = -1, HNf = -1, MLevel = -1;
        public int TProj = -1, TInvProj = -1, TView = -1, TNf = -1, TSsr = -1, TFrame = -1;
        public int RInvProj = -1, RView = -1, RInvView = -1, RCap = -1, RProbeMips = -1, RShift = -1, RPlane = -1, RColShift = -1, RFrame = -1;
        public int PInvProj = -1, PInvView = -1, PPrevVp = -1, PHistOn = -1;
    }

    private static VkSsrState _vkSsr = new();

    /// <summary>Ahead of the colour pass: the programs and targets at the frame's size, armed when the reflections are
    /// on and the pass writes the G-buffer -- the record pair rides that attachment set. False = no records this frame.</summary>
    private static bool VkSsrArm(VkBackend vk, int w, int h, bool gbuf, int viz, ExtenderLog log)
    {
        VkSsrState s = _vkSsr;
        if (s.Dev != vk) _vkSsr = s = new VkSsrState { Dev = vk };
        s.Armed = false;
        // a capture draws into a cube between this frame's and the next: the history stays the screen's
        if (_capturing) return false;
        bool want = gbuf && viz == 0 && Lighting.Ssr.V;
        // a frame without the pass leaves the history a frame behind
        s.HistValid &= want;
        if (!want || !VkSsrEnsure(vk, s, log)) return false;
        if (!VkSsrTargets(vk, s, w, h, Lighting.SsrDetail.V, log)) return false;
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

    /// <summary>After the deferred walk: the pyramid, the colour copy's chain, the trace, the resolve, and the temporal
    /// pass into its history and the scene colour. Nothing unless <see cref="VkSsrArm"/> armed this frame.</summary>
    private static void VkSsrRun(VkBackend vk, CommandList cl, in Matrix4x4 vp, in Matrix4x4 view, in Matrix4x4 proj, int* viewport,
                                 float near, float far, bool mirror, ExtenderLog log)
    {
        VkSsrState s = _vkSsr;
        if (!s.Armed) return;
        s.Armed = false;
        if (!Matrix4x4.Invert(proj, out Matrix4x4 invProj) || !Matrix4x4.Invert(view, out Matrix4x4 invView)) return;
        s.PlOn = mirror && VkPlanarPass(vk, cl, s, vp, viewport, near, far, log);
        VkColourState c = _vkc;
        VkDefStage hz = s.HiZ!, mp = s.Mip!;
        int arena0 = c.ArenaUsed;
        Span<int> hzAt = stackalloc int[s.Levels];
        Span<int> mpAt = stackalloc int[s.ColLevels];
        for (int i = 0; i < s.Levels; i++)
        {
            Array.Clear(hz.Block);
            LooseBlock.Capture(hz.Members, hz.Block);
            LooseBlock.Set(s.HLevel, i);
            LooseBlock.Set(s.HNf, [near, far]);
            LooseBlock.EndCapture();
            if ((hzAt[i] = VkArena(hz.Block)) < 0) return;
        }
        for (int i = 0; i < s.ColLevels; i++)
        {
            Array.Clear(mp.Block);
            LooseBlock.Capture(mp.Members, mp.Block);
            LooseBlock.Set(s.MLevel, i);
            LooseBlock.EndCapture();
            if ((mpAt[i] = VkArena(mp.Block)) < 0) return;
        }
        GuiVkProg tr = s.Trace!, rs = s.Res!, hs = s.Hist!;
        int frame = _frameSeq;
        Array.Clear(tr.Block);
        LooseBlock.Capture(tr.Members, tr.Block);
        LooseBlock.Set(s.TProj, proj);
        LooseBlock.Set(s.TInvProj, invProj);
        LooseBlock.Set(s.TView, view);
        LooseBlock.Set(s.TNf, [near, far]);
        LooseBlock.Set(s.TSsr, [Lighting.SsrRough.V, Ssr.Steps[s.BlkShift], s.BlkShift, s.BlkShift]);
        LooseBlock.Set(s.TFrame, frame);
        LooseBlock.EndCapture();
        int trAt = VkArena(tr.Block);
        Array.Clear(rs.Block);
        LooseBlock.Capture(rs.Members, rs.Block);
        LooseBlock.Set(s.RInvProj, invProj);
        LooseBlock.Set(s.RView, view);
        LooseBlock.Set(s.RInvView, invView);
        LooseBlock.Set(s.RCap, Lighting.SsrRough.V);
        LooseBlock.Set(s.RProbeMips, (float)VkProbeMips);
        LooseBlock.Set(s.RShift, s.BlkShift);
        LooseBlock.Set(s.RColShift, s.ColShift);
        LooseBlock.Set(s.RPlane, [s.PlOn ? s.PlZ : 0f, s.PlOn ? 1f : 0f, 0f, 0f]);
        LooseBlock.Set(s.RFrame, frame);
        LooseBlock.EndCapture();
        int rsAt = VkArena(rs.Block);
        Array.Clear(hs.Block);
        LooseBlock.Capture(hs.Members, hs.Block);
        LooseBlock.Set(s.PInvProj, invProj);
        LooseBlock.Set(s.PInvView, invView);
        LooseBlock.Set(s.PPrevVp, s.PrevVp);
        LooseBlock.Set(s.PHistOn, s.HistValid ? 1 : 0);
        LooseBlock.EndCapture();
        int hsAt = VkArena(hs.Block);
        if (trAt < 0 || rsAt < 0 || hsAt < 0) return;
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
        // the mirror's chain off its draw, through the colour copy's stage: never more levels than that copy, its source
        for (int i = 0; s.PlOn && i < s.PlLevels; i++)
        {
            int lw = Math.Max(s.PlW >> i, 1), lh = Math.Max(s.PlH >> i, 1);
            VkSsrCompute(cl, mp, mpAt[i], s.PlCol, c.Linear, s.PlMip, i, Math.Max(i - 1, 0));
            cl.Dispatch((lw + SkyView.Group - 1) / SkyView.Group, (lh + SkyView.Group - 1) / SkyView.Group, 1);
        }
        ImageId prev = s.HistAt == 0 ? s.Hist0 : s.Hist1, next = s.HistAt == 0 ? s.Hist1 : s.Hist0;
        ReadOnlySpan<ImageId> read = [s.Pyr, s.Col, s.RecN, s.RecW, c.Depth, s.PlOn ? s.PlMip : s.Col, prev];
        cl.ReadImages(read);
        VkSsrDraw(cl, tr, s.TracePipe, trAt, [new(s.Out, 0, 0, LoadOp.DontCare, StoreOp.Store, default)], s.Ow, s.Oh);
        cl.ReadImages([s.Out]);
        VkSsrDraw(cl, rs, s.ResPipe, rsAt, [new(s.Resolved, 0, 0, LoadOp.DontCare, StoreOp.Store, default)], s.W, s.H);
        cl.ReadImages([s.Resolved]);
        VkSsrDraw(cl, hs, s.HistPipe, hsAt, [new(next, 0, 0, LoadOp.DontCare, StoreOp.Store, default),
                                            new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)], s.W, s.H);
        s.HistAt ^= 1;
        s.HistValid = true;
        s.PrevVp = vp;
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan screen-space reflections are LIVE -- {s.W}x{s.H} records, a {s.Levels}-level "
                     + $"hi-Z, the colour copy at 1/{1 << s.ColShift} with {s.ColLevels} mips, a ray per {1 << s.BlkShift}x{1 << s.BlkShift} "
                     + $"block ({s.Ow}x{s.Oh}), resolved and kept over frames at {s.W}x{s.H}");
        }
        if (s.PlOn && !s.PlLive)
        {
            s.PlLive = true;
            log.Always($"AlloyRenderer: the Vulkan planar mirror is LIVE -- the opaque set mirrored across the ground at the "
                     + $"player's feet into {s.PlW}x{s.PlH}, {s.PlLevels} mips (the whole opaque list draws; no mirror re-cull on Vulkan)");
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

    /// <summary>One full-screen triangle of <paramref name="p"/> into <paramref name="colors"/>, each sampler on the image
    /// its name says, the history on last frame's, the probe on the live cube and the mirror on its chain (else their
    /// stand-ins).</summary>
    private static void VkSsrDraw(CommandList cl, GuiVkProg p, PipelineId pipe, int at, ReadOnlySpan<ColorAttachment> colors,
                                  int w, int h)
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
                ubo[nu++] = VkProbeSh(o.Binding) is { } sh ? sh
                          : o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, at, p.Block.Length)
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
                "uTrace" => new ImageBinding(o.Binding, s.Out, s.Point),
                "uRes" => new ImageBinding(o.Binding, s.Resolved, s.Point),
                "uHist" => new ImageBinding(o.Binding, s.HistAt == 0 ? s.Hist0 : s.Hist1, c.Linear),
                "uProbe" => VkProbeImage(o) ?? new ImageBinding(o.Binding, c.TexCube, c.Linear),
                "uPlanar" when s.PlOn => new ImageBinding(o.Binding, s.PlMip, s.Trilinear),
                _ => new ImageBinding(o.Binding, c.Tex2D, c.Linear),
            };
        }
        cl.BeginRenderPass(new RenderPassDesc(colors));
        cl.SetPipeline(pipe);
        cl.SetViewport(0, 0, w, h);
        cl.BindSet(0, new BindSet(ubo[..nu], default, img[..n], default));
        cl.Draw(3);
        cl.EndRenderPass();
    }

    /// <summary>The five programs, two samplers and three pipelines, once per device; a refusal is said once and the
    /// reflections sit the session out on Vulkan.</summary>
    private static bool VkSsrEnsure(VkBackend vk, VkSsrState s, ExtenderLog log)
    {
        if (s.Tried) return s.Hist != null;
        s.Tried = true;
        s.HiZ = VkDefBuild(vk, SsrHiZ, "CX_SSR_STORAGE", "vk ssr hi-Z", log);
        s.Mip = VkDefBuild(vk, SsrHiZ, "CX_SSR_COLOUR", "vk ssr colour chain", log);
        s.Trace = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_ssrtrace_f.glsl"), "ssr trace", p =>
        {
            s.TProj = p.Loc("uProj"); s.TInvProj = p.Loc("uInvProj"); s.TView = p.Loc("uView");
            s.TNf = p.Loc("uNf"); s.TSsr = p.Loc("uSsr"); s.TFrame = p.Loc("uFrame");
        }, log);
        s.Res = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_ssrres_f.glsl"), "ssr resolve", p =>
        {
            s.RInvProj = p.Loc("uInvProj"); s.RView = p.Loc("uView"); s.RInvView = p.Loc("uInvView"); s.RCap = p.Loc("uCap"); s.RProbeMips = p.Loc("uProbeMips"); s.RShift = p.Loc("uShift");
            s.RPlane = p.Loc("uPlane"); s.RColShift = p.Loc("uColShift"); s.RFrame = p.Loc("uFrame");
        }, log);
        s.Hist = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_ssrhist_f.glsl"), "ssr history", p =>
        {
            s.PInvProj = p.Loc("uInvProj"); s.PInvView = p.Loc("uInvView"); s.PPrevVp = p.Loc("uPrevVp"); s.PHistOn = p.Loc("uHistOn");
        }, log);
        if (s.HiZ != null)
            LooseBlock.Resolve(s.HiZ.Names, () =>
            {
                s.HLevel = LooseBlock.Loc("uLevel");
                s.HNf = LooseBlock.Loc("uNf");
            });
        if (s.Mip != null)
            LooseBlock.Resolve(s.Mip.Names, () => s.MLevel = LooseBlock.Loc("uLevel"));
        s.Point = vk.CreateSampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.Nearest, Wrap.Clamp));
        s.Trilinear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Linear, Wrap.Clamp));
        if (s.Trace != null) s.TracePipe = VkSsrPipe(vk, s.Trace, Format.Rgba32F, false);
        if (s.Res != null) s.ResPipe = VkSsrPipe(vk, s.Res, Format.Rgba16F, false);
        if (s.Hist != null) s.HistPipe = VkSsrPipe(vk, s.Hist, Format.Rgba16F, true);
        if (s.HiZ == null || s.Mip == null || s.Trace == null || s.Res == null || s.Hist == null || s.TracePipe.IsNull
            || s.ResPipe.IsNull || s.HistPipe.IsNull || s.Point.IsNull || s.Trilinear.IsNull)
        {
            s.Hist = null;
            log.Always("AlloyRenderer: a Vulkan screen-space reflection program or pipeline was REFUSED (logged above) -- "
                     + "surfaces keep their own lighting alone on Vulkan this session");
            return false;
        }
        return true;
    }

    /// <summary>A stage into one <paramref name="f"/> image, or with <paramref name="add"/> the temporal stage's pair:
    /// the history replaced, the frame's half-float colour added onto.</summary>
    private static PipelineId VkSsrPipe(VkBackend vk, GuiVkProg p, Format f, bool add)
    {
        var d = new PipelineDesc { Vs = p.Vs, Fs = p.Fs, Label = $"vk {p.Label}" };
        d.Targets.ColorCount = add ? 2 : 1;
        d.Targets.Colors[0] = f;
        d.Targets.Samples = 1;
        if (add)
        {
            d.Targets.Colors[1] = Format.Rgba16F;
            d.Blend[1] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One);
        }
        return vk.CreatePipeline(in d);
    }

    /// <summary>The records, pyramid, resolve and history pair at the frame's size, the colour copy from level
    /// min(<paramref name="shift"/>, 1) of it with its full chain, and the trace a texel per 1 &lt;&lt; <paramref name="shift"/>
    /// block; remade, and the history dropped, on a resize or a detail change.</summary>
    private static bool VkSsrTargets(VkBackend vk, VkSsrState s, int w, int h, int shift, ExtenderLog log)
    {
        if (!s.Out.IsNull && s.W == w && s.H == h && s.BlkShift == shift) return true;
        vk.Destroy(s.RecN); vk.Destroy(s.RecW); vk.Destroy(s.Pyr); vk.Destroy(s.Col); vk.Destroy(s.Out);
        vk.Destroy(s.Resolved); vk.Destroy(s.Hist0); vk.Destroy(s.Hist1);
        int colShift = Math.Min(shift, 1);
        int cw = Math.Max(w >> colShift, 1), ch = Math.Max(h >> colShift, 1), blk = 1 << shift;
        s.Levels = SsrLevels(w, h);
        s.ColLevels = SsrLevels(cw, ch);
        s.Ow = (w + blk - 1) >> shift; s.Oh = (h + blk - 1) >> shift;
        const ImageUse attach = ImageUse.Sampled | ImageUse.ColorTarget, store = ImageUse.Sampled | ImageUse.Storage;
        s.RecN = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1, attach, "vk ssr record n"));
        s.RecW = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R11G11B10F, w, h, 1, 1, 1, 1, attach, "vk ssr record w"));
        s.Pyr = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, w, h, 1, s.Levels, 1, 1, store, "vk ssr hi-Z"));
        s.Col = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, cw, ch, 1, s.ColLevels, 1, 1, store, "vk ssr colour"));
        s.Out = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32F, s.Ow, s.Oh, 1, 1, 1, 1, attach, "vk ssr trace"));
        s.Resolved = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1, attach, "vk ssr resolve"));
        s.Hist0 = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1, attach, "vk ssr history 0"));
        s.Hist1 = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1, attach, "vk ssr history 1"));
        s.W = w; s.H = h; s.ColShift = colShift; s.BlkShift = shift;
        s.HistValid = false;
        if (!s.RecN.IsNull && !s.RecW.IsNull && !s.Pyr.IsNull && !s.Col.IsNull && !s.Out.IsNull && !s.Resolved.IsNull
            && !s.Hist0.IsNull && !s.Hist1.IsNull) return true;
        log.Always($"AlloyRenderer: a {w}x{h} Vulkan screen-space reflection target was REFUSED (logged above) -- no reflections this frame");
        s.Out = default;
        return false;
    }

    /// <summary>The world edge and a camera cut: the reflection history shows another place, so the next frame's
    /// resolve stands alone.</summary>
    internal static void VkSsrRevoke() => _vkSsr.HistValid = false;

    /// <summary>The planar mirror -- the opaque set mirrored across the ground at the player's feet into a
    /// 1/<c>light.ssrplanarres</c> target, winding flipped, the lit stage dropping what lies under the plane.
    /// False = no mirror this frame. ponytail: one plane, the player's; no mirror re-cull, the whole opaque list
    /// draws (a mirror phase in the cull pass is the upgrade).</summary>
    private static bool VkPlanarPass(VkBackend vk, CommandList cl, VkSsrState s, in Matrix4x4 vp, int* viewport, float near,
                                     float far, ExtenderLog log)
    {
        VkColourState c = _vkc;
        if (!Lighting.SsrPlanar.V || s.PlFailed || _runtime == null || ArenaCap - c.ArenaUsed < VkProbeArenaNeed) return false;
        int div = Lighting.SsrPlanarRes.V;
        int pw = Math.Max(viewport[2] / div, 1), ph = Math.Max(viewport[3] / div, 1);
        if (!VkPlanarTargets(vk, s, pw, ph, log)) return false;
        float z = PlayerPos.Z;
        // the reflection across z = h, row vectors (p' = p M): z' = 2h - z
        Matrix4x4 m = Matrix4x4.Identity;
        m.M33 = -1f; m.M43 = 2f * z;
        Matrix4x4 mVp = m * vp;
        long draws = Counters._draws, batches = Counters._batches;
        Vector3 eye = _eyePos;
        int* pvp = stackalloc int[4] { 0, 0, pw, ph };
        _capturing = _planar = true;
        _mirrorZ = z;
        _eyePos = new Vector3(eye.X, eye.Y, 2f * z - eye.Z);
        bool ok;
        try
        {
            CommandList side = c.Side!;
            side.Reset();
            int arena0 = c.ArenaUsed;
            var b = new VkBound { Fmt = -1, Tex = -1 };
            VkPassStart(side, pw, ph);
            ok = VkIssue(side, ref b, 0, _gOpaqueEnd, new VkRange(false, true, false, false, VkTargets.Forward, false), mVp, pvp,
                         near, far, false);
            // alpha 0 where nothing mirrored: the trace keeps its own answer there
            ReadOnlySpan<ColorAttachment> colors = [new(s.PlCol, 0, 0, LoadOp.Clear, StoreOp.Store, default)];
            VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(s.PlDepth, 0, 0, LoadOp.Clear, StoreOp.Store, 1f)));
        }
        finally
        {
            _capturing = _planar = false;
            _eyePos = eye;
            Counters._draws = draws; Counters._batches = batches;
        }
        s.PlZ = z;
        return ok;
    }

    /// <summary>The mirror's draw target, depth and chain at <paramref name="w"/> x <paramref name="h"/>, remade on a resize;
    /// a refusal is said once and the mirror sits the session out.</summary>
    private static bool VkPlanarTargets(VkBackend vk, VkSsrState s, int w, int h, ExtenderLog log)
    {
        if (!s.PlMip.IsNull && s.PlW == w && s.PlH == h) return true;
        vk.Destroy(s.PlCol); vk.Destroy(s.PlDepth); vk.Destroy(s.PlMip);
        s.PlLevels = Math.Min(SsrLevels(w, h), s.ColLevels);
        s.PlCol = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1,
                                               ImageUse.Sampled | ImageUse.ColorTarget, "vk ssr mirror"));
        s.PlDepth = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.D32F, w, h, 1, 1, 1, 1, ImageUse.DepthTarget, "vk ssr mirror depth"));
        s.PlMip = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, s.PlLevels, 1, 1,
                                               ImageUse.Sampled | ImageUse.Storage, "vk ssr mirror chain"));
        s.PlW = w; s.PlH = h;
        if (!s.PlCol.IsNull && !s.PlDepth.IsNull && !s.PlMip.IsNull) return true;
        s.PlFailed = true;
        s.PlMip = default;
        log.Always($"AlloyRenderer: the {w}x{h} Vulkan planar mirror target was REFUSED (logged above) -- mirror reflections "
                 + "are off on Vulkan this session");
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
