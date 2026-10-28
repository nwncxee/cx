using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary><see cref="SsaoPass"/> on Vulkan: the same horizon estimate and depth-aware blur, from the same
/// <see cref="SsaoBlock"/> and the same two overridable stages, over the Vulkan frame's prepass depth and into its
/// own R8 targets, which the lit stage samples as <c>uSsao</c>. Single-frame: the temporal accumulator reprojects
/// through the velocity target, whose pass is not on Vulkan, so weight and noise phase are zero here.</summary>
internal static unsafe class SsaoPassVk
{
    /// <summary>Everything on one device, replaced whole when the device is.</summary>
    private sealed class SsaoVkState
    {
        public VkBackend? Dev;
        public PipelineId Prog, ProgBlur;
        public BufferId Ubo;
        /// <summary>The raw estimate and the blurred one; the raw stage writes <see cref="Out"/> itself when
        /// there is no blur, so the lit stage reads one name either way.</summary>
        public ImageId Raw, Out;
        public int W, H, SrcW, SrcH, Runs;
        public bool Failed, Said;
        public SsaoBlock Blk;
    }

    private static SsaoVkState _s = new();

    /// <summary>Did the device refuse this pass? The SSAO video option is then inert.</summary>
    internal static bool Failed => _s.Failed;

    /// <summary>The <c>[alloy]</c> period term: how often the estimate ran, and at what extent.</summary>
    internal static string Stats(int frames)
    {
        SsaoVkState s = _s;
        int n = s.Runs; s.Runs = 0;
        return s.Failed ? "ao FAILED" : n == 0 ? "ao idle"
             : $"ao {(float)n / MathF.Max(frames, 1):F2} runs/frame at {s.W}x{s.H}{(Half ? $" (half of {s.SrcW}x{s.SrcH})" : "")}";
    }

    /// <summary>This frame's visibility is in <see cref="Visibility"/>: the lit stage may read it.</summary>
    internal static bool Live { get; private set; }

    /// <summary>...at half the frame's extent, which the lit stage upsamples depth-aware (<c>uSsaoHalf</c>).</summary>
    internal static bool Half { get; private set; }

    /// <summary>The target the lit stage samples, null until the pass has run once.</summary>
    internal static ImageId Visibility => _s.Out;

    /// <summary>The frame's estimate into <paramref name="cl"/>, between the depth prepass and the colour pass,
    /// off <paramref name="depth"/> -- the sampled copy of the prepass depth, which is what the lit stage tests
    /// each pixel's ownership against. Nothing (and <see cref="Live"/> false) when the pass is idle, the option
    /// is off or the device refused it.</summary>
    internal static void Record(VkBackend vk, CommandList cl, ImageId depth, bool depthFilled, Matrix4x4* proj,
                                int* viewport, ExtenderLog log)
    {
        Live = false;
        Half = false;
        SsaoVkState s = _s;
        if (s.Dev != vk) _s = s = new SsaoVkState { Dev = vk };
        if (s.Failed || !SsaoPass.Active || !depthFilled || depth.IsNull) return;
        bool half = SsaoPass.Wanted;
        if (!Ensure(vk, log) || !Targets(vk, viewport[2], viewport[3], half, log)) return;

        float w = s.W, h = s.H;
        ref SsaoBlock b = ref s.Blk;
        b.Proj = *proj;
        b.Rt = new Vector4(1f / w, 1f / h, w, h);
        b.Radius = Ao.Radius.V;
        b.Power = Ao.Strength.V * SsaoPass.Strength;
        b.Dirs = Ao.Dirs.V;
        b.Steps = Ao.Steps.V;
        // the cap is a SCREEN reach, so it halves with the target, exactly as the GL pass halves it
        b.MaxPx = half ? Ao.MaxPx.V * 0.5f : Ao.MaxPx.V;
        b.Frame = 0;
        b.Weight = 0f;
        Span<byte> blk = stackalloc byte[Std140.BlockBytes<SsaoBlock>()];
        blk.Clear();
        MemoryMarshal.Write(blk, in b);
        cl.UpdateBuffer(s.Ubo, 0, blk);

        bool blur = Ao.Blur.V && !s.ProgBlur.IsNull;
        var vp = new Rect(half ? viewport[0] / 2 : viewport[0], half ? viewport[1] / 2 : viewport[1], s.W, s.H);
        ReadOnlySpan<BufferBinding> ubo = [new(UboSlots.Ssao, s.Ubo)];
        Stage(cl, blur ? s.Raw : s.Out, s.Prog, vp, ubo, [new(SsaoPass.SlotDepth, depth)]);
        // #390: the blur folds the Bayer pattern away; on Vulkan there is no accumulator behind it yet
        if (blur) Stage(cl, s.Out, s.ProgBlur, vp, ubo, [new(SsaoPass.SlotDepth, depth), new(SsaoPass.SlotSrc, s.Raw)]);
        Half = half;
        Live = true;
        s.Runs++;
        if (!s.Said)
        {
            s.Said = true;
            log.Always($"SsaoPassVk: LIVE -- horizon visibility at {s.W}x{s.H}"
                     + (half ? $" (HALF of {s.SrcW}x{s.SrcH})" : "") + (blur ? ", blurred" : ", unblurred")
                     + ", single-frame (the temporal accumulator wants the velocity target, which is not on Vulkan)");
        }
    }

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
        return true;
    }

    /// <summary>The stage pair <paramref name="fsRef"/> names, as GL compiles it (its own override and library
    /// swaps), translated and compiled for Vulkan -- or a SPIR-V <c>&lt;fsRef&gt;.shd</c> as the fragment module. A
    /// null id, logged, for a text the toolchain refused.</summary>
    private static PipelineId Pipeline(VkBackend vk, string fsRef, string label, ExtenderLog log)
    {
        ShaderTexts.Pair(PostChain.ShdPostVs, ShaderStages.Source(PostChain.ShdPostVs), fsRef, ShaderStages.Source(fsRef),
                        log, label, out string vs, out string fs);
        VkProgramText t = VkGlsl.Translate(vs, fs, VkPrograms.Units, glDepth: false);
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
        vk.Destroy(s.Raw); vk.Destroy(s.Out);
        s.Raw = Target(vk, w, h, "vk ssao raw");
        s.Out = Target(vk, w, h, "vk ssao out");
        if (s.Raw.IsNull || s.Out.IsNull)
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

    private static ImageId Target(VkBackend vk, int w, int h, string label)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8, w, h, 1, 1, 1, 1,
                                       ImageUse.Sampled | ImageUse.ColorTarget, label));
}
