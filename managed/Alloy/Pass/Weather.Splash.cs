using System;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The splash pass -- what a drop does when it ARRIVES. One instanced attribute-less draw beside the
/// precipitation field, in the same bracket and the same premultiplied blend: the instance id hashes to a
/// point on a camera-centred disc and a time slice, the cover map's depth over that point is where the rain
/// lands, and the crown is drawn there. No plain-depth cover view, no splashes, and it says so once.</summary>
internal static unsafe partial class Weather
{
    internal const string ShdSplashVs = "cx_splash_vs", ShdSplashFs = "cx_splash_fs";

    /// <summary>Quads per crown: the ring plus <c>CX_SPLASH_DROPS</c> droplets. This IS the shader's
    /// <c>CX_SPLASH_PARTS</c> -- the draw's instance count is a multiple of it and the stage divides the
    /// instance id by it, so the two disagreeing shears every crown apart. <see cref="SplashSelfCheck"/>
    /// reads the constant back out of the GLSL.</summary>
    internal const int SplashParts = 6;
    /// <summary>One crown's life, seconds: the leap, the fall and the ring, start to finish.</summary>
    private const float SplashLifeSec = 0.22f;
    /// <summary>Lifetimes the splash clock is wrapped at. A whole number of them, so the slice index the
    /// stage floors out of the clock is continuous across the wrap and no frame re-rolls the whole field.</summary>
    private const int SplashWrapSlices = 8192;
    /// <summary>Crowns at full rain per unit of the density knob, as a share of <see cref="MaxCount"/>: the
    /// splash field is sized off the same budget the streaks are, so one knob still bounds the whole pass.</summary>
    private const float SplashShare = 0.25f;

    internal static Knob<float> Impacts = null!, ImpactRange = null!;

    private static SpLoc _locSplash;
    private static AlloyRenderer.LitLoc _litSplash;
    private static float _splashT;
    /// <summary>The disc centre's smoothed xy velocity, and the centre and clock it was last measured at.</summary>
    private static Vector2 _splashVel, _splashWasC;
    private static float _splashWasT = -1f;
    /// <summary>Seconds the centre velocity is smoothed over: frame pacing must not jitter every crown.</summary>
    private const float SplashVelTau = 0.1f;
    /// <summary>A centre step past this many m/s is a teleport or an area edge, not motion: velocity resets.</summary>
    private const float SplashVelMax = 20f;
    private static int _splashes;

    internal struct SpLoc { public int View, Proj, Eye, Lit, P, C, V, CoverDepth, CoverVp, CoverVpInv, Depth, Tint, Alpha, Rain, Foot, FootW, WxMapXf, WxMapP; }

    private static void DeclareSplashKnobs()
    {
        G.Section("Impacts");
        Impacts = G.Slider("impacts", "Splash impacts", 0f, 1f, 0.05f, 0.6f, tier: Tier.Advanced,
                           help: "Water leaps where each drop lands -- on the ground, on roofs, on ledges, wherever the rain reaches. 0 = off.")
                   .Ladder(Quality.Weather, 0f, 0.35f, 0.6f, 1f);
        G.Section("");
        ImpactRange = G.Slider("impactrange", "Impact range", 8f, 40f, 2f, 24f, "m", Tier.ChatOnly,
                               help: "Radius of the disc around you that splashes are placed in. Wider spreads the same number of impacts thinner.");
    }

    /// <summary>Crowns to draw this frame: rain only, and only while there is a cover map to read the landing
    /// points out of. 0 = the pass sits out.</summary>
    private static int SplashCount() => Cover.V && CasterPass.CoverFresh && CoverDepthOk ? SplashWanted() : 0;

    /// <summary>The crowns the knobs and the weather ask for, before the cover map gets its veto: rain only,
    /// at the impacts knob under the shared instance budget. Split out so the self-check can drive it without
    /// a live cover bake.</summary>
    private static int SplashWanted()
        => _gl.SplashFailed || Impacts.V <= 0f || CurrentKind != Kind.Rain || Density <= 0f ? 0
         : (int)(MaxCount.V * SplashShare * Impacts.V * Density * DensityScale.V);

