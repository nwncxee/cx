using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>Fog placeables lay ground mist. Oracle, by hand: a fog emitter's column holds mist and a column 20 m off
/// holds less; area fog lays one even floor, deeper at a larger fog amount; an emitter nothing names adds none.</summary>
internal static class MistPlacementApprovedExamples
{
    /// <summary>The stock fog placeable the examples name, and the radius each example reads its mist over.</summary>
    private const string StockFog = "silm_fog_50_lo";
    private const float Reach = 3f;

    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistPlacementApprovedExamples.SelfCheck");
        using var switches = MistFixture.Switches.Force();
        StockPlaceable(check);
        HighEmitter(check);
        AreaFloor(check);
        NoFog(check);
        NightFog(check);
        Campfire(check);
        HakMark(check);
        MarkedOnly(check);
        AnyCase(check);
        Off(check);
        TurnedOff(check);
        AreaChange(check);
        log.Info(check.Ok ? "MistPlacementApprovedExamples.SelfCheck: passed" : "MistPlacementApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>How long each example runs its emitters through the emitter walk: five seconds of frames.</summary>
    private const int Frames = 300;

    /// <summary>Five seconds of one Fountain emitter of <paramref name="model"/> stepped through the emitter walk.
    /// Returns its live sprites on the last frame.</summary>
    private static int Emit(string model, string node, Vector3 at)
        => EmitterSim.MistEmit(model, new[] { node }, new[] { at }, Frames)[0, Frames - 1];

    /// <summary>The mist one emitter lays in five seconds on dry flat ground, read over its reach.</summary>
    private static float Laid(string model, string node)
    {
        Lay(Flat(), Vector3.Zero, false, 0f);
        Emit(model, node, new Vector3(3f, 0f, 0.5f));
        return Near(3f, 0f, Reach);
    }

    private static void StockPlaceable(Checker check)
    {
        // Given Volumetric fog is on and an area holds a stock fog placeable (silm_fog_50_lo)
        // When the player stands beside it
        // Then the fog is denser at ground level within the placeable's reach than on open ground 20 m away, and the placeable's fog sprites do not draw
        Lay(Flat(), Vector3.Zero, false, 0f);
        int sprites = Emit(StockFog, "AuroraEmitter01", new Vector3(3f, 0f, 0.5f));
        float near = Near(3f, 0f, Reach), far = Near(23f, 0f, Reach);
        check.Expect(near > far, $"E1 mist within the placeable's reach is {near:F4} m deep, 20 m away {far:F4} m (want deeper near)");
        check.Expect(sprites == 0, $"E1 silm_fog_50_lo's emitter holds {sprites} live sprites after 5 s of the walk (want none)");
    }

    private static void HighEmitter(Checker check)
    {
        // Given Volumetric fog is on and a fog emitter floats 10 m above the ground
        // When the player looks at it
        // Then the mist lies on the ground below the emitter, not at the emitter's height
        Lay(Flat(), Vector3.Zero, false, 0f);
        Emit(StockFog, "AuroraEmitter01", new Vector3(0f, 0f, 10f));
        float floor = GroundMist.FloorAt(0f, 0f), depth = GroundMist.At(0f, 0f);
        check.Expect(floor == 0f && depth > 0f,
            $"E2 the column under an emitter at 10 m lays {depth:F4} m of mist on a floor at {floor} m (want mist, on the ground's 0 m)");
    }

    private static void AreaFloor(Checker check)
    {
        // Given Volumetric fog is on and an area with fog set holds no fog placeable
        // When the frame draws
        // Then ground mist lies evenly over the walkmesh, as dense as the area's fog amount sets
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        float lo = float.MaxValue, hi = 0f;
        for (float x = -20.5f; x < 20f; x += 1f)
            for (float y = -20.5f; y < 20f; y += 1f)
            {
                float d = GroundMist.At(x, y);
                lo = MathF.Min(lo, d); hi = MathF.Max(hi, d);
            }
        check.Expect(lo > 0f && hi - lo <= hi * 0.01f, $"E3 area fog 10 lays {lo:F4}..{hi:F4} m over 40 m of walkmesh (want above 0, even to 1%)");
        Lay(Flat(), Vector3.Zero, true, 5f);
        Run(1f, Vector2.Zero);
        float thin = GroundMist.At(0.5f, 0.5f);
        check.Expect(thin > 0f && thin < lo, $"E3 area fog 5 lays {thin:F4} m, fog 10 {lo:F4} m (want less at the smaller amount, and some)");
    }

    private static void NoFog(Checker check)
    {
        // Given Volumetric fog is on and an area sets no fog and holds no fog placeable
        // When the frame draws
        // Then no ground mist lies on the walkmesh
        Lay(Flat(), Vector3.Zero, false, 10f);
        Run(1f, Vector2.Zero);
        float m = Mass(-40f, -40f, 40f, 40f);
        check.Expect(m == 0f, $"E4 an area with no fog lays {m:F4} m^3 of mist over 80 m (want none)");
    }

    private static void NightFog(Checker check)
    {
        // Given Volumetric fog is on and an area sets night fog thicker than day fog
        // When the area turns to night
        // Then the ground mist is thicker than it was by day
        Lay(Flat(), Vector3.Zero, true, 5f);
        Run(1f, Vector2.Zero);
        float day = GroundMist.At(0.5f, 0.5f);
        GroundMist.AreaFog(true, 12f);
        Run(10f, Vector2.Zero);
        float night = GroundMist.At(0.5f, 0.5f);
        check.Expect(day > 0f && night > day, $"E5 day fog 5 lays {day:F4} m, night fog 12 {night:F4} m (want some by day, more by night)");
    }

    private static void Campfire(Checker check)
    {
        // Given Volumetric fog is on and an area holds a campfire whose smoke emitter is on no list and carries no mark
        // When the frame draws
        // Then the smoke draws as sprites and adds no mist
        Lay(Flat(), Vector3.Zero, false, 0f);
        int sprites = Emit("plc_i06", "fire!52", new Vector3(3f, 0f, 0.5f));
        float m = Mass(-10f, -10f, 16f, 10f);
        check.Expect(sprites > 0 && m == 0f, $"E6 an unmarked smoke emitter holds {sprites} live sprites (want some) and lays {m:F4} m^3 (want none)");
    }

    private static void HakMark(Checker check)
    {
        // Given Volumetric fog is on and a hak model holds an emitter named cxmist_ground
        // When the player stands beside it
        // Then it adds mist as a stock fog model does, and its sprites do not draw
        float stock = Laid(StockFog, "AuroraEmitter01");
        Lay(Flat(), Vector3.Zero, false, 0f);
        int sprites = Emit("cx_hakmodel", "cxmist_ground", new Vector3(3f, 0f, 0.5f));
        float hak = Near(3f, 0f, Reach);
        check.Expect(hak > 0f && MathF.Abs(hak - stock) <= stock * 0.01f,
            $"E7 cxmist_ground lays {hak:F4} m, silm_fog_50_lo {stock:F4} m (want the same, and some)");
        check.Expect(sprites == 0, $"E7 cxmist_ground holds {sprites} live sprites after 5 s of the walk (want none)");
    }

    private static void MarkedOnly(Checker check)
    {
        // Given Volumetric fog is on and one hak model holds a cxmist emitter and an unmarked smoke emitter
        // When the frame draws
        // Then the mist comes from the marked emitter only, and the smoke still draws as sprites
        Lay(Flat(), Vector3.Zero, false, 0f);
        int[,] live = EmitterSim.MistEmit("cx_hakcamp", new[] { "cxmist", "fire!52" },
            new[] { new Vector3(10f, 0f, 0.5f), new Vector3(-10f, 0f, 0.5f) }, Frames);
        int mist = live[0, Frames - 1], smoke = live[1, Frames - 1];
        float atMist = Near(10f, 0f, Reach), atSmoke = Near(-10f, 0f, Reach);
        check.Expect(mist == 0 && atMist > 0f, $"E8 the cxmist emitter holds {mist} live sprites (want none) and lays {atMist:F4} m (want mist)");
        check.Expect(smoke > 0 && atSmoke == 0f, $"E8 the smoke emitter holds {smoke} live sprites (want some) and lays {atSmoke:F4} m (want none)");
    }

    private static void AnyCase(Checker check)
    {
        // Given Volumetric fog is on and a hak emitter is named CXMist01
        // When the frame draws
        // Then it adds mist as cxmist01 would
        float lower = Laid("cx_hakmodel", "cxmist01"), upper = Laid("cx_hakmodel", "CXMist01");
        check.Expect(lower > 0f && upper == lower, $"E9 CXMist01 lays {upper:F4} m, cxmist01 {lower:F4} m (want the same, and some)");
    }

    private static void Off(Checker check)
    {
        // Given the Air switch is off
        // When an area with fog placeables draws
        // Then its fog sprites draw as the stock client draws them, and no mist shows, rain or dry
        Air.Enabled.V = false;
        Fogged(Flat(), Vector3.Zero);
        int sprites = Emit(StockFog, "AuroraEmitter01", new Vector3(3f, 0f, 0.5f));
        bool shows = GroundMist.Shows();
        Air.Enabled.V = true;
        check.Expect(sprites > 0, $"E10 with the Air switch off silm_fog_50_lo's emitter holds {sprites} live sprites (want some)");
        check.Expect(!shows, $"E10 with the Air switch off mist shows {shows} (want not)");
    }

    /// <summary>The frames a turned-off placeable is stepped for: half a second, enough for its birth rate to
    /// birth a sprite once the walk steps it again from the next frame.</summary>
    private const int Resume = 30;

    private static void TurnedOff(Checker check)
    {
        // Given a fog placeable in view with the Air switch on
        // When the player turns the Air switch off in the panel
        // Then its sprites draw on the next frame and the mist is gone, with no area reload
        Lay(Flat(), Vector3.Zero, false, 0f);
        bool showedOn = false, showedOff = true;
        int[,] live = EmitterSim.MistEmit(StockFog, new[] { "AuroraEmitter01" }, new[] { new Vector3(3f, 0f, 0.5f) },
            Frames + Resume, f =>
            {
                if (f == Frames) { showedOn = GroundMist.Shows(); Air.Enabled.V = false; }
                if (f == Frames + 1) showedOff = GroundMist.Shows();
            });
        Air.Enabled.V = true;
        int on = live[0, Frames - 1], off = live[0, Frames + Resume - 1];
        check.Expect(on == 0 && showedOn, $"E11 with the Air switch on the placeable holds {on} live sprites (want none) and mist shows {showedOn} (want it)");
        check.Expect(off > 0 && !showedOff,
            $"E11 after it is turned off the placeable holds {off} live sprites {Resume} frames on (want some) and mist shows {showedOff} the next frame (want not)");
    }

    private static void AreaChange(Checker check)
    {
        // Given mist lies in area A
        // When the player moves to area B
        // Then none of A's mist shows in B
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        Run(1f, Vector2.Zero);
        float inA = Near(0f, 0f, 3f);
        Lay(Flat(), Vector3.Zero, false, 0f);
        Run(1f, Vector2.Zero);
        float inB = Mass(-20f, -20f, 20f, 20f);
        check.Expect(inA > 0f && inB == 0f, $"E12 area A holds {inA:F4} m of mist (want some), area B {inB:F4} m^3 of A's (want none)");
    }
}
