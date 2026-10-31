using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary><see cref="PostChain"/> on Vulkan: the same bloom chain and resolve, from the same overridable stages,
/// over the Vulkan frame's scene colour and depth, into the post stack's first image or the composite. Each bloom
/// level is an image of its own rather than a mip, so no pass samples the image it writes. Also the full-screen
/// helpers every post program on Vulkan draws through.</summary>
internal static unsafe class PostChainVk
{
    /// <summary>The resolve's loose terms.</summary>
    private sealed class ResolveLocs
    {
        public int SkyRaw = -1, SkyBand = -1, InvVp = -1, Eye = -1, VFog = -1, VFogVp = -1, SkyBare = -1, OverlayOn = -1;
        public int Strength = -1, Mode = -1, Knee = -1, Exposure = -1, AcesMix = -1, Dither = -1, Viz = -1;
    }

    /// <summary>The bloom stages' loose terms.</summary>
    private sealed class BloomLocs
    {
        public int Lod = -1, Texel = -1, First = -1, Thresh = -1, Exposure = -1;
    }

    /// <summary>Everything on one device, replaced whole when the device is.</summary>
    private sealed class PostVkState
    {
        public VkBackend? Dev;
        public GuiVkProg? Down, Up, Resolve;
        public readonly BloomLocs D = new(), U = new();
        public readonly ResolveLocs R = new();
        public bool Tried, Failed;
        public readonly ImageId[] Bloom = new ImageId[MaxLevels];
        public int W, H, Levels;
        public BufferId Ubo;
        public int UboUsed;
        public SamplerId Linear, Nearest, Repeat;
        public bool Said, VizSaid;
        /// <summary>Both bloom stages built from the embedded texts: no content override reached either.</summary>
        public bool BloomStock;
        /// <summary>The bloom has said how it runs on this device: as compute, stood down, or refused.</summary>
        public bool ComputeSaid;
        public PipelineId BloomCs;
        public bool CsTried;
    }

    /// <summary><c>cx_bloom_cs</c>'s push block, member for member.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BloomPush
    {
        public Vector2 Texel, Thresh;
        public float Exposure;
        public int First, Up;
    }

    private static PostVkState _s = new();

    /// <summary>The chain's depth ceiling, as GL's.</summary>
    private const int MaxLevels = 6;

    /// <summary>The loose blocks one frame's post programs fill: the bloom levels, the resolve and the stack.</summary>
    private const int UboBytes = 32 << 10;

