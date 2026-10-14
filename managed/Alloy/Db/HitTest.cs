using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>What a cast hit, in the engine's own three classes (<c>CHitInfo+0x24</c>): nothing, the area's
/// static geometry (a tile part, a static placeable, the ground plane), or an object.</summary>
public enum HitKind { None = 0, Static = 1, Object = 2 }

/// <summary>One segment cast's inputs -- the engine's <c>CHitInfo</c> input fields, named. A caller fills
/// this and the seam translates the game's <c>CHitInfo</c> into it both ways.</summary>
public struct HitQuery
{
    /// <summary>The segment, in world space. Both ends matter: the engine's tests are segment tests, not
    /// infinite rays, and the far end is what a pick's view distance sets.</summary>
    public Vector3 From, To;
    /// <summary>The scene to test (a world scene's statics and objects, or a GUI panel's objects alone).</summary>
    public DbId Scene;
    /// <summary>Surface mask, <c>CHitInfo+0x08</c>: bit <c>n</c> admits faces whose surface (material) id is
    /// <c>n</c>. Bit 30 is the trigger/area-transition bit, which the camera's own casts clear
    /// (<c>Reset(0, -0x40000001, 0)</c>). <see cref="All"/> is every bit.</summary>
    public ulong SurfaceMask;
    /// <summary>Object ids to skip -- the engine's ignore list at <c>CHitInfo+0x10</c> / <c>+0x18</c>
    /// (the followed creature, the item being dropped).</summary>
    public DbId[]? Ignore;
    /// <summary>How many entries of <see cref="Ignore"/> are live.</summary>
    public int IgnoreCount;
    /// <summary>The same list as a bitset over record indices, for the seam's long engine lists (every non-usable
    /// object is on a mouse pick's).</summary>
    public ulong[]? IgnoreBits;
    /// <summary>Box-only mode, <c>CHitInfo+0x20</c>: a mesh part is tested as its (expanded) bounds and no
    /// triangles. <c>GameCameraHitCheck</c> sets it, so EVERY mouse pick in the game is a box pick.</summary>
    public bool BoxOnly;
    /// <summary>Test objects as well as the static set. Clear is the engine's <c>CHitInfo+0x00 != 0</c>:
    /// <c>Scene::HitCheck</c> runs the BSP pass and stops.</summary>
    public bool Objects;
    /// <summary>Test the area's STATIC PLACEABLES -- the parts baked into the scene BSP that are in no object
    /// list. The engine reaches them only while <c>hitcheck_static_trimesh_parts</c> is non-zero, which is 0
    /// by default and set around its own floor casts; a CX cast says so per cast instead of toggling a global.
    /// It gates neither the triggers nor the walkmesh, so the static pass always runs (<see cref="WalkMesh"/>).</summary>
    public bool StaticPlaceables;
    /// <summary>Test the tile walkmesh parts -- the engine's <c>hitcheck_static_bsp_parts</c>, which is 1 by
    /// default, so this is true in every query the constructors below build.</summary>
    public bool WalkMesh;
    /// <summary>Admit the <b>z=0 ground plane</b>, the last arm of <c>HitPartOnBSPNode</c> @0x47b790
    /// (<see cref="HitTest.GroundPlane"/>). Vanilla clears it for a cast whose <c>CHitInfo</c> carries an
    /// ignore list, which is every mouse pick, so the seam takes it from the record rather than the
    /// default.</summary>
    public bool Ground;
    /// <summary>Reject a hit whose segment STARTS inside the part's box, <c>CHitInfo+0x78</c>. Clear (the
    /// default) accepts it with no normal and no part name, which is what the engine does.</summary>
    public bool RejectInside;
    /// <summary>Skip characters, the engine's <c>CHitInfo+0x7c</c> gate on classification 4. Nothing in the
    /// shipped client ever sets it, so the gate is dormant there and implemented here for the seam's sake.</summary>
    public bool SkipCharacters;
    /// <summary>Honour the door fade, the engine's <c>CHitInfo+0x7d</c> (set by <c>Reset</c>): a door whose
    /// fade alpha is at or below <see cref="MinDoorAlpha"/> is not hit-tested at all.</summary>
    public bool DoorAlpha;
    /// <summary>Isotropic box expansion for box-only mode -- the engine's <c>g_HitCheckBoxExpansion</c>
    /// global (<c>AurSetHitcheckBoxExpansion</c>), which widens a creature's pick box.</summary>
    public Vector3 BoxExpand;

    /// <summary>Every surface admitted.</summary>
    public const ulong All = ulong.MaxValue;
    /// <summary>Every surface except the trigger bit -- what the engine's own camera and feedback casts use.</summary>
    public const ulong NoTriggers = ulong.MaxValue & ~(1UL << TriggerSurface);
    /// <summary>The surface id a trigger prism reports (<c>PartTrigger::HitCheckGeom</c>'s gate is
    /// <c>CHitInfo+0xb &amp; 0x40</c>, and it writes <c>1 &lt;&lt; 30</c> into the hit surface).</summary>
    public const int TriggerSurface = 30;

    /// <summary>A hover/click pick, as the game issues it: objects and statics, box-only (the pickers force
    /// <c>CHitInfo+0x20 = 1</c>), the door fade honoured, and <see cref="NoTriggers"/> -- for which the real
    /// client substitutes <c>CClientExoApp::GetHitCheckMaterialMask</c>'s value, passed through by the seam.</summary>
    public static HitQuery Pick(DbId scene, Vector3 from, Vector3 to) => new()
    {
        Scene = scene, From = from, To = to, SurfaceMask = NoTriggers,
        Objects = true, StaticPlaceables = false, WalkMesh = HitTest.WalkMeshDefault, BoxOnly = true, DoorAlpha = true,
        Ground = HitTest.GroundPlane,
    };

    /// <summary>A geometry cast (camera spring arm, footstep, item drop): triangles, no triggers, statics as
    /// the caller asks.</summary>
    public static HitQuery Ray(DbId scene, Vector3 from, Vector3 to, bool statics = false) => new()
    {
        Scene = scene, From = from, To = to, SurfaceMask = NoTriggers,
        Objects = true, StaticPlaceables = statics, WalkMesh = HitTest.WalkMeshDefault, DoorAlpha = true,
        Ground = HitTest.GroundPlane,
    };
}

