using CX.Alloy.Db;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary><b>The route gate, over Alloy's own scene state.</b> Alloy owns everything it draws with: the
/// world matrix (<see cref="GobXform"/>), the tile fade (<see cref="TileFadeRamp"/>) and the enumeration
/// (<see cref="AreaGrid"/>, Alloy's own index of the area), so the visible set is built off that index rather
/// than scanned. <c>/alloy poll 1</c> bypasses the keyhole cache and audits it.</summary>
public sealed unsafe partial class AlloyRenderer
{

    /// <summary>The part's TileData (part+0x98, spawn-time), so the fade question is answered without
    /// dereferencing the part. A tile GOB part (window, vine) carries no part+0x98; its gob's
    /// <see cref="EngineLayout.GobTileData"/> stands in, which is the owner <c>Gob::ProxyPartDraw</c>
    /// @0x4520c0 fogs it by.</summary>
    private static readonly IntPtr[] _oTile = new IntPtr[MaxOwned];
    /// <summary>...and the tile whose CAMERA-OCCLUSION fade this part takes: its own <c>part+0x98</c>, and
    /// only for a <c>tilefade 1</c> part (<see cref="TileFadeRamp.Fades"/>). Kept apart from
    /// <see cref="_oTile"/> because the populations differ -- a tile GOB's parts take only
    /// <c>Gob::ProxyPartDraw</c> @0x4520c0's fog-of-war ramp, and merging the two would fade a window out.</summary>
    private static readonly IntPtr[] _oFadeTile = new IntPtr[MaxOwned];
    /// <summary>This pass's six frustum planes (<see cref="CullPass.Planes"/>), and whether the pass had a
    /// perspective projection to take them from. Without one every slot routes.</summary>
    private static readonly Vector4[] _passPlanes = new Vector4[6];
    private static bool _passFrustumOk;
    /// <summary>World units added to a slot's slack-scaled sphere before the plane test: one frame of camera
    /// motion, so a part entering the view is routed the frame it appears rather than the frame after.</summary>
    private const float RouteSlackUnits = 3f;

    /// <summary><c>/alloy poll 1</c>: bypass the keyhole cache and audit it.</summary>
    private static bool _fullPoll => Renderer.Poll.V;
    /// <summary>Period counters: matrices recomposed vs served from their own record, the keyhole cache's two
    /// halves, and the route gate's two.</summary>
    private static long _mxChased, _mxCached, _disAsked;
    private static long _routeIn, _routeOut, _pollMissAlpha;
    /// <summary>Route verdicts forced open by <see cref="RouteGate.TexStale"/> this period -- the population
    /// that bypasses the frustum gate because a retired owned texture name has to be re-resolved. One per
    /// marked slot per retire event; a still frame must read ~0.</summary>
    private static long _routeStale;
    /// <summary>Why a part was refused the dissolve, per frame. The three gates ahead of the aperture test,
    /// counted apart because "the keyhole stops at that window" is always one of them and the census is the
    /// only thing that says which.</summary>
    private static long _disNoLit, _disNoFeat;

    /// <summary>The part's world box at enrolment (min and max corners), for the dissolve verdict AND for
    /// <see cref="AreaGrid"/>'s filing -- the same box, populated by <c>ComputePartBBox</c> @0x561370 for tile
    /// geometry and six zeros for everything hanging off a gob.</summary>
    internal static readonly Box[] _oBox = new Box[MaxOwned];
    /// <summary>Off-device: the box verdict against the decompiled rule. A plane z = 5 facing up (0,0,1,-5):
    /// a box below it dissolves (its lowest corner is under the plane), a box above it does not, the height
    /// cut refuses a box whose top is under it, and keyhole-off refuses everything. A verdict that picks the
    /// wrong corner passes the roof through the aperture or dissolves the floor.</summary>
    private static bool KeyholeBoxSelfCheck(ExtenderLog log)
    {
        var pl = new Vector4(0f, 0f, 1f, -5f);
        var below = new Box { Min = new Vector3(-1f, -1f, 0f), Max = new Vector3(1f, 1f, 2f) };
        var above = new Box { Min = new Vector3(-1f, -1f, 6f), Max = new Vector3(1f, 1f, 8f) };
        var straddle = new Box { Min = new Vector3(-1f, -1f, 4f), Max = new Vector3(1f, 1f, 6f) };
        bool ok = PartTable.KeyholeDissolvesBox(in below, 1, pl, -10f)
                  && !PartTable.KeyholeDissolvesBox(in above, 1, pl, -10f)
                  && PartTable.KeyholeDissolvesBox(in straddle, 1, pl, -10f)
                  && !PartTable.KeyholeDissolvesBox(in below, 1, pl, 3f)      // top at 2, cut at 3: refused
                  && !PartTable.KeyholeDissolvesBox(in below, 0, pl, -10f);
        if (!ok) log.Info("AlloyRenderer.KeyholeBoxSelfCheck: FAILED -- the cached-box dissolve verdict disagrees with the decompiled rule");
        return ok;
    }

    // ================= the visible set =================

