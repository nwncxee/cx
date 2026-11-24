using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Services.AreaSettings;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>Builders choose an area's air with <c>cx.air</c> variables, held as an area load holds them. Oracle, by hand:
/// the variable texts a builder types, a GIT laid out byte by byte to BioWare's GFF spec, and what the player set.</summary>
internal static class AirAreaVariablesApprovedExamples
{
    /// <summary>A GIT whose only variable is the float <c>CX.air.dust</c> = 0.4, upper-case prefix and all.</summary>
    private const string UpperGit = "R0lUIFYzLjI4AAAAAgAAAFAAAAAEAAAAgAAAAAQAAADAAAAADwAAAM8AAAAMAAAA2wAAAAgAAAD/////AAAAAAEAAAAAAAAAAAAAAAMAAAAPAAAAAAAAAAAAAAAKAAAAAQAAAAAAAAAEAAAAAgAAAAIAAAAIAAAAAwAAAM3MzD5WYXJUYWJsZQAAAAAAAAAATmFtZQAAAAAAAAAAAAAAAFR5cGUAAAAAAAAAAAAAAABWYWx1ZQAAAAAAAAAAAAAACwAAAENYLmFpci5kdXN0AQAAAAIAAAADAAAAAQAAAAEAAAA=";

    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirAreaVariablesApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        foreach (var t in new Action<Checker>[]
                 {
                     OutdoorCrypt, DisabledCrypt, ThinnerDust, SwampRefused, HoldsLeave, FogOffAirLive, HeldOn, DustHeldAtDefault,
                     QualityStaysPlayers, HeldOnLeaves, OwnDustSaved, OutOfRange, UpperPrefix, ScriptLater, SiblingPlaceRefused,
                     MeadowIndoors, ForestWater, ServerLine, OwnNeverPicks,
                 })
        {
            t(check);
            Unhold();
            Air.Enabled.V = true; Air.Quality.V = "medium"; Air.Dust.V = Air.Dust.Default; Air.Place.V = Air.Place.Default;
            VolumetricFog.Enabled.V = true;
        }
        log.Info(check.Ok ? "AirAreaVariablesApprovedExamples.SelfCheck: passed" : "AirAreaVariablesApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>An all-grass outdoor Rural area and an indoor Crypt area, as their tiles give them.</summary>
    private static AreaFeatures Outdoor => Plain("ttr01", 0, "Grass");
    private static AreaFeatures Indoor => Plain("tdc01", Interior, "Floor");

    /// <summary>The dust by the player in an area of <paramref name="f"/>'s look, held as it stands, after three seconds.</summary>
    private static float DustIn(in AreaFeatures f)
    {
        Enter(LookOf(f), Room(), 0f, false);
        Go(3f, Vector2.Zero);
        return Near(AirLayer.Dust, 0f, 0f, 4f);
    }

    /// <summary>Is <paramref name="key"/> listed under refused in <c>/policy</c>?</summary>
    private static bool Refused(string key)
        => Array.Exists(PolicyRefusals.All(), r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));

    // Given an outdoor area whose variable cx.air.place is crypt
    // When the player enters it
    // Then dust lies on its ground as in a crypt.
    private static void OutdoorCrypt(Checker check)
    {
        Hold((Air.Place.Key, "crypt"));
        AirLook look = LookOf(Outdoor);
        float dust = DustIn(Outdoor);
        check.Expect(look.Place == AirPlace.Crypt && dust > 0f, $"E74 an outdoor area held crypt takes {look.Place} and lays {dust:F3} m of dust (want Crypt, some)");
    }

    // Given a crypt whose variable cx.air.enabled is 0
    // When the player enters it
    // Then no dust lies and no motes show, and the Air switch shows greyed in the player's settings panel.
    private static void DisabledCrypt(Checker check)
    {
        Hold((Air.Enabled.Key, "0"));
        EnterWith(LookOf(Indoor), Room(), 0f, false, false, true);
        Go(3f, Vector2.Zero);
        float dust = Near(AirLayer.Dust, 0f, 0f, 4f);
        int motes = Within(AirLayer.Motes, Lamp, LampReach);
        check.Expect(dust == 0f && motes == 0, $"E75 a crypt held air off lays {dust:F3} m of dust and {motes} motes (want none)");
        check.Expect(Greyed(Air.G, Air.Enabled) == true, "E75 the Air switch shows greyed");
    }

