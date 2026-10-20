using System;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The PRODUCER of the hit-test columns: what the seam calls on every object it creates (and again
/// on a model change or tree rebuild) so a cast has something to answer from. The columns are derived --
/// classification from the <see cref="ModelDef"/>, bounds from the parts' meshes and world matrices -- and at
/// their zero defaults every gate in <c>HitTest.Admits</c> rejects. Game thread, allocation-free.</summary>
public static class HitFeed
{
    /// <summary>True once <see cref="NoteObject"/> has been called at least once: a session where the seam
    /// never produced the columns is a session where every cast answers nothing, and
    /// <see cref="Fold"/> says so rather than letting it look like an empty world.</summary>
    public static bool Armed { get; private set; }

    /// <summary>Objects noted, folds run, and objects the fold refreshed.</summary>
    public static long Noted, Folds, Refreshed;

    /// <summary>Fold visits to a dirty entry that carried a destroy AND a LIVE slot -- a record freed and
    /// re-allocated into the same slot inside one frame, which is what an area change does to every tile.
    /// Counted per visit, the marks standing until the frame's <c>TakeDirty</c>. Non-zero is normal at an area
    /// edge; taking one for a plain destroy leaves the new area with no floor.</summary>
    public static long Recycled;

    private static ExtenderLog? _log;
    private static bool _unarmedLogged;

    /// <summary>Set once at service start so a missing producer can be loud.</summary>
    public static void Logger(ExtenderLog log) { _log = log; HitGrid.Logger(log); }

    /// <summary>Subscribed to the model cache's eviction edge on first real use, not at service start: that
    /// edge is the ONLY thing that can retire a content-keyed mesh tree, and <c>ModelCache.SelfCheck</c>
    /// assigns <c>Evicting = null</c> partway through its own run, which would drop a subscription taken in
    /// an Initialize that happened to come first.</summary>
    private static bool _evictHooked;

    private static void HookEviction()
    {
        if (_evictHooked) return;
        _evictHooked = true;
        ModelCache.Evicting += MeshBvh.Evict;
    }

    /// <summary>Seed (or re-seed) object <paramref name="o"/>'s hit columns from its model and the kind of
    /// record it is; idempotent, so a model or tree change just calls it again. <paramref name="isStatic"/>
    /// marks the area's static set -- tile parts, static placeables, trigger prisms, the records in no object
    /// list, which a cast reaches through its static pass. Anything from <c>NewCAurObject</c> is false.</summary>
    public static void NoteObject(DbId o, bool isStatic = false)
    {
        if (!SceneDb.Objects.Valid(o)) return;
        int i = o.Index;
        Armed = true;
        Noted++;
        HookEviction();
        ModelDef? m = SceneDb.ObjModel[i];
        SceneDb.HitObjClass[i] = m?.Classification ?? 0;
        SceneDb.HitObjStatic[i] = isStatic;
        // The engine's two alpha fields start opaque (Gob's constructor) and only a fade moves them. Seeded
        // here because a zero would read as "fully transparent" and take the object out of every pick.
        if (SceneDb.HitObjAlpha[i] <= 0f) SceneDb.HitObjAlpha[i] = 1f;
        if (SceneDb.HitObjFadeAlpha[i] <= 0f) SceneDb.HitObjFadeAlpha[i] = 1f;
        int first = SceneDb.ObjPartFirst[i];
        for (int p = first; p < first + SceneDb.ObjPartCount[i]; p++) NotePart(p);
        SceneDb.Objects.Mark(i, Dirty.Xform | Dirty.Tree);
    }

    /// <summary>Seed one part's own-space box and prewarm its mesh tree. Called by
    /// <see cref="NoteObject"/> and by the fold on a tree change; a part's geometry is immutable, so once is
    /// enough per tenancy.</summary>
    public static void NotePart(int p)
    {
        HitBounds.PartBox(p);
        SceneDb.HitPartTrigger[p] = 0;
        ModelDef? m = SceneDb.PartModel[p];
        if (m == null || (uint)SceneDb.PartNode[p] >= (uint)m.Nodes.Length) return;
        MeshDef? mesh = m.Nodes[SceneDb.PartNode[p]].Mesh;
        // Off the cast path: MeshBvh.For inside a pick is a multi-millisecond first-click hitch.
        if (mesh != null && mesh.Faces.Length > 0) MeshBvh.Prewarm(mesh);
    }

    /// <summary>A trigger part's payload -- what <c>PartTrigger::HitCheckGeom</c> copies into
    /// <c>CHitInfo+0x70</c> (an area transition's identity). The seam sets it when it creates the prism.</summary>
    public static void NoteTrigger(int p, long payload)
    {
        if ((uint)p >= SceneDb.MaxParts) return;
        SceneDb.HitPartTrigger[p] = payload;
    }

    /// <summary>What a CAST calls first, because the engine casts DURING an area load: the creature update
    /// after <c>CNWCArea::LoadArea</c> runs <c>CNWCCreature::ComputeZValue</c>, whose floor probe is a
    /// <c>Scene::HitCheck</c>, while <see cref="Fold"/> runs only after the message pump -- so the frame the
    /// tiles spawned in would find no floor and keep the 9.0 sentinel. Re-folds only if the dirty list grew.</summary>
    public static void Sync()
    {
        if (SceneDb.Objects.DirtyMark == _foldedAt) return;
        SyncFolds++;
        Fold();
    }

    /// <summary>The dirty-list length the last <see cref="Fold"/> covered, for <see cref="Sync"/>.</summary>
    private static long _foldedAt = -1;

    /// <inheritdoc cref="Sync"/>
    public static long SyncFolds { get; private set; }

    /// <summary>Per object, the <see cref="DbTable.MarkClock"/> and compose count its last refresh saw: the
    /// dirty list only grows within a frame and every growth triggers a fold, so without these each append
    /// would re-refresh every entry before it (~14k refreshes a frame for ~48 dirty objects). The clock moves
    /// on EVERY <see cref="DbTable.Mark"/> and is monotonic, so there is no invalidation to wire.</summary>
    private static readonly long[] _foldMark = new long[SceneDb.MaxObjects];
    private static readonly int[] _foldCompose = new int[SceneDb.MaxObjects];
    /// <summary>Dirty entries a fold stepped over because nothing changed since their last refresh.</summary>
    public static long FoldSkipped;

    /// <summary>Once per frame, after the transform step, and from <see cref="Sync"/> on the cast path: fold
    /// the objects table's dirty list into the bounds columns and the index. Only records that CHANGED since
    /// their last refresh are visited (<see cref="_foldMark"/>), so a still scene costs nothing. It does NOT
    /// consume the dirty list -- the renderer's fold does -- so it must run before whatever calls <c>TakeDirty</c>.</summary>
    public static void Fold()
    {
        Folds++;
        _foldedAt = SceneDb.Objects.DirtyMark;
        if (!Armed && SceneDb.Objects.Live > 0 && !_unarmedLogged)
        {
            _unarmedLogged = true;
            _log?.Always($"HitFeed: {SceneDb.Objects.Live} object record(s) exist and HitFeed.NoteObject has "
                       + "never been called -- every hit test will answer NOTHING. The seam (CX-120) must "
                       + "call it on each object it creates; see HitFeed's remarks.");
        }
        long before = HitBounds.Refreshed;
        for (int k = 0; k < SceneDb.Objects.DirtyCount; k++)
        {
            int o = SceneDb.Objects.DirtyIndex(k);
            Dirty d = SceneDb.Objects.DirtyAt(o);
            long mark = SceneDb.Objects.MarkAt(o);
            int serial = DbAnim.ComposeCount[o];
            // The compose count as well as the mark: a getter's on-demand compose moves the matrices this
            // reads without marking the record at all.
            if (_foldMark[o] == mark && _foldCompose[o] == serial) { FoldSkipped++; continue; }
            _foldMark[o] = mark; _foldCompose[o] = serial;
            // Destroyed and Created can BOTH be set on one slot: a free and an alloc between two
            // TakeDirty points OR into one dirty entry (DbTable.Mark ORs, one list entry per slot), and
            // an AREA CHANGE is exactly that -- the old area's tiles are freed and the new area's are
            // allocated into the same slots inside one frame. Taking the destroy and skipping the rest
            // unfiled the NEW tile, and a static never moves again, so nothing ever marked it for a
            // re-file: no floor at all past the first area, which is ComputeZValue's Z=9 sentinel
            // (CX-185). DbFeed.FoldObjects' R1-2 is the same hazard one stream over.
            if ((d & Dirty.Destroyed) != 0)
            {
                HitGrid.Remove(o);
                if (!SceneDb.Objects.LiveAt(o)) continue;
                Recycled++;
            }
            // Dirty.Text is in the mask because a text gob's box is its own document's: the game sets a list
            // row's box AFTER the row exists (SetMaxWidth/SetMaxHeight, or the corner pair), so a row filed at
            // Created alone keeps the degenerate box the constructor left and is unclickable for its whole life.
            if ((d & (Dirty.Xform | Dirty.Tree | Dirty.Created | Dirty.Visible | Dirty.Text)) == 0) continue;
            // A recycled part slot keeps the previous model's own-space box until something re-derives it, so
            // a tree change re-seeds every part of the range rather than only the degenerate ones.
            if ((d & (Dirty.Tree | Dirty.Created)) != 0)
            {
                int first = SceneDb.ObjPartFirst[o];
                for (int p = first; p < first + SceneDb.ObjPartCount[o]; p++) NotePart(p);
            }
            HitBounds.Refresh(o);
        }
        Refreshed += HitBounds.Refreshed - before;
    }

    /// <summary>One line for a status report: whether the columns have a producer, and what the fold did.</summary>
    public static string Stats()
        => (Armed ? $"{SceneDb.Objects.Live} live object(s), produced {Noted} time(s)" : "NO PRODUCER -- every cast answers nothing")
         + $", {Folds} fold(s) ({SyncFolds} on the cast path) refreshing {Refreshed}, {FoldSkipped} unchanged entries skipped"
         + (Recycled > 0 ? $", {Recycled} visit(s) to a recycled slot re-filed it" : "") + "; " + HitGrid.Stats()
         + $"; {MeshBvh.Built} mesh tree(s) / {MeshBvh.BuiltFaces} faces, {MeshBvh.Evicted} evicted"
         + (MeshBvh.Refused > 0 ? $", {MeshBvh.Refused} REFUSED (too many faces)" : "")
         + (MeshBvh.Degraded > 0 ? $", {MeshBvh.Degraded} node(s) degraded to leaves" : "")
         + (MeshBvh.StackFull > 0 ? $", {MeshBvh.StackFull} traversal(s) hit the stack cap" : "")
         + $"; {HitTest.Casts} cast(s) walking {HitTest.CellVisits} cell(s), {HitTest.ObjTests} object(s), "
         + $"{HitTest.PartTests} part(s), {HitTest.FaceTests} face(s)"
         + (HitTest.GroundHits > 0 ? $"; {HitTest.GroundHits} cast(s) answered the z=0 ground plane" : "")
         + (HitTest.UnassignedObjects > 0 ? $"; {HitTest.UnassignedObjects} fill(s) could not report an object" : "")
         + (HitTest.FloorMisses > 0
            ? $"; floor {HitTest.FloorHits}/{HitTest.FloorCasts} hit; MISSED {HitTest.FloorMisses}x ({HitTest.FloorNoWalkmesh} with statics but no walkmesh "
            + "-- the tile .wok is not loaded; the engine then keeps ComputeZValue's Z=9 sentinel)" : "")
         + (HitTest.FloorCasts > 0 && HitTest.FloorMisses == 0 ? $"; floor {HitTest.FloorHits}/{HitTest.FloorCasts} hit" : "");
}
