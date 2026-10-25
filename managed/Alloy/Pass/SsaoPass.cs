using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The <c>CxSsao</c> block in the GLSL's own member order (<c>cx_ssaolib.glsl</c>). C# sequential
/// layout IS std140 here: no member is a vec3 or an array, so neither side pads.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SsaoBlock
{
    public Matrix4x4 Proj;
    public Vector4 Rt;
    public float Radius, Power, MaxPx, Weight;
    public int Dirs, Steps, Frame;

    /// <summary>The GLSL against this struct. A member moved in either file stages every term after it into
    /// its neighbour, and a wrong projection row reconstructs every pixel at the wrong depth.</summary>
    internal static void Pin(Checker check)
    {
        var b = default(SsaoBlock);
        Std140.Pin(check, ShaderSource.Get("cx_ssaolib.glsl"), "CxSsao", UboSlots.Ssao, Unsafe.SizeOf<SsaoBlock>(),
        [
            ("uProj", Std140.At(ref b, ref b.Proj)), ("uRt", Std140.At(ref b, ref b.Rt)),
            ("uRadius", Std140.At(ref b, ref b.Radius)), ("uPower", Std140.At(ref b, ref b.Power)),
            ("uMaxPx", Std140.At(ref b, ref b.MaxPx)), ("uWeight", Std140.At(ref b, ref b.Weight)),
            ("uDirs", Std140.At(ref b, ref b.Dirs)), ("uSteps", Std140.At(ref b, ref b.Steps)),
            ("uFrame", Std140.At(ref b, ref b.Frame)),
        ]);
    }
}

/// <summary>Screen-space ambient occlusion as a LIGHTING INPUT, not a post stage (ADR 0021). The client's SSAO
/// option is bit 0 of <c>Camera::m_nFramebufferEffectsEnabled</c>; vanilla runs its pass over the finished
/// frame and darkens direct light with it, while Alloy computes horizon-based visibility (GTAO) from the depth
/// prepass and scales the lit stage's AMBIENT term by it. <c>/ao direct</c> carries it onto the direct terms.</summary>
internal static unsafe class SsaoPass
{
    private static ExtenderLog? _log;
    /// <summary><c>Camera::m_nFramebufferEffectsEnabled</c>: bit 0 is SSAO.</summary>
    private static uint* _effects;
    /// <summary>The <c>enableFBeffects</c> console gate, the engine's outer condition on every FB effect.</summary>
    private static int* _fbEffects;
    /// <summary>The <c>ssao_intensity</c> config global, 0.5 stock. Not global float param 1: that copy is
    /// made by Camera::SetFrameBufferParameters @0x5a6d10 inside the frame loop Alloy owns, so under Alloy it
    /// reads 0 (live: "x0,00 from ssao_intensity", no occlusion at all).</summary>
    private static float* _intensity;
    /// <summary>FramebufferEffect 0 is SSAO (<c>CClientOptions::SetEnableFBEffectSSAO</c> sets effect 0).</summary>
    internal const uint EffectSsao = 1u << 0;
    private const float StockIntensity = 0.5f;

    /// <summary>Everything the device owns, in one object: context loss replaces it, so no handle from the dead
    /// device survives as a field nobody remembered to zero. <see cref="HistValid"/> rides here because a lost
    /// context loses the history with its targets; the world edge clears that one field on its own.</summary>
    private sealed class SsaoState
    {
        /// <summary>The device every id here is a handle on; another one (a new context) is a rebuild.</summary>
        public IDevice? Dev;
        public CommandList? Cmds;
        public ImageId Raw, Out;
        /// <summary>The visibility history pair, RG16F (visibility, view distance), sampled through <see cref="Linear"/>.</summary>
        public ImageId Hist0, Hist1;
        public PipelineId Prog, ProgBlur, ProgTemporal;
        public SamplerId Linear;
        /// <summary>The <see cref="SsaoBlock"/> all three stages share.</summary>
        public BufferId Ubo;
        public SsaoBlock Blk;
        /// <summary>The inputs this pass reads but does not own, and the GL names they were borrowed under: a new
        /// name (a resized depth target) is a new borrow.</summary>
        public ImageId Depth, Vel;
        public uint DepthName, VelName;
        public int W, H, HistCur;
        /// <summary>The DISPLAY-space extent the targets were sized against, for the arm-time line and the
        /// stats term: <see cref="W"/> x <see cref="H"/> is half of it under <c>post.halfres</c>.</summary>
        public int SrcW, SrcH;
        public bool Built, Failed, HistValid;
        /// <summary>The GL name of the target the lit stage reads this frame: the blurred estimate, or the history
        /// the accumulator just wrote.</summary>
        public uint LiveOut;
    }