    /// <summary>The splash draw, inside the precipitation bracket (its blend, depth test and depth mask are
    /// already set). Depth-tested against the finished frame with no depth write, so a wall in front of an
    /// impact hides it.</summary>
    private static void DrawSplash(Matrix4x4* view, Matrix4x4* proj, int* viewport, Vector3 eye, float near, float far, bool lightingOn)
    {
        _splashes = 0; _footSplashes = 0;
        int crowns = SplashCount(), feet = FootLive(TrackNow);
        if (crowns <= 0 && !_gl.SplashSaid && SplashWanted() > 0 && Cover.V && CasterPass.CoverFresh && CoverDepthView == 0)
        {
            _gl.SplashSaid = true;
            _log?.Info("Weather: no plain-depth view of the cover map -- rain splashes sit out (the landing point is unreadable without it)");
        }
        Matrix4x4 vp = default, inv = default;
        // the footfalls read the map too, for the ground under the foot, so it binds whether or not it rains
        bool cover = Cover.V && CasterPass.CoverFresh && CoverDepthView != 0 && Matrix4x4.Invert(CasterPass.CoverVp, out inv);
        if (cover) vp = CasterPass.CoverVp;
        else crowns = 0;
        if (crowns + feet <= 0 || !EnsureSplash()) return;
        if (!AlloyLights.TryPerspective(*proj, out _, out float p11, out _, out _)) return;
        ref SpLoc l = ref _locSplash;
        Gl.UseProgram(_gl.ProgSplash);
        Gl.UniformMatrix4fv(l.View, 1, 0, (float*)view);
        Gl.UniformMatrix4fv(l.Proj, 1, 0, (float*)proj);
        Gl.Uniform3f(l.Eye, eye.X, eye.Y, eye.Z);
        Vector3 centre = FrameInputs.HasFocus ? FrameInputs.Focus : eye;
        // z = the height the cover ray's forward projection starts from: the fit's own centre, which is
        // what the map scatter uses for the same Newton step
        Gl.Uniform3f(l.C, centre.X, centre.Y, CasterPass.CoverCentre.Z);
        Vector2 vel = SplashVelocity(new Vector2(centre.X, centre.Y));
        Gl.Uniform2f(l.V, vel.X, vel.Y);
        // w = pixels per metre at unit view depth: the stage's distance FADE, never a size
        Gl.Uniform4f(l.P, _splashT, SplashLifeSec, ImpactRange.V, p11 * viewport[3] * 0.5f);
        Gl.UniformMatrix4fv(l.CoverVp, 1, 0, (float*)&vp);
        Gl.UniformMatrix4fv(l.CoverVpInv, 1, 0, (float*)&inv);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.CoverDepth);
        Gl.BindTexture(Gl.TEXTURE_2D, cover ? CoverDepthView : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(l.CoverDepth, (int)TextureSlots.CoverDepth);
        Gl.Uniform1i(l.Depth, (int)SceneDepth.Unit);
        // the footfalls: past the rain crowns, gated per point by the wetness map (bound by UploadMap)
        Gl.Uniform1i(l.Rain, crowns * SplashParts);
        fixed (Vector4* f = _footLive) Gl.Uniform4fv(l.Foot, feet, (float*)f);
        Gl.Uniform2f(l.FootW, WetShown, FootSplash.V);
        UploadMap(l.WxMapXf, l.WxMapP, false);
        float c = Brightness.V;
        Gl.Uniform3f(l.Tint, c, c, c);
        Gl.Uniform1f(l.Alpha, MathF.Min(Opacity.V * 1.5f, 1f));
        bool lit = lightingOn && AlloyLights.Ready;
        Gl.Uniform1i(l.Lit, lit ? 1 : 0);
        AlloyRenderer.BindPass(_litSplash.CubeCount, viewport, near, far);
        if (lit) AlloyLights.BindForDraw();
        Gl.DrawArraysInstancedBaseInstance(GlTriangleStrip, 0, 4, (crowns + feet * FootCrowns) * SplashParts, 0);
        _splashes = crowns;
        _footSplashes = feet;
        _drawn += (crowns + feet * FootCrowns) * SplashParts;
    }

    private static bool EnsureSplash()
    {
        if (_gl.SplashFailed || Gl.DrawArraysInstancedBaseInstance == null) return false;
        if (_gl.ProgSplash != 0) return true;
        _gl.ProgSplash = GlProgram.Graphics(ShdSplashVs, ShaderStages.Source(ShdSplashVs), ShdSplashFs, ShaderStages.Source(ShdSplashFs), _log, "Weather splash");
        if (_gl.ProgSplash == 0)
        { _gl.SplashFailed = true; _log?.Info("Weather: splash program build failed -- no rain impacts this session"); return false; }
        ref SpLoc l = ref _locSplash;
        l.View = GlProgram.Loc(_gl.ProgSplash, "uView"); l.Proj = GlProgram.Loc(_gl.ProgSplash, "uProj");
        l.Eye = GlProgram.Loc(_gl.ProgSplash, "uEye"); l.Lit = GlProgram.Loc(_gl.ProgSplash, "uLit");
        l.P = GlProgram.Loc(_gl.ProgSplash, "uSplashP"); l.C = GlProgram.Loc(_gl.ProgSplash, "uSplashC"); l.V = GlProgram.Loc(_gl.ProgSplash, "uSplashV");
        l.CoverDepth = GlProgram.Loc(_gl.ProgSplash, "uCoverDepth");
        l.CoverVp = GlProgram.Loc(_gl.ProgSplash, "uCoverVp"); l.CoverVpInv = GlProgram.Loc(_gl.ProgSplash, "uCoverVpInv");
        l.Depth = GlProgram.Loc(_gl.ProgSplash, "uSceneDepth");
        l.Tint = GlProgram.Loc(_gl.ProgSplash, "uTint"); l.Alpha = GlProgram.Loc(_gl.ProgSplash, "uAlpha");
        l.Rain = GlProgram.Loc(_gl.ProgSplash, "uSplashRain"); l.Foot = GlProgram.Loc(_gl.ProgSplash, "uFoot"); l.FootW = GlProgram.Loc(_gl.ProgSplash, "uFootW");
        l.WxMapXf = GlProgram.Loc(_gl.ProgSplash, "uWxMapXf"); l.WxMapP = GlProgram.Loc(_gl.ProgSplash, "uWxMapP");
        _litSplash = Programs.LitLocs(_gl.ProgSplash);
        return true;
    }

