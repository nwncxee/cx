using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>The field's own rules under the approved examples. Oracle, by hand: which face a column floors on, the
/// exponential rise 1 - e^(-t/2) toward an emitter's metre, and a settled field holding exactly nothing.</summary>
internal static class GroundMistSelfCheck
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GroundMistSelfCheck.SelfCheck");
        bool on = VolumetricFog.Enabled.V;
        try
        {
            VolumetricFog.Enabled.V = true;
            Floors(check);
            Triangle(check);
            Rise(check);
            NewArea(check);
            Frozen(check);
        }
        finally
        {
            VolumetricFog.Enabled.V = on;
            GroundMist.AreaEdge();
        }
        log.Info(check.Ok ? "GroundMistSelfCheck.SelfCheck: passed" : "GroundMistSelfCheck.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A column floors on the highest walked face at most the headroom over the feet, and again on the deck once the feet climb onto it.</summary>
    private static void Floors(Checker check)
    {
        var wall = Flat();
        Deck(wall, 0f, 0f, 4f, 4f, 1f, Nonwalk);
        Lay(wall, new Vector3(2f, 2f, 0f), false, 0f);
        float underWall = GroundMist.FloorAt(2.5f, 2.5f);
        check.Expect(underWall == 0f, $"a wall face 1 m up floors the column at {underWall} (want the ground, 0)");

        var deck = Flat();
        Deck(deck, 0f, 0f, 4f, 4f, 5f, Stone);
        Lay(deck, new Vector3(2f, 2f, 0f), false, 0f);
        float below = GroundMist.FloorAt(2.5f, 2.5f);
        GroundMist.Follow(new Vector3(2f, 2f, 5f));
        float onDeck = GroundMist.FloorAt(2.5f, 2.5f);
        check.Expect(below == 0f && onDeck == 5f, $"under a deck 5 m up the floor is {below}, on it {onDeck} (want 0, then 5)");

        var cellar = Flat();
        Deck(cellar, 0f, 0f, 4f, 4f, -3f, Stone);
        Lay(cellar, new Vector3(2f, 2f, 0f), false, 0f);
        float over = GroundMist.FloorAt(2.5f, 2.5f);
        check.Expect(over == 0f, $"over a cellar 3 m down the floor is {over} (want the ground, 0)");
    }

    /// <summary>A column floors only where its centre falls inside a face.</summary>
    private static void Triangle(Checker check)
    {
        var mesh = new MeshDef
        {
            Positions = new[] { Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(0f, 10f, 0f) },
            Faces = new[] { new FaceDef { V0 = 0, V1 = 1, V2 = 2, Surface = Stone, Adj0 = -1, Adj1 = -1, Adj2 = -1 } },
        };
        GroundMist.AreaEdge();
        GroundMist.TakeWalk(mesh, Matrix4x4.Identity);
        GroundMist.Follow(new Vector3(2f, 2f, 0f));
        float inside = GroundMist.FloorAt(2.5f, 2.5f), outside = GroundMist.FloorAt(8.5f, 8.5f);
        check.Expect(inside == 0f && float.IsNaN(outside), $"a triangle floors (2.5, 2.5) at {inside} and (8.5, 8.5) past its long edge at {outside} (want 0, then none)");
    }

    /// <summary>An emitter fed for 5 s fills its disc to 1 - e^-2.5 of a metre and nothing past it; fed no more, it settles to nothing.</summary>
    private static void Rise(Checker check)
    {
        Lay(Flat(), Vector3.Zero, false, 0f);
        for (int n = 0; n < 300; n++)
        {
            GroundMist.AreaFog(false, 0f);
            GroundMist.Source(new Vector3(0f, 0f, 0.5f), 3f);
            GroundMist.Step(Frame, Vector2.Zero);
        }
        float centre = GroundMist.At(0.5f, 0.5f), past = GroundMist.At(6.5f, 0.5f), corner = GroundMist.At(2.5f, 2.5f);
        check.Expect(centre > 0.9f && centre < 0.93f && past == 0f && corner == 0f,
            $"5 s of a 3 m emitter lays {centre:F4} m at its centre, {past:F4} m 6 m off, {corner:F4} m 3.5 m off on the diagonal (want 0.918, then none)");
        for (int n = 0; n < 60; n++) GroundMist.Step(10f, Vector2.Zero);
        float left = GroundMist.At(0.5f, 0.5f);
        bool shows = GroundMist.Shows(false);
        check.Expect(left == 0f && !shows, $"600 s after the emitter stops the centre holds {left} m, mist shows {shows} (want none)");
    }

    /// <summary>A new area keeps none of the old one's walkmesh.</summary>
    private static void NewArea(Checker check)
    {
        Lay(Flat(), Vector3.Zero, true, AreaAmount);
        GroundMist.AreaEdge();
        var b = new List<Tile>();
        Deck(b, 0f, 0f, 4f, 4f, 0f, Stone);
        Lay(b, new Vector3(2f, 2f, 0f), true, AreaAmount);
        float old = GroundMist.FloorAt(20.5f, 20.5f);
        check.Expect(float.IsNaN(old), $"after the area edge the old area's ground floors (20.5, 20.5) at {old} (want none)");
    }

    /// <summary>With Volumetric fog off the field holds still.</summary>
    private static void Frozen(Checker check)
    {
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        VolumetricFog.Enabled.V = false;
        Run(1f, Vector2.Zero);
        VolumetricFog.Enabled.V = true;
        float held = GroundMist.At(0.5f, 0.5f);
        check.Expect(held == 1f, $"a second with Volumetric fog off leaves {held} m of a 1 m disc (want it all)");
    }
}
