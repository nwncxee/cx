using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

internal static partial class SkyMath
{
    /// <summary>Headless check of the sky-visibility math, in three parts whose oracles their own summaries
    /// name: the sweep's ladder, the horizon estimate over four analytic height fields, and the overhead
    /// fit's frustum. Every expected value is a literal worked out by hand from plane geometry and from the
    /// ortho box <see cref="ShadowMath.DirectionalLightViewProj"/> documents.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        CheckLadder(check);
        CheckOpenness(check);
        CheckFit(check);
        log.Info(check.Ok ? "SkyMath.SelfCheck: passed" : "SkyMath.SelfCheck: FAILED");
    }

    /// <summary>The range the openness oracle below was worked out at: at 8 m its radii come out whole.</summary>
    private const float OracleRangeM = 8f;

    /// <summary>Oracle, by hand: a geometric ladder from 1 m gives 1/2/4/8 m at range 8 and
    /// 1 / 16^(1/3) / 16^(2/3) / 16 at the shipped reach; azimuth 0 points along +X and azimuth 2 a quarter
    /// turn round, along +Y.</summary>
    private static void CheckLadder(Checker check)
    {
        check.Expect(MathF.Abs(Radius(0, OracleRangeM) - 1f) < 1e-4f && MathF.Abs(Radius(1, OracleRangeM) - 2f) < 1e-4f
                  && MathF.Abs(Radius(2, OracleRangeM) - 4f) < 1e-4f && MathF.Abs(Radius(3, OracleRangeM) - 8f) < 1e-4f,
                     "at range 8 the radii are 1, 2, 4, 8 m");
        check.Expect(MathF.Abs(Radius(0, RangeM) - 1f) < 1e-4f && MathF.Abs(Radius(1, RangeM) - 2.5198421f) < 1e-4f
                  && MathF.Abs(Radius(2, RangeM) - 6.3496042f) < 1e-3f && MathF.Abs(Radius(3, RangeM) - 16f) < 1e-3f,
                     "at the shipped 16 m reach they are 1, 2.5198, 6.3496, 16 m");
        Vector2 d0 = Dir(0), d2 = Dir(2);
        check.Expect(MathF.Abs(d0.X - 1f) < 1e-4f && MathF.Abs(d0.Y) < 1e-4f
                  && MathF.Abs(d2.X) < 1e-4f && MathF.Abs(d2.Y - 1f) < 1e-4f, "azimuth 0 is +X and azimuth 2 is +Y");
    }

    /// <summary>Oracle, by hand: open ground sees the whole sky; a roof 3 m up puts the horizon tangent at
    /// 3/1, so the cosine-weighted share is 1/(1+9); and in a 3 m alley between 6 m walls the two azimuths
    /// facing a wall read 0.1, the two along it 1 and the four diagonals 1/3.25, averaging 0.428846.</summary>
    private static void CheckOpenness(Checker check)
    {
        Span<float> flat = stackalloc float[Dirs * Steps];
        check.Expect(MathF.Abs(Openness(0f, flat, OracleRangeM) - 1f) < 1e-5f, "open ground sees the whole sky");

        Span<float> roof = stackalloc float[Dirs * Steps];
        roof.Fill(3f);
        check.Expect(MathF.Abs(Openness(0f, roof, OracleRangeM) - 0.1f) < 1e-4f, "under a roof 3 m up the term is 0.1");

        Span<float> alley = stackalloc float[Dirs * Steps];
        Alley(alley, 1.5f, 6f);
        float v = Openness(0f, alley, OracleRangeM);
        check.Expect(MathF.Abs(v - 0.428846f) < 1e-4f, "a 3 m alley between 6 m walls sees 0.428846 of the sky");

        Span<float> wall = stackalloc float[Dirs * Steps];
        for (int k = 0; k < Steps; k++) wall[k] = Radius(k, OracleRangeM) >= 1.5f ? 6f : 0f;
        float one = Openness(0f, wall, OracleRangeM) * Dirs - (Dirs - 1);
        check.Expect(MathF.Abs(one - 0.1f) < 1e-3f, "the azimuth facing a 6 m wall 1.5 m away sees 0.1");

        Span<float> tight = stackalloc float[Dirs * Steps];
        Alley(tight, 0.5f, 6f);
        check.Expect(Openness(0f, tight, OracleRangeM) < v, "a narrower alley sees less sky");
        Span<float> tall = stackalloc float[Dirs * Steps];
        Alley(tall, 1.5f, 12f);
        check.Expect(Openness(0f, tall, OracleRangeM) < v, "taller walls on the same alley see less sky");

        Span<float> pit = stackalloc float[Dirs * Steps];
        pit.Fill(-20f);
        check.Expect(MathF.Abs(Openness(0f, pit, OracleRangeM) - 1f) < 1e-5f, "a drop on every side takes no sky");
    }

    /// <summary>Every tap's height for an alley running along Y: a floor at 0, and walls
    /// <paramref name="high"/> metres tall from |x| &gt;= <paramref name="half"/> outward.</summary>
    private static void Alley(Span<float> heights, float half, float high)
    {
        for (int j = 0; j < Dirs; j++)
            for (int k = 0; k < Steps; k++)
                heights[j * Steps + k] = MathF.Abs(Dir(j).X * Radius(k, OracleRangeM)) >= half ? high : 0f;
    }

    /// <summary>Oracle, by hand: a 320 m area is radius 160, and the ortho box is 2r across with an upstream
    /// margin of r, so world z runs from centre + r + m = 320 at device depth 0 down to centre - r = -160 at
    /// depth 1, one texture-coordinate unit spans 2r = 320 m, and 32 m is a tenth of the map either way.</summary>
    private static void CheckFit(Checker check)
    {
        SkyFit f = Fit(new Vector2(320f, 320f), 1024);
        check.Expect(MathF.Abs(f.Radius - 160f) < 1e-3f, "a 320 m area fits at radius 160");
        check.Expect(MathF.Abs(f.ZAt0 - 320f) < 1e-2f, "device depth 0 is world z 320");
        check.Expect(MathF.Abs(f.ZPerDepth - -480f) < 1e-2f, "the stored range is 480 m, downward");
        check.Expect(MathF.Abs(HeightAt(f, 1f) - -160f) < 1e-2f, "device depth 1 is world z -160");
        check.Expect(MathF.Abs(f.UvPerMetre - 0.003125f) < 1e-7f, "0.003125 texture-coordinate units per metre");
        Vector2 mid = Uv(f, f.Centre);
        check.Expect((mid - new Vector2(0.5f)).Length() < 1e-4f, "the area's centre lands at uv (0.5, 0.5)");
        Vector2 ax = Uv(f, f.Centre + new Vector3(32f, 0f, 0f)) - mid;
        Vector2 ay = Uv(f, f.Centre + new Vector3(0f, 32f, 0f)) - mid;
        check.Expect(MathF.Abs(ax.Length() - 0.1f) < 1e-4f && MathF.Abs(ay.Length() - 0.1f) < 1e-4f,
                     "32 m is 0.1 of the map either way");
        check.Expect(MathF.Abs(Vector2.Dot(ax, ay)) < 1e-5f, "the two world axes land perpendicular on the map");
        Vector2 up = Uv(f, f.Centre + new Vector3(0f, 0f, 50f));
        check.Expect((up - mid).Length() < 1e-5f, "height does not move a point on the map -- the view looks straight down");
        check.Expect(MathF.Abs(Fit(new Vector2(40f, 40f), 1024).Radius - MinRadiusM) < 1e-3f,
                     "a 40 m area takes the fit's floor radius, so its depth range clears a pit or a tower");
        check.Expect(Fit(Vector2.Zero, 1024).IsNone && Fit(new Vector2(320f, 320f), 0).IsNone,
                     "no extent and no map are both 'no fit'");
    }
}
