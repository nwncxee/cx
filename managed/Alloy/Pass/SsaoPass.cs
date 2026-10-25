using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

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

    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no name from the
    /// dead context survives as a field nobody remembered to zero. <see cref="HistValid"/> rides here because
    /// a lost context loses the history with its targets; the world edge clears that one field on its own.</summary>
    private sealed class SsaoState
    {
        public uint Fbo, Raw, Out, Prog, ProgBlur;
        /// <summary>The visibility history pair, RG16F (visibility, view distance), and its stage.</summary>
        public uint Hist0, Hist1, ProgTemporal;
        /// <summary>Sampler units only: every other term the three stages read is in <see cref="Blk"/>.</summary>
        public int LocDepth, LocBlurSrc, LocBlurDepth, LocTRaw, LocTHist, LocTVel, LocTDepth;
        /// <summary>The terms all three stages share, bound once at the pass's start.</summary>
        public readonly UniformBlock<SsaoBlock> Blk = new();
        public int W, H, HistCur;
        /// <summary>The DISPLAY-space extent the targets were sized against, for the arm-time line and the
        /// stats term: <see cref="W"/> x <see cref="H"/> is half of it under <c>post.halfres</c>.</summary>
        public int SrcW, SrcH;
        public bool Built, Failed, HistValid;
        /// <summary>The target the lit stage reads this frame: the blurred estimate, or the history the
        /// accumulator just wrote.</summary>
        public uint LiveOut;
    }

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
    /// depth-aware 4x4 blur into the one the lit stage reads on <see cref="TextureSlots.Ssao"/>, and -- with
    /// <c>temporal</c> above 0 and a velocity target painted -- a reprojected blend with last frame's history,
    /// clamped to the estimate's 3x3 range. Restores the frame's framebuffer and viewport. Hot.</summary>
    internal static void Run(uint depthTex, bool depthFilled, Matrix4x4* proj, int* viewport, uint restoreFbo, int frameSeq)
    {
        var g = _gl;
        Live = false;
        Half = false;
        if (!Active || !depthFilled || depthTex == 0) return;
        if (!g.Built) EnsurePrograms();
        bool half = Wanted;
        if (g.Failed || !EnsureTargets(viewport[2], viewport[3], half)) return;
        Half = half;
        float w = g.W, h = g.H;
        bool temporal = Ao.Temporal.V > 0f && g.ProgTemporal != 0 && g.Hist0 != 0 && MotionVectors.Painted && MotionVectors.Tex != 0;
        Gl.BindVertexArray(PostChain.FullscreenVao);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.Fbo);
        bool blur = Ao.Blur.V && g.ProgBlur != 0;
        uint histRead = g.HistCur == 0 ? g.Hist0 : g.Hist1, histWrite = g.HistCur == 0 ? g.Hist1 : g.Hist0;
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, blur || temporal ? g.Raw : g.Out, 0);
        Gl.Viewport(half ? viewport[0] / 2 : viewport[0], half ? viewport[1] / 2 : viewport[1], g.W, g.H);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, depthTex);
        ref SsaoBlock b = ref g.Blk.Stage;
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
        // One bind covers all three stages: the block is the same for each, and nothing else takes the point
        // between them.
        g.Blk.Bind(UboSlots.Ssao);
        Gl.UseProgram(g.Prog);
        Gl.Uniform1i(g.LocDepth, 0);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        // #390: the blur FIRST -- it folds the Bayer pattern away, and the accumulator's 3x3 clamp needs a
        // neighbourhood that means something, which four fetches a pixel do not give it.
        uint blurred = g.Raw;
        if (blur)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.Out, 0);
            Gl.ActiveTexture(Gl.TEXTURE0 + 1);
            Gl.BindTexture(Gl.TEXTURE_2D, g.Raw);
            Gl.UseProgram(g.ProgBlur);
            Gl.Uniform1i(g.LocBlurDepth, 0);
            Gl.Uniform1i(g.LocBlurSrc, 1);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            blurred = g.Out;
        }
        g.LiveOut = g.Out;
        if (temporal)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, histWrite, 0);
            Gl.ActiveTexture(Gl.TEXTURE0 + 1); Gl.BindTexture(Gl.TEXTURE_2D, blurred);
            Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, histRead);
            Gl.ActiveTexture(Gl.TEXTURE0 + 3); Gl.BindTexture(Gl.TEXTURE_2D, MotionVectors.Tex);
            Gl.UseProgram(g.ProgTemporal);
            Gl.Uniform1i(g.LocTDepth, 0);
            Gl.Uniform1i(g.LocTRaw, 1);
            Gl.Uniform1i(g.LocTHist, 2);
            Gl.Uniform1i(g.LocTVel, 3);
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
            Gl.ActiveTexture(Gl.TEXTURE0 + 3); Gl.BindTexture(Gl.TEXTURE_2D, 0);
            Gl.ActiveTexture(Gl.TEXTURE0 + 2); Gl.BindTexture(Gl.TEXTURE_2D, 0);
            g.HistCur ^= 1; g.HistValid = true; _temporalRuns++;
            // the history IS this frame's output: the lit stage reads its red channel
            g.LiveOut = histWrite;
        }
        else g.HistValid = false;
        Gl.ActiveTexture(Gl.TEXTURE0 + 1);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, 0);
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, restoreFbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        Live = true;
        _runs++;
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
    private static bool EnsureTargets(int srcW, int srcH, bool half)
    {
        var g = _gl;
        int w = half ? (srcW + 1) / 2 : srcW, h = half ? (srcH + 1) / 2 : srcH;
        if (g.Out != 0 && w == g.W && h == g.H) return true;
        if (w <= 0 || h <= 0) return false;
        g.SrcW = srcW; g.SrcH = srcH;
        if (g.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); g.Fbo = f; }
        RenderTarget.Create(ref g.Raw, Gl.R8, w, h, Gl.NEAREST);
        RenderTarget.Create(ref g.Out, Gl.R8, w, h, Gl.NEAREST);
        // #390: LINEAR -- the reprojected history lands between texels; the blur reads it by texelFetch.
        RenderTarget.Create(ref g.Hist0, Gl.RG16F, w, h, Gl.LINEAR);
        RenderTarget.Create(ref g.Hist1, Gl.RG16F, w, h, Gl.LINEAR);
        g.HistValid = false;
        g.LiveOut = 0;   // it named one of the deleted targets
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.Out, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            g.Failed = true;
            _log?.Info($"SsaoPass: the R8 target is incomplete (0x{status:x}) -- the pass stands down on this context");
            return false;
        }
        g.W = w; g.H = h;
        _log?.Info($"SsaoPass: visibility targets {w}x{h} R8"
                 + (half ? $" -- HALF of the {srcW}x{srcH} frame (post.halfres), upsampled depth-aware in the lit stage"
                         : $" -- the {srcW}x{srcH} frame's own extent"));
        return true;
    }

    /// <summary>Both programs once per context. The visibility program failing drops the pass with a named
    /// line; the blur failing only loses the blur.</summary>
    private static void EnsurePrograms()
    {
        var g = _gl;
        g.Built = true;
        string vs = ShaderStages.Source(PostChain.ShdPostVs);
        g.Prog = GlProgram.Graphics(PostChain.ShdPostVs, vs, ShdSsaoFs, ShaderStages.Source(ShdSsaoFs), _log, "SSAO visibility");
        if (g.Prog == 0)
        {
            g.Failed = true;
            _log?.Info("SsaoPass: the visibility program did not build -- the pass stands down; the ambient term keeps the material AO alone");
            return;
        }
        g.LocDepth = GlProgram.Loc(g.Prog, "uDepth");
        g.ProgBlur = GlProgram.Graphics(PostChain.ShdPostVs, vs, ShdSsaoBlurFs, ShaderStages.Source(ShdSsaoBlurFs), _log, "SSAO blur");
        if (g.ProgBlur == 0) { _log?.Info("SsaoPass: the blur did not build -- visibility ships unblurred (the 4x4 pattern will show)"); }
        else
        {
            g.LocBlurSrc = GlProgram.Loc(g.ProgBlur, "uSrc"); g.LocBlurDepth = GlProgram.Loc(g.ProgBlur, "uDepth");
        }
        // #390: embedded only, like MotionVectors -- not an override point.
        g.ProgTemporal = GlProgram.Graphics(PostChain.FullscreenVs, TemporalFs, _log, "SSAO temporal");
        if (g.ProgTemporal == 0) { _log?.Info("SsaoPass: the temporal stage did not build -- visibility is single-frame (/ao dirs 3 steps 3 restores today's density)"); return; }
        g.LocTDepth = GlProgram.Loc(g.ProgTemporal, "uDepth");
        g.LocTRaw = GlProgram.Loc(g.ProgTemporal, "uRaw"); g.LocTHist = GlProgram.Loc(g.ProgTemporal, "uHist");
        g.LocTVel = GlProgram.Loc(g.ProgTemporal, "uVel");
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
