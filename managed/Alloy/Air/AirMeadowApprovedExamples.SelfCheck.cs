using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>A meadow's wisps drift overhead and its pollen glints by day. Oracle, by hand: the heights a person looks
/// at (the ground, 60 m up), the wind each script sets, and a twin run with no walker or blast.</summary>
internal static class AirMeadowApprovedExamples
{
    /// <summary>Where the wisps are read: high overhead.</summary>
    private const float Overhead = 60f;

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirMeadowApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        OpenGrass(check);
        PollenByDay(check);
        NoPollenAtNight(check);
        WispsStretch(check);
        WalkLeavesWisps(check);
        PollenDownwind(check);
        BlastPollen(check);
        PollenSwirls(check);
        WispsMoveCalm(check);
        log.Info(check.Ok ? "AirMeadowApprovedExamples.SelfCheck: passed" : "AirMeadowApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A meadow by day or by night, three still seconds after entry.</summary>
    private static void Field(bool night)
    {
        Enter(Meadow, Flat(), 0f, night);
        Go(3f, Vector2.Zero);
    }

    // Given an outdoor area of open grass with no fog placeable, by day
    // When the player stands in it
    // Then nothing lies on the ground, and pale wisps drift high overhead.
    private static void OpenGrass(Checker check)
    {
        Field(false);
        float ground = Near(Lying, 0f, 0f, 8f), low = Thick(AirLayer.Wisps, 0f, 0f, 8f, 1f), high = Thick(AirLayer.Wisps, 0f, 0f, 8f, Overhead);
        check.Expect(ground == 0f && low == 0f && high > 0f, $"E57 the meadow lays {ground:F4} m on its ground, wisps {low:F4} at head height and {high:F4} overhead (want none, none, some)");
    }

    // Given the same meadow in sunlight
    // When the player looks around them
    // Then pollen glints in the air.
    private static void PollenByDay(Checker check)
    {
        Field(false);
        int pollen = Within(AirLayer.Pollen, new Vector3(0f, 0f, 1f), 8f);
        check.Expect(pollen > 0, $"E58 {pollen} grains of pollen float round the player by day (want some)");
    }

    // Given the same meadow at night
    // When the player looks around them
    // Then no pollen shows.
    private static void NoPollenAtNight(Checker check)
    {
        Field(true);
        check.Expect(AirScene.Particles(AirLayer.Pollen).Length == 0, $"E59 {AirScene.Particles(AirLayer.Pollen).Length} grains of pollen at night (want none)");
    }

    // Given wisps over a meadow
    // When the wind is strong
    // Then the wisps stretch along the wind's direction and move with it.
    private static void WispsStretch(Checker check)
    {
        Field(false);
        Go(30f, Vector2.Zero);
        float calm = AirScene.Wisps.Stretch.Length();
        Go(60f, Strong);
        Vector2 stretch = AirScene.Wisps.Stretch, from = AirScene.Wisps.Carried;
        Go(10f, Strong);
        Vector2 moved = AirScene.Wisps.Carried - from;
        check.Expect(stretch.Length() > calm && MathF.Abs(Vector2.Dot(Vector2.Normalize(stretch), Vector2.Normalize(Strong))) > 0.9f,
            $"E60 the wisps stretch {stretch} in the strong wind against {calm:F3} calm (want longer, along {Strong})");
        check.Expect(Vector2.Dot(moved, Vector2.Normalize(Strong)) > 0f, $"E60 the wisps move {moved} in ten seconds (want along {Strong})");
    }

    // Given wisps over a meadow
    // When the player walks through the meadow
    // Then the wisps are unchanged by the walk.
    private static void WalkLeavesWisps(Checker check)
    {
        int frames = Pace(12f);
        Field(false);
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, new Vector3(-6f, 0f, 0f), true);
        Walk(world, who, new Vector3(-6f, 0f, 0f), new Vector3(6f, 0f, 0f), true, frames);
        AirWisps walked = AirScene.Wisps;
        MistPartingApprovedExamples.Drop(world, who);
        Field(false);
        world = MistPartingApprovedExamples.World();
        who = MistPartingApprovedExamples.Seat(world, new Vector3(-6f, 0f, 0f), true);
        Still(world, frames);
        AirWisps stood = AirScene.Wisps;
        MistPartingApprovedExamples.Drop(world, who);
        check.Expect(Vector2.Distance(walked.Carried, stood.Carried) < 1e-4f && Vector2.Distance(walked.Stretch, stood.Stretch) < 1e-4f,
            $"E62 after the walk the wisps sit at {walked.Carried}, stretched {walked.Stretch}; standing, {stood.Carried}, {stood.Stretch} (want the same)");
    }

