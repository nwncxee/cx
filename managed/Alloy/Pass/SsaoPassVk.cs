using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary><see cref="SsaoPass"/> on Vulkan: the same horizon estimate and depth-aware blur from the same
/// <see cref="SsaoBlock"/> and overridable stages, over the frame's prepass depth, then the accumulator over its
/// own history pair -- which is what the lit stage samples as <c>uSsao</c>. It reprojects through the CAMERA: the
/// velocity target is painted after the colour pass this estimate is read in.</summary>
internal static unsafe class SsaoPassVk
{
    /// <summary>Everything on one device, replaced whole when the device is.</summary>
    private sealed class SsaoVkState
    {
        public VkBackend? Dev;
        public PipelineId Prog, ProgBlur, ProgAccum;
        public BufferId Ubo;
        /// <summary>The raw estimate and the blurred one; the raw stage writes <see cref="Out"/> itself when
        /// there is no blur, so the next reader finds one name either way.</summary>
        public ImageId Raw, Out;
        /// <summary>The accumulator's pair, Rg16F (visibility, view distance): <see cref="HistAt"/> names the one
        /// last frame wrote, and this frame writes the other. Neither is aliased -- a later frame reads them.</summary>
        public ImageId Hist0, Hist1;
        public int HistAt;
        /// <summary>The history holds the frame before this one, seen through <see cref="PrevVpClean"/>.</summary>
        public bool HistValid;
        /// <summary>The unjittered view-projection the pass last recorded under, and that frame's number: the
        /// history is only a frame old while the number is this frame's minus one.</summary>
        public Matrix4x4 PrevVpClean;
        public int PrevSeq = -2;
        /// <summary>The nearest sampler the accumulator reads the history through for its blend; its depth test
        /// fetches the texel instead.</summary>
        public SamplerId Linear;
        public int W, H, SrcW, SrcH, Runs, AccumRuns;
        /// <summary>The <see cref="AlloyRenderer.FrameSeq"/> whose estimate is in <see cref="Visibility"/>; -1 = none.</summary>
        public int LiveFrame = -1;
        public bool Failed, Said, Half, Accumulated;
        public SsaoBlock Blk;
    }

    private static SsaoVkState _s = new();

    /// <summary>Did the device refuse this pass? The SSAO video option is then inert.</summary>
    internal static bool Failed => _s.Failed;

    /// <summary>The <c>[alloy]</c> period term: how often the estimate ran, and at what extent.</summary>
    internal static string Stats(int frames)
    {
        SsaoVkState s = _s;
        int n = s.Runs, a = s.AccumRuns; s.Runs = 0; s.AccumRuns = 0;
        return s.Failed ? "ao FAILED" : n == 0 ? "ao idle"
             : $"ao {(float)n / MathF.Max(frames, 1):F2} runs/frame at {s.W}x{s.H}{(s.Half ? $" (half of {s.SrcW}x{s.SrcH})" : "")}"
             + (a == 0 ? "" : $" ({(float)a / MathF.Max(frames, 1):F2} accumulated)");
    }

    /// <summary>This frame's visibility is in <see cref="Visibility"/>: the lit stage may read it. False before the
    /// pass records, since a resize there destroys the target an earlier bind of this frame would hold.</summary>
    internal static bool Live => _s.LiveFrame == AlloyRenderer.FrameSeq;

    /// <summary>...at half the frame's extent, which the lit stage upsamples depth-aware (<c>uSsaoHalf</c>).</summary>
    internal static bool Half => Live && _s.Half;

    /// <summary>The target the lit stage samples, null until the pass has run once: the history the accumulator
    /// just wrote (its red channel), or the blurred estimate on a frame that took the frame whole.</summary>
    internal static ImageId Visibility => _s.Accumulated ? _s.HistAt == 0 ? _s.Hist0 : _s.Hist1 : _s.Out;

    /// <summary>The world edge (an area transition) and a camera cut: the history is another place's, so the
    /// next frame's estimate stands alone.</summary>
    internal static void Revoke() => _s.HistValid = false;