/// <summary>What a cast found. Every field maps to one <c>CHitInfo</c> field, so
/// <see cref="HitTest.Fill"/> can hand the game layer a record byte-identical to the engine's.</summary>
public struct HitResult
{
    /// <summary><c>CHitInfo+0x24</c>.</summary>
    public HitKind Kind;
    /// <summary>The object hit (null for a static hit), <c>CHitInfo+0x48</c>'s referent.</summary>
    public DbId Obj;
    /// <summary>The part hit, an absolute index into <c>SceneDb.Parts</c>; -1 for none. The engine keeps only
    /// the part's NAME (<c>CHitInfo+0x68</c>), which <see cref="HitTest.Fill"/> derives from this.</summary>
    public int Part;
    /// <summary>World-space hit point, <c>CHitInfo+0x28</c>.</summary>
    public Vector3 Point;
    /// <summary>The same point in the HIT PART's own space, <c>CHitInfo+0x34</c> -- the field the engine
    /// writes at the leaf and never transforms back up the chain.</summary>
    public Vector3 LocalPoint;
    /// <summary>World-space surface normal, <c>CHitInfo+0x5c</c>. Zero when the engine leaves it zero: a
    /// triangle hit on a <c>PartTriMesh</c>, and an inside-the-box hit.</summary>
    public Vector3 Normal;
    /// <summary>Distance along the segment from <see cref="HitQuery.From"/>, for callers that order casts.
    /// Not a <c>CHitInfo</c> field -- the engine re-derives it.</summary>
    public float Distance;
    /// <summary>Hit face index, <c>CHitInfo+0x58</c>; -1 when the hit was not a face (box mode, a trigger).</summary>
    public int Face;
    /// <summary>The hit face's surface (material) id, or -1. <c>CHitInfo+0x40</c> holds it as
    /// <c>1 &lt;&lt; id</c>.</summary>
    public int Surface;
    /// <summary>The trigger payload, <c>CHitInfo+0x70</c> (<c>PartTrigger+0xd0</c>): an area transition's
    /// identity. Zero for every other hit.</summary>
    public long Trigger;
}

/// <summary><b>Hit testing over CX's scene database</b>, in place of the engine's <c>Scene::HitCheck</c>
/// @0x462960 chain: the static (tile BSP) pass, class 1, then the objects, class 2, on the segment the first
/// left -- that shortening IS nearest-wins, and the gate order and <c>CHitInfo</c> fields are vanilla's. Broad
/// phase is <see cref="HitGrid"/>, triangles a per-mesh <see cref="MeshBvh"/>; allocation-free, game thread.</summary>
public static class HitTest
{
    /// <summary>The engine's <c>mindooralpha</c>, the value <c>Gob::HitCheck</c> compares <c>Gob+0x10c</c>
    /// against before it will hit-test a door. 0.25 is what the live client reports
    /// (<c>HitProbe</c> reads the symbol at boot and overwrites this), not the 0.1 the fade integrator's own
    /// constant suggests -- the two are different numbers and only this one gates a click.</summary>
    public static float MinDoorAlpha = 0.25f;

    /// <summary>The engine's <c>hitcheck_static_bsp_parts</c>, which gates the tile WALKMESH parts and is 1
    /// in <c>.data</c>. <c>HitProbe</c> reads the symbol at boot and overwrites this; the query constructors
    /// take their <see cref="HitQuery.WalkMesh"/> from it, so CX's default is the client's, not a guess.</summary>
    public static bool WalkMeshDefault = true;

    /// <summary>The engine's <c>enablenonbspgroundhit</c>, read off the LIVE client on first use -- the word
    /// is in BSS and the client writes it from its own settings long after CX loads. It is the last arm of
    /// <c>HitPartOnBSPNode</c> @0x47b790: a tile whose BSP node has no walkmesh part answers a downward
    /// segment crossing z=0 with the plane itself. Measured 0 on build 8193.37-17; ON if the symbol misses.</summary>
    public static unsafe bool GroundPlane
    {
        get
        {
            if (_groundSym == -1) _groundSym = Native.Sym(SymbolMap.EnableNonBspGroundHit);
            // Unreadable reads as OFF, not ON: the global has NO writer anywhere in the image (verified for
            // CX-118, CX-186 section 6), so vanilla never takes this arm at all. A
            // permissive default would make the z=0 plane answer casts the engine lets miss -- and it did,
            // off-device, where it answered a cast whose every real surface was masked out.
            return _groundSym != 0 && *(int*)_groundSym != 0;
        }
    }

    private static nint _groundSym = -1;

    /// <summary>Casts the z=0 plane answered.</summary>
    public static long GroundHits;

    /// <summary>Model classification bits (<c>Model+0x8e</c>, written by <c>classification()</c> @0x446440).</summary>
    public const byte ClassEffect = 1, ClassTile = 2, ClassCharacter = 4, ClassDoor = 8;

    /// <summary>Casts this period and what they walked: the counters the pick report prints. <c>FaceTests</c>
    /// is faces actually plane-tested (<see cref="MeshBvh"/> counts them), one unit on every arm.</summary>
    public static long Casts, CellVisits, ObjTests, PartTests;
    /// <inheritdoc cref="Casts"/>
    public static long FaceTests => MeshBvh.FacesTested;

    /// <summary>The candidate buffer a pass fills. One per thread, allocated once: a
    /// <c>stackalloc int[4096]</c> is a 16 KB zeroing memset per pass, which on the input path is more than
    /// the whole cast costs. The game thread owns the database (ADR 0028 §5), so the only other caller is a
    /// self-check -- hence thread-static rather than one static array.</summary>
    [ThreadStatic] private static int[]? _candidates;
    private static int[] Candidates => _candidates ??= new int[HitGrid.MaxQuery];

    /// <summary>Nearest hit on <paramref name="q"/>'s segment. False leaves <paramref name="r"/> cleared.</summary>
    public static bool Cast(in HitQuery q, out HitResult r)
    {
        long t0 = FrameZones.Now;
        r = default;
        r.Part = -1; r.Face = -1; r.Surface = -1;
        Casts++;
        Vector3 to = q.To;

        // The engine runs BSPHitCheck unconditionally on a world scene and decides INSIDE the node visitor
        // which of the three static lists to walk; which parts the pass admits is therefore a per-part gate
        // (see TestPart), not a reason to skip the pass.
        if (SceneDb.Scenes.Valid(q.Scene) && SceneDb.SceneIsWorld[q.Scene.Index])
        {
            Pass(q, statics: true, ref to, ref r);
            DbTriggers.Cast(q, ref to, ref r);
            GroundZero(q, ref to, ref r);
        }
        if (q.Objects)
            Pass(q, statics: false, ref to, ref r);

        if (r.Kind != HitKind.None)
        {
            r.Point = to;
            r.Distance = Vector3.Distance(q.From, to);
        }
        else if (IsFloorProbe(q)) NoFloor(q);
        if (IsFloorProbe(q)) Floor(q, in r);
        FrameZones.Add(FrameZones.Zone.Pick, t0);
        return r.Kind != HitKind.None;
    }