    /// <summary>The VISIBLE SET -- per class, in enrollment order, the slots the route walk visits. Built
    /// from <see cref="AreaGrid"/> rather than by scanning the whole table: every slot on a gob, every slot the
    /// grid could not bound, and every filed slot whose cell survives this pass's planes. Rebuilt when a plane
    /// could have drifted past <see cref="VisDriftUnits"/> anywhere in view (<see cref="VisDrifted"/>).</summary>
    private static readonly int[] _vis = new int[MaxOwned];
    private static readonly int[] _visStart = new int[AlloyClasses.Count], _visEnd = new int[AlloyClasses.Count];
    private static readonly Vector4[] _visPlanes = new Vector4[6];
    private static int _visBuiltAt, _visEnrollGen = -1, _visEpoch = -1;
    /// <summary>Bumped by every <see cref="BuildIndex"/>: the set must be rebuilt WHOLE after one, even when
    /// no enrolment generation moved (a budgeted walk bumps it at its begin, frames before the finish that
    /// re-seats slots). Not an <c>int.MinValue</c> sentinel -- <c>_frameSeq - int.MinValue</c> overflows
    /// negative, the rebuild never comes, and a repacked table routes off the old set.</summary>
    private static int _visIndexGen, _visIndexSeen = -1;
    private static bool _visFull = true;
    private static bool _visList => Renderer.VisList.V;
    private static long _visRebuilds, _visIndexed;
    /// <summary>How far (world units) any plane may have moved, anywhere the pass can see, before a reused set
    /// is rebuilt. The index query takes it on top of <see cref="RouteSlackUnits"/>, so a reused set still holds
    /// every box the pass's own planes would keep. TAA's sub-pixel jitter stays well under it.</summary>
    private const float VisDriftUnits = 2f;

    /// <summary><see cref="_order"/>'s inverse permutation -- where slot i sits in the enrollment order. The
    /// index yields slots in CELL order and the enrollment order is the batch key (class, then texture, then
    /// feature/primitive, then VAO), so the query's output is sorted back into it before the class ranges are
    /// cut. Without that, one visible cell per tile would cut a batch per tile.</summary>
    private static readonly int[] _orderRank = new int[MaxOwned];
    /// <summary>The slots on a gob, in enrollment order. Always in the visible set: they are the movers, and
    /// their exact per-pass verdict is <see cref="RouteWanted"/>'s sphere test.</summary>
    private static readonly int[] _dynList = new int[MaxOwned];
    private static int _dynN;
    /// <summary>The radix sort's scratch and its 9-bit counters (two passes cover
    /// <see cref="MaxOwned"/>'s 18 bits).</summary>
    private static readonly int[] _visTmp = new int[MaxOwned];
    private const int RadixBits = 9, RadixBuckets = 1 << RadixBits, RadixMask = RadixBuckets - 1;
    private static readonly int[] _radix = new int[RadixBuckets];

    /// <summary>Rebuild the area index and the two order-derived lists. Called at the end of every enrolment
    /// -- the one edge that changes which slot holds which part -- so nothing incremental is needed and a slot
    /// never has to be removed from a cell.</summary>
    private static void BuildIndex()
    {
        for (int k = 0; k < _orderCount; k++) _orderRank[_order[k]] = k;
        AreaGrid.Begin();
        _dynN = 0; _unboxedEmpty = 0;
        for (int k = 0; k < _orderCount; k++)
            {
                int i = _order[k];
                // "Does this slot move" -- a non-static object's part. A mover is routed every pass rather
                // than filed, because its box moves with it.
                bool mover = _oDbPart[i] >= 0
                    ? (SceneDb.ObjFlags[SceneDb.PartObj[_oDbPart[i]].Index] & ObjFlag.Static) == 0
                    : _oGob[i] != IntPtr.Zero;
                if (mover) { _dynList[_dynN++] = i; continue; }
                // The world box ComputePartBBox filled at tile spawn. One that was never filled is a part with
                // no bounds to index on, and unbounded means VISIBLE -- the same answer its NeverCull sphere
                // already gave the cull.
                ref Box b = ref _oBox[i];
                if (HasBox(in b)) AreaGrid.Add(i, in b);
                else { AreaGrid.AddUnboxed(i); if (b.Min == Vector3.Zero && b.Max == Vector3.Zero) _unboxedEmpty++; }
            }
        // The set of slots changed, so last pass's visible set describes nothing.
        _visIndexGen++;
    }

    /// <summary>Does this world box carry bounds? The engine zeroes it in the <c>PartTriMesh</c> ctor and only
    /// <c>Scene::SpawnTile</c> fills it, so all-zero is "never filled"; a zero extent on one axis is a flat,
    /// axis-aligned surface (a wall, a floor) and IS a box. Requiring a positive width on x and y left every
    /// axis-aligned wall unboxed -- walked and sphere-tested every pass, whatever the camera saw.</summary>
    private static bool HasBox(in Box b)
        => b.Max.X >= b.Min.X && b.Max.Y >= b.Min.Y && b.Max.Z >= b.Min.Z
           && (b.Min != Vector3.Zero || b.Max != Vector3.Zero);
    /// <summary>Of the unboxed slots at the last index build, how many had an all-zero (never filled) box.</summary>
    private static int _unboxedEmpty;

    /// <summary>Before the route walk: decide whether this pass walks the whole order or the visible set,
    /// and rebuild the set if it is due.</summary>
    private static void PrepareVisible()
    {
        _visFull = !_visList || !_passFrustumOk;
        if (_visFull) { _texStalePending = false; return; }   // this pass walks every slot anyway
        // CX-104 (review): a cold path has marked slots tex-stale, and RouteWanted can only re-resolve the
        // ones the set visits -- see NoteTexStalePending.
        bool full = _visEnrollGen != _enrollGen || _visEpoch != _recEpoch || _visIndexSeen != _visIndexGen
                    || _texStalePending;
        if (!full && !VisDrifted()) { _visWhy = 0; return; }
        _visWhy = full ? 2 : 1;
        _visRebuilds++; FrameZones.Count(FrameZones.Ev.VisRebuild);
        _visBuiltAt = _frameSeq; _visEnrollGen = _enrollGen; _visEpoch = _recEpoch; _visIndexSeen = _visIndexGen;
        Array.Copy(_passPlanes, _visPlanes, _passPlanes.Length);
        // An enrolment or an epoch bump means some slot has no record to serve and MUST be rebuilt whatever
        // the frustum says, so that one rebuild walks the whole order. Every other rebuild is index-driven.
        if (full) { _texStalePending = false; BuildVisibleFull(); MarkVisibleSet(); return; }
        int n = 0;
        for (int k = 0; k < _dynN && n < _vis.Length; k++) _vis[n++] = _dynList[k];
        int fromGrid = n;
        n = AreaGrid.Query(_passPlanes, RouteSlackUnits + VisDriftUnits, _vis, n);
        n = TrimVisible(fromGrid, n);
        _visIndexed += n;
        SortVisibleByOrder(n);
        CutVisibleClasses(n);
        MarkVisibleSet();   // S1-6d: membership is stamped by the build, so the audit can slice
    }

