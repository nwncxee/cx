using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

internal static partial class SkyMath
{
    /// <summary>Headless check of the sky-visibility math. Oracle: plane geometry worked out by hand off the
    /// sweep's specification (8 azimuths from +X, radii 1/2/4/8 m at range 8) over open ground, a roof 3 m
    /// up and a 3 m alley between 6 m walls, plus the fit's near/far heights and texels per metre derived by
    /// hand from the ortho box <see cref="ShadowMath.DirectionalLightViewProj"/> documents.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        const float RangeM = 8f;

        // --- the sweep's ladder, against the numbers the oracle below was worked out with
        check.Expect(Dirs == 8 && Steps == 4, "the sweep is 8 azimuths x 4 radii");
        check.Expect(MathF.Abs(Radius(0, RangeM) - 1f) < 1e-4f && MathF.Abs(Radius(1, RangeM) - 2f) < 1e-4f
                  && MathF.Abs(Radius(2, RangeM) - 4f) < 1e-4f && MathF.Abs(Radius(3, RangeM) - 8f) < 1e-4f,
                     "at range 8 the radii are 1, 2, 4, 8 m");
        Vector2 d0 = Dir(0), d2 = Dir(2);
        check.Expect(MathF.Abs(d0.X - 1f) < 1e-4f && MathF.Abs(d0.Y) < 1e-4f
                  && MathF.Abs(d2.X) < 1e-4f && MathF.Abs(d2.Y - 1f) < 1e-4f, "azimuth 0 is +X and azimuth 2 is +Y");

        // --- open ground: every tap level with the point, so the whole sky is visible
        Span<float> flat = stackalloc float[Dirs * Steps];
        check.Expect(MathF.Abs(Openness(0f, flat, RangeM) - 1f) < 1e-5f, "open ground sees the whole sky");

        // --- a roof 3 m up, filling every tap: the nearest radius is 1 m, so the horizon tangent is 3 and
        // the cosine-weighted share is 1/(1+9) = 0.1
        Span<float> roof = stackalloc float[Dirs * Steps];
        roof.Fill(3f);
        check.Expect(MathF.Abs(Openness(0f, roof, RangeM) - 0.1f) < 1e-4f, "under a roof 3 m up the term is 0.1");

        // --- a 3 m alley between 6 m walls, running along Y: walls at |x| >= 1.5, floor at 0.
        // By hand: +-X find the wall at r = 2 (tangent 3 -> 0.1 each); +-Y never leave the floor (1 each);
        // the four diagonals first reach |x| 1.5 at r = 4 (tangent 1.5 -> 1/3.25 = 0.307692 each).
        // (2 x 0.1 + 2 x 1 + 4 x 0.307692) / 8 = 0.428846.
        Span<float> alley = stackalloc float[Dirs * Steps];
        for (int j = 0; j < Dirs; j++)
            for (int k = 0; k < Steps; k++)
                alley[j * Steps + k] = MathF.Abs(Dir(j).X * Radius(k, RangeM)) >= 1.5f ? 6f : 0f;
        float v = Openness(0f, alley, RangeM);
        check.Expect(MathF.Abs(v - 0.428846f) < 1e-4f, "a 3 m alley between 6 m walls sees 0.428846 of the sky");

        // ...and the single azimuth that looks straight at a wall, on its own
        Span<float> wall = stackalloc float[Dirs * Steps];
        for (int k = 0; k < Steps; k++) wall[k] = Radius(k, RangeM) >= 1.5f ? 6f : 0f;
        float one = Openness(0f, wall, RangeM) * Dirs - (Dirs - 1);
        check.Expect(MathF.Abs(one - 0.1f) < 1e-3f, "the azimuth facing a 6 m wall 1.5 m away sees 0.1");

        // --- narrower is darker, taller is darker: the two directions a builder would reach for
        Span<float> tight = stackalloc float[Dirs * Steps];
        for (int j = 0; j < Dirs; j++)
            for (int k = 0; k < Steps; k++)
                tight[j * Steps + k] = MathF.Abs(Dir(j).X * Radius(k, RangeM)) >= 0.5f ? 6f : 0f;
        check.Expect(Openness(0f, tight, RangeM) < v, "a narrower alley sees less sky");
        Span<float> tall = stackalloc float[Dirs * Steps];
        for (int i = 0; i < alley.Length; i++) tall[i] = alley[i] > 0f ? 12f : 0f;
        check.Expect(Openness(0f, tall, RangeM) < v, "taller walls on the same alley see less sky");
        // ...and geometry BELOW the point never takes sky away
        Span<float> pit = stackalloc float[Dirs * Steps];
        pit.Fill(-20f);
        check.Expect(MathF.Abs(Openness(0f, pit, RangeM) - 1f) < 1e-5f, "a drop on every side takes no sky");

        // --- the fit. A 320 x 320 m area at 1024 texels: radius 160, the ortho box 2r across with an
        // upstream margin of r, so world z runs from centre + r + m = 320 at device depth 0 down to
        // centre - r = -160 at depth 1, and one texture-coordinate unit spans 2r = 320 m.
        SkyFit f = Fit(new Vector2(320f, 320f), 1024);
        check.Expect(MathF.Abs(f.Radius - 160f) < 1e-3f, "a 320 m area fits at radius 160");
        check.Expect(MathF.Abs(f.ZAt0 - 320f) < 1e-2f, "device depth 0 is world z 320");
        check.Expect(MathF.Abs(f.ZPerDepth - -480f) < 1e-2f, "the stored range is 480 m, downward");
        check.Expect(MathF.Abs(HeightAt(f, 1f) - -160f) < 1e-2f, "device depth 1 is world z -160");
        check.Expect(MathF.Abs(f.UvPerMetre - 0.003125f) < 1e-7f, "0.003125 texture-coordinate units per metre");
        Vector2 mid = Uv(f, f.Centre);
        check.Expect((mid - new Vector2(0.5f)).Length() < 1e-4f, "the area's centre lands at uv (0.5, 0.5)");
        // 32 m along either world axis is 0.1 of the map, and the two land on PERPENDICULAR axes of it
        Vector2 ax = Uv(f, f.Centre + new Vector3(32f, 0f, 0f)) - mid;
        Vector2 ay = Uv(f, f.Centre + new Vector3(0f, 32f, 0f)) - mid;
        check.Expect(MathF.Abs(ax.Length() - 0.1f) < 1e-4f && MathF.Abs(ay.Length() - 0.1f) < 1e-4f,
                     "32 m is 0.1 of the map either way");
        check.Expect(MathF.Abs(Vector2.Dot(ax, ay)) < 1e-5f, "the two world axes land perpendicular on the map");
        check.Expect(MathF.Abs(Uv(f, f.Centre + new Vector3(0f, 0f, 50f)).X - mid.X) < 1e-5f
                  && MathF.Abs(Uv(f, f.Centre + new Vector3(0f, 0f, 50f)).Y - mid.Y) < 1e-5f,
                     "height does not move a point on the map -- the view looks straight down");
        // ...the floor under a small area, so its depth range still clears a pit or a tower
        check.Expect(MathF.Abs(Fit(new Vector2(40f, 40f), 1024).Radius - MinRadiusM) < 1e-3f,
                     "a 40 m area takes the fit's floor radius");
        check.Expect(Fit(Vector2.Zero, 1024).IsNone && Fit(new Vector2(320f, 320f), 0).IsNone,
                     "no extent and no map are both 'no fit'");

        log.Info(check.Ok ? "SkyMath.SelfCheck: passed" : "SkyMath.SelfCheck: FAILED");
    }
}
