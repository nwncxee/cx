using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The <c>CxDeferred</c> block in the GLSL's own member order (<c>cx_defblk.glsl</c>). C# sequential
/// layout IS std140 here: the one vec3 is followed by the int that fills its padding, and nothing else needs
/// any.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DeferredBlock
{
    public Matrix4x4 InvVp, Proj, View;
    public Vector3 EyePos;
    public int Tiles;
    public Int4 Rect;
    public Int2 ShSize;
    public int LightCount, ReflK, ShRes;

    /// <summary>The GLSL against this struct. A member moved in either file stages every term after it into
    /// its neighbour, and a wrong viewport rect reconstructs every pixel's world position somewhere else.</summary>
    internal static void Pin(Checker check)
    {
        var b = default(DeferredBlock);
        Std140.Pin(check, ShaderSource.Get("cx_defblk.glsl"), "CxDeferred", UboSlots.Deferred, Unsafe.SizeOf<DeferredBlock>(),
        [
            ("uInvVp", Std140.At(ref b, ref b.InvVp)), ("uProj", Std140.At(ref b, ref b.Proj)),
            ("uView", Std140.At(ref b, ref b.View)), ("uEyePos", Std140.At(ref b, ref b.EyePos)),
            ("uTiles", Std140.At(ref b, ref b.Tiles)), ("uRect", Std140.At(ref b, ref b.Rect)),
            ("uShSize", Std140.At(ref b, ref b.ShSize)), ("uLightCount", Std140.At(ref b, ref b.LightCount)),
            ("uReflK", Std140.At(ref b, ref b.ReflK)), ("uShRes", Std140.At(ref b, ref b.ShRes)),
        ]);
    }
}

/// <summary>The deferred point-light pass (ADR 0029) -- one compute dispatch over the render target after the
/// opaque and cutout passes, reading <see cref="GBuffer"/> and adding every point light's diffuse, specular,
/// film highlight and hot-light debt into the HDR target. The arithmetic lives in <c>cx_deferred_cs</c>, which
/// composes the SAME libraries the forward loop does, so the two cannot differ except by an input.</summary>
internal static unsafe class DeferredLights
{
    /// <summary>The overridable compute stage's resref (hak-limit length).</summary>
    internal const string ShdCs = "cx_deferred_cs";

    private static ExtenderLog? _log;
    private static readonly StandDown _down = new("DeferredLights");

    /// <summary>The two programs whose link the deferred verdict rests on, in one object: context loss
    /// replaces it, so no name from the dead context survives as a field nobody remembered to zero.</summary>
    private sealed class DeferredLightsState
    {
        public uint Prog, ProgFilm;
    }

    private static DeferredLightsState _gl = new();
    /// <summary>The colour pass may draw opaque and cutout batches with the <c>CX_DEFERRED</c> twin this frame:
    /// <c>light.deferred</c> on, this pass's program linked, the G-buffer allocated, a main-view perspective
    /// pass, and <see cref="DeferredShadows"/> armed -- without its record every point-light shadow would be a
    /// single hardware tap. Every <c>/alloy viz</c> mode draws forward (ADR 0029) but the forward class's light-source view, which reads this pass.</summary>
    internal static bool Active => Lighting.Deferred.V && (AlloyRenderer._lightViz == 0 || AlloyRenderer._lightViz == AlloyRenderer.VizLightFrom)
                                   && GBuffer.Ready && !GBuffer.Failed && Programs._progLitDef != 0
                                   && Ready() && DeferredShadows.Ready();

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Build the program on the first frame that wants it, once. A link failure is LOUD and latched: the
    /// alternative is opaque surfaces with no point light on them and nothing in the log.</summary>
    private static bool Ready()
    {
        if (_gl.Prog != 0) return true;
        if (_down.Failed) return false;
        if (Gl.DispatchCompute == null || Gl.MemoryBarrier == null || Gl.BindImageTexture == null || Gl.BindBufferBase == null)
            return _down.Fail(_log, "compute, image stores or SSBO entry points are missing", "deferred point lights are off for the session");
        string src = ShaderStages.Source(ShdCs);
        if (!ShaderContract.RecordSafe(src) || !ShaderContract.RecordSafe(src, "CxChart", LightmapBake.RecFloats))
            return _down.Fail(_log, "the program's CxDraw or CxChart record disagrees with the extender's (an override older than it)",
                              "deferred point lights are off for the session");
        uint prog = GlProgram.Compute(ShdCs, src, _log, "DeferredLights");
        if (prog == 0)
        {
            _log?.Always("DeferredLights: the deferred point-light program did not build (the driver's own log is "
                       + "above) -- light.deferred is off for the session and the forward light loop draws the frame");
            return _down.Fail(_log, "the compute program did not link", "deferred point lights are off for the session");
        }
        uint film = GlProgram.Compute(ShaderStages.WithDefine(src, "CX_FILM_PASS 1"), _log, "DeferredLights film");
        if (film == 0)
        {
            _log?.Always("DeferredLights: the film pass did not build (the driver's own log is above) -- light.deferred is "
                       + "off for the session and the forward light loop draws the frame");
            return _down.Fail(_log, "the film pass did not link", "deferred point lights are off for the session");
        }
        _gl.ProgFilm = film;
        _gl.Prog = prog;
        _log?.Info("DeferredLights: ready -- the walk and the film pass both link");
        return true;
    }

    /// <summary>Context loss: the program and any per-size objects are gone.</summary>
    internal static void Forget()
    {
        _gl = new DeferredLightsState();
        _down.Forget();
    }

    internal static string Stats(int frames)
        => $"deferred lights {(!Lighting.Deferred.V ? "off" : Lighting.DeferredTiles.V ? "on, tiled" : "on, froxel lists")}"
         + (_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "");
}
