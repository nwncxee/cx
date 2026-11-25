using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>An area hold's plain regressions: a held place picks the look, a held switch greys and reads as it stood at
/// entry, a slider is held only inside its range and Air detail follows the presets. Oracle, by hand: the values each
/// case holds, the places they name, and a crypt room's dust with the air on and none with it off.</summary>
internal static class AirAreaVariablesRegressions
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirAreaVariablesRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        foreach (var t in new Action<Checker>[]
                 {
                     HoldOffWaits, HoldOnWaits, OwnUnderHold, OwnLive, HeldPlaces, AutoReadsArea, MasterGreyed, DustRange, DetailLadder,
                 })
        {
            t(check);
            Unhold();
            Air.Enabled.V = true; Air.Dust.V = Air.Dust.Default; Air.Place.V = Air.Place.Default;
        }
        log.Info(check.Ok ? "AirAreaVariablesRegressions.SelfCheck: passed" : "AirAreaVariablesRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The dust by the player in a crypt room entered now, after three calm seconds.</summary>
    private static float Entered()
    {
        Enter(Crypt, Room(), 0f, false);
        Go(3f, Vector2.Zero);
        return Near(AirLayer.Dust, 0f, 0f, 4f);
    }

    /// <summary>The dust by the player after <paramref name="seconds"/> more calm seconds in the area as it stands.</summary>
    private static float After(float seconds)
    {
        Go(seconds, Vector2.Zero);
        return Near(AirLayer.Dust, 0f, 0f, 4f);
    }

    // Given a crypt entered with no hold, its dust lying
    // When a hold turns the Air switch off while the player stands in it
    // Then the dust stays, and the next entry lays none.
    private static void HoldOffWaits(Checker check)
    {
        float before = Entered();
        Hold((Air.Enabled.Key, "0"));
        float standing = After(5f), again = Entered();
        check.Expect(before > 0f && standing >= 0.9f * before && again == 0f,
            $"a mid-area hold off: {before:F3} m, then {standing:F3} m standing, {again:F3} m at the next entry (want some, kept, none)");
    }

    // Given the player's Air switch off and a crypt entered with no hold
    // When a hold turns the Air switch on while the player stands in it
    // Then no dust lies yet, and the next entry lays it.
    private static void HoldOnWaits(Checker check)
    {
        Air.Enabled.V = false;
        Entered();
        Hold((Air.Enabled.Key, "1"));
        float standing = After(3f), again = Entered();
        check.Expect(standing == 0f && again > 0f, $"a mid-area hold on: {standing:F3} m standing, {again:F3} m at the next entry (want none, some)");
    }

    // Given a crypt entered under a hold of the Air switch off
    // When the player turns their own switch on
    // Then no dust lies: the hold stands.
    private static void OwnUnderHold(Checker check)
    {
        Hold((Air.Enabled.Key, "0"));
        Entered();
        Air.Enabled.Set(true);
        float dust = After(3f);
        check.Expect(dust == 0f && !Air.Enabled.V, $"the player's switch on under a held off: {dust:F3} m lies, the switch reads {Air.Enabled.V} (want none, off)");
    }

    // Given a crypt entered with no hold, its dust lying
    // When the player turns their own switch off
    // Then the dust is gone at once.
    private static void OwnLive(Checker check)
    {
        Entered();
        Air.Enabled.Set(false);
        float dust = Near(AirLayer.Dust, 0f, 0f, 4f);
        check.Expect(dust == 0f, $"the player's switch off with no hold: {dust:F3} m lies (want none)");
    }

    // Given an area whose cx.air.place names city, forest, meadow, crypt or none
    // When the player enters it
    // Then it takes that place's look.
    private static void HeldPlaces(Checker check)
    {
        foreach ((string v, AirPlace want) in new[] { ("city", AirPlace.City), ("forest", AirPlace.Forest), ("meadow", AirPlace.Meadow), ("crypt", AirPlace.Crypt), ("none", AirPlace.None) })
        {
            Hold((Air.Place.Key, v));
            AirPlace got = LookOf(Plain("tdc01", Interior, "Floor")).Place;
            check.Expect(got == want, $"cx.air.place={v} takes {got} (want {want})");
        }
    }

    // Given the player's own place row at crypt, or an area that holds cx.air.place=auto
    // When the player enters an area of no place
    // Then it takes no place: neither picks one.
    private static void AutoReadsArea(Checker check)
    {
        Air.Place.Set("crypt");
        AirPlace own = LookOf(Plain("ttr01", 0, "Grass")).Place;
        Hold((Air.Place.Key, "auto"));
        AirPlace auto = LookOf(Plain("ttr01", 0, "Grass")).Place;
        check.Expect(own == AirPlace.None && auto == AirPlace.None, $"the player's crypt takes {own}, a held auto {auto} (want None both)");
    }

    // Given an area whose cx.air.enabled is 0, then one with no variables
    // When the player opens the Air rows
    // Then the Air switch is greyed in the first and live in the second.
    private static void MasterGreyed(Checker check)
    {
        Hold((Air.Enabled.Key, "0"));
        bool? held = Greyed(Air.G, Air.Enabled);
        Unhold();
        bool? free = Greyed(Air.G, Air.Enabled);
        check.Expect(held == true && free == false, $"the Air switch is greyed {held} under a hold, {free} with none (want greyed, then live)");
    }

    // Given areas whose cx.air.dust is 0, 2, -0.05 and 2.05
    // When the player enters each
    // Then the two ends of the range hold and the two past them are refused.
    private static void DustRange(Checker check)
    {
        foreach ((string v, bool takes) in new[] { ("0", true), ("2", true), ("-0.05", false), ("2.05", false) })
        {
            bool ok = ServerPolicy.Set(Air.Dust.Key, v);
            check.Expect(ok == takes && Air.Dust.Enforced == takes, $"cx.air.dust={v} held {ok} (want {takes})");
            ServerPolicy.Set(Air.Dust.Key, "");
            PolicyRefusals.Forget(Air.Dust.Key);
        }
    }

    // Given the player's Effects quality at Low, then High, then Ultra
    // When they open the Air rows
    // Then Air detail reads Low, then Medium, then High.
    private static void DetailLadder(Checker check)
    {
        string was = Quality.Effects.V;
        Quality.Effects.Set("low");
        string low = Air.Quality.V;
        Quality.Effects.Set("high");
        string high = Air.Quality.V;
        Quality.Effects.Set("ultra");
        string ultra = Air.Quality.V;
        Quality.Effects.Set(was);
        check.Expect(low == "low" && high == "medium" && ultra == "high",
            $"Air detail reads {low} at Effects Low, {high} at High, {ultra} at Ultra (want low, medium, high)");
    }
}