    /// <summary>The frame's estimate into <paramref name="cl"/>, between the depth prepass and the colour pass,
    /// off <paramref name="depth"/> -- the sampled copy of the prepass depth, which is what the lit stage tests
    /// each pixel's ownership against. <paramref name="vp"/> and <paramref name="vpClean"/> are this frame's
    /// jittered and unjittered view-projections, which the accumulator reprojects its history through.</summary>
    internal static void Record(VkBackend vk, CommandList cl, ImageId depth, bool depthFilled, Matrix4x4* proj,
                                in Matrix4x4 vp, in Matrix4x4 vpClean, int* viewport, ExtenderLog log)
    {
        SsaoVkState s = _s;
        if (s.Dev != vk) _s = s = new SsaoVkState { Dev = vk };
        s.LiveFrame = -1;
        s.Accumulated = false;
        if (s.Failed || !SsaoPass.Active || !depthFilled || depth.IsNull) return;
        bool half = SsaoPass.Wanted;
        if (!Ensure(vk, log) || !Targets(vk, viewport[2], viewport[3], half, log)) return;

        int seq = AlloyRenderer.FrameSeq;
        bool reproject = Matrix4x4.Invert(vp, out Matrix4x4 inv);
        bool accum = !s.ProgAccum.IsNull && reproject;
        float w = s.W, h = s.H;
        ref SsaoBlock b = ref s.Blk;
        b.Proj = *proj;
        // this frame's ndc straight into the previous frame's clip space: row-vector order, so the inverse first
        b.Reproj = accum ? inv * s.PrevVpClean : Matrix4x4.Identity;
        b.Jitter = new Vector2(PostChain.JitterX, PostChain.JitterY);
        b.Rt = new Vector4(1f / w, 1f / h, w, h);
        b.Radius = Ao.Radius.V;
        b.Power = Ao.Strength.V * SsaoPass.Strength;
        b.Dirs = Ao.Dirs.V;
        b.Steps = Ao.Steps.V;
        // the cap is a SCREEN reach, so it halves with the target
        b.MaxPx = half ? Ao.MaxPx.V * 0.5f : Ao.MaxPx.V;
        b.Frame = accum ? seq & 63 : 0;
        b.Weight = accum && s.HistValid && s.PrevSeq == seq - 1 ? SsaoPass.HistoryWeight : 0f;
        Span<byte> blk = stackalloc byte[Std140.BlockBytes<SsaoBlock>()];
        blk.Clear();
        MemoryMarshal.Write(blk, in b);
        cl.UpdateBuffer(s.Ubo, 0, blk);

        bool blur = Ao.Blur.V && !s.ProgBlur.IsNull;
        var rect = new Rect(half ? viewport[0] / 2 : viewport[0], half ? viewport[1] / 2 : viewport[1], s.W, s.H);
        ReadOnlySpan<BufferBinding> ubo = [new(UboSlots.Ssao, s.Ubo)];
        RecordStages(cl, s.Prog, blur ? s.ProgBlur : default, s.Raw, s.Out, depth, rect, ubo);
        if (accum)
        {
            ImageId read = s.HistAt == 0 ? s.Hist0 : s.Hist1, write = s.HistAt == 0 ? s.Hist1 : s.Hist0;
            Stage(cl, write, s.ProgAccum, rect, ubo,
                  [new(SsaoPass.SlotDepth, depth), new(SsaoPass.SlotSrc, s.Out), new(SsaoPass.SlotHist, read, s.Linear)]);
            s.HistAt ^= 1;
            s.HistValid = true;
            s.Accumulated = true;
            s.AccumRuns++;
        }
        else s.HistValid = false;
        s.PrevVpClean = vpClean;
        s.PrevSeq = seq;
        s.Half = half;
        s.LiveFrame = seq;
        s.Runs++;
        if (!s.Said)
        {
            s.Said = true;
            log.Always($"SsaoPassVk: LIVE -- horizon visibility at {s.W}x{s.H}"
                     + (half ? $" (HALF of {s.SrcW}x{s.SrcH})" : "") + (blur ? ", blurred" : ", unblurred")
                     + (accum ? $", accumulated over an Rg16F history pair at {SsaoPass.HistoryWeight:F2} "
                              + "(reprojected through the camera; a mover's own occlusion lags where the depth test keeps it)"
                              : ", single-frame (the accumulator did not build)"));
        }
    }

    /// <summary>The raw stage, then the blur when <paramref name="blur"/> is a pipeline; <paramref name="target"/> takes
    /// the last of them. The blur runs BEFORE the accumulator: it folds the Bayer pattern away, and the
    /// accumulator's 3x3 clamp needs a neighbourhood that means something.</summary>
    internal static void RecordStages(CommandList cl, PipelineId prog, PipelineId blur, ImageId raw, ImageId target, ImageId depth,
                                      Rect vp, ReadOnlySpan<BufferBinding> ubo)
    {
        Stage(cl, blur.IsNull ? target : raw, prog, vp, ubo, [new(SsaoPass.SlotDepth, depth)]);
        if (!blur.IsNull) Stage(cl, target, blur, vp, ubo, [new(SsaoPass.SlotDepth, depth), new(SsaoPass.SlotSrc, raw)]);
    }

