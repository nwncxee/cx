using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The area's wind drifts the mist and goes around walls. Oracle, by hand: the strong wind
/// <see cref="MistFixture.HighWind"/> read off <c>CNWArea::SetWind</c>, Nonwalk walls laid out here, and where the
/// centre of a pool of mist must have gone.</summary>
internal static class MistWindApprovedExamples
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistWindApprovedExamples.SelfCheck");
        bool on = VolumetricFog.Enabled.V;
        string quality = VolumetricFog.Quality.V;
        try
        {
            VolumetricFog.Enabled.V = true;
            VolumetricFog.Quality.V = "medium";
            Downwind(check);
            LongWall(check);
            ShortWall(check);
            Still(check);
            Turn(check);
            BoxEdge(check);
            VolumetricFog.Quality.V = "low";
            Lowest(check);
        }
        finally
        {
            VolumetricFog.Enabled.V = on;
            VolumetricFog.Quality.V = quality;
            GroundMist.AreaEdge();
        }
        log.Info(check.Ok ? "MistWindApprovedExamples.SelfCheck: passed" : "MistWindApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Flat stone with a Nonwalk wall over x 8..10 m and |y| &lt; <paramref name="halfLength"/>.</summary>
    private static System.Collections.Generic.List<Tile> Walled(float halfLength)
        => Ground(160f, 1f, (_, _) => 0f, (x, y) => x >= 8f && x < 10f && MathF.Abs(y) < halfLength ? Nonwalk : Stone);

    private static void Downwind(Checker check)
    {
        // Given an area with wind set to high and mist on the ground
        // When ten seconds pass
        // Then the mist has moved downwind
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        Run(10f, HighWind);
        Vector2 c = Centroid(100f, 0f, out float m);
        Vector2 down = Vector2.Normalize(HighWind);
        float along = Vector2.Dot(c, down), across = MathF.Abs(c.X * down.Y - c.Y * down.X);
        check.Expect(m > 0f && along > 1f && across < along * 0.5f,
            $"E1 after 10 s of high wind the pool's centre is {along:F3} m downwind and {across:F3} m across (want over 1 m down, under half that across, of {m:F3} m^3)");
    }

    private static void LongWall(Checker check)
    {
        // Given the same wind and a wall across its path
        // When the mist reaches the wall
        // Then it banks up on the windward side and stays thin behind it
        Lay(Walled(40f), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        Run(20f, HighWindX);
        float bank = Mean(6f, -3f, 8f, 3f), start = Mean(-2f, -3f, 2f, 3f), behind = Mean(10f, -3f, 16f, 3f);
        check.Expect(bank > start, $"E2 against the wall the mist lies {bank:F4} m deep, where it was placed {start:F4} (want it banked deeper)");
        check.Expect(bank > 0f && behind * 4f < bank, $"E2 behind the wall it lies {behind:F4} m, against it {bank:F4} (want under a quarter)");
    }

    private static void ShortWall(Checker check)
    {
        // Given a short wall across the wind
        // When ten seconds pass
        // Then mist flows past both ends and is thicker there than straight behind the wall's middle
        Lay(Walled(3f), Vector3.Zero, false, 0f);
        for (float y = -12f; y <= 12f; y += 2f) GroundMist.Place(new Vector3(0f, y, 0f), 2f, 1f);
        Run(10f, HighWindX);
        float left = Mean(11f, -8f, 15f, -4f), right = Mean(11f, 4f, 15f, 8f), middle = Mean(11f, -2f, 15f, 2f);
        check.Expect(left > middle && right > middle,
            $"E3 past the wall's ends the mist lies {left:F4} and {right:F4} m, straight behind its middle {middle:F4} (want both ends thicker)");
    }

    private static void Still(Checker check)
    {
        // Given an area with no wind
        // When ten seconds pass
        // Then the mist does not drift
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        Run(10f, Vector2.Zero);
        Vector2 c = Centroid(100f, 0f, out float m);
        check.Expect(m > 0f && c.Length() < 0.1f, $"E4 after 10 s with no wind the pool's centre moved {c.Length():F3} m (want under 0.1, of {m:F3} m^3)");
    }

    private static void Turn(Checker check)
    {
        // Given drifting mist
        // When the area's wind changes direction
        // Then the mist drifts the new way within ten seconds
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        Run(5f, HighWindX);
        var north = new Vector2(0f, 2f);
        Run(7f, north);
        Vector2 a = Centroid(100f, 0f, out float m);
        Run(3f, north);
        Vector2 d = Centroid(100f, 0f, out _) - a;
        check.Expect(m > 0f && d.Y > 0.3f && d.Y > MathF.Abs(d.X),
            $"E5 from 7 to 10 s after the wind turns north the pool moves ({d.X:F3}, {d.Y:F3}) m (want over 0.3 m north, more north than east)");
    }

    /// <summary>The world columns the walk watches: x from <see cref="FirstColumn"/>, one a metre, wider than the field
    /// at both ends of the walk. A pop is a jump of the whole depth; a pool 1 m deep and 3 m wide drifting at the
    /// 2 m/s wind changes a column by about 0.011 m a frame, so 5% of the depth is the bound.</summary>
    private const int Columns = 211;
    /// <inheritdoc cref="Columns"/>
    private const float FirstColumn = -79.5f, PoolDepth = 1f;

    private static void BoxEdge(Checker check)
    {
        // Given drifting mist
        // When the player walks 50 m
        // Then no mist pops, vanishes or jumps at the edge of the air box, and mist outside the box still draws
        Fogged(Flat(), Vector3.Zero);
        Run(1f, HighWind);
        float floor = GroundMist.At(0.5f, 0.5f), worst = 0f, outside = float.MaxValue;
        GroundMist.Place(new Vector3(10f, 0f, 0f), 3f, PoolDepth);
        Span<float> was = stackalloc float[Columns];
        for (int n = 0; n <= 1500; n++)
        {
            float px = n * (50f / 1500f);
            GroundMist.Follow(new Vector3(px, 0f, 0f));
            GroundMist.Step(Frame, HighWind);
            for (int k = 0; k < was.Length; k++)
            {
                float d = GroundMist.At(FirstColumn + k, 0.5f);
                if (n > 0) worst = MathF.Max(worst, MathF.Abs(d - was[k]));
                was[k] = d;
            }
            outside = MathF.Min(outside, GroundMist.At(px + 100.5f, 0.5f));
        }
        check.Expect(floor > 0f && worst <= (floor + PoolDepth) * 0.05f,
            $"E6 over 50 m a world column's mist changed by up to {worst:F4} m in one frame (want at most 5% of the {floor:F4} m floor and the {PoolDepth} m pool)");
        check.Expect(outside > 0f, $"E6 100 m ahead of the player, outside the air box, the mist lies {outside:F4} m deep at least (want some)");
    }

    /// <summary>The lowest quality still lays and pools mist and holds it still. Its cost clause, that the fog volume
    /// costs what it does with no air box, is measured by hand with the frame profiler, not here.</summary>
    private static void Lowest(Checker check)
    {
        // Given high wind and mist on the ground
        // When the mist quality is set to its lowest
        // Then the mist still lies and pools as before but does not drift, and the fog volume costs what it does with no air box
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        Run(10f, HighWind);
        Vector2 c = Centroid(100f, 0f, out float m);
        check.Expect(m > 0f && c.Length() < 0.1f, $"E7 at the lowest quality 10 s of high wind move the pool {c.Length():F3} m (want under 0.1, of {m:F3} m^3)");
        Lay(MistPoolApprovedExamples.SlopeGround(), new Vector3(10f, 0f, -2f), false, 0f);
        GroundMist.Place(new Vector3(2f, 0f, 0f), 2f, 1f);
        Run(5f, HighWind);
        float top = Mass(-1f, -5f, 6f, 5f), bottom = Mass(20f, -10f, 40f, 10f);
        check.Expect(bottom > top, $"E7 at the lowest quality the slope's bottom holds {bottom:F3} m^3, its top {top:F3} (want it still pooling at the bottom)");
    }
}