    /// <summary>The samplers the post programs declare without a binding: one unit per name, distinct within any
    /// one program.</summary>
    private static readonly IReadOnlyDictionary<string, uint> Units = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["uSrc"] = TextureSlots.PostHdr, ["uHdr"] = TextureSlots.PostHdr, ["uCur"] = TextureSlots.PostHdr,
        ["uBloom"] = TextureSlots.PostBloom, ["uDepth"] = TextureSlots.PostDepth, ["uOverlay"] = TextureSlots.PostOverlay,
        ["uHist"] = TextureSlots.PostHist, ["uVel"] = TextureSlots.PostVel, ["uMask"] = TextureSlots.PostHdr,
        ["texFBColor"] = TextureSlots.PostHdr, ["texFBDepth"] = TextureSlots.PostDepth,
    };

    /// <summary>What a post draw samples, by the role its declaration names: <c>uSrc</c>/<c>uHdr</c>/<c>uCur</c>
    /// read <see cref="Src"/>, then <c>uBloom</c>, <c>uDepth</c>, <c>uHist</c>, <c>uVel</c> and SMAA's four tables;
    /// any other sampler, or a null image, takes a stand-in. <see cref="Storage"/> is the one storage buffer a draw may read.</summary>
    internal readonly record struct Inputs(ImageId Src, ImageId Bloom = default, ImageId Depth = default,
                                           ImageId Hist = default, ImageId Vel = default, bool HistLinear = false,
                                           ImageId Mask = default, ImageId Edges = default, ImageId Area = default,
                                           ImageId Search = default, ImageId Weights = default,
                                           BufferBinding Storage = default);

    /// <summary>How a post draw meets its target: replacing it, adding to it, or alpha-over it.</summary>
    internal enum Blend : byte { None, Add, Over }

    /// <summary>Bloom, then the resolve of <paramref name="hdr"/> into <paramref name="into"/> at
    /// <paramref name="rect"/>, dithered only when it is the frame's last write; <c>/post viz 4</c> paints <paramref name="vel"/>
    /// instead. False (logged) when a program or target was refused: the frame has no other way to the screen.</summary>
    internal static bool Record(VkBackend vk, CommandList cl, ImageId hdr, ImageId depth, ImageId into, bool last,
                                int* viewport, int* rect, ImageId vel, ExtenderLog log)
    {
        if (!Ensure(vk, log)) return false;
        PostVkState s = _s;
        if (!Targets(vk, s, viewport[2], viewport[3], log)) return false;
        bool bloom = Post.Bloom.V > 0f && StockConfig.HdrBloom;
        if (bloom)
        {
            cl.BeginLabel("bloom"u8);
            bool compute = s.BloomStock && BloomCompute(vk, cl, s, hdr, log);
            if (!compute) Bloom(cl, s, hdr);
            if (!s.ComputeSaid && (compute || !s.BloomStock))
                log.Always(compute ? "PostChainVk: the compute bloom is LIVE -- one dispatch per level, no render pass"
                                   : "PostChainVk: the compute bloom STOOD DOWN for the content override of a bloom stage -- it draws as fragments");
            s.ComputeSaid = true;
            cl.EndLabel();
        }

        ResolveLocs r = s.R;
        Matrix4x4 invVp = AlloyRenderer.SkyInvVp;
        Vector3 eye = AlloyRenderer.EyePos;
        Open(s.Resolve!);
        LooseBlock.Set(r.SkyRaw, Post.SkyRaw.V && invVp != default ? 1 : 0);
        LooseBlock.Set(r.SkyBand, FrameInputs.FogOn && invVp != default ? SceneFog.SkyBand : 0f);
        LooseBlock.Set(r.InvVp, invVp);
        LooseBlock.Set(r.Eye, [eye.X, eye.Y, eye.Z]);
        FogVolume.Upload(r.VFog, r.VFogVp, capture: false);
        LooseBlock.Set(r.SkyBare, invVp == default ? 1 : 0);
        LooseBlock.Set(r.OverlayOn, StageResources.VkOverlay.IsNull ? 0 : 1);
        LooseBlock.Set(r.Strength, bloom ? Post.Bloom.V : 0f);
        LooseBlock.Set(r.Mode, Post.Tonemap.V);
        LooseBlock.Set(r.Knee, Post.TmKnee.V);
        LooseBlock.Set(r.Exposure, Post.Exposure.V);
        LooseBlock.Set(r.AcesMix, Post.AcesMix.V);
        LooseBlock.Set(r.Dither, last ? PostChain.DitherLsb : 0f);
        bool motion = Post.Viz.V == PostChain.VizMotion;
        if (motion && vel.IsNull && !s.VizSaid)
        {
            s.VizSaid = true;
            log.Always("PostChainVk: /post viz 4 shows NOTHING -- the velocity target is the taa stage's, and taa is not "
                     + "drawing this frame; the frame resolves as viz 0");
        }
        LooseBlock.Set(r.Viz, motion && vel.IsNull ? 0 : Post.Viz.V);
        cl.BeginLabel("resolve"u8);
        int at = Close(cl, s.Resolve!);
        // the velocity viz rides the bloom input: the two views are exclusive by construction
        Draw(cl, s.Resolve!, FormatOf(into), Blend.None, into, LoadOp.Clear, new Rect(rect[0], rect[1], rect[2], rect[3]), at,
             new Inputs(hdr, motion && !vel.IsNull ? vel : bloom ? s.Bloom[0] : default, depth), log);
        cl.EndLabel();
        if (!s.Said)
        {
            s.Said = true;
            log.Always($"PostChainVk: LIVE -- {(bloom ? $"{s.Levels}-level bloom, " : "no bloom, ")}the resolve's tonemap "
                     + $"(mode {Post.Tonemap.V}) and encode at {rect[2]}x{rect[3]} from {s.W}x{s.H}");
        }
        return true;
    }

    /// <summary>The golden's handle on the bloom chain: over <paramref name="hdr"/>, a <paramref name="w"/> x
    /// <paramref name="h"/> scene, as compute or (<paramref name="compute"/> false) as the fragment draws, which
    /// <see cref="Record"/> picks between itself. Its level 0, or the null id (logged) when refused.</summary>
    internal static ImageId BloomVk(VkBackend vk, CommandList cl, ImageId hdr, int w, int h, bool compute, ExtenderLog log)
    {
        if (!Ensure(vk, log)) return default;
        PostVkState s = _s;
        if (!Targets(vk, s, w, h, log)) return default;
        if (!compute) Bloom(cl, s, hdr);
        else if (!BloomCompute(vk, cl, s, hdr, log)) return default;
        return s.Bloom[0];
    }

    /// <summary>The downsample from the scene colour through every level, then the tent upsample back, adding.</summary>
    private static void Bloom(CommandList cl, PostVkState s, ImageId hdr)
    {
        BloomLocs d = s.D, u = s.U;
        for (int i = 0; i < s.Levels; i++)
        {
            int sw = i == 0 ? s.W : LevelW(s, i - 1), sh = i == 0 ? s.H : LevelH(s, i - 1);
            Open(s.Down!);
            LooseBlock.Set(d.Exposure, Post.Exposure.V);
            LooseBlock.Set(d.Lod, 0);
            LooseBlock.Set(d.First, i == 0 ? 1 : 0);
            LooseBlock.Set(d.Texel, [1f / sw, 1f / sh]);
            LooseBlock.Set(d.Thresh, [Post.Threshold.V, MathF.Max(Post.Knee.V, 1e-4f)]);
            int at = Close(cl, s.Down!);
            Draw(cl, s.Down!, Format.Rgba16F, Blend.None, s.Bloom[i], LoadOp.DontCare, new Rect(0, 0, LevelW(s, i), LevelH(s, i)),
                 at, new Inputs(i == 0 ? hdr : s.Bloom[i - 1]), null);
        }
        for (int i = s.Levels - 1; i > 0; i--)
        {
            Open(s.Up!);
            LooseBlock.Set(u.Lod, 0);
            LooseBlock.Set(u.Texel, [1f / LevelW(s, i), 1f / LevelH(s, i)]);
            int at = Close(cl, s.Up!);
            Draw(cl, s.Up!, Format.Rgba16F, Blend.Add, s.Bloom[i - 1], LoadOp.Load, new Rect(0, 0, LevelW(s, i - 1), LevelH(s, i - 1)),
                 at, new Inputs(s.Bloom[i]), null);
        }
    }

    /// <summary><see cref="Bloom"/> as one <c>cx_bloom_cs</c> dispatch per level; false (logged once) when the stage
    /// was refused.</summary>
    private static bool BloomCompute(VkBackend vk, CommandList cl, PostVkState s, ImageId hdr, ExtenderLog log)
    {
        if (!s.CsTried)
        {
            s.CsTried = true;
            s.BloomCs = vk.CreateCompute(ShaderSource.Get("cx_bloom_cs.glsl"), "bloom compute", "cx_bloom_cs");
            if (s.BloomCs.IsNull) log.Always("PostChainVk: the compute bloom was REFUSED (logged above) -- the bloom draws as fragments");
        }
        if (s.BloomCs.IsNull) return false;
        cl.SetPipeline(s.BloomCs);
        var p = new BloomPush { Thresh = new Vector2(Post.Threshold.V, MathF.Max(Post.Knee.V, 1e-4f)), Exposure = Post.Exposure.V };
        for (int i = 0; i < s.Levels; i++)
        {
            int sw = i == 0 ? s.W : LevelW(s, i - 1), sh = i == 0 ? s.H : LevelH(s, i - 1);
            p.Texel = new Vector2(1f / sw, 1f / sh);
            p.First = i == 0 ? 1 : 0;
            BloomDispatch(cl, s, in p, i == 0 ? hdr : s.Bloom[i - 1], s.Bloom[i], LevelW(s, i), LevelH(s, i));
        }
        p.First = 0;
        p.Up = 1;
        for (int i = s.Levels - 1; i > 0; i--)
        {
            p.Texel = new Vector2(1f / LevelW(s, i), 1f / LevelH(s, i));
            BloomDispatch(cl, s, in p, s.Bloom[i], s.Bloom[i - 1], LevelW(s, i - 1), LevelH(s, i - 1));
        }
        return true;
    }

    /// <summary>The 8x8 groups covering a <paramref name="w"/> x <paramref name="h"/> level.</summary>
    private const int BloomGroup = 8;

    private static void BloomDispatch(CommandList cl, PostVkState s, in BloomPush p, ImageId src, ImageId dst, int w, int h)
    {
        ReadOnlySpan<ImageBinding> sampled = [new(0, src, s.Linear)];
        ReadOnlySpan<ImageBinding> storage = [new(0, dst)];
        cl.BindSet(0, new BindSet(default, default, sampled, storage));
        cl.PushConstants(0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<BloomPush>(in p)));
        cl.Dispatch((w + BloomGroup - 1) / BloomGroup, (h + BloomGroup - 1) / BloomGroup);
    }

    /// <summary>The attachment format a post draw into <paramref name="into"/> renders: the composite is 8-bit, every
    /// other post image half-float.</summary>
    internal static Format FormatOf(ImageId into)
        => _s.Dev is { } vk && into == vk.Composite ? Format.Rgba8 : Format.Rgba16F;

    /// <summary>One full-screen triangle of <paramref name="p"/> into <paramref name="into"/> at <paramref name="vp"/>,
    /// its loose block at <paramref name="at"/>, sampling <paramref name="t"/>. Nothing (logged) when the device
    /// refused the pipeline.</summary>
    internal static void Draw(CommandList cl, GuiVkProg p, Format f, Blend add, ImageId into, LoadOp load, Rect vp, int at,
                              in Inputs t, ExtenderLog? log)
    {
        PipelineId pipe = Pipe(p, f, add, log);
        if (pipe.IsNull) return;
        PostVkState s = _s;
        Span<ImageBinding> img = stackalloc ImageBinding[8 + (int)StageResources.DrawImgUnits];
        Span<ImageBinding> sti = stackalloc ImageBinding[(int)StageResources.DrawImgUnits];
        int n = 0, ns = 0;
        foreach (VkOpaque o in p.Opaques)
        {
            if (o.Set != 0) continue;
            if (o.Kind == VkBindKind.StorageImage && ns < sti.Length && StageResources.VkImageFor(p.StageBinds, o.Binding, true) is { } w)
                sti[ns++] = w;
            if (o.Kind != VkBindKind.Sampled || n == img.Length) continue;
            if (StageResources.VkImageFor(p.StageBinds, o.Binding, false) is { } own) { img[n++] = own; continue; }
            (ImageId i, SamplerId smp) = o.Name switch
            {
                "uSrc" or "uHdr" or "uCur" or "texFBColor" => (t.Src, s.Linear),
                "texFBDepth" => (t.Depth, s.Nearest),
                "texUnitNoise" => (VkTextures.Image(FrameSnapshot.NoiseTid), s.Repeat),
                "uBloom" => (t.Bloom, s.Linear),
                "uDepth" or "uTrigDepth" => (t.Depth, s.Nearest),
                "uHist" => (t.Hist, t.HistLinear ? s.Linear : s.Nearest),
                "uVel" => (t.Vel, s.Linear),
                "uMask" => (t.Mask, s.Linear),
                "uEdges" => (t.Edges, s.Linear),
                "uArea" => (t.Area, s.Linear),
                "uSearch" => (t.Search, s.Nearest),
                "uBlend" => (t.Weights, s.Linear),
                "uVFogVol" => (AlloyRenderer.VkFogVolume, s.Linear),
                "uOverlay" => (StageResources.VkOverlay, s.Linear),
                _ => (default(ImageId), s.Linear),
            };
            if (i.IsNull) i = o.Type == "sampler3D" ? AlloyRenderer.VkStandIn3D : AlloyRenderer.VkStandIn2D;
            img[n++] = new ImageBinding(o.Binding, i, smp);
        }
        ReadOnlySpan<BufferBinding> ubo = p.Block.Length == 0 ? default : [new(UboSlots.Loose, s.Ubo, at, p.Block.Length)];
        ReadOnlySpan<ColorAttachment> colors = [new(into, 0, 0, load, StoreOp.Store, new Vector4(0f, 0f, 0f, 1f))];
        cl.BeginRenderPass(new RenderPassDesc(colors));
        cl.SetPipeline(pipe);
        cl.SetViewport(vp.X, vp.Y, vp.W, vp.H);
        ReadOnlySpan<BufferBinding> sto = t.Storage.Buffer.IsNull ? default : [t.Storage];
        cl.BindSet(0, new BindSet(ubo, sto, img[..n], sti[..ns]));
        cl.Draw(3);
        cl.EndRenderPass();
    }

    /// <summary>Open a capture into <paramref name="p"/>'s loose block: the <c>LooseBlock.Set</c> calls until
    /// <see cref="Close"/> fill it.</summary>
    internal static void Open(GuiVkProg p)
    {
        Array.Clear(p.Block);
        if (p.Block.Length > 0) LooseBlock.Capture(p.Members, p.Block);
    }

    /// <summary>Close <see cref="Open"/>'s capture and append the block to the frame's post uniforms; its offset,
    /// or -1 (said once) when they are full, which <see cref="Draw"/> then reads as zeros at 0.</summary>
    internal static int Close(CommandList cl, GuiVkProg p)
    {
        if (p.Block.Length == 0) return 0;
        LooseBlock.EndCapture();
        PostVkState s = _s;
        int align = Math.Max(s.Dev!.UniformAlign, 16);
        int at = (s.UboUsed + align - 1) & ~(align - 1);
        if (at + p.Block.Length > UboBytes) return 0;
        cl.UpdateBuffer(s.Ubo, at, p.Block);
        s.UboUsed = at + p.Block.Length;
        return at;
    }

    /// <summary>The frame's post uniforms start over; once per Vulkan frame, ahead of the first post draw.</summary>
    internal static void BeginFrame() => _s.UboUsed = 0;

    /// <summary>Post program <paramref name="label"/> from its two stages' GL text, its sizing tokens expanded as GL's
    /// compile expands them, translated and compiled. Null (logged) when a step refused.</summary>
    internal static GuiVkProg? Program(VkBackend vk, string vs, string fs, string label, Action<GuiVkProg> locate, ExtenderLog log,
                                       int stage = -1)
    {
        vs = ShaderResource.Tokens(vs);
        fs = ShaderResource.Tokens(fs);
        if (SharedUnit(fs) is { } clash) log.Always($"PostChainVk: post {label} puts {clash} -- BOTH read whichever image binds last");
        if (StageResources.VkUnits(UnitsFor(fs), fs, stage, -1, StageResources.DrawImgUnits, out string why) is not { } units)
        {
            log.Always($"PostChainVk: post {label} -- {why}");
            return null;
        }
        GuiVkProg? p = GuiVkProg.Build(vk, vs, fs, units, $"post {label}", locate, log);
        if (p == null || stage < 0) return p;
        p.Stage = stage;
        p.StageBinds = StageResources.VkBinds(p.Opaques, stage, -1, draw: true);
        LooseBlock.Resolve(p.Names, () => p.StageFrame = FrameLoc.Locate());
        return p;
    }

    /// <summary>Two samplers <paramref name="fs"/> declares that <see cref="Units"/> puts on one unit, as
    /// "uA and uB on unit n"; null when every one has its own. Two on one unit read whichever image binds last.</summary>
    internal static string? SharedUnit(string fs)
    {
        var seen = new Dictionary<uint, string>();
        foreach (string name in Unbound(fs))
        {
            if (!Units.TryGetValue(name, out uint u)) continue;
            if (seen.TryGetValue(u, out string? other) && other != name) return $"{other} and {name} on unit {u}";
            seen[u] = name;
        }
        return null;
    }

    /// <summary>The samplers <paramref name="fs"/> declares without a binding, in order.</summary>
    private static IEnumerable<string> Unbound(string fs)
    {
        foreach ((string type, string name, string? binding) in VkGlsl.Opaques(fs))
            if (binding == null && type.Contains("sampler", StringComparison.Ordinal)) yield return name;
    }

    /// <summary>The first unit a content stage's own unbound sampler takes: past every unit Alloy names.</summary>
    private const uint ContentUnitBase = 40;

    /// <summary><see cref="Units"/>, plus a unit of its own for every sampler a content chain declares without a binding
    /// and Alloy does not name -- the engine's <c>texUnit</c><i>n</i> set, which <see cref="Draw"/> binds by name.</summary>
    private static IReadOnlyDictionary<string, uint> UnitsFor(string fs)
    {
        Dictionary<string, uint>? extra = null;
        uint next = ContentUnitBase;
        foreach (string name in Unbound(fs))
        {
            if (Units.ContainsKey(name) || extra?.ContainsKey(name) == true) continue;
            extra ??= new Dictionary<string, uint>(Units, StringComparer.Ordinal);
            extra[name] = next++;
        }
        return extra ?? Units;
    }

    /// <summary><paramref name="p"/>'s pipeline into one <paramref name="f"/> attachment, adding under
    /// <paramref name="add"/>; made on first ask, a null id (logged once) when the device refused it.</summary>
    private static PipelineId Pipe(GuiVkProg p, Format f, Blend add, ExtenderLog? log)
    {
        long key = (long)f | (long)add << 32;
        if (p.Pipes.TryGetValue(key, out PipelineId id)) return id;
        var d = new PipelineDesc { Vs = p.Vs, Fs = p.Fs, Label = $"vk {p.Label} {f} {add}" };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = f;
        d.Targets.Samples = 1;
        if (add == Blend.Add) d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One);
        else if (add == Blend.Over)
            d.Blend[0] = new AttachmentBlend(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha);
        id = _s.Dev!.CreatePipeline(in d);
        if (id.IsNull) log?.Always($"PostChainVk: the device REFUSED pipeline {d.Label} (logged above) -- that post draw sits out");
        p.Pipes[key] = id;
        return id;
    }

    private static int LevelW(PostVkState s, int i) => Math.Max(s.W >> (i + 1), 1);
    private static int LevelH(PostVkState s, int i) => Math.Max(s.H >> (i + 1), 1);

    /// <summary>The device's samplers, block buffer and the chain's three programs, made once. A refusal is said
    /// once and fails the frame: there is no other encode on Vulkan.</summary>
    internal static bool Ensure(VkBackend vk, ExtenderLog log)
    {
        PostVkState s = _s;
        if (s.Dev != vk) _s = s = new PostVkState { Dev = vk };
        if (s.Tried) return !s.Failed;
        s.Tried = true;
        s.Ubo = vk.CreateBuffer(new BufferDesc(UboBytes, BufferUse.Uniform, MemoryKind.Upload, "vk post blocks"));
        s.Linear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Nearest, Wrap.Clamp));
        s.Nearest = vk.CreateSampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.Nearest, Wrap.Clamp));
        s.Repeat = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Linear, Wrap.Repeat));
        s.Down = Make(vk, PostChain.ShdBloomDownFs, "bloom downsample", out bool downStock, p =>
        {
            s.D.Lod = p.Loc("uLod"); s.D.Texel = p.Loc("uTexel"); s.D.First = p.Loc("uFirst");
            s.D.Thresh = p.Loc("uThresh"); s.D.Exposure = p.Loc("uExposure");
        }, log);
        s.Up = Make(vk, PostChain.ShdBloomUpFs, "bloom upsample", out bool upStock, p => { s.U.Lod = p.Loc("uLod"); s.U.Texel = p.Loc("uTexel"); }, log);
        s.BloomStock = downStock && upStock;
        s.Resolve = Make(vk, PostChain.ShdResolveFs, "resolve", out _, p =>
        {
            ResolveLocs r = s.R;
            r.SkyRaw = p.Loc("uSkyRaw"); r.SkyBand = p.Loc("uSkyBand"); r.InvVp = p.Loc("uInvVP"); r.Eye = p.Loc("uEyePos");
            r.VFog = p.Loc("uVFog"); r.VFogVp = p.Loc("uVFogVp"); r.SkyBare = p.Loc("uSkyBare"); r.OverlayOn = p.Loc("uOverlayOn");
            r.Strength = p.Loc("uStrength"); r.Mode = p.Loc("uMode"); r.Knee = p.Loc("uKnee"); r.Exposure = p.Loc("uExposure");
            r.AcesMix = p.Loc("uAcesMix"); r.Dither = p.Loc("uDither"); r.Viz = p.Loc("uViz");
        }, log);
        s.Failed = s.Down == null || s.Up == null || s.Resolve == null || s.Ubo.IsNull;
        if (s.Failed) log.Always("PostChainVk: a post program or its block was REFUSED (logged above) -- the Vulkan frame cannot reach the screen");
        return !s.Failed;
    }

    /// <summary>Post stage <paramref name="fsRef"/> over the full-screen vertex stage, as GL compiles the pair;
    /// <paramref name="stock"/> when the embedded texts are built: no override reached either, or one was refused.</summary>
    private static GuiVkProg? Make(VkBackend vk, string fsRef, string label, out bool stock, Action<GuiVkProg> locate, ExtenderLog log)
    {
        stock = ShaderTexts.Pair(PostChain.ShdPostVs, ShaderStages.Source(PostChain.ShdPostVs), fsRef, ShaderStages.Source(fsRef),
                                 log, $"PostChainVk {label}", out string vs, out string fs);
        return Program(vk, vs, fs, label, locate, log);
    }

    /// <summary>The bloom levels for a <paramref name="w"/> x <paramref name="h"/> scene, as GL sizes its chain.</summary>
    private static bool Targets(VkBackend vk, PostVkState s, int w, int h, ExtenderLog log)
    {
        if (s.W == w && s.H == h && s.Levels > 0) return true;
        foreach (ImageId i in s.Bloom) vk.Destroy(i);
        Array.Clear(s.Bloom);
        s.W = w; s.H = h;
        int levels = 0;
        while (levels < MaxLevels && Math.Min(LevelW(s, levels), LevelH(s, levels)) >= 8) levels++;
        s.Levels = Math.Max(levels, 1);
        for (int i = 0; i < s.Levels; i++)
        {
            s.Bloom[i] = Target(vk, LevelW(s, i), LevelH(s, i), $"vk bloom {i}", ImageUse.Storage | ImageUse.CopySrc, i == 0 ? AliasLanes.Weight : null);
            if (s.Bloom[i].IsNull)
            {
                s.Levels = 0;
                log.Always($"PostChainVk: bloom level {i} was REFUSED (logged above) -- the Vulkan frame cannot reach the screen");
                return false;
            }
        }
        return true;
    }

    /// <summary>A half-float post image a pass renders and a later one samples, with <paramref name="extra"/> uses.</summary>
    internal static ImageId Target(VkBackend vk, int w, int h, string label, ImageUse extra = ImageUse.None, string? alias = null)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1,
                                       ImageUse.Sampled | ImageUse.ColorTarget | extra, label, alias));
}
