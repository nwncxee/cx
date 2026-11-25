using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The night mist's plain regressions: the client's night reaching it, the stage drawing it where the place has
/// no dust, the sky it is lit by, and which floors are water. Oracle, by hand: stock surfacemat.2da's rows, the quay and
/// stream laid tile by tile, and the head layout the dust stage declares.</summary>
internal static class AirWaterMistRegressions
{
    /// <summary>A fog colour no stage starts from, to see it reach the head.</summary>
    private static readonly Vector3 Dusky = new(0.1f, 0.2f, 0.3f);

    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirWaterMistRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        WaterRows(check);
        WetFollowsFloor(check);
        FrameNight(check);
        StreamDrawn(check);
        PlacedDrawn(check);
        PlacedRefilled(check);
        SkyGoesUp(check);
        log.Info(check.Ok ? "AirWaterMistRegressions.SelfCheck: passed" : "AirWaterMistRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given surfacemat.2da's stock rows
    // When each is asked whether it is open water
    // Then Water and DeepWater are, Stone is not, and a row past the table is not.
    private static void WaterRows(Checker check)
    {
        bool water = TileBuild.Water(MistFixture.Water), deep = TileBuild.Water(DeepWater), stone = TileBuild.Water(Stone), past = TileBuild.Water(64 + MistFixture.Water);
        check.Expect(water && deep && !stone && !past,
            $"Water {water}, DeepWater {deep}, Stone {stone}, row {64 + MistFixture.Water} {past} (want true, true, false, false)");
    }

    // Given a pool laid with the player standing in it
    // When the player drops far below it
    // Then the pool's columns hold no floor and are no longer water.
    private static void WetFollowsFloor(Checker check)
    {
        Lay(Stream(), Vector3.Zero, true, 0f);
        bool before = GroundMist.WetAt(0.5f, 0.5f);
        GroundMist.Follow(new Vector3(0f, 0f, -10f));
        bool after = GroundMist.WetAt(0.5f, 0.5f);
        check.Expect(before && !after, $"the stream is water {before} with the player in it, {after} once the player is 10 m below (want true, false)");
    }

    // Given the city by the water entered by day
    // When the renderer frames it with the client's night flag set
    // Then mist gathers over the water.
    private static void FrameNight(Checker check)
    {
        Enter(City, Quay(), 0f, false);
        for (int n = 1; n <= (int)(Gather / Frame); n++) AirScene.Frame(default(DbId), n, false, true, Frame, Vector2.Zero);
        float water = Near(AirLayer.NightMist, 10f, 0f, 4f);
        check.Expect(water > 0.02f, $"framed {Gather} s with the client's night set, the water holds {water:F4} m (want mist)");
    }

    // Given a meadow with a stream, at night
    // When the renderer asks whether the dust stage runs, and the stage takes its first upload
    // Then it runs and draws, though a meadow lays no dust.
    private static void StreamDrawn(Checker check)
    {
        Enter(Meadow with { Water = true }, Stream(), 0f, true);
        Go(5f, Vector2.Zero);
        check.Expect(AirScene.Lies && Drawn(), $"a meadow's stream at night runs the dust stage {AirScene.Lies}, drawn {Drawn()} (want both)");
    }

    // Given a meadow by day
    // When mist is placed on it before the air steps
    // Then the dust stage runs at once and draws it.
    private static void PlacedDrawn(Checker check)
    {
        Enter(Meadow, Flat(), 0f, false);
        GroundMist.Place(new Vector3(0.5f, 0.5f, 0f), 2f, 1f);
        check.Expect(AirScene.Lies && Drawn(), $"mist placed on a meadow by day runs the dust stage {AirScene.Lies}, drawn {Drawn()} (want both)");
    }

    // Given mist placed on a meadow by day
    // When the area's fog is set again before the air steps, filling the field evenly over the placed mist
    // Then the dust stage does not run.
    private static void PlacedRefilled(Checker check)
    {
        Enter(Meadow, Flat(), 0f, false);
        GroundMist.Place(new Vector3(0.5f, 0.5f, 0f), 2f, 1f);
        GroundMist.AreaFog(true, 0f);
        check.Expect(!AirScene.Lies, "a meadow's placed mist filled over by its fog still runs the dust stage (want it idle)");
    }

    // Given the night mist uploaded under one fog colour
    // When the sky's fog colour changes with the air unchanged
    // Then the upload goes again, its head carrying the new colour and the mist's height.
    private static void SkyGoesUp(Checker check)
    {
        Enter(City, Quay(), 0f, true);
        Go(5f, Vector2.Zero);
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent, Vector3.Zero);
        int again = GroundMist.Pack(ref gen, Sent, Dusky);
        Vector4 sky = MemoryMarshal.Read<Vector4>(Sent.AsSpan(GroundMist.Head / 2));
        check.Expect(again > 0 && sky == new Vector4(Dusky, AirScene.MistTop),
            $"a new fog colour sends {again} bytes with sky {sky} (want an upload carrying <{Dusky}, {AirScene.MistTop}>)");
    }
}
