using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The meadow pollen's plain regressions: a steady wind's drift, a front's quick rush, the settle back to its
/// height after a blast, none shown over ground that is no floor, and none by night. Oracle, by hand: the flat meadow, the strong wind
/// of 2 m/s along x whose drift is half of it, and a wall east of x = 4.</summary>
internal static class AirPollenRegressions
{
    /// <summary>Where the pollen's drift is read, and how far round it.</summary>
    private static readonly Vector3 Around = new(0f, 0f, 1f);

    /// <summary>Half the strong wind's speed, metres a second: the air's drift in it.</summary>
    private const float SteadyDrift = 1f;

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirPollenRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        SteadyDrifts(check);
        RushesQuickly(check);
        SettlesBack(check);
        WallHides(check);
        NightStops(check);
        log.Info(check.Ok ? "AirPollenRegressions.SelfCheck: passed" : "AirPollenRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A meadow entered by day stops its pollen when night falls there and floats it again when day breaks.</summary>
    private static void NightStops(Checker check)
    {
        Calm(Flat());
        bool day = AirScene.Glinting;
        AirScene.Night(true);
        Go(1f, Vector2.Zero);
        bool night = AirScene.Glinting;
        AirScene.Night(false);
        Go(1f, Vector2.Zero);
        check.Expect(day && !night && AirScene.Glinting,
            $"a meadow's pollen glints {day} by day, {night} once night falls, {AirScene.Glinting} once day breaks (want true, false, true)");
    }

    /// <summary>A meadow by day, five still seconds after entry.</summary>
    private static void Calm(List<Tile> tiles)
    {
        Enter(Meadow, tiles, 0f, false);
        Go(5f, Vector2.Zero);
    }

    /// <summary>The mean height of every grain shown.</summary>
    private static float MeanHeight()
    {
        float sum = 0f;
        ReadOnlySpan<AirParticle> all = AirScene.Particles(AirLayer.Pollen);
        foreach (AirParticle p in all) sum += p.Pos.Z;
        return all.Length > 0 ? sum / all.Length : float.NaN;
    }

    private static void SteadyDrifts(Checker check)
    {
        Calm(Flat());
        Go(60f, Strong);
        Vector3 drift = Drift(AirLayer.Pollen, Around, 8f);
        check.Expect(MathF.Abs(drift.X - SteadyDrift) < 0.2f && MathF.Abs(drift.Y) < 0.2f,
            $"a minute into the strong wind the pollen drifts at {drift} (want about <{SteadyDrift}, 0>)");
    }

    private static void RushesQuickly(Checker check)
    {
        Calm(Flat());
        Go(0.3f, Strong);
        float early = Drift(AirLayer.Pollen, Around, 8f).X;
        Go(1.2f, Strong);
        float full = Drift(AirLayer.Pollen, Around, 8f).X;
        check.Expect(AirScene.Front.Live && full > 0f && early > 0.75f * full,
            $"0.3 s into a gust the pollen rushes at {early:F3} m/s against {full:F3} m/s 1.5 s in (want over three quarters of it)");
    }

    private static void SettlesBack(Checker check)
    {
        Calm(Flat());
        float before = MeanHeight();
        AirScene.Blast(Vector3.Zero, 10f);
        Go(1f, Vector2.Zero);
        float thrown = MeanHeight();
        Go(15f, Vector2.Zero);
        float after = MeanHeight();
        check.Expect(thrown > before + 0.05f, $"Given: a blast on the ground lifts the pollen's mean height from {before:F3} m to {thrown:F3} m");
        check.Expect(MathF.Abs(after - before) < 0.05f, $"fifteen seconds after the blast the pollen sits {after:F3} m up on the mean against {before:F3} m before (want back)");
    }

    private static void WallHides(Checker check)
    {
        Calm(Ground(40f, 1f, (_, _) => 0f, (x, _) => x < 4f ? Stone : Nonwalk));
        Go(10f, Strong);
        int over = 0, field = 0;
        foreach (AirParticle p in AirScene.Particles(AirLayer.Pollen))
            if (p.Pos.X > 4.5f) over++;
            else if (p.Pos.X < 3.5f) field++;
        check.Expect(field > 0 && over == 0, $"after ten seconds' strong wind toward a wall {over} grains show over it and {field} over the field (want none over it)");
    }
}
