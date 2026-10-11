using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A live instrument on the engine's own <c>Scene::HitCheck</c> @0x462960, to measure what
/// <see cref="HitTest"/> has to beat: counts the calls, times them, reads the engine's BSP-node counter
/// (<c>counthitcheckscenebspnode</c>) and prints the first records field by field at the offsets
/// <c>EngineLayout</c> declares, so a wrong offset row cannot ship silently. Armed by <c>alloy.hitcheck</c>.</summary>
internal static unsafe class HitProbe
{
    /// <summary>Records printed in full before the probe goes quiet. Enough to see a hit, a miss and a
    /// static hit; past that the counters carry the story.</summary>
    private const int DumpMax = 12;

    private static ExtenderLog? _log;
    private static SceneHitCheckFn* _chain;
    private static int* _bspCount;
    private static float* _minDoorAlpha;
    private static int _dumped;

    /// <summary>Which cast this is; averaging them together makes the measurement useless, because
    /// <c>Scene::HitCheck</c>'s callers include <c>PartEmitter::moveParticle*</c>, <c>ComputeZValue</c> and the
    /// camera spring arm, and only the pickers are what a CX pick must beat. Bucketed on the record's own input
    /// fields: PICK is box-only (<c>+0x20 != 0</c>, only the pickers set it), GROUND statics-only (<c>+0x00</c>).</summary>
    private enum Kind { Pick, Ground, Ray, Count }
    private static readonly long[] _calls = new long[(int)Kind.Count];
    private static readonly long[] _hits = new long[(int)Kind.Count];
    private static readonly long[] _ticks = new long[(int)Kind.Count];
    private static readonly long[] _worst = new long[(int)Kind.Count];
    private static readonly long[] _nodes = new long[(int)Kind.Count];
    /// <summary>Records printed per bucket, so one busy bucket cannot spend the whole dump budget.</summary>
    private static readonly int[] _dumpedOf = new int[(int)Kind.Count];