    /// <summary>Does this slot's <see cref="_oBox"/> bind where its geometry can be -- may a box test EXCLUDE
    /// it? No for an empty box, for a <see cref="NeverCull"/> sphere (the geometry leaves its bounds), or for a
    /// MATRIX slot, whose <c>_oBox</c> is unscaled while the cull sphere it is weighed against is scaled. The
    /// grid files on that same unscaled box, so refusing to excuse it puts the case on <c>vischeck</c>'s misses.</summary>
    private static bool BoxBinds(int i, in Box b)
        => HasBox(in b) && _dSph[i].Radius < NeverCull && !_oMx[i];

    /// <summary>Keep the candidates whose OWN box survives this pass's planes, not every slot filed in a
    /// surviving cell: a cell carries the union of its members' boxes, so testing each member is strictly
    /// tighter at the same slack and nothing can be lost. The slack is the route's own plus the drift the set
    /// is rebuilt at, which is why a set reused for several frames still holds every box the route would keep.</summary>
    private static int TrimVisible(int from, int n)
    {
        int w = from;
        for (int k = from; k < n; k++)
            {
                int i = _vis[k];
                ref Box b = ref _oBox[i];
                // An unboundable slot has no box to test and is always visible, exactly as its NeverCull
                // sphere already made it. So is a slot whose SPHERE is NeverCull: that exemption exists
                // because the geometry leaves its bounds (a wind-swayed plant, a roofknm offset), which is a
                // statement about this box too -- the cell union used to give such a part a tile of margin,
                // and taking that away with a box its vertices do not stay inside is how geometry vanishes at
                // the screen edge. A MATRIX slot is exempt too -- see BoxBinds for all three.
                if (!BoxBinds(i, in b)
                    || AreaGrid.BoxInFrustum(_passPlanes, in b, RouteSlackUnits + VisDriftUnits)) _vis[w++] = i;
            }
        _visTrimmed += n - w;
        return w;
    }

    /// <summary>Off-device: the trim keeps what the route can still want. Four rules stated independently of
    /// <see cref="AuditVisible"/>, whose own reference test uses the same box predicate -- a box inside the
    /// planes survives, a box well outside does not, and a slot whose box does not BIND it (a cull-exempt
    /// sphere, or a matrix slot whose unscaled box the scaled sphere left behind) survives whatever it says.</summary>
    private static bool TrimSelfCheck(Checker check)
    {
        const int inView = 0, outView = 1, exempt = 2, mx = 3;
        var boxWas = new Box[4];
        Array.Copy(_oBox, boxWas, boxWas.Length);
        var sphWas = new Sphere[4];
        Array.Copy(_dSph, sphWas, sphWas.Length);
        Vector4[] planesWas = (Vector4[])_passPlanes.Clone();
        int[] visWas = { _vis[0], _vis[1], _vis[2], _vis[3] };
        bool mxWas = _oMx[mx];

        // Six planes keeping x >= 0 (five degenerate ones that keep everything), as AreaGrid's own check does.
        Array.Fill(_passPlanes, new Vector4(0f, 0f, 0f, 1f));
        _passPlanes[0] = new Vector4(1f, 0f, 0f, 0f);
        void Slab(int i, float x0, float x1) => _oBox[i] = new Box { Min = new Vector3(x0, 0f, 0f), Max = new Vector3(x1, 1f, 1f) };
        Slab(inView, 10f, 20f);
        Slab(outView, -500f, -490f);
        Slab(exempt, -500f, -490f);
        Slab(mx, -500f, -490f);
        _dSph[inView].Radius = 1f; _dSph[outView].Radius = 1f; _dSph[exempt].Radius = NeverCull;
        // A matrix slot with an ordinary, cullable sphere: the only thing that keeps it is its class.
        _dSph[mx].Radius = 1f; _oMx[mx] = true;
        _vis[0] = inView; _vis[1] = outView; _vis[2] = exempt; _vis[3] = mx;
        int n = TrimVisible(0, 4);
        bool ok = n == 3 && _vis[0] == inView && _vis[1] == exempt && _vis[2] == mx;
        check.Expect(ok, "the candidate trim drops a box in view, keeps one behind the planes, or drops a slot "
                       + "whose box does not bind it (a NeverCull sphere, or a matrix slot whose box is unscaled)");
        // ...and the audit agrees, so such a slot's absence reads as a MISS rather than as box-excluded.
        check.Expect(!BoxInPass(outView) && BoxInPass(exempt) && BoxInPass(mx),
                     "the audit's reference box test disagrees with the trim, so a scaled matrix part the cell "
                     + "test dropped would be excused instead of counted");
        ok &= !BoxInPass(outView) && BoxInPass(exempt) && BoxInPass(mx);

        Array.Copy(boxWas, _oBox, boxWas.Length);
        Array.Copy(sphWas, _dSph, sphWas.Length);
        Array.Copy(planesWas, _passPlanes, planesWas.Length);
        for (int k = 0; k < 4; k++) _vis[k] = visWas[k];
        _oMx[mx] = mxWas;
        return ok;
    }
    /// <summary>Candidates the box test dropped that a cell would have yielded, over the period.</summary>
    private static long _visTrimmed;

