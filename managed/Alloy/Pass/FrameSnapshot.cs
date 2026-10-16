using System;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The Alloy frame's ONE framebuffer copy, and the water stage that reads it (ADR 0011): colour and depth of
/// everything drawn up to <see cref="PassId.FbCopy"/>, snapshotted into this class's own texture pair so the
/// water stage can march them. The engine takes TWO such copies per frame because its bucket order is fixed,
/// not because two are needed; ours holds LINEAR HDR, so no engine draw could have read it anyway.
/// </summary>
internal static unsafe class FrameSnapshot
{
    /// <summary>Texture units the copy binds on -- above the probe's cube, and clear of the grass push map
    /// and the resolve even though those are other programs. <see cref="TextureSlots"/> is the ladder
    /// and the only place it is written down; do not paraphrase it here.</summary>
    internal const uint ColorUnit = TextureSlots.SnapshotColor, DepthUnit = TextureSlots.SnapshotDepth;

    /// <summary>The unit the engine's own noise texture binds on. The vanilla wave field IS that
    /// texture at four scales, so the stage cannot draw without it.</summary>
    internal const uint NoiseUnit = TextureSlots.WaterNoise;

    /// <summary>The resref vanilla's water shader samples as <c>texUnitNoise</c>. Not a guess and not a
    /// string-table hunch: <c>Scene::Render</c> @0x5762d0 loads exactly this name into <c>Scene+0x1f8</c>
    /// and binds it with <c>SetActiveTextureAndBind(0xf, 4, tid)</c> -- unit 15, which is the unit
    /// <see cref="TextureSlots.WaterNoise"/> independently landed on.</summary>
    private const string NoiseResref = "solid_noise";

    /// <summary>The unit the SCENE's environment map binds on -- the reflection vanilla's water
    /// carries.</summary>
    internal const uint EnvUnit = TextureSlots.WaterEnv;

    /// <summary><c>water.enabled</c> off stands the class down; on brings it back at the next pass. Armed
    /// and idling rather than un-installed, so the toggle is live either way.</summary>
    internal static bool On => Water.Enabled.V;

    /// <summary>The class can still work in this context: switched on, and nothing permanent has gone wrong.
    /// Read BEFORE the batch build, which is why it cannot simply be <see cref="Ready"/> -- staging has to
    /// decide whether to enroll the water class one pass phase before the copy is attempted.</summary>
    internal static bool Usable => On && !_down.Failed;

    /// <summary>The copy for THIS pass is taken and sampleable. False whenever the Alloy water stage must not
    /// draw -- which is also the signal <see cref="BucketGate"/>'s bucket-6 gate reads, so the engine
    /// keeps drawing its own water rather than nobody drawing any.</summary>
    internal static bool Ready { get => _gl.Ready; private set => _gl.Ready = value; }

    /// <summary>Alloy copies taken this session, and the number the [alloy] line reports. The acceptance
    /// criterion is one per frame that has water on screen -- see <see cref="BucketGate"/> for the
    /// other half of it, which is that the engine took none.</summary>
    internal static int Copies { get; private set; }

    /// <summary>Frames the copy was wanted and could not be taken. Non-zero means the Alloy water stage stood
    /// down and the engine drew water instead: correct, just not ours.</summary>
    internal static int Refusals => _down.Refusals;

    /// <summary>Everything the GL context owns, in one object: context loss replaces it, so no name, probed
    /// depth format or copy size from the dead context survives as a field nobody remembered to zero.
    /// <see cref="Ready"/> rides here with the targets it names; the per-pass edge clears that one field.</summary>
    private sealed class SnapshotState
    {
        public uint Fbo, ColTex, DepTex;
        public int W, H;
        public uint DepFormat, DepMask;
        /// <summary>The copy pair was allocated against an armed HDR target, so its colour half is RGBA16F.</summary>
        public bool Hdr;
        /// <summary>The copy for the current pass is taken and sampleable.</summary>
        public bool Ready;
        /// <summary>The REPEAT sampler the noise texture is bound under.</summary>
        public uint NoiseSamp;
    }

