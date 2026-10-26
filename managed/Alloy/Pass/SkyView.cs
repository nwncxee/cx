using System;
using System.Globalization;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Session-lifetime state of the sky-visibility term: the log, the bake census, and the reason it
/// is standing down if it is.</summary>
internal sealed class SkyViewState
{
    public ExtenderLog? Log;
    public int Bakes;
    public bool SaidNoUnit;
    /// <summary>Why the term cannot run at all this session, or null. Named in the log once and on the
    /// period line, never left silent.</summary>
    public string? Down;
    /// <summary>The fit the last log line named, so a re-bake of the same area says nothing.</summary>
    public Matrix4x4 SaidFit;
}

/// <summary>The GL half, replaced on context loss.</summary>
internal sealed class SkyGlState
{
    /// <summary>The baked map: R the column's top surface in world metres, G the sky share it sees.</summary>
    public uint MapTex;
    public int MapSize;
    public uint Prog;
    public int LocDepth = -1, LocSize = -1, LocZ = -1, LocFit = -1;
    public bool Failed, Valid;
    /// <summary>The lit program was built without <c>CX_SKY_VIEW</c> -- this driver cannot seat the map's
    /// texture unit, so the term stands down for the context.</summary>
    public bool NoUnit;
}

/// <summary>The sky-visibility term. A vanilla area has ONE ambient for the whole of it, so an alley at noon
/// is as bright as the street, and neither sun shadows nor contact-scale AO cover that. This bakes the
/// overhead depth view <see cref="CasterPass"/> renders into one small map -- top surface and sky share per
/// column -- which the lit stage scales the area ambient by, one tap. No content, every exterior.</summary>
internal static unsafe class SkyView
{
    /// <summary>The map's side, in texels. One size for every area: the fit is the area's own extent, so a
    /// 320 m area resolves to 0.31 m and a small one far finer. 1024^2 RG16F is 4 MiB, beside the overhead
    /// depth layer's 4 MiB.</summary>
    internal const int MapSize = 1024;
    /// <summary>The bake dispatch's work-group side, pinned against <c>cx_skybake</c>'s own
    /// <c>local_size</c> by <see cref="SelfCheck"/>.</summary>
    internal const int Group = 8;

    /// <summary>How far off the surface the lookup steps, along the surface normal: far enough that a wall
    /// face asks about the ground beside it rather than its own roofline.</summary>
    internal const float StepM = 0.75f;
    /// <summary>Metres under a column's top surface at which a point counts as fully covered.</summary>
    internal const float CoverM = 3f;
    /// <summary>Metres above it at which a point counts as seeing the open sky -- high on an alley wall
    /// there IS more sky than on the floor.</summary>
    internal const float RiseM = 12f;

    private static SkyViewState _s = new();
    private static SkyGlState _gl = new();

    /// <summary>The term is wanted this frame: a strength above zero, a feed to read and a driver that can
    /// seat the map.</summary>
    internal static bool Wanted => Lighting.Sky.V > 0f && _s.Down == null && !_gl.Failed && !_gl.NoUnit;

