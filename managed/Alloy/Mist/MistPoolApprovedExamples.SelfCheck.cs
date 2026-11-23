using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>Ground mist pools in low ground and stops at walls. Oracle, by hand: walkmeshes laid out here (a 4 m slope,
/// a Nonwalk strip, a pond of Water and DeepWater, a deck 5 m over a road) and the mass each region should hold.</summary>
internal static class MistPoolApprovedExamples
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistPoolApprovedExamples.SelfCheck");
        bool on = VolumetricFog.Enabled.V;
        try
        {
            VolumetricFog.Enabled.V = true;
            Slope(check);
            Wall(check);
            Pond(check);
            Bridge(check);
            LongWalk(check);
            AreaRoundTrip(check);
        }
        finally
        {
            VolumetricFog.Enabled.V = on;
            GroundMist.AreaEdge();
        }
        log.Info(check.Ok ? "MistPoolApprovedExamples.SelfCheck: passed" : "MistPoolApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Flat stone at 0 m, falling 4 m over x 0..20 m to a flat bottom.</summary>
    internal static System.Collections.Generic.List<Tile> SlopeGround()
        => Ground(160f, 2f, (x, _) => x < 0f ? 0f : x > 20f ? -4f : -0.2f * x, (_, _) => Stone);

    private static void Slope(Checker check)
    {
        // Given mist is placed at the top of a slope
        // When a few seconds pass
        // Then the mist gathers at the bottom of the slope and thins at the top
        Lay(SlopeGround(), new Vector3(10f, 0f, -2f), false, 0f);
        GroundMist.Place(new Vector3(2f, 0f, 0f), 2f, 1f);
        float topBefore = Mass(-1f, -5f, 6f, 5f);
        Run(5f, Vector2.Zero);
        float top = Mass(-1f, -5f, 6f, 5f), bottom = Mass(20f, -10f, 40f, 10f);
        check.Expect(topBefore > 0f && top < topBefore, $"E1 the top holds {topBefore:F3} m^3 when placed and {top:F3} after 5 s (want some, then less)");
        check.Expect(bottom > top, $"E1 after 5 s the bottom holds {bottom:F3} m^3, the top {top:F3} (want more at the bottom)");
    }

    private static void Wall(Checker check)
    {
        // Given mist is placed beside a wall the player cannot walk through
        // When the mist spreads
        // Then no mist appears on the far side of the wall
        Lay(Ground(160f, 1f, (_, _) => 0f, (x, _) => x >= 4f && x < 6f ? Nonwalk : Stone), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 2f, 1f);
        Run(10f, Vector2.Zero);
        float near = Mass(-4f, -4f, 4f, 4f), far = Mass(6f, -40f, 40f, 40f);
        check.Expect(near > 0f && far == 0f, $"E2 after 10 s this side of the wall holds {near:F3} m^3, the far side {far:F4} (want some, and none)");
    }

    private static void Pond(Checker check)
    {
        // Given mist is placed at a pond's shore
        // When it spreads
        // Then it lies over the water as it lies over ground, and does not stop at the shoreline
        Lay(Ground(160f, 1f, (_, _) => 0f, (x, _) => x < 0f ? Stone : x < 2f ? MistFixture.Water : DeepWater), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 2f, 1f);
        Run(10f, Vector2.Zero);
        float water = Mean(1f, -2f, 4f, 2f), land = Mean(-4f, -2f, -1f, 2f);
        check.Expect(water > 0f && MathF.Abs(water - land) <= land * 0.2f,
            $"E3 after 10 s the water holds {water:F4} m deep, the shore's land {land:F4} (want mist over the water, within 20% of the land's)");
    }

    /// <summary>A stone road at 0 m with a stone bridge deck at 5 m over the strip |y| &lt; 3, x -20..20 m.</summary>
    private static System.Collections.Generic.List<Tile> BridgeGround()
    {
        var faces = Flat();
        Deck(faces, -20f, -3f, 20f, 3f, 5f, StoneBridge);
        return faces;
    }

    /// <summary>The road's mist outside the deck's strip.</summary>
    private static float Road() => Mass(-40f, 4f, 40f, 40f) + Mass(-40f, -40f, 40f, -4f);

    private static void Bridge(Checker check)
    {
        // Given the player stands on a bridge over a road and mist is placed on both
        // When it settles
        // Then the mist lies on the bridge deck, and none of it drains down onto the road
        Lay(BridgeGround(), new Vector3(0f, 0f, 5f), false, 0f);
        GroundMist.Place(new Vector3(0f, 0f, 5f), 2f, 1f);
        GroundMist.Place(new Vector3(0f, 20f, 0f), 2f, 1f);
        Run(10f, Vector2.Zero);
        float deck = Mass(-20f, -3f, 20f, 3f), road = Road(), floor = GroundMist.FloorAt(0f, 0f);
        Lay(BridgeGround(), new Vector3(0f, 0f, 5f), false, 0f);
        GroundMist.Place(new Vector3(0f, 20f, 0f), 2f, 1f);
        Run(10f, Vector2.Zero);
        float roadAlone = Road();
        check.Expect(floor == 5f && deck > 0f, $"E4 the field lays its mist on a floor at {floor} m (want the deck's 5 m) and the deck holds {deck:F3} m^3 (want some)");
        check.Expect(roadAlone > 0f && MathF.Abs(road - roadAlone) <= roadAlone * 0.01f,
            $"E4 the road holds {road:F3} m^3 with the deck's mist and {roadAlone:F3} without it (want the same: none drained)");
    }

    private static void LongWalk(Checker check)
    {
        // Given the player walks 200 m across an area
        // When the mist field moves with the player
        // Then mist already pooled stays where it pooled in the world, and no hard straight edge of mist shows at the field's border
        Fogged(Ground(240f, 2f, (_, _) => 0f, (_, _) => Stone), new Vector3(-100f, 0f, 0f));
        Run(1f, Vector2.Zero);
        float floor = GroundMist.At(-99.5f, 0.5f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        Run(10f, Vector2.Zero);
        float worst = 0f, least = float.MaxValue;
        for (int n = 0; n <= 2400; n++)
        {
            GroundMist.Follow(new Vector3(-100f + n * (200f / 2400f), 0f, 0f));
            GroundMist.Step(Frame, Vector2.Zero);
            if (n % 240 != 0) continue;
            Vector2 c = Centroid(10f, floor, out float m);
            worst = MathF.Max(worst, c.Length());
            least = MathF.Min(least, m);
        }
        check.Expect(floor > 0f && least > 0f && worst < 0.5f,
            $"E5 over 200 m the pool's centre wandered {worst:F3} m (want under 0.5) and its mass fell to {least:F3} m^3 above a floor of {floor:F4} m (want both above 0)");
        float jump = 0f, last = GroundMist.At(200.5f, 0.5f);
        for (float x = 201.5f; x < 240f; x += 1f)
        {
            float d = GroundMist.At(x, 0.5f);
            jump = MathF.Max(jump, MathF.Abs(d - last));
            last = d;
        }
        check.Expect(floor > 0f && jump <= floor * 0.05f, $"E5 past the field's border a metre steps the mist by up to {jump:F4} m (want at most 5% of the {floor:F4} m floor)");
    }

    private static void AreaRoundTrip(Checker check)
    {
        // Given mist has pooled in area A
        // When the player goes to area B and comes back
        // Then none of A's mist showed in B, and A's mist settles again from the start
        Fogged(SlopeGround(), new Vector3(10f, 0f, -2f));
        Run(10f, Vector2.Zero);
        float fresh = GroundMist.At(30.5f, 0.5f);
        Fogged(SlopeGround(), new Vector3(10f, 0f, -2f));
        GroundMist.Place(new Vector3(2f, 0f, 0f), 2f, 1f);
        Run(10f, Vector2.Zero);
        float pooled = GroundMist.At(30.5f, 0.5f);
        Lay(Flat(), Vector3.Zero, false, 0f);
        Run(1f, Vector2.Zero);
        float inB = Mass(-40f, -40f, 40f, 40f);
        Fogged(SlopeGround(), new Vector3(10f, 0f, -2f));
        Run(10f, Vector2.Zero);
        float back = GroundMist.At(30.5f, 0.5f);
        check.Expect(pooled > fresh, $"E6 the pool in A lies {pooled:F4} m deep at the bottom, a fresh A {fresh:F4} (want the pool deeper)");
        check.Expect(inB == 0f, $"E6 area B holds {inB:F4} m^3 of A's mist (want none)");
        check.Expect(fresh > 0f && back == fresh, $"E6 back in A the bottom lies {back:F4} m deep, a fresh A {fresh:F4} (want the same: settled from the start)");
    }
}
