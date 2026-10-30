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

    /// <summary>Texture slots, the <c>layout(binding)</c> of each sampler in the three stages. Read by
    /// <see cref="SsaoPassVk"/> too: one declaration, two recorders.</summary>
    internal const uint SlotDepth = 0, SlotSrc = 1;

    /// <summary>The lit stage's <c>uAoDirect</c>, read at every lit program bind.</summary>
    internal static float Direct => Ao.Direct.V;
    /// <summary><c>post.halfres</c>: the pass runs at half the frame's extent (a quarter of the pixels) and
    /// the lit stage upsamples it depth-aware. The estimate is coarser per frame, which is exactly what the
    /// TAA is there to average. Ships OFF: on a fragment-bound frame the lit stage's 4-tap upsample costs
    /// more than the quarter-size pass saves.</summary>
    internal static bool Wanted => Post.HalfRes.V;

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
            + $"lit pass's (#379): {(SsaoPassVk.Failed ? "the pass did NOT build -- the option is inert"
                                     : !Ao.On.V ? "BYPASSED by /ao on 0"
                                     : OptionOn ? "horizon occlusion from the depth prepass scales the ambient term"
                                     : "off")}");
        log.Info($"SsaoPass: attached -- {Describe()}");
    }

    /// <summary>The option's bit under the console gate, the engine's own condition for the effect.</summary>
    private static bool OptionOn => _effects != null && (*_effects & EffectSsao) != 0 && (_fbEffects == null || *_fbEffects != 0);
    internal static bool Active => Ao.On.V && !SsaoPassVk.Failed && OptionOn;
    /// <summary>The option's intensity as a multiplier on the exponent: stock 0.5 is 1x; an unresolved or
    /// zeroed config reads as stock rather than as "no occlusion".</summary>
    internal static float Strength
    {
        get { float i = _intensity == null ? StockIntensity : *_intensity; return (i > 0f ? i : StockIntensity) / StockIntensity; }
    }

    internal static string Describe()
        => $"ao {(SsaoPassVk.Failed ? "FAILED" : !Ao.On.V ? "BYPASSED" : OptionOn ? "live" : "idle (option off)")}  radius {Ao.Radius.V:F2}  "
         + $"strength {Ao.Strength.V:F2} (x{Strength:F2} from ssao_intensity)  dirs {Ao.Dirs.V}  steps {Ao.Steps.V}  maxpx {Ao.MaxPx.V:F0}  blur {(Ao.Blur.V ? 1 : 0)}  direct {Ao.Direct.V:F2}  "
         + $"halfres {(Post.HalfRes.V ? "on" : "off")}";

    /// <summary>The 4x4 depth-aware box that folds the pattern away: one full period of the Bayer tile,
    /// weighted by view-distance similarity so occlusion never bleeds across a silhouette. The depth is
    /// addressed by UV, never by texel: under <c>post.halfres</c> this stage runs at half the depth target's
    /// extent, and a <c>texelFetch</c> would read the top-left quarter of the depth for the whole screen.</summary>
    internal static string SsaoBlurFs => ShaderSource.Get("cx_ssaoblur_f.glsl");

    /// <summary>Off-device: the two stages compose on the shared reconstruction, the gate is the engine's
    /// effect bit, and the lit stage takes the result on the ambient term only.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        ExtenderLog? quiet = _log; _log = null;
        var check = new Checker(log);
        try
        {
            string fs = ShaderStages.Source(ShdSsaoFs), bl = ShaderStages.Source(ShdSsaoBlurFs);
            check.Expect(fs.Contains("float CxViewDist(float depth)") && bl.Contains("float CxViewDist(float depth)"), "both stages carry the reconstruction");
            check.Expect(fs.Contains("uProj[3].z / (depth * 2.0 - 1.0 + uProj[2].z)"), "the linearisation is the particle fade's (uProj[3].z / (ndc + uProj[2].z))");
            check.Expect(ShaderContract.SlotSafe(fs, "uDepth") && ShaderContract.SlotSafe(bl, "uSrc", "uDepth"), "the samplers the C# side binds are declared");
            check.Expect(ShaderSource.Get("cx_ssaolib.glsl").Contains($"layout(binding = {SlotDepth}) uniform sampler2D uDepth;")
              && SsaoBlurFs.Contains($"layout(binding = {SlotSrc}) uniform sampler2D uSrc;"),
              "every sampler the two stages read sits on the slot the recorded BindSet names (the seam sets no sampler uniform)");
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
            check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("bool aoMine = uSsaoOn > 0 && aoDz < CX_AO_DEPTH_EPS + 0.5 * aoSlope;")
              && AlloyRenderer.OwnedLitFsMain.Contains("if (aoMine) occl *="),
              "the fetch is per FRAGMENT: the pixel's occlusion must have been computed from this surface's own depth, "
              + "within half the fragment's slope (a coarse fragment's depth sits at its area's centre)");
            check.Expect(Strength > 0f && Ao.Strength.Min > 0f, "the strength never reads 0 -- a zero exponent is pow(v, 0) = 1, no occlusion at all");
            check.Expect(Describe().Contains("radius") && Describe().Contains("strength") && Describe().Contains("dirs"), "/ao reports its dials");
            check.Expect(fs.Contains("fract(b / 16.0 + float(uFrame) * 0.618034)"), "the raw stage's noise phase rotates with uFrame, which the single-frame estimate leaves at 0");
            check.Expect(Ao.Dirs.Default == 1 && Ao.Steps.Default == 3, "one slice at its full step count is the per-frame horizon");
        }
        finally { _log = quiet; }
        log.Info(check.Ok ? "SsaoPass.SelfCheck: passed" : "SsaoPass.SelfCheck: FAILED");
        return check.Ok;
    }
}
