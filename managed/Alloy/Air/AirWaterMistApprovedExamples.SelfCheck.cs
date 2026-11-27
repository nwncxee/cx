using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>Mist gathers over the water at night and creeps onto the bank. Oracle, by hand: the quay and the stream laid
/// tile by tile (water east of x = 0, a 2 m stream), the hour each example sets, and depths read at chosen points.</summary>
internal static class AirWaterMistApprovedExamples
{
    /// <summary>The most mist a clear street, or a faint haze over the water, may hold, in metres.</summary>
    private const float Faint = 0.02f;

    /// <summary>A strong wind from the water onto the streets.</summary>
    private static readonly Vector2 Onshore = new(-2f, 0f);

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirWaterMistApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        ClearByDay(check);
        CreepsAtNight(check);
        DryCityClear(check);
        WindDrags(check);
        DayThins(check);
        NightAmount(check);
        DayAmount(check);
        RuralStream(check);
        CryptPool(check);
        ForestStream(check);
        DuskGathers(check);
        log.Info(check.Ok ? "AirWaterMistApprovedExamples.SelfCheck: passed" : "AirWaterMistApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The city by the water at fog amount <paramref name="amount"/>, by night or by day, let gather.</summary>
    private static void Docks(bool night, float amount)
    {
        Enter(City, Quay(), amount, night);
        Go(Gather, Vector2.Zero);
    }

    // Given an outdoor city area beside water, by day
    // When the player walks its streets
    // Then the streets are clear, with at most a faint haze over the water.
    private static void ClearByDay(Checker check)
    {
        Docks(false, 0f);
        float streets = Near(AirLayer.NightMist, -10f, 0f, 4f), water = Near(AirLayer.NightMist, 10f, 0f, 4f);
        check.Expect(streets == 0f && water <= Faint, $"E67 by day the streets hold {streets:F4} m and the water {water:F4} m (want clear, at most faint)");
    }

    // Given the same city at night
    // When the player stands on the quay
    // Then mist lies over the water and creeps a few metres onto the quay, and the streets farther from the water stay clear.
    private static void CreepsAtNight(Checker check)
    {
        Docks(true, 0f);
        float water = Near(AirLayer.NightMist, 10f, 0f, 4f), quay = Near(AirLayer.NightMist, -1.5f, 0f, 1f), streets = Near(AirLayer.NightMist, -15f, 0f, 4f);
        check.Expect(water > Faint && quay > 0f && streets == 0f, $"E68 at night the water holds {water:F4} m, the quay's edge {quay:F4} m, the streets {streets:F4} m (want mist, some, clear)");
    }

    // Given an outdoor city area with no water, at night
    // When the player walks its streets
    // Then no mist lies in them.
    private static void DryCityClear(Checker check)
    {
        Enter(DryCity, Flat(), 0f, true);
        Go(Gather, Vector2.Zero);
        float streets = Near(AirLayer.NightMist, 0f, 0f, 8f);
        check.Expect(streets == 0f, $"E69 the dry city's streets hold {streets:F4} m at night (want none)");
    }

    // Given the night mist
    // When the wind is strong
    // Then the mist is thinner and drags along the streets downwind.
    private static void WindDrags(Checker check)
    {
        Docks(true, 0f);
        float calmWater = Near(AirLayer.NightMist, 10f, 0f, 4f), calmStreet = Near(AirLayer.NightMist, -6f, 0f, 2f);
        Go(60f, Onshore);
        float windWater = Near(AirLayer.NightMist, 10f, 0f, 4f), windStreet = Near(AirLayer.NightMist, -6f, 0f, 2f);
        check.Expect(windWater < calmWater, $"E72 the mist over the water reads {windWater:F4} m in the wind against {calmWater:F4} m calm (want thinner)");
        check.Expect(windStreet > calmStreet, $"E72 the street downwind holds {windStreet:F4} m against {calmStreet:F4} m calm (want dragged along it)");
    }

    // Given the night mist
    // When the area turns to day
    // Then the mist thins away over the next minutes rather than vanishing at once.
    private static void DayThins(Checker check)
    {
        Docks(true, 0f);
        float night = Near(AirLayer.NightMist, 10f, 0f, 4f);
        AirScene.Night(false);
        Go(1f, Vector2.Zero);
        float second = Near(AirLayer.NightMist, 10f, 0f, 4f);
        Go(Gather, Vector2.Zero);
        float later = Near(AirLayer.NightMist, 10f, 0f, 4f);
        check.Expect(night > Faint && second >= 0.8f * night, $"E73 a second into the day {second:F4} m lies against {night:F4} m (want still there)");
        check.Expect(later <= Faint, $"E73 minutes into the day {later:F4} m lies (want thinned away)");
    }

    // Given two cities by the water alike but for their night fog amount, 0 and 10
    // When night falls
    // Then thicker mist lies over the water in the one with night fog amount 10.
    private static void NightAmount(Checker check)
    {
        Docks(true, 0f);
        float none = Near(AirLayer.NightMist, 10f, 0f, 4f);
        Docks(true, 10f);
        float ten = Near(AirLayer.NightMist, 10f, 0f, 4f);
        check.Expect(ten > none, $"E93 the water holds {ten:F4} m at night fog amount 10, {none:F4} m at 0 (want thicker)");
    }

    // Given a city by the water whose day fog amount is 10
    // When the player walks its streets by day
    // Then they are clear, as in E67.
    private static void DayAmount(Checker check)
    {
        Docks(false, 10f);
        float streets = Near(AirLayer.NightMist, -10f, 0f, 4f), water = Near(AirLayer.NightMist, 10f, 0f, 4f);
        check.Expect(streets == 0f && water <= Faint, $"E94 by day at fog amount 10 the streets hold {streets:F4} m and the water {water:F4} m (want clear, at most faint)");
    }

    // Given an outdoor rural area with a stream among its tiles, at night
    // When the player stands beside the stream
    // Then mist lies over the water and a little way onto its banks, and the meadow farther off stays clear.
    private static void RuralStream(Checker check)
    {
        Enter(new AirLook(AirPlace.Meadow, false, true, false), Stream(), 0f, true);
        Go(Gather, Vector2.Zero);
        float water = Near(AirLayer.NightMist, 0f, 0f, 0.9f), bank = Near(AirLayer.NightMist, 2f, 0f, 0.5f), far = Near(AirLayer.NightMist, 15f, 0f, 4f);
        check.Expect(water > Faint && bank > 0f && far == 0f, $"E102 the stream holds {water:F4} m, its bank {bank:F4} m, the meadow off {far:F4} m (want mist, some, clear)");
    }

    // Given an indoor crypt with a pool of standing water, at night
    // When the player stands beside the pool
    // Then crypt dust lies over the water as in E51 and no night mist gathers there.
    private static void CryptPool(Checker check)
    {
        var room = Ground(20f, 1f, (_, _) => 0f, (x, y) => MathF.Abs(x) >= RoomHalf || MathF.Abs(y) >= RoomHalf ? Nonwalk : MathF.Abs(x - 4f) < 2f && MathF.Abs(y) < 2f ? MistFixture.Water : Stone);
        AirLook look = new(AirPlace.Crypt, false, true, false);
        Enter(look, room, 0f, true);
        Go(Gather, Vector2.Zero);
        float pool = Near(AirLayer.Dust, 4f, 0f, 1.5f), floor = Near(AirLayer.Dust, -4f, 0f, 1.5f), mist = Thick(AirLayer.NightMist, 4f, 0f, 1.5f, 0.1f);
        check.Expect(floor > 0f && MathF.Abs(pool - floor) <= 0.1f * floor, $"E142 the dust lies {pool:F4} m over the pool and {floor:F4} m over the floor (want the same)");
        check.Expect(!look.Layers(true).HasFlag(AirLayer.NightMist) && mist == 0f, $"E142 the crypt at night lays {look.Layers(true)}, night mist {mist:F4} over the pool (want none)");
    }

    // Given an outdoor forest area with a stream among its tiles, at night
    // When the player stands beside the stream
    // Then mist lies over the stream and the haze and leaves of E63 still show.
    private static void ForestStream(Checker check)
    {
        EnterWith(new AirLook(AirPlace.Forest, false, true, false), Stream(), 0f, true, true, false);
        Go(Gather, Vector2.Zero);
        float water = Near(AirLayer.NightMist, 0f, 0f, 0.9f), haze = Thick(AirLayer.Haze, -10f, 0f, 4f, 1.5f);
        int leaves = 0;
        for (int n = (int)(30f / Frame); n > 0; n--) { AirScene.Step(Frame, Vector2.Zero); leaves = Math.Max(leaves, AirScene.Particles(AirLayer.Leaves).Length); }
        check.Expect(water > Faint && haze > 0f && leaves > 0, $"E143 the stream holds {water:F4} m, the trunks haze {haze:F4} and {leaves} leaves fall (want all three)");
    }

    // Given a city by the water at dusk
    // When night falls
    // Then the mist gathers over the water over the next minutes rather than appearing at once.
    private static void DuskGathers(Checker check)
    {
        Docks(false, 0f);
        AirScene.Night(true);
        Go(5f, Vector2.Zero);
        float soon = Near(AirLayer.NightMist, 10f, 0f, 4f);
        Go(Gather, Vector2.Zero);
        float later = Near(AirLayer.NightMist, 10f, 0f, 4f);
        check.Expect(later > Faint && soon < 0.2f * later, $"E145 five seconds into the night {soon:F4} m lies against {later:F4} m minutes on (want gathering, not at once)");
    }
}
