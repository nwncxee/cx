using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The arithmetic every behaviour solve derives from -- the zoom curve, the shared lag factor, the
/// rig offset, the GUI-scale division, the viewport rounding, the navigate basis -- transcribed from the
/// engine. <see cref="Db.DbAnim"/> solves the engine's behaviour classes over database columns with it.</summary>
internal static unsafe partial class BehaviorSolvers
{
    /// <summary>The engine constant a GUI viewport's part coordinates are expressed in: the subwindow solver
    /// divides by it to get pixels and multiplies back to place the camera.</summary>
    private const float GuiUnit = 0.01f;

    /// <summary>One solve's exponential zoom: the distance halves per two units of accumulated zoom velocity
    /// per second (<c>dist * pow(2, -zoomVel * dt * 0.5)</c>, the exponent computed in float and widened, as
    /// the engine does).</summary>
    internal static float Zoom(float dist, float zoomVel, float dt) =>
        dist * (float)Math.Pow(2.0, -zoomVel * dt * 0.5f);

    /// <summary>The lag factor the camera solve uses for BOTH the focus lerp and the orientation slerp:
    /// <c>1 - 0.5^(dt * rate)</c>, i.e. "close half the remaining gap every <c>1/rate</c> seconds", which is
    /// frame-rate independent by construction.</summary>
    internal static float Lag(float rate, float dt) => 1f - MathF.Pow(0.5f, dt * rate);

    /// <summary>One step of the camera focus toward its target plus the behaviour's own offset.</summary>
    internal static Vector3 FocusStep(in Vector3 focus, in Vector3 target, in Vector3 offset, float a)
    {
        float keep = 1f - a;
        return new Vector3((target.X + offset.X) * a + focus.X * keep,
                           (target.Y + offset.Y) * a + focus.Y * keep,
                           (target.Z + offset.Z) * a + focus.Z * keep);
    }

    /// <summary>Where the camera sits: one <paramref name="dist"/> along <c>q * +Z</c> from the focus.</summary>
    internal static Vector3 Rig(in Vector3 focus, float dist, in Quat q)
    {
        Vector3 f = BehaviorMath.Forward(q);
        return new Vector3(f.X * dist + focus.X, f.Y * dist + focus.Y, f.Z * dist + focus.Z);
    }

    /// <summary>The GUI-scale attach's whole effect: x and y divided by the live scale, z left alone.</summary>
    internal static Vector3 GuiScaled(in Vector3 p, float scale) => new(p.X / scale, p.Y / scale, p.Z);

    /// <summary>One side of the subwindow viewport, in pixels: the GUI-unit span rounded half-up and never
    /// allowed to collapse.</summary>
    internal static int ViewportSpan(float lo, float hi) => Math.Max(1, BehaviorMath.Round((hi - lo) / GuiUnit));

    /// <summary>The navigate solver's basis at <paramref name="yawDeg"/>: forward, then the right it strafes
    /// along (<c>cross(forward, +Z)</c>).</summary>
    internal static (Vector3 Forward, Vector3 Right) NavBasis(float yawDeg)
    {
        (float sin, float cos) = MathF.SinCos(yawDeg * BehaviorMath.Deg2Rad);
        Vector3 fwd = new(-sin, cos, 0f);
        return (fwd, BehaviorMath.Cross(fwd, new Vector3(0f, 0f, 1f)));
    }
}
