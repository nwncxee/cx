using System;
using System.Numerics;

namespace CX.Services.Camera;

/// <summary>A self-check world of axis-aligned boxes, hit where a segment enters one and, from inside it, where it
/// leaves: the two faces a two-sided mesh shows a ray.</summary>
internal struct BoxWorld
{
    internal (Vector3 Lo, Vector3 Hi)[] Boxes;

    public readonly bool Cast(Vector3 from, Vector3 to, out Vector3 hit)
    {
        hit = default;
        float best = float.MaxValue;
        Vector3 d = to - from;
        foreach (var (lo, hi) in Boxes)
            if (Enter(from, d, lo, hi, out float t) && t < best) best = t;
        if (best == float.MaxValue) return false;
        hit = from + d * best;
        return true;
    }

    /// <summary>The segment fraction where <paramref name="o"/> + t <paramref name="d"/> enters the box, or leaves it
    /// when it starts inside; false when it misses within (0, 1].</summary>
    private static bool Enter(Vector3 o, Vector3 d, Vector3 lo, Vector3 hi, out float t)
    {
        float t0 = 0f, t1 = 1f;
        for (int a = 0; a < 3; a++)
        {
            float oa = o[a], da = d[a];
            if (MathF.Abs(da) < 1e-9f)
            {
                if (oa < lo[a] || oa > hi[a]) { t = 0f; return false; }
                continue;
            }
            float u = (lo[a] - oa) / da, v = (hi[a] - oa) / da;
            if (u > v) (u, v) = (v, u);
            t0 = MathF.Max(t0, u);
            t1 = MathF.Min(t1, v);
            if (t0 > t1) { t = 0f; return false; }
        }
        t = t0 > 0f ? t0 : t1;
        return t > 0f && t <= 1f;
    }
}