    /// <summary>Could a plane have moved more than <see cref="VisDriftUnits"/> at a point in view since the set
    /// was built? A plane's change at p is <c>dn.(p - eye) + (dn.eye + dd)</c>; p lies within the far plane's
    /// distance of the eye times <see cref="VisReachScale"/>, which covers a frustum corner at any sane FOV.</summary>
    private static bool VisDrifted()
    {
        Vector3 e = _eyePos;
        Vector4 far = _passPlanes[5];
        float reach = MathF.Max(0f, far.X * e.X + far.Y * e.Y + far.Z * e.Z + far.W)
                      * VisReachScale;
        for (int f = 0; f < _passPlanes.Length; f++)
        {
            float dx = _passPlanes[f].X - _visPlanes[f].X, dy = _passPlanes[f].Y - _visPlanes[f].Y;
            float dz = _passPlanes[f].Z - _visPlanes[f].Z, dw = _passPlanes[f].W - _visPlanes[f].W;
            if (MathF.Abs(dx * e.X + dy * e.Y + dz * e.Z + dw) + MathF.Sqrt(dx * dx + dy * dy + dz * dz) * reach > VisDriftUnits)
                return true;
        }
        return false;
    }
    /// <summary>Frustum corner distance over far-plane distance, generously: 1,55 at a 90x60 degree view.</summary>
    private const float VisReachScale = 2f;

    /// <summary>The flicker hunt: this pass's visible-set verdict (0 reused, 1 rebuilt from the index, 2 full),
    /// the running mean of opaque slots routed, and what the audit found.</summary>
    private static int _visWhy, _routedAvg, _visLogged;
    private static long _visCollapses, _visSlices, _visMisses, _visBoxOnly;
    private const int VisLogCap = 6;

    /// <summary>The set's MEMBERSHIP mark, stamped once per set BUILD rather than once per audit -- a slot is
    /// in the current set exactly while <see cref="_visMark"/> holds <see cref="_visMarkGen"/>, so the audit
    /// can ask about any slot on any frame. Gen 0 means no set has been built and nothing may be audited (the
    /// array's own default would otherwise read as "every slot is a member").</summary>
    private static readonly int[] _visMark = new int[MaxOwned];
    private static int _visMarkGen, _visTotal;

    private static void MarkVisibleSet()
    {
        int gen = ++_visMarkGen, total = 0;
        for (int c = 0; c < AlloyClasses.Count; c++)
            for (int k = _visStart[c]; k < _visEnd[c]; k++) { _visMark[_vis[k]] = gen; total++; }
        _visTotal = total;
    }

    /// <summary>S1-6d: how many frames one full sweep of the table takes, and where the next slice starts.
    /// Power of two (the old audit's own 1-in-64 cadence), so the coverage per 64 frames is what it always
    /// was -- 37,7k slots once -- and the 37,7k-slot SPIKE every 64th frame is gone.</summary>
    private const int VisAuditSlices = 64;
    private static int _visSliceAt;

    /// <summary>The slice to audit this frame, and the cursor advanced past it. Pure in its arguments, so the
    /// coverage claim -- every slot of the table exactly once per cycle, no gap and no slot visited twice -- is
    /// a self-check rather than a comment. A count that is not a multiple of
    /// <see cref="VisAuditSlices"/> rounds the step UP, so a cycle finishes in at most that many frames.</summary>
    private static void AuditSlice(int count, ref int at, out int from, out int to)
    {
        int step = (count + VisAuditSlices - 1) / VisAuditSlices;
        if (at >= count || at < 0) at = 0;
        from = at;
        to = Math.Min(count, from + step);
        at = to;
    }

