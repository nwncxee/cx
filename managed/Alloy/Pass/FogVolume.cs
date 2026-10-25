using System;
using System.Diagnostics;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The volumetric fog <c>vfog.enabled</c> draws: a froxel grid over the main view, lit by the area's fog colour,
/// the sun through its cascades and every clustered lamp and filtered over frames (<c>cx_vfog_cs</c>), then integrated
/// from the eye with the area's fog wall (<c>cx_vfogsum_cs</c>) into the one volume every fogging stage reads
/// (<c>cx_vfog.glsl</c>). The area's fog start and end shape the wall; nothing here reproduces the ramp's look.</summary>
internal static unsafe class FogVolume
{
    internal const string ShdLightCs = "cx_vfog_cs", ShdSumCs = "cx_vfogsum_cs";
    /// <summary>The transmittance the fog leaves at the area's fog end, out from the player: the far plane sits past it.</summary>
    internal const float EndTransmittance = 0.01f;
    /// <summary>The largest share of that opacity the haze takes; the wall keeps the rest, so the far plane stays hidden.</summary>
    internal const float MaxHaze = 0.9f;
    /// <summary>The narrowest ramp the wall takes, metres: an area whose fog starts at its end gets a wall this thin.</summary>
    private const float MinRamp = 0.5f;
    /// <summary>In an area with no fog of its own, the haze spends its share over the view's far distance, or this if longer.</summary>
    private const float AirFloor = 150f;
    /// <summary>The volume's near distance, metres: the grid's first slice starts here.</summary>
    private const float Near = 1f;
    /// <summary>Invocations per group along x and y, matching both stages' <c>local_size</c>.</summary>
    private const int Group = 8;
    /// <summary>Seconds over which the sun's share of the air eases in at dawn and out at dusk.</summary>
    private const float DayEase = 2f;
    /// <summary>The most a channel of the sun's hue (its colour over its luminance) may weigh: a red dusk stays red, not a flare.</summary>
    private const float MaxHue = 2f;
    /// <summary>The haze's patches rise this fraction of their drift speed, so the air churns on a still day too.</summary>
    private const float Rise = 0.1f;
    /// <summary>The ramp's horizon band a pass that cannot read the volume (a probe face, a reflection) fogs its sky with.</summary>
    internal const float CaptureBand = 0.15f;

    /// <summary>The medium's extinction, per metre: <see cref="Haze"/> at the haze floor, <see cref="Wall"/> from
    /// <see cref="End"/> on, rising from nothing at <see cref="Start"/>; both distances from the player.</summary>
    internal readonly record struct Medium(float Haze, float Wall, float Start, float End);

    /// <summary>Uniform locations of the lighting pass.</summary>
    private struct LightLocs
    {
        internal GridLocs Grid;
        internal int Height, Noise, Drift, Ambient, Sun, Local, Phase, Jitter, PrevVp, PrevZ, Cubes;
    }

    /// <summary>Locations of the grid and medium uniforms both passes declare (<c>cx_vfogmed.glsl</c>).</summary>
    private struct GridLocs { internal int Size, Z, InvVp, Eye, Fwd, Med, Origin, Top; }

    /// <summary>Everything the GL context owns: context loss replaces it whole.</summary>
    private sealed class FogVolumeGlState
    {
        public uint ProgLight, ProgSum;
        public LightLocs L;
        public GridLocs S;
        /// <summary>The lighting pass's two outputs (this frame's and last frame's, swapped each frame) and the
        /// integrated volume, whose last slice is also the sky's.</summary>
        public uint MedA, MedB, Sum;
        public int W, H, D;
        public bool CurA, History;
        public Matrix4x4 PrevVp;
        public float PrevInvLog;
        public long Frame;
        /// <summary>This frame's volume is built, for the main view whose unjittered view-projection is <see cref="Vp"/>.</summary>
        public bool Built;
        public Matrix4x4 Vp;
        public float InvLog;
    }