    /// <summary>Fold this frame's centre into the smoothed velocity the stage back-dates each crown's birth with.</summary>
    private static Vector2 SplashVelocity(Vector2 c)
    {
        float dt = _splashT - _splashWasT;
        if (dt < 0f) dt += SplashLifeSec * SplashWrapSlices;
        if (_splashWasT >= 0f && dt > 1e-4f && dt < 0.5f)
        {
            Vector2 v = (c - _splashWasC) / dt;
            _splashVel = v.Length() > SplashVelMax ? Vector2.Zero : Vector2.Lerp(_splashVel, v, MathF.Min(dt / SplashVelTau, 1f));
        }
        else if (dt >= 0.5f) _splashVel = Vector2.Zero;
        _splashWasC = c;
        _splashWasT = _splashT;
        return _splashVel;
    }

    private static void SplashTick(float dt) => _splashT = (_splashT + dt) % (SplashLifeSec * SplashWrapSlices);

    private static string SplashStats() => Impacts.V <= 0f ? "" : $", {_splashes} splashes";

    /// <summary>The splash vertex stage: the disc, the cover-map landing point and the crown's ballistics,
    /// all a function of the instance id, plus the drops' own per-vertex light rig.</summary>
    internal static string SplashVs => ShaderSource.Get("cx_wx_sv.glsl");

    /// <summary>One crown part: a ring on the ground or a droplet streak, as premultiplied scatter.</summary>
    internal static string SplashFs => ShaderSource.Get("cx_splash_f.glsl");

