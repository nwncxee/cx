using System.Numerics;
using CX.Framework;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>The chest's lean from speeding up and from a slope tips along the TRAVEL, not the facing. Oracle: the
/// direction each body should tip, worked by hand (a walker tips its top away from a fall and along a climb or a
/// start; the facing is +Y, its right +X), read off the top of a chest tipped by <see cref="UpperBody.Step"/>'s lean.</summary>
internal static class UpperBodyLeanCheck
{
    /// <summary>Where the top of the chest points after a body travelling at <paramref name="vel"/> (facing +Y)
    /// accelerating at <paramref name="accel"/> on <paramref name="slope"/> settles into its lean.</summary>
    private static Vector3 Top(Vector2 vel, float accel, float slope)
    {
        PoseService.Slot s = default;
        s.Vel = vel;
        s.Speed = vel.Length();
        s.Fwd = Vector3.UnitY;
        s.Right = Vector3.UnitX;
        s.Cue = accel;
        s.Slope = slope;
        s.Moving = 1f;
        UpperBody.Step(ref s, System.IntPtr.Zero, Vector3.Zero, 1f, 1f, s.Speed, false, true, 1f / 60f);
        return Vector3.Transform(Vector3.UnitZ, Tip(Vector3.UnitX, Vector3.UnitY, s.Lean.X, s.Lean.Y));
    }

    /// <summary>Runs the lean directions; each logs a FAIL line naming its case when it no longer holds.</summary>
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "UpperBody.Lean.SelfCheck");
        check.Expect(Top(new Vector2(0f, 2f), 0f, 0.2f).Y > 0.02f, "walking forward up a slope tips the top forward");
        check.Expect(Top(new Vector2(0f, 2f), 0f, -0.2f).Y < -0.02f, "walking forward down a slope tips the top back");
        check.Expect(Top(new Vector2(0f, 2f), 3f, 0f).Y > 0.02f, "a forward start tips the top forward");
        check.Expect(Top(new Vector2(0f, -2f), 0f, -0.2f).Y > 0.02f, "walking backwards down a slope tips the top forward, toward the hill");
        check.Expect(Top(new Vector2(0f, -2f), 0f, 0.2f).Y < -0.02f, "walking backwards up a slope tips the top back, along the climb");
        check.Expect(Top(new Vector2(0f, -2f), 3f, 0f).Y < -0.02f, "a backwards start tips the top back, along the travel");
        check.Expect(Top(new Vector2(2f, 0f), 0f, 0.2f).X > 0.02f, "a sidestep right up a slope tips the top right");
        check.Expect(Top(new Vector2(-2f, 0f), 3f, 0f).X < -0.02f, "a sidestep left from a start tips the top left");
        Vector3 still = Top(Vector2.Zero, 3f, 0.2f);
        check.Expect(System.MathF.Abs(still.X) < 1e-4f && System.MathF.Abs(still.Y) < 1e-4f, "a body with no travel to read leans nothing from a start or a slope");
    }
}
