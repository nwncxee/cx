using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>Motes float in a crypt's lamplight. Oracle, by hand: the lamp's place and reach as the fixture seats it, the
/// wind each script sets, and walking and drifting speeds chosen off a person's pace (a drift is under 0.5 m/s).</summary>
internal static class AirMotesApprovedExamples
{
    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirMotesApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        InTheLight(check);
        NoLampNone(check);
        OwnPaths(check);
        FrontEmpties(check);
        SteadyDrift(check);
        TurnDrift(check);
        WalkerSwirl(check);
        Fireball(check);
        HeldOutdoors(check);
        Unhold();
        log.Info(check.Ok ? "AirMotesApprovedExamples.SelfCheck: passed" : "AirMotesApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A crypt room with its lamp, settled three seconds under <paramref name="wind"/>.</summary>
    private static void Lit(Vector2 wind)
    {
        EnterWith(Crypt, Room(), 0f, false, false, true);
        Go(3f, wind);
    }

    // Given a crypt room with one lamp
    // When the player looks across the room
    // Then motes float in the lamp's light, and none show in the corners the lamp does not reach.
    private static void InTheLight(Checker check)
    {
        Lit(Vector2.Zero);
        int inside = Within(AirLayer.Motes, Lamp, LampReach), all = AirScene.Particles(AirLayer.Motes).Length;
        check.Expect(inside > 0 && inside == all, $"E53 {inside} of {all} motes float in the lamp's light (want some, and none outside it)");
    }

    // Given a crypt room lit only by its ambient light
    // When the player looks across it
    // Then no motes show.
    private static void NoLampNone(Checker check)
    {
        EnterWith(Crypt, Room(), 0f, false, false, false);
        Go(3f, Vector2.Zero);
        check.Expect(AirScene.Particles(AirLayer.Motes).Length == 0, $"E54 a room with no lamp holds {AirScene.Particles(AirLayer.Motes).Length} motes (want none)");
    }

    // Given motes in a lamp's light in still air
    // When ten seconds pass
    // Then each mote has moved, slowly and on a path of its own.
    private static void OwnPaths(Checker check)
    {
        Lit(Vector2.Zero);
        AirParticle[] before = AirScene.Particles(AirLayer.Motes).ToArray();
        Go(10f, Vector2.Zero);
        ReadOnlySpan<AirParticle> after = AirScene.Particles(AirLayer.Motes);
        check.Expect(before.Length > 1 && after.Length == before.Length, $"E55 Given: {before.Length} motes, {after.Length} ten seconds on");
        if (after.Length != before.Length || before.Length < 2) return;
        int still = 0, fast = 0;
        float spread = 0f;
        Vector3 first = after[0].Pos - before[0].Pos;
        for (int i = 0; i < before.Length; i++)
        {
            Vector3 d = after[i].Pos - before[i].Pos;
            if (d.Length() < 0.05f) still++;
            if (d.Length() > Slow * 10f) fast++;
            spread = MathF.Max(spread, Vector3.Distance(d, first));
        }
        check.Expect(still == 0 && fast == 0, $"E55 {still} motes stayed put and {fast} moved fast over ten seconds (want every one moved, slowly)");
        check.Expect(spread > 0.1f, $"E55 the motes' paths differ by up to {spread:F3} m (want each its own)");
    }

    // Given motes in a lamp's light
    // When a gust front passes
    // Then the motes race off in the wind's direction, and the lamp's light is empty of them until the air calms.
    private static void FrontEmpties(Checker check)
    {
        Lit(Vector2.Zero);
        Go(0.3f, Strong);
        Vector3 race = Drift(AirLayer.Motes, Lamp, LampReach);
        Go(2.7f, Strong);
        int gusting = Within(AirLayer.Motes, Lamp, LampReach);
        Go(10f, Vector2.Zero);
        int calm = Within(AirLayer.Motes, Lamp, LampReach);
        check.Expect(race.X > Slow && race.X > MathF.Abs(race.Y), $"E56 the front sends the motes off at {race} (want racing along {Strong})");
        check.Expect(gusting == 0 && calm > 0, $"E56 the light holds {gusting} motes in the gust, {calm} once calm (want none, then some)");
    }

    // Given motes in a lamp's light and a strong wind that has blown for a minute
    // When another ten seconds pass
    // Then the motes drift downwind through the light and do not race off, and the light still holds motes.
    private static void SteadyDrift(Checker check)
    {
        Lit(Strong);
        Go(60f, Strong);
        Go(10f, Strong);
        Vector3 drift = Drift(AirLayer.Motes, Lamp, LampReach);
        float fastest = Fastest(AirLayer.Motes, Lamp, LampReach);
        int held = Within(AirLayer.Motes, Lamp, LampReach);
        check.Expect(held > 0 && drift.X > 0f && fastest < Slow, $"E124 {held} motes drift at {drift}, the fastest {fastest:F3} m/s (want some, downwind, none racing)");
    }

    // Given motes in a lamp's light in a strong east wind
    // When a script turns the wind to the north at the same strength
    // Then the motes drift the new way and do not race off.
    private static void TurnDrift(Checker check)
    {
        Lit(FromEast);
        Go(60f, FromEast);
        Go(10f, FromNorth);
        Vector3 drift = Drift(AirLayer.Motes, Lamp, LampReach);
        float fastest = Fastest(AirLayer.Motes, Lamp, LampReach);
        check.Expect(drift.Y < 0f && MathF.Abs(drift.Y) > MathF.Abs(drift.X) && fastest < Slow,
            $"E125 after the turn the motes drift at {drift}, the fastest {fastest:F3} m/s (want south, none racing)");
    }

    // Given motes in a lamp's light
    // When the player walks through that light and stops
    // Then the motes along their path swirl for a few seconds before drifting slowly again.
    private static void WalkerSwirl(Checker check)
    {
        Lit(Vector2.Zero);
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, new Vector3(0f, 0f, 0f), true);
        Walk(world, who, Vector3.Zero, new Vector3(8f, 0f, 0f), true, Pace(8f));
        float swirl = Fastest(AirLayer.Motes, new Vector3(6f, 0f, 1f), 2f);
        Still(world, Frames(6f));
        float settled = Fastest(AirLayer.Motes, Lamp, LampReach);
        MistPartingApprovedExamples.Drop(world, who);
        check.Expect(swirl > Slow && settled < Slow, $"E126 the motes on the path swirl at {swirl:F3} m/s and drift at {settled:F3} m/s six seconds on (want swirling, then slow)");
    }

