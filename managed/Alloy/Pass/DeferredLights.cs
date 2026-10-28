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
/// opaque and cutout passes, reading the G-buffer and adding every point light's diffuse, specular,
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
    }

    /// <summary>May the VANILLA renderer's chain draw opaque and cutout batches through the <c>CX_DEFERRED</c>
    /// twin? <c>light.deferred</c> on, a main-view pass, and that chain's own deferred twin linked -- which it
    /// never is (<see cref="Programs._progLitDef"/>), so the pair tiles it would take its debt from stay down.</summary>
    internal static bool Active => Lighting.Deferred.V && (AlloyRenderer._lightViz == 0 || AlloyRenderer._lightViz == AlloyRenderer.VizLightFrom)
                                   && Programs._progLitDef != 0;

    internal static void Attach(ExtenderLog log) => _log = log;

    internal static string Stats(int frames)
        => $"deferred lights {(!Lighting.Deferred.V ? "off" : Lighting.DeferredTiles.V ? "on, tiled" : "on, froxel lists")}"
         + (_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "");
}
