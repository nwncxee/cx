using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Session-lifetime state of the sky-visibility term: the log and the bake census.</summary>
internal sealed class SkyViewState
{
    public ExtenderLog? Log;
    public int Bakes;
    public bool SaidNoUnit;
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
    private const int Group = 8;

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

    /// <summary>The term is wanted this frame: a strength above zero and a driver that can seat the map.</summary>
    internal static bool Wanted => Lighting.Sky.V > 0f && !_gl.Failed && !_gl.NoUnit;

    internal static void Attach(ExtenderLog log) => _s.Log = log;

    /// <summary>The lit program came out without <c>CX_SKY_VIEW</c>: nothing declares the map, so the term
    /// stands down rather than uploading to a location that does not exist.</summary>
    internal static void NoTextureUnit()
    {
        _gl.NoUnit = true;
        if (_s.SaidNoUnit) return;
        _s.SaidNoUnit = true;
        _s.Log?.Info("SkyView: the lit stage has no texture unit for the sky map on this driver -- the "
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
        Gl.Uniform2f(_gl.LocFit, fit.UvPerMetre, MathF.Max(Lighting.SkyRange.V, SkyMath.NearM));
        uint g = (uint)((MapSize + Group - 1) / Group);
        Gl.DispatchCompute(g, g, 1);
        Gl.MemoryBarrier(Gl.SHADER_IMAGE_ACCESS_BARRIER_BIT | Gl.TEXTURE_FETCH_BARRIER_BIT);
        Gl.BindTexture(Gl.TEXTURE_2D_ARRAY, 0);
        Gl.UseProgram(0);
        _gl.Valid = true;
        _s.Bakes++;
        if (_s.SaidFit.Equals(fit.Vp)) return;
        _s.SaidFit = fit.Vp;
        _s.Log?.Info($"SkyView: baked {MapSize}^2 over a {fit.Radius * 2f:F0} m fit at "
                   + $"({fit.Centre.X:F0}, {fit.Centre.Y:F0}), {fit.Radius * 2f / MapSize:F2} m a texel, "
                   + $"heights {fit.ZAt0 + fit.ZPerDepth:F0}..{fit.ZAt0:F0} m -- an area taller or deeper "
                   + $"than that clamps; strength {Lighting.Sky.V:F2}, reach {Lighting.SkyRange.V:F0} m");
    }

    /// <summary>Per lit program bind: the overhead fit, the look terms and the map on its unit. A strength
    /// of 0 is how every stand-down reaches the stage -- interior, no bake yet, the knob off.</summary>
    internal static void UploadLit(int locVp, int locTerm)
    {
        if (locVp < 0 && locTerm < 0) return;
        bool on = Wanted && _gl.Valid && CasterPass.SkyFresh;
        Matrix4x4 vp = on ? CasterPass.SkyMapFit.Vp : default;
        if (locVp >= 0) Gl.UniformMatrix4fv(locVp, 1, 0, (float*)&vp);
        if (locTerm >= 0) Gl.Uniform4f(locTerm, on ? Lighting.Sky.V : 0f, StepM, CoverM, RiseM);
        Gl.ActiveTexture(Gl.TEXTURE0 + TextureSlots.SkyMap);
        Gl.BindTexture(Gl.TEXTURE_2D, on ? _gl.MapTex : 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
    }

    /// <summary>Area edge: the baked map describes an area that has gone. The depth view it came from is
    /// released with the caster set, and the next exterior frame bakes again.</summary>
    internal static void Revoke() => _gl.Valid = false;

    internal static void Forget() => _gl = new SkyGlState();

    internal static string Stats()
        => Lighting.Sky.V <= 0f ? "skyview off (light sky 0)"
         : _gl.NoUnit ? "skyview off -- no texture unit on this driver"
         : _gl.Failed ? "skyview stood down"
         : $"skyview {(_gl.Valid ? $"{MapSize}^2" : "no map yet")} strength {Lighting.Sky.V:F2} reach "
         + $"{Lighting.SkyRange.V:F0}m, {_s.Bakes} baked, {CasterPass.SkyStats()}";

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
        _s.Log?.Info($"SkyView: {why} -- the sky-visibility term is off, and the area ambient fills an "
                   + "alley as vanilla does");
    }

    internal const string Shd = "cx_skybake";

    internal static string BakeSrc => ShaderSource.Get(Shd + ".glsl");
    internal static string Lib => ShaderSource.Get("cx_skyview.glsl");

    /// <summary>Oracles: the EMBEDDED GLSL of the two stages (the bake must carry the same sweep ladder
    /// <see cref="SkyMath"/> is checked against, and the lit library the same resolve), plus the
    /// fragment-side values worked out by hand -- level ground passes the baked share through, 4 m under a
    /// roof reads the strength's floor, well above a column's top reads the open sky.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        string bake = BakeSrc, lib = Lib;

        // --- the bake carries the same ladder the pure reference is checked against
        check.Expect(bake.Contains($"const int CX_SKY_DIRS = {SkyMath.Dirs};")
                  && bake.Contains($"const int CX_SKY_STEPS = {SkyMath.Steps};")
                  && bake.Contains("const float CX_SKY_NEAR_M = "
                                   + SkyMath.NearM.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + ";"),
                     "the bake's sweep ladder is SkyMath's");
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

        // --- the one place it is applied, and the geometric normal it is applied with
        check.Expect(AlloyRenderer.OwnedLitFsMain.Contains("* ambShape * occl * CxSkyView(vWorld, normalize(vNrm));"),
                     "the lit stage folds it into the AMBIENT term alone, off the geometric normal");
        check.Expect(AlloyRenderer.OwnedLitFsHead.Contains("#include \"cx_skyview.glsl\"")
                  || AlloyRenderer.OwnedLitFsHead.Contains("float CxSkyView(vec3 P, vec3 N)"),
                     "...and the head carries the library it calls");

        // --- the fragment-side resolve, against the hand-worked values
        check.Expect(MathF.Abs(SkyMath.Term(StepM, 0.428846f, 0.85f, StepM, CoverM, RiseM) - 0.514519f) < 1e-4f,
                     "level ground at strength 0.85 over a 0.428846 column reads 0.514519");
        check.Expect(MathF.Abs(SkyMath.Term(-4f, 1f, 0.85f, StepM, CoverM, RiseM) - 0.15f) < 1e-4f,
                     "4 m under a roof reads the strength's floor, 0.15");
        check.Expect(MathF.Abs(SkyMath.Term(StepM + RiseM, 0.4f, 0.85f, StepM, CoverM, RiseM) - 1f) < 1e-4f,
                     "a point a rise band above the column's top surface reads the open sky");
        check.Expect(MathF.Abs(SkyMath.Term(-4f, 0.3f, 0f, StepM, CoverM, RiseM) - 1f) < 1e-6f,
                     "strength 0 is the flat vanilla fill back, everywhere");

        // --- it ships ON, the player can switch it off, and the reach outlives the sweep's near radius
        check.Expect(Lighting.Sky.Default > 0f && Lighting.Sky.Min == 0f,
                     "the term ships on and the knob reaches 0 -- it is a look change on vanilla content");
        check.Expect(Lighting.SkyRange.Default > SkyMath.NearM && Lighting.SkyRange.Min > SkyMath.NearM,
                     "the reach never falls to the sweep's own near radius");
        check.Expect(MapSize % Group == 0, $"the map's {MapSize} texels divide the dispatch's {Group}-wide groups");

        log.Info(check.Ok ? "SkyView.SelfCheck: passed" : "SkyView.SelfCheck: FAILED");
    }
}