    // Given pollen glinting over a meadow
    // When the wind is strong
    // Then the pollen drifts along the wind's direction, and a gust front crossing the field carries it along.
    private static void PollenDownwind(Checker check)
    {
        Field(false);
        Go(0.3f, Strong);
        Vector3 front = Drift(AirLayer.Pollen, new Vector3(0f, 0f, 1f), 8f);
        Go(60f, Strong);
        Vector3 steady = Drift(AirLayer.Pollen, new Vector3(0f, 0f, 1f), 8f);
        check.Expect(steady.X > 0f && steady.X > MathF.Abs(steady.Y), $"E129 in a steady strong wind the pollen drifts at {steady} (want along {Strong})");
        check.Expect(front.X > steady.X, $"E129 the front carries the pollen at {front.X:F3} m/s against {steady.X:F3} m/s steady (want carried along)");
    }

    // Given pollen in the air over a meadow
    // When a fireball detonates there
    // Then the pollen near the blast is thrown outward, and the wisps overhead are unchanged.
    private static void BlastPollen(Checker check)
    {
        var at = new Vector3(2f, 0f, Knee);
        Field(false);
        Go(0.2f, Vector2.Zero);
        AirWisps calm = AirScene.Wisps;
        Field(false);
        AirScene.Blast(at, 4f);
        Go(0.2f, Vector2.Zero);
        float outward = Outward(AirLayer.Pollen, at, 4f);
        AirWisps blasted = AirScene.Wisps;
        check.Expect(outward > Slow, $"E132 the blast throws the pollen outward at {outward:F3} m/s (want thrown)");
        check.Expect(Vector2.Distance(blasted.Carried, calm.Carried) < 1e-4f && Vector2.Distance(blasted.Stretch, calm.Stretch) < 1e-4f,
            $"E132 the wisps sit at {blasted.Carried} after the blast and {calm.Carried} without (want the same)");
    }

    // Given pollen around the player in a meadow
    // When they walk through it
    // Then pollen beside their path swirls aside and settles back.
    private static void PollenSwirls(Checker check)
    {
        Field(false);
        int before = Within(AirLayer.Pollen, new Vector3(0f, 0f, 1f), 3f);
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, new Vector3(-6f, 0f, 0f), true);
        Walk(world, who, new Vector3(-6f, 0f, 0f), new Vector3(6f, 0f, 0f), true, Pace(12f));
        float swirl = Fastest(AirLayer.Pollen, new Vector3(4f, 0f, 1f), 2f);
        Still(world, Frames(10f));
        float settled = Fastest(AirLayer.Pollen, new Vector3(0f, 0f, 1f), 3f);
        int back = Within(AirLayer.Pollen, new Vector3(0f, 0f, 1f), 3f);
        MistPartingApprovedExamples.Drop(world, who);
        check.Expect(swirl > Slow, $"E133 the pollen beside the path swirls at {swirl:F3} m/s (want swirled aside)");
        check.Expect(settled < Slow && back >= (int)(0.8f * before) && before > 0, $"E133 ten seconds on it drifts at {settled:F3} m/s, {back} grains against {before} (want settled back)");
    }

    // Given a meadow in calm air
    // When ten seconds pass with the player standing still
    // Then the wisps have moved.
    private static void WispsMoveCalm(Checker check)
    {
        Field(false);
        Vector2 from = AirScene.Wisps.Carried;
        Go(10f, Vector2.Zero);
        check.Expect(Vector2.Distance(AirScene.Wisps.Carried, from) > 0.1f, $"E134 the wisps move from {from} to {AirScene.Wisps.Carried} in ten calm seconds (want moved)");
    }
}
