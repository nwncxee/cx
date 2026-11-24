using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Db;
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
            Kept(check);
            Spread(check);
            Walls(check);
            Uploads(check);
            Live(check);
            Wind(check);
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

    /// <summary>A disc lays mist within its radius only; an emitter fed 5 s on an island its size fills it to 1 - e^-2.5 m, then settles to nothing.</summary>
    private static void Rise(Checker check)
    {
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        float laid = GroundMist.At(0.5f, 0.5f), past = GroundMist.At(6.5f, 0.5f), corner = GroundMist.At(2.5f, 2.5f);
        check.Expect(laid == 1f && past == 0f && corner == 0f,
            $"a 3 m disc lays {laid} m at its centre, {past} m 6 m off, {corner} m 3.5 m off on the diagonal (want 1, then none)");

        Lay(Ground(20f, 1f, (_, _) => 0f, (x, y) => x * x + y * y <= 9f ? Stone : Nonwalk), Vector3.Zero, false, 0f);
        for (int n = 0; n < 300; n++)
        {
            GroundMist.AreaFog(false, 0f);
            GroundMist.Source(new Vector3(0f, 0f, 0.5f), 3f);
            GroundMist.Step(Frame, Vector2.Zero);
        }
        float centre = GroundMist.At(0.5f, 0.5f), rim = GroundMist.At(2.5f, 1.5f);
        check.Expect(centre > 0.9f && centre < 0.93f && rim > 0.9f && rim < 0.93f,
            $"5 s of a 3 m emitter on an island its size lays {centre:F4} m at its centre and {rim:F4} at its rim (want 0.918 both)");
        for (int n = 0; n < 60; n++) GroundMist.Step(10f, Vector2.Zero);
        float left = GroundMist.At(0.5f, 0.5f);
        bool shows = GroundMist.Shows();
        check.Expect(left == 0f && !shows, $"600 s after the emitter stops the centre holds {left} m, mist shows {shows} (want none)");
    }

    /// <summary>The live frame: an area's first frame keeps what came in before it, a second call in one frame
    /// changes nothing, and a new scene starts the field over.</summary>
    private static void Live(Checker check)
    {
        DbId a = DbId.Make(4000, 7), b = DbId.Make(4001, 7);
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Source(new Vector3(0.5f, 0.5f, 0f), 3f);
        GroundMist.Frame(a, 1, Vector3.Zero, false, 0f, 1f, Vector2.Zero);
        float first = GroundMist.At(0.5f, 0.5f);
        GroundMist.Frame(a, 1, Vector3.Zero, false, 0f, 1f, Vector2.Zero);
        float again = GroundMist.At(0.5f, 0.5f);
        GroundMist.Frame(b, 2, Vector3.Zero, false, 0f, 1f, Vector2.Zero);
        float other = GroundMist.FloorAt(0.5f, 0.5f);
        check.Expect(first > 0f && again == first && float.IsNaN(other),
            $"the live frame lays {first:F4} m from a source queued before it, {again:F4} m on a second call that frame, and the next scene floors at {other} (want some, the same, then none)");
    }

    /// <summary>The wind carries mist whichever way it blows, a long frame makes none, none pours off a deck, and area fog
    /// stays even under it: the area's floor blows in at the field's upwind edge, out at the other, and onto no wall.</summary>
    private static void Wind(Checker check)
    {
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        Run(3f, new Vector2(-MathF.Sqrt(2f), -MathF.Sqrt(2f)));
        Vector2 c = Centroid(100f, 0f, out _);
        check.Expect(c.X < -1f && c.Y < -1f, $"3 s of wind to the south-west carry a pool's centre to ({c.X:F3}, {c.Y:F3}) (want past -1 m on both)");

        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        float before = Mass(-80f, -80f, 80f, 80f);
        GroundMist.Step(1f, MistFixture.HighWindX);
        float after = Mass(-80f, -80f, 80f, 80f), want = before * MathF.Exp(-1f / 20f);
        check.Expect(MathF.Abs(after - want) <= want * 0.005f, $"one 1 s frame of high wind leaves {after:F4} m^3 of {before:F4} (want {want:F4}, settling alone)");

        var deck = Flat();
        Deck(deck, -20f, -3f, 20f, 3f, 5f, Stone);
        Lay(deck, new Vector3(0f, 0f, 5f), false, 0f);
        GroundMist.Place(new Vector3(0f, 0f, 5f), 2f, 1f);
        Run(5f, new Vector2(0f, 2f));
        float road = Mass(-40f, 3f, 40f, 40f);
        check.Expect(road == 0f, $"5 s of wind toward a deck's edge 5 m over a road pour {road:F4} m^3 onto the road (want none)");

        Fogged(Flat(), Vector3.Zero);
        Run(5f, MistFixture.HighWindX);
        float mid = GroundMist.At(0.5f, 0.5f), upwind = GroundMist.At(-127.5f, 0.5f), downwind = GroundMist.At(127.5f, 0.5f);
        check.Expect(mid > 0f && MathF.Abs(upwind - mid) <= mid * 0.01f && MathF.Abs(downwind - mid) <= mid * 0.01f,
            $"5 s of high wind over area fog leave {upwind:F4} m at the field's upwind edge, {mid:F4} mid-field, {downwind:F4} at its downwind edge (want all within 1%)");

        Lay(Ground(160f, 1f, (_, _) => 0f, (_, y) => y >= 0f && y < 2f ? Nonwalk : Stone), Vector3.Zero, true, AreaAmount);
        Run(1f, MistFixture.HighWindX);
        float wall = GroundMist.At(-127.5f, 0.5f);
        check.Expect(wall == 0f, $"a wall reaching the field's upwind edge holds {wall:F4} m after 1 s of high wind over area fog (want none)");
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
    /// <summary>Running down a slope moves mist and makes none: each frame the field holds e^(-dt/20) of the last, as settling alone would leave.</summary>
    private static void Kept(Checker check)
    {
        Lay(MistPoolApprovedExamples.SlopeGround(), new Vector3(10f, 0f, -2f), false, 0f);
        GroundMist.Place(new Vector3(2f, 0f, 0f), 3f, 1f);
        float before = Mass(-80f, -80f, 80f, 80f);
        Run(0.5f, Vector2.Zero);
        GroundMist.Step(2f, Vector2.Zero);
        float after = Mass(-80f, -80f, 80f, 80f), want = before * MathF.Exp(-2.5f / 20f);
        check.Expect(MathF.Abs(after - want) <= want * 0.005f, $"2.5 s of mist running downhill leaves {after:F4} m^3 of {before:F4} (want {want:F4}, settling alone)");
    }

    /// <summary>One 2 s frame spreads a disc's edge without turning it over: the depth still falls from its centre outward.</summary>
    private static void Spread(Checker check)
    {
        Lay(Flat(), Vector3.Zero, false, 0f);
        GroundMist.Place(Vector3.Zero, 3f, 1f);
        GroundMist.Step(2f, Vector2.Zero);
        float worst = 0f, last = GroundMist.At(0.5f, 0.5f);
        for (float x = 1.5f; x < 8f; x += 1f)
        {
            float d = GroundMist.At(x, 0.5f);
            worst = MathF.Max(worst, d - last);
            last = d;
        }
        check.Expect(worst <= 0f, $"a 2 s frame leaves a column {worst:F4} m deeper than the one inside it (want none deeper)");
    }

    /// <summary>A wall's columns hold no mist under area fog, the frame after it falls.</summary>
    private static void Walls(Checker check)
    {
        Lay(Ground(20f, 1f, (_, _) => 0f, (x, _) => x >= 4f && x < 6f ? Nonwalk : Stone), Vector3.Zero, true, AreaAmount);
        GroundMist.Step(Frame, Vector2.Zero);
        float wall = GroundMist.At(4.5f, 0.5f), ground = GroundMist.At(0.5f, 0.5f);
        check.Expect(wall == 0f && ground > 0f, $"under area fog a wall column holds {wall} m and the ground {ground} (want none on the wall)");
    }

    /// <summary>The field goes up on a frame where a column moved, by settling or by running downhill, and not on one where none did.</summary>
    private static void Uploads(Checker check)
    {
        var into = new byte[GroundMist.PackedBytes];
        Lay(MistPoolApprovedExamples.SlopeGround(), new Vector3(10f, 0f, -2f), true, AreaAmount);
        bool runs = Moves(into);
        Lay(Flat(), Vector3.Zero, true, AreaAmount);
        bool still = Moves(into);
        Lay(Ground(20f, 1f, (_, _) => 0f, (x, y) => x >= 0f && x < 1f && y >= 0f && y < 1f ? Stone : Nonwalk), Vector3.Zero, false, 0f);
        GroundMist.Place(new Vector3(0.5f, 0.5f, 0f), 1f, 1f);
        bool settles = Moves(into);
        Lay(Ground(20f, 1f, (_, _) => 0f, (x, _) => x >= 4f && x < 6f ? Nonwalk : Stone), Vector3.Zero, true, AreaAmount);
        GroundMist.Step(Frame, Vector2.Zero);
        bool banks = Moves(into, MistFixture.HighWindX);
        check.Expect(runs && !still && settles && banks,
            $"a frame uploads the field: area fog running down a slope {runs}, lying still on the flat {still}, settling on a one-metre island {settles}, banking on a wall in the wind {banks} (want true, false, true, true)");
    }

    /// <summary>Whether one frame's step under <paramref name="wind"/> makes the field go up again.</summary>
    private static bool Moves(byte[] into, Vector2 wind = default)
    {
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, into);
        GroundMist.Step(Frame, wind);
        return GroundMist.Pack(ref gen, into) > 0;
    }
}
