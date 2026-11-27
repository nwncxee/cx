using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>Rules every place's air shares: its switch, its quality, its fog amount, a fresh area. Oracle, by hand: each
/// place's look read off the field and the particles at points chosen from the layout, the fog stage's upload head.</summary>
internal static class AirSharedApprovedExamples
{
    /// <summary>The four places: a crypt, a meadow by day, a forest, a city by the water at night.</summary>
    private static readonly string[] Places = { "crypt", "meadow", "forest", "city" };

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirSharedApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        FogOffShows(check);
        OffShowsNone(check);
        LowLiesStill(check);
        AmountThickens(check);
        AmountHolds(check);
        NotWhereNotAsked(check);
        FreshArea(check);
        OthersPart(check);
        log.Info(check.Ok ? "AirSharedApprovedExamples.SelfCheck: passed" : "AirSharedApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>Stand in place <paramref name="i"/> of <see cref="Places"/> at fog amount <paramref name="amount"/>.</summary>
    private static void Visit(int i, float amount)
    {
        switch (i)
        {
            case 0: EnterWith(Crypt, Room(), amount, false, false, true); break;
            case 1: Enter(Meadow, Flat(), amount, false); break;
            case 2: EnterWith(Forest, Flat(), amount, false, true, false); break;
            default: Enter(City, Quay(), amount, true); break;
        }
    }

    /// <summary>How much of its look place <paramref name="i"/> shows where its look lives: crypt dust by the player,
    /// meadow wisps overhead, forest haze among the trunks, night mist over the water.</summary>
    private static float Shown(int i) => i switch
    {
        0 => Near(AirLayer.Dust, 0f, 0f, 4f),
        1 => Thick(AirLayer.Wisps, 0f, 0f, 8f, 60f),
        2 => Thick(AirLayer.Haze, -10f, 0f, 4f, 1.5f),
        _ => Near(AirLayer.NightMist, 10f, 0f, 4f),
    };

    /// <summary>Everything the air shows in the place just entered: every layer's density by the player and over the
    /// water, and every particle.</summary>
    private static float Anything()
    {
        float sum = Near(Lying, 0f, 0f, 8f) + Near(Lying, 10f, 0f, 4f);
        foreach (AirLayer l in Enum.GetValues<AirLayer>())
        {
            if (l == AirLayer.None) continue;
            sum += Thick(l, 0f, 0f, 8f, 1f) + Thick(l, 0f, 0f, 8f, 60f) + Thick(l, -10f, 0f, 4f, 1.5f) + AirScene.Particles(l).Length;
        }
        return sum;
    }

    // Given Volumetric fog off and the Air switch on
    // When the player visits a crypt, a meadow by day, a forest and a city by the water at night
    // Then each shows its look as it does with Volumetric fog on.
    private static void FogOffShows(Checker check)
    {
        for (int i = 0; i < Places.Length; i++)
        {
            VolumetricFog.Enabled.V = true;
            Visit(i, 0f); Go(3f, Vector2.Zero);
            float on = Shown(i);
            VolumetricFog.Enabled.V = false;
            Visit(i, 0f); Go(3f, Vector2.Zero);
            float off = Shown(i);
            check.Expect(on > 0f && MathF.Abs(off - on) <= 1e-4f, $"E103 the {Places[i]} shows {off:F4} with Volumetric fog off, {on:F4} with it on (want the same, and some)");
        }
        VolumetricFog.Enabled.V = true;
    }

    // Given the Air switch off
    // When the player visits the same four places
    // Then none of them shows dust, motes, wisps, pollen, haze, leaves or mist.
    private static void OffShowsNone(Checker check)
    {
        Air.Enabled.V = false;
        for (int i = 0; i < Places.Length; i++)
        {
            Visit(i, 10f); Go(3f, Vector2.Zero);
            float any = Anything();
            check.Expect(any == 0f && !Drawn(), $"E104 with the air off the {Places[i]} shows {any:F4} and the fog stage draws {Drawn()} (want nothing)");
        }
        Air.Enabled.V = true;
    }

    // Given the air at Low
    // When the player visits the same four places
    // Then each shows its look as at Medium, and a gust, a blast or a walker leaves it where it lies.
    private static void LowLiesStill(Checker check)
    {
        for (int i = 0; i < Places.Length; i++)
        {
            Air.Quality.V = "medium";
            Visit(i, 0f); Go(3f, Vector2.Zero);
            float medium = Shown(i);
            Air.Quality.V = "low";
            Visit(i, 0f); Go(3f, Vector2.Zero);
            float low = Shown(i);
            check.Expect(medium > 0f && MathF.Abs(low - medium) <= 0.05f * medium, $"E105 the {Places[i]} shows {low:F4} at Low, {medium:F4} at Medium (want the same look)");
        }
        Air.Quality.V = "low";
        Visit(0, 0f); Go(3f, Vector2.Zero);
        float before = Near(AirLayer.Dust, 0f, 0f, 4f);
        Go(2f, Strong); Go(1f, Vector2.Zero);
        AirScene.Blast(new Vector3(0f, 0f, Knee), 6f); Go(0.5f, Vector2.Zero);
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, new Vector3(-3f, 0f, 0f), true);
        Walk(world, who, new Vector3(-3f, 0f, 0f), new Vector3(3f, 0f, 0f), true, Pace(6f));
        MistPartingApprovedExamples.Drop(world, who);
        float after = Near(AirLayer.Dust, 0f, 0f, 4f), path = Mean(AirLayer.Dust, -2f, -0.5f, 2f, 0.5f);
        check.Expect(MathF.Abs(after - before) <= 0.02f * before && path >= 0.98f * before,
            $"E105 at Low a gust, a blast and a walker leave the crypt's dust at {after:F4} m, {path:F4} m on the path, against {before:F4} m (want it where it lay)");
        Air.Quality.V = "medium";
    }