    /// <summary>What outlives a context: the log, the day/night easing, the drift, the counters.</summary>
    private sealed class FogVolumeSessionState
    {
        public ExtenderLog? Log;
        public float Day = 1f;
        public long LastTicks;
        /// <summary>How far the haze's patches have travelled, metres; double, since it grows all session.</summary>
        public double DriftX, DriftY, DriftZ;
        public int Runs;
        public Medium Last;
        public Vector3 LastAmbient;
        public Vector4 LastSun;
    }

    private static FogVolumeGlState _gl = new();
    private static FogVolumeSessionState _s = new();
    private static readonly StandDown _down = new("FogVolume");

    /// <summary>The mode is chosen and its programs have not failed: the far plane, the cull and the sky band follow
    /// the volume's shape rather than the ramp's.</summary>
    internal static bool On => VolumetricFog.Enabled.V && !_down.Failed;

    internal static void Attach(ExtenderLog log) => _s.Log = log;

    /// <summary>The medium for an area's fog: <paramref name="haze"/> of the opacity spread evenly out to the fog end,
    /// the rest a ramp from start to end, so a ray out from the player keeps <see cref="EndTransmittance"/> at the end.
    /// With no area fog the haze alone spans <paramref name="far"/> (at least <see cref="AirFloor"/>).</summary>
    internal static Medium MediumFor(bool on, float start, float end, float far, float haze)
    {
        float tau = -MathF.Log(EndTransmittance);
        float h = Math.Clamp(haze, 0f, MaxHaze);
        if (!on || end <= 0f) return new Medium(h * tau / MathF.Max(far, AirFloor), 0f, 0f, 1f);
        float e = MathF.Max(end, MinRamp), s = Math.Clamp(start, 0f, e - MinRamp);
        return new Medium(h * tau / e, 2f * (1f - h) * tau / (e - s), s, e);
    }

    /// <summary>The distance from the player past which the fog hides everything below the eye: where the wall alone
    /// spends the whole opacity, since the haze thins with height and the bank is whole below the eye. Infinity with
    /// no area fog.</summary>
    internal static float OpaqueDistance(bool on, float start, float end, float haze)
    {
        Medium m = MediumFor(on, start, end, 0f, haze);
        if (m.Wall <= 0f) return float.PositiveInfinity;
        float tau = -MathF.Log(EndTransmittance);
        return m.End + (tau - m.Wall * (m.End - m.Start) * 0.5f) / m.Wall;
    }

    /// <summary>The light the medium scatters from every direction, LINEAR: the area's fog colour, pulled toward the
    /// sky's average by <paramref name="skyMix"/> where a dome was read; with no area fog the sky or the area ambient.</summary>
    internal static Vector3 Ambient(bool fogOn, Vector3 fogDisplay, bool haveSky, Vector3 sky, Vector3 areaAmbient, float skyMix)
    {
        if (!fogOn) return haveSky ? sky : areaAmbient;
        var a = new Vector3(Lin(fogDisplay.X), Lin(fogDisplay.Y), Lin(fogDisplay.Z));
        return haveSky ? Vector3.Lerp(a, sky, skyMix) : a;
    }

    /// <summary>The share (w) of the medium's light that arrives as the sun's beam, and the sun's hue times it (xyz): the
    /// fog colour is the lit fog averaged over view directions, so a beam gains what its shade loses. Scales with <paramref name="day"/> (0 = the
    /// moon, which lights no air) and the sun's luminance up to 1; nothing indoors, under blind lights or unread.</summary>
    internal static Vector4 SunShare(bool interior, bool blind, bool haveDiffuse, Vector3 diffuse, float day, float strength)
    {
        if (interior || blind || !haveDiffuse) return default;
        Vector3 d = diffuse * AlloyLights.SunScale;
        float lum = 0.2126f * d.X + 0.7152f * d.Y + 0.0722f * d.Z;
        if (lum <= 1e-4f) return default;
        float k = Math.Clamp(strength, 0f, 1f) * day * MathF.Min(lum, 1f);
        return new Vector4(Vector3.Min(d / lum, new Vector3(MaxHue)) * k, k);
    }

    private static float Lin(float v) => MathF.Pow(MathF.Max(v, 0f), AlloyLights.SrgbGamma);

