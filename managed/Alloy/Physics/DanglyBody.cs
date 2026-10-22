using System;
using System.Numerics;

namespace CX.Alloy.Physics;

/// <summary>The body a worn strand keeps out of: one capsule per rigid part of its wearer, in world space,
/// refilled every frame from the parts' own boxes. Fixed capacity, so the refill allocates nothing.</summary>
public sealed class DanglyBody
{
    public const int MaxCapsules = 32;

    /// <summary>How far a joint stays off a capsule's surface: the thickness of the strand around it.</summary>
    internal const float Clearance = 0.03f;

    /// <summary>A part thinner than this (world units) is a finger, an eye or a buckle, nothing a strand rests
    /// on; longer than <see cref="MaxLength"/>, a weapon or an effect, not a limb.</summary>
    internal const float MinRadius = 0.04f, MaxLength = 1.5f;

    internal readonly Vector3[] A = new Vector3[MaxCapsules], B = new Vector3[MaxCapsules];
    internal readonly float[] R = new float[MaxCapsules];
    public int Count;
    /// <summary>Parts offered past <see cref="MaxCapsules"/> since the last <see cref="Clear"/>.</summary>
    public int Dropped;

    public void Clear() { Count = 0; Dropped = 0; }

    /// <summary>Add the capsule inscribed along the longest axis of a part's own-space box
    /// <paramref name="lo"/>..<paramref name="hi"/>, its radius the box's thinnest half-width. False for a
    /// part too thin or too long to be a limb, or once the body is full.</summary>
    public bool AddBox(Vector3 lo, Vector3 hi, in Matrix4x4 world)
    {
        Vector3 half = (hi - lo) * 0.5f;
        if (!(half.X >= 0f && half.Y >= 0f && half.Z >= 0f)) return false;
        Vector3 axis = half.X >= half.Y && half.X >= half.Z ? Vector3.UnitX
                     : half.Y >= half.Z ? Vector3.UnitY : Vector3.UnitZ;
        float len = Vector3.Dot(half, axis);
        float r = axis.X > 0f ? MathF.Min(half.Y, half.Z) : axis.Y > 0f ? MathF.Min(half.X, half.Z)
                                                                     : MathF.Min(half.X, half.Y);
        float scale = new Vector3(world.M11, world.M12, world.M13).Length();
        if (!(r * scale >= MinRadius) || !(2f * len * scale <= MaxLength)) return false;
        if (Count >= MaxCapsules) { Dropped++; return false; }
        Vector3 c = (lo + hi) * 0.5f, reach = axis * (len - r);
        A[Count] = Vector3.Transform(c - reach, world);
        B[Count] = Vector3.Transform(c + reach, world);
        R[Count] = r * scale;
        Count++;
        return true;
    }

    /// <summary>Every moving joint back out of each capsule it entered. A capsule the joint's own rest place
    /// <paramref name="target"/> is already inside -- the scalp a strand grows from -- does not hold it.</summary>
    internal void Push(Span<Vector3> x, ReadOnlySpan<Vector3> target, ReadOnlySpan<int> parent, int nj)
    {
        for (int j = 0; j < nj; j++)
        {
            if (parent[j] < 0) continue;
            for (int i = 0; i < Count; i++)
            {
                float rr = R[i] + Clearance;
                if ((target[j] - Closest(A[i], B[i], target[j])).LengthSquared() < rr * rr) continue;
                Vector3 q = Closest(A[i], B[i], x[j]);
                Vector3 d = x[j] - q;
                float len = d.Length();
                if (len >= rr) continue;
                if (len < 1e-6f)
                {
                    d = target[j] - q;
                    len = d.Length();
                    if (len < 1e-6f) continue;
                }
                x[j] = q + d * (rr / len);
            }
        }
    }

    /// <summary>The point of segment <paramref name="a"/>..<paramref name="b"/> nearest <paramref name="p"/>.</summary>
    internal static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
    {
        Vector3 ab = b - a;
        float l2 = ab.LengthSquared();
        float t = l2 > 1e-12f ? Math.Clamp(Vector3.Dot(p - a, ab) / l2, 0f, 1f) : 0f;
        return a + ab * t;
    }
}