    /// <summary>After the opaque/cutout route: did this pass's visible set collapse against the running mean,
    /// and does the set miss any live slot the pass's own sphere test would route? A miss -- a slot the full
    /// walk draws and the set does not -- is the flicker. A rolling 1/<see cref="VisAuditSlices"/> slice of the
    /// table per PASS, against this frame's own set and planes; a COLLAPSE walks the whole table at once.</summary>
    private static void AuditVisible(int routed)
    {
        // A full pass has no set to be missing from -- and it must NOT touch the cursor: a probe pass that
        // walks the whole order every frame would otherwise rewind it, and the world pass would audit the
        // first slice for ever while the rest of the table went unchecked.
        if (_visFull) return;
        bool collapse = _routedAvg > 400 && routed * 4 < _routedAvg;
        _routedAvg = _routedAvg == 0 ? routed : (_routedAvg * 7 + routed) / 8;
        if (collapse) _visCollapses++;
        if (_visMarkGen == 0 || _orderCount <= 0) return;   // no set has been built: nothing to be missing from
        int gen = _visMarkGen, total = _visTotal;
        int from, to;
        if (collapse) { from = 0; to = _orderCount; }
        else { AuditSlice(_orderCount, ref _visSliceAt, out from, out to); _visSlices++; }
        int missed = 0, first = -1;
        for (int k = from; k < to; k++)
        {
            int i = _order[k];
            if (_oTri[i] == IntPtr.Zero || _gate[i].RecGen != _recEpoch || _visMark[i] == gen) continue;
            if (!SphereInPass(i)) continue;
            // BW6: the set now excludes a candidate by its OWN box rather than by its cell's union, so the
            // population "the sphere would route it and the box says it is not in view" is what the trim
            // removed. It is not a miss -- the part's geometry is inside a box that BINDS it, which is the one
            // thing BoxInPass tests (BoxBinds: never a matrix slot, whose box the sphere's !790 scale has left
            // behind, and never a NeverCull or unboxed one) -- but it is the number that moves if the trim ever
            // drops more than it should, which is why it is counted rather than skipped silently. A slot the
            // trim dropped whose box the pass's own test DOES keep still lands below as a real miss: the trim's
            // slack (route + drift) is looser than BoxInPass's, and a plane cannot have moved further than the
            // drift without rebuilding the set.
            if (!BoxInPass(i)) { _visBoxOnly++; continue; }
            if (first < 0) first = i;
            missed++;
        }
        _visMisses += missed;
        // a collapse the set did not cause (a camera with nothing in front of it) is not this hunt's
        if (missed == 0 || _visLogged >= VisLogCap) return;
        _visLogged++;
        string where = "";
        if (first >= 0)
            {
                ref Box b = ref _oBox[first];
                ref Sphere sp = ref _dSph[first];
                int held = Array.IndexOf(_vis, first, 0, _visEnd[AlloyClasses.Count - 1]);
                // CX-188: the one question the line never asked. A query reaches a slot only through the cell
                // it was FILED under and tests that cell's union box, so a box that moved after the filing
                // (DbWriteWorldBox runs on every record rewrite; the grid is built once per enrolment) is
                // invisible to every query. "filed 123 now 456" or "union no" says that is what happened;
                // "filed 123 now 123 union yes" says it is not, and the loss is downstream of the cell test.
                bool stillFiled = AreaGrid.StillFiled(first, in b, out int cellWas, out int cellNow, out bool covered);
                where = $" first miss slot {first} cls {_oCls[first]} rank {_orderRank[first]} gob {(_oGob[first] != IntPtr.Zero ? "yes" : "no")} "
                      + $"grid {(stillFiled ? "agrees" : "STALE")} (filed cell {cellWas}, box now in {cellNow}, cell union covers it: {(covered ? "yes" : "NO")}) "
                      + $"{(held >= 0 ? $"IN the set at {held} but outside its class cut" : "absent from the set")} "
                      + $"box ({b.Min.X:F1},{b.Min.Y:F1},{b.Min.Z:F1})-({b.Max.X:F1},{b.Max.Y:F1},{b.Max.Z:F1}) "
                      + $"sphere ({sp.Center.X:F1},{sp.Center.Y:F1},{sp.Center.Z:F1} r{sp.Radius:F1})";
            }
        _log?.Info($"AlloyRenderer: [vischeck] {(collapse ? "COLLAPSE (whole table)" : $"miss (slice {from}..{to} of {_orderCount})")} frame {_frameSeq}: routed {routed} vs mean "
                   + $"{_routedAvg}, visible set {total} ({(_visWhy == 0 ? "reused" : _visWhy == 1 ? "rebuilt from the index" : "rebuilt full")}, "
                   + $"built at {_visBuiltAt}, enrol gen {_visEnrollGen}/{_enrollGen}, epoch {_visEpoch}/{_recEpoch}), {_dynN} gob slots, "
                   + $"{AreaGrid.Filed} filed in {AreaGrid.Occupied} cells, {AreaGrid.Unboxed} unboxed, {missed} live slot(s) the set misses;"
                   + where + $" eye ({_eyePos.X:F1},{_eyePos.Y:F1},{_eyePos.Z:F1}) near plane ({_passPlanes[4].X:F2},{_passPlanes[4].Y:F2},{_passPlanes[4].Z:F2},{_passPlanes[4].W:F1})");
    }

    /// <summary>Is a static slot's own world box in this pass's planes with the route slack? A gob slot or an
    /// unboxed one always is: neither is filed, so the set cannot miss it by position.</summary>
    private static bool BoxInPass(int i)
    {
        if (_oGob[i] != IntPtr.Zero) return true;
        ref Box b = ref _oBox[i];
        // Exactly the trim's own predicate: a box that does not bind cannot excuse the slot's absence, so
        // such a slot reads as a real miss rather than as box-excluded.
        return !BoxBinds(i, in b) || AreaGrid.BoxInFrustum(_passPlanes, in b, RouteSlackUnits);
    }

    /// <summary>The hunt's clause. Collapses and misses are LIFETIME totals (a miss must read 0 for the
    /// session, not for the period); the slice count and the box-excluded population are this period's, the
    /// latter scaled to one CYCLE. Printed when EITHER a slice or a collapse has happened, since a period made
    /// of nothing but collapse frames does no slices.</summary>
    private static string VisAuditNote()
        => _visSlices == 0 && _visCollapses == 0 ? "" : $", vischeck {_visCollapses} collapse(s) and {_visMisses} miss(es) lifetime "
                                  + $"(must be 0) over {_visSlices} slice(s) of 1/{VisAuditSlices} of the table "
                                  + $"this period ({_visBoxOnly * VisAuditSlices / Math.Max(1, _visSlices)} box-excluded/cycle)";

    /// <summary>The whole order, filtered by nothing: what an enrolment or epoch rebuild does, and the shape
    /// the index-driven path has to end up equal to.</summary>
    private static void BuildVisibleFull()
    {
        int n = 0;
        for (int c = 0; c < AlloyClasses.Count; c++)
        {
            _visStart[c] = n;
            for (int k = _clsStart[c]; k < _clsEnd[c]; k++)
            {
                int i = _order[k];
                if (_oTri[i] != IntPtr.Zero) _vis[n++] = i;
            }
            _visEnd[c] = n;
        }
    }