    /// <summary>Build this frame's volume for the main view, ahead of the first stage that fogs. <paramref name="main"/>
    /// false (a capture, no perspective) or the mode off leaves every stage on the ramp this frame.</summary>
    internal static void Run(in Matrix4x4 view, in Matrix4x4 vpClean, int* viewport, float near, float far, bool main, bool lightingOn)
    {
        _gl.Built = false;
        if (!VolumetricFog.Enabled.V) { Release(); return; }
        // a frame without a volume leaves last frame's medium describing a camera from frames ago
        if (!main || viewport[2] <= 0 || viewport[3] <= 0 || !Ready()
            || !Matrix4x4.Invert(vpClean, out Matrix4x4 invVp) || !Matrix4x4.Invert(view, out Matrix4x4 camToWorld))
        { _gl.History = false; return; }
        (int w, int h, int d) = VolumetricFog.Grid;
        Ensure(w, h, d);
        float dt = Tick();

        Vector3 eye = camToWorld.Translation;
        Vector3 fwd = -Vector3.Normalize(new Vector3(view.M13, view.M23, view.M33));
        float vfar = MathF.Max(far, Near * 2f);
        float logZ = MathF.Log(vfar / Near);
        Vector3 origin = SceneFog.Origin(out _);
        Medium m = MediumFor(FrameInputs.FogOn, FrameInputs.FogStart, FrameInputs.FogEnd, vfar, VolumetricFog.Haze.V);
        bool outdoors = !FrameInputs.Interior && !FrameInputs.Blind;
        bool haveSky = SceneFog.TrySky(out Vector3 sky) && outdoors;
        Vector3 ambient = Ambient(FrameInputs.FogOn, FrameInputs.FogColor, haveSky, sky,
                                  (FrameInputs.HasAreaAmbient ? FrameInputs.AreaAmbient : default) * AlloyLights.AmbientScale,
                                  VolumetricFog.Sky.V);
        _s.Day += ((FrameInputs.Night ? 0f : 1f) - _s.Day) * MathF.Min(dt / DayEase, 1f);
        Vector4 sun = SunShare(FrameInputs.Interior, FrameInputs.Blind, FrameInputs.HasAreaDiffuse, FrameInputs.AreaDiffuse,
                               _s.Day, VolumetricFog.Sun.V);
        _s.Last = m; _s.LastAmbient = ambient; _s.LastSun = sun;
        Vector2 wind = AlloyRenderer.AreaWind();
        Vector2 dir = wind.LengthSquared() > 1e-6f ? Vector2.Normalize(wind) : new Vector2(0.8f, 0.6f);
        float step = VolumetricFog.Wind.V * dt;
        _s.DriftX += dir.X * step; _s.DriftY += dir.Y * step; _s.DriftZ += Rise * step;
        bool temporal = VolumetricFog.Temporal.V > 0f;
        Vector3 jit = temporal ? Jitter(_gl.Frame) : new Vector3(0f, 0f, 0.5f);
        uint cur = _gl.CurA ? _gl.MedA : _gl.MedB, prev = _gl.CurA ? _gl.MedB : _gl.MedA;

        Gl.UseProgram(_gl.ProgLight);
        AlloyLights.BindForDraw();
        AlloyRenderer.BindPass(_gl.L.Cubes, viewport, near, far);
        PushGrid(in _gl.L.Grid, w, h, d, logZ, vfar, &invVp, eye, fwd, m, origin);
        Gl.Uniform2f(_gl.L.Height, origin.Z + VolumetricFog.Floor.V, VolumetricFog.Height.V);
        Gl.Uniform2f(_gl.L.Noise, VolumetricFog.Noise.V, 1f / MathF.Max(VolumetricFog.NoiseSize.V, 0.5f));
        Gl.Uniform3f(_gl.L.Drift, (float)_s.DriftX, (float)_s.DriftY, (float)_s.DriftZ);
        Gl.Uniform3f(_gl.L.Ambient, ambient.X, ambient.Y, ambient.Z);
        Gl.Uniform4f(_gl.L.Sun, sun.X, sun.Y, sun.Z, sun.W);
        Gl.Uniform3f(_gl.L.Local, lightingOn ? VolumetricFog.Lights.V : 0f, VolumetricFog.LightShadows.V ? 1f : 0f, VolumetricFog.Reach.V);
        Gl.Uniform1f(_gl.L.Phase, VolumetricFog.Aniso.V);
        Gl.Uniform3f(_gl.L.Jitter, jit.X, jit.Y, jit.Z);
        fixed (Matrix4x4* pv = &_gl.PrevVp) Gl.UniformMatrix4fv(_gl.L.PrevVp, 1, 0, (float*)pv);
        Gl.Uniform4f(_gl.L.PrevZ, Near, _gl.PrevInvLog, VolumetricFog.Temporal.V, _gl.History && temporal ? 1f : 0f);
        Gl.BindTextureUnit(TextureSlots.VFog, prev);
        Gl.BindImageTexture(0, cur, 0, 1, 0, Gl.WRITE_ONLY, Gl.RGBA16F);
        Gl.DispatchCompute((uint)((w + Group - 1) / Group), (uint)((h + Group - 1) / Group), (uint)d);
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);