    /// <summary>Chain <c>Scene::HitCheck</c>. Safe to call with the knob off (it returns at once) and safe on
    /// a build where the symbol does not resolve -- that is a loud line, not a silent pass.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!Renderer.HitCheck.Boot) return;
        _bspCount = (int*)Native.Sym(SymbolMap.HitCheckBspNodeCount);
        _minDoorAlpha = (float*)Native.Sym(SymbolMap.MinDoorAlpha);
        if (_minDoorAlpha != null) HitTest.MinDoorAlpha = *_minDoorAlpha;
        // The engine's own default for the walkmesh gate, read rather than assumed: it is 1 in .data, and a
        // build that shipped it as 0 would make every click-to-move pick miss the ground.
        int* bsp = (int*)Native.Sym(SymbolMap.HitcheckStaticBsp);
        if (bsp != null) HitTest.WalkMeshDefault = *bsp != 0;
        _staticBsp = bsp;
        _staticTri = (int*)Native.Sym(SymbolMap.HitcheckStaticTrimesh);
        _groundPlane = (int*)Native.Sym(SymbolMap.EnableNonBspGroundHit);
        _chain = hooks.ChainSceneHitCheck(&OnHitCheck, Priority);
        // A floor cast that never arrives is invisible to the slot probe, so this names ComputeZValue's
        // early-out instead.
        _origZ = hooks.InstallComputeZValue(&OnComputeZ);
        log.Info(_chain != null
            ? $"HitProbe: armed on Scene::HitCheck (mindooralpha={HitTest.MinDoorAlpha:F3}, "
              + $"walkmesh gate {(HitTest.WalkMeshDefault ? 1 : 0)}, "
              + $"bsp counter {(_bspCount != null ? "resolved" : "UNRESOLVED")}) -- [hitprobe] every period"
            : "HitProbe: Scene::HitCheck could not be chained -- no live comparison this session "
              + "(alloy.hitcheck=1 asked for one)");
    }

    /// <summary>INNERMOST of the chain (the lowest priority runs closest to the engine), so the record this
    /// reads is the one the engine itself just filled, before any other subscriber can edit it.</summary>
    private const int Priority = -10;

    [UnmanagedCallersOnly]
    private static int OnHitCheck(nint scene, VectorArg from, VectorArg to, Vec3* hit, nint info)
    {
        long t0 = Stopwatch.GetTimestamp();
        int before = _bspCount != null ? *_bspCount : 0;
        int r = _chain != null && *_chain != null ? (*_chain)(scene, from, to, hit, info) : 0;
        long dt = Stopwatch.GetTimestamp() - t0;
        int k = (int)Bucket(info);
        // A GROUND cast (CHitInfo+0x00 != 0) is ComputeZValue's shape, the one the scene database has to
        // reproduce; the budget is per scene, so a transition is always captured.
        if (scene != _floorScene) { _floorScene = scene; _floorLeft = FloorMax; }
        if (k == (int)Kind.Ground && _floorLeft > 0) Floor(info, r, from, to);
        _calls[k]++;
        _ticks[k] += dt;
        if (dt > _worst[k]) _worst[k] = dt;
        if (_bspCount != null) _nodes[k] += *_bspCount - before;
        if (r != 0) _hits[k]++;
        if (_dumped < DumpMax && _dumpedOf[k] < DumpMax / (int)Kind.Count + 1 && info != 0)
        { _dumpedOf[k]++; Dump(info, r, (Kind)k); }
        return r;
    }

    /// <summary>Which bucket this record's INPUT fields put the cast in. Read off the record rather than
    /// guessed from the caller, because the caller is unknowable from inside the funnel.</summary>
    private static Kind Bucket(nint info)
    {
        if (info == 0) return Kind.Ray;
        var h = new CHitInfo(info);
        if (h.BoxOnly != 0) return Kind.Pick;
        if (h.Mode != 0) return Kind.Ground;
        return Kind.Ray;
    }

    private static ComputeZValueFn _origZ;
    private static nint _zArea;
    private static int _zSaid, _zSentinel;

    /// <summary><c>ComputeZValue</c>'s inputs, before it decides whether to cast at all. The three early-outs
    /// that leave the 9.0 sentinel are a null area, a null <c>CNWCArea+0x248</c> and a <c>GetTile</c> miss; the
    /// first two are readable here, and the third is implied when a cast with a live scene never reaches the
    /// seam. Budgeted per AREA so a transition is always captured.</summary>
    [UnmanagedCallersOnly]
    private static void OnComputeZ(nint self, nint area, nint pos, float f, nint outv)
    {
        var before = pos != 0 ? *(Vec3*)pos : default;
        if (area != _zArea) { _zArea = area; _zSaid = 0; }
        bool head = _zSaid < 4;
        if (head) _zSaid++;
        if (_origZ != null) _origZ(self, area, pos, f, outv);
        var after = pos != 0 ? *(Vec3*)pos : default;
        // EVERY sentinel is logged (to a cap), whatever the budget: the four that matter are the ones a
        // per-area budget spent in the first area can never reach.
        bool sentinel = after.Z == 9f;
        if (!head && !(sentinel && _zSentinel < 12)) return;
        if (sentinel) _zSentinel++;
        nint scene = area != 0 ? new CNWCArea(area).Scene.Ptr : 0;
        string known = "area NULL";
        if (area != 0)
        {
            if (scene == 0) known = "area+0x248 is NULL";
            else
            {
                DbId id = CX.Alloy.Seam.Facade.SceneId(scene);
                known = id.IsNull ? "NOT a live CX scene shell (retired, foreign or an engine Scene)"
                                  : $"CX scene {id.Value} world={SceneDb.SceneIsWorld[id.Index]} "
                                  + $"live={SceneDb.Scenes.Valid(id)} objs={SceneDb.Objects.Live}";
            }
        }
        // The last early-out: the game layer's own tile lookup. A 0 here means ComputeZValue returned before
        // it ever reached the Scene's HitCheck slot, which is why no cast arrives at the seam.
        string tile = "?";
        AreaGetTileFn gt = EngineFn.AreaGetTile;
        if (gt != null && area != 0)
        {
            Vec3 at = before;
            nint t = gt(area, VectorArg.Of(&at));
            tile = t == 0 ? "GetTile MISSED (tile array null, or the position is off the grid)" : $"tile=0x{t:x}";
        }
        _log?.Info($"[computez]{(sentinel ? " SENTINEL" : "")} area=0x{area:x} scene=0x{scene:x} [{known}] "
                   + $"in=({before.X:F2},{before.Y:F2},{before.Z:F2}) -> z={after.Z:F3} | {tile}");
    }

    /// <summary>Floor casts dumped per AREA. ComputeZValue fires every step of a walk, so one budget for the
    /// session is spent long before the first transition -- and the transition is the interesting part.</summary>
    private const int FloorMax = 60;
    private static int _floorLeft = FloorMax;
    private static nint _floorScene;
    private static int* _staticTri, _staticBsp, _groundPlane;

    /// <summary>ONE ComputeZValue-shaped floor cast, named end to end -- who asked, the segment, the three
    /// static-arm globals as they stood at the call, and what came back. It answers which of
    /// <c>Scene::HitCheck</c>'s arms gives vanilla a floor on a tile whose model carries no <c>aabb</c>
    /// walkmesh: kind 1 is the BSP/static pass (the part name says which list), kind 2 the GOB pass.</summary>
    private static void Floor(nint info, int r, VectorArg from, VectorArg to)
    {
        var h = new CHitInfo(info);
        int kind = h.Kind;
        string who = CX.Alloy.Seam.SeamCallers.Chain(4);
        // Only ComputeZValue's own casts: the mode bit is also set by the top-down feedback branch, and
        // mixing the two would answer a different question than the one being asked.
        if (who.Length > 0 && who.IndexOf("ComputeZValue", StringComparison.Ordinal) < 0) return;
        _floorLeft--;
        nint gob = h.Gob;
        nint name = h.PartName;
        Vector3 pt = h.Point, nr = h.Normal;
        Vec3 a = from.Value, b = to.Value;
        string obj = "-";
        if (gob != 0)
        {
            // Gob vtable +0x108 is GetExternalDataType, +0xf8 GetExternalData: what KIND of game object the
            // hit belongs to, which is how "a tile gob" is told from a placeable or a creature.
            try { obj = $"type={new Gob(gob).ExternalType}"; } catch { obj = "type=?"; }
        }
        _log?.Info($"[floorcast] kind={kind} r={r} from=({a.X:F2},{a.Y:F2},{a.Z:F2}) to=({b.X:F2},{b.Y:F2},{b.Z:F2}) "
                   + $"hit=({pt.X:F3},{pt.Y:F3},{pt.Z:F3}) nrm=({nr.X:F2},{nr.Y:F2},{nr.Z:F2}) "
                   + $"face={h.Face} surface=0x{h.Surface:x} "
                   + $"gob=0x{gob:x} {obj} part={(name != 0 ? Marshal.PtrToStringAnsi(name) : "-")} "
                   + $"globals: statictri={(_staticTri != null ? *_staticTri : -1)} "
                   + $"staticbsp={(_staticBsp != null ? *_staticBsp : -1)} "
                   + $"groundplane={(_groundPlane != null ? *_groundPlane : -1)} | {who}");
    }

    /// <summary>One engine record, field by field at the offsets <c>EngineLayout</c> declares -- the live
    /// cross-check on those layout rows.</summary>
    private static void Dump(nint info, int r, Kind k)
    {
        _dumped++;
        var h = new CHitInfo(info);
        Vector3 pt = h.Point, lp = h.LocalPoint, nr = h.Normal;
        nint name = h.PartName;
        _log?.Info($"[hitprobe] {k} r={r} kind={h.Kind} "
                 + $"mode={h.Mode} mask=0x{h.SurfaceMask:x} "
                 + $"box={h.BoxOnly} inside={h.RejectInside} "
                 + $"skipchr={h.SkipCharacters} dooralpha={h.DoorAlpha} "
                 + $"ignore={h.IgnoreCount} "
                 + $"pos=({pt.X:F2},{pt.Y:F2},{pt.Z:F2}) local=({lp.X:F2},{lp.Y:F2},{lp.Z:F2}) "
                 + $"nrm=({nr.X:F2},{nr.Y:F2},{nr.Z:F2}) face={h.Face} "
                 + $"surface=0x{h.Surface:x} safeIdx={h.SafeIndex} "
                 + $"gob=0x{h.Gob:x} trigger=0x{h.Trigger:x} "
                 + $"part={(name != 0 ? Marshal.PtrToStringAnsi(name) : "-")}");
    }

    /// <summary>The period line: the engine's own per-cast cost, which is the number <c>HitTest.Bench</c> has
    /// to beat, and a PASS/FAIL verdict against it.</summary>
    internal static string Period()
    {
        long total = _calls[0] + _calls[1] + _calls[2];
        if (total == 0) return "";
        var sb = new System.Text.StringBuilder("[hitprobe] engine Scene::HitCheck");
        for (int k = 0; k < (int)Kind.Count; k++)
        {
            if (_calls[k] == 0) continue;
            sb.Append($" | {(Kind)k} {_calls[k]} casts, {_hits[k]} hits, "
                    + $"{_ticks[k] * 1e6 / Stopwatch.Frequency / _calls[k]:F1}us/cast "
                    + $"(worst {_worst[k] * 1e6 / Stopwatch.Frequency:F0}us, "
                    + $"{_nodes[k] / (double)_calls[k]:F1} bsp nodes)");
        }
        // The ticket's perf bar, against the PICK bucket alone: a particle bounce and a creature's six
        // ground probes are not what a mouse pick has to beat, and averaging them in was what made the first
        // measurement meaningless. CX's benchmark is triangle mode over 3000 objects; the engine's picks are
        // box-only, so the comparison is if anything unkind to CX.
        double cx = HitTestCheck.BenchUsPerPick;
        long pn = _calls[(int)Kind.Pick];
        if (cx <= 0) sb.Append(" || no CX benchmark yet (/alloy hitbench)");
        else if (pn == 0) sb.Append($" || CX {cx:F1}us/cast -- no verdict (no PICK cast this period)");
        else
        {
            double nodes = _nodes[(int)Kind.Pick] / (double)pn;
            double us = _ticks[(int)Kind.Pick] * 1e6 / Stopwatch.Frequency / pn;
            // A pick that walked no BSP node at all is the main menu or a loading screen: the engine's cast
            // there touches an empty world and is not a like-for-like comparison.
            if (nodes < 1.0) sb.Append($" || CX {cx:F1}us/cast -- no verdict (no area BSP walked by a pick)");
            else sb.Append($" || CX {cx:F1}us/cast over {HitTestCheck.BenchObjects} objects vs the PICK bucket -> "
                         + (cx <= us ? "PASS" : "FAIL") + " (CX-118 perf bar)");
        }
        Array.Clear(_calls); Array.Clear(_hits); Array.Clear(_ticks); Array.Clear(_worst); Array.Clear(_nodes);
        return sb.ToString();
    }
}