    // Given two areas of each place alike but for their fog amount, 0 and 10
    // When the player stands in each
    // Then the one with 10 shows more of its look, and the one with 0 still shows its base look.
    private static void AmountThickens(Checker check)
    {
        for (int i = 0; i < Places.Length; i++)
        {
            Visit(i, 0f); Go(3f, Vector2.Zero);
            float none = Shown(i);
            Visit(i, 10f); Go(3f, Vector2.Zero);
            float ten = Shown(i);
            check.Expect(none > 0f && ten > none, $"E107 the {Places[i]} shows {none:F4} at fog amount 0, {ten:F4} at 10 (want some, then more)");
        }
    }

    /// <summary>The look of place <paramref name="i"/> at fog amount <paramref name="amount"/> through a three-second gust
    /// and the calm after: its least, as a share of its settled look, and the seconds it takes to fill back to 90%.</summary>
    private static (float Least, float Back) Gusted(int i, float amount)
    {
        Visit(i, amount); Go(3f, Vector2.Zero);
        float settled = Shown(i), least = settled;
        for (int n = 0; n < 180; n++) { AirScene.Step(Frame, Strong); least = MathF.Min(least, Shown(i)); }
        float back = 0f;
        while (back < 120f && Shown(i) < 0.9f * settled) { Go(0.25f, Vector2.Zero); back += 0.25f; }
        return (settled > 0f ? least / settled : 0f, back);
    }

    // Given those pairs of areas
    // When the same gust crosses each and the air turns calm
    // Then the look is torn less, and fills back sooner, in the one with fog amount 10.
    private static void AmountHolds(Checker check)
    {
        for (int i = 0; i < Places.Length; i++)
        {
            (float least0, float back0) = Gusted(i, 0f);
            (float least10, float back10) = Gusted(i, 10f);
            check.Expect(least10 > least0 && back10 < back0,
                $"E108 the {Places[i]} keeps {least10:P0} through the gust at 10 against {least0:P0} at 0, back in {back10:F1} s against {back0:F1} s (want torn less, back sooner)");
        }
    }

