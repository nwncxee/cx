using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The forest's plain regressions: the leaves' drift and rush, where they are loosed, the haze each trunk lays,
/// its fade and ceiling, and haze kept apart from dust. Oracle, by hand: the flat grove, the strong wind of 2 m/s along x
/// whose drift is half of it, and haze read on one column with and without a second trunk beside it.</summary>
internal static class AirForestRegressions
{
    /// <summary>Half the strong wind's speed, metres a second: the air's drift in it.</summary>
    private const float SteadyDrift = 1f;

    /// <summary>How far from the feet a leaf may be seen: the 16 m a trunk sheds within, its 2 m crown and a metre of sway.</summary>
    private const float LeafNear = 19f;

    /// <summary>A trunk the haze readings stand by, on a column's centre, and the column 3 m east of it, half way through the fade.</summary>
    private static readonly Vector2 Trunk = new(-9.5f, 0.5f), Fading = new(-6.5f, 0.5f);

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirForestRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        LeavesDrift(check);
        FrontRushes(check);
        CrownHigh(check);
        LoosedNear(check);
        LoosedWhereWalked(check);
        HazeDrawn(check);
        CryptUnhazed(check);
        HazeCeiling(check);
        HazeFades(check);
        LayersApart(check);
        log.Info(check.Ok ? "AirForestRegressions.SelfCheck: passed" : "AirForestRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The grove entered and settled ten seconds under <paramref name="wind"/>.</summary>
    private static void Wood(Vector2 wind)
    {
        EnterWith(Forest, Flat(), 0f, false, true, false);
        Go(10f, wind);
    }

    /// <summary>A forest with no trunk but <paramref name="at"/>, read before the air steps.</summary>
    private static float LoneTrunk(Vector2 at, Vector2 read)
    {
        Enter(Forest, Flat(), 0f, false);
        AirScene.Trunk(at);
        return AirScene.Shown(read.X, read.Y).Depth;
    }

    /// <summary>The leaves seen over <paramref name="seconds"/> under <paramref name="wind"/>: their mean velocity over
    /// every frame that held one, the highest any flew and the farthest any was from <paramref name="feet"/> on the ground.</summary>
    private static (int Frames, Vector3 Vel, float Highest, float Farthest) Seen(float seconds, Vector2 wind, Vector2 feet)
    {
        int frames = 0;
        float highest = 0f, farthest = 0f;
        Vector3 vel = Vector3.Zero;
        for (int n = (int)MathF.Round(seconds / Frame); n > 0; n--)
        {
            AirScene.Step(Frame, wind);
            ReadOnlySpan<AirParticle> l = AirScene.Particles(AirLayer.Leaves);
            if (l.Length == 0) continue;
            Vector3 sum = Vector3.Zero;
            foreach (AirParticle p in l)
            {
                sum += p.Vel;
                highest = MathF.Max(highest, p.Pos.Z);
                farthest = MathF.Max(farthest, Vector2.Distance(new Vector2(p.Pos.X, p.Pos.Y), feet));
            }
            vel += sum / l.Length;
            frames++;
        }
        return (frames, frames > 0 ? vel / frames : Vector3.Zero, highest, farthest);
    }

    // Given leaves falling in a forest
    // When a strong wind has blown steadily for a minute
    // Then they move downwind at about the air's drift, half the wind's speed, and not across it.
    private static void LeavesDrift(Checker check)
    {
        Wood(Strong);
        Go(60f, Strong);
        (int frames, Vector3 vel, _, _) = Seen(10f, Strong, Vector2.Zero);
        check.Expect(frames > 0 && vel.X > 0.6f * SteadyDrift && vel.X < 1.3f * SteadyDrift && MathF.Abs(vel.Y) < 0.3f,
            $"a minute into the strong wind the leaves move at {vel} over {frames} frames (want about <{SteadyDrift}, 0>)");
    }

    // Given leaves falling in calm air
    // When a strong wind's front arrives
    // Then the front carries them at least a metre a second faster than the same wind does once it is steady.
    private static void FrontRushes(Checker check)
    {
        Wood(Vector2.Zero);
        Seen(30f, Vector2.Zero, Vector2.Zero);
        (_, Vector3 front, _, _) = Seen(0.5f, Strong, Vector2.Zero);
        Go(60f, Strong);
        (_, Vector3 steady, _, _) = Seen(0.5f, Strong, Vector2.Zero);
        check.Expect(front.X > steady.X + 1f, $"the front carries the leaves at {front.X:F3} m/s against {steady.X:F3} m/s steady (want a metre a second more)");
    }

    // Given a forest in calm air
    // When leaves fall for thirty seconds
    // Then some are seen from the crowns, over 2.5 m up, not only at the ankles.
    private static void CrownHigh(Checker check)
    {
        Wood(Vector2.Zero);
        (int frames, _, float highest, _) = Seen(30f, Vector2.Zero, Vector2.Zero);
        check.Expect(frames > 0 && highest > 2.5f, $"the highest leaf in thirty calm seconds flies {highest:F2} m up over {frames} frames (want over 2.5 m)");
    }

    // Given a grove reaching 40 m west of the player
    // When leaves fall for thirty calm seconds
    // Then every one is seen within 19 m of the player, none from the grove's far end.
    private static void LoosedNear(Checker check)
    {
        Wood(Vector2.Zero);
        (int frames, _, _, float farthest) = Seen(30f, Vector2.Zero, Vector2.Zero);
        check.Expect(frames > 0 && farthest <= LeafNear, $"the farthest leaf falls {farthest:F1} m from the player over {frames} frames (want within {LeafNear} m)");
    }

    // Given leaves falling about the player at the grove's edge
    // When the player moves 42 m into the grove's far corner
    // Then the leaves then seen all fall within 19 m of the player's new place.
    private static void LoosedWhereWalked(Checker check)
    {
        Wood(Vector2.Zero);
        Seen(10f, Vector2.Zero, Vector2.Zero);
        var there = new Vector2(-30f, 30f);
        GroundMist.Follow(new Vector3(there, 0f));
        Go(10f, Vector2.Zero);
        (int frames, _, _, float farthest) = Seen(20f, Vector2.Zero, there);
        check.Expect(frames > 0 && farthest <= LeafNear, $"after the move the farthest leaf falls {farthest:F1} m from the player over {frames} frames (want within {LeafNear} m)");
    }

    // Given a forest in calm air
    // When the frame's air is packed for drawing
    // Then the haze counts as lying and the client draws it.
    private static void HazeDrawn(Checker check)
    {
        Wood(Vector2.Zero);
        check.Expect(AirScene.Lies && Drawn(), $"the forest's haze lies {AirScene.Lies} and is drawn {Drawn()} (want both)");
    }

    // Given a crypt room's dust
    // When a trunk is seated in the middle of it
    // Then the dust there is just as it was, the trunk hazing nothing.
    private static void CryptUnhazed(Checker check)
    {
        Enter(Crypt, Room(), 0f, false);
        float before = Thick(AirLayer.Dust, 0f, 0f, 3f, 0.1f);
        AirScene.Trunk(Vector2.Zero);
        float after = Thick(AirLayer.Dust, 0f, 0f, 3f, 0.1f);
        check.Expect(before > 0f && after == before, $"a trunk in the crypt leaves its dust at {after:F5} from {before:F5} (want unchanged)");
    }

    // Given a column a lone trunk hazes whole
    // When the grove's other trunks stand round it, their reach overlapping it
    // Then the haze there is no deeper than the lone trunk's.
    private static void HazeCeiling(Checker check)
    {
        var at = new Vector2(-10f, -2f);
        float lone = LoneTrunk(at, at);
        EnterWith(Forest, Flat(), 0f, false, true, false);
        float grove = AirScene.Shown(at.X, at.Y).Depth;
        check.Expect(lone > 0f && MathF.Abs(grove - lone) <= 1e-6f, $"the grove's haze at {at} reads {grove:F5} against {lone:F5} by a lone trunk (want the same)");
    }

    // Given a lone trunk's haze
    // When it is read at the trunk, 3 m off and 4 m and 6 m off
    // Then it reads whole at the trunk, half 3 m off and none at 4 m or past it.
    private static void HazeFades(Checker check)
    {
        float whole = LoneTrunk(Fading, Fading), half = LoneTrunk(Trunk, Fading);
        float at = AirScene.Shown(Trunk.X, Trunk.Y).Depth, reach = AirScene.Shown(Trunk.X + 4f, Trunk.Y).Depth, past = AirScene.Shown(Trunk.X + 6f, Trunk.Y).Depth;
        check.Expect(whole > 0f && MathF.Abs(half / whole - 0.5f) < 0.05f && half < at,
            $"3 m from its trunk the haze reads {half:F5} against {whole:F5} whole and {at:F5} at the trunk (want half)");
        check.Expect(reach == 0f && past == 0f, $"4 m and 6 m from its trunk the haze reads {reach:F5} and {past:F5} (want none)");
    }

    // Given a forest's haze and a crypt's dust
    // When each is read as the other's layer
    // Then the forest holds no dust and the crypt no haze, each place showing only its own.
    private static void LayersApart(Checker check)
    {
        Wood(Vector2.Zero);
        float haze = Thick(AirLayer.Haze, -10f, 0f, 4f, 0.5f), dust = Thick(AirLayer.Dust, -10f, 0f, 4f, 0.5f);
        Enter(Crypt, Room(), 0f, false);
        float cryptDust = Thick(AirLayer.Dust, 0f, 0f, 3f, 0.1f), cryptHaze = Thick(AirLayer.Haze, 0f, 0f, 3f, 0.1f);
        check.Expect(haze > 0f && dust == 0f, $"the forest holds haze {haze:F5} and dust {dust:F5} (want haze, no dust)");
        check.Expect(cryptDust > 0f && cryptHaze == 0f, $"the crypt holds dust {cryptDust:F5} and haze {cryptHaze:F5} (want dust, no haze)");
    }
}