    /// <summary>Is this <c>ComputeZValue</c>'s FLOOR probe? Statics-only is its shape, but so is the camera's
    /// top-down branch, and a short near-horizontal segment at eye height is neither -- counting those as
    /// floor misses made the instrument read 28 failures where the real one was a single spot. The engine's
    /// probe is a vertical segment that descends, so that is the test.</summary>
    private static bool IsFloorProbe(in HitQuery q)
    {
        if (q.Objects || !q.WalkMesh) return false;
        Vector3 d = q.To - q.From;
        if (d.Z >= 0f) return false;
        float horiz = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
        return -d.Z > horiz;
    }

    /// <summary>Floor casts answered, and the first few per scene named in full. A statics-only cast is
    /// <c>ComputeZValue</c>'s shape, and its answer is what decides whether the engine keeps its Z=9
    /// sentinel -- so the DB side of that question gets the same instrument the engine side has
    /// (<c>HitProbe</c>'s <c>[floorcast]</c>), or the two cannot be compared at all.</summary>
    public static long FloorCasts, FloorHits;
    private static int _floorSaid;
    private static DbId _floorScene;

    private static void Floor(in HitQuery q, in HitResult r)
    {
        FloorCasts++;
        if (r.Kind != HitKind.None) FloorHits++;
        if (q.Scene != _floorScene) { _floorScene = q.Scene; _floorSaid = 0; }
        if (_floorSaid >= 8) return;
        _floorSaid++;
        Log?.Info($"[dbfloor] kind={(int)r.Kind} from=({q.From.X:F2},{q.From.Y:F2},{q.From.Z:F2}) "
                  + $"to=({q.To.X:F2},{q.To.Y:F2},{q.To.Z:F2}) hit=({r.Point.X:F3},{r.Point.Y:F3},{r.Point.Z:F3}) "
                  + $"face={r.Face} surface={r.Surface} part={r.Part} walk={q.WalkMesh} "
                  + $"statics={q.StaticPlaceables} scene={q.Scene.Value}");
    }

    /// <summary>Floor casts that found nothing, and the ones whose candidates carried no walkmesh part at all.
    /// A statics-only cast is <c>CNWCCreature::ComputeZValue</c>'s shape, and the engine KEEPS ITS SENTINEL
    /// Z of 9.0 when it misses -- so a miss here is not a quiet "no hit", it is a creature nine metres in the
    /// air and, in a fogged area, a green frame. It cost three investigations to find that once.</summary>
    public static long FloorMisses, FloorNoWalkmesh;
    private static bool _noFloorLogged;
    private static int _noFloorBoxes;
    private static DbId _boxScene;
    /// <summary>The scene the index-free sweep has already been run for: once per scene, never twice.</summary>
    private static DbId _dbScene;