    // Given motes in a lamp's light
    // When a fireball detonates 3 m from the lamp
    // Then the motes are thrown outward from the blast point, and motes float in the light again within ten seconds.
    private static void Fireball(Checker check)
    {
        Lit(Vector2.Zero);
        int before = Within(AirLayer.Motes, Lamp, LampReach);
        Vector3 at = Lamp + new Vector3(-3f, 0f, -1.5f);
        AirScene.Blast(at, 6f);
        Go(0.2f, Vector2.Zero);
        float outward = Outward(AirLayer.Motes, at, 6f);
        Go(9.8f, Vector2.Zero);
        int again = Within(AirLayer.Motes, Lamp, LampReach);
        check.Expect(outward > Slow, $"E127 the blast throws the motes outward at {outward:F3} m/s (want thrown)");
        check.Expect(again >= before / 2 && again > 0, $"E127 ten seconds on {again} motes float in the light against {before} before (want them back)");
    }

    // Given an outdoor area with cx.air.place set to crypt and a lit lamp
    // When the player looks across the lamp's light
    // Then motes float in it.
    private static void HeldOutdoors(Checker check)
    {
        Hold((Air.Place.Key, "crypt"));
        EnterWith(LookOf(Plain("ttr01", 0, "Grass")), Flat(), 0f, false, false, true);
        Go(3f, Vector2.Zero);
        int motes = Within(AirLayer.Motes, Lamp, LampReach);
        check.Expect(motes > 0, $"E128 the lamp in an outdoor area held crypt holds {motes} motes (want some)");
    }
}
