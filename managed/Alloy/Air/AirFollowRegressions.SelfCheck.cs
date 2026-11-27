using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The air follows the mist grid as it re-centres on the player: what lies where they walk to is what an area
/// entered there would show. Oracle: the same ground entered afresh at the far place.</summary>
internal static class AirFollowRegressions
{
    /// <summary>Where the player enters and where they walk to, 200 m east: past every re-centre the grid can make in place.</summary>
    private static readonly Vector3 Start = new(-100f, 0f, 0f), There = new(100f, 0f, 0f);

    /// <summary>The share an air walked to may differ by from one entered there.</summary>
    private const float Same = 0.02f;

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirFollowRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        DustFollows(check);
        MistFollows(check);
        log.Info(check.Ok ? "AirFollowRegressions.SelfCheck: passed" : "AirFollowRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A crypt's dust over flat stone: walked 200 m, the dust at the feet is the dust an entry there lays.</summary>
    private static void DustFollows(Checker check)
    {
        float entered = Walked(Flat(), Crypt, false, AirLayer.Dust, false);
        float walked = Walked(Flat(), Crypt, false, AirLayer.Dust, true);
        check.Expect(entered > 0f && MathF.Abs(walked - entered) <= Same * entered,
            $"walked 200 m the crypt's dust at the feet is {walked:F4} m against {entered:F4} m entered there (want the same, some dust)");
    }

    /// <summary>Night mist by a shore the player walks to from 200 m inland: the mist over its water is the mist an
    /// entry there lays.</summary>
    private static void MistFollows(Checker check)
    {
        static System.Collections.Generic.List<Tile> Shore() => Ground(160f, 2f, (_, _) => 0f, (x, _) => x < 90f ? Stone : MistFixture.Water);
        float entered = Walked(Shore(), City, true, AirLayer.NightMist, false);
        float walked = Walked(Shore(), City, true, AirLayer.NightMist, true);
        check.Expect(entered > 0f && MathF.Abs(walked - entered) <= Same * entered,
            $"walked 200 m to the shore its night mist at the feet is {walked:F4} m against {entered:F4} m entered there (want the same, some mist)");
    }

    /// <summary>The <paramref name="layer"/> at <see cref="There"/> once the air is entered at <see cref="Start"/> and the
    /// player has <paramref name="walked"/> there, or once it is entered at <see cref="There"/> itself.</summary>
    private static float Walked(System.Collections.Generic.List<Tile> tiles, AirLook look, bool night, AirLayer layer, bool walked)
    {
        Lay(tiles, walked ? Start : There, true, 0f);
        AirScene.Enter(look, night);
        Go(1f, Vector2.Zero);
        if (walked)
        {
            for (float x = Start.X; x <= There.X; x += 4f)
            {
                GroundMist.Follow(new Vector3(x, 0f, 0f));
                Go(Frame, Vector2.Zero);
            }
        }
        Go(1f, Vector2.Zero);
        return Near(layer, There.X, There.Y, 2f);
    }
}