    /// <summary>A floor cast missed. Counted always; said out loud ONCE, with the reason separated: either
    /// nothing was near the segment at all, or there were static candidates but not one of them carried a
    /// walkmesh part -- which is the signature of a tile whose <c>.wok</c> CX never loaded.</summary>
    private static void NoFloor(in HitQuery q)
    {
        FloorMisses++;
        int[] cells = Candidates;
        int n = HitGrid.Query(q.From, q.To, cells);
        int statics = 0, walk = 0;
        for (int k = 0; k < n; k++)
        {
            int o = cells[k];
            if (!SceneDb.Objects.LiveAt(o) || !SceneDb.HitObjStatic[o]) continue;
            statics++;
            int first = SceneDb.ObjPartFirst[o];
            for (int p = first; p < first + SceneDb.ObjPartCount[o]; p++)
            {
                ModelDef? m = SceneDb.PartModel[p];
                if (m == null || (uint)SceneDb.PartNode[p] >= (uint)m.Nodes.Length) continue;
                AabbDef? ab = m.Nodes[SceneDb.PartNode[p]].Aabb;
                if (ab != null && ab.Tree.Length > 0) { walk++; break; }
            }
        }
        if (walk == 0 && statics > 0) FloorNoWalkmesh++;
        // The walkmesh parts that WERE near the segment, with their world boxes: when the geometry is present
        // and the cast still misses, the question is placement, not loading, and this is the number that
        // separates the two.
        if (_boxScene != q.Scene) { _boxScene = q.Scene; _noFloorBoxes = 0; }
        if (walk >= 0 && _noFloorBoxes < 3)
        {
            _noFloorBoxes++;
            var sb = new System.Text.StringBuilder();
            for (int k = 0; k < n && sb.Length < 900; k++)
            {
                int o = cells[k];
                if (!SceneDb.Objects.LiveAt(o) || !SceneDb.HitObjStatic[o]) continue;
                int first = SceneDb.ObjPartFirst[o];
                sb.Append($" obj{o}[{SceneDb.ObjModel[o]?.Name ?? "-"} parts={SceneDb.ObjPartCount[o]}"
                        + $" at({SceneDb.ObjWorld[o].M41:F1},{SceneDb.ObjWorld[o].M42:F1},{SceneDb.ObjWorld[o].M43:F1})]");
                for (int pp = first; pp < first + SceneDb.ObjPartCount[o] && pp < first + 6; pp++)
                {
                    ModelDef? mm = SceneDb.PartModel[pp];
                    int ni = SceneDb.PartNode[pp];
                    sb.Append($" p{pp}(kind={SceneDb.PartKindOf[pp]} node={ni} mdl={mm?.Name ?? "NULL"}"
                            + $" nodes={mm?.Nodes.Length ?? -1} en={SceneDb.PartEnabled[pp]})");
                    if (mm == null || (uint)ni >= (uint)mm.Nodes.Length) continue;
                    NodeDef ndd = mm.Nodes[ni];
                    if (ndd.Aabb == null || ndd.Aabb.Tree.Length == 0) continue;
                    Vector3 lo = SceneDb.HitPartBMin[pp], hi = SceneDb.HitPartBMax[pp];
                    Matrix4x4 w = SceneDb.PartWorld[pp];
                    sb.Append($" [{mm.Name}/{ndd.Name} own({lo.X:F1},{lo.Y:F1},{lo.Z:F1})..({hi.X:F1},{hi.Y:F1},{hi.Z:F1})"
                            + $" at({w.M41:F1},{w.M42:F1},{w.M43:F1}) obj({SceneDb.HitObjBMin[o].X:F1},{SceneDb.HitObjBMin[o].Y:F1},{SceneDb.HitObjBMin[o].Z:F1})"
                            + $"..({SceneDb.HitObjBMax[o].X:F1},{SceneDb.HitObjBMax[o].Y:F1},{SceneDb.HitObjBMax[o].Z:F1})]");
                }
            }
            // ...and the same question asked WITHOUT the index: a record the database holds but the grid does
            // not hand back is a filing bug; no record at all is a placement or build bug. The two have
            // nothing in common but the symptom, so the dump separates them.
            int inDb = 0, inDbWalk = 0;
            // Once per SCENE, not once per session: every area after the first is a fresh set of records, and
            // the question this sweep answers -- does the database hold a floor the index will not hand back --
            // has to be asked of the area that is actually missing one.
            if (_dbScene == q.Scene) { Log?.Info($"HitTest: floor miss at ({q.From.X:F2},{q.From.Y:F2}) with {walk} walkmesh part(s) near it:{sb}"); return; }
            _dbScene = q.Scene;
            int cx = HitGrid.CellOf(q.From.X), cy = HitGrid.CellOf(q.From.Y);
            var ix = new System.Text.StringBuilder();
            for (int o2 = 1; o2 < SceneDb.Objects.High; o2++)
            {
                if (!SceneDb.Objects.LiveAt(o2) || !SceneDb.HitObjStatic[o2]) continue;
                Vector3 lo2 = SceneDb.HitObjBMin[o2], hi2 = SceneDb.HitObjBMax[o2];
                if (lo2.X > hi2.X) continue;
                if (q.From.X < lo2.X || q.From.X > hi2.X || q.From.Y < lo2.Y || q.From.Y > hi2.Y) continue;
                inDb++;
                bool hasWalk = false;
                int f2 = SceneDb.ObjPartFirst[o2];
                for (int pp = f2; pp < f2 + SceneDb.ObjPartCount[o2]; pp++)
                {
                    ModelDef? mm = SceneDb.PartModel[pp];
                    if (mm == null || (uint)SceneDb.PartNode[pp] >= (uint)mm.Nodes.Length) continue;
                    AabbDef? ab2 = mm.Nodes[SceneDb.PartNode[pp]].Aabb;
                    if (ab2 != null && ab2.Tree.Length > 0) { hasWalk = true; break; }
                }
                if (hasWalk) inDbWalk++;
                // ...and how the INDEX holds each one. A sound box plus "ent=NONE" or a rectangle that misses
                // the query's own cell is a filing fault and nothing else; it is the one thing the record's
                // own columns cannot say.
                if (ix.Length < 500)
                    ix.Append($" {SceneDb.ObjModel[o2]?.Name ?? "?"}@{o2}(walk={hasWalk}"
                            + $" scene={SceneDb.ObjScene[o2].Value} dirty={SceneDb.Objects.DirtyAt(o2)}"
                            + $" {HitGrid.Describe(o2, cx, cy)})");
            }
            Log?.Info($"HitTest: floor miss at ({q.From.X:F2},{q.From.Y:F2}) z {q.From.Z:F2}->{q.To.Z:F2} "
                      + $"in cell ({cx},{cy}) of scene {q.Scene.Value} with {walk} walkmesh part(s) near it; "
                      + $"the DATABASE holds {inDb} static(s) over that point, {inDbWalk} with a walkmesh:{ix}"
                      + $" || candidates:{sb}");
        }
        if (_noFloorLogged) return;
        _noFloorLogged = true;
        Log?.Always($"HitTest: a FLOOR cast found no ground at ({q.From.X:F2},{q.From.Y:F2}) -- "
                  + $"{statics} static record(s) near the segment, {walk} of them with a walkmesh part. "
                  + (walk == 0 && statics > 0
                     ? "None carries one, so this area has no floor where the cast went: either the tile's "
                     + "walkmesh never loaded (it is the .mdl's own `node aabb`, or failing that the separate "
                     + ".wok resource, restype 2016 -- TileBuild says which), or the record carrying it is not "
                     + "in the hit index. The engine keeps ComputeZValue's sentinel Z of 9.0 on a miss, which "
                     + "is the +9 camera."
                     : "The segment reached no static geometry at all."));
    }

    /// <summary>Vanilla's LAST static arm: the z=0 ground plane (<see cref="GroundPlane"/>).
    /// <c>HitPartOnBSPNode</c> @0x47b790 runs it per walkmesh-less BSP node for a segment crossing z=0
    /// downwards; here it runs once, after the static pass and on the segment that pass left -- the same
    /// answer, since the plane is global and nearest-wins has already shortened past anything above it.</summary>
    private static void GroundZero(in HitQuery q, ref Vector3 to, ref HitResult r)
    {
        if (!q.Ground || !q.WalkMesh) return;
        if (q.From.Z <= 0f || to.Z >= 0f) return;
        float t = q.From.Z / (q.From.Z - to.Z);
        Vector3 p = Vector3.Lerp(q.From, to, t);
        to = p;
        r.Kind = HitKind.Static;
        r.Obj = default;
        r.Part = -1;
        r.Face = -1;
        r.Surface = -1;
        r.LocalPoint = p;
        // planelineintersection against the plane {normal (0,0,1), d 0}: the engine writes that normal.
        r.Normal = Vector3.UnitZ;
        GroundHits++;
    }

    /// <summary>One of the two passes: the area's static set, or the objects. Both walk the same grid and the
    /// same per-object test; which records they admit is the only difference, which is why the engine's two
    /// quite different walks (a BSP recursion and a gob-list loop) collapse into one here.</summary>
    private static void Pass(in HitQuery q, bool statics, ref Vector3 to, ref HitResult r)
    {
        int[] cells = Candidates;
        int n = HitGrid.Query(q.From, to, cells);
        CellVisits += n;
        for (int k = 0; k < n; k++)
        {
            int o = cells[k];
            if (!SceneDb.Objects.LiveAt(o)) continue;
            if (SceneDb.HitObjStatic[o] != statics) continue;
            // The engine's BSP pass applies NONE of the object gates -- no classification, no alpha, no door
            // test, no ignore list -- because the static set is in no object list for them to be read off.
            // A grafted body piece is its creature's in the engine (one part tree, Gob::Append @0x4422a0), so the
            // creature's gates apply and the creature is what the hit reports: the game reads the hit object's
            // external data type (5 = creature), which only the creature's own record carries.
            int own = statics ? o : DbAnim.GraftRoot(o);
            if (!statics && (!Admits(q, own) || (own != o && !SceneDb.ObjVisible[o]))) continue;
            if (statics && SceneDb.ObjScene[o] != q.Scene) continue;
            ObjTests++;
            int hit = own;
            if (TestObject(q, statics, o, ref to, ref r, ref hit))
            {
                r.Kind = statics ? HitKind.Static : HitKind.Object;
                r.Obj = SceneDb.Objects.IdAt(hit);
            }
        }
    }

