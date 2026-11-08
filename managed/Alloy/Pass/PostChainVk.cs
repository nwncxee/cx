using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Alloy;

/// <summary><see cref="PostChain"/> on Vulkan: the same bloom chain and resolve, from the same overridable stages,
/// over the Vulkan frame's scene colour and depth, into the post stack's first image or the screen. Each bloom
/// level is an image of its own rather than a mip, so no pass samples the image it writes. Also the full-screen
/// helpers every post program on Vulkan draws through.</summary>
internal static unsafe class PostChainVk
{
    /// <summary>The resolve's loose terms: the view and the fog, whose matrices and vectors a block carries. Its
    /// scalars are <see cref="ResolvePush"/>'s.</summary>
    private sealed class ResolveLocs
    {
        public int SkyBand = -1, InvVp = -1, Eye = -1, VFog = -1, VFogVp = -1;
    }

    /// <summary>The resolve's per-pass scalars, pushed rather than staged: the sky's gates and what tells it, the bloom strength,
    /// the grade, the output dither and the stage view.</summary>
    private struct ResolvePush
    {
        public int SkyRaw, SkyBare, SkyCover;
        public float Strength;
        public PostChain.GradeTerms Grade;
        public float Dither;
        public int Viz, OverlayOn;
    }

    /// <summary><c>cx_resolve_f</c>'s own push members, each beside the field that fills it.</summary>
    private static VkPushTail ResolveTail()
    {
        var p = new ResolvePush();
        return VkPushTail.Of<ResolvePush>(
            VkPush.Of("int", "uSkyRaw", ref p, ref p.SkyRaw),
            VkPush.Of("int", "uSkyBare", ref p, ref p.SkyBare),
            VkPush.Of("int", "uSkyCover", ref p, ref p.SkyCover),
            VkPush.Of("float", "uStrength", ref p, ref p.Strength),
            VkPush.Of("int", "uMode", ref p, ref p.Grade.Mode),
            VkPush.Of("float", "uKnee", ref p, ref p.Grade.Knee),
            VkPush.Of("float", "uExposure", ref p, ref p.Grade.Exposure),
            VkPush.Of("float", "uAcesMix", ref p, ref p.Grade.AcesMix),
            VkPush.Of("float", "uDither", ref p, ref p.Dither),
            VkPush.Of("int", "uViz", ref p, ref p.Viz),
            VkPush.Of("int", "uOverlayOn", ref p, ref p.OverlayOn));
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
        /// <summary>The levels' format on this device, settled once: both chains write it, and
        /// <c>cx_bloom_cs</c>'s storage qualifier is built against it.</summary>
        public Format BloomFmt;
        public int W, H, Levels;
        public BufferId Ubo;
        public int UboUsed;
        public SamplerId Linear, Nearest, Repeat;
        public bool Said, VizSaid, EngineSaid;
        /// <summary>Both bloom stages built from the embedded texts: no content override reached either.</summary>
        public bool BloomStock;
        /// <summary>The bloom has said how it runs on this device: as compute, stood down, or refused.</summary>
        public bool ComputeSaid;
        public PipelineId BloomCs;
        public bool CsTried;
        /// <summary>The tile <see cref="BloomCs"/> was built for, which its dispatch counts groups by.</summary>
        public int BloomGroup;
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

    /// <summary>Every way a bloom level is reached: a draw blends into it or a dispatch stores into it, the next
    /// level samples it filtered, and level 0 is copied out. One ask over the union settles the whole chain, and
    /// each level takes the uses it has of it.</summary>
    private const ImageUse BloomUse = ImageUse.Sampled | ImageUse.ColorTarget | ImageUse.Storage | ImageUse.CopySrc;

    /// <summary>What a level asks of no format though <see cref="BloomUse"/> stands for it: level 0 is copied out
    /// whole into a buffer and nothing blits it, so a device that cannot scale the format need not be refused.</summary>
    private const VkFormatFeatureFlags BloomUnneeded = VkFormatFeatureFlags.BlitSrc;

    /// <summary>The loose blocks one frame's post programs fill: the bloom levels, the resolve and the stack.</summary>
    private const int UboBytes = 32 << 10;

    /// <summary>The samplers the post programs declare without a binding: one unit per name, distinct within any
    /// one program.</summary>
    private static readonly IReadOnlyDictionary<string, uint> Units = new Dictionary<string, uint>(StringComparer.Ordinal)
    {
        ["uSrc"] = TextureSlots.PostHdr, ["uHdr"] = TextureSlots.PostHdr, ["uCur"] = TextureSlots.PostHdr,
        ["uBloom"] = TextureSlots.PostBloom, ["uDepth"] = TextureSlots.PostDepth, ["uOverlay"] = TextureSlots.PostOverlay,
        ["uHist"] = TextureSlots.PostHist, ["uVel"] = TextureSlots.PostVel, ["uMask"] = TextureSlots.PostHdr,
        ["uCopy"] = TextureSlots.PostCopy, ["uReact"] = TextureSlots.PostReact, ["uWx"] = TextureSlots.PostWx,
        ["texFBColor"] = TextureSlots.PostHdr, ["texFBDepth"] = TextureSlots.PostDepth,
    };

    /// <summary>What a post draw samples, by the role its declaration names: <c>uSrc</c>/<c>uHdr</c>/<c>uCur</c>
    /// read <see cref="Src"/>, then <c>uBloom</c>, <c>uDepth</c>, <c>uHist</c>, <c>uVel</c>, <c>uReact</c>, <c>uWx</c> and SMAA's four;
    /// any other sampler, or a null image, takes a stand-in. <see cref="Storage"/> is the one storage buffer a draw may read.</summary>
    internal readonly record struct Inputs(ImageId Src, ImageId Bloom = default, ImageId Depth = default,
                                           ImageId Hist = default, ImageId Vel = default, bool HistLinear = false,
                                           ImageId Mask = default, ImageId Edges = default, ImageId Area = default,
                                           ImageId Search = default, ImageId Weights = default,
                                           BufferBinding Storage = default, ImageId React = default, ImageId Wx = default);

    /// <summary>What the resolve tells sky from scene by: the world depth, sky where it is 0, or (<paramref name="Cover"/>)
    /// a sky coverage from 0 to 1 laid on the colour it grades.</summary>
    internal readonly record struct SkyInput(ImageId Image, bool Cover = false);

    /// <summary>How a post draw meets its target: replacing it, adding to it, or alpha-over it.</summary>
    internal enum Blend : byte { None, Add, Over }

    /// <summary>Bloom, then the resolve of <paramref name="hdr"/> into <paramref name="into"/> at
    /// <paramref name="rect"/>, telling sky from scene by <paramref name="sky"/>, dithered only when it is the frame's last write; <c>/post viz 4</c> paints <paramref name="vel"/>
    /// instead. False (logged) when a program or target was refused: the frame has no other way to the screen.</summary>
    internal static bool Record(VkBackend vk, CommandList cl, ImageId hdr, SkyInput sky, ImageId into, bool last,
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
                log.Always(compute ? $"PostChainVk: the compute bloom is LIVE -- one dispatch per level over a {s.BloomGroup}x{s.BloomGroup} tile, no render pass"
                                   : "PostChainVk: the compute bloom STOOD DOWN for the content override of a bloom stage -- it draws as fragments");
            s.ComputeSaid = true;
            cl.EndLabel();
        }

        ResolveLocs r = s.R;
        Matrix4x4 invVp = AlloyRenderer.SkyInvVp;
        Vector3 eye = AlloyRenderer.EyePos;
        Open(s.Resolve!);
        LooseBlock.Set(r.SkyBand, FrameInputs.FogOn && invVp != default ? SceneFog.SkyBand : 0f);
        LooseBlock.Set(r.InvVp, invVp);
        LooseBlock.Set(r.Eye, [eye.X, eye.Y, eye.Z]);
        FogVolume.Upload(r.VFog, r.VFogVp, capture: false);
        var push = new ResolvePush
        {
            SkyRaw = Post.SkyRaw.V && invVp != default ? 1 : 0,
            SkyBare = invVp == default ? 1 : 0,
            SkyCover = sky.Cover ? 1 : 0,
            OverlayOn = StageResources.VkOverlay.IsNull ? 0 : 1,
            Strength = bloom ? Post.Bloom.V : 0f,
            Grade = PostChain.GradeTerms.Now,
            Dither = last ? PostChain.DitherLsb : 0f,
        };
        bool motion = Post.Viz.V == PostChain.VizMotion;
        if (motion && vel.IsNull && !s.VizSaid)
        {
            s.VizSaid = true;
            log.Always("PostChainVk: /post viz 4 shows NOTHING -- the velocity target is the taa stage's, and taa is not "
                     + "drawing this frame; the frame resolves as viz 0");
        }
        push.Viz = motion && vel.IsNull ? 0 : Post.Viz.V;
        cl.BeginLabel("resolve"u8);
        int at = Close(cl, s.Resolve!);
        // push constants are command-buffer state the render pass Draw opens does not reset
        cl.PushConstants(VkGlsl.PushTailAt, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<ResolvePush>(in push)));
        // the velocity viz rides the bloom input: the two views are exclusive by construction
        Draw(cl, s.Resolve!, FormatOf(into), Blend.None, into, LoadOp.Clear, new Rect(rect[0], rect[1], rect[2], rect[3]), at,
             new Inputs(hdr, motion && !vel.IsNull ? vel : bloom ? s.Bloom[0] : default, sky.Image), log);
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
            Draw(cl, s.Down!, s.BloomFmt, Blend.None, s.Bloom[i], LoadOp.DontCare, new Rect(0, 0, LevelW(s, i), LevelH(s, i)),
                 at, new Inputs(i == 0 ? hdr : s.Bloom[i - 1]), null);
        }
        for (int i = s.Levels - 1; i > 0; i--)
        {
            Open(s.Up!);
            LooseBlock.Set(u.Lod, 0);
            LooseBlock.Set(u.Texel, [1f / LevelW(s, i), 1f / LevelH(s, i)]);
            int at = Close(cl, s.Up!);
            Draw(cl, s.Up!, s.BloomFmt, Blend.Add, s.Bloom[i - 1], LoadOp.Load, new Rect(0, 0, LevelW(s, i - 1), LevelH(s, i - 1)),
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
            s.BloomCs = BloomPipe(vk, s, log);
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

    /// <summary>The square tile of a level one group covers, in texels. The wide one is 256 threads, which a device
    /// fills its warps with where 64 left them a quarter idle, and more than Vulkan guarantees a group may hold.</summary>
    private const int BloomGroupWide = 16, BloomGroupNarrow = 8;

    /// <summary>The defines that hand <c>cx_bloom_cs</c> the tile the dispatch counts groups by and, where the
    /// levels are packed, the storage qualifier that agrees with them.</summary>
    private const string BloomGroupDefine = "CX_BLOOM_GROUP", BloomPackedDefine = "CX_BLOOM_PACKED";

    /// <summary>The tile a device whose group holds <paramref name="invocations"/> threads takes. Only the thread
    /// count can refuse the wide tile: Vulkan guarantees a group 128 threads per axis, past either tile's side.</summary>
    private static int BloomGroupFor(int invocations)
        => invocations >= BloomGroupWide * BloomGroupWide ? BloomGroupWide : BloomGroupNarrow;

    /// <summary><c>cx_bloom_cs</c> on one device, its group size the one <paramref name="s"/> then counts groups by
    /// and its storage qualifier the levels' own format. The override is read here rather than left to the pipeline,
    /// since the defines have to reach the text that is built; one that does not build, or that does not take its
    /// group from the define, falls back to the embedded stage, logged.</summary>
    private static PipelineId BloomPipe(VkBackend vk, PostVkState s, ExtenderLog log)
    {
        s.BloomGroup = BloomGroupFor(vk.Caps.MaxComputeInvocations);
        string embedded = ShaderStages.Source(PostChain.ShdBloomCs);
        string over = ShaderResource.Override(PostChain.ShdBloomCs, log) ?? embedded;
        if (!ReferenceEquals(over, embedded) && !over.Contains(BloomGroupDefine, StringComparison.Ordinal))
        {
            log.Always($"PostChainVk: the compute bloom's {PostChain.ShdBloomCs} override names no {BloomGroupDefine} -- a group "
                     + "size of its own covers part of each level and leaves the rest unwritten, so the embedded stage builds");
            over = embedded;
        }
        if (s.BloomFmt == PackedColour.Packed && !over.Contains(BloomPackedDefine, StringComparison.Ordinal))
        {
            log.Always($"PostChainVk: the compute bloom's {PostChain.ShdBloomCs} override names no {BloomPackedDefine} -- the "
                     + "levels are packed on this device and a storage qualifier that disagrees with them stores nothing "
                     + "defined, so the embedded stage builds");
            over = embedded;
        }
        PipelineId pipe = vk.CreateCompute(BloomText(over, s), "bloom compute");
        if (!pipe.IsNull || ReferenceEquals(over, embedded)) return pipe;
        log.Always($"PostChainVk: the compute bloom's {PostChain.ShdBloomCs} override did not build (logged above) -- building the embedded stage");
        return vk.CreateCompute(BloomText(embedded, s), "bloom compute");
    }

    /// <summary><paramref name="src"/> with the tile and, where the levels are packed, the qualifier spliced in. A
    /// qualifier that disagrees with the view makes the store undefined, so the define follows the image.</summary>
    private static string BloomText(string src, PostVkState s)
    {
        string t = ShaderStages.WithDefine(src, $"{BloomGroupDefine} {s.BloomGroup}");
        return s.BloomFmt == PackedColour.Packed ? ShaderStages.WithDefine(t, $"{BloomPackedDefine} 1") : t;
    }

    private static void BloomDispatch(CommandList cl, PostVkState s, in BloomPush p, ImageId src, ImageId dst, int w, int h)
    {
        ReadOnlySpan<ImageBinding> sampled = [new(0, src, s.Linear)];
        ReadOnlySpan<ImageBinding> storage = [new(0, dst)];
        cl.BindSet(0, new BindSet(default, default, sampled, storage));
        cl.PushConstants(0, System.Runtime.InteropServices.MemoryMarshal.AsBytes(new ReadOnlySpan<BloomPush>(in p)));
        cl.Dispatch((w + s.BloomGroup - 1) / s.BloomGroup, (h + s.BloomGroup - 1) / s.BloomGroup);
    }

    /// <summary>The attachment format a post draw into <paramref name="into"/> renders: the image's own (the screen's
    /// is the window's 8-bit format), half-float for an image the device does not know.</summary>
    internal static Format FormatOf(ImageId into)
        => _s.Dev is { } vk && vk.DescOf(into, out ImageDesc d) ? d.Fmt : Format.Rgba16F;

    /// <summary>One full-screen triangle of <paramref name="p"/> into <paramref name="into"/> at <paramref name="vp"/>,
    /// its loose block at <paramref name="at"/>, sampling <paramref name="t"/>. False, with nothing drawn (logged), when
    /// the device refused the pipeline.</summary>
    internal static bool Draw(CommandList cl, GuiVkProg p, Format f, Blend add, ImageId into, LoadOp load, Rect vp, int at,
                              in Inputs t, ExtenderLog? log)
    {
        PipelineId pipe = Pipe(p, f, add, log);
        if (pipe.IsNull) return false;
        PostVkState s = _s;
        Span<ImageBinding> img = stackalloc ImageBinding[SampledMax];
        Span<ImageBinding> sti = stackalloc ImageBinding[(int)StageResources.DrawImgUnits];
        (int n, int ns, bool engine) = Images(p, in t, img, sti);
        Span<BufferBinding> ubo = stackalloc BufferBinding[2];
        int nu = 0;
        if (p.Block.Length > 0) ubo[nu++] = new(UboSlots.Loose, s.Ubo, at, p.Block.Length);
        if (engine)
        {
            int e = AlloyRenderer.VkStageEngine(cl, out BufferId arena, out int len);
            if (e < 0)
            {
                if (!s.EngineSaid) log?.Always($"PostChainVk: post {p.Label} NOT DRAWN -- the frame's uniform arena could not stage the engine block it reads");
                s.EngineSaid = true;
                return false;
            }
            ubo[nu++] = new(UboSlots.Engine, arena, e, len);
        }
        ReadOnlySpan<ColorAttachment> colors = [new(into, 0, 0, load, StoreOp.Store, new Vector4(0f, 0f, 0f, 1f))];
        cl.BeginRenderPass(new RenderPassDesc(colors));
        cl.SetPipeline(pipe);
        cl.SetViewport(vp.X, vp.Y, vp.W, vp.H);
        ReadOnlySpan<BufferBinding> sto = t.Storage.Buffer.IsNull ? default : [t.Storage];
        cl.BindSet(0, new BindSet(ubo[..nu], sto, img[..n], sti[..ns]));
        cl.Draw(3);
        cl.EndRenderPass();
        return true;
    }

    /// <summary>Sampled images one post draw binds: every sampled binding the set has.</summary>
    private const int SampledMax = (int)VkBackend.SampledSlots;

    /// <summary>The set-0 images <paramref name="p"/> declares, bound by role into <paramref name="img"/> (sampled) and
    /// <paramref name="sti"/> (storage), and whether it reads the engine block.</summary>
    internal static (int Sampled, int Storage, bool Engine) Images(GuiVkProg p, in Inputs t, Span<ImageBinding> img,
                                                                  Span<ImageBinding> sti)
    {
        PostVkState s = _s;
        int n = 0, ns = 0;
        bool engine = false;
        foreach (VkOpaque o in p.Opaques)
        {
            if (o.Set != 0) continue;
            engine |= o.Kind == VkBindKind.UniformBlock && o.Binding == UboSlots.Engine;
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
                "uReact" => (t.React, s.Linear),
                "uWx" => (t.Wx, s.Linear),
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
        return (n, ns, engine);
    }

    /// <summary>Off-device: a post program that declares twenty samplers draws with all twenty bound, the bloom's
    /// tile is one every device seats, and the stage the levels' format builds declares that format. Oracles: the
    /// twenty hand-written declarations; Vulkan's guaranteed 128 invocations a group, by hand; the two storage
    /// qualifiers Vulkan spells the two formats, by hand.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "PostChainVk.SelfCheck");
        var p = new GuiVkProg { Opaques = new VkOpaque[20] };
        for (uint i = 0; i < 20; i++) p.Opaques[i] = new VkOpaque($"uContent{i}", "sampler2D", 0, i, VkBindKind.Sampled);
        Span<ImageBinding> img = stackalloc ImageBinding[SampledMax];
        (int n, _, _) = Images(p, new Inputs(default), img, default);
        check.Expect(n == 20 && img[19].Slot == 19, $"a post program's twenty samplers are all bound, not {n}");
        const int guaranteed = 128;
        int floor = BloomGroupFor(guaranteed), seated = BloomGroupFor(1024);
        check.Expect(floor * floor <= guaranteed, $"a device at Vulkan's guaranteed {guaranteed} invocations a group takes a "
                                                + $"{floor}x{floor} bloom tile, which is {floor * floor} threads");
        check.Expect(seated > floor && seated * seated > guaranteed, $"a device that seats 1024 threads a group takes the same "
                                                                   + $"{seated}x{seated} tile the guaranteed one does");
        string src = ShaderStages.Source(PostChain.ShdBloomCs);
        string packed = BloomText(src, new PostVkState { BloomFmt = PackedColour.Packed, BloomGroup = seated });
        string wide = BloomText(src, new PostVkState { BloomFmt = PackedColour.Wide, BloomGroup = seated });
        check.Expect(packed.Contains($"#define {BloomPackedDefine} ", StringComparison.Ordinal)
                  && !wide.Contains($"#define {BloomPackedDefine} ", StringComparison.Ordinal),
            $"the compute bloom is built with {BloomPackedDefine} where the levels are {PackedColour.Packed} and without "
          + $"it where they are {PackedColour.Wide}");
        check.Expect(Declares(src, $"#ifdef {BloomPackedDefine}", "r11f_g11f_b10f") && Declares(src, "#else", "rgba16f"),
            $"the compute bloom declares uDst r11f_g11f_b10f under {BloomPackedDefine} and rgba16f without it, so the "
          + "define the levels' format splices picks the qualifier that agrees with them");
        return check.Ok;
    }

    /// <summary>The first <c>uDst</c> declaration after <paramref name="after"/> in <paramref name="src"/> carries
    /// <paramref name="qualifier"/>, and no other declaration comes between.</summary>
    private static bool Declares(string src, string after, string qualifier)
    {
        int at = src.IndexOf(after, StringComparison.Ordinal);
        if (at < 0) return false;
        int decl = src.IndexOf("uniform image2D uDst;", at, StringComparison.Ordinal);
        return decl > 0 && src[at..decl].Contains($"layout({qualifier}, binding = 0) ", StringComparison.Ordinal);
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
                                       int stage = -1, VkPushTail pushTail = default)
    {
        vs = ShaderResource.Tokens(vs);
        fs = ShaderResource.Tokens(fs);
        if (SharedUnit(fs) is { } clash) log.Always($"PostChainVk: post {label} puts {clash} -- BOTH read whichever image binds last");
        if (StageResources.VkUnits(UnitsFor(fs), fs, stage, -1, StageResources.DrawImgUnits, out string why) is not { } units)
        {
            log.Always($"PostChainVk: post {label} -- {why}");
            return null;
        }
        GuiVkProg? p = GuiVkProg.Build(vk, vs, fs, units, $"post {label}", locate, log, pushTail);
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
        // the chain carries three never-negative channels and no alpha: the threshold floors it and the tent adds
        s.BloomFmt = PackedColour.Or16F(vk, BloomUse, "the bloom levels", log, BloomUnneeded);
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
            r.SkyBand = p.Loc("uSkyBand"); r.InvVp = p.Loc("uInvVP"); r.Eye = p.Loc("uEyePos");
            r.VFog = p.Loc("uVFog"); r.VFogVp = p.Loc("uVFogVp");
        }, log, ResolveTail());
        s.Failed = s.Down == null || s.Up == null || s.Resolve == null || s.Ubo.IsNull;
        if (s.Failed) log.Always("PostChainVk: a post program or its block was REFUSED (logged above) -- the Vulkan frame cannot reach the screen");
        return !s.Failed;
    }

    /// <summary>Post stage <paramref name="fsRef"/> over the full-screen vertex stage, as GL compiles the pair;
    /// <paramref name="stock"/> when the embedded texts are built: no override reached either, or one was refused.</summary>
    private static GuiVkProg? Make(VkBackend vk, string fsRef, string label, out bool stock, Action<GuiVkProg> locate,
                                   ExtenderLog log, VkPushTail pushTail = default)
    {
        stock = ShaderTexts.Pair(PostChain.ShdPostVs, ShaderStages.Source(PostChain.ShdPostVs), fsRef, ShaderStages.Source(fsRef),
                                 log, $"PostChainVk {label}", out string vs, out string fs);
        return Program(vk, vs, fs, label, locate, log, pushTail: pushTail);
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
            s.Bloom[i] = Target(vk, LevelW(s, i), LevelH(s, i), s.BloomFmt, $"vk bloom {i}",
                                 i == 0 ? BloomUse : BloomUse & ~ImageUse.CopySrc,
                                 i == 0 ? AliasLanes.Weight : null);
            if (s.Bloom[i].IsNull)
            {
                s.Levels = 0;
                log.Always($"PostChainVk: bloom level {i} was REFUSED (logged above) -- the Vulkan frame cannot reach the screen");
                return false;
            }
        }
        return true;
    }

    /// <summary>A post image of <paramref name="f"/> a pass renders and a later one samples, with
    /// <paramref name="extra"/> uses.</summary>
    internal static ImageId Target(VkBackend vk, int w, int h, Format f, string label, ImageUse extra = ImageUse.None, string? alias = null)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, f, w, h, 1, 1, 1, 1,
                                       ImageUse.Sampled | ImageUse.ColorTarget | extra, label, alias));
}
