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
    public int LightCount, ShRes;

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
            ("uShRes", Std140.At(ref b, ref b.ShRes)),
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

    internal static void Attach(ExtenderLog log) => _log = log;

    /// <summary>Lights the walk's tile lists can name (<c>cx_deferred_cs</c>'s <c>CX_TILE_LIGHTS</c>): an area with more
    /// walks each pixel's froxel list instead.</summary>
    internal const int TileLights = 4096;

    internal static string Stats(int frames)
        => $"deferred lights {(!Lighting.Deferred.V ? "off" : Lighting.DeferredTiles.V ? "on, tiled" : "on, froxel lists")}"
         + (Lighting.Deferred.V ? ", " + AlloyRenderer.VkDefWalked() : "")
         + (_down.Reason.Length > 0 ? ", last refusal: " + _down.Reason : "");

    /// <summary>The period line's lights per pixel: <paramref name="pixels"/> deferred pixels lit on the sampled frame,
    /// <paramref name="walked"/> list entries they walked between them, <paramref name="most"/> the most one walked.</summary>
    internal static string WalkedText(uint pixels, uint walked, uint most)
        => pixels == 0 ? "no deferred pixel sampled yet"
         : $"a deferred pixel walked {(double)walked / pixels:F1} lights on average, {most} at most";
}