    private static SnapshotState _gl = new();
    private static readonly StandDown _down = new("FrameSnapshot");
    private static ExtenderLog? _log;

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>CX's own <c>solid_noise</c>, for the frames the engine's has no id in. The engine never asks
    /// for it here -- <c>Scene::Render</c> is what loads that resref and this path does not run it -- so the
    /// registry answers from the resources on CX's own loader rather than creating a <c>CAurTexture</c> left
    /// queued on the engine's <c>RefreshTextures</c> list. Acquired once per session.</summary>
    private static int OwnNoise()
    {
        if (!_ownNoiseAsked) { _ownNoiseAsked = true; _ownNoise = Db.TexRegistry.Acquire(NoiseResref); }
        return Db.TexRegistry.TryResident(_ownNoise, out uint name, out _) ? (int)name : 0;
    }

    /// <summary>The noise a content stage samples as <c>texUnitNoise</c>: CX's own <c>solid_noise</c>, the one copy
    /// that is owned and so has a bindless handle. 0 until it is resident.</summary>
    internal static int ContentNoiseTid() => OwnNoise();

    /// <inheritdoc cref="OwnNoise"/>
    private static Db.TexId _ownNoise;
    /// <inheritdoc cref="OwnNoise"/>
    private static bool _ownNoiseAsked;
    /// <summary>What the ENGINE's <c>solid_noise</c> answered at the last bind, beside the id actually bound
    /// (<see cref="_noiseTid"/>). The two differ exactly when <see cref="OwnNoise"/> is carrying the stage.</summary>
    private static int _noiseEngTid;

    internal static string Describe()
        // #456: the noise texture DESCRIBED, not just named. A bound-but-incomplete texture (a mipmapped
        // min filter with no mips) samples as a constant, and a constant wave field is a flat sea -- which
        // is indistinguishable from "bound and fine" if all the line prints is the id.
        => $"water {(On ? "on" : "off")} (vanilla fswater)  noise tid {_noiseTid} on unit {NoiseUnit} "
         + $"resolved=\"{_noiseName}\" {_noiseEng} "
         + $"{(_noiseTid == 0 ? "" : TexProbe.TexFormat(_noiseTid) + " " + TexProbe.TexTopMip(_noiseTid) + " ")}"
         + $"rect {(Rect ? 1 : 0)} (last copy {LastRectFrac * 100f:F0}% of the frame)" +
           (Water.Diag.V > 0 ? $"  DIAG {Water.Diag.V} left" : "");

    /// <summary>Drop every GL object (context loss / disarm). Names mean nothing once the context is gone, so
    /// this forgets rather than deletes -- same rule as <see cref="PostChain.Forget"/>.</summary>
    internal static void Forget()
    {
        _gl = new SnapshotState();
        _down.Forget();
    }

    /// <summary>Nothing is drawn from the copy any more -- called at the top of every Alloy colour pass so a
    /// pass that never reaches <see cref="Copy"/> cannot leave the previous pass's snapshot armed. A GUI 3D
    /// panel drawing from the world's copy would sample the world into a portrait.</summary>
    internal static void Revoke() => Ready = false;

    /// <summary>Copy only the rows and columns the water can cover (<c>/water rect 0</c> = whole frame).</summary>
    internal static bool Rect => Water.Rect.V;
    /// <summary>Margin around the water's screen rectangle, as a fraction of the viewport height.</summary>
    internal const float RectMargin = 0.05f;
    /// <summary>The last copy's rectangle as a fraction of the frame; 1 = a whole-frame copy.</summary>
    internal static float LastRectFrac { get; private set; } = 1f;

    /// <summary>The whole-frame copy. <paramref name="depth"/> false blits colour alone: the grass root
    /// blend reads only colour, and a depth(+stencil) blit costs a full-res copy plus the driver's decompress.</summary>
    internal static bool Copy(int* viewport, bool depth = true) => Copy(viewport, 0, 0, viewport[2], viewport[3], depth);