    /// <summary>The per-object gates, in the engine's own order: scene membership, visibility, the ignore
    /// list, the classification tests, and the two alpha gates.</summary>
    private static bool Admits(in HitQuery q, int o)
    {
        if (SceneDb.ObjScene[o] != q.Scene) return false;
        if (!SceneDb.ObjVisible[o]) return false;
        byte cls = SceneDb.HitObjClass[o];
        if ((cls & ClassEffect) != 0) return false;
        if (q.SkipCharacters && (cls & ClassCharacter) != 0) return false;
        // Scene::HitCheckGob @0x45f860: an object at or below zero alpha is not in the pick at all.
        if (SceneDb.HitObjAlpha[o] <= 0f) return false;
        if (q.DoorAlpha && (cls & ClassDoor) != 0 && SceneDb.HitObjFadeAlpha[o] <= MinDoorAlpha) return false;
        if (q.IgnoreBits != null && (q.IgnoreBits[o >> 6] & (1UL << (o & 63))) != 0) return false;
        if (q.Ignore != null)
        {
            DbId id = SceneDb.Objects.IdAt(o);
            for (int i = 0; i < q.IgnoreCount; i++) if (q.Ignore[i] == id) return false;
        }
        return true;
    }

    /// <summary>One object: the sphere and box rejects, then every part of its range against the shortened
    /// segment. Returns true when the segment got shorter, i.e. this object owns the nearest hit so far;
    /// <paramref name="hit"/> becomes the record the hit reports when that is not the object itself.</summary>
    private static bool TestObject(in HitQuery q, bool statics, int o, ref Vector3 to, ref HitResult r,
                                   ref int hit)
    {
        if (!SphereHit(q.From, to, SceneDb.HitObjSphereC[o], SceneDb.HitObjSphereR[o])) return false;
        Vector3 lo = SceneDb.HitObjBMin[o], hi = SceneDb.HitObjBMax[o];
        if (lo.X > hi.X) return false;
        if (!BoxHit(q.From, to, lo, hi)) return false;

        // A text gob's hit test is its OWN, not its parts': StringGob::HitCheck @0x492920 polyhits the quad, and
        // only on a quad hit casts on into the models riding its visible lines. A GUI list row has no parts at
        // all, so the part loop answered nothing for it.
        if (SceneDb.ObjKindOf[o] == ObjKind.Text)
        {
            if (!HitBounds.TextQuad(o, out Vector3 qlo, out Vector3 qhi) || !TestTextQuad(q, o, qlo, qhi, ref to, ref r))
                return false;
            LineModels(q, o, ref to, ref r, ref hit);
            return true;
        }

        bool any = false;
        int first = SceneDb.ObjPartFirst[o], count = SceneDb.ObjPartCount[o];
        for (int p = first; p < first + count; p++)
        {
            if (SceneDb.PartMerged[p] || !SubtreeEnabled(first, p)) continue;
            if (TestPart(q, statics, p, ref to, ref r)) { any = true; r.Part = p; }
        }
        return any;
    }

    /// <summary>The segment against a text gob's quad, in the gob's own frame -- the <c>polyhit</c> call at the
    /// top of <c>StringGob::HitCheck</c> @0x492920, which for a planar convex quad is the plane crossing plus a
    /// rectangle test. The engine transforms by position and orientation only; the object's matrix is used here
    /// instead, so a scaled text gob's quad scales with it rather than staying its unscaled size.</summary>
    private static bool TestTextQuad(in HitQuery q, int o, in Vector3 lo, in Vector3 hi,
                                     ref Vector3 to, ref HitResult r)
    {
        Matrix4x4 w = SceneDb.ObjWorld[o];
        if (!Matrix4x4.Invert(w, out Matrix4x4 inv)) return false;
        Vector3 a = Vector3.Transform(q.From, inv), b = Vector3.Transform(to, inv);
        float dz = b.Z - a.Z;
        if (MathF.Abs(dz) < 1e-12f) return false;
        float t = -a.Z / dz;
        if (t < 0f || t > 1f) return false;
        Vector3 p = a + (b - a) * t;
        if (p.X < lo.X - EdgeEps || p.X > hi.X + EdgeEps || p.Y < lo.Y - EdgeEps || p.Y > hi.Y + EdgeEps)
            return false;
        // No part, so no part-space point, no face and no surface: the engine's own answer for a quad hit
        // (its ClearOutput zeroes all three and HitCheck writes only the world point).
        to = Vector3.Transform(p, w);
        r.Part = -1;
        r.LocalPoint = Vector3.Zero;
        r.Face = -1;
        r.Surface = 0;
        r.Trigger = 0;
        r.Normal = Vector3.Zero;
        return true;
    }

    /// <summary>The rest of <c>StringGob::HitCheck</c> @0x492920: after a quad hit, each visible line's attached
    /// model is cast in that line's frame, and a model hit is what the cast reports -- the only way to reach a chat
    /// line's reply button (hover: a party member's status, click: a tell). The frames are <see cref="DbGui"/>'s;
    /// no gate but geometry, as the engine calls the model's own <c>HitCheck</c> directly.</summary>
    private static void LineModels(in HitQuery q, int o, ref Vector3 to, ref HitResult r, ref int hit)
    {
        QuadList? lines = TextObjects.Of(SceneDb.Objects.IdAt(o))?.PublishedQuads;
        if (lines == null) return;
        for (int k = 0; k < lines.AttachedCount; k++)
        {
            var (model, ax, ay) = lines.Attached[k];
            if (!SceneDb.Objects.Valid(model)) continue;
            Matrix4x4 frame = Matrix4x4.CreateTranslation(ax, ay, 0f) * SceneDb.ObjWorld[o];
            if (!Matrix4x4.Invert(frame, out Matrix4x4 inv)) continue;
            HitQuery lq = q;
            lq.From = Vector3.Transform(q.From, inv);
            Vector3 lto = Vector3.Transform(to, inv);
            lq.To = lto;
            int m = model.Index, first = SceneDb.ObjPartFirst[m], count = SceneDb.ObjPartCount[m];
            bool any = false;
            for (int p = first; p < first + count; p++)
            {
                if (SceneDb.PartMerged[p] || !SubtreeEnabled(first, p)) continue;
                if (TestPart(lq, false, p, ref lto, ref r)) { any = true; r.Part = p; }
            }
            if (!any) continue;
            to = Vector3.Transform(lto, frame);
            hit = m;
        }
    }

