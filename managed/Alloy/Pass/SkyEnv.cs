using System.Numerics;
using CX.Engine;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The sky's own frame, as one record handed to every sky program of that frame: the MAIN view's
/// camera and size whichever view the dome is drawn into, and whether the twin reading it may trust its
/// history. The GLSL side is <c>cx_skyenv.glsl</c>'s <c>cxSky*</c> block.</summary>
internal readonly struct SkyFrame
{
    internal readonly Matrix4x4 InvVp, PrevVp;
    internal readonly Vector3 Eye;
    internal readonly int W, H, Seq;
    /// <summary>Is this frame's history garbage -- images just (re)made, or the first sky frame after a gap?</summary>
    internal readonly bool Fresh;

    internal SkyFrame(Matrix4x4 invVp, Matrix4x4 prevVp, Vector3 eye, int w, int h, int seq, bool fresh)
    {
        InvVp = invVp; PrevVp = prevVp; Eye = eye; W = w; H = h; Seq = seq; Fresh = fresh;
    }
}

/// <summary>Every engine term a SKY program takes, located once and uploaded in one call: the scriptable, fog
/// and camera block every Alloy stage gets, the calendar and weather scalars from
/// <c>m_shaderExposedRuntimeData</c>, and the sky's own frame. The dome's fragment program and a twin's
/// compute passes both go through it, so neither can disagree with the other. GLSL: <c>cx_skyenv.glsl</c>.</summary>
internal unsafe struct SkyEnvLoc
{
    internal AlloyRenderer.FeatLoc Feat;
    internal int Year, Month, Day, Hour, Dawn, Dusk, Transition, Weather;
    internal int Frame, Main, Fresh, InvVp, PrevVp, Eye, View;

    /// <summary>Resolve every name in one program. A location the program does not declare is -1, which GL
    /// ignores, so the same set is uploaded to a stock sky stage and to a twin's pass alike.</summary>
    internal static SkyEnvLoc Locate(uint prog)
    {
        SkyEnvLoc e = default;
        e.Feat = Programs.FeatLocs(prog);
        e.Year = GlProgram.Loc(prog, "uModYear"); e.Month = GlProgram.Loc(prog, "uModMonth");
        e.Day = GlProgram.Loc(prog, "uModDay"); e.Hour = GlProgram.Loc(prog, "uModHour");
        e.Dawn = GlProgram.Loc(prog, "uModDawn"); e.Dusk = GlProgram.Loc(prog, "uModDusk");
        e.Transition = GlProgram.Loc(prog, "uModTransition"); e.Weather = GlProgram.Loc(prog, "uWeatherDensity");
        e.Frame = GlProgram.Loc(prog, "cxSkyFrame"); e.Main = GlProgram.Loc(prog, "cxSkyMain");
        e.Fresh = GlProgram.Loc(prog, "cxSkyFresh"); e.InvVp = GlProgram.Loc(prog, "cxSkyInvVp");
        e.PrevVp = GlProgram.Loc(prog, "cxSkyPrevVp"); e.Eye = GlProgram.Loc(prog, "cxSkyEye");
        e.View = GlProgram.Loc(prog, "cxSkyView");
        return e;
    }

    /// <summary>Upload the whole block into the program currently bound. <paramref name="main"/> is what the
    /// stage reads as <c>cxSkyMain</c>: 1 for the on-screen view, 0 for a probe cube face. Hot -- one call per
    /// sky program per pass, and it allocates nothing.</summary>
    internal void Upload(int* viewport, bool main, in SkyFrame f)
    {
        AlloyRenderer.UploadFeatureTerms(Feat, viewport);
        Gl.Uniform1i(Frame, f.Seq);
        Gl.Uniform1i(Main, main ? 1 : 0);
        Gl.Uniform1i(Fresh, f.Fresh ? 1 : 0);
        // raw row-major bytes, read column-major by GLSL: the transpose, so `M * v` in the stage is exact
        // (the convention MotionVectors' uInvVP/uPrevVP upload takes).
        Matrix4x4 inv = f.InvVp, prev = f.PrevVp;
        Gl.UniformMatrix4fv(InvVp, 1, 0, (float*)&inv);
        Gl.UniformMatrix4fv(PrevVp, 1, 0, (float*)&prev);
        Vector3 eye = f.Eye;
        Gl.Uniform3fv(Eye, 1, (float*)&eye);
        Gl.Uniform2i(View, f.W, f.H);
        if (!AlloyRenderer.HasRuntime) return;
        ShaderExposedRuntimeData rt = AlloyRenderer.Rt;
        Gl.Uniform1i(Year, rt.ModuleYear); Gl.Uniform1i(Month, rt.ModuleMonth);
        Gl.Uniform1i(Day, rt.ModuleDay); Gl.Uniform1i(Hour, rt.ModuleHour);
        Gl.Uniform1i(Dawn, rt.ModuleDawnHour); Gl.Uniform1i(Dusk, rt.ModuleDuskHour);
        Gl.Uniform1f(Transition, rt.ModuleTimeIntoTransition);
        Gl.Uniform1f(Weather, rt.AreaWeatherDensity);
    }
}