    /// <summary>The copy over one rectangle of the frame, source and destination at the same coordinates so
    /// the stage's gl_FragCoord read stays 1:1; texels outside it keep the previous copy. Returns false,
    /// logged once, whenever the copy cannot be taken -- a full stand-down, not a degraded draw:
    /// <see cref="Ready"/> stays false and the Alloy water stage does not run.</summary>
    internal static bool Copy(int* viewport, int x0, int y0, int x1, int y1, bool depth = true)
    {
        Ready = false;
        if (!On || _down.Failed) return false;
        if (Gl.BlitFramebuffer == null || Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null
            || Gl.GetFramebufferAttachmentParameteriv == null)
            return Fail("a required GL entry point is missing");
        // An offset viewport would break the 1:1 mapping between gl_FragCoord and the copy's texels, which
        // every screen-space read in the stage assumes. A refusal, not a workaround.
        if (viewport[0] != 0 || viewport[1] != 0) return Refuse("the pass viewport is offset");
        int w = viewport[2], h = viewport[3];
        if (w <= 0 || h <= 0) return Refuse("the pass viewport is empty");

        int src; Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &src);
        // Every exit from here down rebinds src. EnsureTargets leaves the COPY framebuffer bound (it has to,
        // to check completeness), so returning straight out of a failure would leave the rest of the frame --
        // additive, the #139 resolve, and the engine's entire residual bucket walk -- drawing into an
        // incomplete FBO. That is a lost frame, not the stand-down this method promises.
        if (!ProbeDepth((uint)src, w, h) || !EnsureTargets(w, h))
        { Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src); return false; }

        Gl.DrainErrors(4);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, (uint)src);
        Gl.BindFramebuffer(Gl.DRAW_FRAMEBUFFER, _gl.Fbo);
        Gl.BlitFramebuffer(x0, y0, x1, y1, x0, y0, x1, y1, Gl.COLOR_BUFFER_BIT | (depth ? _gl.DepMask : 0), (uint)Gl.NEAREST);
        LastRectFrac = (float)(x1 - x0) * (y1 - y0) / ((float)w * h);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)src);
        // ...and the blit is CHECKED, because its whole-or-nothing failure mode is silent and total. A depth
        // blit between mismatched formats raises INVALID_OPERATION and discards the entire call -- colour
        // included -- leaving two never-written textures that read as "the scene is empty and one unit away".
        // Ready would otherwise stay true and the acceptance counter would report success the whole time.
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        if (err != 0)
        {
            uint chosen = _gl.DepFormat;
            _gl.DepFormat = 0;                    // re-probe: the format we chose is the prime suspect
            return Fail($"the framebuffer blit raised 0x{err:x} (depth format 0x{chosen:x} mismatched?)");
        }
        Copies++;
        Ready = true;
        // water.diag is a BUDGET of readbacks: a Set, so the file follows the countdown (Timer.Change, no alloc).
        if (Water.Diag.V > 0) { Water.Diag.Set(Water.Diag.V - 1); Diagnose((uint)src, w, h); }
        return true;
    }

    /// <summary><c>/water diag &lt;n&gt;</c>: read one pixel back out of BOTH framebuffers and say what is in
    /// them. Costs a full pipeline stall, so it runs a handful of times per session. Every cheaper signal is
    /// ambiguous -- a raw depth texel saturates to white for any real geometry, a colour view is filtered
    /// through the tonemap -- so the one unambiguous answer is that pixel from source and copy, as numbers.</summary>
    private static void Diagnose(uint src, int w, int h)
    {
        if (Gl.ReadPixels == null || Gl.ReadBuffer == null) return;
        int x = w / 2, y = h / 2;
        float* sc = stackalloc float[4];
        float* dc = stackalloc float[4];
        float sd = 0f, dd = 0f;
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src);
        if (src != 0) Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
        Gl.ReadPixels(x, y, 1, 1, Gl.RGBA, Gl.FLOAT, sc);
        Gl.ReadPixels(x, y, 1, 1, Gl.DEPTH_COMPONENT, Gl.FLOAT, &sd);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _gl.Fbo);
        Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
        Gl.ReadPixels(x, y, 1, 1, Gl.RGBA, Gl.FLOAT, dc);
        Gl.ReadPixels(x, y, 1, 1, Gl.DEPTH_COMPONENT, Gl.FLOAT, &dd);
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src);
        uint err = Gl.GetError == null ? 0 : Gl.GetError();
        _log?.Info($"[waterdiag] src fbo {src} (post hdr fbo {PostChain.Fbo}, copy fbo {_gl.Fbo}) centre pixel ({x},{y}) -- source rgb({sc[0]:F4},{sc[1]:F4},{sc[2]:F4}) " +
                   $"depth {sd:F6} | copy rgb({dc[0]:F4},{dc[1]:F4},{dc[2]:F4}) depth {dd:F6} | " +
                   // The COLOUR pair is the verdict. A blit is all-or-nothing, so colour landing means the
                   // whole call landed -- and reading depth back out of the source FBO proved unreliable
                   // (it answers a flat 1.0 while the copy taken FROM it holds a real value, which no blit
                   // can produce). The depth pair is printed for the record, never judged on.
                   $"{(sc[0] == dc[0] && sc[1] == dc[1] && sc[2] == dc[2] ? "COLOUR IDENTICAL -- the copy lands; look downstream in the stage" : "COLOUR DIFFERS -- the copy is not landing")}" +
                   (err == 0 ? "" : $" (readback err 0x{err:x})"));
    }

    /// <summary>Bind the copy on <see cref="ColorUnit"/>/<see cref="DepthUnit"/> and leave unit 0 current --
    /// the diffuse rebinds there, and every other pass in the frame assumes it.</summary>
    /// <summary>The colour copy's GL name for a reader outside the water program (grass), 0 until a copy has landed.</summary>
    internal static uint ColorTex => Ready ? _gl.ColTex : 0;

    /// <summary>Diag: a 3x3 grid of colour texels read from the copy and from <paramref name="src"/>, printed
    /// side by side with the scissor state and the source's colour attachment name.</summary>
    internal static void Compare(uint src, int w, int h, string tag)
    {
        if (Gl.ReadPixels == null || Gl.ReadBuffer == null || _gl.Fbo == 0) return;
        int sc = Gl.IsEnabled(Gl.SCISSOR_TEST);
        int* box = stackalloc int[4]; Gl.GetIntegerv(Gl.SCISSOR_BOX, box);
        int name = 0;
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src);
        if (src != 0) Gl.GetFramebufferAttachmentParameteriv(Gl.READ_FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.FRAMEBUFFER_ATTACHMENT_OBJECT_NAME, &name);
        var sb = new System.Text.StringBuilder();
        sb.Append($"[grassground] {tag}: src fbo {src} colour0 = tex {name}, copy tex {_gl.ColTex}, scissor {sc} box {box[0]},{box[1]} {box[2]}x{box[3]} |");
        float* a = stackalloc float[4]; float* b = stackalloc float[4];
        for (int j = 1; j <= 3; j++) for (int i = 1; i <= 3; i++)
        {
            int x = w * i / 4, y = h * j / 4;
            Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src); if (src != 0) Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
            Gl.ReadPixels(x, y, 1, 1, Gl.RGBA, Gl.FLOAT, a);
            Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, _gl.Fbo); Gl.ReadBuffer(Gl.COLOR_ATTACHMENT0);
            Gl.ReadPixels(x, y, 1, 1, Gl.RGBA, Gl.FLOAT, b);
            sb.Append($" ({x},{y}) src {a[0]:F2},{a[1]:F2},{a[2]:F2} copy {b[0]:F2},{b[1]:F2},{b[2]:F2}{(a[0]==b[0]&&a[1]==b[1]&&a[2]==b[2] ? "" : " DIFF")};");
        }
        Gl.BindFramebuffer(Gl.READ_FRAMEBUFFER, src);
        _log?.Info(sb.ToString());
    }

    internal static void Bind()
    {
        Gl.ActiveTexture(Gl.TEXTURE0 + ColorUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _gl.ColTex);
        Gl.ActiveTexture(Gl.TEXTURE0 + DepthUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, _gl.DepTex);
        // #339: re-resolved every bind and never cached -- a CAurTexture* is borrowed and the engine frees
        // it on an area transition (see AurTex). A miss binds nothing, which reads as a dead-flat sea rather
        // than as a wrong one, so it is logged once.
        // #456: the RESOLVED name, not the one we asked for. Windows returns a 512x350 RGBA texture where
        // Linux returns 1024x1024 compressed, so the open question is whether the resman handed back a
        // different RESOURCE or the same one built differently -- and only the object's own name says which.
        IntPtr noisePtr = CX.Framework.AurTex.FindByName(NoiseResref);
        int noise = CX.Framework.AurTex.Tid(noisePtr);
        _noiseEngTid = noise;
        if (noise != _noiseTid) { _noiseName = AlloyCensus.TexName(noisePtr); _noiseEng = TexScan.Describe(noisePtr); }
        // A zero here is "not uploaded YET" as often as "not there": AurTextureGet's create path queues the
        // upload on the engine's RefreshTextures list, so the first bind after a load legitimately misses.
        // Reported once, and worded as what it is -- a flat sea is the symptom either way.
        if (noise == 0 && !_noiseMissed)
        {
            _noiseMissed = true;
            _log?.Info($"FrameSnapshot: the engine texture \"{NoiseResref}\" has no GL id yet -- loading CX's "
                     + "own copy of it instead (the wave field is sampled from it; without one the sea is flat)");
        }
        if (noise == 0) noise = OwnNoise();
        _noiseTid = noise;
        Gl.ActiveTexture(Gl.TEXTURE0 + NoiseUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)noise);
        Gl.ActiveTexture(Gl.TEXTURE0);
        // ...under a sampler of our own, for the WRAP MODE. The wave field indexes the noise by world
        // position at four scales, so anything but REPEAT samples one edge texel over a whole harbour --
        // the same flat sea a missing texture gives, and just as silent. The engine's own params on this
        // object are not ours to assume, and not ours to change either.
        if (EnsureNoiseSampler()) Gl.BindSampler(NoiseUnit, _gl.NoiseSamp);

        // #339: the scene's environment map, on its own unit. Re-read per bind for AurTex's reason -- the
        // pointer is borrowed and an area transition frees it.
        _envTid = SceneEnvTid();
        Gl.ActiveTexture(Gl.TEXTURE0 + EnvUnit);
        Gl.BindTexture(Gl.TEXTURE_2D, (uint)_envTid);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>The environment-map texture id for this pass, or 0 when there is none to sample.
    /// <see cref="AlloyEnvMap"/> makes the CHOICE -- the area tileset's own resref, else the engine's
    /// <c>chrome1</c> fallback, so one is present even where the MATERIAL has no env map -- and holds it in
    /// the texture registry.</summary>
    private static int SceneEnvTid() => AlloyEnvMap.Tid();

    /// <summary>Is the environment term live this pass? The stage reads it as a switch, because a missing
    /// env map must leave the surface shaded rather than multiplied by black.</summary>
    internal static int EnvOk => _envTid != 0 ? 1 : 0;

    private static int _envTid;

    /// <summary>Drop the noise sampler off its unit. The unit is shared with the soft-particle scene depth
    /// (<see cref="TextureSlots.SceneDepth"/>), and a sampler object left bound overrides that texture's own
    /// parameters in a later pass.</summary>
    internal static void Unbind()
    {
        if (_gl.NoiseSamp != 0 && Gl.BindSampler != null) Gl.BindSampler(NoiseUnit, 0);
    }

    private static bool EnsureNoiseSampler()
    {
        if (_gl.NoiseSamp != 0) return true;
        if (Gl.GenSamplers == null || Gl.BindSampler == null || Gl.SamplerParameteri == null) return false;
        uint sm; Gl.GenSamplers(1, &sm);
        if (sm == 0) return false;
        Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_S, Gl.REPEAT);
        Gl.SamplerParameteri(sm, Gl.TEXTURE_WRAP_T, Gl.REPEAT);
        // ponytail: LINEAR, not a mip filter. A mip filter on a texture that turns out to have only level 0
        // makes it INCOMPLETE, which samples black -- the one failure that looks exactly like the bug this
        // sampler exists to prevent. Add mips (and the level query behind them) if the far sea aliases.
        Gl.SamplerParameteri(sm, Gl.TEXTURE_MIN_FILTER, Gl.LINEAR);
        Gl.SamplerParameteri(sm, Gl.TEXTURE_MAG_FILTER, Gl.LINEAR);
        _gl.NoiseSamp = sm;
        return true;
    }

    private static bool _noiseMissed;
    /// <summary>The GL id the noise texture last resolved to, on the <c>/water</c> line. Zero with water on
    /// screen is the whole diagnosis of a flat sea: no noise, no wave amplitude.</summary>
    private static int _noiseTid;
    /// <summary>What the resman actually handed back for <c>solid_noise</c>.</summary>
    private static string _noiseName = "?";
    /// <summary>The engine's own account of the noise texture, latched with the name.</summary>
    private static string _noiseEng = "eng=?";

    /// <summary>Size the copy pair to the pass. The DEPTH half is allocated from what the engine's own
    /// attachment reports rather than from a guess: <c>glBlitFramebuffer</c> requires the two depth formats
    /// to match, so a 24-bit target copied into a 32F texture is an INVALID_OPERATION and a silently empty
    /// depth buffer -- which reads as "every screen-space ray hits the near plane".</summary>
    private static bool EnsureTargets(int w, int h)
    {
        var g = _gl;
        bool hdr = PostChain.Armed;
        // ...and on the COLOUR FORMAT, not only the size. #139 can arm or disarm mid-session at an unchanged
        // resolution (a /post hdr toggle, a target the pass refuses on one frame and takes on the next), and
        // a cache keyed on size alone would then blit a linear HDR frame into an 8-bit copy and clip every
        // highlight the water was about to reflect.
        if (g.Fbo != 0 && g.W == w && g.H == h && g.Hdr == hdr && g.ColTex != 0 && g.DepTex != 0) return true;
        g.W = w; g.H = h; g.Hdr = hdr;
        if (g.Fbo == 0) { uint f; Gl.GenFramebuffers(1, &f); g.Fbo = f; }
        if (g.Fbo == 0) return Fail("could not create the copy framebuffer");

        // #139: RGBA16F while the HDR target is armed, because that is what the copy SOURCE holds -- an 8-bit
        // copy of a linear HDR frame clips every highlight the water would reflect, and those are exactly the
        // pixels a reflection is made of.
        // LINEAR on both: the refraction and the SSR march sample at an offset, not at texel centres.
        RenderTarget.AllocColor(ref g.ColTex, hdr, w, h, Gl.LINEAR);
        bool packed = RenderTarget.Packed(g.DepFormat);
        RenderTarget.AllocDepth(ref g.DepTex, g.DepFormat, w, h, Gl.LINEAR);
        if (g.ColTex == 0 || g.DepTex == 0) return Fail("could not create the copy targets");

        Gl.BindFramebuffer(Gl.FRAMEBUFFER, g.Fbo);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, g.ColTex, 0);
        Gl.FramebufferTexture2D(Gl.FRAMEBUFFER,
            packed ? Gl.DEPTH_STENCIL_ATTACHMENT : Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D, g.DepTex, 0);
        uint status = Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER);
        if (status != Gl.FRAMEBUFFER_COMPLETE)
        {
            g.W = g.H = 0;
            return Fail($"the copy framebuffer is incomplete (0x{status:x})");
        }
        _log?.Info($"FrameSnapshot: framebuffer copy {w}x{h}, colour {(hdr ? "RGBA16F" : "RGBA8")}, " +
                   $"depth 0x{g.DepFormat:x} -- one copy per frame, /water enabled 0 disables");
        return true;
    }

    /// <summary>What depth format the blit source carries, re-probed whenever the target changes size (the
    /// engine reallocates its attachments on a resolution change, and a cached answer would then size the
    /// copy from the previous mode). The default framebuffer answers under attachment point GL_DEPTH, where
    /// GL_DEPTH_ATTACHMENT is not a legal name.</summary>
    private static bool ProbeDepth(uint src, int w, int h)
    {
        if (_gl.DepFormat != 0 && _gl.W == w && _gl.H == h) return true;
        uint fmt = RenderTarget.ProbeDepthFormat(src);
        if (fmt == 0) return Refuse("the pass target has no depth attachment to copy");
        _gl.DepFormat = fmt;
        bool packed = RenderTarget.Packed(_gl.DepFormat);
        _gl.DepMask = packed ? Gl.STENCIL_BUFFER_BIT | Gl.DEPTH_BUFFER_BIT : Gl.DEPTH_BUFFER_BIT;
        return true;
    }

    /// <summary>Permanent: the class cannot work in this context at all. Water reverts to the engine's own
    /// draw for the session.</summary>
    private static bool Fail(string why)
        => _down.Fail(_log, why, "water is NOT DRAWN for the session -- #285: since #264 deleted the engine "
                               + "walk there is nothing behind the refusal");

    /// <summary>Per-pass: this particular pass cannot take the copy. NOT latched -- a GUI 3D panel or a
    /// reflection subframe legitimately refuses while the world pass keeps arming -- but the reason is logged
    /// once per distinct reason, because the fallback is otherwise silent.</summary>
    private static bool Refuse(string why)
        => _down.Refuse(_log, why, "no framebuffer copy this pass; water is not drawn on it (#285: a hole, not a hand-back)");

    /// <summary>The periodic line. <c>tod</c> is the engine clock the whole wave field's ANIMATION runs on
    /// (<c>fTick = worldtimerTimeOfDay * 0.001</c>), printed with its delta over the census period: a delta
    /// of ZERO means the phase never advances and the sea is a still photograph of a wave field, however
    /// correct that field is in space. Cheap, and it is the one thing a screenshot cannot show.</summary>
    internal static string Stats(int frames)
    {
        int tod = AlloyRenderer.TimeOfDay;
        int delta = tod - _todLast;
        _todLast = tod;
        return $"water: {Copies} copies, {Refusals} refused" + (frames > 0 ? $" over {frames} frames" : "")
             + $", tod {tod} ({(delta >= 0 ? "+" : "")}{delta} since last)"
             // Per period, not just in the boot census: the noise id is read at BIND time, and the one-shot
             // line could only ever report the state before any water had drawn.
             + $", noise {_noiseTid}{(_noiseTid == 0 ? " -- NO WAVE FIELD" : _noiseEngTid == 0 ? " (CX's own; the engine's has none)" : "")}";
    }

    private static int _todLast;

    /// <summary>The <c>water</c> knobs, off-device: the master stands the class down without touching the
    /// stand-down latch, and the diagnostic budget has no boot value -- a readback is a live request.</summary>
    private static void CheckTuning(Checker check)
    {
        bool on = Water.Enabled.V; int diag = Water.Diag.V;
        try
        {
            Water.Diag.V = 1;
            check.Expect(Describe().Contains("DIAG 1 left"), "diag is a BUDGET: Describe says how many readbacks are owed while any are");
            Water.Enabled.V = false;
            check.Expect(!On && !Usable, "water.enabled off stands the class down without touching the stand-down latch");
            check.Expect(Water.Enabled.Default && Water.Diag.Default == 0, "reset re-arms the class and spends no diagnostic readbacks");
        }
        finally
        {
            Water.Enabled.V = on; Water.Diag.V = diag;
        }
    }

    /// <summary>Off-device (run-demo.sh): the stage's own contracts, the ones a GPU would only report as a
    /// wrong-looking sea. Two are structural -- water reads the copy the pass list takes immediately before
    /// it, and the probe is what the reflection falls back to -- and both are text checks on the source that
    /// is actually compiled, which is the only thing an override cannot silently change out from under.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        CheckTuning(check);

        string fs = Fs;
        check.Expect(fs.Contains("uniform sampler2D uFbColor") && fs.Contains("uniform sampler2D uFbDepth"),
          "the stage declares the copy pair it marches");
        // #339's own criterion, as a text check: the shading is stock fswater's, not a look of ours. The
        // march is vanilla's GetRayIntersectCoords, the wave field reads the ENGINE's noise texture, and the
        // reflection's fallback is the shaded surface -- a probe here would be the custom look coming back.
        check.Expect(fs.Contains("CxRayIntersect("), "the stage marches with stock's own ray intersection");
        check.Expect(fs.Contains("texture(uNoise,"), "the wave field reads the engine's noise texture");
        check.Expect(!fs.Contains("samplerCube uProbe"), "the probe is NOT the reflection's fallback -- vanilla falls back to the surface");
        // Colour space: since #132 the whole Alloy frame is linear, so the copy is linear and a decode on
        // read would be a second linearisation. Sampling it in the wrong space is a whole-image error that
        // reads as "the reflection is washed out" (or "far too dark") rather than as a bug, so it is
        // asserted rather than left to the next reader of the shader text.
        check.Expect(fs.Contains("return texture(uFbColor, uv).rgb;") && !fs.Contains("CxLinear(texture(uFbColor"),
          "the copy is sampled as LINEAR, with no decode");
        // Ordering: FbCopy immediately precedes WaterOwned, and both sit between the blended class and
        // additive. That IS the ticket's "one copy, vanilla's placement" -- it is not an implementation
        // detail of the pass list, it is the design.
        int copy = -1, water = -1, blend = -1, add = -1;
        for (int i = 0; i < FrameGraph.Passes.Length; i++)
        {
            PassId id = FrameGraph.Passes[i].Id;
            if (id == PassId.FbCopy) copy = i;
            if (id == PassId.WaterOwned) water = i;
            if (id == PassId.BlendedOwned) blend = i;
            if (id == PassId.AdditiveOwned) add = i;
        }
        check.Expect(copy >= 0 && water == copy + 1, "the water pass reads a copy taken immediately before it");
        check.Expect(blend >= 0 && blend < copy, "the blended class (fade included) is IN the copy");
        check.Expect(add >= 0 && add > water, "additive is NOT in the copy, and composites over the water");
        check.Expect(ColorUnit > EnvProbe.ProbeUnit && DepthUnit > ColorUnit,
          "the copy's units sit above the probe cube's -- two sampler types on one unit drops the draw");
        // #339: every view the stage implements is REACHABLE. /alloy viz clamps to AlloyRenderer.LightVizMax,
        // silently -- a view above it paints the highest one instead, which looks like a working instrument
        // answering the wrong question. Read off the stage text so adding a view and forgetting the ceiling
        // fails here rather than in a screenshot.
        int highest = 0;
        for (int i = fs.IndexOf("uViz == "); i >= 0; i = fs.IndexOf("uViz == ", i + 1))
        {
            int j = i + 8, n = 0;
            while (j < fs.Length && fs[j] >= '0' && fs[j] <= '9') { n = n * 10 + (fs[j] - '0'); j++; }
            if (n > highest) highest = n;
        }
        check.Expect(highest <= AlloyRenderer.LightVizMax,
          $"the stage's highest view ({highest}) is above /alloy viz's ceiling ({AlloyRenderer.LightVizMax})");
        check.Expect(NoiseUnit != ColorUnit && NoiseUnit != DepthUnit && NoiseUnit != TextureSlots.Diffuse,
          "the noise texture has a unit of its own");

        log.Info(check.Ok ? "FrameSnapshot.SelfCheck: passed" : "FrameSnapshot.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The whole fragment stage, composed by <see cref="ShaderStages"/> -- the one place any owned
    /// stage's composition is written down (ADR 0012).</summary>
    internal static string Fs => ShaderStages.Source(AlloyRenderer.ShdWaterFs);

    /// <summary>The water vertex stage: the lit pass's clip expression and nothing else. Deliberately not the
    /// lit stage itself -- water is never skinned and never a wind-feature part -- and not a tessellating one
    /// either: vanilla water displaces in the FRAGMENT stage (fswater's parallax search over
    /// GetMainWaveOffset), so there is nothing for a tessellator to do.</summary>
    internal static string Vs => ShaderStages.Source(AlloyRenderer.ShdWaterVs);

    /// <summary>The water fragment stage, part one: the declarations, and the stock constants they carry.
    /// Every number is <c>inc_water.shd</c>'s own <c>#define</c> at the quality mode this client runs
    /// (<c>SHADER_QUALITY_MODE 2</c>). Only that branch is ported: the two lower tiers are different shaders,
    /// and reproducing a look nobody on this client sees is the kind of ladder that rots.</summary>
    internal static string FsHead => ShaderSource.Get("cx_water_fhead.glsl");
}
