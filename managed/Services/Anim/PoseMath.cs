using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>The pose layer's shared maths and part access: springs, the two-bone solve, and the engine's
/// w-first part rotations. Part access goes through <see cref="PoseRig"/>, which answers both scene layers.</summary>
internal static unsafe class PoseMath
{
    /// <summary><c>Part::GetWorld</c>, set by <see cref="PoseService"/> before anything is chained.</summary>
    internal static PartGetWorldFn GetWorld;

    /// <summary>Game Programming Gems 4's SmoothCD: a critically damped spring, stable at any <paramref name="dt"/>.</summary>
    internal static void Smooth(ref float x, ref float v, float target, float dt, float time)
    {
        float omega = 2f / time, k = omega * dt;
        float e = 1f / (1f + k + 0.48f * k * k + 0.235f * k * k * k);
        float change = x - target, temp = (v + omega * change) * dt;
        v = (v - omega * temp) * e;
        x = target + (change + temp) * e;
    }

    /// <summary>Two <see cref="Smooth"/> springs at once.</summary>
    internal static void Smooth(ref Vector2 x, ref Vector2 v, Vector2 target, float dt, float time)
    {
        Smooth(ref x.X, ref v.X, target.X, dt, time);
        Smooth(ref x.Y, ref v.Y, target.Y, dt, time);
    }

    /// <summary>Longest step <see cref="Wobble"/> integrates in one go; a longer dt is cut into these.</summary>
    private const float WobbleStep = 1f / 120f;
    /// <summary>Substeps past which a hitch-sized dt just lets the wobble die: it is a flinch, not a state.</summary>
    private const int WobbleMaxSteps = 12;