    // Given a crypt whose variable cx.air.dust is 0.4
    // When the player enters it
    // Then less dust lies than in the same crypt with no variable.
    private static void ThinnerDust(Checker check)
    {
        float plain = DustIn(Indoor);
        Hold((Air.Dust.Key, "0.4"));
        float held = DustIn(Indoor);
        check.Expect(plain > 0f && held < plain, $"E76 the crypt held at dust 0.4 lays {held:F3} m against {plain:F3} m with no variable (want less)");
    }

    // Given an area whose variable cx.air.place is swamp, a place with no look
    // When the player enters it
    // Then the variable is listed under refused in /policy, and the area takes the look it would have with no variable.
    private static void SwampRefused(Checker check)
    {
        PolicyRefusals.Forget(Air.Place.Key);
        AirLook plain = LookOf(Indoor);
        Hold((Air.Place.Key, "swamp"));
        AirLook held = LookOf(Indoor);
        check.Expect(Refused(Air.Place.Key), "E77 cx.air.place=swamp is listed under refused");
        check.Expect(held == plain && plain.Place == AirPlace.Crypt, $"E77 the crypt still takes {held.Place} (want {plain.Place}, its own)");
        PolicyRefusals.Forget(Air.Place.Key);
    }

    // Given the player in an area whose cx.air variables hold the air
    // When they go to an area with none
    // Then that area takes its own look, and the Air rows in the panel are no longer greyed.
    private static void HoldsLeave(Checker check)
    {
        Hold((Air.Place.Key, "forest"));
        AirLook held = LookOf(Indoor);
        Unhold();
        AirLook own = LookOf(Indoor);
        check.Expect(held.Place == AirPlace.Forest && own.Place == AirPlace.Crypt, $"E83 held forest {held.Place}, then with no variables {own.Place} (want Forest, then Crypt)");
        check.Expect(Greyed(Air.G, Air.Enabled) == false && Greyed(Air.G, Air.Dust) == false, "E83 the Air rows are no longer greyed");
    }

    // Given Volumetric fog switched off in the player's settings
    // When they open the Air rows in the panel
    // Then the Air switch is not greyed and can be switched on.
    private static void FogOffAirLive(Checker check)
    {
        VolumetricFog.Enabled.V = false;
        Air.Enabled.V = false;
        bool? greyed = Greyed(Air.G, Air.Enabled);
        Air.Enabled.Set(true);
        check.Expect(greyed == false && Air.Enabled.V, $"E95 with Volumetric fog off the Air switch is greyed {greyed} and switches to {Air.Enabled.V} (want live, on)");
    }

    // Given the player switched Air off
    // When they enter an outdoor area whose only variable is cx.air.place=crypt
    // Then dust lies on its ground, and the Air switch shows on and greyed.
    private static void HeldOn(Checker check)
    {
        Air.Enabled.V = false;
        Hold((Air.Place.Key, "crypt"));
        float dust = DustIn(Outdoor);
        check.Expect(dust > 0f && Air.Enabled.V && Greyed(Air.G, Air.Enabled) == true,
            $"E147 the held area lays {dust:F3} m of dust, the switch reads {Air.Enabled.V}, greyed {Greyed(Air.G, Air.Enabled)} (want some, on, greyed)");
    }