    /// <summary>The overhead view rides the caster pass, so the shadow feed has to reach it: with
    /// <c>shadow.enabled=0</c> or a <c>shadow.stage</c> short of the bake there is nothing to read, and
    /// saying so here is the whole stand-down -- the knob's help names the dependency too.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _s.Log = log;
        if (!Shadows.Enabled.Boot)
            _s.Down = "shadow.enabled=0, so the caster pass never runs and there is no overhead view to bake";
        else if (Shadows.Stage.Boot < 4)
            _s.Down = $"shadow.stage={Shadows.Stage.Boot} stops the feed before the bake, so there is no overhead view";
        if (_s.Down != null)
            log.Always($"SkyView: {_s.Down} -- the sky-visibility term is OFF and the area ambient fills an "
                   + "alley as vanilla does (turn Shadows on to get it back)");
    }

    /// <summary>The lit program came out without <c>CX_SKY_VIEW</c>: nothing declares the map, so the term
    /// stands down rather than uploading to a location that does not exist.</summary>
    internal static void NoTextureUnit()
    {
        _gl.NoUnit = true;
        if (_s.SaidNoUnit) return;
        _s.SaidNoUnit = true;
        _s.Log?.Always("SkyView: the lit stage has no texture unit for the sky map on this driver -- the "
                   + "sky-visibility term is off, and the area ambient fills an alley as vanilla does");
    }

    /// <summary>The overhead depth view landed: turn it into the map the lit stage reads. One dispatch over
    /// <see cref="MapSize"/>^2 texels, each walking <see cref="SkyMath.Dirs"/> x <see cref="SkyMath.Steps"/>
    /// taps of the depth -- the work the lit stage would otherwise redo per pixel per frame for an area that
    /// has not changed (the weather shelter cache's own reason).</summary>
    internal static void Bake(uint depthTex, in SkyFit fit)
    {
        _gl.Valid = false;
        if (depthTex == 0 || fit.IsNone || !Ensure()) return;
        Gl.UseProgram(_gl.Prog);
        Gl.BindImageTexture(0, _gl.MapTex, 0, 0, 0, Gl.WRITE_ONLY, Gl.RG16F);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, depthTex);
        Gl.Uniform1i(_gl.LocDepth, 0);
        Gl.Uniform2i(_gl.LocSize, MapSize, MapSize);
        Gl.Uniform2f(_gl.LocZ, fit.ZAt0, fit.ZPerDepth);
        Gl.Uniform2f(_gl.LocFit, fit.UvPerMetre, SkyMath.RangeM);
        uint g = (uint)((MapSize + Group - 1) / Group);
        Gl.DispatchCompute(g, g, 1);
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);
        Gl.UseProgram(0);
        _gl.Valid = true;
        Baked(in fit);
    }

    /// <summary>A bake landed, on either backend: counted, and the fit named once per area.</summary>
    internal static void Baked(in SkyFit fit)
    {
        _s.Bakes++;
        if (_s.SaidFit.Equals(fit.Vp)) return;
        _s.SaidFit = fit.Vp;
        _s.Log?.Info($"SkyView: baked {MapSize}^2 over a {fit.Radius * 2f:F0} m fit at "
                   + $"({fit.Centre.X:F0}, {fit.Centre.Y:F0}), {fit.Radius * 2f / MapSize:F2} m a texel, "
                   + $"heights {fit.ZAt0 + fit.ZPerDepth:F0}..{fit.ZAt0:F0} m -- an area taller or deeper "
                   + $"than that clamps; strength {Lighting.Sky.V:F2}, reach {SkyMath.RangeM:F0} m");
    }

    /// <summary>Per colour program bind: the overhead fit, the look terms and the map on its unit. A strength
    /// of 0 is how every stand-down reaches the stage -- interior, another area, no bake yet, the knob off.
    /// <paramref name="capturing"/> excludes the probe capture, which is what keeps the term applied ONCE.
    /// </summary>
    internal static void UploadLit(int locVp, int locTerm, bool capturing)
    {
        // a Vulkan stage's host borrows this setter inside a capture; the binder puts the map on uSkyMap
        if (Renderer.OnVulkan) { UploadLitVk(locVp, locTerm, AlloyRenderer.VkSkyMap, capturing); return; }
        bool on = Wanted && _gl.Valid && CasterPass.SkyFresh && !capturing;
        if (locVp >= 0 || locTerm >= 0)
        {
            Matrix4x4 vp = on ? CasterPass.SkyMapFit.Vp : default;
            if (locVp >= 0) Gl.UniformMatrix4fv(locVp, 1, 0, (float*)&vp);
            if (locTerm >= 0) Gl.Uniform4f(locTerm, on ? Lighting.Sky.V : 0f, StepM, CoverM, RiseM);
        }
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.SkyMap);
        Gl.BindTexture(Gl.TEXTURE_2D, on ? _gl.MapTex : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary><see cref="UploadLit"/> on Vulkan: the same two terms into the captured block, the map bound by the
    /// caller where the stage declares it. <paramref name="map"/> is the null id until a bake landed.</summary>
    internal static void UploadLitVk(int locVp, int locTerm, ImageId map, bool capturing)
    {
        bool on = Wanted && !map.IsNull && CasterPass.SkyFresh && !capturing;
        VkUniformShim.Set(locVp, on ? CasterPass.SkyMapFit.Vp : default);
        VkUniformShim.Set(locTerm, [on ? Lighting.Sky.V : 0f, StepM, CoverM, RiseM]);
    }

    /// <summary>The baked map describes an area that has gone; <see cref="CasterPass.SkyAreaEdge"/> drops the
    /// key it was rendered under, so the next exterior frame bakes again.</summary>
    internal static void Revoke()
    {
        _gl.Valid = false;
        AlloyRenderer.VkSkyRevoke();
    }

    internal static void Forget() => _gl = new SkyGlState();

    internal static string Stats()
        => _s.Down != null ? $"skyview OFF -- {_s.Down}"
         : _gl.NoUnit ? "skyview OFF -- no fragment texture unit for the map on this driver"
         : _gl.Failed ? "skyview stood down -- see the SkyView line above"
         : Lighting.Sky.V <= 0f ? "skyview off (light sky 0)"
         : $"skyview {(_gl.Valid || !AlloyRenderer.VkSkyMap.IsNull ? $"{MapSize}^2" : "no map yet")} strength {Lighting.Sky.V:F2} reach "
         + $"{SkyMath.RangeM:F0}m, {_s.Bakes} baked, {CasterPass.SkyStats()}";

    private static bool Ensure()
    {
        if (_gl.Failed) return false;
        if (_gl.Prog == 0)
        {
            if (Gl.DispatchCompute == null || Gl.BindImageTexture == null || Gl.MemoryBarrier == null)
            { Fail("this context has no compute entry points"); return false; }
            _gl.Prog = GlProgram.Compute(ShaderSource.Get(Shd + ".glsl"), _s.Log, "SkyView bake");
            if (_gl.Prog == 0) { Fail("the bake program did not build"); return false; }
            _gl.LocDepth = GlProgram.Loc(_gl.Prog, "uSkyDepth");
            _gl.LocSize = GlProgram.Loc(_gl.Prog, "uSize");
            _gl.LocZ = GlProgram.Loc(_gl.Prog, "uSkyZ");
            _gl.LocFit = GlProgram.Loc(_gl.Prog, "uSkyFit");
        }
        if (_gl.MapTex == 0 || _gl.MapSize != MapSize)
        {
            RenderTarget.Create(ref _gl.MapTex, Gl.RG16F, MapSize, MapSize, Gl.LINEAR);
            _gl.MapSize = MapSize;
            _s.Log?.Info($"SkyView: sky-visibility map {MapSize}x{MapSize} RG16F, fit to the area's own extent");
        }
        return true;
    }

    /// <summary>The caster pass could not allocate the overhead depth view: nothing to bake from.</summary>
    internal static void NoDepthTarget(int size) => Fail($"a {size}x{size} depth view could not be allocated");

    private static void Fail(string why)
    {
        _gl.Failed = true;
        _gl.Prog = 0;
        _s.Log?.Always($"SkyView: {why} -- the sky-visibility term is off, and the area ambient fills an "
                   + "alley as vanilla does");
    }

    internal const string Shd = "cx_skybake";

    internal static string BakeSrc => ShaderSource.Get(Shd + ".glsl");
    internal static string Lib => ShaderSource.Get("cx_skyview.glsl");

    /// <summary>The number written straight after <paramref name="before"/> in <paramref name="src"/>, or
    /// NaN. Reads a GLSL constant as a VALUE, so the C# side can be held to it rather than to its spelling.
    /// </summary>
    private static float GlslFloat(string src, string before)
    {
        int at = src.IndexOf(before, StringComparison.Ordinal);
        if (at < 0) return float.NaN;
        at += before.Length;
        int end = at;
        while (end < src.Length && (char.IsAsciiDigit(src[end]) || src[end] == '.')) end++;
        return float.TryParse(src.AsSpan(at, end - at), NumberStyles.Float, CultureInfo.InvariantCulture,
                              out float v) ? v : float.NaN;
    }

    /// <summary>Oracles: the EMBEDDED GLSL of the bake and the lit library -- the ladder, the azimuth step as
    /// a value, the horizon term and the resolve, each of which <see cref="SkyMath"/> is independently
    /// checked against -- plus the fragment-side values worked out by hand off
    /// <see cref="SkyMath.Term"/>'s definition.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string bake = BakeSrc, lib = Lib;

        // --- the bake carries the same ladder the pure reference is checked against
        check.Expect(bake.Contains($"const int CX_SKY_DIRS = {SkyMath.Dirs};")
                  && bake.Contains($"const int CX_SKY_STEPS = {SkyMath.Steps};")
                  && bake.Contains("const float CX_SKY_NEAR_M = "
                                   + SkyMath.NearM.ToString("0.0", CultureInfo.InvariantCulture) + ";"),
                     "the bake's sweep ladder is SkyMath's");
        // the azimuth step as a VALUE, not a spelling: a truncated tau would skew every direction
        check.Expect(MathF.Abs(GlslFloat(bake, "float a = float(j) * (") - MathF.Tau) < 1e-6f,
                     $"the bake's azimuth step is a full turn ({MathF.Tau:F7}) over CX_SKY_DIRS");
        check.Expect(bake.Contains("float r = CX_SKY_NEAR_M * pow(max(uSkyFit.y, CX_SKY_NEAR_M) / CX_SKY_NEAR_M, "
                                 + "float(k) / float(CX_SKY_STEPS - 1));"),
                     "...and its radius ladder is SkyMath.Radius' geometric one, near to reach");
        check.Expect(bake.Contains($"layout(local_size_x = {Group}, local_size_y = {Group}) in;"),
                     $"the bake's work group is {Group} wide, which is what the dispatch counts in");
        check.Expect(bake.Contains("open += 1.0 / (1.0 + tmax * tmax);"),
                     "...and its horizon estimate is SkyMath.Openness' cos^2 term");
        check.Expect(bake.Contains("uniform sampler2DArray uSkyDepth;") && bake.Contains("texelFetch(uSkyDepth,"),
                     "the bake reads a STORED height off the depth view, not a compare verdict");
        check.Expect(bake.Contains("d >= 1.0 ? CX_SKY_VOID_M") && bake.Contains("imageStore(uOut, t, vec4(CX_SKY_VOID_M, 1.0,"),
                     "a column no caster covered is stored as open, far below the world: neither a fragment over it nor a sweep into it reads cover");

        // --- the lit library's resolve mirrors SkyMath.Term, term for term, in the order UploadLit sends
        check.Expect(lib.Contains("uniform vec4 uSkyTerm;") && lib.Contains("vec3 probe = P + N * uSkyTerm.y;")
                  && lib.Contains("float open = mix(m.y, 1.0, clamp((dz - uSkyTerm.y) / max(uSkyTerm.w, 1e-3), 0.0, 1.0));")
                  && lib.Contains("open *= 1.0 - clamp(-dz / max(uSkyTerm.z, 1e-3), 0.0, 1.0);")
                  && lib.Contains("return 1.0 - uSkyTerm.x * (1.0 - open);"),
                     "the lit library's resolve is SkyMath.Term's");
        check.Expect(lib.Contains("if (uSkyTerm.x <= 0.0) return 1.0;"),
                     "a zero strength is how every stand-down reaches the stage");
        // Spatial only, so it holds with TAA off: nothing here is dithered or frame-rotated.
        check.Expect(!lib.Contains("uFrame") && !bake.Contains("uFrame") && !bake.Contains("fract("),
                     "neither stage dithers or rotates per frame -- the term must look right with TAA off");

        // --- the stages that apply it, and the normal each applies it with
        check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("* ambShape * occl * CxSkyView(vWorld, normalize(vNrm));"),
                     "the lit stage folds it into the AMBIENT term alone, off the geometric normal");
        // the heads come back SPLICED, so the library's own text is what proves it reached the stage
        check.Expect(AlloyRenderer.OwnedLitFsHead.Contains("float CxSkyView(vec3 P, vec3 N)")
                  && GrassField.GrassFsHead.Contains("float CxSkyView(vec3 P, vec3 N)")
                  && GrassField.BladeFsHead.Contains("float CxSkyView(vec3 P, vec3 N)"),
                     "...and the lit, grass-card and blade heads all carry the library they call");
        check.Expect(ShaderSource.Get("cx_grass_fmain.glsl").Contains("uAreaAmbient * uMatAmbient * CxSkyView(vWorld, CX_SKY_UP)")
                  && ShaderSource.Get("cx_gblade_fmain.glsl").Contains("uAreaAmbient * vMatAmb * CxSkyView(vWorld, CX_SKY_UP)"),
                     "ground cover takes the same term off the WORLD UP -- a blade's own normal is not where its sky is");
        check.Expect(lib.Contains("#define CX_SKY_UP vec3(0.0, 0.0, 1.0)"),
                     "...and that up vector is the library's, not a third spelling");

        // --- the fragment-side resolve, against the hand-worked values
        check.Expect(MathF.Abs(SkyMath.Term(StepM, 0.428846f, 0.85f, StepM, CoverM, RiseM) - 0.514519f) < 1e-4f,
                     "level ground at strength 0.85 over a 0.428846 column reads 0.514519");
        check.Expect(MathF.Abs(SkyMath.Term(-4f, 1f, 0.85f, StepM, CoverM, RiseM) - 0.15f) < 1e-4f,
                     "4 m under a roof reads the strength's floor, 0.15");
        check.Expect(MathF.Abs(SkyMath.Term(StepM + RiseM, 0.4f, 0.85f, StepM, CoverM, RiseM) - 1f) < 1e-4f,
                     "a point a rise band above the column's top surface reads the open sky");
        check.Expect(MathF.Abs(SkyMath.Term(-4f, 0.3f, 0f, StepM, CoverM, RiseM) - 1f) < 1e-6f,
                     "strength 0 is the flat vanilla fill back, everywhere");

        // --- it ships ON and the player can switch it off; the probe capture is excluded, so it lands once
        check.Expect(Lighting.Sky.Default > 0f && Lighting.Sky.Min == 0f,
                     "the term ships on and the knob reaches 0 -- it is a look change on vanilla content");
        check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("CxAmbientShape(N)"),
                     "the ambient's shape comes off the PROBE, which is why the capture must not take the term too");

        log.Info(check.Ok ? "SkyView.SelfCheck: passed" : "SkyView.SelfCheck: FAILED");
    }
}
