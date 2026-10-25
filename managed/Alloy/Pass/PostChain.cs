using System;
using System.Globalization;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The Alloy colour pass's HDR target and the post chain that resolves it -- bloom, tonemap, encode. The
/// passes render into an RGBA16F attachment holding LINEAR values, unclamped, so a carried torch keeps its
/// overbright range instead of flattening to 8-bit white, and one full-screen pass spends it. The target owns
/// its depth too, and a refusal is the whole frame's business: <see cref="Begin"/> is asked before staging.
/// </summary>
internal static unsafe class PostChain
{
    /// <summary>Bloom chain depth ceiling. Six halvings from 1080p bottoms out at 8px, which is already
    /// wider than any bloom anyone wants; the real chain is the smaller of this and what the size allows.</summary>
    private const int MaxLevels = 6;
    /// <summary>Texture units the resolve samples the HDR colour and the bloom chain on -- clear of every
    /// unit the Alloy lit stage binds, because the resolve runs in the same bracket and leaving a unit shared
    /// with a live sampler of a different type is INVALID_OPERATION. See <see cref="TextureSlots"/>.</summary>
    private const uint HdrUnit = TextureSlots.PostHdr, BloomUnit = TextureSlots.PostBloom, DepthUnit = TextureSlots.PostDepth,
                       OverlayUnit = TextureSlots.PostOverlay;

    /// <summary>Tonemap operators, <c>post.tonemap</c>'s stored value; the cfg and chat form is the name
    /// (<c>post.tonemap=aces</c>). There is no off: the shoulder is the identity below its knee and differs
    /// from a clip only on highlights. ACES is the default, mixed against that shoulder by
    /// <c>post.acesmix</c>; Neutral is Khronos PBR Neutral, fixed, whose toe darkens 0.01 linear 16x.</summary>
    internal const int TonemapShoulder = 1, TonemapAces = 2, TonemapNeutral = 3;

    /// <summary>The Alloy colour passes of THIS pass are drawing into the HDR target. The owned frame does
    /// not run at all when this is false -- there is no un-resolved variant of it.</summary>
    internal static bool Armed { get; private set; }

    /// <summary>Resolves this session -- the [alloy] period line prints it. A number that stays 0 while the
    /// world renders is the "HDR never engaged, you are seeing the fallback" case, and the arm-time log line
    /// says which target refused.</summary>
    internal static int Resolves { get; private set; }
    /// <summary>Resolves since boot, never reset -- "did a world frame cover the screen this frame" is a
    /// compare against the value at the last present.</summary>
    internal static int ResolveSeq { get; private set; }

    /// <summary>A frame outside the post chain covered the whole present image (the Vulkan frame): counted as a resolve.</summary>
    internal static void Painted() => ResolveSeq++;

    private static bool _on = true;
    /// <summary>The <c>hdr</c> boot decision, published because it is a REFUSAL the cutover has to know
    /// about at arm time: <c>hdr=0</c> makes <see cref="Precheck"/> refuse every pass forever, and there is
    /// no engine frame behind that refusal -- the world would be bare clear colour for the session. Reported
    /// apart from a latched stand-down, because that is a CHOICE and this an INCAPACITY (ADR 0010).</summary>
    internal static bool Configured => _on;

    /// <summary>...and the latched half.</summary>
    internal static bool Failed => _down.Failed;
    private static readonly StandDown _down = new("PostChain");
    private static ExtenderLog? _log;
    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no name, probed
    /// format or uniform location from the dead context survives as a field nobody remembered to zero. The
    /// locations ride with the programs on purpose -- a location without its program is the one thing a
    /// partial reset here leaves behind.</summary>
    private sealed class PostChainState
    {
        public uint Fbo, HdrTex, BloomTex, DepthTex;
        public uint DepFormat;
        public int DepW, DepH;
        public int W, H, Levels;
        public uint ProgDown, ProgUp, ProgComposite;
        /// <summary>The empty VAO the full-screen triangle draws under.</summary>
        public uint Vao;
        public bool ProgFailed;
        /// <summary>The bloom chain built this frame, rewritten every frame by the downsample.</summary>
        public bool BloomOk;
        public int DSrc, DLod, DTexel, DFirst, DThresh, DExposure;
        public int USrc, ULod, UTexel;
        public int CHdr, CBloom, CDepth, CStrength, CMode, CKnee, CDither, CViz, CExposure, CAcesMix, CSkyRaw,
                   CSkyBand, CInvVp, CEye, COverlay, COverlayOn, CVFog, CVFogVp, CSkyBare;
    }

    private static PostChainState _gl = new();
    /// <summary>The framebuffer the Alloy colour passes draw into, for the one pass that has to leave it and
    /// come back: the sky scratch. Read-only on purpose -- binding it is <see cref="Begin"/>'s job.</summary>
    internal static uint Fbo => _gl.Fbo;
    /// <summary>The depth attachment the Alloy colour passes write, owned rather than the engine's.
    /// Published as <see cref="DepthResource"/> by the prepass; sampling it while it is attached is the
    /// feedback loop <see cref="SceneDepth"/> exists to avoid, so consumers take a copy.</summary>
    internal static uint DepthTex => _gl.DepthTex;
    /// <summary>The HDR colour target, for the sky decode that reads it back through a texture barrier.</summary>
    internal static uint HdrTex => _gl.HdrTex;
    /// <summary>The empty VAO the full-screen triangle draws under.</summary>
    internal static uint FullscreenVao => _gl.Vao;
    /// <summary>The colour the HDR buffer was last cleared to -- the void, for the period line.</summary>
    private static Vector3 _lastClear;

    /// <summary>The engine's clear colour the last pass began with, display space, as the engine set it.</summary>
    internal static Vector3 LastClear => _lastClear;

    /// <summary>The engine's own gamma gate, <c>GetGlobalShaderParami(0)</c> -- the value that becomes
    /// <c>#define GAMMA_CORRECTION</c> in every engine shader and makes <c>ApplyColorSpace</c> the identity
    /// when it is 0. Asked once per pass in <see cref="Precheck"/>: it is a settings-driven global and
    /// reading it is one call. Unresolved means "on", which is what this client measures.</summary>
    private static RiGetGlobalShaderParamiFn _gammaParam;
    private const uint GammaParam = 0;

