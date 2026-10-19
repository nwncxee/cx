using System.Numerics;
using CX.Engine;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The frame a native stage and its passes see, as one record: the MAIN view's camera and size whichever view
/// a program is drawn into, and whether the stage reading it may trust its history.</summary>
internal readonly struct StageFrame
{
    internal readonly Matrix4x4 InvVp, PrevVp;
    internal readonly Vector3 Eye;
    internal readonly int W, H, Seq;
    /// <summary>Is this frame's history garbage -- images just (re)made, or the first frame after a gap?</summary>
    internal readonly bool Fresh;

    internal StageFrame(Matrix4x4 invVp, Matrix4x4 prevVp, Vector3 eye, int w, int h, int seq, bool fresh)
    {
        InvVp = invVp; PrevVp = prevVp; Eye = eye; W = w; H = h; Seq = seq; Fresh = fresh;
    }
}

/// <summary>The <c>cx*</c> frame uniforms, located in one program by name: no environment declares them, so a stage
/// declares the ones it reads and gets them set -- a native one or a stage the engine also compiles, which leaves
/// them 0. Every host uploads the same set (<c>docs/shader-overrides.md</c>, "The frame").</summary>
internal unsafe struct FrameLoc
{
    internal int Frame, Main, Fresh, InvVp, PrevVp, Eye, View, Overlay;

    /// <summary>Does the program declare any of them? A program that declares none costs its host nothing per bind.</summary>
    internal readonly bool Any => Frame >= 0 || Main >= 0 || Fresh >= 0 || InvVp >= 0 || PrevVp >= 0 || Eye >= 0
                                || View >= 0 || Overlay >= 0;

    internal static FrameLoc Locate(uint prog) => new()
    {
        Frame = GlProgram.Loc(prog, "cxFrame"), Main = GlProgram.Loc(prog, "cxMain"),
        Fresh = GlProgram.Loc(prog, "cxFresh"), InvVp = GlProgram.Loc(prog, "cxInvVp"),
        PrevVp = GlProgram.Loc(prog, "cxPrevVp"), Eye = GlProgram.Loc(prog, "cxEye"),
        View = GlProgram.Loc(prog, "cxView"), Overlay = GlProgram.Loc(prog, "cxOverlay"),
    };

    /// <summary>Upload the set into the program currently bound. <paramref name="main"/> is <c>cxMain</c>: 1 for the
    /// on-screen view, 0 for a probe face or a reflection. Hot, and it allocates nothing.</summary>
    internal readonly void Upload(in StageFrame f, bool main, bool overlay)
    {
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
        Gl.Uniform1i(Overlay, overlay ? 1 : 0);
    }
}

/// <summary>The engine terms a WHOLE stage takes -- the sky's fragment stage and a compute pass, which run under the
/// engine's own names (<c>cx_stageenv.glsl</c>): the scriptable, fog and camera block every Alloy stage gets, and the
/// calendar and weather scalars from <c>m_shaderExposedRuntimeData</c>. One upload feeds both.</summary>
internal unsafe struct StageEnvLoc
{
    internal AlloyRenderer.FeatLoc Feat;
    internal int Year, Month, Day, Hour, Dawn, Dusk, Minutes, Frame, Transition, Weather, WeatherType;

    /// <summary>Resolve every name in one program. A location the program does not declare is -1, which GL
    /// ignores, so the same set is uploaded to a stock sky stage and to a pass alike.</summary>
    internal static StageEnvLoc Locate(uint prog)
    {
        StageEnvLoc e = default;
        e.Feat = Programs.FeatLocs(prog);
        e.Year = GlProgram.Loc(prog, "uModYear"); e.Month = GlProgram.Loc(prog, "uModMonth");
        e.Day = GlProgram.Loc(prog, "uModDay"); e.Hour = GlProgram.Loc(prog, "uModHour");
        e.Dawn = GlProgram.Loc(prog, "uModDawn"); e.Dusk = GlProgram.Loc(prog, "uModDusk");
        e.Minutes = GlProgram.Loc(prog, "uModMinutes"); e.Frame = GlProgram.Loc(prog, "uSceneFrame");
        e.Transition = GlProgram.Loc(prog, "uModTransition"); e.Weather = GlProgram.Loc(prog, "uWeatherDensity");
        e.WeatherType = GlProgram.Loc(prog, "uWeatherType");
        return e;
    }

    /// <summary>Upload the whole block into the program currently bound. Hot -- one call per program per pass, and it
    /// allocates nothing.</summary>
    internal readonly void Upload(int* viewport)
    {
        AlloyRenderer.UploadFeatureTerms(Feat, viewport);
        Gl.Uniform1i(Frame, AlloyRenderer.FrameSeq);
        if (!AlloyRenderer.HasRuntime) return;
        ShaderExposedRuntimeData rt = AlloyRenderer.Rt;
        Gl.Uniform1i(Year, rt.ModuleYear); Gl.Uniform1i(Month, rt.ModuleMonth);
        Gl.Uniform1i(Day, rt.ModuleDay); Gl.Uniform1i(Hour, rt.ModuleHour);
        Gl.Uniform1i(Dawn, rt.ModuleDawnHour); Gl.Uniform1i(Dusk, rt.ModuleDuskHour);
        Gl.Uniform1i(Minutes, rt.ModuleMinutesPerHour);
        Gl.Uniform1f(Transition, rt.ModuleTimeIntoTransition);
        Gl.Uniform1f(Weather, rt.AreaWeatherDensity); Gl.Uniform1i(WeatherType, rt.AreaWeatherType);
    }
}