    /// <summary>Is <paramref name="model"/> riding one of text object <paramref name="text"/>'s visible lines?</summary>
    public static bool RidesLine(DbId text, DbId model)
    {
        QuadList? lines = TextObjects.Of(text)?.PublishedQuads;
        if (lines == null || model.IsNull) return false;
        for (int k = 0; k < lines.AttachedCount; k++) if (lines.Attached[k].Model == model) return true;
        return false;
    }

    /// <summary>Is <paramref name="p"/> drawable AND every ancestor of it too? <c>Part::HitCheck</c> @0x4394e0
    /// reaches a child only through its parent, so a disabled part prunes its whole subtree -- which a walk
    /// over the flattened part range loses unless the bit is inherited. Short chain, allocation-free, so it
    /// is re-derived rather than kept in a column.</summary>
    private static bool SubtreeEnabled(int first, int p)
    {
        for (int i = p; i >= first; i = SceneDb.PartParent[i])
        {
            if (!SceneDb.PartEnabled[i]) return false;
            if (SceneDb.PartParent[i] < 0) break;
        }
        return true;
    }

    /// <summary>One part: the segment into part space, the part's own bounds, then its geometry -- the
    /// engine's <c>Part::HitCheck</c> + <c>Part::HitCheckGeom</c> pair, flattened (the DB's part range is
    /// already the tree in pre-order, and <see cref="SubtreeEnabled"/> is what the recursion's early return
    /// expressed).</summary>
    private static bool TestPart(in HitQuery q, bool statics, int p, ref Vector3 to, ref HitResult r)
    {
        Vector3 bmin = SceneDb.HitPartBMin[p], bmax = SceneDb.HitPartBMax[p];
        if (bmin.X > bmax.X) return false;
        ModelDef? m = SceneDb.PartModel[p];
        NodeDef? nd = m != null && (uint)SceneDb.PartNode[p] < (uint)m.Nodes.Length ? m.Nodes[SceneDb.PartNode[p]] : null;
        if (nd == null || nd.Mesh == null) return false;
        bool walkmesh = nd.Aabb != null && nd.Aabb.Tree.Length > 0;
        bool trigger = nd.Kind == NodeKind.Trigger;

        // Which of the three static lists this part would have been on, and whether this cast walks it.
        // HitPartOnBSPNode @0x47b790: triggers with no gate, the static trimesh parts behind
        // hitcheck_static_trimesh_parts (0 by default), the tile walkmesh behind hitcheck_static_bsp_parts (1).
        if (statics && !(trigger || (walkmesh ? q.WalkMesh : q.StaticPlaceables))) return false;

        if (!Matrix4x4.Invert(SceneDb.PartWorld[p], out Matrix4x4 inv)) return false;
        PartTests++;
        Vector3 a = Vector3.Transform(q.From, inv), b = Vector3.Transform(to, inv);

        // The trigger prism: its own surface bit gates it, as PartTrigger::HitCheckGeom @0x4d9cb0 does; it is
        // never box-tested (that function does not read CHitInfo+0x20 at all) and it ignores the accumulated
        // scale entirely, so the part-space segment is re-multiplied by it before the untransformed prism is
        // tested, and the hit divided back out.
        if (trigger)
        {
            if ((q.SurfaceMask >> HitQuery.TriggerSurface & 1) == 0) return false;
            float ts = Scale(SceneDb.PartWorld[p]);
            Vector3 ta = a * ts, tb = b * ts;
            if (!BoxHit(ta, tb, bmin, bmax)) return false;
            int tf = MeshBvh.For(nd.Mesh).Cast(ta, ref tb, 1f, HitQuery.All, out Vector3 tn);
            if (tf < 0) return false;
            // The trigger arm writes the point, the surface bit and the payload, and never touches the face
            // index -- it does not ClearOutput either, so the caller's -1 is what the game reads back.
            Accept(p, ref to, ref r, ts > 1e-8f ? tb / ts : tb, tn, -1, HitQuery.TriggerSurface,
                   SceneDb.HitPartTrigger[p], localPoint: true);
            return true;
        }

        // BLOCKER: box-only is PartTriMesh's alone. PartAABB::HitCheckGeom @0x43dd70 never reads
        // CHitInfo+0x20, so a walkmesh part is tested per triangle even on a mouse pick -- and it must be,
        // or the pick collapses onto the camera's own position the moment the camera is inside the tile's
        // walkmesh box, which it always is.
        if (q.BoxOnly && !walkmesh)
        {
            // g_HitCheckBoxExpansion is added AFTER the engine scales the box by the accumulated scale, so in
            // the unscaled part space the segment lives in, the expansion divides by that scale.
            float bs = Scale(SceneDb.PartWorld[p]);
            Vector3 ex = bs > 1e-8f ? q.BoxExpand / bs : q.BoxExpand;
            Vector3 lo = bmin - ex, hi = bmax + ex;
            int face = BoxFace(a, ref b, lo, hi, out Vector3 bn);
            if (face == 0) return false;
            if (face < 0)
            {
                if (q.RejectInside) return false;
                // The segment STARTS inside this part, so the hit is at distance zero and nothing can be
                // nearer: collapse the segment to its origin. Vanilla leaves it alone, which makes its
                // answer depend on the order it happened to visit the objects in -- the one place the
                // engine's own nearest-wins mechanism has a hole.
                Accept(p, ref to, ref r, a, Vector3.Zero, -1, -1, 0, localPoint: false);
                to = q.From;
                return true;
            }
            // The box arm writes CHitInfo+0x28 and nothing else past ClearOutput: the part-space point at
            // +0x34 stays ZERO on a box pick, and so does the face index.
            Accept(p, ref to, ref r, b, bn, -1, -1, 0, localPoint: false);
            return true;
        }

        if (!BoxHit(a, b, bmin, bmax)) return false;
        int f;
        Vector3 nrm;
        int surf;
        if (walkmesh)
        {
            f = MeshBvh.CastAabb(nd.Aabb!, nd.Mesh, a, ref b, 1f, q.SurfaceMask, out nrm);
            if (f < 0) return false;
            // The AABB walk is the one arm that writes the face index, the surface bit and a normal.
            surf = nd.Mesh.Faces[f].Surface;
        }
        else
        {
            // The surface mask is NOT consulted here, and that is the engine's behaviour, not an oversight:
            // only HitCheckAABBnode @0x43da50 tests it, so a plain PartTriMesh's triangles are hit whatever
            // the mask says. A walkmesh part (PartAABB) is the one that filters.
            f = MeshBvh.For(nd.Mesh).Cast(a, ref b, 1f, HitQuery.All, out _);
            if (f < 0) return false;
            // The triangle arm ClearOutputs and then writes only the two points: no normal (the 7-argument
            // polyhit has none to give), no surface bit, and the face index stays at ClearOutput's -1.
            nrm = Vector3.Zero;
            surf = -1;
            f = -1;
        }
        Accept(p, ref to, ref r, b, nrm, f, surf, 0, localPoint: true);
        return true;
    }