    // Given the player set their own Dust dial to 0.2
    // When they enter an area whose only variable is cx.air.place=crypt
    // Then the dust lies as it does at the default, and the Dust row is greyed.
    private static void DustHeldAtDefault(Checker check)
    {
        float atDefault = DustIn(Indoor);
        Air.Dust.Set(0.2f);
        Hold((Air.Place.Key, "crypt"));
        float held = DustIn(Indoor);
        check.Expect(atDefault > 0f && MathF.Abs(held - atDefault) <= 1e-4f, $"E148 the held crypt lays {held:F4} m against {atDefault:F4} m at the default (want the same)");
        check.Expect(Greyed(Air.G, Air.Dust) == true, "E148 the Dust row is greyed");
    }

    // Given the player's Air quality at Low
    // When they enter an area with cx.air.place=crypt and no quality variable
    // Then Air quality stays Low and can still be changed.
    private static void QualityStaysPlayers(Checker check)
    {
        Air.Quality.Set("low");
        Hold((Air.Place.Key, "crypt"));
        check.Expect(Air.Quality.V == "low" && !Air.Quality.Enforced && Greyed(Air.G, Air.Quality) == false,
            $"E149 Air quality reads {Air.Quality.V}, held {Air.Quality.Enforced} (want low, the player's)");
    }

    // Given the player's Air switch off, and an area whose cx.air.enabled=1 held it on
    // When they go to an area with no cx.air variables
    // Then the Air switch is off again and no dust lies.
    private static void HeldOnLeaves(Checker check)
    {
        Air.Enabled.V = false;
        Hold((Air.Enabled.Key, "1"));
        bool held = Air.Enabled.V;
        Unhold();
        float dust = DustIn(Indoor);
        check.Expect(held && !Air.Enabled.V && dust == 0f, $"E150 held {held}, then the switch reads {Air.Enabled.V} and {dust:F3} m lies (want on, then off and none)");
    }

    // Given the player in an area whose cx.air.dust=0.4
    // When they save their settings and quit
    // Then cx.cfg holds their own air.dust, not 0.4.
    private static void OwnDustSaved(Checker check)
    {
        Air.Dust.Set(1.3f);
        string own = Air.Dust.Format();
        Hold((Air.Dust.Key, "0.4"));
        string[] lines = KnobFile.Compose().Split('\n');
        check.Expect(Array.IndexOf(lines, $"{Air.Dust.Key}={own}") >= 0 && Array.IndexOf(lines, $"{Air.Dust.Key}=0.4") < 0,
            $"E151 cx.cfg is written with {Air.Dust.Key}={own}, the player's own");
    }

    // Given a crypt whose cx.air.dust is 7, out of range
    // When the player enters it
    // Then cx.air.dust is listed under refused in /policy, and the area's other cx.air keys still hold.
    private static void OutOfRange(Checker check)
    {
        PolicyRefusals.Forget(Air.Dust.Key);
        Hold((Air.Dust.Key, "7"), (Air.Place.Key, "crypt"));
        check.Expect(Refused(Air.Dust.Key), "E152 cx.air.dust=7 is listed under refused");
        check.Expect(Air.Place.Enforced && LookOf(Outdoor).Place == AirPlace.Crypt, "E152 cx.air.place=crypt still holds");
        PolicyRefusals.Forget(Air.Dust.Key);
    }

    // Given a crypt whose variable is CX.air.dust=0.4
    // When the player enters it
    // Then less dust lies than with no variable.
    private static void UpperPrefix(Checker check)
    {
        float plain = DustIn(Indoor);
        Gff? git = Gff.TryParse(Convert.FromBase64String(UpperGit), out string? why);
        check.Expect(git != null, $"E153 Given: the GIT parses ({why})");
        if (git == null) return;
        AreaSettingsService.Apply(AreaSettings.Holds(AreaSettings.Read(git), KnobGroup.Find));
        float held = DustIn(Indoor);
        check.Expect(plain > 0f && held < plain, $"E153 the crypt read from CX.air.dust=0.4 lays {held:F3} m against {plain:F3} m (want less)");
    }

