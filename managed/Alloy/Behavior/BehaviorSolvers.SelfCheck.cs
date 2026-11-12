using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class BehaviorSolvers
{
    private static bool Near(float a, float b, float eps = 2e-4f) => MathF.Abs(a - b) < eps;
    private static bool Near(in Vector3 a, in Vector3 b, float eps = 2e-4f) => (a - b).Length() < eps;

    /// <summary>
    /// The solvers' arithmetic against the decompilation, off-device. What is pinned is every number a solve derives: the
    /// zoom curve, the lag factor both the focus lerp and the orientation slerp share, the rig offset, the
    /// GUI-scale division, the viewport rounding, the navigate basis and the Euler slot order the rotate
    /// attach feeds <c>YawPitchRoll</c>.
    /// </summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        int n = 0;
        void Expect(bool cond, string what) { n++; check.Expect(cond, what); }

        // ---- the camera solve's three integrators ----
        Expect(Zoom(8f, 0f, 1f / 60f) == 8f, "no zoom input leaves the distance alone");
        Expect(Near(Zoom(8f, 2f, 1f), 4f), "zoom velocity 2 for one second halves the distance");
        Expect(Near(Zoom(8f, -2f, 1f), 16f), "a negative zoom velocity doubles it");
        Expect(Near(Zoom(8f, 2f, 0.5f), 8f * MathF.Pow(2f, -0.5f)), "the curve is continuous in dt");
        Expect(Step(1f / 60f) == 1f / 60f && Step(0.1f) == 0.1f, "a frame at 10 fps or better integrates its whole dt");
        Expect(Near(Zoom(9.8f, -7.5f, Step(1.1f)), 9.8f * MathF.Pow(2f, 0.375f), 1e-3f),
               "one zoom-out pass across a 1.1 s load frame moves the camera by a 0.1 s step, not out to 168");

        Expect(Lag(1f, 0f) == 0f, "dt 0 moves nothing");
        Expect(Near(Lag(1f, 1f), 0.5f), "rate 1 closes half the gap in one second");
        Expect(Near(Lag(1f, 2f), 0.75f), "...and three quarters in two -- frame-rate independent by construction");
        Expect(Near(Lag(2f, 0.5f), 0.5f), "rate scales the clock");

        var focus = new Vector3(10f, 20f, 0f);
        var target = new Vector3(14f, 20f, 1f);
        var off = new Vector3(0f, 0f, 1f);                 // the constructor's default: chest height
        Expect(Near(FocusStep(focus, target, off, 0f), focus), "a lag factor of 0 holds the focus");
        Expect(Near(FocusStep(focus, target, off, 1f), target + off), "a lag factor of 1 snaps to target + offset");
        Expect(Near(FocusStep(focus, target, off, 0.5f), new Vector3(12f, 20f, 1f)),
                     "half way is the midpoint of focus and target + offset");

        // ---- the rig: focus + dist * (q * +Z) ----
        Expect(Near(Rig(focus, 8f, BehaviorMath.YawPitchRoll(0f, 90f, 0f)), new Vector3(10f, 12f, 0f)),
                     "pitch 90 puts the camera 8 units south of the focus");
        Expect(Near(Rig(focus, 8f, BehaviorMath.YawPitchRoll(0f, 0f, 0f)), new Vector3(10f, 20f, 8f)),
                     "pitch 0 puts it 8 units above");
        Expect(Near(Rig(focus, 0f, BehaviorMath.YawPitchRoll(33f, 44f, 0f)), focus),
                     "distance 0 is the focus itself");

        // ---- the rotate attach's Euler slots: the field is a Vector and the engine feeds it (z, x, y) ----
        var euler = new Vector3(0f, 0f, 90f);              // z only -> a pure yaw
        Expect(Near(BehaviorMath.Yaw(BehaviorMath.YawPitchRoll(euler.Z, euler.X, euler.Y)), 90f),
                     "m_vRotation.z is the yaw");
        euler = new Vector3(25f, 0f, 0f);
        Expect(Near(BehaviorMath.Pitch(BehaviorMath.YawPitchRoll(euler.Z, euler.X, euler.Y)), 25f),
                     "m_vRotation.x is the pitch");
        euler = new Vector3(0f, 25f, 0f);
        Expect(Near(BehaviorMath.Roll(BehaviorMath.YawPitchRoll(euler.Z, euler.X, euler.Y)), 25f),
                     "m_vRotation.y is the roll");

        // ---- GUI scale: x and y only ----
        var p = new Vector3(100f, 50f, 7f);
        Expect(Near(GuiScaled(p, 1f), p), "a scale of 1 changes nothing");
        Expect(Near(GuiScaled(p, 2f), new Vector3(50f, 25f, 7f)), "x and y are divided, z is not");

        // ---- the subwindow viewport ----
        Expect(ViewportSpan(0f, 6.4f) == 640, "a 6.4-unit span is 640 pixels at 0.01 per unit");
        Expect(ViewportSpan(1.23f, 1.23f) == 1, "a collapsed span is one pixel, never zero");
        Expect(ViewportSpan(5f, 1f) == 1, "an inverted span is one pixel too");
        Expect(ViewportSpan(0f, 0.01005f) == 1 && ViewportSpan(0f, 0.0155f) == 2, "the span rounds half-up");

        // ---- the navigate basis ----
        (Vector3 fwd, Vector3 right) = NavBasis(0f);
        Expect(Near(fwd, new Vector3(0f, 1f, 0f)), "yaw 0 walks +Y");
        Expect(Near(right, new Vector3(1f, 0f, 0f)), "...and strafes +X");
        (fwd, right) = NavBasis(90f);
        Expect(Near(fwd, new Vector3(-1f, 0f, 0f)), "yaw 90 walks -X");
        Expect(Near(right, new Vector3(0f, 1f, 0f)), "...and strafes +Y");
        (fwd, right) = NavBasis(37f);
        Expect(Near(Vector3.Dot(fwd, right), 0f) && Near(fwd.Length(), 1f) && Near(right.Length(), 1f),
                     "the basis stays orthonormal at an arbitrary yaw");
        Expect(Near(fwd, BehaviorMath.Rotate(BehaviorMath.YawPitchRoll(37f, 0f, 0f), new Vector3(0f, 1f, 0f))),
                     "forward is the yaw quaternion applied to +Y -- the same convention as the orientation write");

        log.Info(check.Ok ? $"BehaviorSolvers.SelfCheck: passed ({n} checks)" : "BehaviorSolvers.SelfCheck: FAILED");
    }
}
