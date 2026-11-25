using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The meadow wisps' plain regressions: the wind's speed carries them, a front tears behind its line, a second
/// front finds what the first left, they fill back, fog holds them, the band's middle is thickest, and none hang with the
/// air off or in a crypt. Oracle, by hand: the flat meadow, the strong wind along x and the band's middle 60 m up, where a
/// streak's mean over a 16 m square is exactly the wisps' fill.</summary>
internal static class AirMeadowRegressions
{
    /// <summary>Where the wisps are read: the band's middle.</summary>
    private const float Overhead = 60f;

    /// <summary>Either side of the player along the wind, metres: well behind a fresh front's line, and well ahead of it.</summary>
    private const float Behind = -20f, Ahead = 20f;

    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirMeadowRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        WindCarries(check);
        FrontTears(check);
        SecondFront(check);
        FillsBack(check);
        FogHolds(check);
        BandMiddle(check);
        OffHangsNone(check);
        CryptHangsNone(check);
        log.Info(check.Ok ? "AirMeadowRegressions.SelfCheck: passed" : "AirMeadowRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A meadow at <paramref name="amount"/> fog by day, thirty still seconds after entry.</summary>
    private static void Settled(float amount)
    {
        Enter(Meadow, Flat(), amount, false);
        Go(30f, Vector2.Zero);
    }

    /// <summary>The wisps' mean thickness over the 16 m square at <paramref name="x"/> along the wind, in the band's middle.</summary>
    private static float At(float x) => Thick(AirLayer.Wisps, x, 0f, 8f, Overhead);

    /// <summary>How far the wisps travel in ten seconds of <paramref name="wind"/>, from a settled calm meadow.</summary>
    private static float Travel(Vector2 wind)
    {
        Settled(0f);
        Vector2 from = AirScene.Wisps.Carried;
        Go(10f, wind);
        return Vector2.Distance(AirScene.Wisps.Carried, from);
    }

    private static void WindCarries(Checker check)
    {
        float calm = Travel(Vector2.Zero), strong = Travel(Strong);
        check.Expect(calm > 0f && strong > 3f * calm, $"in ten seconds the wisps travel {strong:F2} m in the strong wind and {calm:F2} m calm (want over three times as far)");
    }

    private static void FrontTears(Checker check)
    {
        Settled(0f);
        float behind0 = At(Behind), ahead0 = At(Ahead);
        Go(0.25f, Strong);
        float behind = At(Behind), ahead = At(Ahead);
        check.Expect(behind < 0.9f * behind0 && ahead > 0.98f * ahead0,
            $"a quarter second into a gust the wisps behind its line keep {behind / behind0:P0} and those ahead {ahead / ahead0:P0} (want torn, then whole)");
    }

    private static void SecondFront(Checker check)
    {
        Settled(0f);
        float whole = At(Ahead);
        Go(3f, Strong);
        Go(3f, Vector2.Zero);
        float left = At(Ahead);
        Go(0.25f, Strong);
        float ahead = At(Ahead);
        check.Expect(left < 0.9f * whole, $"Given: the first gust left {left / whole:P0} of the wisps");
        check.Expect(MathF.Abs(ahead - left) < 0.05f * left, $"a second gust's front finds {ahead:F5} ahead of its line where {left:F5} was left (want the same)");
    }

    private static void FillsBack(Checker check)
    {
        Settled(0f);
        float whole = At(Ahead);
        Go(3f, Strong);
        float torn = At(Ahead);
        Go(20f, Strong);
        float back = At(Ahead);
        check.Expect(torn < 0.9f * whole && back > torn + 0.5f * (whole - torn),
            $"the wisps torn to {torn / whole:P0} by a gust are back to {back / whole:P0} twenty seconds on (want over half the way back)");
    }

    /// <summary>The share of the wisps a gust's front leaves behind its line at <paramref name="amount"/> fog, and the share
    /// of what is missing once it passed that fills back two seconds on.</summary>
    private static (float Torn, float Back) Gust(float amount)
    {
        Settled(amount);
        float whole = At(Behind);
        Go(0.25f, Strong);
        float torn = At(Behind);
        Go(2.75f, Strong);
        float passed = At(Ahead);
        Go(2f, Strong);
        return (torn / whole, (At(Ahead) - passed) / (whole - passed));
    }

    private static void FogHolds(Checker check)
    {
        Settled(0f);
        float thin = At(0f);
        Settled(10f);
        float thick = At(0f);
        check.Expect(thick > 1.5f * thin, $"the wisps hang {thick:F5} at fog amount 10 against {thin:F5} at 0 (want thicker)");
        (float torn0, float back0) = Gust(0f);
        (float torn10, float back10) = Gust(10f);
        check.Expect(torn10 > torn0 + 0.05f && back10 > back0 + 0.05f,
            $"at fog amount 10 a gust leaves {torn10:P0} and two seconds fill back {back10:P0} of the rest; at 0, {torn0:P0} and {back0:P0} (want more left, and faster back)");
    }

    private static void BandMiddle(Checker check)
    {
        Settled(0f);
        float middle = At(0f), low = Thick(AirLayer.Wisps, 0f, 0f, 8f, 35f), high = Thick(AirLayer.Wisps, 0f, 0f, 8f, 85f);
        check.Expect(low > 0f && high > 0f && middle > 2f * low && middle > 2f * high,
            $"the wisps hang {low:F5} 35 m up, {middle:F5} 60 m up and {high:F5} 85 m up (want thickest in the middle)");
    }

    private static void OffHangsNone(Checker check)
    {
        Settled(0f);
        float on = At(0f);
        Air.Enabled.V = false;
        float off = At(0f);
        Air.Enabled.V = true;
        check.Expect(on > 0f && off == 0f, $"with the air off the wisps hang {off:F5} overhead (want none; {on:F5} with it on)");
    }

    private static void CryptHangsNone(Checker check)
    {
        Enter(Crypt, Room(), 0f, false);
        Go(1f, Vector2.Zero);
        float wisps = Thick(AirLayer.Wisps, 0f, 0f, 4f, Overhead);
        check.Expect(wisps == 0f, $"a crypt hangs wisps {wisps:F5} 60 m over its floor (want none)");
    }
}