    // Given a meadow at night with a lit lamp post, a forest with fog amount 15, and a city with no water whose night fog amount is 10
    // When the player stands in each
    // Then no motes float in the meadow's lamplight, no dust or mist lies on the forest floor, and no mist lies in the city's streets.
    private static void NotWhereNotAsked(Checker check)
    {
        Enter(Meadow, Flat(), 0f, true); AirScene.Lamp(Lamp, LampReach); Go(3f, Vector2.Zero);
        int motes = Within(AirLayer.Motes, Lamp, LampReach);
        EnterWith(Forest, Flat(), 15f, false, true, false); Go(3f, Vector2.Zero);
        float floor = Near(Lying, -10f, 0f, 4f);
        Enter(DryCity, Flat(), 10f, true); Go(60f, Vector2.Zero);
        float streets = Near(AirLayer.NightMist, 0f, 0f, 8f);
        check.Expect(motes == 0, $"E109 the meadow's lamplight at night holds {motes} motes (want none)");
        check.Expect(floor == 0f, $"E109 the forest at fog amount 15 lays {floor:F4} m on its floor (want none)");
        check.Expect(streets == 0f, $"E109 the dry city at night fog amount 10 lays {streets:F4} m in its streets (want none)");
    }

    // Given a gust has just torn one crypt's dust and emptied its lamplight of motes
    // When the player enters another crypt with a lamp
    // Then its dust lies whole and motes already float in its lamplight.
    private static void FreshArea(Checker check)
    {
        Visit(0, 0f); Go(3f, Vector2.Zero);
        float whole = Near(AirLayer.Dust, 0f, 0f, 4f);
        Go(1f, Vector2.Zero); Go(2f, Strong);
        float torn = Near(AirLayer.Dust, 0f, 0f, 4f);
        int emptied = Within(AirLayer.Motes, Lamp, LampReach);
        Visit(0, 0f); AirScene.Step(Frame, Strong);
        float fresh = Near(AirLayer.Dust, 0f, 0f, 4f);
        int motes = Within(AirLayer.Motes, Lamp, LampReach);
        check.Expect(emptied == 0, $"E112 Given: the gust emptied the lamplight of motes ({emptied} left)");
        check.Expect(torn < 0.8f * whole, $"E112 Given: the gust tore the first crypt's dust to {torn:F4} m from {whole:F4} m, {emptied} motes left");
        check.Expect(fresh >= 0.95f * whole && motes > 0, $"E112 the next crypt lays {fresh:F4} m at once and {motes} motes float by its lamp (want whole, some)");
    }

    /// <summary>The gap a creature other than the player leaves walking 5 m along y = 0 from x = <paramref name="x"/>, and
    /// what of it is left ten seconds on, in <paramref name="layer"/>: the depth beside the path less the depth on it, each time.</summary>
    private static (float Gap, float Left) Passer(AirLayer layer, float x)
    {
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, new Vector3(x, 0f, 0f), true);
        Still(world, Frames(0.1f));
        Walk(world, who, new Vector3(x, 0f, 0f), new Vector3(x + 5f, 0f, 0f), false, Pace(5f));
        float gap = Beside(layer, x, 0f) - Path(layer, x, 0f);
        Still(world, Frames(10f));
        float left = Beside(layer, x, 0f) - Path(layer, x, 0f);
        MistPartingApprovedExamples.Drop(world, who);
        return (gap, left);
    }

    // Given crypt dust, or night mist on the quay
    // When a creature other than the player walks 5 m through it
    // Then it leaves a gap that closes again, as E26 and E31 say.
    private static void OthersPart(Checker check)
    {
        Visit(0, 0f); Go(2f, Vector2.Zero);
        (float dustGap, float dustLeft) = Passer(AirLayer.Dust, -3f);
        Enter(City, Quay(), 10f, true); Go(Gather, Vector2.Zero);
        (float mistGap, float mistLeft) = Passer(AirLayer.NightMist, -4f);
        check.Expect(dustGap > 0.02f && dustLeft < 0.25f * dustGap, $"E113 a passer leaves a {dustGap:F4} m gap in the dust, {dustLeft:F4} m ten seconds on (want a gap that closes)");
        check.Expect(mistGap > 0.02f && mistLeft < 0.25f * mistGap, $"E113 a passer leaves a {mistGap:F4} m gap in the quay's mist, {mistLeft:F4} m ten seconds on (want a gap that closes)");
    }
}