    /// <summary>What about this pass cannot be seen going wrong, against the GLSL text and hand-written literals:
    /// the part count the draw multiplies by has to be the one the stage divides by; a crown has to stand on a
    /// plane the frame draws, laid out in it, at the puddle ripple's size; screen size and lens distance have to
    /// fade it, never grow it; and the clock has to wrap on a whole lifetime.</summary>
    private static void SplashSelfCheck(Checker check)
    {
        string v = SplashVs;
        check.Expect(v.Contains($"const int CX_SPLASH_DROPS = {SplashParts - 1};")
          && v.Contains("const int CX_SPLASH_PARTS = CX_SPLASH_DROPS + 1;")
          && v.Contains("int part = int(id % uint(CX_SPLASH_PARTS));"),
          $"the stage splits each instance id into the {SplashParts} parts the draw counts in");
        check.Expect(GlslConst(v, "CX_SPLASH_RING_M") == GlslConst(RippleLib, "CX_RIPPLE_REACH_M"),
          "a splash ring and a puddle ripple are the same drop, so the same reach");
        check.Expect(v.Contains("float px = uSplashP.w / max(-cv.z, 0.05);")
          && v.Contains("float life = uSplashP.y, gain = 1.0, v0k = 1.0, ringM = CX_SPLASH_RING_M;")
          && v.Contains("ringM = CX_SPLASH_RING_M * mix(CX_FOOT_RING.x, CX_FOOT_RING.y, pool);")
          && v.Contains("dim = smoothstep(CX_SPLASH_PX.x, CX_SPLASH_PX.y, ringM * CX_SPLASH_CREST * px);")
          && v.Contains("pos = P + (T * corner.x + B * corner.y) * (ringM * 1.15) + up * CX_SPLASH_LIFT;")
          && v.Contains("vSeed = (0.4 + 0.6 * fract(hc.x * 7.31)) * dim;"),
          "the ring is strictly world-sized: its screen size only FADES it");
        check.Expect(v.Contains("float floorM = CX_SPLASH_MINPX / max(px, 1e-3);")
          && v.Contains("dim = (CX_SPLASH_DROP_M * len) / (w * l);"),
          "a sub-pixel DROPLET widens to a pixel and gives back the area in scatter, rather than flickering");
        check.Expect(v.Contains("const vec2 CX_SPLASH_NEAR = vec2(0.5, 1.0);")
          && v.Contains("dim *= gain * smoothstep(CX_SPLASH_NEAR.x, CX_SPLASH_NEAR.y, -cv.z);"),
          "a crown inside a metre of the lens fades out rather than filling the screen (a camera in a wall sits on the surface the rain lands on)");
        check.Expect(v.Contains("float d = texture(uCoverDepth, uv).r;") && v.Contains("if (d >= 1.0) return p;")
          && v.Contains("if (uCoverVp[3][3] == 0.0) return p;"),
          "a splash lands where the cover map says the rain does, and nowhere when it says nothing");
        check.Expect(v.Contains("if (length(xp + xn - 2.0 * p) + length(yp + yn - 2.0 * p) > CX_SPLASH_FLAT_M) return p;"),
          "no crown on an edge texel, where the filtered depth blends a roof with the ground under it and the point hangs in the air");
        check.Expect(v.Contains("up = normalize(cross(xp - xn, yp - yn));")
          && v.Contains("vec3 dir = T * (cos(a) * sin(theta)) + B * (sin(a) * sin(theta)) + up * cos(theta);")
          && v.Contains("if (dot(P - L, up) < 0.0) alive = false;"),
          "a crown stands on the surface it hit: the ring lies in its plane and the droplets leave along its normal");
        check.Expect(v.Contains("if (onPlane && abs(ground.z - L.z) < CX_FOOT_GROUND_M) { L = ground; up = n; }"),
          "a footfall's crowns stand on the ground under the foot too, unless the cover map's hit there is a roof over it");
        float steep = MathF.Acos(GlslConst(v, "CX_SPLASH_STEEP")) * (180f / MathF.PI);
        check.Expect(MathF.Abs(steep - 80f) < 0.5f && v.Contains("ok = up.z >= CX_SPLASH_STEEP;"),
          $"only a face the fall grazes, past 80 degrees, takes no crown (got {steep:F1})");
        check.Expect(v.Contains("alive = ok && fract(hp.z * 91.7) <= edge && CxSplashSeen(L);")
          && v.Contains("return scene + v.z < CX_SPLASH_CONTACT.x - CX_SPLASH_CONTACT.y * v.z;"),
          "no crown where the frame shows nothing at the landing point: a wall the keyhole dissolved is still in the cover map");
        check.Expect(ShaderContract.LinearSafe(SplashFs) && SplashFs.Contains("scatter * cov")
          && SplashFs.Contains("dot(scatter, CX_WX_LUMA)") && !SplashFs.Contains("uSoft"),
          "the splash stage is linear premultiplied scatter with no contact fade (a splash sits ON the surface)");
        float wrap = SplashLifeSec * SplashWrapSlices;
        float was = _splashT; _splashT = wrap - 0.01f; SplashTick(0.02f);
        check.Expect(MathF.Abs(_splashT - 0.01f) < 1e-3f, $"the splash clock wraps on a whole lifetime (got {_splashT:F4})");
        _splashT = was;
        float d = Density; Kind k = CurrentKind; float im = Impacts.V;
        CurrentKind = Kind.Rain; Density = 1f; Impacts.V = 0f;
        check.Expect(SplashWanted() == 0, "the impacts knob at 0 asks for no crowns, whatever the rain is doing");
        Impacts.V = Impacts.Default;
        int wet = SplashWanted();
        CurrentKind = Kind.Snow;
        check.Expect(wet > 0 && wet <= MaxCount.V && SplashWanted() == 0,
          $"full rain asks for {wet} crowns inside the {MaxCount.V} instance budget, and snow for none");
        Density = d; CurrentKind = k; Impacts.V = im;
        Vector2 vc = _splashVel, wc = _splashWasC; float wt = _splashWasT, st = _splashT;
        _splashWasT = -1f; _splashVel = Vector2.Zero; _splashT = 1f;
        SplashVelocity(Vector2.Zero);
        for (int i = 1; i <= 30; i++) { _splashT = 1f + i * 0.016f; SplashVelocity(new Vector2(3f * i * 0.016f, 0f)); }
        check.Expect(MathF.Abs(_splashVel.X - 3f) < 0.1f && MathF.Abs(_splashVel.Y) < 1e-3f,
          $"a centre walking 3 m/s back-dates its crowns at 3 m/s (got {_splashVel.X:F2})");
        _splashT += 0.016f; SplashVelocity(new Vector2(100f, 0f));
        check.Expect(_splashVel == Vector2.Zero, "a teleport resets the velocity instead of flinging the field");
        check.Expect(v.Contains("uSplashC.xy - uSplashV * (age * uSplashP.y)"), "a crown is placed off the centre at its birth");
        _splashVel = vc; _splashWasC = wc; _splashWasT = wt; _splashT = st;
    }
}