    /// <summary>Texture slots, the <c>layout(binding)</c> of each sampler in the three stages.</summary>
    private const uint SlotDepth = 0, SlotSrc = 1, SlotHist = 2, SlotVel = 3;
    /// <summary>The per-pass descriptor set.</summary>
    private const int PassSet = 1;
    /// <summary>The command arena: three stages of about 200 bytes each, with room to spare.</summary>
    private const int ArenaBytes = 1024;

    private static SsaoState _gl = new();
    private static int _runs, _temporalRuns;
    /// <summary>True from this frame's pass until the next: the lit stage may read the texture.</summary>
    internal static bool Live { get; private set; }

    /// <summary>The lit stage's <c>uAoDirect</c>, read at every lit program bind.</summary>
    internal static float Direct => Ao.Direct.V;
    /// <summary><c>post.halfres</c>: the pass runs at half the frame's extent (a quarter of the pixels) and
    /// the lit stage upsamples it depth-aware. The estimate is coarser per frame, which is exactly what the
    /// temporal accumulator and TAA are there to average. Ships OFF: on a fragment-bound frame the lit
    /// stage's 4-tap upsample costs more than the quarter-size pass saves (review 2026-09-18).</summary>
    internal static bool Wanted => Post.HalfRes.V;
    /// <summary>...as it actually resolved THIS frame: what the lit stage's <c>uSsaoHalf</c> carries.</summary>
    internal static bool Half { get; private set; }
    /// <summary>Does this pass want the velocity target this frame? The motion pass runs ahead of it when so.</summary>
    internal static bool TemporalWanted => Active && Ao.Temporal.V > 0f;
    /// <summary><c>ao.temporal</c>'s Changed: the history was accumulated at another weight.</summary>
    internal static void DropHistory() => _gl.HistValid = false;

