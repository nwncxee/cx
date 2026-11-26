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
    /// <summary>A fog colour no stage starts from, to see it stay out of the head.</summary>
    private static readonly Vector3 Dusky = new(0.1f, 0.2f, 0.3f);

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
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
        BlueFogLeavesMist(check);
        MistGoesUp(check);
        FogLaysNoFloor(check);
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

    // Given the city by the water at night, the mist gathered
    // When the fog stage takes its upload
    // Then the column over the water carries the mist the air holds there, and a street far from the water carries none.
    private static void MistGoesUp(Checker check)
    {
        Enter(City, Quay(), 0f, true);
        Go(Gather, Vector2.Zero);
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        Vector4 head = MemoryMarshal.Read<Vector4>(Sent);
        float water = Carried(head, 10.5f, 0.5f), street = Carried(head, -15.5f, 0.5f), held = AirScene.MistAt(10.5f, 0.5f);
        check.Expect(held > 0f && water == held && street == 0f,
            $"the upload carries {water:F4} m over the water where the air holds {held:F4} m, and {street:F4} m on the street (want the same, some, none)");
    }

    /// <summary>The mist depth the upload in <see cref="Sent"/> carries for the column at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static float Carried(Vector4 head, float x, float y)
    {
        int c = ((int)MathF.Floor(y - head.Y)) * GroundMist.Side + (int)MathF.Floor(x - head.X);
        return MemoryMarshal.Cast<byte, GroundMist.Cell>(Sent.AsSpan(GroundMist.Head))[c].Mist;
    }

    // Given a meadow by day at fog amount 10
    // When the field steps, and then mist is placed beside the player
    // Then the dust stage idles before the placement, and after it the mist lies only where it was placed.
    private static void FogLaysNoFloor(Checker check)
    {
        Enter(Meadow, Flat(), 10f, false);
        for (int n = 0; n < 60; n++) GroundMist.Step(Frame, Vector2.Zero);
        bool idle = !AirScene.Lies;
        GroundMist.Place(new Vector3(0.5f, 0.5f, 0f), 2f, 1f);
        Go(1f, Vector2.Zero);
        float beside = Near(AirLayer.NightMist, 0.5f, 0.5f, 1f), far = Near(AirLayer.NightMist, 30f, 0f, 4f);
        check.Expect(idle && beside > 0f && far == 0f,
            $"at fog amount 10 the stage idles {idle} before any placement; the placement lays {beside:F4} m beside it and {far:F4} m 30 m off (want true, some, none)");
    }

    // Given the night mist uploaded
    // When the sky's fog colour changes with the air unchanged
    // Then nothing goes again: the stage lights the mist by the area's ambient and moon, and the head carries the mist's height.
    private static void SkyGoesUp(Checker check)
    {
        Enter(City, Quay(), 0f, true);
        Go(5f, Vector2.Zero);
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        Vector3 was = FrameInputs.FogColor;
        FrameInputs.FogColor = Dusky;
        int again = GroundMist.Pack(ref gen, Sent);
        FrameInputs.FogColor = was;
        Vector4 slab = MemoryMarshal.Read<Vector4>(Sent.AsSpan(GroundMist.SlabAt));
        check.Expect(again == 0 && slab.W == AirScene.MistTop,
            $"a new fog colour sends {again} bytes and the head carries the mist {slab.W} m deep (want none, {AirScene.MistTop})");
    }

    // Given a city by the water at night whose builder tints the fog a saturated blue, under a grey ambient and a pale moon
    // When the player looks across the mist
    // Then the mist takes the hue of the light the ground takes, the ambient and the moon, not the fog's blue.
    private static void BlueFogLeavesMist(Checker check)
    {
        Enter(City, Quay(), 0f, true);
        Go(30f, Vector2.Zero);
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        var field = new AirDustMarch.Field(Sent);
        var room = new AirDustMarch.Shine(new Vector3(0.04f, 0.04f, 0.05f), new Vector3(0.02f, 0.05f, 0.6f), new Vector3(0.05f, 0.06f, 0.1f));
        (_, Vector3 added) = field.March(new Vector3(4f, 0.3f, 1.7f), new Vector3(16f, 0.3f, 0f), room, ReadOnlySpan<AirDustMarch.Point>.Empty);
        Vector3 ground = room.Ambient + room.Diffuse;
        check.Expect(added.X > 0f && added.Z / added.X <= 1.05f * ground.Z / ground.X,
            $"under a blue fog the mist adds {added.X:F4},{added.Y:F4},{added.Z:F4}, {added.Z / added.X:F2} times as much blue as red where the ground's light has {ground.Z / ground.X:F2} (want the ground's hue)");
    }
}