    /// <summary>The accumulator alone, over <paramref name="src"/> and <paramref name="hist"/> into
    /// <paramref name="write"/>: what the frame records after the blur, and what the golden drives by hand.</summary>
    internal static void RecordAccum(CommandList cl, PipelineId accum, ImageId write, ImageId src, ImageId hist, ImageId depth,
                                     SamplerId linear, Rect vp, ReadOnlySpan<BufferBinding> ubo)
        => Stage(cl, write, accum, vp, ubo,
                 [new(SsaoPass.SlotDepth, depth), new(SsaoPass.SlotSrc, src), new(SsaoPass.SlotHist, hist, linear)]);

    /// <summary>One full-screen stage into <paramref name="target"/>, which it overwrites whole.</summary>
    private static void Stage(CommandList cl, ImageId target, PipelineId p, Rect vp, ReadOnlySpan<BufferBinding> ubo,
                              ReadOnlySpan<ImageBinding> sampled)
    {
        ReadOnlySpan<ColorAttachment> color = [new(target, 0, 0, LoadOp.DontCare, StoreOp.Store, default)];
        cl.BeginRenderPass(new RenderPassDesc(color));
        cl.SetPipeline(p);
        cl.SetViewport(vp.X, vp.Y, vp.W, vp.H);
        cl.BindSet(0, new BindSet(ubo, default, sampled, default));
        cl.Draw(3);
        cl.EndRenderPass();
    }

    /// <summary>The device's two pipelines and the block, made once. The visibility one failing stands the pass
    /// down with a named line; the blur failing only loses the blur, as on GL.</summary>
    private static bool Ensure(VkBackend vk, ExtenderLog log)
    {
        SsaoVkState s = _s;
        if (!s.Prog.IsNull) return true;
        s.Ubo = vk.CreateBuffer(new BufferDesc(Std140.BlockBytes<SsaoBlock>(), BufferUse.Uniform, MemoryKind.Upload, "vk ssao block"));
        s.Prog = Pipeline(vk, SsaoPass.ShdSsaoFs, "vk SSAO visibility", log);
        if (s.Prog.IsNull || s.Ubo.IsNull)
        {
            s.Failed = true;
            log.Always("SsaoPassVk: the visibility pipeline or its block was REFUSED (logged above) -- the pass stands "
                     + "down on this device; the ambient term keeps the material AO alone");
            return false;
        }
        s.ProgBlur = Pipeline(vk, SsaoPass.ShdSsaoBlurFs, "vk SSAO blur", log);
        if (s.ProgBlur.IsNull) log.Always("SsaoPassVk: the blur did not build -- visibility ships unblurred (the 4x4 pattern will show)");
        // the reprojected history lands between texels; the depth test beside it fetches a whole one
        s.Linear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
        s.ProgAccum = s.Linear.IsNull ? default : AccumPipeline(vk, log);
        if (s.ProgAccum.IsNull)
            log.Always("SsaoPassVk: the accumulator did not build -- visibility is single-frame, so one slice per "
                     + "frame shows its pattern (/ao dirs 3 steps 3 trades frame rate for density)");
        return true;
    }

    /// <summary>The accumulator's pipeline: the embedded stage pair, translated and compiled, into an Rg16F
    /// target. A null id, logged, for a text the toolchain refused.</summary>
    internal static PipelineId AccumPipeline(VkBackend vk, ExtenderLog log)
    {
        const string Label = "vk SSAO accumulate";
        VkProgramText x = VkGlsl.Translate(PostChain.FullscreenVs, SsaoPass.AccumFs, VkPrograms.Units);
        if (x.Error != null) { log.Always($"SsaoPassVk: {Label} did not translate -- {x.Error}"); return default; }
        if (x.Loose.Count > 0)
        {
            log.Always($"SsaoPassVk: {Label} declares {x.Loose.Count} loose uniforms ({x.Loose[0].Name} first) -- "
                     + "the pass fills the CxSsao block alone, so it stands down rather than blend with zeros");
            return default;
        }
        byte[]? v = VulkanToolchain.Compile(VkBackend.Translate(x.Vs), $"{Label} vs", ShaderKind.VertexShader, out string ve);
        byte[]? f = VulkanToolchain.Compile(VkBackend.Translate(x.Fs), $"{Label} fs", ShaderKind.FragmentShader, out string fe);
        if (v == null || f == null) { log.Always($"SsaoPassVk: {Label} did not compile -- {(v == null ? ve : fe)}"); return default; }
        var d = new PipelineDesc
        {
            Vs = vk.CreateShader(ShaderStage.Vertex, v, $"{Label} vs"),
            Fs = vk.CreateShader(ShaderStage.Fragment, f, $"{Label} fs"),
            Label = Label,
        };
        d.Targets.Colors[0] = Format.Rg16F;
        d.Targets.ColorCount = 1;
        d.Targets.Samples = 1;
        return d.Vs.IsNull || d.Fs.IsNull ? default : vk.CreatePipeline(in d);
    }

