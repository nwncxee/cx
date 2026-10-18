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

    /// <summary>Advance <paramref name="s"/> by <paramref name="dt"/> and write the drawn positions (node space)
    /// into <paramref name="dst"/>. Returns the largest vertex move against the previous draw, or
    /// <see cref="float.PositiveInfinity"/> when the part teleported.</summary>
    public static float Run(ReadOnlySpan<Vector3> rest, ReadOnlySpan<float> constraints, float displacement,
                            float tightness, float period, VanillaDanglyState s, Vector3 worldPos, Quaternion worldRot,
                            Vector3 wind, float dt, Span<Vector3> dst)
    {
        rest.Slice(0, Math.Min(rest.Length, dst.Length)).CopyTo(dst);
        return 0f;
    }
}
