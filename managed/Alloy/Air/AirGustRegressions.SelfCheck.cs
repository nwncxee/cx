using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>A gust front's plain regressions: it runs the way the wind blows or blew, crosses the whole floor, lets the dust settle
/// and never tears more than lies. Oracle, by hand: the wind each case sets and a still crypt room's dust.</summary>
internal static class AirGustRegressions
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirGustRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        NorthRise(check);
        NorthDrop(check);
        Settles(check);
        Upwind(check);
        Storm(check);
        log.Info(check.Ok ? "AirGustRegressions.SelfCheck: passed" : "AirGustRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A still crypt room, three seconds calm.</summary>
    private static void Still()
    {
        EnterWith(Crypt, Room(), 0f, false, false, false);
        Go(3f, Vector2.Zero);
    }

    // Given a still crypt
    // When a script raises the wind from none to strong from the south
    // Then the front runs north, the way the new wind blows.
    private static void NorthRise(Checker check)
    {
        Still();
        Vector2 dir = Vector2.Zero;
        for (int n = Frames(2f); n > 0; n--)
        {
            AirScene.Step(Frame, new Vector2(0f, 2f));
            if (AirScene.Front.Live) dir = AirScene.Front.Dir;
        }
        check.Expect(Vector2.Dot(dir, Vector2.UnitY) > 0.9f, $"a rise toward the north sends its front along {dir} (want <0, 1>)");
    }

    // Given a crypt under a strong wind from the south
    // When a script drops the wind to none
    // Then the front runs north, the way the wind blew.
    private static void NorthDrop(Checker check)
    {
        Still();
        Go(5f, new Vector2(0f, 2f));
        Vector2 dir = Vector2.Zero;
        for (int n = Frames(2f); n > 0; n--)
        {
            AirScene.Step(Frame, Vector2.Zero);
            if (AirScene.Front.Live) dir = AirScene.Front.Dir;
        }
        check.Expect(Vector2.Dot(dir, Vector2.UnitY) > 0.9f, $"a drop from a wind toward the north sends its front along {dir} (want <0, 1>)");
    }

    // Given a crypt a front has just crossed
    // When ten seconds pass under the same wind
    // Then no dust stands above the knee.
    private static void Settles(Checker check)
    {
        Still();
        Go(2f, Strong);
        Go(10f, Strong);
        float most = 0f;
        for (float x = -6f; x <= 6f; x += 1f) most = MathF.Max(most, AirScene.Density(AirLayer.Dust, new Vector3(x, 0f, OverKnee)));
        check.Expect(most == 0f, $"ten seconds after the front the dust reads {most:F4} above the knee (want none)");
    }

    // Given a still crypt
    // When a script raises the wind from none to strong
    // Then the front tears the floor upwind of the player as well as downwind.
    private static void Upwind(Checker check)
    {
        Still();
        float before = Near(AirLayer.Dust, -5f, 0f, 2f);
        Go(2f, Strong);
        float after = Near(AirLayer.Dust, -5f, 0f, 2f);
        check.Expect(before > 0f && after < before, $"upwind of the player the dust lies {after:F4} m after the front against {before:F4} m (want torn)");
    }

    // Given a still crypt
    // When a script raises the wind from none to a storm of 8 m/s
    // Then no column of the room is left holding less than no dust.
    private static void Storm(Checker check)
    {
        Still();
        Go(2f, new Vector2(8f, 0f));
        float least = float.MaxValue;
        for (float x = -RoomHalf + 0.5f; x < RoomHalf; x += 1f)
            for (float y = -RoomHalf + 0.5f; y < RoomHalf; y += 1f) least = MathF.Min(least, Column(AirLayer.Dust, x, y));
        check.Expect(least >= 0f, $"after a storm's front the thinnest column holds {least:F4} m (want none below zero)");
    }
}