    /// <summary>The stage pair <paramref name="fsRef"/> names, as GL compiles it (its own override and library
    /// swaps), translated and compiled for Vulkan -- or a SPIR-V <c>&lt;fsRef&gt;.shd</c> as the fragment module. A
    /// null id, logged, for a text the toolchain refused.</summary>
    internal static PipelineId Pipeline(VkBackend vk, string fsRef, string label, ExtenderLog log)
    {
        ShaderTexts.Pair(PostChain.ShdPostVs, ShaderStages.Source(PostChain.ShdPostVs), fsRef, ShaderStages.Source(fsRef),
                        log, label, out string vs, out string fs);
        VkProgramText t = VkGlsl.Translate(vs, fs, VkPrograms.Units);
        if (t.Error != null) { log.Always($"SsaoPassVk: {label} did not translate -- {t.Error}"); return default; }
        if (t.Loose.Count > 0)
        {
            // every term of these stages is a CxSsao member: a loose one would be a block this pass never fills
            log.Always($"SsaoPassVk: {label} declares {t.Loose.Count} loose uniforms ({t.Loose[0].Name} first) -- "
                     + "the pass fills the CxSsao block alone, so it stands down rather than draw with zeros");
            return default;
        }
        byte[]? v = VulkanToolchain.Compile(VkBackend.Translate(t.Vs), $"{label} vs", ShaderKind.VertexShader, out string ve);
        byte[]? f = ShaderBinary.Stage(fsRef, ShaderStage.Fragment, t.Opaques, VkBackend.Translate(t.Fs),
                                       $"{label} fs", log, out string fe);
        if (v == null || f == null) { log.Always($"SsaoPassVk: {label} did not compile -- {(v == null ? ve : fe)}"); return default; }
        var d = new PipelineDesc
        {
            Vs = vk.CreateShader(ShaderStage.Vertex, v, $"{label} vs"),
            Fs = vk.CreateShader(ShaderStage.Fragment, f, $"{label} fs"),
            Label = label,
        };
        d.Targets.Colors[0] = Format.R8;
        d.Targets.ColorCount = 1;
        d.Targets.Samples = 1;
        return d.Vs.IsNull || d.Fs.IsNull ? default : vk.CreatePipeline(in d);
    }

    /// <summary>The visibility targets at the extent this frame wants: the frame's, or half of it under
    /// <c>post.halfres</c>. A size change reallocates both.</summary>
    private static bool Targets(VkBackend vk, int srcW, int srcH, bool half, ExtenderLog log)
    {
        SsaoVkState s = _s;
        int w = half ? (srcW + 1) / 2 : srcW, h = half ? (srcH + 1) / 2 : srcH;
        if (!s.Out.IsNull && w == s.W && h == s.H) return true;
        if (w <= 0 || h <= 0) return false;
        s.SrcW = srcW; s.SrcH = srcH;
        vk.Destroy(s.Raw); vk.Destroy(s.Out); vk.Destroy(s.Hist0); vk.Destroy(s.Hist1);
        s.Raw = Target(vk, w, h, "vk ssao raw", AliasLanes.Small);
        s.Out = Target(vk, w, h, "vk ssao out");
        // the history was accumulated on the other grid, and its pair is read a frame later: no alias lane
        s.Hist0 = History(vk, w, h, "vk ssao history 0");
        s.Hist1 = History(vk, w, h, "vk ssao history 1");
        s.HistValid = false;
        s.Accumulated = false;
        if (s.Raw.IsNull || s.Out.IsNull || s.Hist0.IsNull || s.Hist1.IsNull)
        {
            s.Failed = true;
            s.Out = default;
            log.Always($"SsaoPassVk: a {w}x{h} visibility target was REFUSED (logged above) -- the pass stands down on this device");
            return false;
        }
        s.W = w; s.H = h;
        log.Info($"SsaoPassVk: visibility targets {w}x{h} R8"
               + (half ? $" -- HALF of the {srcW}x{srcH} frame (post.halfres), upsampled in the lit stage" : ""));
        return true;
    }

    private static ImageId Target(VkBackend vk, int w, int h, string label, string? alias = null)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8, w, h, 1, 1, 1, 1,
                                       ImageUse.Sampled | ImageUse.ColorTarget, label, alias));

    /// <summary>One half of the accumulator's pair: visibility in red, the view distance it was computed at in
    /// green, which is what the next frame's disocclusion test reads.</summary>
    private static ImageId History(VkBackend vk, int w, int h, string label)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg16F, w, h, 1, 1, 1, 1,
                                       ImageUse.Sampled | ImageUse.ColorTarget, label));
}