    /// <summary>The uniform scale a part's world matrix carries -- the engine's accumulated scale argument.
    /// An Aurora transform is restricted to a uniform scale, so one row length is the whole answer.</summary>
    private static float Scale(in Matrix4x4 w)
        => MathF.Sqrt(w.M11 * w.M11 + w.M12 * w.M12 + w.M13 * w.M13);

    /// <summary>Take a hit: the part-space point back to world through the part's matrix, the normal with it,
    /// and the segment shortened so every later test answers against the nearer hit.</summary>
    private static void Accept(int p, ref Vector3 to, ref HitResult r, Vector3 local, Vector3 normal,
                               int face, int surface, long trigger, bool localPoint)
    {
        Matrix4x4 w = SceneDb.PartWorld[p];
        to = Vector3.Transform(local, w);
        // CHitInfo+0x34 is written by the triangle and AABB arms and by nothing else, so a BOX hit leaves it
        // at ClearOutput's zero -- a caller that reads a part-space point off a mouse pick is reading the
        // engine's zero, and must see ours.
        r.LocalPoint = localPoint ? local : Vector3.Zero;
        r.Face = face;
        r.Surface = surface;
        r.Trigger = trigger;
        // The engine rotates the normal by each level's quaternion; the matrix carries the same rotation, and
        // normalising absorbs the uniform scale an Aurora transform is restricted to.
        if (normal != Vector3.Zero)
        {
            Vector3 n = Vector3.TransformNormal(normal, w);
            float len = n.Length();
            r.Normal = len > 1e-8f ? n / len : Vector3.Zero;
        }
        else r.Normal = Vector3.Zero;
    }

    // ---- geometry primitives, the engine's semantics ----

    /// <summary>Boundary slack for the box and triangle tests. A tile walkmesh's edge is the next tile's edge, and
    /// a floor probe that lands exactly on it (the ruins spawn at y = 20.00) came out a few ulps outside BOTH
    /// tiles after the world-to-part transform -- vanilla hits face 42 there, CX hit nothing and the creature
    /// kept ComputeZValue's 9.0 sentinel. Far below any modelled feature (1e-4 m on a 10 m tile).</summary>
    public const float EdgeEps = 1e-4f;

    /// <summary>The engine's <c>BoxIntersect(from, to, min, max)</c> as a predicate: does the SEGMENT touch
    /// the box at all.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool BoxHit(Vector3 from, Vector3 to, Vector3 min, Vector3 max)
    {
        Vector3 d = to - from;
        float t0 = 0f, t1 = 1f;
        for (int i = 0; i < 3; i++)
        {
            float o = i == 0 ? from.X : i == 1 ? from.Y : from.Z;
            float dd = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            float lo = i == 0 ? min.X : i == 1 ? min.Y : min.Z;
            float hi = i == 0 ? max.X : i == 1 ? max.Y : max.Z;
            lo -= EdgeEps; hi += EdgeEps;
            if (MathF.Abs(dd) < 1e-12f) { if (o < lo || o > hi) return false; continue; }
            float a = (lo - o) / dd, b = (hi - o) / dd;
            if (a > b) (a, b) = (b, a);
            if (a > t0) t0 = a;
            if (b < t1) t1 = b;
            if (t0 > t1) return false;
        }
        return true;
    }

    /// <summary>The engine's <c>BoxIntersect</c> with its face code: 0 miss, -1 the segment started inside,
    /// else 1..6 naming the face (axis <c>(code-1)/2</c>, sign +1 when <c>(code-1)&amp;1</c>), which is how
    /// <c>PartTriMesh::HitCheckGeom</c>'s box arm gets a normal. <paramref name="to"/> is shortened to the
    /// entry point on a face hit, left alone on an inside hit.</summary>
    public static int BoxFace(Vector3 from, ref Vector3 to, Vector3 min, Vector3 max, out Vector3 normal)
    {
        normal = default;
        if (from.X >= min.X && from.X <= max.X && from.Y >= min.Y && from.Y <= max.Y
            && from.Z >= min.Z && from.Z <= max.Z) return -1;
        Vector3 d = to - from;
        float best = float.MaxValue;
        int code = 0;
        for (int i = 0; i < 3; i++)
        {
            float o = i == 0 ? from.X : i == 1 ? from.Y : from.Z;
            float dd = i == 0 ? d.X : i == 1 ? d.Y : d.Z;
            if (MathF.Abs(dd) < 1e-12f) continue;
            for (int s = 0; s < 2; s++)
            {
                float plane = s == 0 ? (i == 0 ? min.X : i == 1 ? min.Y : min.Z)
                                     : (i == 0 ? max.X : i == 1 ? max.Y : max.Z);
                float t = (plane - o) / dd;
                if (t < 0f || t > 1f || t >= best) continue;
                Vector3 h = from + d * t;
                if (i != 0 && (h.X < min.X || h.X > max.X)) continue;
                if (i != 1 && (h.Y < min.Y || h.Y > max.Y)) continue;
                if (i != 2 && (h.Z < min.Z || h.Z > max.Z)) continue;
                best = t;
                code = i * 2 + s + 1;
                normal = i == 0 ? new Vector3(s == 0 ? -1f : 1f, 0, 0)
                       : i == 1 ? new Vector3(0, s == 0 ? -1f : 1f, 0)
                                : new Vector3(0, 0, s == 0 ? -1f : 1f);
            }
        }
        if (code == 0) return 0;
        to = from + d * best;
        return code;
    }