    /// <summary>The RENDER scale -- the owned 3D frame's size as a fraction of the display target's,
    /// per axis. Below 1 the world renders at fewer pixels and the resolve upscales it (bilinear); above 1 it
    /// is supersampling. The engine's 2D UI never sees it. <c>alloy.scale</c>, live; the knob's range is
    /// <see cref="MinScale"/>..<see cref="MaxScale"/> because a 0x0 target is a refusal, not a look.</summary>
    private static float _scale => Renderer.Scale.V;
    internal const float MinScale = 0.25f, MaxScale = 2f;


    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        // The one boot decision in this class: since #132 there is no Alloy frame without the resolve, so
        // alloy.hdr=0 is not a look toggle but a stand-down of the whole Alloy frame -- Restart, read through Boot.
        _on = Renderer.Hdr.Boot;
        _gammaParam = EngineFn.RiGetGlobalShaderParami;
        if (_gammaParam == null)
            log.Info("PostChain: RenderInterface::GetGlobalShaderParami unresolved -- the resolve encodes "
                   + "unconditionally, which is what this client measures anyway (#102)");
        log.Info(_on ? $"PostChain: armed -- {Describe()}; /post retunes every dial live"
                     : "PostChain: alloy.hdr=0 -- the Alloy frame stands down; the world renders vanilla");
    }

    /// <summary>The <c>/post</c> status line.</summary>
    internal static string Describe() =>
        $"hdr {(_on ? "on" : "off")}  bloom {Post.Bloom.V:F3}{(StockConfig.HdrBloom ? "" : " (OFF: stock HDR/Bloom option)")}  threshold {Post.Threshold.V:F2}  knee {Post.Knee.V:F2}  " +
        $"exposure {Post.Exposure.V:F2}  tonemap {Post.Tonemap.Format()}  acesmix {Post.AcesMix.V:F2}  tmknee {Post.TmKnee.V:F2}  " +
        $"dither {Post.Dither.V:F2}  scale {_scale:F2} ({(_gl.W > 0 ? $"{_gl.W}x{_gl.H} render, " : "")}lod bias {LodBias:F2})  motion {(Post.Motion.V ? "on" : "off")}  taa {(Post.Taa.V ? "on" : "off")} (taablend {Post.TaaBlend.V:F2}, taasharp {Post.TaaSharp.V:F2}, taau {(Post.Taau.V ? 1 : 0)}, ao temporal {Ao.Temporal.V:F2})  halfres {(Post.HalfRes.V ? "on" : "off")}"
        + (Post.Viz.V > 0 ? $"  VIZ {Post.Viz.V}" : "")
        + (Post.ClearViz.V ? "  CLEARVIZ (target cleared magenta)" : "")
        + " | " + PostStack.Describe();

    /// <summary>The resolve's dither in the unit the shader takes: one LSB of the 8-bit target, or 0.
    /// Whichever stage WRITES that target applies it -- the resolve when the stack is empty, the stack's
    /// last stage otherwise -- so the frame is quantised exactly once.</summary>
    internal static float DitherLsb => Post.Dither.V / 255f;

    /// <summary>A display extent at the render scale, never below one pixel.</summary>
    internal static int RenderSize(int display) => Math.Max((int)MathF.Round(display * _scale), 1);

    /// <summary>The texture LOD bias every owned material sample takes: <c>log2(scale)</c>, so a frame
    /// rendered at fewer pixels picks the mips the DISPLAY footprint would have and the upscale does not
    /// go soft on top of them. Clamped at 0: above 1.0 the point is the extra texture detail the resolve
    /// then averages, and a coarser mip would throw it away. 0 at 1.0, so the default is pixel-identical.</summary>
    internal static float LodBias => MathF.Min(MathF.Log2(_scale), 0f);

    /// <summary>Whether the motion-vector pass runs this frame -- armed, the viz that paints it, or a
    /// consumer (TAA, temporal GTAO) that reprojects through it.</summary>
    internal static bool MotionWanted => Post.Motion.V || Post.Viz.V == VizMotion || TaaOn || SsaoPass.TemporalWanted;
    /// <summary>TAA is on and its stage can build -- the gate the jitter and the accumulator share, so
    /// a stage that failed to build never leaves the jitter running with nothing to average it.</summary>
    internal static bool TaaOn => Post.Taa.V && !TaaStage.Failed && !MotionVectors.Failed;
    internal static float TaaBlend => Post.TaaBlend.V;
    /// <summary>Temporal upsampling: under a render scale below 1 with TAA on, the accumulator keeps its
    /// history at DISPLAY size and reconstructs from the jittered render-size frames, instead of the resolve
    /// upscaling bilinearly and TAA smoothing the result. <c>/post taau 0</c> is the A/B.</summary>
    internal static bool TaaUpsample => Post.Taau.V;
    /// <summary>This pass's projection jitter in NDC, as <see cref="Jitter"/> handed it out (0 with TAA off);
    /// the accumulator reads it to place the current frame's samples on the display grid.</summary>
    internal static float JitterX, JitterY;
    /// <summary>The jitter sequence length -- Halton(2,3), eight points, then it repeats.</summary>
    internal const int JitterCount = 8;
    /// <summary>This frame's sub-pixel jitter in NDC for a render target of <paramref name="w"/> x
    /// <paramref name="h"/>: Halton(2,3) over <see cref="JitterCount"/> frames, centred on zero, one render
    /// pixel of spread. Pure, so the self-check can walk the sequence.</summary>
    internal static void Jitter(int frameSeq, int w, int h, out float jx, out float jy)
    {
        int i = (frameSeq & 0x7fffffff) % JitterCount + 1;
        jx = (Halton(i, 2) - 0.5f) * 2f / Math.Max(w, 1);
        jy = (Halton(i, 3) - 0.5f) * 2f / Math.Max(h, 1);
    }
    private static float Halton(int i, int b)
    {
        float f = 1f, r = 0f;
        while (i > 0) { f /= b; r += f * (i % b); i /= b; }
        return r;
    }
    /// <summary><c>/post viz 4</c>: the velocity target, painted by the resolve in place of the frame.</summary>
    internal const int VizMotion = 4;

    /// <summary>Drop every GL object (context loss / disarm). Names are meaningless once the context is
    /// gone, so this only forgets them -- it never deletes.</summary>
    internal static void Forget()
    {
        _gl = new PostChainState();
        Armed = false; _down.Forget();
    }

    /// <summary>The framebuffer the last composite wrote into -- 0 is the default framebuffer. Published so
    /// the probe line can say whether a black frame is a frame that went somewhere else or one that was
    /// lost.</summary>
    internal static uint LastInto;

    /// <summary>The refusals decidable WITHOUT the 4.6 bracket, asked in ENGINE context before the Alloy
    /// pass pays for its preamble; false stands the whole pass down to vanilla.</summary>
    internal static bool Precheck(int* viewport)
    {
        // config hdr=0 is a deliberate setting and Attach already said so; it is not a refusal to report.
        if (!_on || _down.Failed) return false;
        // #215/#467: the default framebuffer IS the target, every frame and on purpose. Keyed on the shim
        // WINDOW rather than on its setting: with the key on but the shim branch not taken, the engine is
        // still drawing this pass and the refusal is still the right answer.
        if (viewport[0] != 0 || viewport[1] != 0) return Refuse("the engine's viewport is offset");
        return true;
    }

    /// <summary>The engine's gamma pipeline is off (stock Enhanced Lighting off). Only the GUI follows it: the
    /// Alloy world stages compile with <c>GAMMA_CORRECTION 1</c> whatever the engine's is. Unresolved reads as "on".</summary>
    internal static bool GammaOff() => _gammaParam != null && _gammaParam(GammaParam) == 0;

    /// <summary>Point the Alloy colour passes at the HDR target for this pass; a false answer stands the whole
    /// Alloy frame down to vanilla, so this is asked BEFORE anything is staged. The target's starting content
    /// comes from the engine's clear COLOUR rather than a blit of its buffer: a blit would copy a
    /// display-space value into a linear target, wrong by a gamma wherever an area clears to non-black.</summary>
    internal static bool Begin(int* viewport, float* clearRgba)
    {
        Armed = false;
        if (!_on || _down.Failed) return false;
        // BlitFramebuffer is in the list for the sampled depth copies (SceneDepth, FrameSnapshot) and for
        // the present hand-back, which is how the finished frame reaches the screen at all.
        if (Gl.ClearColor == null || Gl.Clear == null || Gl.GenFramebuffers == null
            || Gl.FramebufferTexture2D == null || Gl.BlitFramebuffer == null)
            return StandDown("a required GL entry point is missing");
        if (!EnsurePrograms()) return false;
        if (!EnsureTargets(viewport[2], viewport[3])) return false;
        if (!EnsureDepth(OwnDepthFormat, viewport[2], viewport[3])) return false;

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, _gl.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, _gl.HdrTex, 0);
        // DEPTH_ATTACHMENT alone even for a packed format: nothing owned touches stencil, and every Alloy
        // funnel attaches depth here (see FrameSnapshot's probe, which relies on exactly that).
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, _gl.DepthTex, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, PresentTarget.Fbo46);
            return Refuse($"the HDR framebuffer is incomplete (0x{status:x})");
        }

        // The engine's own clear colour, linearised: ground truth rather than a guess, and one clear
        // rather than a full-screen blit.
        // clearRgba is READ IN THE ENGINE CONTEXT by the caller and handed over. A GL share list shares
        // OBJECTS, never per-context state, and glClearColor is per-context: querying it here would return
        // whatever THIS context was last set to -- (0,0,0,0) until the first probe capture, and EnvProbe's
        // own clear colour ever after.
        _lastClear = new Vector3(clearRgba[0], clearRgba[1], clearRgba[2]);
        if (Post.ClearViz.V) Gl.ClearColor(1f, 0f, 1f, 1f);
        else Gl.ClearColor(MathF.Pow(MathF.Max(clearRgba[0], 0f), AlloyLights.SrgbGamma),
                      MathF.Pow(MathF.Max(clearRgba[1], 0f), AlloyLights.SrgbGamma),
                      MathF.Pow(MathF.Max(clearRgba[2], 0f), AlloyLights.SrgbGamma), clearRgba[3]);
        // Depth comes with it: this attachment is Alloy's own and nothing else clears it. The mask is set
        // beside the clear rather than assumed -- glClear honours it, and a stale DepthMask(0) would leave
        // the prepass writing into last pass's depth. The VALUE is this context's own glClearDepth, which
        // nothing in Alloy sets, i.e. GL's initial 1.0: the far plane, which is what every pass assumes.
        Gl.DepthMask(1);
        Gl.Clear(Gl.COLOR_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT);
        // Left set: the only other consumer of this context's clear colour is EnvProbe, which sets its own
        // every capture (EnvProbe.Begin), so there is nothing here worth restoring.
        Armed = true;
        return true;
    }

    /// <summary>Alloy's depth attachment format -- its OWN choice, no longer matched to the window's: a
    /// 32-bit float with no stencil, which is what every Alloy pass that samples it reads back. Nothing blits
    /// this buffer into an engine attachment any more, so nothing constrains it but Alloy.</summary>
    private const uint OwnDepthFormat = Gl.DEPTH_COMPONENT32F;

    /// <summary>Alloy's own depth attachment, sized to the target. Keyed on the FORMAT as well as the size so
    /// a changed <see cref="OwnDepthFormat"/> reallocates rather than keeping the old storage.</summary>
    private static bool EnsureDepth(uint fmt, int w, int h)
    {
        if (_gl.DepthTex != 0 && fmt == _gl.DepFormat && w == _gl.DepW && h == _gl.DepH) return true;
        if (w <= 0 || h <= 0) return false;
        uint tex = _gl.DepthTex;
        RenderTarget.AllocDepth(ref tex, fmt, w, h);
        if (tex == 0) { _gl.DepthTex = 0; return Refuse("the owned depth texture could not be created"); }
        _gl.DepthTex = tex; _gl.DepFormat = fmt; _gl.DepW = w; _gl.DepH = h;
        _log?.Info($"PostChain: owned depth attachment {w}x{h}, internal format 0x{fmt:x} (Alloy's own, "
                 + "sampled by the passes that read scene depth and handed back to nothing)");
        return true;
    }

    /// <summary>Say why the HDR target is not arming, once per distinct reason, and go on checking. A refusal
    /// taken inside the shim window ALSO latches the frame verdict -- the engine's frame is already gone by
    /// then, so the next frame goes vanilla with this reason rather than the world staying empty. NOT latched
    /// otherwise: the engine's target is a per-pass fact, so a session that refused once must still arm.</summary>
    private static bool Refuse(string why)
    {
        AlloyGate.Fail(why);   // #263: nothing to hand this pass back to, and no restart-free way to get it
        return _down.Refuse(_log, why, "the HDR target is not armed; #132: the Alloy frame stands down for those "
                                     + "passes and the engine renders them vanilla");
    }

    /// <summary>...and the latching kind, for a failure that cannot come back: a stage that did not build,
    /// an entry point the driver does not have.</summary>
    private static bool StandDown(string why)
    {
        // #263: same argument as Refuse's -- this latches inside a frame the verdict already froze on, and
        // nothing re-reads PostChain.Failed afterwards.
        AlloyGate.Fail(why);
        return _down.Fail(_log, why, "the HDR target cannot arm this session; the engine renders vanilla");
    }

    /// <summary>The bloom chain: progressive downsample, then a tent upsample back up, accumulating. Runs
    /// with the HDR target still bound as the read source and the chain's own framebuffer as the draw
    /// target, so the caller must rebind the engine target before <see cref="Composite"/>.</summary>
    internal static void Bloom()
    {
        var g = _gl;
        g.BloomOk = false;
        // #571: the stock "HDR / Bloom" option is a hard gate over /post bloom -- off means no bloom
        // contribution, and the composite then reads strength 0. The HDR target itself stays: the Alloy
        // stages have no display-space variant (AlloyGate's hdr=0 refusal says the same).
        if (!Armed || Post.Bloom.V <= 0f || g.Levels <= 0 || !StockConfig.HdrBloom) return;
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.Fbo);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.BindVertexArray(g.Vao);
        Gl.ActiveTexture(Gl.TEXTURE0 + BloomUnit);

        // Downsample. Level 0 reads the HDR colour (and is the only pass that thresholds);
        // every level after it reads the level above out of the same texture it is writing into -- legal
        // only while BASE/MAX_LEVEL exclude the attached level, which is what the clamp below is for. Skip
        // it and the chain samples its own attachment: undefined, and black on the drivers that enforce it.
        Gl.UseProgram(g.ProgDown);
        Gl.Uniform1i(g.DSrc, (int)BloomUnit);
        // the threshold is on the EXPOSED brightness, so what blooms tracks what the resolve will show
        Gl.Uniform1f(g.DExposure, Post.Exposure.V);
        for (int i = 0; i < g.Levels; i++)
        {
            int sw = i == 0 ? g.W : LevelW(i - 1), sh = i == 0 ? g.H : LevelH(i - 1);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.BloomTex, i);
            Gl.Viewport(0, 0, LevelW(i), LevelH(i));
            if (i == 0) { Gl.BindTexture(Gl.TEXTURE_2D, g.HdrTex); Gl.Uniform1i(g.DLod, 0); }
            else { Gl.BindTexture(Gl.TEXTURE_2D, g.BloomTex); Clamp(i - 1); Gl.Uniform1i(g.DLod, i - 1); }
            Gl.Uniform1i(g.DFirst, i == 0 ? 1 : 0);
            Gl.Uniform2f(g.DTexel, 1f / sw, 1f / sh);
            Gl.Uniform2f(g.DThresh, Post.Threshold.V, MathF.Max(Post.Knee.V, 1e-4f));
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        }

        // ...and back up, adding each level into the one below it. ONE/ONE rather than a second texture:
        // the tent filter's whole job is to be added, and a ping-pong pair would double the allocation to
        // save nothing.
        Gl.UseProgram(g.ProgUp);
        Gl.Uniform1i(g.USrc, (int)BloomUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, g.BloomTex);
        Gl.Enable(Gl.BLEND);
        Gl.BlendFunc(Gl.ONE, Gl.ONE);
        for (int i = g.Levels - 1; i > 0; i--)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.BloomTex, i - 1);
            Gl.Viewport(0, 0, LevelW(i - 1), LevelH(i - 1));
            Clamp(i);
            Gl.Uniform1i(g.ULod, i);
            Gl.Uniform2f(g.UTexel, 1f / LevelW(i), 1f / LevelH(i));
            Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        }
        Gl.Disable(Gl.BLEND);
        Clamp(0);
        // The HDR colour goes back on the attachment: anything binding this framebuffer afterwards (nothing
        // does today, but the pass list is a list) must not find a bloom mip there.
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.HdrTex, 0);
        g.BloomOk = true;
    }

    /// <summary>Resolve the HDR target: add the bloom, tonemap, encode, with the colour draw at
    /// <paramref name="rect"/> (the render viewport while the accumulator upsamples, the display viewport
    /// otherwise). The COLOUR goes to <paramref name="into"/> when it is non-zero, and the dither goes with
    /// the 8-bit write, so it is that stage's.</summary>
    internal static void Composite(uint into, int* rect)
    {
        var g = _gl;
        if (!Armed) return;
        if (into != 0) { Gl.BindFramebuffer(Gl.FRAMEBUFFER, into); Gl.Disable(Gl.FRAMEBUFFER_SRGB); }
        Gl.UseProgram(g.ProgComposite);
        Gl.BindVertexArray(g.Vao);
        new PassState(depth: false, depthWrite: false).Apply();
        Gl.ActiveTexture(Gl.TEXTURE0 + HdrUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, g.HdrTex);
        Gl.ActiveTexture(Gl.TEXTURE0 + BloomUnit);
        // #388: the velocity viz rides the bloom unit -- nothing else the resolve samples is free, and the
        // two views are exclusive by construction.
        Gl.BindTexture(Gl.TEXTURE_2D, Post.Viz.V == VizMotion ? MotionVectors.Tex : g.BloomOk ? g.BloomTex : 0);
        Gl.ActiveTexture(Gl.TEXTURE0 + DepthUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, g.DepthTex);
        uint overlay = StageResources.OverlayTex();
        Gl.ActiveTexture(Gl.TEXTURE0 + OverlayUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, overlay);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(g.CHdr, (int)HdrUnit);
        Gl.Uniform1i(g.CBloom, (int)BloomUnit);
        Gl.Uniform1i(g.CDepth, (int)DepthUnit);
        Gl.Uniform1i(g.COverlay, (int)OverlayUnit);
        Gl.Uniform1i(g.COverlayOn, overlay != 0 ? 1 : 0);
        Matrix4x4 invVp = AlloyRenderer.SkyInvVp;
        // no dome this view: a far-depth pixel is the fog-coloured clear, and raw it rings fully fogged geometry
        Gl.Uniform1i(g.CSkyRaw, Post.SkyRaw.V && invVp != default ? 1 : 0);
        Vector3 eye = AlloyRenderer.EyePos;
        Gl.Uniform1f(g.CSkyBand, FrameInputs.FogOn && invVp != default ? SceneFog.SkyBand : 0f);
        Gl.UniformMatrix4fv(g.CInvVp, 1, 0, (float*)&invVp);
        Gl.Uniform3f(g.CEye, eye.X, eye.Y, eye.Z);
        // the volume is built only for the main view, so a capture's frame finds it stood down on its own
        FogVolume.Upload(g.CVFog, g.CVFogVp, capture: false);
        Gl.Uniform1i(g.CSkyBare, invVp == default ? 1 : 0);
        Gl.Uniform1f(g.CStrength, g.BloomOk ? Post.Bloom.V : 0f);
        Gl.Uniform1i(g.CMode, Post.Tonemap.V);
        Gl.Uniform1f(g.CKnee, Post.TmKnee.V);
        Gl.Uniform1f(g.CExposure, Post.Exposure.V);
        Gl.Uniform1f(g.CAcesMix, Post.AcesMix.V);
        // ...in LSBs of the 8-bit target this writes into, converted here rather than in the shader so the
        // dial reads as "how many quantisation steps of noise", which is the only unit it makes sense in.
        Gl.Uniform1f(g.CDither, into != 0 ? 0f : DitherLsb);
        Gl.Uniform1i(g.CViz, Post.Viz.V);
        Gl.Viewport(rect[0], rect[1], rect[2], rect[3]);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        // Hand the depth state back the way the colour passes left it -- the probe capture may still be
        // ahead of us in another pass order, and the engine's own draws follow the bracket.
        Gl.Enable(Gl.DEPTH_TEST); Gl.DepthMask(1);
        Gl.BindVertexArray(0);
        Armed = false;
        Resolves++;
        ResolveSeq++;
        LastInto = into;
    }

    /// <summary>The one term the [alloy] period line carries: did the resolve run, and how often. Resets the
    /// counter, like every other period statistic.</summary>
    internal static string Stats(int frames)
    {
        int n = Resolves;
        Resolves = 0;
        if (!_on) return "off (config hdr=0)";
        // The clear colour rides along because it IS the void: every pixel the world does not cover comes out
        // of this buffer's clear, in both modes, and no other period line reports it.
        string clear = $", clear=({_lastClear.X:F3},{_lastClear.Y:F3},{_lastClear.Z:F3})";
        return n == 0 ? $"NOT RESOLVING ({(_down.Reason.Length == 0 ? "never reached the pass" : _down.Reason)}){clear}"
                      : $"{(float)n / MathF.Max(frames, 1):F2} resolves/frame at {_gl.W}x{_gl.H} (scale {_scale:F2}), {_gl.Levels}-level bloom{clear}";
    }

    private static int LevelW(int i) => Math.Max(_gl.W >> (i + 1), 1);
    private static int LevelH(int i) => Math.Max(_gl.H >> (i + 1), 1);

    /// <summary>Pin the bloom texture to exactly one level, so the level currently attached as the
    /// draw buffer is outside the sampleable range.</summary>
    private static void Clamp(int level)
    {
        Gl.TextureParameteri(_gl.BloomTex, Gl.TEXTURE_BASE_LEVEL, level);
        Gl.TextureParameteri(_gl.BloomTex, Gl.TEXTURE_MAX_LEVEL, level);
    }

    private static bool EnsureTargets(int w, int h)
    {
        if (_gl.HdrTex != 0 && w == _gl.W && h == _gl.H) return true;
        if (w <= 0 || h <= 0) return false;
        if (_gl.HdrTex != 0)
        {
            GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, _gl.HdrTex);
            GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, _gl.BloomTex);
            _gl.HdrTex = 0; _gl.BloomTex = 0;
        }
        _gl.W = w; _gl.H = h;
        _gl.Levels = 0;
        while (_gl.Levels < MaxLevels && Math.Min(LevelW(_gl.Levels), LevelH(_gl.Levels)) >= 8) _gl.Levels++;
        // ...but never zero: a texture with no levels is incomplete, and one bound to a declared sampler is
        // worth avoiding even where nothing samples it. A 1-level chain on a tiny target is a no-op bloom.
        if (_gl.Levels < 1) _gl.Levels = 1;

        uint tex = 0;
        // LINEAR: the resolve and the bloom downsample both read it at an offset, not at texel centres.
        RenderTarget.AllocColor(ref tex, true, w, h, Gl.LINEAR);
        _gl.HdrTex = tex;

        // Level 0 of the chain is half the target, so its storage is sized from LevelW(0)/LevelH(0) and the
        // mip sizes the immutable allocation derives match LevelW(i)/LevelH(i) exactly.
        Gl.CreateTextures(Gl.TEXTURE_2D, 1, &tex);
        Gl.TextureStorage2D(tex, _gl.Levels, Gl.RGBA16F, LevelW(0), LevelH(0));
        // LINEAR within a level, never across levels: every read is an explicit textureLod and a mip-filtered
        // minification here would blend in a level the chain has not written yet. Hence MAX_LEVEL rather than
        // a plain shared allocation -- the chain is levelled, so only the clamp half is shared.
        RenderTarget.Clamp(tex, Gl.LINEAR);
        Gl.TextureParameteri(tex, Gl.TEXTURE_MAX_LEVEL, _gl.Levels - 1);
        _gl.BloomTex = tex;

        if (_gl.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); _gl.Fbo = f; }
        _log?.Info($"PostChain: HDR target {w}x{h} RGBA16F, {_gl.Levels}-level bloom chain, with its own " +
                   "depth attachment (#214)");
        return true;
    }

    /// <summary>Throw away a post program built from an override that breaks the colour-space contract
    /// and rebuild it from the embedded pair -- the same refuse-and-say-so shape as
    /// <c>Programs.RebuildEmbedded</c>. Both stages come from the embedded text, never a content
    /// vertex stage over an embedded fragment one: the fall back is to a KNOWN pair.</summary>
    private static uint Rebuild(uint stale, string fs, string what, string why)
    {
        Gl.DeleteProgram(stale);
        _log?.Info($"PostChain: the {what} override is refused -- {why}. Rebuilding from the embedded "
                 + "default (regenerate the override with /alloy shaderdump to get it back).");
        return GlProgram.Graphics(FullscreenVs, fs, _log, $"PostChain {what} (embedded)", content: false);
    }

    private static bool EnsurePrograms()
    {
        if (_gl.ProgComposite != 0) return true;
        if (_gl.ProgFailed) return false;
        string post = ShaderStages.Source(ShdPostVs);
        _gl.ProgDown = GlProgram.Graphics(ShdPostVs, post, ShdBloomDownFs, ShaderStages.Source(ShdBloomDownFs),
                                       _log, "PostChain bloom downsample", out _, out string downSrc);
        if (_gl.ProgDown != 0 && !ShaderContract.LinearSafe(downSrc))
            _gl.ProgDown = Rebuild(_gl.ProgDown, BloomDownFs, "bloom downsample",
                                "it still encodes to display space (pre-#132), and the chain it feeds is linear");
        _gl.ProgUp = GlProgram.Graphics(ShdPostVs, post, ShdBloomUpFs, ShaderStages.Source(ShdBloomUpFs),
                                     _log, "PostChain bloom upsample", out _, out string upSrc);
        if (_gl.ProgUp != 0 && !ShaderContract.LinearSafe(upSrc))
            _gl.ProgUp = Rebuild(_gl.ProgUp, BloomUpFs, "bloom upsample",
                              "it still encodes to display space (pre-#132), and the chain it feeds is linear");
        _gl.ProgComposite = GlProgram.Graphics(ShdPostVs, post, ShdResolveFs, ShaderStages.Source(ShdResolveFs),
                                            _log, "PostChain resolve", out _, out string resolveSrc);
        // #208: the resolve's two units come with it -- an override that renamed either samples unit 0.
        if (_gl.ProgComposite != 0 && !(ShaderContract.ResolveSafe(resolveSrc)
                                     && ShaderContract.SlotSafe(resolveSrc, "uHdr", "uBloom", "uDepth")))
            _gl.ProgComposite = Rebuild(_gl.ProgComposite, ResolveFs, "resolve",
                                     "it decodes its input (pre-#132) or performs no encode at all, and this "
                                     + "is the frame's one encode");
        if (_gl.ProgDown == 0 || _gl.ProgUp == 0 || _gl.ProgComposite == 0)
        {
            _gl.ProgFailed = true;
            StandDown("a post program did not build");
            return false;
        }
        _gl.DSrc = GlProgram.Loc(_gl.ProgDown, "uSrc"); _gl.DLod = GlProgram.Loc(_gl.ProgDown, "uLod");
        _gl.DTexel = GlProgram.Loc(_gl.ProgDown, "uTexel"); _gl.DFirst = GlProgram.Loc(_gl.ProgDown, "uFirst");
        _gl.DThresh = GlProgram.Loc(_gl.ProgDown, "uThresh");
        _gl.DExposure = GlProgram.Loc(_gl.ProgDown, "uExposure");
        _gl.USrc = GlProgram.Loc(_gl.ProgUp, "uSrc"); _gl.ULod = GlProgram.Loc(_gl.ProgUp, "uLod");
        _gl.UTexel = GlProgram.Loc(_gl.ProgUp, "uTexel");
        _gl.CHdr = GlProgram.Loc(_gl.ProgComposite, "uHdr"); _gl.CBloom = GlProgram.Loc(_gl.ProgComposite, "uBloom");
        _gl.CDepth = GlProgram.Loc(_gl.ProgComposite, "uDepth"); _gl.CSkyRaw = GlProgram.Loc(_gl.ProgComposite, "uSkyRaw");
        _gl.CSkyBand = GlProgram.Loc(_gl.ProgComposite, "uSkyBand"); _gl.CInvVp = GlProgram.Loc(_gl.ProgComposite, "uInvVP");
        _gl.CEye = GlProgram.Loc(_gl.ProgComposite, "uEyePos");
        _gl.CVFog = GlProgram.Loc(_gl.ProgComposite, "uVFog"); _gl.CVFogVp = GlProgram.Loc(_gl.ProgComposite, "uVFogVp");
        _gl.CSkyBare = GlProgram.Loc(_gl.ProgComposite, "uSkyBare");
        _gl.COverlay = GlProgram.Loc(_gl.ProgComposite, "uOverlay"); _gl.COverlayOn = GlProgram.Loc(_gl.ProgComposite, "uOverlayOn");
        _gl.CStrength = GlProgram.Loc(_gl.ProgComposite, "uStrength"); _gl.CMode = GlProgram.Loc(_gl.ProgComposite, "uMode");
        _gl.CKnee = GlProgram.Loc(_gl.ProgComposite, "uKnee");
        _gl.CExposure = GlProgram.Loc(_gl.ProgComposite, "uExposure");
        _gl.CAcesMix = GlProgram.Loc(_gl.ProgComposite, "uAcesMix");
        _gl.CDither = GlProgram.Loc(_gl.ProgComposite, "uDither");
        _gl.CViz = GlProgram.Loc(_gl.ProgComposite, "uViz");
        // Core profile draws nothing with VAO 0 bound, even from a vertex stage that reads no attributes.
        if (_gl.Vao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _gl.Vao = v; }
        return true;
    }

    /// <summary>Resrefs a content author overrides the post stages at, same rules as every other owned
    /// program. All three share <see cref="FullscreenVs"/>, so overriding <c>cx_post_vs</c> replaces the
    /// vertex stage of all three at once.</summary>
    internal const string ShdPostVs = "cx_post_vs", ShdBloomDownFs = "cx_bloomdn_fs";
    internal const string ShdBloomUpFs = "cx_bloomup_fs", ShdResolveFs = "cx_resolve_fs";

    /// <summary>The full-screen triangle every post stage draws: three vertices, no buffers, no attributes.
    /// A triangle rather than a quad because the diagonal seam of a two-triangle quad costs a second
    /// rasterisation of every pixel along it and buys nothing.</summary>
    internal static string FullscreenVs => ShaderSource.Get("cx_fullscreen_v.glsl");

    /// <summary>The frame's one encode, and the only colour-space conversion left anywhere in the Alloy
    /// pipeline. Its own text rather than a share of <see cref="AlloyLights.FragmentLib"/>, which declares
    /// the light SSBOs and the cluster grid a full-screen pass has no use for. Unconditional: the engine's
    /// gamma parameter is asked in <see cref="Precheck"/>, which stands the whole pass down when it is off.</summary>
    private static string PostColorLib => ShaderSource.Get("cx_postcolor.glsl");

    /// <summary>The frame's one dither, shared by the resolve and every <see cref="PostStack"/> stage: the
    /// whole frame is computed in RGBA16F and written ONCE into an 8-bit target, so a smooth shallow gradient
    /// -- a bloom halo -- lands its steps as contour bands. TRIANGULAR PDF, two independent samples summed:
    /// a uniform dither leaves bands faintly standing, the triangular sum decorrelates the error outright.</summary>
    internal static string PostDitherLib => ShaderSource.Get("cx_postdither.glsl");

    /// <summary>Progressive downsample, the 13-tap filter with the four-group Karis average (Jimenez'
    /// "Next Generation Post Processing"). The Karis weighting is not decoration: a plain box filter over a
    /// single very bright texel makes the bloom flicker as that texel crosses a pixel boundary, which on a
    /// carried torch is every frame. Level 0 is also the only pass that applies the threshold.</summary>
    internal static string BloomDownFs => ShaderSource.Get("cx_bloomdown_f.glsl");

    /// <summary>The 3x3 tent back up the chain, added into the level below with ONE/ONE. Tent rather than
    /// bilinear: the upsample is what turns nine discrete mips into one continuous falloff, and a plain
    /// bilinear read leaves each level's own resolution visible as a ring.</summary>
    internal static string BloomUpFs => ShaderSource.Get("cx_bloomup_f.glsl");

    /// <summary>The resolve: add the bloom, tonemap, and do the frame's ONE encode. <c>uMode</c> 1 is a
    /// shoulder (identity below the knee, asymptotic to 1 above it), 2 is ACES (Narkowicz' fit) mixed against
    /// that shoulder, and 3 is Khronos PBR Neutral -- a 0.04 offset up to 0.76, then a hue-preserving
    /// roll-off, reading neither <c>uKnee</c> nor <c>uAcesMix</c>.</summary>
    internal static string ResolveFs => ShaderSource.Get("cx_resolve_f.glsl");

    /// <summary>Off-device (run-demo.sh): the knob table has the shape the frame relies on and the shoulder
    /// really is the identity below its knee -- which is the whole parity argument, and the one property no
    /// compile check covers. Writes <c>V</c> directly (a Set would run Changed and arm the cfg writer) and
    /// restores whatever it touched.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        ExtenderLog? quiet = _log; _log = null;
        var check = new Checker(log);

        float sc = _scale, tb = Post.TaaBlend.V;
        bool mo = Post.Motion.V, ta = Post.Taa.V, on = _on, armed = Armed;
        int vz = Post.Viz.V;
        try
        {
            check.Expect(Post.G.Get("hdr") == null && Post.G.Get("scale") == null && Post.G.Get("aotemporal") == null,
              "hdr, scale and aotemporal are not post knobs: hdr is the Renderer's boot setting, the other two are alloy.scale and ao.temporal");
            check.Expect(Post.Tonemap.RungValue(Quality.Shipped) == TonemapAces && Post.Tonemap.RangeText() == "shoulder, aces, neutral",
              "the tonemap knob stores the operator index and formats its name");
            check.Expect(Post.Tonemap.TryParse("shoulder", out int tm) && tm == TonemapShoulder && Post.Tonemap.TryParse("0", out tm) && tm == TonemapShoulder
              && Post.Tonemap.TryParse("neutral", out tm) && tm == TonemapNeutral,
              "...and parses a name or an index");
            check.Expect(!Post.Tonemap.TryParse("off", out _), "there is no off: the shoulder is the minimal curve");
            // #389: TAA arms the motion pass and stands SMAA down; both toggle with the one key. The jitter
            // sequence is centred, one render pixel wide, and repeats -- and is exactly zero-width at no size.
            Post.Motion.V = false; Post.Viz.V = 0; Post.Taa.V = true;
            check.Expect(TaaOn && MotionWanted && !SmaaStage.Active, "/post taa 1 arms the motion pass and stands SMAA down");
            Post.Taa.V = false; check.Expect(!TaaOn && !MotionWanted, "/post taa 0 disarms both together");
            float sx = 0f, sy = 0f, mx = 0f;
            for (int f = 0; f < JitterCount; f++)
            {
                Jitter(f, 1000, 500, out float jx, out float jy);
                sx += jx; sy += jy; mx = MathF.Max(mx, MathF.Max(MathF.Abs(jx) * 500f, MathF.Abs(jy) * 250f));
            }
            Jitter(0, 1000, 500, out float j0x, out float j0y); Jitter(JitterCount, 1000, 500, out float j8x, out float j8y);
            // Halton's first eight points are not exactly balanced (base 2 sums to 3.5625, a mean of -0.055
            // px): a mean under a tenth of a pixel is what this asserts, not zero.
            check.Expect(MathF.Abs(sx) * 500f / JitterCount < 0.1f && MathF.Abs(sy) * 250f / JitterCount < 0.1f, "the jitter sequence is centred within 0.1 px");
            check.Expect(mx <= 0.5f + 1e-4f, "...and stays within half a render pixel");
            check.Expect(j0x == j8x && j0y == j8y, "...and repeats every JitterCount frames");
            check.Expect(Post.TaaBlend.TryParse("0", out float blend) && blend == 0.02f, "taablend never reaches 0: a frozen history would never converge");
            check.Expect(Post.TaaBlend.TryParse("1", out blend) && blend == 0.5f, "...nor 1: that is the raw jitter with nothing averaging it");
            // #391: the render size and lod bias the scale produces never hit 0; the clamp itself is
            // alloy.scale's range, MinScale..MaxScale. Plain field writes on the knob: nothing runs or is written.
            check.Expect(Renderer.Scale.Min == MinScale && Renderer.Scale.Max == MaxScale, "alloy.scale spans exactly the resolve's range");
            Renderer.Scale.V = 0.67f;
            check.Expect(RenderSize(2560) == 1715 && RenderSize(1) == 1, "render size rounds, never below one pixel");
            check.Expect(MathF.Abs(LodBias - MathF.Log2(0.67f)) < 1e-6f, "the lod bias is log2(scale) below 1");
            Renderer.Scale.V = 1.5f; check.Expect(LodBias == 0f, "...and 0 above it: supersampling keeps its detail");
            Renderer.Scale.V = 1f; check.Expect(RenderSize(2560) == 2560 && LodBias == 0f, "1.0 is pixel-identical");
            // #388: the viz runs the pass without arming it, and arming is what /post motion does.
            Post.Motion.V = false; Post.Viz.V = 0; check.Expect(!MotionWanted, "motion off and no viz: the pass sits out");
            Post.Viz.V = VizMotion; check.Expect(MotionWanted, "viz 4 runs the pass unarmed");
            Post.Viz.V = 0; Post.Motion.V = true; check.Expect(MotionWanted, "/post motion 1 arms it");
            check.Expect(Describe().Contains("bloom") && Describe().Contains("tonemap") && Describe().Contains("dither")
              && Describe().Contains("exposure") && Describe().Contains("scale") && Describe().Contains("motion") && Describe().Contains("taa"),
              "Describe names every knob group");
            check.Expect(ResolveFs.Contains("if (uViz == 4)") && ResolveFs.Contains("textureLod(uBloom, vUV, 0.0).rg"),
              "viz 4 paints the velocity target off the bloom unit");
            // Exposure is scene-referred: it multiplies the linear frame BEFORE the tonemap and the encode,
            // never the display value -- a gain after the encode is a gamma-wrong brightness slider.
            check.Expect(ResolveFs.IndexOf("c *= uExposure;", StringComparison.Ordinal)
              < ResolveFs.IndexOf("if (uMode == 1) c = CxShoulder", StringComparison.Ordinal)
              && ResolveFs.IndexOf("c *= uExposure;", StringComparison.Ordinal)
              < ResolveFs.IndexOf("vec3 outc = CxEncode(c);", StringComparison.Ordinal),
              "exposure is applied in linear, before the tonemap and the encode");
            check.Expect(ResolveFs.Contains("mix(CxShoulder(c, uKnee), CxAces(c), uAcesMix)"),
              "ACES is mixed against the shoulder, so the darks below the knee are never crushed past the mix");
            check.Expect(ResolveFs.Contains("else if (uMode == 3) c = CxNeutral(c);")
              && ResolveFs.IndexOf("else if (uMode == 3) c = CxNeutral(c);", StringComparison.Ordinal)
              < ResolveFs.IndexOf("vec3 outc = CxEncode(c);", StringComparison.Ordinal),
              "mode 3 is PBR Neutral, in the tonemap block before the encode");
            check.Expect(ResolveFs.IndexOf("c += overlay;", StringComparison.Ordinal)
                         > ResolveFs.IndexOf("else if (uMode == 3) c = CxNeutral(c);", StringComparison.Ordinal)
              && ResolveFs.IndexOf("c += overlay;", StringComparison.Ordinal)
                 < ResolveFs.IndexOf("vec3 outc = CxEncode(c);", StringComparison.Ordinal)
              && ResolveFs.Contains("raw = CxEncode(c + g + overlay);"),
              "a native stage's overlay is added after the tone curve on both arms, so a lens ghost over a ridgeline is one ghost");
            // The resolve is the ONLY place the Alloy frame meets 8-bit, so it is the only place a dither
            // can go. A resolve that lost it would band again, and the symptom (faint contour stripes over
            // smooth gradients) reads as a lighting bug rather than as a missing line here.
            check.Expect(PostDitherLib.Contains("(r1 + r2 - 1.0) * lsb") && ResolveFs.Contains("CxDither(outc, gl_FragCoord.xy, uDither)"),
              "the resolve dithers with a TRIANGULAR pdf -- two samples summed, not one");
            check.Expect(ResolveFs.IndexOf("vec3 outc = CxEncode(c);", StringComparison.Ordinal)
              < ResolveFs.IndexOf("CxDither(outc, gl_FragCoord.xy, uDither)", StringComparison.Ordinal),
              "...and it dithers in DISPLAY space, after the encode -- dithering before it is shaped by the "
              + "curve and stops matching the quantisation step it exists to break up");
            // #132: this resolve is the frame's ONLY encode, and it is gated on the engine's own
            // GAMMA_CORRECTION. A resolve that read the HDR colour through a decode would be undoing an
            // encode nobody applies any more -- the #102 failure, whole-frame and in the other direction.
            check.Expect(!ResolveFs.Contains("CxLinear") && !BloomDownFs.Contains("CxLinear"),
              "nothing in the chain decodes: the target is linear");
            // ...and it is UNCONDITIONAL: the Alloy decodes are compiled at GAMMA_CORRECTION 1 whatever the
            // engine's is, so an encode that followed the engine would leave every surface a gamma dark.
            check.Expect(ResolveFs.Contains("vec3 outc = CxEncode(c);") && !PostColorLib.Contains("uniform"),
              "the resolve's encode is unconditional -- it pairs with the stages' fixed decode, not the engine's gamma");

            // The debug VIEWS default off, so /post reset (every Default) clears a stray /post viz with the rest.
            // /post motion stays off because TAA arms the pass itself; /post taa is ON -- it is the default
            // anti-aliaser, and reset must put it back rather than leave the frame on SMAA.
            check.Expect(!Post.Motion.Default && Post.Taa.RungValue(Quality.Shipped) && Post.Viz.Default == 0 && !Post.ClearViz.Default,
              "the debug views default OFF and taa defaults ON: reset restores the shipped anti-aliaser");

            // The shoulder, in C#, mirroring the GLSL: identity below the knee, and never past 1.
            static float Shoulder(float c, float k)
            {
                if (k <= 0f || k >= 1f) return c;
                float m = 1f - k, t = MathF.Max(c - k, 0f);
                return MathF.Min(c, k) + m * t / (m + t);
            }
            for (float x = 0f; x <= 0.8f; x += 0.1f)
                check.Expect(MathF.Abs(Shoulder(x, 0.8f) - x) < 1e-5f, $"shoulder is the identity at {x:F1}");
            check.Expect(Shoulder(4f, 0.8f) < 1f && Shoulder(4f, 0.8f) > 0.9f, "shoulder compresses 4.0 into (0.9, 1)");
            check.Expect(Shoulder(100f, 0.8f) < 1f, "shoulder never reaches 1 however bright the input");
            check.Expect(Shoulder(1.5f, 0.8f) < Shoulder(3f, 0.8f), "shoulder stays monotonic, so detail survives it");

            // CX-47: PBR Neutral on a grey, mirroring CxNeutral: the 0.04 offset below 0.76, 1 never reached.
            static float Neutral(float c)
            {
                c -= c < 0.08f ? c - 6.25f * c * c : 0.04f;
                if (c < 0.76f) return c;
                const float d = 0.24f;
                return 1f - d * d / (c + d - 0.76f);
            }
            check.Expect(Neutral(0f) == 0f && MathF.Abs(Neutral(0.5f) - 0.46f) < 1e-5f, "neutral keeps black and only offsets the mids");
            check.Expect(MathF.Abs(Neutral(0.08f) - 0.04f) < 1e-5f, "...with a toe that meets the offset at 0.08");
            check.Expect(Neutral(100f) < 1f && Neutral(1.5f) < Neutral(3f), "neutral is monotonic and never reaches 1");
        }
        finally
        {
            // Every knob the check drove goes back: a missed one would ship its test value as the live value,
            // which is how /post viz once booted every session into the bloom-debug view.
            Renderer.Scale.V = sc; Post.TaaBlend.V = tb;
            Post.Motion.V = mo; Post.Taa.V = ta; _on = on; Armed = armed; Post.Viz.V = vz;
            _log = quiet;
        }
        log.Info(check.Ok ? "PostChain.SelfCheck: passed" : "PostChain.SelfCheck: FAILED");
        return check.Ok;
    }
}
