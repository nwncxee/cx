using System.Numerics;
using CX.Engine;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The scene terms the engine's own <c>SetUniforms</c> hands every program by NAME and only a content pair's
/// stage reads -- the module calendar, the frame counter, the weather kind, the mouse and the wind's point sources.
/// The content environments declare them under those names, so a stage reads what it would in the engine.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The point sources the engine uploads at most (<c>SetUniforms</c> clamps the list to 0x80).</summary>
    internal const int MaxWindUniforms = 128;

    /// <summary>The engine-named terms' locations in one program, -1 for a term it does not declare. Resolved under
    /// GLRender::ConnectShaderData @0x4ce8e0's own spellings.</summary>
    internal struct EngineNamedLocs
    {
        public int Year, Month, Day, Hour, Dawn, Dusk, MinutesPerHour, IntoTransition, TransitionTime;
        public int Frame, WeatherType, WeatherDensity, Mouse, MouseButtons;
        public int WindCount, WindPos, WindRadius, WindDuration, WindIntensity, WindRemaining;
        /// <summary>Does the program declare any of them? Most do not, and then the upload makes no call at all.</summary>
        public bool Any;
    }

    /// <summary>The point sources as the stages read them, gathered once per frame rather than per program bind.</summary>
    private sealed class WindUniformState
    {
        public readonly Vector3[] Pos = new Vector3[MaxWindUniforms];
        public readonly float[] Radius = new float[MaxWindUniforms], Duration = new float[MaxWindUniforms],
                                Intensity = new float[MaxWindUniforms], Remaining = new float[MaxWindUniforms];
        public int Count, Frame = -1;
    }

    private static readonly WindUniformState _windU = new();

    /// <summary>The engine-named terms' locations in <paramref name="prog"/>.</summary>
    internal static EngineNamedLocs EngineNamedLocsOf(uint prog)
    {
        var n = new EngineNamedLocs
        {
            Year = GlProgram.Loc(prog, "moduleYear"), Month = GlProgram.Loc(prog, "moduleMonth"),
            Day = GlProgram.Loc(prog, "moduleDay"), Hour = GlProgram.Loc(prog, "moduleHour"),
            Dawn = GlProgram.Loc(prog, "moduleDawnHour"), Dusk = GlProgram.Loc(prog, "moduleDuskHour"),
            MinutesPerHour = GlProgram.Loc(prog, "moduleMinutesPerHour"),
            IntoTransition = GlProgram.Loc(prog, "moduleTimeIntoTransition"),
            TransitionTime = GlProgram.Loc(prog, "moduleTransitionTime"),
            Frame = GlProgram.Loc(prog, "sceneCurrentFrame"), WeatherType = GlProgram.Loc(prog, "areaWeatherType"),
            WeatherDensity = GlProgram.Loc(prog, "areaWeatherDensity"),
            Mouse = GlProgram.Loc(prog, "userinputMousePosition"), MouseButtons = GlProgram.Loc(prog, "userinputMouseButtons"),
            WindCount = GlProgram.Loc(prog, "windPointSourcesCount"), WindPos = GlProgram.Loc(prog, "windPointSourcesPosition"),
            WindRadius = GlProgram.Loc(prog, "windPointSourcesRadius"),
            WindDuration = GlProgram.Loc(prog, "windPointSourcesDuration"),
            WindIntensity = GlProgram.Loc(prog, "windPointSourcesIntensity"),
            WindRemaining = GlProgram.Loc(prog, "windPointSourcesTimeRemaining"),
        };
        n.Any = n.Year >= 0 || n.Month >= 0 || n.Day >= 0 || n.Hour >= 0 || n.Dawn >= 0 || n.Dusk >= 0
                || n.MinutesPerHour >= 0 || n.IntoTransition >= 0 || n.TransitionTime >= 0 || n.Frame >= 0
                || n.WeatherType >= 0 || n.WeatherDensity >= 0 || n.Mouse >= 0 || n.MouseButtons >= 0 || n.WindCount >= 0
                || n.WindPos >= 0 || n.WindRadius >= 0 || n.WindDuration >= 0 || n.WindIntensity >= 0 || n.WindRemaining >= 0;
        return n;
    }

    /// <summary>Upload the engine-named terms <paramref name="n"/>'s program declares (GL ignores a -1 location). The
    /// calendar, weather and mouse come off <see cref="Rt"/>, the frame counter is the presented frame's, the point
    /// sources Alloy's own wind's.</summary>
    internal static void UploadEngineNamed(in EngineNamedLocs n)
    {
        if (!n.Any) return;
        if (_runtime != null)
        {
            ShaderExposedRuntimeData rt = Rt;
            Gl.Uniform1i(n.Year, rt.ModuleYear); Gl.Uniform1i(n.Month, rt.ModuleMonth);
            Gl.Uniform1i(n.Day, rt.ModuleDay); Gl.Uniform1i(n.Hour, rt.ModuleHour);
            Gl.Uniform1i(n.Dawn, rt.ModuleDawnHour); Gl.Uniform1i(n.Dusk, rt.ModuleDuskHour);
            Gl.Uniform1i(n.MinutesPerHour, rt.ModuleMinutesPerHour);
            Gl.Uniform1f(n.IntoTransition, rt.ModuleTimeIntoTransition);
            Gl.Uniform1f(n.TransitionTime, rt.ModuleTransitionTime);
            Gl.Uniform1i(n.WeatherType, rt.AreaWeatherType);
            Gl.Uniform1f(n.WeatherDensity, rt.AreaWeatherDensity);
            if (Gl.Uniform2i != null) Gl.Uniform2i(n.Mouse, rt.MouseX, rt.MouseY);
            Gl.Uniform1i(n.MouseButtons, rt.MouseButtons);
        }
        Gl.Uniform1i(n.Frame, _frameSeq);
        if (n.WindCount < 0 && n.WindPos < 0) return;
        WindUniformState w = _windU;
        if (w.Frame != _frameSeq)
        {
            w.Frame = _frameSeq;
            w.Count = AlloyWind.PointSources(w.Pos, w.Radius, w.Duration, w.Intensity, w.Remaining);
        }
        Gl.Uniform1i(n.WindCount, w.Count);
        if (w.Count == 0 || Gl.Uniform1fv == null || Gl.Uniform3fv == null) return;
        fixed (Vector3* p = w.Pos) Gl.Uniform3fv(n.WindPos, w.Count, (float*)p);
        fixed (float* p = w.Radius) Gl.Uniform1fv(n.WindRadius, w.Count, p);
        fixed (float* p = w.Duration) Gl.Uniform1fv(n.WindDuration, w.Count, p);
        fixed (float* p = w.Intensity) Gl.Uniform1fv(n.WindIntensity, w.Count, p);
        fixed (float* p = w.Remaining) Gl.Uniform1fv(n.WindRemaining, w.Count, p);
    }
}
