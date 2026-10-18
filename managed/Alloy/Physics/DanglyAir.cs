using System;
using System.Numerics;

namespace CX.Alloy.Physics;

/// <summary>The air the automatic dangly motions move in (docs/spec/dangly-motions.md, section 5a): a mean
/// wind in metres per second, gust fronts the mean wind carries across the area, and turbulence on the
/// scale of a bush. Pure and allocation-free; sampled once per joint per frame.</summary>
public static class DanglyAir
{
    /// <summary>Metres per second per unit of the engine's base wind: an area set to weak wind blows 1 unit
    /// (<c>CNWArea::SetWind</c>), a light breeze; strong blows 2, a fresh one.</summary>
    public const float SpeedPerUnit = 3.5f;

    /// <summary>Metres per second per unit of the engine's point-source wind: a moving creature pushes
    /// <c>creaturewindintensity</c> (2.5) units at its centre, tapering to nothing a metre out.</summary>
    public const float PointSpeed = 6f;

    /// <summary>How far a gust swings the mean speed either way, and how strong the turbulence is against
    /// the gusting speed.</summary>
    internal const float GustDepth = 0.6f, TurbDepth = 0.35f;

    /// <summary>Gust fronts: three wavenumbers (rad/m) along the wind with a cross-wind skew each, weighted
    /// to sum to 1 -- fronts 30, 13 and 6 m apart, so a field of bushes takes a gust as a travelling wave.</summary>
    private static readonly Vector4[] Fronts =
    {
        new(0.21f, 0.13f, 0f, 0.55f), new(0.47f, -0.29f, 1.3f, 0.3f), new(1.1f, 0.7f, 4.1f, 0.15f),
    };

    /// <summary>Turbulence: three eddies from 0.4 to 2.2 Hz, each a plane wave of its own direction (xyz,
    /// rad/m) and rate (w, rad/s), weighted to sum to 1, so neighbouring cards do not move as one.</summary>
    private static readonly Vector4[] Eddies =
    {
        new(0.7f, 0.9f, 0.5f, 2.7f), new(-1.5f, 1.9f, 0.8f, 6.3f), new(3.3f, -2.0f, 3.6f, 14.1f),
    };
    private static readonly float[] EddyWeight = { 0.5f, 0.3f, 0.2f };

    /// <summary>The air's velocity at <paramref name="at"/> (world, metres per second) at time
    /// <paramref name="t"/> seconds: <paramref name="mean"/> carried through the gust envelope, plus
    /// turbulence scaled by <paramref name="field"/> (0 = the steady mean, 1 = the full field).</summary>
    public static Vector3 At(Vector3 at, float t, Vector3 mean, float field)
    {
        var flat = new Vector2(mean.X, mean.Y);
        float s = flat.Length();
        if (!(s > 1e-4f)) return Vector3.Zero;
        Vector2 d = flat / s;
        var p = new Vector2(-d.Y, d.X);
        float along = at.X * d.X + at.Y * d.Y, across = at.X * p.X + at.Y * p.Y;
        float speed = s * (1f + field * GustDepth * Gust(along - s * t, across));
        float turb = field * TurbDepth * speed;
        var dir = new Vector3(d.X, d.Y, 0f);
        var side = new Vector3(p.X, p.Y, 0f);
        return dir * (speed + turb * Eddy(at, t, 0f)) + side * (turb * Eddy(at, t, 2.1f))
             + Vector3.UnitZ * (0.3f * turb * Eddy(at, t, 4.7f));
    }

    /// <summary>The gust envelope at a point <paramref name="carried"/> metres down the wind from where the
    /// pattern started (so <c>along - speed * t</c>): in [-1, 1], and frozen in the moving air.</summary>
    internal static float Gust(float carried, float across)
    {
        float g = 0f;
        foreach (Vector4 f in Fronts) g += f.W * MathF.Sin(f.X * carried + f.Y * across + f.Z);
        return g;
    }

    /// <summary>One axis of the turbulence, in [-1, 1]; <paramref name="phase"/> decorrelates the axes.</summary>
    private static float Eddy(Vector3 at, float t, float phase)
    {
        float e = 0f;
        for (int i = 0; i < Eddies.Length; i++)
        {
            Vector4 k = Eddies[i];
            e += EddyWeight[i] * MathF.Sin(k.W * t + k.X * at.X + k.Y * at.Y + k.Z * at.Z + phase * (i + 1));
        }
        return e;
    }
}