    /// <summary>LSD radix over <see cref="_orderRank"/>, two 9-bit passes. Stable and allocation-free.</summary>
    private static void SortVisibleByOrder(int n)
    {
        for (int shift = 0; shift < 18; shift += RadixBits)
        {
            Array.Clear(_radix, 0, RadixBuckets);
            for (int k = 0; k < n; k++) _radix[(_orderRank[_vis[k]] >> shift) & RadixMask]++;
            int at = 0;
            for (int b = 0; b < RadixBuckets; b++) { int c = _radix[b]; _radix[b] = at; at += c; }
            for (int k = 0; k < n; k++) _visTmp[_radix[(_orderRank[_vis[k]] >> shift) & RadixMask]++] = _vis[k];
            Array.Copy(_visTmp, _vis, n);
        }
    }

    /// <summary>...and, because <see cref="_order"/>'s most significant radix digit IS the class, the sorted
    /// output is already grouped by it: one scan cuts the per-class ranges, by RANK against the order's own
    /// class ranges. Not by <c>_oCls</c>: a budgeted walk's step rewrites a kept tenant's class in place
    /// frames before the finish re-sorts, and one flipped slot splits a run so the cut keeps only its tail.</summary>
    private static void CutVisibleClasses(int n)
    {
        int k = 0;
        for (int c = 0; c < AlloyClasses.Count; c++)
        {
            _visStart[c] = k;
            while (k < n && _orderRank[_vis[k]] < _clsEnd[c]) k++;
            _visEnd[c] = k;
        }
    }

    /// <summary>The list this pass routes class <paramref name="c"/> from, and its range.</summary>
    private static int[] RouteRange(int c, out int from, out int to)
    {
        if (_visFull) { from = _clsStart[c]; to = _clsEnd[c]; return _order; }
        from = _visStart[c]; to = _visEnd[c]; return _vis;
    }

    /// <summary>Is the slot's cull sphere inside this pass's six planes, with the route slack?</summary>
    private static bool SphereInPass(int i)
    {
        ref Sphere s = ref _dSph[i];
        if (s.Radius >= NeverCull) return true;
        float r = s.Radius * ShadowMath.FrustumCullSlack + RouteSlackUnits;
        for (int f = 0; f < 6; f++)
        {
            Vector4 pl = _passPlanes[f];
            if (pl.X * s.Center.X + pl.Y * s.Center.Y + pl.Z * s.Center.Z + pl.W < -r) return false;
        }
        return true;
    }

    private static void SetPassFrustum(in Matrix4x4 vp, bool perspective)
    {
        _passFrustumOk = perspective;
        if (perspective) CullPass.Planes(vp, _passPlanes, _eyePos);
    }

    // ================= BW7: the gate row =================

    /// <summary>Every per-slot record stamp the route gate reads, in ONE row -- the gate IS what the walk
    /// costs (the live median visits 23,063 candidates to route 5,650), and the whole question fits in one
    /// cache line. Padded to a 32-byte STRIDE so a row spans at most two lines; not an alignment guarantee,
    /// since a managed array is 8-byte aligned and only the stride is ours. Every writer keeps the row whole.</summary>
    [StructLayout(LayoutKind.Sequential, Size = GateRowBytes)]
    private struct RouteGate
    {
        /// <summary>The epoch this slot's record was built under, and the frame it was last verified on. A
        /// slot whose epoch is stale rebuilds unconditionally; one already verified this frame is free for
        /// every pass after the first. Epoch 0 is "never built", so the row needs no initialisation.</summary>
        public int RecGen, RecFrame;
        /// <summary>The slot TENANCY generation -- which enrollment owns this slot (<see cref="EnrollGen"/>)
        /// and which one the record in it was built for (<see cref="RecEnroll"/>). NOT the record epoch: that
        /// is bumped by every wholesale invalidation, some landing AFTER the frame's record build, while what a
        /// bake must not trust is a record built for a DIFFERENT PART. <c>_enrollGen</c> starts at 1.</summary>
        public int RecEnroll, EnrollGen;
        /// <summary>How far toward the floor the part is faded, 0 = not at all. Zero is the row's default, so
        /// a fresh slot needs no seeding and a re-tenanted one is cleared in <see cref="UnstampChase"/>.</summary>
        public float OccFade;
        /// <summary>Seconds of <c>OccHold</c> left since the last hit; cleared with the fade.</summary>
        public float OccHeld;
        /// <summary>The record's owned texture name RETIRED under it (the transcode drip swapping a raw upload
        /// for its compressed copy): the engine pointer is unchanged, so a pointer compare alone would keep the
        /// record sampling a dropped handle. Routed and re-resolved regardless of the frustum, then cleared
        /// where the three memos it invalidates are rewritten -- <c>StageDbPart</c>'s tail. Per slot, not global.</summary>
        public bool TexStale;
    }
    /// <summary>29 bytes of fields, padded to a 32-byte stride (0,47 MiB of padding over
    /// <see cref="MaxOwned"/>). Declared as the struct's <c>Size</c>, so a field appended past it cannot load;
    /// raise it with the field and <see cref="ChaseSelfCheck"/>'s loud line takes over.
    /// not-an-offset: a size, not an engine layout.</summary>
    private const int GateRowBytes = 32;
    /// <summary>The gate row per ownership slot. Every field's default is zero, so nothing seeds it.</summary>
    private static readonly RouteGate[] _gate = new RouteGate[MaxOwned];

