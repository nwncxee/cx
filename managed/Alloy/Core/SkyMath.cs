using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary>The overhead sky view's fit: world to its clip space, and the two affine terms that turn a
/// stored device depth back into a world height. Produced by <see cref="SkyMath.Fit"/>.</summary>
internal readonly struct SkyFit
{
    /// <summary>World -&gt; the overhead view's clip space, as the depth layer was rendered.</summary>
    public readonly Matrix4x4 Vp;
    /// <summary>World z at device depth 0 (the view's near plane, above everything).</summary>
    public readonly float ZAt0;
    /// <summary>Metres of world z per unit of device depth -- negative, the view looks down.</summary>
    public readonly float ZPerDepth;
    /// <summary>Texture-coordinate units per world metre; the fit is square, so one number.</summary>
    public readonly float UvPerMetre;
    /// <summary>The fit's centre and half-extent in world units.</summary>
    public readonly Vector3 Centre;
    public readonly float Radius;

    internal SkyFit(Matrix4x4 vp, float zAt0, float zPerDepth, float uvPerMetre, Vector3 centre, float radius)
    { Vp = vp; ZAt0 = zAt0; ZPerDepth = zPerDepth; UvPerMetre = uvPerMetre; Centre = centre; Radius = radius; }

    /// <summary>A fit was never computed (no area extent, or the term is off).</summary>
    public bool IsNone => Radius <= 0f;
}

/// <summary>Pure sky-visibility math: the overhead view's fit and the horizon estimate the bake runs at
/// every texel of it. No engine or GL deps, exercised headless by <see cref="SelfCheck"/>.
/// <c>cx_skybake.glsl</c> computes the same openness and must agree with <see cref="Openness"/>.</summary>
internal static partial class SkyMath
{
    /// <summary>Azimuths the sweep walks, evenly spaced from +X. Even, so an alley's two walls are sampled
    /// symmetrically; the set is fixed, so nothing here needs a dither or TAA to settle.</summary>
    internal const int Dirs = 8;
    /// <summary>Radii per azimuth, geometric from <see cref="NearM"/> out to the sweep's range: a near step
    /// finds a roof, a far one finds a wall across the street.</summary>
    internal const int Steps = 4;
    /// <summary>The nearest radius, in metres. Everything inside it belongs to the contact-scale AO
    /// (<c>ao.radius</c>, 0.6 m), so the two terms meet rather than overlap.</summary>
    internal const float NearM = 1f;
    /// <summary>The farthest radius, in metres: far enough to find the building across a street, near
    /// enough that the map's own texels still resolve it. Baked into the map, so not a live dial.</summary>
    internal const float RangeM = 16f;
    /// <summary>The fit's floor half-extent, in metres. A small area would otherwise get a shallow depth
    /// range (the ortho fit spans its own radius below the centre), and a pit or a tower would clamp.</summary>
    internal const float MinRadiusM = 48f;

    /// <summary>The overhead view looks straight down, so the stored depth is an affine function of world
    /// z and the sweep needs no per-tap unprojection.</summary>
    internal static readonly Vector3 Down = new(0f, 0f, -1f);

    /// <summary>The <paramref name="k"/>-th sweep radius in metres, out to <paramref name="rangeM"/>.</summary>
    internal static float Radius(int k, float rangeM)
        => NearM * MathF.Pow(MathF.Max(rangeM, NearM) / NearM, k / (float)(Steps - 1));

    /// <summary>The <paramref name="j"/>-th azimuth as a unit vector.</summary>
    internal static Vector2 Dir(int j)
    {
        float a = j * (MathF.Tau / Dirs);
        return new Vector2(MathF.Cos(a), MathF.Sin(a));
    }

    /// <summary>The share of the sky a point at height <paramref name="z"/> sees, given the top-surface
    /// height at each of <see cref="Dirs"/> x <see cref="Steps"/> taps around it (azimuth-major).
    /// Per azimuth the taps give a horizon tangent; a cosine-weighted hemisphere above elevation h is
    /// cos^2(h) of the whole, so the term is the mean of 1/(1+tan^2) over the azimuths.</summary>
    internal static float Openness(float z, ReadOnlySpan<float> heights, float rangeM)
    {
        float sum = 0f;
        for (int j = 0; j < Dirs; j++)
        {
            float tmax = 0f;
            for (int k = 0; k < Steps; k++)
            {
                float e = (heights[j * Steps + k] - z) / Radius(k, rangeM);
                if (e > tmax) tmax = e;
            }
            sum += 1f / (1f + tmax * tmax);
        }
        return sum / Dirs;
    }

    /// <summary>The ambient scale a fragment takes, from the baked column (<paramref name="open"/> at
    /// <paramref name="dz"/> metres below the fragment's probe point) and the look terms. Mirrored by
    /// <c>CxSkyView</c> in <c>cx_skyview.glsl</c>; the two must agree.</summary>
    internal static float Term(float dz, float open, float strength, float stepM, float coverM, float riseM)
    {
        float v = Lerp(open, 1f, Saturate((dz - stepM) / MathF.Max(riseM, 1e-3f)));
        v *= 1f - Saturate(-dz / MathF.Max(coverM, 1e-3f));
        return 1f - strength * (1f - v);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float Saturate(float v) => v < 0f ? 0f : v > 1f ? 1f : v;

    /// <summary>The overhead fit for an area of <paramref name="extent"/> metres whose tile (0,0) starts at
    /// the world origin, rendered into a <paramref name="mapSize"/>-texel square map. Zero extent gives
    /// <see cref="SkyFit.IsNone"/>.</summary>
    internal static SkyFit Fit(Vector2 extent, int mapSize)
    {
        if (!(extent.X > 0f) || !(extent.Y > 0f) || mapSize <= 0) return default;
        float radius = MathF.Max(MathF.Max(extent.X, extent.Y) * 0.5f, MinRadiusM);
        var centre = new Vector3(extent.X * 0.5f, extent.Y * 0.5f, 0f);
        // upX: the view looks straight down, so the light basis takes world +X as its up -- passed rather
        // than latched, since this direction never moves.
        Matrix4x4 vp = ShadowMath.DirectionalLightViewProj(Down, centre, radius, radius, mapSize, upX: true);
        if (!Matrix4x4.Invert(vp, out Matrix4x4 inv)) return default;
        float z0 = Unproject(inv, -1f), z1 = Unproject(inv, 1f);
        return new SkyFit(vp, z0, z1 - z0, 0.5f / radius, centre, radius);
    }

    /// <summary>Where <paramref name="p"/> lands in the fit's map, in texture coordinates. Outside [0,1] is
    /// outside the map.</summary>
    internal static Vector2 Uv(in SkyFit fit, Vector3 p)
    {
        Vector4 c = Vector4.Transform(new Vector4(p, 1f), fit.Vp);
        return new Vector2(c.X / c.W, c.Y / c.W) * 0.5f + new Vector2(0.5f);
    }

    /// <summary>The world height a stored device depth stands for.</summary>
    internal static float HeightAt(in SkyFit fit, float deviceDepth) => fit.ZAt0 + deviceDepth * fit.ZPerDepth;

    /// <summary>World z on the view axis at clip z <paramref name="clipZ"/> (-1 = device depth 0).</summary>
    private static float Unproject(Matrix4x4 inv, float clipZ)
    {
        Vector4 w = Vector4.Transform(new Vector4(0f, 0f, clipZ, 1f), inv);
        return w.Z / w.W;
    }
}
