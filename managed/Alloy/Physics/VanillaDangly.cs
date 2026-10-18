using System;
using System.Numerics;

namespace CX.Alloy.Physics;

/// <summary>One part's vanilla state: the stepped positions and velocities per vertex, the step before (for
/// the interpolated draw), the previous world pose and the step accumulator.</summary>
public sealed class VanillaDanglyState
{
    public readonly Vector3[] Cur, Prev, Vel;
    public Vector3 PrevPos;
    public Quaternion PrevRot;
    public float Acc;
    public bool Seeded;

    public VanillaDanglyState(int verts) { Cur = new Vector3[verts]; Prev = new Vector3[verts]; Vel = new Vector3[verts]; }
}

/// <summary>The stock dangly solve, exactly (docs/spec/dangly-motions.md, section 2), stepped at a fixed rate
/// and drawn interpolated so it looks the same at any frame rate.</summary>
public static class VanillaDangly
{
    /// <summary>The fixed step: what a 60 fps machine runs.</summary>
    public const float StepDt = 1f / 60f;

    /// <summary>Steps one frame may run before the rest of the accumulated time is dropped: a machine slower
    /// than 15 fps falls behind in dangly time rather than spending the frame catching up.</summary>
    public const int MaxSteps = 4;

    /// <summary>A world unit of part movement between calls is a teleport, not a swing.</summary>
    private const float TeleportDist = 1f;

    /// <summary>The runaway bound the stock solve resets a vertex at.</summary>
    private const float Blown = 1e10f;

    internal const float ConstraintMax = 255f;

    /// <summary>The stock solve's gains on its two terms: half the constraint times tightness as the spring,
    /// one and a half times period as the damping.</summary>
    internal const float StiffnessGain = 0.5f, DampingGain = 1.5f;

    /// <summary>What the stock solve multiplies the area's wind vector by before it drifts a vertex.</summary>
    internal const float WindScale = 20f;

    /// <summary>Advance <paramref name="s"/> by <paramref name="dt"/> and write the drawn positions (node space)
    /// into <paramref name="dst"/>. Returns the largest vertex move against the previous draw, or
    /// <see cref="float.PositiveInfinity"/> when the part teleported.</summary>
    public static float Run(ReadOnlySpan<Vector3> rest, ReadOnlySpan<float> constraints, float displacement,
                            float tightness, float period, VanillaDanglyState s, Vector3 worldPos, Quaternion worldRot,
                            Vector3 wind, float dt, Span<Vector3> dst)
    {
        int n = Math.Min(Math.Min(rest.Length, constraints.Length), Math.Min(dst.Length, s.Cur.Length));
        if (n <= 0) return 0f;

        if (!s.Seeded)
        {
            for (int i = 0; i < n; i++) { s.Cur[i] = s.Prev[i] = rest[i]; s.Vel[i] = default; }
            s.PrevPos = worldPos;
            s.PrevRot = worldRot;
            s.Acc = 0f;
            s.Seeded = true;
        }

        if ((worldPos - s.PrevPos).Length() > TeleportDist)
        {
            // The velocity survives: the stock solve copies rest over the dynamic positions and leaves it.
            for (int i = 0; i < n; i++) { s.Cur[i] = s.Prev[i] = rest[i]; dst[i] = rest[i]; }
            s.PrevPos = worldPos;
            s.PrevRot = worldRot;
            s.Acc = 0f;
            return float.PositiveInfinity;
        }

        if (float.IsFinite(dt) && dt > 0f) s.Acc += dt;
        int steps = 0;
        while (s.Acc >= StepDt && steps < MaxSteps) { s.Acc -= StepDt; steps++; }
        if (s.Acc >= StepDt) s.Acc %= StepDt;

        Vector3 fromPos = s.PrevPos;
        Quaternion fromRot = s.PrevRot;
        for (int k = 0; k < steps; k++)
        {
            float frac = (k + 1f) / steps;
            Vector3 toPos = Vector3.Lerp(s.PrevPos, worldPos, frac);
            Quaternion toRot = Quaternion.Slerp(s.PrevRot, worldRot, frac);
            s.Cur.AsSpan(0, n).CopyTo(s.Prev.AsSpan(0, n));
            One(rest, constraints, displacement, tightness, period, s, n,
                fromPos, fromRot, toPos, toRot, wind);
            fromPos = toPos;
            fromRot = toRot;
        }
        // The pose is stored only once a step has consumed the movement: a frame too short to step leaves the
        // delta to the next one rather than dropping the motion compensation.
        if (steps > 0) { s.PrevPos = worldPos; s.PrevRot = worldRot; }

        float alpha = Math.Clamp(s.Acc / StepDt, 0f, 1f);
        float maxSq = 0f;
        for (int i = 0; i < n; i++)
        {
            Vector3 p = Vector3.Lerp(s.Prev[i], s.Cur[i], alpha);
            float moved = (p - dst[i]).LengthSquared();
            if (moved > maxSq) maxSq = moved;
            dst[i] = p;
        }
        return MathF.Sqrt(maxSq);
    }

    /// <summary>One fixed step of the stock per-vertex solve, between two poses of the part.</summary>
    private static void One(ReadOnlySpan<Vector3> rest, ReadOnlySpan<float> constraints, float displacement,
                            float tightness, float period, VanillaDanglyState s, int n,
                            Vector3 fromPos, Quaternion fromRot, Vector3 toPos, Quaternion toRot, Vector3 wind)
    {
        Quaternion inv = Quaternion.Conjugate(toRot);
        Quaternion d = inv * fromRot;
        Vector3 tLocal = Vector3.Transform(fromPos - toPos, Quaternion.Conjugate(fromRot));
        Vector3 wind20 = Vector3.Transform(wind, inv) * WindScale;
        float spring = StiffnessGain * tightness * StepDt, damp = DampingGain * period * StepDt;

        for (int i = 0; i < n; i++)
        {
            float c = constraints[i];
            if (!(c > 0f)) continue;              // the stock solve never writes these; nor do we

            Vector3 r = rest[i];
            Vector3 p = Vector3.Transform(s.Cur[i] + tLocal, d);
            Vector3 v = Vector3.Transform(s.Vel[i], d);
            v += (r - p) * (c * spring) - v * damp;
            s.Vel[i] = v;
            p += v * StepDt;
            float cap = displacement * (1f - c / ConstraintMax);
            p += wind20 * cap;

            Vector3 off = p - r;
            bool blown = off.X < -Blown || off.Y < -Blown || off.Z < -Blown
                      || off.X > Blown || off.Y > Blown || off.Z > Blown;
            s.Cur[i] = blown ? r
                             : new Vector3(Axis(r.X, p.X, off.X, cap), Axis(r.Y, p.Y, off.Y, cap),
                                           Axis(r.Z, p.Z, off.Z, cap));
        }
    }

    /// <summary>One axis of the stock excursion clamp, its comparisons in the engine's order: a NaN offset
    /// fails the first test and lands on the upper bound, exactly as the engine's does.</summary>
    private static float Axis(float rest, float p, float off, float cap)
        => !(off <= cap) ? rest + cap
         : !(-cap <= off) ? rest - cap
         : p;
}