    /// <summary>Should this pass touch the slot at all? A record only needs rewriting when (a) there is none to
    /// serve (stale epoch, new tenant, a retired texture name), or (b) the part is in this pass's view, where
    /// its identity is re-verified and its alpha, fade and dissolve re-read. A slot failing both keeps its
    /// record, spheres and shadow levels as they stand, stamped verified for the shadow side; serial (ADR 0022).</summary>
    private static bool RouteWanted(int i)
    {
        if (_fullPoll || !_passFrustumOk) return true;
        // BW7: one address, one bounds check, one line -- the four record terms are one row.
        ref RouteGate g = ref _gate[i];
        if (g.RecGen != _recEpoch || g.RecEnroll != g.EnrollGen) return true;
        // CX-188: counted, not folded into the line above -- this term fails the gate OPEN regardless of the
        // frustum, and until the stage started clearing it (DbStage, beside the tid memos) it never went back
        // down. A still frame must read ~0 here; a drip frame reads the parts the retired name sat on, once.
        if (g.TexStale) { _routeStale++; return true; }
        // #336: no stamp on the rejected slot -- the shadow side reads "moved on its LAST build, and that
        // build was last frame" (SlotRecMoved), so an unvisited slot reads still on its own.
        if (!SphereInPass(i)) { _routeOut++; return false; }
        _routeIn++;
        return true;
    }

    // ================= the visible gobs =================

    // ================= per-slot state the route reads =================

    /// <summary>Does the camera keyhole dissolve this part this frame -- lit, a feature that keyholes, and the
    /// dissolve column says so? The verdict is a column the dissolve step writes (<c>DbKeyhole</c>), not a
    /// question asked of the engine's camera per part per pass; the feature and lit gates still apply, since a
    /// bit that reaches a stage compiled KEYHOLING 0 does nothing.</summary>
    private static bool Dissolves(int i, IntPtr tri, ref StageCounters c)
    {
        if (ShaderFeatures.NoKeyholing((int)_oFeat[i * 4])) { c.DisNoFeat++; return false; }
        if (!_oLit[i]) { c.DisNoLit++; return false; }
        c.DisAsked++;
        return SceneDb.PartDissolve[_oDbPart[i]] < 1f;
    }

    /// <summary>The tile fade factor this part draws at -- the camera-occlusion ramp times the fog-of-war
    /// exploration ramp, both from <see cref="TileFadeRamp"/>, which owns them. Vanilla draws an unexplored
    /// interior tile fogged to black (<c>RenderDrawBucket</c> @0x46d060); Alloy fades it by alpha at the same
    /// 4/s rate. ponytail: alpha, not fog-to-black -- a per-draw fog scale in the record is the upgrade.</summary>
    private static float TileFade(int i, IntPtr tri) => SceneDb.PartFade[_oDbPart[i]];

    /// <summary>The slot's model matrix: the column the transform step composed on the game thread, which the
    /// feed rewrote the record from when it changed. Read, never recomposed here, and the engine is asked
    /// nothing.</summary>
    private static Matrix4x4 ChaseModel(int i, bool stale, bool newTenant, ref StageCounters c)
    {
        // Nothing to chase: the transform step composed this matrix on the game thread and the feed rewrote the
        // record when it did, so the draw reads the column and asks the engine nothing.
        c.MxCached++;
        return SceneDb.PartWorld[_oDbPart[i]];
    }

    /// <summary>A re-tenanted slot's caches describe the previous tenant.</summary>
    private static void UnstampChase(int i)
    { _gate[i].OccFade = 0f; _gate[i].OccHeld = 0f; }

    /// <summary>The [alloy] line's clause.</summary>
    private static string ChaseNote()
    {
        long mxAll = _mxChased + _mxCached;
        long frames = Math.Max(1, Counters._frames);
        string s = $"route {(float)_routeIn / frames:F0}/frame, {(float)_routeOut / frames:F0} off-frustum untouched"
                 + $" ({(float)_routeStale / frames:F1}/frame forced by a retired texture name)"
                 + $"; texture memo: the registry serial moved on {_texSerialMoves} of {frames} frames"
                 + (_visList ? $", visible set rebuilt {_visRebuilds} of {frames} frames from "
                             + $"{AreaGrid.Occupied} cells ({AreaGrid.Filed} indexed, {AreaGrid.Unboxed} unboxed ({_unboxedEmpty} never filled), "
                             + $"{_dynN} on gobs, {_visIndexed / Math.Max(1, _visRebuilds)}/rebuild, {_visTrimmed / Math.Max(1, _visRebuilds)} cell-only candidates trimmed/rebuild)"
                             : ", visible set OFF") + VisAuditNote()
                 + $"; matrices {(float)_mxChased / frames:F0}/frame composed of {(float)mxAll / frames:F0}"
                 // CX-188: "asked" was the ENGINE ask, from a path that no longer exists. In DB mode the
                 // verdict is a column (SceneDb.PartDissolve, written by the dissolve step), so this counts
                 // the parts whose verdict was READ.
                 + $", keyhole {(float)_disAsked / frames:F0}/frame read from the dissolve column"
                 // CX-188: the counts below are of the slots this pass ROUTED -- the gate rejects the rest
                 // before any of these questions is asked, and `route N/frame` above is that population.
                 // ("replayed frozen", which this used to point at, went with the static freeze.)
                 + $"; of the routed: dissolve refused {(float)_disNoLit / frames:F0} unlit + "
                 + $"{(float)_disNoFeat / frames:F0} nokeyhole-feature/frame"
                 + BatchAuditNote()
                 + (_fullPoll ? $"; POLL AUDIT misses (must be 0): alpha {_pollMissAlpha}" : "");
        return s;
    }

    private static void ResetChasePeriod()
    {
        _mxChased = _mxCached = _disAsked = 0;
        _routeIn = _routeOut = 0; _routeStale = 0; _texSerialMoves = 0; _visRebuilds = 0; _visIndexed = 0;
        _pollMissAlpha = 0;
        _visTrimmed = 0; _visBoxOnly = 0; _visSlices = 0;
        ResetBatchAuditPeriod();
        _disNoLit = _disNoFeat = 0;
    }