    internal const string ShdSsaoFs = "cx_ssao_fs", ShdSsaoBlurFs = "cx_ssaoblur_fs";

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _effects = (uint*)Native.Sym(SymbolMap.FbEffectsMask);
        _fbEffects = (int*)Native.Sym(SymbolMap.EnableFBEffects);
        _intensity = (float*)Native.Sym(SymbolMap.SsaoIntensity);
        if (_effects == null) log.Info("SsaoPass: Camera::m_nFramebufferEffectsEnabled is unresolved -- the SSAO option stays inert");
        KnobSurface.Note(static () => _effects == null ? null
            : $"the client's SSAO video option (settings.tml graphics.fbo.ssao.enabled = {(OptionOn ? 1 : 0)}) is the Alloy "
            + $"lit pass's (#379): {(_gl.Failed ? "the pass did NOT build -- the option is inert"
                                     : !Ao.On.V ? "BYPASSED by /ao on 0"
                                     : OptionOn ? "horizon occlusion from the depth prepass scales the ambient term"
                                     : "off")}");
        log.Info($"SsaoPass: attached -- {Describe()}");
    }

    /// <summary>The option's bit under the console gate, the engine's own condition for the effect.</summary>
    private static bool OptionOn => _effects != null && (*_effects & EffectSsao) != 0 && (_fbEffects == null || *_fbEffects != 0);
    internal static bool Active => Ao.On.V && !_gl.Failed && OptionOn;
    /// <summary>The option's intensity as a multiplier on the exponent: stock 0.5 is 1x; an unresolved or
    /// zeroed config reads as stock rather than as "no occlusion".</summary>
    private static float Strength
    {
        get { float i = _intensity == null ? StockIntensity : *_intensity; return (i > 0f ? i : StockIntensity) / StockIntensity; }
    }

    internal static string Describe()
        => $"ao {(_gl.Failed ? "FAILED" : !Ao.On.V ? "BYPASSED" : OptionOn ? "live" : "idle (option off)")}  radius {Ao.Radius.V:F2}  "
         + $"strength {Ao.Strength.V:F2} (x{Strength:F2} from ssao_intensity)  dirs {Ao.Dirs.V}  steps {Ao.Steps.V}  maxpx {Ao.MaxPx.V:F0}  blur {(Ao.Blur.V ? 1 : 0)}  direct {Ao.Direct.V:F2}  "
         + $"temporal {Ao.Temporal.V:F2}{(Ao.Temporal.V > 0f ? "" : " (single-frame; today's look is temporal 0 dirs 3 steps 3)")}  "
         + $"halfres {(Post.HalfRes.V ? "on" : "off")}";

    internal static string Stats(int frames)
    {
        int n = _runs, t = _temporalRuns; _runs = 0; _temporalRuns = 0;
        return _gl.Failed ? "ao FAILED" : n == 0 ? "ao idle"
             : $"ao {(float)n / MathF.Max(frames, 1):F2} runs/frame at {_gl.W}x{_gl.H}{(Half ? $" (half of {_gl.SrcW}x{_gl.SrcH})" : "")}"
             + (t == 0 ? "" : $" ({(float)t / MathF.Max(frames, 1):F2} temporal)");
    }

    /// <summary>Context loss: the whole GL state goes at once, the build verdict with it -- a program that did
    /// not build on the dead context may build on the next.</summary>
    internal static void Forget()
    {
        _gl = new SsaoState();
        Live = false; Half = false;
    }

    /// <summary>World edge: the history is another area's; the next temporal pass takes the frame whole.</summary>
    internal static void Revoke() => _gl.HistValid = false;

    /// <summary>The pass, between the depth prepass and the colour pass: visibility into the raw target, the
    /// depth-aware 4x4 blur, and with <c>temporal</c> above 0 a reprojected blend with last frame's history
    /// clamped to the estimate's 3x3 range. Recorded through the render seam; true when it reached the device,
    /// which leaves the frame's target, viewport and depth state for the caller to re-enter. Hot.</summary>
    internal static bool Run(uint depthTex, bool depthFilled, Matrix4x4* proj, int* viewport, int frameSeq)
    {
        var g = _gl;
        Live = false;
        Half = false;
        if (!Active || !depthFilled || depthTex == 0 || _log == null) return false;
        IDevice? dev = AlloyDevice.Get(_log);
        if (dev == null) return false;
        if (g.Dev != dev) _gl = g = new SsaoState { Dev = dev };
        if (!g.Built) EnsurePrograms(dev);
        bool half = Wanted;
        if (g.Failed || !EnsureTargets(dev, viewport[2], viewport[3], half)) return false;
        bool temporal = Ao.Temporal.V > 0f && !g.ProgTemporal.IsNull && MotionVectors.Painted && MotionVectors.Tex != 0;
        ImageId depth = Borrowed(dev, ref g.Depth, ref g.DepthName, depthTex, "frame depth");
        ImageId vel = temporal ? Borrowed(dev, ref g.Vel, ref g.VelName, MotionVectors.Tex, "velocity") : default;
        float w = g.W, h = g.H;
        ref SsaoBlock b = ref g.Blk;
        b.Proj = *proj;
        b.Rt = new Vector4(1f / w, 1f / h, w, h);
        b.Radius = Ao.Radius.V;
        b.Power = Ao.Strength.V * Strength;
        b.Dirs = Ao.Dirs.V;
        b.Steps = Ao.Steps.V;
        // the cap is a SCREEN reach, so it halves with the target: 96 half-res texels would be 192 of the frame's
        b.MaxPx = half ? Ao.MaxPx.V * 0.5f : Ao.MaxPx.V;
        b.Frame = temporal ? frameSeq & 63 : 0;
        b.Weight = temporal && g.HistValid ? Ao.Temporal.V : 0f;
        bool blur = Ao.Blur.V && !g.ProgBlur.IsNull;
        ImageId histRead = g.HistCur == 0 ? g.Hist0 : g.Hist1, histWrite = g.HistCur == 0 ? g.Hist1 : g.Hist0;
        var vp = new Rect(half ? viewport[0] / 2 : viewport[0], half ? viewport[1] / 2 : viewport[1], g.W, g.H);

        CommandList cl = g.Cmds!;
        cl.Reset();
        cl.Immediate = Renderer.RhiImmediate.V ? dev : null;
        Span<byte> blk = stackalloc byte[Std140.BlockBytes<SsaoBlock>()];
        MemoryMarshal.Write(blk, in g.Blk);
        cl.UpdateBuffer(g.Ubo, 0, blk);
        ReadOnlySpan<BufferBinding> ubo = [new(UboSlots.Ssao, g.Ubo)];
        Stage(cl, blur || temporal ? g.Raw : g.Out, g.Prog, vp, ubo, [new(SlotDepth, depth)]);
        // #390: the blur FIRST -- it folds the Bayer pattern away, and the accumulator's 3x3 clamp needs a
        // neighbourhood that means something, which four fetches a pixel do not give it.
        ImageId blurred = g.Raw;
        if (blur)
        {
            Stage(cl, g.Out, g.ProgBlur, vp, ubo, [new(SlotDepth, depth), new(SlotSrc, g.Raw)]);
            blurred = g.Out;
        }
        if (temporal)
            Stage(cl, histWrite, g.ProgTemporal, vp, ubo,
                  [new(SlotDepth, depth), new(SlotSrc, blurred), new(SlotHist, histRead, g.Linear), new(SlotVel, vel)]);
        if (cl.Immediate != null ? cl.Faulted : !dev.Submit(cl))
        {
            g.Failed = true;
            _log.Always("SsaoPass: the device REFUSED the pass (logged above) -- it stands down on this context; the ambient term keeps the material AO alone");
            return true;
        }
        // the history IS this frame's output when the accumulator ran: the lit stage reads its red channel
        g.LiveOut = AlloyDevice.GlName(temporal ? histWrite : g.Out);
        if (temporal) { g.HistCur ^= 1; g.HistValid = true; _temporalRuns++; }
        else g.HistValid = false;
        Half = half;
        Live = true;
        _runs++;
        return true;
    }

    /// <summary>One full-screen stage into <paramref name="target"/>, which it overwrites whole.</summary>
    private static void Stage(CommandList cl, ImageId target, PipelineId p, Rect vp, ReadOnlySpan<BufferBinding> ubo, ReadOnlySpan<ImageBinding> sampled)
    {
        ReadOnlySpan<ColorAttachment> color = [new(target, 0, 0, LoadOp.DontCare, StoreOp.Store, default)];
        cl.BeginRenderPass(new RenderPassDesc(color));
        cl.SetPipeline(p);
        cl.SetViewport(vp.X, vp.Y, vp.W, vp.H);
        cl.BindSet(PassSet, new BindSet(ubo, default, sampled, default));
        cl.Draw(3);
        cl.EndRenderPass();
    }

    /// <summary><paramref name="name"/> under the id <paramref name="id"/> holds, borrowed again when the owner's
    /// name changed since.</summary>
    private static ImageId Borrowed(IDevice dev, ref ImageId id, ref uint had, uint name, string label)
    {
        if (name == had && !id.IsNull) return id;
        dev.Destroy(id);
        had = name;
        return id = AlloyDevice.Borrow(name, label);
    }

    /// <summary>From every lit-program bind: the visibility texture on its unit, or nothing when the pass
    /// did not run. Per bind and not once per frame because the frame snapshot takes the same unit at
    /// FbCopy, and lit additive draws follow it. Leaves unit 0 current for the batch loop.</summary>
    internal static void BindForDraw(bool on, uint sceneDepth, bool depthToo = false)
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.Ssao);
        Gl.BindTexture(Gl.TEXTURE_2D, on ? _gl.LiveOut : 0);
        // ...and the depth it was computed FROM, so the lit stage can refuse a pixel that is not its own.
        // depthToo: the decal layer's writer reads that depth with no visibility buffer behind it.
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.SsaoDepth);
        Gl.BindTexture(Gl.TEXTURE_2D, on || depthToo ? sceneDepth : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>The visibility targets at the extent this frame wants them: the frame's, or half of it
    /// under <c>post.halfres</c>. A size change reallocates and drops the history, which was accumulated on
    /// the other grid.</summary>
    private static bool EnsureTargets(IDevice dev, int srcW, int srcH, bool half)
    {
        var g = _gl;
        int w = half ? (srcW + 1) / 2 : srcW, h = half ? (srcH + 1) / 2 : srcH;
        if (!g.Out.IsNull && w == g.W && h == g.H) return true;
        if (w <= 0 || h <= 0) return false;
        g.SrcW = srcW; g.SrcH = srcH;
        dev.Destroy(g.Raw); dev.Destroy(g.Out); dev.Destroy(g.Hist0); dev.Destroy(g.Hist1);
        g.Raw = dev.CreateImage(Target(Format.R8, w, h, "ssao raw"));
        g.Out = dev.CreateImage(Target(Format.R8, w, h, "ssao out"));
        g.Hist0 = dev.CreateImage(Target(Format.Rg16F, w, h, "ssao history 0"));
        g.Hist1 = dev.CreateImage(Target(Format.Rg16F, w, h, "ssao history 1"));
        g.HistValid = false;
        g.LiveOut = 0;   // it named one of the destroyed targets
        if (g.Raw.IsNull || g.Out.IsNull || g.Hist0.IsNull || g.Hist1.IsNull)
        {
            g.Failed = true;
            g.Out = default;
            _log?.Always($"SsaoPass: the device REFUSED a {w}x{h} visibility target (logged above) -- the pass stands down on this context");
            return false;
        }
        g.W = w; g.H = h;
        _log?.Info($"SsaoPass: visibility targets {w}x{h} R8"
                 + (half ? $" -- HALF of the {srcW}x{srcH} frame (post.halfres), upsampled depth-aware in the lit stage"
                         : $" -- the {srcW}x{srcH} frame's own extent"));
        return true;
    }

    private static ImageDesc Target(Format f, int w, int h, string label)
        => new(ImageKind.Tex2D, f, w, h, 1, 1, 1, 1, ImageUse.Sampled | ImageUse.ColorTarget, label);

    /// <summary>The three pipelines once per device. The visibility one failing drops the pass with a named
    /// line; the blur or the accumulator failing only loses that stage.</summary>
    private static void EnsurePrograms(IDevice dev)
    {
        var g = _gl;
        g.Built = true;
        g.Cmds = new CommandList("ssao", ArenaBytes, _log);
        g.Ubo = dev.CreateBuffer(new BufferDesc(Std140.BlockBytes<SsaoBlock>(), BufferUse.Uniform, MemoryKind.Upload, "ssao block"));
        // #390: LINEAR -- the reprojected history lands between texels; the blur reads it by texelFetch.
        g.Linear = dev.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
        ShaderId vs = dev.CreateShader(new ShaderDesc(ShaderStage.Vertex, PostChain.ShdPostVs, ShaderStages.Source(PostChain.ShdPostVs), "SSAO post vs"));
        g.Prog = Pipeline(dev, vs, ShdSsaoFs, ShaderStages.Source(ShdSsaoFs), Format.R8, "SSAO visibility");
        if (g.Prog.IsNull || g.Ubo.IsNull)
        {
            g.Failed = true;
            _log?.Always("SsaoPass: the visibility pipeline did not build -- the pass stands down; the ambient term keeps the material AO alone");
            return;
        }
        g.ProgBlur = Pipeline(dev, vs, ShdSsaoBlurFs, ShaderStages.Source(ShdSsaoBlurFs), Format.R8, "SSAO blur");
        if (g.ProgBlur.IsNull) _log?.Always("SsaoPass: the blur did not build -- visibility ships unblurred (the 4x4 pattern will show)");
        // #390: embedded only, like MotionVectors -- not an override point.
        ShaderId fvs = dev.CreateShader(new ShaderDesc(ShaderStage.Vertex, null, PostChain.FullscreenVs, "SSAO fullscreen vs"));
        g.ProgTemporal = Pipeline(dev, fvs, null, TemporalFs, Format.Rg16F, "SSAO temporal");
        if (g.ProgTemporal.IsNull) _log?.Always("SsaoPass: the temporal stage did not build -- visibility is single-frame (/ao dirs 3 steps 3 restores today's density)");
    }

    /// <summary>A full-screen stage's pipeline: no depth, no blend, no cull, one colour target.</summary>
    private static PipelineId Pipeline(IDevice dev, ShaderId vs, string? fsRef, string fs, Format target, string label)
    {
        var d = new PipelineDesc
        {
            Vs = vs,
            Fs = dev.CreateShader(new ShaderDesc(ShaderStage.Fragment, fsRef, fs, label)),
            Label = label,
        };
        d.Targets.Colors[0] = target;
        d.Targets.ColorCount = 1;
        d.Targets.Samples = 1;
        return dev.CreatePipeline(in d);
    }

    /// <summary>The 4x4 depth-aware box that folds the pattern away: one full period of the Bayer tile,
    /// weighted by view-distance similarity so occlusion never bleeds across a silhouette. The depth is
    /// addressed by UV, never by texel: under <c>post.halfres</c> this stage runs at half the depth target's
    /// extent, and a <c>texelFetch</c> would read the top-left quarter of the depth for the whole screen.</summary>
    internal static string SsaoBlurFs => ShaderSource.Get("cx_ssaoblur_f.glsl");

    /// <summary>The accumulator. Velocity is <c>prev - cur</c> in this target's pixels, so it is uv
    /// through its own size; the history's stored view distance against this pixel's is the disocclusion test
    /// (10% relative), and an off-screen or sky sample takes the current estimate whole.</summary>
    internal static string TemporalFs => ShaderSource.Get("cx_ssaotemp_f.glsl");

    /// <summary>Off-device: the two stages compose on the shared reconstruction, the gate is the engine's
    /// effect bit, and the lit stage takes the result on the ambient term only.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var g = _gl;
        ExtenderLog? quiet = _log; _log = null;
        var check = new Checker(log);
        try
        {
            string fs = ShaderStages.Source(ShdSsaoFs), bl = ShaderStages.Source(ShdSsaoBlurFs);
            check.Expect(fs.Contains("float CxViewDist(float depth)") && bl.Contains("float CxViewDist(float depth)"), "both stages carry the reconstruction");
            check.Expect(fs.Contains("uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z)"), "the linearisation is the particle fade's (uProj[3].z / (ndc + uProj[2].z))");
            check.Expect(ShaderContract.SlotSafe(fs, "uDepth") && ShaderContract.SlotSafe(bl, "uSrc", "uDepth"), "the samplers the C# side binds are declared");
            check.Expect(ShaderSource.Get("cx_ssaolib.glsl").Contains($"layout(binding = {SlotDepth}) uniform sampler2D uDepth;")
              && SsaoBlurFs.Contains($"layout(binding = {SlotSrc}) uniform sampler2D uSrc;")
              && TemporalFs.Contains($"layout(binding = {SlotSrc}) uniform sampler2D uRaw;")
              && TemporalFs.Contains($"layout(binding = {SlotHist}) uniform sampler2D uHist;")
              && TemporalFs.Contains($"layout(binding = {SlotVel}) uniform sampler2D uVel;"),
              "every sampler the three stages read sits on the slot the recorded BindSet names (the seam sets no sampler uniform)");
            check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("occl *= mix(1.0, CxSsaoAt(ivec2(gl_FragCoord.xy), aoDepth, aoSlope), uAlphaCut >= 0.0 ? uAoCutout : 1.0);")
              && AlloyRenderer.OwnedLitFsMain.Contains("* ambShape * occl"), "the lit stage folds it into occl, which scales the AMBIENT term");
            // post.halfres: the pass is a quarter of the pixels and the LIT stage is the upsampler -- there
            // is no apply pass to put a bilateral filter in, the consumer fetches per fragment.
            check.Expect(AlloyRenderer.OwnedLitFsHead.Contains("float CxSsaoAt(ivec2 pix, float myDepth, float slope)")
              && AlloyRenderer.OwnedLitFsHead.Contains("if (uSsaoHalf == 0) return texelFetch(uSsao, pix, 0).r;"),
              "the half-res upsample is the lit stage's, and at full resolution it is the same single fetch as before");
            check.Expect(AlloyRenderer.OwnedLitFsHead.Contains("return wsum > 0.0 ? sum / wsum : near;")
              && AlloyRenderer.OwnedLitFsHead.Contains("if (dz < best) { best = dz; near = v; }")
              && AlloyRenderer.OwnedLitFsHead.Contains("max(1.0 - dz / tol, 0.0)"),
              "...four taps weighted by depth proximity, falling back to the nearest where none agrees");
            check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("float aoSlope = fwidth(gl_FragCoord.z);"),
              "the tolerance is the surface's own depth slope, taken outside every branch (fwidth wants a defined neighbourhood)");
            check.Expect(!SsaoBlurFs.Contains("texelFetch(uDepth"), "the blur addresses the DEPTH by uv: at half res its own texel coordinates are another quarter of the screen");
            check.Expect(!Post.HalfRes.Default, "post.halfres ships OFF -- the 4-tap upsample costs every lit fragment more than the quarter-size pass saves");
            check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("float dirOcc = mix(1.0, occl, uAoDirect);")
              && AlloyRenderer.OwnedLitFsMain.Contains("vec3 diffuse = (sun + sunSss + point) * dirOcc;"),
              "/ao direct is the only route to the DIRECT terms, and 0 leaves them exactly as ADR 0021 had them");
            check.Expect(Ao.Direct.Default == 1f, "...and it ships at 1: ambient-only is invisible in a dark-ambient area (ADR 0021, amended)");
            check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("bool aoMine = uSsaoOn > 0 && aoDz < CX_AO_DEPTH_EPS;")
              && AlloyRenderer.OwnedLitFsMain.Contains("if (aoMine) occl *="),
              "the fetch is per FRAGMENT: the pixel's occlusion must have been computed from this surface's own depth");
            check.Expect(Strength > 0f && Ao.Strength.Min > 0f, "the strength never reads 0 -- a zero exponent is pow(v, 0) = 1, no occlusion at all");
            check.Expect(Describe().Contains("radius") && Describe().Contains("strength") && Describe().Contains("temporal"), "/ao reports its dials");
            // #390: the accumulator reprojects in the velocity target's own units, tests the history's depth,
            // and at temporal 0 the raw stage's noise phase is the single-frame one exactly.
            check.Expect(TemporalFs.Contains("/ vec2(textureSize(uVel, 0))") && TemporalFs.Contains("abs(hd - dist)") && TemporalFs.Contains("texelFetch(uHist,")
              && TemporalFs.Contains("clamp(h.r, mn - PAD, mx + PAD)")
              && ShaderContract.SlotSafe(TemporalFs, "uRaw", "uHist", "uVel", "uDepth"), "the temporal stage reprojects through the velocity target, rejects a depth mismatch and clamps to the neighbourhood");
            check.Expect(fs.Contains("fract(b / 16.0 + float(uFrame) * 0.618034)"), "the raw stage's noise phase is uFrame-rotated, 0 = today's pattern");
            float tp = Ao.Temporal.V; bool hv = g.HistValid;
            g.HistValid = true; DropHistory(); check.Expect(!g.HistValid, "a temporal change drops the history");
            Ao.Temporal.V = 0f; check.Expect(!TemporalWanted, "temporal 0 asks for no velocity target");
            Ao.Temporal.V = tp; g.HistValid = hv;
            check.Expect(Ao.Temporal.TryParse("1", out float tw) && tw == 0.98f, "the history weight never reaches 1: a frozen history");
            check.Expect(Ao.Dirs.Default == 1 && Ao.Steps.Default == 3 && Ao.Temporal.Default > 0f, "slices are what the accumulator averages; the per-frame horizon keeps its full step count");
        }
        finally { _log = quiet; }
        log.Info(check.Ok ? "SsaoPass.SelfCheck: passed" : "SsaoPass.SelfCheck: FAILED");
        return check.Ok;
    }
}