    // Given a crypt the player is standing in
    // When a script sets cx.air.enabled to 0 on the area
    // Then the dust stays until the player enters the area again.
    private static void ScriptLater(Checker check)
    {
        float before = DustIn(Indoor);
        Hold((Air.Enabled.Key, "0"));
        Go(5f, Vector2.Zero);
        float standing = Near(AirLayer.Dust, 0f, 0f, 4f);
        float again = DustIn(Indoor);
        check.Expect(before > 0f && standing >= 0.9f * before, $"E154 standing on, {standing:F3} m still lies against {before:F3} m (want it to stay)");
        check.Expect(again == 0f, $"E154 entering again under cx.air.enabled=0 lays {again:F3} m (want none)");
    }

    // Given an area whose cx.air.place is cave, desert or snowfield
    // When the player enters it
    // Then the variable is listed under refused in /policy, and the area takes its look as if it had no variable.
    private static void SiblingPlaceRefused(Checker check)
    {
        AirLook plain = LookOf(Indoor);
        foreach (string place in new[] { "cave", "desert", "snowfield" })
        {
            PolicyRefusals.Forget(Air.Place.Key);
            Hold((Air.Place.Key, place));
            AirLook held = LookOf(Indoor);
            check.Expect(Refused(Air.Place.Key) && held == plain, $"E155 cx.air.place={place}: refused {Refused(Air.Place.Key)}, the crypt takes {held.Place} (want refused, {plain.Place})");
            Unhold();
        }
        PolicyRefusals.Forget(Air.Place.Key);
    }

    // Given an indoor crypt whose cx.air.place is meadow
    // When the player enters it
    // Then nothing lies on the floor, as in E57.
    private static void MeadowIndoors(Checker check)
    {
        Hold((Air.Place.Key, "meadow"));
        float lies = DustIn(Indoor);
        check.Expect(lies == 0f, $"E156 a crypt held meadow lays {lies:F3} m on its floor (want none)");
    }

    // Given an outdoor area with water among its tiles whose cx.air.place is forest
    // When night falls
    // Then mist lies over the water on top of the forest haze.
    private static void ForestWater(Checker check)
    {
        AreaFeatures f = Features("ttr01", 0, 64, i => i % 4 == 0 ? new[] { "Water", "Grass", "Grass", "Grass" } : new[] { "Grass", "Grass", "Grass", "Grass" },
                                  _ => new[] { "", "", "", "" });
        Hold((Air.Place.Key, "forest"));
        AirLook look = LookOf(f);
        EnterWith(look, Stream(), 0f, false, true, false);
        AirScene.Night(true);
        Go(120f, Vector2.Zero);
        float water = Near(AirLayer.NightMist, 0f, 4f, 0.9f);
        AirLayer layers = look.Layers(true);
        check.Expect(look.Place == AirPlace.Forest && layers.HasFlag(AirLayer.Haze) && layers.HasFlag(AirLayer.NightMist),
            $"E157 the held forest by water lays {layers} at night (want haze and night mist)");
        check.Expect(water > 0f, $"E157 {water:F3} m of mist lies over the stream (want some)");
    }

    // Given a server that sends air.enabled=0 as a policy line on area entry
    // When the player enters that area
    // Then no dust lies and the Air switch is greyed.
    private static void ServerLine(Checker check)
    {
        ServerPolicy.Apply($"{Air.Enabled.Key}=0");
        float dust = DustIn(Indoor);
        bool? greyed = Greyed(Air.G, Air.Enabled);
        ServerPolicy.Set(Air.Enabled.Key, "");
        check.Expect(dust == 0f && greyed == true, $"E158 under the server's line {dust:F3} m lies and the switch is greyed {greyed} (want none, greyed)");
    }

    // Given the player's own Air rows and no area hold
    // When they enter an indoor crypt with no variables
    // Then it takes the crypt look of E78: the player's own settings never pick the place.
    private static void OwnNeverPicks(Checker check)
    {
        Air.Place.Set("forest");
        AirLook look = LookOf(Indoor);
        check.Expect(look.Place == AirPlace.Crypt, $"E159 with the player's own place row at forest the crypt takes {look.Place} (want Crypt)");
    }
}