        Gl.UseProgram(_gl.ProgSum);
        PushGrid(in _gl.S, w, h, d, logZ, vfar, &invVp, eye, fwd, m, origin);
        Gl.BindImageTexture(0, cur, 0, 1, 0, Gl.READ_ONLY, Gl.RGBA16F);
        Gl.BindImageTexture(1, _gl.Sum, 0, 1, 0, Gl.WRITE_ONLY, Gl.RGBA16F);
        Gl.DispatchCompute((uint)((w + Group - 1) / Group), (uint)((h + Group - 1) / Group), 1);
        Gl.MemoryBarrier(Gl.TEXTURE_FETCH_BARRIER_BIT | Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT);
        Gl.BindImageTexture(0, 0, 0, 0, 0, Gl.READ_ONLY, Gl.RGBA16F);
        Gl.BindImageTexture(1, 0, 0, 0, 0, Gl.WRITE_ONLY, Gl.RGBA16F);
        Gl.BindTextureUnit(TextureSlots.VFog, _gl.Sum);
        Gl.ActiveTexture(Gl.TEXTURE0);   // the unit every pass leaves current: the colour loop rebinds diffuse there
        Gl.UseProgram(0);

        _gl.Vp = vpClean; _gl.InvLog = 1f / logZ; _gl.Built = true;
        _gl.PrevVp = vpClean; _gl.PrevInvLog = _gl.InvLog; _gl.History = true;
        _gl.CurA = !_gl.CurA; _gl.Frame++;
        _s.Runs++;
    }

    /// <summary>The grid and medium uniforms, into whichever pass is bound.</summary>
    private static void PushGrid(in GridLocs g, int w, int h, int d, float logZ, float far, Matrix4x4* invVp, Vector3 eye,
                                 Vector3 fwd, Medium m, Vector3 origin)
    {
        Gl.Uniform3i(g.Size, w, h, d);
        Gl.Uniform3f(g.Z, Near, far, logZ);
        Gl.UniformMatrix4fv(g.InvVp, 1, 0, (float*)invVp);
        Gl.Uniform3f(g.Eye, eye.X, eye.Y, eye.Z);
        Gl.Uniform3f(g.Fwd, fwd.X, fwd.Y, fwd.Z);
        Gl.Uniform4f(g.Med, m.Haze, m.Wall, m.Start, m.End);
        Gl.Uniform3f(g.Origin, origin.X, origin.Y, origin.Z);
        Gl.Uniform1f(g.Top, VolumetricFog.Top.V);
    }

    /// <summary>Seconds since the last build, at most a tenth: a hitch does not throw the drift or the dawn ahead.</summary>
    private static float Tick()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _s.LastTicks == 0 ? 0f : (float)((now - _s.LastTicks) / (double)Stopwatch.Frequency);
        _s.LastTicks = now;
        return Math.Clamp(dt, 0f, 0.1f);
    }

    /// <summary>Frame <paramref name="f"/>'s sample point inside its froxel: the R2 sequence across, the golden ratio's
    /// along the slice -- low-discrepancy, so a few frames already cover the froxel evenly.</summary>
    private static Vector3 Jitter(long f)
    {
        double x = 0.5 + f * 0.7548776662466927, y = 0.5 + f * 0.5698402909980532, z = 0.5 + f * 0.6180339887498949;
        return new Vector3((float)(x - Math.Floor(x)) - 0.5f, (float)(y - Math.Floor(y)) - 0.5f, (float)(z - Math.Floor(z)));
    }

    /// <summary>The LINEAR light fully fogged geometry converges to, averaged over view directions, while this frame's
    /// volume is built: the area's fog colour as the medium scatters it.</summary>
    internal static bool Horizon(out Vector3 lin)
    {
        lin = _s.LastAmbient;
        return _gl.Built;
    }

    /// <summary>Push this frame's volume at one program (its <c>uVFog</c> and <c>uVFogVp</c>), or stand it down
    /// (<c>uVFog.x</c> 0) where none was built or <paramref name="capture"/> says the pass is another camera's.</summary>
    internal static void Upload(int vfog, int vfogVp, bool capture)
    {
        bool on = _gl.Built && !capture;
        Gl.Uniform4f(vfog, on ? 1f : 0f, _gl.D, Near, _gl.InvLog);
        if (on) fixed (Matrix4x4* m = &_gl.Vp) Gl.UniformMatrix4fv(vfogVp, 1, 0, (float*)m);
    }

    /// <summary>Build both programs on the first frame that wants them, once; a failure is loud and latched, and the ramp
    /// keeps drawing the fog. The shadow libraries' samplers take the units the frame binds the arrays on, and both
    /// receivers are told each sample is a point in a volume, not a surface to lift off.</summary>
    private static bool Ready()
    {
        if (_gl.ProgLight != 0) return true;
        if (_down.Failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindImageTexture == null || Gl.TextureStorage3D == null
            || Gl.BindTextureUnit == null)
            return _down.Fail(_s.Log, "compute, image or 3D texture entry points are missing", "the fog stays the ramp for the session");
        uint light = GlProgram.Compute(ShdLightCs, ShaderStages.Source(ShdLightCs), _s.Log, "FogVolume light");
        uint sum = light == 0 ? 0 : GlProgram.Compute(ShdSumCs, ShaderStages.Source(ShdSumCs), _s.Log, "FogVolume integrate");
        if (light == 0 || sum == 0)
        {
            _s.Log?.Always("FogVolume: a volumetric fog program did not build (the driver's own log is above) -- vfog.enabled "
                         + "has no effect this session and the fog group's ramp draws the fog");
            return _down.Fail(_s.Log, "a compute program did not link", "the fog stays the ramp for the session");
        }
        _gl.ProgLight = light; _gl.ProgSum = sum;
        _gl.L.Grid = TakeGrid(light);
        _gl.L.Height = GlProgram.Loc(light, "uVfHeight");
        _gl.L.Noise = GlProgram.Loc(light, "uVfNoise");
        _gl.L.Drift = GlProgram.Loc(light, "uVfDrift");
        _gl.L.Ambient = GlProgram.Loc(light, "uVfAmbient");
        _gl.L.Sun = GlProgram.Loc(light, "uVfSun");
        _gl.L.Local = GlProgram.Loc(light, "uVfLocal");
        _gl.L.Phase = GlProgram.Loc(light, "uVfPhase");
        _gl.L.Jitter = GlProgram.Loc(light, "uVfJitter");
        _gl.L.PrevVp = GlProgram.Loc(light, "uVfPrevVp");
        _gl.L.PrevZ = GlProgram.Loc(light, "uVfPrevZ");
        _gl.L.Cubes = GlProgram.Loc(light, "uLocalCubes");
        _gl.S = TakeGrid(sum);
        Gl.UseProgram(light);
        Gl.Uniform1i(GlProgram.Loc(light, "uSunShadow"), (int)TextureSlots.SunShadow);
        Gl.Uniform1i(GlProgram.Loc(light, "uSunDynShadow"), (int)TextureSlots.SunShadowDyn);
        Gl.Uniform1i(GlProgram.Loc(light, "uLocalShadow"), (int)TextureSlots.LocalShadow);
        Gl.Uniform1i(GlProgram.Loc(light, "uLocalDynShadow"), (int)TextureSlots.LocalShadowDyn);
        Gl.Uniform1i(GlProgram.Loc(light, "uVfHist"), (int)TextureSlots.VFog);
        Gl.Uniform1f(GlProgram.Loc(light, "uSunMedia"), 1f);
        Gl.Uniform1f(GlProgram.Loc(light, "uLocalMedia"), 1f);
        Gl.UseProgram(0);
        _s.Log?.Info("FogVolume: ready -- two compute passes over the main view's froxel grid");
        return true;
    }

    private static GridLocs TakeGrid(uint prog) => new()
    {
        Size = GlProgram.Loc(prog, "uVfGrid"), Z = GlProgram.Loc(prog, "uVfZ"), InvVp = GlProgram.Loc(prog, "uVfInvVp"),
        Eye = GlProgram.Loc(prog, "uVfEye"), Fwd = GlProgram.Loc(prog, "uVfFwd"), Med = GlProgram.Loc(prog, "uVfMed"),
        Origin = GlProgram.Loc(prog, "uVfOrigin"), Top = GlProgram.Loc(prog, "uVfTop"),
    };

    /// <summary>The three volumes at this grid; a new grid is new storage and starts without history.</summary>
    private static void Ensure(int w, int h, int d)
    {
        if (_gl.Sum != 0 && _gl.W == w && _gl.H == h && _gl.D == d) return;
        Release();
        _gl.MedA = Volume(w, h, d); _gl.MedB = Volume(w, h, d); _gl.Sum = Volume(w, h, d);
        _gl.W = w; _gl.H = h; _gl.D = d;
        _s.Log?.Info($"FogVolume: {w}x{h}x{d} froxels ({3L * w * h * d * 8 >> 20} MiB)");
    }

    private static uint Volume(int w, int h, int d)
    {
        uint t;
        Gl.CreateTextures(Gl.TEXTURE_3D, 1, &t);
        Gl.TextureStorage3D(t, 1, Gl.RGBA16F, w, h, d);
        RenderTarget.Clamp(t, Gl.LINEAR);
        Gl.TextureParameteri(t, Gl.TEXTURE_WRAP_R, Gl.CLAMP_TO_EDGE);
        return t;
    }

    /// <summary>Delete the volumes (the mode switched off, or a new grid); the programs stay.</summary>
    private static void Release()
    {
        if (_gl.Sum == 0) return;
        GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, _gl.MedA);
        GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, _gl.MedB);
        GpuGraveyard.Retire(GpuGraveyard.Kind.Texture, _gl.Sum);
        _gl.MedA = _gl.MedB = _gl.Sum = 0;
        _gl.W = _gl.H = _gl.D = 0;
        _gl.History = false;
    }

    /// <summary>A new area: last frame's medium is another place's air.</summary>
    internal static void Revoke() => _gl.History = false;

    /// <summary>Context loss: every name went with it.</summary>
    internal static void Forget()
    {
        _gl = new FogVolumeGlState();
        _down.Forget();
    }

    /// <summary>The period line's clause: nothing while the mode is off.</summary>
    internal static string Stats(int frames)
    {
        string s = !VolumetricFog.Enabled.V ? "" : $"vfog {_gl.W}x{_gl.H}x{_gl.D} ({_s.Runs} builds in {frames} frames"
                 + (_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "") + ")";
        _s.Runs = 0;
        return s;
    }

    /// <summary>Off-device: the medium's opacity budget, the far distance it implies, both colours, the grid ladder, the
    /// server's hold and SceneFog's arms. Oracle: the 1% target integrated numerically over the wall's smoothstep ramp
    /// (<see cref="WallDepth"/>), figures hand-worked off ln 100 = 4.60517 and the 2.2 curve at 0.5 (0.217638), and the
    /// ladder's one promise, that more effects quality never means fewer froxels.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Medium m = MediumFor(true, 15f, 60f, 0f, 0.12f);
        check.Expect(MathF.Abs(m.Haze * 60f - 0.552620f) < 1e-4f, "the haze takes its share of ln 100 over the fog's end distance");
        check.Expect(MathF.Abs(MathF.Exp(-RadialDepth(m, 60f)) - 0.01f) < 2e-4f, "a ray out from the player keeps 1% at the area's fog end");
        // the toolset default: fog amount 0 puts the start 1.01 m short of the clip
        Medium thin = MediumFor(true, 43.99f, 45f, 0f, 0.12f);
        check.Expect(float.IsFinite(thin.Wall) && MathF.Abs(MathF.Exp(-RadialDepth(thin, 45f)) - 0.01f) < 2e-4f, "...and so does a ramp a metre thin");
        Medium bad = MediumFor(true, 50f, 45f, 0f, 0.12f);
        check.Expect(bad.Start < bad.End && float.IsFinite(bad.Wall), "a start past the end still leaves a ramp to cross");
        Medium air = MediumFor(false, 0f, 0f, 300f, 0.12f);
        check.Expect(air.Wall == 0f && MathF.Abs(air.Haze - 0.00184207f) < 1e-7f, "no area fog: the haze alone, spread over the far distance");
        check.Expect(MathF.Abs(MediumFor(false, 0f, 0f, 20f, 0.12f).Haze - 0.00368413f) < 1e-7f, "...but never over less than 150 m");
        check.Expect(MediumFor(true, 15f, 60f, 0f, 1f).Wall > 0f, "the haze never takes the whole budget: the wall always stands");

        float opaque = OpaqueDistance(true, 15f, 60f, 0.12f);
        // the wall ALONE spends ln 100 at e + h * tau / wall = 60 + 0.552620 / 0.180113
        check.Expect(MathF.Abs(opaque - 63.068f) < 0.01f, $"the opaque distance is where the wall alone keeps 1% ({opaque:F3})");
        check.Expect(MathF.Abs(MathF.Exp(-WallDepth(m, opaque)) - 0.01f) < 2e-4f, "...which the wall's own ramp, integrated, agrees with");
        check.Expect(float.IsPositiveInfinity(OpaqueDistance(false, 15f, 60f, 0.12f)), "no area fog, no opaque distance");

        Vector3 red = new(1f, 0f, 0f), half = new(0.5f);
        Vector3 a0 = Ambient(true, half, true, red, new Vector3(0.3f), 0f);
        check.Expect(MathF.Abs(a0.X - 0.217638f) < 1e-4f && MathF.Abs(a0.Z - 0.217638f) < 1e-4f, "sky 0: the fog colour, linearised");
        check.Expect(Ambient(true, half, true, red, default, 1f) == red, "sky 1: the sky's average");
        check.Expect(MathF.Abs(Ambient(true, half, false, red, default, 1f).Y - 0.217638f) < 1e-4f, "no sky read: the fog colour whatever sky says");
        check.Expect(Ambient(false, half, false, default, new Vector3(0.3f), 1f) == new Vector3(0.3f), "no area fog and no sky: the area ambient");

        float sunWas = Lighting.Sun.V;
        Lighting.Sun.V = 1f;
        // luminance of (0.8, 0.6, 0.4) is 0.62808: half of it is the share, and hue times share is the colour at half
        Vector4 sh = SunShare(false, false, true, new Vector3(0.8f, 0.6f, 0.4f), 1f, 0.5f);
        check.Expect(MathF.Abs(sh.W - 0.31404f) < 1e-4f && MathF.Abs(sh.X - 0.4f) < 1e-4f && MathF.Abs(sh.Z - 0.2f) < 1e-4f,
                     $"by day a sun under full luminance moves its luminance's share of the fog into the beam ({sh})");
        Vector4 bright = SunShare(false, false, true, new Vector3(2f), 1f, 0.5f);
        check.Expect(MathF.Abs(bright.W - 0.5f) < 1e-5f && MathF.Abs(bright.X - 0.5f) < 1e-5f,
                     "a sun past full luminance moves the whole strength, in its own hue");
        check.Expect(SunShare(false, false, true, new Vector3(0.8f), 0f, 0.5f) == Vector4.Zero, "the moon lights no air");
        check.Expect(SunShare(true, false, true, new Vector3(0.8f), 1f, 0.5f) == Vector4.Zero
                     && SunShare(false, true, true, new Vector3(0.8f), 1f, 0.5f) == Vector4.Zero, "nor does anything indoors or under blind lights");
        Lighting.Sun.V = sunWas;

        check.Expect(VolumetricFog.Grids.Length == VolumetricFog.Levels.Length, "every detail level names a grid");
        long cost = 0;
        bool rising = true;
        for (int i = 0; i < Quality.Levels.Length; i++)
        {
            (int gw, int gh, int gd) = VolumetricFog.Grids[Math.Max(Array.IndexOf(VolumetricFog.Levels, VolumetricFog.Quality.RungValue(i)), 0)];
            rising &= Array.IndexOf(VolumetricFog.Levels, VolumetricFog.Quality.RungValue(i)) >= 0 && (long)gw * gh * gd >= cost;
            cost = (long)gw * gh * gd;
        }
        check.Expect(rising, "raising the effects area never makes the fog volume coarser");

        bool enWas = VolumetricFog.Enabled.V;
        check.Expect(ServerPolicy.Set("vfog.enabled", enWas ? "0" : "1") && VolumetricFog.Enabled.Enforced && VolumetricFog.Enabled.V != enWas,
                     "a server may hold vfog.enabled");
        check.Expect(ServerPolicy.Set("vfog.haze", "0.3") && VolumetricFog.Haze.Enforced && VolumetricFog.Haze.V == 0.3f, "...and its dials");
        ServerPolicy.Set("vfog.*", "");
        check.Expect(!VolumetricFog.Enabled.Enforced && !VolumetricFog.Haze.Enforced && VolumetricFog.Enabled.V == enWas,
                     "releasing hands both back to the player");

        bool fogWas = Fog.Enabled.V, originWas = Fog.Origin.V;
        Fog.Enabled.V = false; Fog.Origin.V = false;
        VolumetricFog.Enabled.V = true;
        check.Expect(SceneFog.SkyBand == 0.15f && SceneFog.OriginPlayer,
                     "on: a capture's sky keeps a band with the fog group off, and the fog is measured from the player");
        VolumetricFog.Enabled.V = false;
        check.Expect(SceneFog.SkyBand == 0f && !SceneFog.OriginPlayer, "off: both are the fog group's again");
        Fog.Enabled.V = fogWas; Fog.Origin.V = originWas; VolumetricFog.Enabled.V = enWas;
        log.Info(check.Ok ? "FogVolume.SelfCheck: passed" : "FogVolume.SelfCheck: FAILED");
    }

    /// <summary>Optical depth on a ray straight out from the player to <paramref name="d"/> metres, haze at its floor.</summary>
    private static float RadialDepth(Medium m, float d) => m.Haze * d + WallDepth(m, d);

    /// <summary>The wall's share of it: the documented shape, a smoothstep from start to end, summed every centimetre.</summary>
    private static float WallDepth(Medium m, float d)
    {
        const double step = 0.01;
        double tau = 0;
        for (double r = step * 0.5; r < d; r += step)
        {
            double t = Math.Clamp((r - m.Start) / (m.End - m.Start), 0, 1);
            tau += m.Wall * t * t * (3 - 2 * t) * step;
        }
        return (float)tau;
    }

    internal static string Describe()
    {
        if (!VolumetricFog.Enabled.V) return "OFF -- the fog group draws the fog";
        if (_down.Failed) return "FAILED -- " + _down.Reason + "; the fog group draws the fog";
        Medium m = _s.Last;
        float opaque = OpaqueDistance(FrameInputs.FogOn, FrameInputs.FogStart, FrameInputs.FogEnd, VolumetricFog.Haze.V);
        return $"{_gl.W}x{_gl.H}x{_gl.D}  haze {m.Haze:F4}/m  wall {m.Wall:F3}/m over {m.Start:F0}..{m.End:F0} m, bank top {VolumetricFog.Top.V:F0} m  "
             + $"[opaque {(float.IsInfinity(opaque) ? "--" : opaque.ToString("F0"))} m]  "
             + $"ambient {_s.LastAmbient.X:F2},{_s.LastAmbient.Y:F2},{_s.LastAmbient.Z:F2}  "
             + $"sun share {_s.LastSun.W:F2} hue {_s.LastSun.X:F2},{_s.LastSun.Y:F2},{_s.LastSun.Z:F2} (day {_s.Day:F2})";
    }
}
