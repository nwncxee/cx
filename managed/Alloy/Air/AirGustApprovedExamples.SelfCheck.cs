using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>A change in the area's wind sends a gust front; a steady wind sends none. Oracle, by hand: the wind each
/// example's script sets, the dust's depth and motion read at points chosen off the room, and the front's reader.</summary>
internal static class AirGustApprovedExamples
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirGustApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        CalmToStrong(check);
        WeakToStrong(check);
        SteadyNoFront(check);
        BuildsBack(check);
        TurnNoFront(check);
        StrongToWeak(check);
        EnterStrong(check);
        WallLee(check);
        OffThenOn(check);
        MistFront(check);
        log.Info(check.Ok ? "AirGustApprovedExamples.SelfCheck: passed" : "AirGustApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A crypt room laid and settled under <paramref name="wind"/> for <paramref name="seconds"/>.</summary>
    private static void Settled(Vector2 wind, float seconds)
    {
        EnterWith(Crypt, Room(), 0f, false, false, false);
        Go(seconds, wind);
    }

    /// <summary>The mean motion of the dust over the square of half side <paramref name="r"/> round the origin.</summary>
    private static Vector2 Flow(float r)
    {
        Vector2 sum = Vector2.Zero;
        int n = 0;
        for (float x = -r + 0.5f; x < r; x += 1f)
            for (float y = -r + 0.5f; y < r; y += 1f) { sum += AirScene.Motion(x, y); n++; }
        return n > 0 ? sum / n : Vector2.Zero;
    }

    /// <summary>The spread of the dust's depth across the wind, along x = 0: how streaked it lies.</summary>
    private static float Streaks()
    {
        float sum = 0f, sq = 0f;
        int n = 0;
        for (float y = -RoomHalf + 0.25f; y < RoomHalf; y += 0.25f) { float d = Column(AirLayer.Dust, 0f, y); sum += d; sq += d * d; n++; }
        float mean = sum / n;
        return MathF.Sqrt(MathF.Max(sq / n - mean * mean, 0f));
    }

    /// <summary>Step <paramref name="seconds"/> under <paramref name="wind"/>; true when a front was live on any frame,
    /// and the strongest it was.</summary>
    private static (bool Any, float Strength, Vector2 Dir) Watch(float seconds, Vector2 wind)
    {
        bool any = false;
        float most = 0f;
        Vector2 dir = Vector2.Zero;
        for (int n = (int)MathF.Round(seconds / Frame); n > 0; n--)
        {
            AirScene.Step(Frame, wind);
            AirFront f = AirScene.Front;
            if (f.Live && f.Strength > most) { any = true; most = f.Strength; dir = f.Dir; }
        }
        return (any, most, dir);
    }

    // Given a crypt with dust on the floor and no wind
    // When a script raises the area's wind from none to strong
    // Then a front sweeps across the floor in the wind's direction, tearing the dust into streaks and lifting some of it.
    private static void CalmToStrong(Checker check)
    {
        Settled(Vector2.Zero, 3f);
        float calm = Streaks();
        float lifted = 0f;
        bool any = false;
        Vector2 dir = Vector2.Zero;
        for (int n = (int)(1.5f / Frame); n > 0; n--)
        {
            AirScene.Step(Frame, Strong);
            if (AirScene.Front.Live) { any = true; dir = AirScene.Front.Dir; }
            for (float x = -6f; x <= 6f; x += 1f) lifted = MathF.Max(lifted, AirScene.Density(AirLayer.Dust, new Vector3(x, 0f, OverKnee)));
        }
        check.Expect(any && Vector2.Dot(Vector2.Normalize(dir == Vector2.Zero ? Vector2.UnitY : dir), Vector2.Normalize(Strong)) > 0.9f,
            $"E40 the rise sends a front {(any ? "along " + dir : "nowhere")} (want one along {Strong})");
        check.Expect(Streaks() > 1.5f * calm, $"E40 the dust lies streaked {Streaks():F4} m across the wind against {calm:F4} m calm (want torn into streaks)");
        check.Expect(lifted > 0f, $"E40 the front lifts dust to {lifted:F4} above the knee (want some lifted)");
    }

    /// <summary>The dust left by the player two seconds after a rise to strong from <paramref name="from"/>.</summary>
    private static float LeftAfterRise(Vector2 from, out (bool Any, float Strength, Vector2 Dir) front)
    {
        Settled(from, 60f);
        front = Watch(2f, Strong);
        return Near(AirLayer.Dust, 0f, 0f, 4f);
    }

    // Given the same crypt with a weak wind
    // When a script raises the wind to strong
    // Then a front sweeps the same way, and leaves more dust lying behind it than the rise from none to strong does.
    private static void WeakToStrong(Checker check)
    {
        float fromCalm = LeftAfterRise(Vector2.Zero, out _);
        float fromWeak = LeftAfterRise(Weak, out var front);
        check.Expect(front.Any && Vector2.Dot(front.Dir, Vector2.Normalize(Strong)) > 0.9f, $"E41 the rise from weak sends a front along {front.Dir} (want {Strong})");
        check.Expect(fromWeak > fromCalm, $"E41 it leaves {fromWeak:F4} m lying against {fromCalm:F4} m after a rise from calm (want more)");
    }

    // Given a crypt where a strong wind has blown for a minute
    // When another minute passes with no change
    // Then no new front sweeps, and the dust lies thin and drifts downwind.
    private static void SteadyNoFront(Checker check)
    {
        Settled(Vector2.Zero, 3f);
        float calm = Near(AirLayer.Dust, 0f, 0f, 4f);
        Go(60f, Strong);
        var front = Watch(60f, Strong);
        float thin = Near(AirLayer.Dust, 0f, 0f, 4f);
        Vector2 flow = Flow(4f);
        check.Expect(!front.Any, "E42 a minute of steady strong wind sends no new front");
        check.Expect(thin > 0f && thin < calm && flow.X > 0f, $"E42 the dust lies {thin:F4} m against {calm:F4} m calm, moving {flow} (want thin, drifting along {Strong})");
    }

    // Given a strong wind has swept a crypt's floor clear
    // When the wind drops to none
    // Then the dust builds back over the next tens of seconds, and lies as it did before the wind within a minute.
    private static void BuildsBack(Checker check)
    {
        Settled(Vector2.Zero, 3f);
        float before = Near(AirLayer.Dust, 0f, 0f, 4f);
        Go(60f, Strong);
        Go(5f, Vector2.Zero);
        float soon = Near(AirLayer.Dust, 0f, 0f, 4f);
        Go(55f, Vector2.Zero);
        float minute = Near(AirLayer.Dust, 0f, 0f, 4f);
        check.Expect(soon < 0.8f * before, $"E43 five seconds after the drop {soon:F4} m lies against {before:F4} m (want still building)");
        check.Expect(minute >= 0.9f * before, $"E43 a minute after the drop {minute:F4} m lies against {before:F4} m (want as before)");
    }

    // Given a crypt with a strong wind from the east
    // When a script turns the wind to come from the north at the same strength
    // Then no front sweeps, and the dust drifts the new way.
    private static void TurnNoFront(Checker check)
    {
        Settled(FromEast, 60f);
        var front = Watch(10f, FromNorth);
        Vector2 flow = Flow(4f);
        check.Expect(!front.Any, $"E44 turning the wind sends {(front.Any ? "a front" : "no front")} (want none)");
        check.Expect(flow.Y < 0f && MathF.Abs(flow.Y) > MathF.Abs(flow.X), $"E44 the dust moves {flow} (want toward the south)");
    }

    // Given a crypt with a strong wind
    // When a script lowers the wind to weak
    // Then a front sweeps across the floor in the wind's direction, weaker than the one a rise from weak to strong sends.
    private static void StrongToWeak(Checker check)
    {
        Settled(Strong, 60f);
        var drop = Watch(2f, Weak);
        Settled(Weak, 60f);
        var rise = Watch(2f, Strong);
        check.Expect(drop.Any && Vector2.Dot(drop.Dir, Vector2.Normalize(Weak)) > 0.9f, $"E115 the drop sends a front along {drop.Dir} (want {Weak})");
        check.Expect(drop.Strength < rise.Strength, $"E115 the drop's front is {drop.Strength:F3} against the rise's {rise.Strength:F3} (want weaker)");
    }

    // Given the player in an area with no wind
    // When they enter a crypt whose wind is already strong
    // Then no front sweeps, and the dust lies thin and drifts downwind as in E42.
    private static void EnterStrong(Checker check)
    {
        Settled(Vector2.Zero, 3f);
        float calm = Near(AirLayer.Dust, 0f, 0f, 4f);
        EnterWith(Crypt, Room(), 0f, false, false, false);
        var front = Watch(60f, Strong);
        float thin = Near(AirLayer.Dust, 0f, 0f, 4f);
        check.Expect(!front.Any, "E116 entering a crypt already in a strong wind sends no front");
        check.Expect(thin > 0f && thin < calm && Flow(4f).X > 0f, $"E116 the dust lies {thin:F4} m against {calm:F4} m calm, moving {Flow(4f)} (want thin, downwind)");
    }

    // Given a crypt room whose wall faces the wind's direction, with dust on both sides
    // When a front sweeps
    // Then the dust in the wall's lee is torn less than the dust in the open.
    private static void WallLee(Checker check)
    {
        var room = Ground(20f, 1f, (_, _) => 0f, (x, y) =>
            MathF.Abs(x) >= RoomHalf || MathF.Abs(y) >= RoomHalf || (MathF.Abs(x) < 0.5f && MathF.Abs(y) < 4f) ? Nonwalk : Stone);
        Enter(Crypt, room, 0f, false);
        Go(3f, Vector2.Zero);
        float lee0 = Mean(AirLayer.Dust, 1f, -3f, 3f, 3f), open0 = Mean(AirLayer.Dust, 1f, 5f, 3f, 7f);
        Go(1.5f, Strong);
        float lee = Mean(AirLayer.Dust, 1f, -3f, 3f, 3f), open = Mean(AirLayer.Dust, 1f, 5f, 3f, 7f);
        check.Expect(lee0 > 0f && open0 > 0f && lee / lee0 > open / open0, $"E117 the lee keeps {lee / lee0:P0}, the open floor {open / open0:P0} (want the lee torn less)");
    }

    // Given the Air switch off in a crypt
    // When a script raises the wind to strong and the player then switches the air on
    // Then no front sweeps, and the dust lies as for a strong wind.
    private static void OffThenOn(Checker check)
    {
        Settled(Strong, 60f);
        float steady = Near(AirLayer.Dust, 0f, 0f, 4f);
        Air.Enabled.V = false;
        Settled(Vector2.Zero, 3f);
        Go(2f, Strong);
        Air.Enabled.V = true;
        var front = Watch(3f, Strong);
        float lies = Near(AirLayer.Dust, 0f, 0f, 4f);
        check.Expect(!front.Any, "E118 switching the air on under a strong wind sends no front");
        check.Expect(steady > 0f && MathF.Abs(lies - steady) <= 0.2f * steady, $"E118 the dust lies {lies:F4} m against {steady:F4} m under a steady strong wind (want the same)");
    }

    // Given night mist over the water in calm air
    // When a script raises the area's wind from none to strong
    // Then a front can be followed by eye as it crosses the mist and drags it downwind.
    private static void MistFront(Checker check)
    {
        Enter(City, Quay(), 10f, true);
        Go(180f, Vector2.Zero);
        Vector2 before = Centroid(AirLayer.NightMist, 30f, 0f, out _);
        var front = Watch(3f, Strong);
        Vector2 after = Centroid(AirLayer.NightMist, 30f, 0f, out _);
        check.Expect(front.Any && Vector2.Dot(front.Dir, Vector2.Normalize(Strong)) > 0.9f, $"E119 the rise sends a front along {front.Dir} over the mist (want {Strong})");
        check.Expect(after.X > before.X, $"E119 the mist's centre moves from {before} to {after} (want downwind)");
    }
}