    /// <summary>S1-6d, off-device: one cycle of <see cref="AuditSlice"/> visits every index of the table exactly
    /// once, in order, and the next cycle starts over. Run on a count with a remainder (1000 over 64 slices) and
    /// on counts smaller than the slice count, where the step rounds up to 1 and a cycle is short.</summary>
    private static bool SliceCoverSelfCheck(Checker check)
    {
        bool ok = true;
        foreach (int count in stackalloc int[] { 1000, 64, 7, 1 })
        {
            var seen = new int[count];
            int at = 0, cycles = 0, next = 0;
            while (next < count && cycles <= VisAuditSlices)
            {
                cycles++;
                AuditSlice(count, ref at, out int from, out int to);
                ok &= from == next && to > from;
                for (int k = from; k < to; k++) seen[k]++;
                next = to;
            }
            for (int k = 0; k < count; k++) ok &= seen[k] == 1;
            ok &= cycles <= VisAuditSlices && next == count;
            // ...and the cursor wraps rather than running off the end
            AuditSlice(count, ref at, out int f2, out _);
            ok &= f2 == 0;
        }
        check.Expect(ok, "a cycle of audit slices leaves slots unvisited, visits one twice, or does not wrap");
        return ok;
    }

    /// <summary>Off-device: the parts of the route that fail as a creature frozen mid-stride rather than as an
    /// error -- the camera key compare, the visible set's class cut, and the radix sort that keeps the batch
    /// order across an index-driven rebuild.</summary>
    internal static bool ChaseSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.ChaseSelfCheck");
        // CX-104: the radix sort restores enrollment order, and the class cut follows it. Four slots handed
        // to the sort in cell order must come back in rank order and split into two contiguous class runs --
        // a sort that lost the order would cut a batch per cell, and a cut that read the wrong class run
        // would draw the blended set in the opaque pass.
        int a = 0, b = 1, c2 = 2, d = 3;
        int[] saveRank = { _orderRank[a], _orderRank[b], _orderRank[c2], _orderRank[d] };
        byte[] saveCls = { _oCls[a], _oCls[b], _oCls[c2], _oCls[d] };
        int[] saveEnd = (int[])_clsEnd.Clone();
        _orderRank[a] = 7; _orderRank[b] = 2; _orderRank[c2] = 5; _orderRank[d] = 300;
        for (int c = 0; c < AlloyClasses.Count; c++) _clsEnd[c] = c < (int)AlloyClass.Blended ? 100 : 400;
        // c2 ranks opaque but a mid-walk step already re-read it as blended: the cut follows the rank
        _oCls[a] = (byte)AlloyClass.Opaque; _oCls[b] = (byte)AlloyClass.Opaque;
        _oCls[c2] = (byte)AlloyClass.Blended; _oCls[d] = (byte)AlloyClass.Blended;
        _vis[0] = d; _vis[1] = a; _vis[2] = c2; _vis[3] = b;
        SortVisibleByOrder(4);
        bool sorted = _vis[0] == b && _vis[1] == c2 && _vis[2] == a && _vis[3] == d;
        check.Expect(sorted, "the visible set comes back in enrollment order, which is the batch key");
        CutVisibleClasses(4);
        check.Expect(_visStart[(int)AlloyClass.Opaque] == 0 && _visEnd[(int)AlloyClass.Opaque] == 3
                     && _visStart[(int)AlloyClass.Blended] == 3 && _visEnd[(int)AlloyClass.Blended] == 4
                     && _visStart[(int)AlloyClass.Water] == _visEnd[(int)AlloyClass.Water],
                     "...and cuts one range per class by the ORDER's class, even for a slot whose live class a budgeted step flipped");
        for (int k = 0; k < 4; k++) { _orderRank[k] = saveRank[k]; _oCls[k] = saveCls[k]; }
        Array.Copy(saveEnd, _clsEnd, saveEnd.Length);
        for (int k = 0; k < AlloyClasses.Count; k++) { _visStart[k] = 0; _visEnd[k] = 0; }
        _visIndexGen++;

        check.Expect(TrimSelfCheck(check), "the visible-set trim would hide geometry from a pass");

        // BW7: a row grown past its stride costs cache lines, not correctness, so it is SAID loudly and does
        // NOT join this check's verdict -- a false verdict here stands the whole renderer down
        // (AlloyRenderer.cs), which would be the wrong price for a field somebody appended.
        if (sizeof(RouteGate) != GateRowBytes)
            log.Always($"AlloyRenderer: the route gate row is {sizeof(RouteGate)} bytes, not {GateRowBytes} -- "
                     + "the gate costs a second cache line per visited slot again (BW7)");
        // S1-6d: the rolling audit's whole claim is that a cycle of slices covers the table exactly as the
        // whole-table walk did. Checked on a count that is NOT a multiple of the slice count, which is where an
        // off-by-one leaves a tail of slots no frame ever audits -- the flicker hunt going quiet about them.
        check.Expect(SliceCoverSelfCheck(check), "the rolling visible-set audit does not cover the table");

        var wall = new Box { Min = new Vector3(10f, 20f, 0f), Max = new Vector3(20f, 20f, 5f) };
        var never = new Box();
        var inverted = new Box { Min = new Vector3(5f, 5f, 0f), Max = new Vector3(1f, 6f, 1f) };
        check.Expect(HasBox(in wall) && !HasBox(in never) && !HasBox(in inverted),
                     "a flat axis-aligned wall is indexed; a never-filled or inverted box is not");

        log.Info(check.Ok ? "AlloyRenderer.ChaseSelfCheck: passed"
                          : "AlloyRenderer.ChaseSelfCheck: FAILED -- the route would serve a stale record");
        return check.Ok;
    }
}
