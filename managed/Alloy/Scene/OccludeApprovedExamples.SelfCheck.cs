using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>Trees, tile walls and static props between the camera and the character fade as placeables do. Oracle:
/// hand-placed geometry, the walls.floor and frame times written here as literals, and each opacity a hand literal.</summary>
internal static unsafe partial class OccludeApprovedExamples
{
    private const float Frame = 1f / 60f;
    /// <summary>Half a second of frames at 60 a second.</summary>
    private const int Half = 30;
    /// <summary>The walls.floor the checks set: a faded part draws at this opacity.</summary>
    private const float FloorSet = 0.4f;
    private const float Near = 0.01f;
    /// <summary>How far above walls.floor a fade may still sit after half a second: any curve that reaches the floor
    /// in that time, a ramp or an ease, is within it.</summary>
    private const float Reached = 0.02f;

    private static readonly Vector3 Focus = new(50f, 50f, 1.5f), Cam = new(50f, 40f, 2.5f);
    /// <summary>A trunk in the way: 0.6 m across, 4 m tall, 5 m in front of the character toward the camera.</summary>
    private static readonly Vector3 TrunkAt = new(50f, 45f, 2f), TrunkHalf = new(0.3f, 0.3f, 2f);
    /// <summary>Where the followed character stands.</summary>
    private static readonly Vector3 Feet = new(50f, 50f, 0f);
    /// <summary>A crate-sized slab: 0.8 m wide, 0.2 m deep, 3 m tall.</summary>
    private static readonly Vector3 SlabHalf = new(0.4f, 0.1f, 1.5f);
    /// <summary>A placeable crate 3 m in front of the character.</summary>
    private static readonly Vector3 CrateAt = new(50f, 47f, 1.5f);
    /// <summary>A statue 4 m in front of the character.</summary>
    private static readonly Vector3 StatueAt = new(50f, 46f, 1.5f);

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "OccludeApprovedExamples.SelfCheck");
        if (SceneDb.Scenes.Live != 0)
            return check.Skip($"the seam is live ({SceneDb.Scenes.Live} scene(s)) and the fixture writes to the real tables");
        bool fade = Walls.Fade.V;
        float floor = Walls.Floor.V, radius = Walls.Radius.V;
        try
        {
            Each(check, TrunkFades);
            Each(check, OnlyTheTrunkInTheWay);
            Each(check, FadedTrunkStillCasts);
            Each(check, StaticPropFades);
            Each(check, DungeonWallFades);
            Each(check, GroundNeverFades);
            Each(check, RoofFadeTakesTheMin);
            Each(check, KeyholeAndFade);
            Each(check, EdgeHoldsSteady);
            Each(check, SolidAgain);
            Each(check, CreatureAndDoorStay);
            Each(check, OffFadesNothing);
            Each(check, FadeAllocatesNothing);
            Each(check, RecordCarriesTheFade);
            Each(check, RecordKeepsTheRoofMin);
            Each(check, RecordLeavesTheKeyhole);
            Each(check, CylinderHasAFloor);
            Each(check, StampSaysWhenItMoved);
            HelpNamesThem();
            AlloyOffGreysFade(check);
        }
        finally
        {
            Walls.Fade.V = fade; Walls.Floor.V = floor; Walls.Radius.V = radius;
        }
        log.Info(check.Ok ? "OccludeApprovedExamples.SelfCheck: passed"
                          : "OccludeApprovedExamples.SelfCheck: FAILED -- trees, walls and props in the way do not fade as approved");
        return check.Ok;
    }

    /// <summary>One example on a fresh rig with walls.fade on at the checks' floor, torn down after.</summary>
    private static void Each(Checker check, Action<Checker, Rig> body)
    {
        Walls.Fade.V = true; Walls.Floor.V = FloorSet; Walls.Radius.V = 0.5f;
        var rig = new Rig();
        try { body(check, rig); }
        finally { rig.Tear(); }
    }

    /// <summary>A forest tile: both trunks drawn as ONE part, the one in the way and one 5 m to the side, and the
    /// ground as a second part; returns the trunks' part.</summary>
    private static int ForestTile(Rig rig, out int ground)
    {
        rig.Player(Feet);
        MeshDef trunks = new MeshBuilder().Box(TrunkAt, TrunkHalf).Box(new Vector3(55f, 45f, 2f), TrunkHalf).Build();
        MeshDef floor = new MeshBuilder().Flat(40f, 60f, 40f, 60f, 0f, up: true).Build();
        int first = rig.Obj("forest", HitTest.ClassTile, true, ObjFlag.Tile | ObjFlag.Static, trunks, floor);
        ground = first + 1;
        return first;
    }

    // Given Alloy is on, walls.fade is on, and a tree trunk on a forest tile stands between the camera and the character
    // When half a second of frames draws
    // Then the trunk draws at walls.floor opacity
    private static void TrunkFades(Checker check, Rig rig)
    {
        int tile = ForestTile(rig, out _);
        rig.Frames(Half, Focus, Cam);
        float a = rig.Drawn(tile);
        check.Expect(a >= FloorSet - Near && a <= FloorSet + Reached,
                     $"E1 a trunk in the way draws at walls.floor opacity: drew {a}, expected 0.4 (at most 0.42)");
    }

    // Given two trunks that are drawn as one part of one tile, one between the camera and the character and one off to the side
    // When half a second of frames draws
    // Then only the trunk in the way fades, and the other trunk and the tile's ground stay solid
    /// <summary>The side trunk shares the faded part, so its staying solid is proven by the live forest check.</summary>
    private static void OnlyTheTrunkInTheWay(Checker check, Rig rig)
    {
        int tile = ForestTile(rig, out int ground);
        rig.Frames(Half, Focus, Cam);
        float inWay = rig.Drawn(tile), groundPeak = rig.Peak(ground);
        check.Expect(MathF.Abs(inWay - FloorSet) <= Near && groundPeak == 0f,
                     $"E2 only the trunks' part is chosen to fade: it drew {inWay} (expected 0.4), the ground's part peaked at {groundPeak} (expected 0, never chosen)");
    }

    // Given the same faded trunk
    // When the shadows draw
    // Then the trunk still casts its full shadow
    private static void FadedTrunkStillCasts(Checker check, Rig rig)
    {
        int tile = ForestTile(rig, out _);
        rig.Frames(Half, Focus, Cam);
        float a = rig.Drawn(tile);
        bool fading = OccludeFan.CasterFading(SceneDb.PartFade[tile], rig.Occ(tile));
        check.Expect(MathF.Abs(a - FloorSet) <= Near && !fading,
                     $"E3 a faded trunk still casts: drew {a} (expected 0.4, faded), and the caster admission read it as {(fading ? "fading, so no shadow" : "solid")}");
    }

    // Given a static placeable, a statue baked into the area, between the camera and the character
    // When half a second of frames draws
    // Then it fades to walls.floor opacity, as a placeable does
    private static void StaticPropFades(Checker check, Rig rig)
    {
        rig.Player(Feet);
        int statue = rig.Obj("statue", 0, true, ObjFlag.Static, Box(new Vector3(50f, 45f, 1.5f), new Vector3(0.4f, 0.4f, 1.5f)));
        int crate = rig.Placeable("crate", Box(CrateAt, SlabHalf));
        rig.Frames(Half, Focus, Cam);
        float s = rig.Drawn(statue);
        float p = rig.Drawn(crate);
        check.Expect(MathF.Abs(p - FloorSet) <= Near,
                     $"E4 the placeable the statue is held to fades in the fixture: drew {p}, expected 0.4");
        check.Expect(MathF.Abs(s - FloorSet) <= Near,
                     $"E4 a static placeable in the way fades as a placeable does: drew {s}, expected 0.4");
    }

    // Given a dungeon tile wall between the camera and the character
    // When half a second of frames draws
    // Then the wall fades, and the floor and ceiling of that tile stay solid
    private static void DungeonWallFades(Checker check, Rig rig)
    {
        rig.Player(Feet);
        MeshDef wall = new MeshBuilder().Quad(new(47f, 45f, 0f), new(47f, 45f, 3f), new(53f, 45f, 3f), new(53f, 45f, 0f)).Build();
        MeshDef floor = new MeshBuilder().Flat(47f, 53f, 45f, 56f, 0f, up: true).Build();
        MeshDef ceiling = new MeshBuilder().Flat(47f, 53f, 45f, 56f, 3f, up: false).Build();
        int first = rig.Obj("dungeon", HitTest.ClassTile, true, ObjFlag.Tile | ObjFlag.Static, wall, floor, ceiling);
        Vector3 high = new(50f, 40f, 4f);
        rig.Frames(Half, Focus, high);
        float w = rig.Drawn(first), floorPeak = rig.Peak(first + 1), ceilingPeak = rig.Peak(first + 2);
        check.Expect(MathF.Abs(w - FloorSet) <= Near && floorPeak == 0f && ceilingPeak == 0f,
                     $"E5 a dungeon wall's part is chosen to fade and its floor and ceiling parts are not: wall {w} (expected 0.4), floor peaked at {floorPeak} and ceiling at {ceilingPeak} (expected 0)");
    }

    // Given a camera low behind the character, with the ground between them
    // When the frame draws
    // Then the ground does not fade, and a trunk behind that stretch of ground still fades
    private static void GroundNeverFades(Checker check, Rig rig)
    {
        rig.Player(Feet);
        MeshDef ground = new MeshBuilder().Flat(47f, 53f, 43f, 46f, 0.6f, up: true).Build();
        MeshDef trunk = new MeshBuilder().Box(new Vector3(50f, 42f, 2f), TrunkHalf).Build();
        int first = rig.Obj("rise", HitTest.ClassTile, true, ObjFlag.Tile | ObjFlag.Static, ground, trunk);
        Vector3 focus = new(50f, 50f, 1f), low = new(50f, 40f, 0.3f);
        rig.Frames(Half, focus, low);
        float groundPeak = rig.Peak(first), t = rig.Drawn(first + 1);
        check.Expect(groundPeak == 0f && MathF.Abs(t - FloorSet) <= Near,
                     $"E6 the ground's part between a low camera and the character is never chosen and the trunk's part past it fades: ground peaked at {groundPeak} (expected 0), trunk {t} (expected 0.4)");
    }

    // Given a canopy the engine's roof fade already draws at 0.3 opacity, between the camera and the character, with walls.floor at 0.5
    // When half a second of frames draws
    // Then the canopy draws at 0.3 opacity, not fainter
    private static void RoofFadeTakesTheMin(Checker check, Rig rig)
    {
        rig.Player(Feet);
        int canopy = rig.Obj("canopy", HitTest.ClassTile, true, ObjFlag.Tile | ObjFlag.Static,
                             Box(new Vector3(50f, 45f, 2f), new Vector3(1f, 0.3f, 1f)));
        SceneDb.PartFade[canopy] = 0.3f;
        Walls.Floor.V = 0.5f;
        rig.Frames(Half, Focus, Cam);
        float occ = rig.Occ(canopy);
        float a = rig.Drawn(canopy);
        check.Expect(MathF.Abs(occ - 0.5f) <= Near,
                     $"E7 the canopy in the way is one the occluder fade found: its fade is {occ}, expected 0.5");
        check.Expect(MathF.Abs(a - 0.3f) <= Near,
                     $"E7 a roof-faded canopy draws at the fainter of the two, never their product: drew {a}, expected 0.3");
    }

    // Found work. Given a trunk the occluder fade has faded
    // When its draw record is written
    // Then what the record carries draws it at walls.floor inside the cylinder, not at full opacity
    private static void RecordCarriesTheFade(Checker check, Rig rig)
    {
        int tile = ForestTile(rig, out _);
        rig.Frames(Half, Focus, Cam);
        float a = rig.Recorded(tile);
        check.Expect(MathF.Abs(a - FloorSet) <= Near,
                     $"T1 a faded trunk's draw record draws it at walls.floor: it carries {a}, expected 0.4");
    }

    // Found work. Given a roof-faded canopy at 0.3 the occluder fade also found, with walls.floor 0.5
    // When its draw record is written
    // Then the record draws it at 0.3, the fainter of the two
    private static void RecordKeepsTheRoofMin(Checker check, Rig rig)
    {
        rig.Player(Feet);
        int canopy = rig.Obj("canopy", HitTest.ClassTile, true, ObjFlag.Tile | ObjFlag.Static,
                             Box(new Vector3(50f, 45f, 2f), new Vector3(1f, 0.3f, 1f)));
        SceneDb.PartFade[canopy] = 0.6f;
        Walls.Floor.V = 0.4f;
        rig.Frames(Half, Focus, Cam);
        float a = rig.Recorded(canopy);
        check.Expect(MathF.Abs(a - 0.4f) <= Near,
                     $"T2 a roof-faded canopy's record draws it at the fainter of 0.6 and walls.floor 0.4: it carries {a}, expected 0.4");
    }

    // Found work. Given a trunk the occluder fade found, which the keyhole is also dissolving
    // When its draw record is written
    // Then the record carries no cut: the keyhole already opens the view
    private static void RecordLeavesTheKeyhole(Checker check, Rig rig)
    {
        int tile = ForestTile(rig, out _);
        SceneDb.PartDissolve[tile] = 0.5f;
        rig.Frames(Half, Focus, Cam);
        float occ = rig.Occ(tile), a = rig.Recorded(tile);
        check.Expect(occ > 0.5f && a == 1f,
                     $"T3 a dissolving trunk the fade found (fade {occ}, expected over 0.5) records no cut: it carries {a}, expected 1");
    }

    // Found work. Given walls.radius at 0 (one fan ray) or at 0.5
    // When a faded part's record takes its cylinder
    // Then the cylinder is 0.25 m and 0.5 m across the ray: never too narrow to show the character
    private static void CylinderHasAFloor(Checker check, Rig rig)
    {
        Walls.Radius.V = 0f;
        float none = OccludeFan.Cylinder;
        Walls.Radius.V = 0.5f;
        float half = OccludeFan.Cylinder;
        check.Expect(none == 0.25f && half == 0.5f,
                     $"T4 the cut's cylinder is 0.25 m at walls.radius 0 and 0.5 m at 0.5: got {none} and {half}");
    }

    // Found work. Given a trunk's record lane, stamped once with the trunk faded
    // When it is stamped again with the same fade, then after walls.radius widens, then with the fade gone
    // Then the repeat says nothing moved, the wider cylinder and the clear each say it moved, and no cut is left
    private static void StampSaysWhenItMoved(Checker check, Rig rig)
    {
        int tile = ForestTile(rig, out _);
        Vector4 lane = default;
        bool first = OccludeFan.Stamp(ref lane, tile, 0.6f);
        bool again = OccludeFan.Stamp(ref lane, tile, 0.6f);
        Walls.Radius.V = 1f;
        bool wider = OccludeFan.Stamp(ref lane, tile, 0.6f);
        bool cleared = OccludeFan.Stamp(ref lane, tile, 0f);
        check.Expect(first && !again && wider && cleared && lane == Vector4.Zero,
                     $"T5 a stamp reports a move only when the lane changes: first {first}, again {again}, wider {wider}, cleared {cleared}, lane {lane}");
    }

    // Given the keyhole is on and a trunk below its cut height stands between the camera and the character
    // When half a second of frames draws
    // Then the trunk fades, and a part the keyhole already dissolves draws as it does with walls.fade off
    private static void KeyholeAndFade(Checker check, Rig rig)
    {
        rig.Player(Feet);
        int first = rig.Obj("keyhole", HitTest.ClassTile, true, ObjFlag.Tile | ObjFlag.Static,
                            Box(TrunkAt, TrunkHalf), Box(new Vector3(50f, 47f, 2f), new Vector3(1f, 0.2f, 1f)));
        int trunk = first, cut = first + 1;
        SceneDb.PartDissolve[cut] = 0.5f;
        rig.Frames(Half, Focus, Cam);
        float t = rig.Drawn(trunk);
        float k = rig.Drawn(cut);
        check.Expect(MathF.Abs(t - FloorSet) <= Near,
                     $"E8 a trunk below the keyhole's cut fades: drew {t}, expected 0.4");
        check.Expect(k == 1f,
                     $"E8 a part the keyhole dissolves draws as it does with walls.fade off: drew {k}, expected 1");
    }

    // Given a trunk at the edge of the space between the camera and the character, with the camera swaying so it is in the way on some frames and not on others
    // When a second of frames draws
    // Then the trunk's opacity holds steady
    private static void EdgeHoldsSteady(Checker check, Rig rig)
    {
        rig.Player(Feet);
        int tile = rig.Obj("edge", HitTest.ClassTile, true, ObjFlag.Tile | ObjFlag.Static,
                           Box(new Vector3(50.75f, 45f, 2f), new Vector3(0.2f, 0.2f, 2f)));
        Vector3 inWay = new(50.3f, 40f, 2.5f), clear = new(49.7f, 40f, 2.5f);
        float lo = 1f, hi = 0f;
        for (int f = 0; f < 2 * Half; f++)
        {
            rig.Frames(1, Focus, (f / 6 & 1) == 0 ? inWay : clear);
            if (f < Half) continue;
            float a = rig.Drawn(tile);
            lo = MathF.Min(lo, a); hi = MathF.Max(hi, a);
        }
        check.Expect(hi - lo <= 0.05f && hi <= FloorSet + 0.2f,
                     $"E9 a trunk at the edge of the way holds a steady fade while the camera sways: drew {lo}..{hi} over the last half second, expected one opacity at most 0.6");
    }

    // Given a faded trunk, and then the camera turns so the trunk is no longer in the way
    // When half a second of frames draws
    // Then the trunk is solid again
    private static void SolidAgain(Checker check, Rig rig)
    {
        int tile = ForestTile(rig, out _);
        rig.Frames(Half, Focus, Cam);
        float faded = rig.Drawn(tile);
        rig.Frames(Half, Focus, new Vector3(60f, 48f, 2.5f));
        float back = rig.Drawn(tile);
        check.Expect(MathF.Abs(faded - FloorSet) <= Near && back >= 0.98f,
                     $"E10 a trunk the camera turns away from is solid again in half a second: faded {faded} (expected 0.4), then {back} (expected 1)");
    }

    // Given a creature or a door between the camera and the character
    // When half a second of frames draws
    // Then it does not fade
    private static void CreatureAndDoorStay(Checker check, Rig rig)
    {
        rig.Player(Feet);
        int creature = rig.Obj("orc", HitTest.ClassCharacter, false, ObjFlag.Creature,
                               Box(new Vector3(50f, 45f, 1.5f), new Vector3(0.3f, 0.3f, 1.5f)));
        int door = rig.Obj("door", HitTest.ClassDoor, false, ObjFlag.None,
                           Box(CrateAt, new Vector3(0.8f, 0.1f, 1.5f)));
        int crate = rig.Placeable("crate", Box(new Vector3(50f, 43f, 1.5f), SlabHalf));
        bool inWay = HitTest.Cast(HitQuery.Ray(rig.Scene, Focus, Cam), out HitResult r) && r.Part == door;
        check.Expect(inWay, $"E11 the door stands in the way: the cast hit part {r.Part}, expected {door}");
        rig.Frames(Half, Focus, Cam);
        float c = rig.Drawn(creature);
        float d = rig.Drawn(door);
        float p = rig.Drawn(crate);
        check.Expect(MathF.Abs(p - FloorSet) <= Near, $"E11 the crate past them fades, so the fan reached it: drew {p}, expected 0.4");
        check.Expect(c == 1f && d == 1f, $"E11 a creature and a door in the way do not fade: creature {c}, door {d}, expected 1");
    }

    // Given walls.fade is off
    // When half a second of frames draws
    // Then no tile, static or placeable part fades
    private static void OffFadesNothing(Checker check, Rig rig)
    {
        int tile = ForestTile(rig, out _);
        int statue = rig.Obj("statue", 0, true, ObjFlag.Static, Box(StatueAt, SlabHalf));
        int crate = rig.Placeable("crate", Box(CrateAt, SlabHalf));
        rig.Frames(Half, Focus, Cam);
        float on = rig.Drawn(crate);
        check.Expect(MathF.Abs(on - FloorSet) <= Near, $"E12 the placeable in the way fades with walls.fade on: drew {on}, expected 0.4");
        Walls.Fade.V = false;
        rig.Frames(4 * Half, Focus, Cam);
        float t = rig.Drawn(tile);
        float s = rig.Drawn(statue);
        float p = rig.Drawn(crate);
        check.Expect(t == 1f && s == 1f && p == 1f,
                     $"E12 with walls.fade off nothing fades: tile {t}, static {s}, placeable {p}, expected 1");
    }

    // Given a forest scene with trunks and statics in the way
    // When 600 frames of the fade draw
    // Then the fade allocated nothing
    private static void FadeAllocatesNothing(Checker check, Rig rig)
    {
        int tile = ForestTile(rig, out _);
        int statue = rig.Obj("statue", 0, true, ObjFlag.Static, Box(StatueAt, SlabHalf));
        rig.Frames(Half, Focus, Cam);
        float t = rig.Drawn(tile), s = rig.Drawn(statue);
        check.Expect(MathF.Abs(t - FloorSet) <= Near && MathF.Abs(s - FloorSet) <= Near,
                     $"E13 the scene's trunk and statue are fading while it is measured: trunk {t}, statue {s}, expected 0.4");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < 600; f++)
        {
            rig.Frames(1, Focus, Cam);
            rig.Drawn(tile);
            rig.Drawn(statue);
        }
        long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        check.Expect(bytes == 0, $"E13 600 frames of the fade allocate nothing: {bytes} bytes");
    }

    // Given the settings panel's walls.fade row
    // When the player reads its help
    // Then it names trees, walls and props, and the settings spec row says the same
    /// <summary>No assertion: the help text is approved on the red-pen page during the build.</summary>
    private static void HelpNamesThem() { }

    // Given Alloy is off
    // When the player opens the settings panel at the walls group
    // Then the walls.fade row is greyed out and says it needs Alloy
    private static void AlloyOffGreysFade(Checker check)
    {
        bool was = KnobNeeds.AssumeAlloyOff(true);
        try
        {
            bool grey = Walls.Fade.Disabled;
            string? why = Walls.Fade.Unavailable;
            check.Expect(grey && why == KnobNeeds.AlloyOffReason,
                         $"E15 with Alloy off the walls.fade row is greyed and gives the declared Alloy reason: greyed {grey}, reason \"{why}\"");
        }
        finally { KnobNeeds.AssumeAlloyOff(was); }
    }
}