    /// <summary>An underdamped spring toward 0 (natural frequency <paramref name="omega"/> rad/s, damping ratio
    /// <paramref name="zeta"/>): semi-implicit Euler in fixed substeps, so a flinch overshoots once and settles.</summary>
    internal static void Wobble(ref float x, ref float v, float omega, float zeta, float dt)
    {
        if (!(dt > 0f)) return;
        int n = (int)MathF.Ceiling(dt / WobbleStep);
        if (n > WobbleMaxSteps) { x = v = 0f; return; }
        float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            v += (-omega * omega * x - 2f * zeta * omega * v) * h;
            x += v * h;
        }
    }

    /// <summary>Two <see cref="Wobble"/> springs at once.</summary>
    internal static void Wobble(ref Vector2 x, ref Vector2 v, float omega, float zeta, float dt)
    {
        Wobble(ref x.X, ref v.X, omega, zeta, dt);
        Wobble(ref x.Y, ref v.Y, omega, zeta, dt);
    }

    /// <summary>Weight of the character's forward in the knee's bend plane, as a share of the leg's length, once
    /// the ankle is moved <see cref="PoleBlend"/> or more: large next to a straight or splayed leg's small knee
    /// bulge (so a lifted foot never bows the knee out), small next to a walking knee's bend.</summary>
    private const float PoleForward = 0.4f;
    /// <summary>Share of the leg's length the ankle must move before the forward pole has its full weight. Below
    /// it the bend plane is the animation's own, scaled in with the move, so the solve is continuous: a move of 0
    /// returns the animated knee exactly, and a correction switching on or off never jumps the knee.</summary>
    private const float PoleBlend = 0.1f;
    /// <summary>Share of the leg's reach over which a stretching leg eases into full extension (soft IK)
    /// instead of locking straight: the last few centimetres approach the target asymptotically.</summary>
    private const float SoftReach = 0.06f;

    /// <summary>World-space rotations taking the chain hip <paramref name="h"/>, knee <paramref name="k"/>, ankle
    /// <paramref name="a"/> to reach <paramref name="t"/>: <paramref name="r1"/> about the hip, then
    /// <paramref name="r2"/> about the new knee. The knee stays in the animation's own bend plane, blended toward the
    /// character's forward as the move grows; reach past what the animation stretched is softened. False if degenerate.</summary>
    internal static bool TwoBone(Vector3 h, Vector3 k, Vector3 a, Vector3 t, Vector3 fwd, out Quaternion r1, out Quaternion r2)
    {
        r1 = r2 = Quaternion.Identity;
        float la = Vector3.Distance(h, k), lb = Vector3.Distance(k, a);
        Vector3 d = t - h;
        float c = d.Length();
        if (la < 1e-4f || lb < 1e-4f || c < 1e-4f) return false;
        Vector3 dir = d / c;
        c = Soften(c, Vector3.Distance(h, a), la + lb);
        c = Math.Clamp(c, MathF.Abs(la - lb) + 1e-4f, la + lb - 1e-4f);
        Vector3 line = a - h;
        Vector3 bulge = (k - h) - line * (Vector3.Dot(k - h, line) / MathF.Max(line.LengthSquared(), 1e-12f));
        float w = PoleForward * (la + lb) * MathF.Min(1f, Vector3.Distance(t, a) / (PoleBlend * (la + lb)));
        Vector3 pole = bulge + fwd * w;
        pole -= dir * Vector3.Dot(pole, dir);
        if (pole.LengthSquared() < 1e-12f)
        {
            pole = fwd - dir * Vector3.Dot(fwd, dir);
            if (pole.LengthSquared() < 1e-12f) return false;
        }
        pole = Vector3.Normalize(pole);
        float cosA = Math.Clamp((la * la + c * c - lb * lb) / (2f * la * c), -1f, 1f);
        Vector3 knee = h + la * (dir * cosA + pole * MathF.Sqrt(1f - cosA * cosA));
        r1 = Arc(k - h, knee - h);
        Vector3 a1 = h + Vector3.Transform(a - h, r1);
        r2 = Arc(a1 - knee, h + dir * c - knee);
        return true;
    }

    /// <summary>Soft IK: a hip-to-target distance <paramref name="c"/> past the soft zone approaches
    /// <paramref name="reach"/> exponentially instead of hitting it. The zone never starts below what the
    /// animation itself stretched (<paramref name="animated"/>), so an unmoved ankle is never pulled in.</summary>
    internal static float Soften(float c, float animated, float reach)
    {
        float start = MathF.Max(reach * (1f - SoftReach), animated);
        float soft = reach - start;
        if (c <= start || soft < 1e-5f) return c;
        return start + soft * (1f - MathF.Exp(-(c - start) / soft));
    }

    /// <summary>The shortest rotation taking <paramref name="u"/> onto <paramref name="v"/>.</summary>
    internal static Quaternion Arc(Vector3 u, Vector3 v)
    {
        u = Vector3.Normalize(u); v = Vector3.Normalize(v);
        float d = Vector3.Dot(u, v);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            Vector3 axis = Vector3.Cross(MathF.Abs(u.X) < 0.9f ? Vector3.UnitX : Vector3.UnitY, u);
            return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis), MathF.PI);
        }
        Vector3 c = Vector3.Cross(u, v);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }

    /// <summary>The rotation whose axis-angle vector is <paramref name="r"/> (radians about
    /// <paramref name="right"/>) plus <paramref name="f"/> (radians about <paramref name="fwd"/>): the pose
    /// layer's upper-body currency. A positive angle about right tips the top backward, about forward to the right.</summary>
    internal static Quaternion Tip(Vector3 right, Vector3 fwd, float r, float f)
    {
        Vector3 axis = right * r + fwd * f;
        float angle = axis.Length();
        return angle < 1e-6f ? Quaternion.Identity : Quaternion.CreateFromAxisAngle(axis / angle, angle);
    }

    /// <summary><c>Part::GetWorld</c>; the engine's quaternion is w-first, System.Numerics' is w-last. Both products
    /// are Hamilton (<c>operator*(Quaternion const&amp;, Quaternion const&amp;)</c> @0x5b5c30), and a child's world
    /// rotation is its parent's times its local.</summary>
    internal static void World(IntPtr part, out Vector3 pos, out Quaternion rot) => PoseRig.World(part, out pos, out rot);

    /// <summary>The rotation vector (axis times angle, radians) of <paramref name="q"/>, the short way round.</summary>
    internal static Vector3 Log(Quaternion q)
    {
        if (q.W < 0f) q = Quaternion.Negate(q);
        Vector3 v = new(q.X, q.Y, q.Z);
        float s = v.Length();
        return s < 1e-7f ? 2f * v : v * (2f * MathF.Atan2(s, q.W) / s);
    }

    /// <summary>The rotation whose rotation vector is <paramref name="r"/>: <see cref="Log"/>'s inverse.</summary>
    internal static Quaternion Exp(Vector3 r)
    {
        float a = r.Length();
        return a < 1e-7f ? Quaternion.Normalize(new Quaternion(0.5f * r, 1f)) : Quaternion.CreateFromAxisAngle(r / a, a);
    }

    internal static Quaternion Local(IntPtr part) => PoseRig.Local(part);

    internal static void SetLocal(IntPtr part, Quaternion q) => PoseRig.SetLocal(part, Quaternion.Normalize(q));
}