    /// <summary>The engine's <c>SphereIntersect(from, to, centre, radius)</c>: does the segment touch the
    /// sphere. A non-positive radius never rejects (an object whose sphere was never computed).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SphereHit(Vector3 from, Vector3 to, Vector3 c, float radius)
    {
        if (radius <= 0f) return true;
        Vector3 d = to - from, m = from - c;
        float dd = Vector3.Dot(d, d);
        float t = dd <= 1e-12f ? 0f : Math.Clamp(-Vector3.Dot(m, d) / dd, 0f, 1f);
        Vector3 q = m + d * t;
        return Vector3.Dot(q, q) <= radius * radius;
    }

    /// <summary>The engine's <c>polyhit</c> (@0x5b71a0, the precomputed-plane overload) for one triangle,
    /// shortening <paramref name="to"/> to the hit. ONE-SIDED, as the engine's is: the segment must start on
    /// or in front of the plane and end on or behind it, so a back face is never hit -- which is why a pick
    /// inside a building does not catch the far wall.</summary>
    public static bool PolyHit(Vector3 a, Vector3 b, Vector3 c, Vector3 from, ref Vector3 to)
    {
        Vector3 n = Vector3.Cross(b - a, c - a);
        float len = n.Length();
        if (len < 1e-8f) return false;
        return PolyHit(a, b, c, n / len, -Vector3.Dot(n / len, a), from, ref to);
    }

    /// <summary>...with the face's STORED plane, which is what the engine passes (the 7-argument
    /// <c>polyhit</c> takes <c>face+0</c> as the normal and <c>face+0xc</c> as the distance and never
    /// re-derives them). Cheaper, and sign-correct by construction rather than by winding.</summary>
    public static bool PolyHit(Vector3 a, Vector3 b, Vector3 c, Vector3 n, float d, Vector3 from, ref Vector3 to)
    {
        float f0 = Vector3.Dot(n, from) + d, f1 = Vector3.Dot(n, to) + d;
        if (f0 < 0f || f1 > 0f || f0 == f1) return false;
        float t = f0 / (f0 - f1);
        Vector3 h = from + (to - from) * t;
        if (n.LengthSquared() < 1e-16f) return false;
        if (Vector3.Dot(Vector3.Cross(b - a, h - a), n) < -EdgeEps) return false;
        if (Vector3.Dot(Vector3.Cross(c - b, h - b), n) < -EdgeEps) return false;
        if (Vector3.Dot(Vector3.Cross(a - c, h - c), n) < -EdgeEps) return false;
        to = h;
        return true;
    }

    /// <summary>Fill a game-visible <c>CHitInfo</c> at <paramref name="info"/> from <paramref name="r"/> as the
    /// engine does: the OUTPUT fields only, the caller's inputs left as the game set them. <c>+0x48</c> takes
    /// the game's own <c>CAurObject*</c> facade, not a DB handle; <c>+0x50</c>'s back-index is -1. Wants a
    /// record already through <c>CHitInfo::Reset</c> -- the assignment reads <c>+0x48</c> to unregister it.</summary>
    public static unsafe void Fill(in HitResult r, byte* info)
    {
        var h = new CHitInfo((nint)info);
        h.Kind = (int)r.Kind;
        h.Point = r.Point;
        h.LocalPoint = r.LocalPoint;
        h.Normal = r.Normal;
        h.Surface = r.Surface >= 0 ? 1L << (r.Surface & 63) : 0L;
        h.Face = r.Face;
        // Vanilla leaves the object pointer NULL on a class-1 (static) hit: Scene::HitCheck assigns the
        // SafePointer from HitCheckGob's return, which is zero unless the object pass took the hit.
        nint facade = r.Kind != HitKind.Object || r.Obj.IsNull || !SceneDb.Objects.Valid(r.Obj)
                        ? 0 : SceneDb.ObjFacade[r.Obj.Index];
        AssignObject(info, facade);
        h.PartName = r.Part >= 0 ? PartName(r.Part) : 0;
        h.Trigger = r.Trigger;
    }

    /// <summary>Put <paramref name="facade"/> (or null) into <c>CHitInfo+0x48</c> through the engine's own
    /// <c>SafePointer&lt;CAurObject&gt;::operator=</c>: it unregisters the record from whatever object it held
    /// and takes a real back-reference index. A hand-written pointer plus a -1 index is read later as
    /// "registered at index -1" and indexes <c>array[-1]</c>. Unresolved symbol: left as <c>Reset</c> left it.</summary>
    public static unsafe void AssignObject(byte* info, nint facade)
    {
        SafePointerObjectAssignFn fn = EngineFn.SafePointerObjectAssign;
        var h = new CHitInfo((nint)info);
        if (fn != null) { fn((nint)Unsafe.AsPointer(ref h.Gob), facade); return; }
        UnassignedObjects++;
        if (!_unassignedLogged)
        {
            _unassignedLogged = true;
            Log?.Always("HitTest: SafePointer<CAurObject>::operator= did not resolve -- a cast's hit OBJECT is "
                      + "not reported to the game (the point, normal and kind still are). Hand-writing the "
                      + "pair would corrupt the engine's back-reference array, so it is left alone.");
        }
    }

    /// <summary>Fills that could not report their object, and the log latch for the first one.</summary>
    public static long UnassignedObjects;
    private static bool _unassignedLogged;
    /// <summary>Set once at service start, so a refusal in <see cref="AssignObject"/> can be loud.</summary>
    public static ExtenderLog? Log;

    /// <summary>The hit part's node name at <c>CHitInfo+0x68</c>, a native C string interned once per distinct
    /// name (the game may hold it; a managed string's address it may not). <c>CGuiSlider::HandleLMouseDown</c>
    /// @0x68f470 starts a drag only on a "thumb" prefix and <c>HandleGuiTimer</c> @0x690440 pages on it.</summary>
    private static nint PartName(int p)
    {
        ModelDef? m = SceneDb.PartModel[p];
        int n = SceneDb.PartNode[p];
        if (m == null || (uint)n >= (uint)m.Nodes.Length) return 0;
        string s = m.Nodes[n].Name;
        if (!_partNames.TryGetValue(s, out nint z))
        {
            z = System.Runtime.InteropServices.Marshal.StringToCoTaskMemUTF8(s);   // ponytail: never freed, one per distinct node name
            _partNames.Add(s, z);
        }
        return z;
    }

    private static readonly System.Collections.Generic.Dictionary<string, nint> _partNames = new(StringComparer.Ordinal);
}
