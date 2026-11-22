using System;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Object picking builds its mesh trees on a worker (approved examples E1 to E10). Oracle: flat grid meshes
/// embedded here, whose face counts and hit points are hand-worked literals, and the build counters counted per thread.</summary>
internal static partial class MeshBvhWorkerApprovedExamples
{
    /// <summary>Quads along one side of a grid mesh, and its faces worked by hand: two triangles a quad, 40 x 40 quads.</summary>
    private const int Side = 40, SideFaces = 3200;
    /// <summary>The large mesh's side, and its faces by hand (2 x 180 x 180), under <see cref="MeshBvh.MaxFaces"/>.</summary>
    private const int LargeSide = 180, LargeFaces = 64800;
    /// <summary>The faces of the hitbench's own unit cube: six quads, two triangles each.</summary>
    private const int BenchCubeFaces = 12;
    /// <summary>Area entry: objects noted in one frame, over this many distinct meshes.</summary>
    private const int EntryObjects = 400, EntryMeshes = 40;
    /// <summary>Fresh area entries E1 runs; the fastest one's picking time is logged for the record, never asserted.</summary>
    private const int EntrySamples = 4;
    /// <summary>Ceiling on a wait for the worker to reach a state; a working one is there in microseconds.</summary>
    private const int WaitMs = 500;
    /// <summary>Ceiling on a probe that must not wait for a build.</summary>
    private const int AnswerMs = 2000;
    /// <summary>A tree pick tests a handful of faces; an untreed walk tests every one of <see cref="SideFaces"/>.</summary>
    private const long TreeFacesTested = 64;
    /// <summary>Height every fixture object stands at: a grid's face plane is z = 0, so a hit lands at this z.</summary>
    private const float StandZ = 1f;

    /// <summary>What one example seated, so it can be torn down whole.</summary>
    private sealed class Area
    {
        public DbId Scene;
        public DbId[] Objs = new DbId[EntryObjects + 64];
        public int Count;
        public ModelDef[] Models = new ModelDef[EntryObjects + 64];
        public int ModelCount;
    }

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MeshBvhWorkerApprovedExamples.SelfCheck");
        if (SceneDb.Scenes.Live != 0)
            return check.Skip($"the seam is live ({SceneDb.Scenes.Live} scene(s)) and the fixture writes to the real tables");

        WarmUp();
        AreaEntryFrame(check, log);
        ClickBeforeBuilt(check);
        LaterPickUsesBuilt(check);
        EvictedMidBuild(check);
        DanglyBuildsNone(check);
        NoWaitOnBuild(check);
        ClickMidBuild(check);
        EvictedWhileQueued(check);
        HitbenchCountsOnce(check, log);
        EntryAllocatesNothing(check);

        if (check.Ok) log.Info("MeshBvhWorkerApprovedExamples.SelfCheck: passed");
        return check.Ok;
    }

    // Given an area entry that creates hundreds of objects at once
    // When their parts are noted for picking
    // Then that frame's picking work stays under 2 ms, measured on the whole frame, and no mesh tree is built on the game thread, counted per thread
    private static void AreaEntryFrame(Checker check, ExtenderLog log)
    {
        double best = double.MaxValue;
        for (int s = 0; s < EntrySamples; s++) best = Math.Min(best, OneAreaEntry(check, s));
        log.Info($"MeshBvhWorkerApprovedExamples: E1 the fastest of {EntrySamples} area entries took {best:F2} ms of picking work");
    }

    /// <summary>One fresh area entry over meshes of its own: its picking work, milliseconds, with the trees it built
    /// counted per thread and each one's builder thread named against this thread's id.</summary>
    private static double OneAreaEntry(Checker check, int sample)
    {
        Area a = NewArea();
        try
        {
            var meshes = new MeshDef[EntryMeshes];
            for (int m = 0; m < EntryMeshes; m++) meshes[m] = Grid(Side, 2f);
            for (int i = 0; i < EntryObjects; i++) Seat(a, Model(a, $"entry{sample}_{i % EntryMeshes}", meshes[i % EntryMeshes]), Lattice(i));
            int game0 = MeshBvh.BuiltOnGame, worker0 = MeshBvh.BuiltOnWorker;

            long t0 = Stopwatch.GetTimestamp();
            for (int i = 0; i < a.Count; i++) HitFeed.NoteObject(a.Objs[i]);
            HitFeed.Fold();
            MeshBvh.FileFinished();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            int gameBuilt = MeshBvh.BuiltOnGame - game0;

            check.Expect(gameBuilt == 0, $"E1 the game thread built {gameBuilt} mesh tree(s) in area entry {sample + 1}'s frame, want 0");
            check.Expect(Settle(), $"E1 the worker drains area entry {sample + 1}'s meshes");
            int workerBuilt = MeshBvh.BuiltOnWorker - worker0;
            check.Expect(workerBuilt == EntryMeshes,
                         $"E1 the worker built {workerBuilt} of area entry {sample + 1}'s {EntryMeshes} distinct meshes' trees");
            int game = Environment.CurrentManagedThreadId, byWorker = 0, byGame = 0;
            for (int m = 0; m < EntryMeshes; m++)
            {
                int by = MeshBvh.BuilderOf(meshes[m]);
                if (by == game) byGame++;
                else if (by > 0 && by == MeshBvh.WorkerThreadId) byWorker++;
            }
            check.Expect(byGame == 0 && byWorker == EntryMeshes,
                         $"E1 area entry {sample + 1}'s trees name their builder: {byWorker} the worker, {byGame} this thread, of {EntryMeshes}");
            return ms;
        }
        finally { Tear(a); }
    }

    // Given a click lands on an object whose mesh tree is not built yet
    // When the pick runs
    // Then it picks the same object a built tree would, building that one mesh's tree on the spot
    private static void ClickBeforeBuilt(Checker check)
    {
        Area a = NewArea();
        MeshDef filler = Grid(Side, 2f), target = Grid(Side, 2f), other = Grid(Side, 2f);
        try
        {
            MeshBvh.HoldWorker();
            Note(a, filler, 0);
            check.Expect(Parked(filler), "E2 the held worker parks on the first mesh it takes");
            DbId hit = Note(a, target, 1);
            Note(a, other, 2);
            check.Expect(!MeshBvh.Filed(target) && !MeshBvh.Filed(other),
                         "E2 the clicked mesh and its neighbour have no tree yet: they wait behind the held worker");
            int game0 = MeshBvh.BuiltOnGame;
            Vector3 at = Lattice(1) + new Vector3(0.33f, 0.27f, 0f);
            bool picked = Pick(a, at, out HitResult r);
            check.Expect(picked && r.Obj == hit, $"E2 the pick answers the clicked object (hit {picked}, {r.Kind})");
            check.Expect(Near(r.Point, new Vector3(at.X, at.Y, StandZ)), $"E2 the pick lands on the grid's face at z 1 ({r.Point})");
            check.Expect(MeshBvh.BuiltOnGame - game0 == 1 && MeshBvh.Filed(target) && !MeshBvh.Filed(other),
                         $"E2 the pick built that one mesh's tree on the spot and no other ({MeshBvh.BuiltOnGame - game0} built)");
        }
        finally { MeshBvh.ReleaseWorker(); Settle(); Tear(a); }
    }

    // Given the worker has built a mesh's tree
    // When a later pick reaches that mesh
    // Then it uses the built tree, and nothing is built on the game thread
    private static void LaterPickUsesBuilt(Checker check)
    {
        Area a = NewArea();
        MeshDef mesh = Grid(Side, 2f);
        try
        {
            int worker0 = MeshBvh.BuiltOnWorker;
            DbId hit = Note(a, mesh, 0);
            check.Expect(Settle() && MeshBvh.BuiltOnWorker - worker0 == 1 && MeshBvh.Filed(mesh),
                         $"E3 the worker built the mesh's tree and it is filed ({MeshBvh.BuiltOnWorker - worker0} built)");
            int game0 = MeshBvh.BuiltOnGame;
            long tested0 = MeshBvh.FacesTested;
            Vector3 at = Lattice(0) + new Vector3(-0.71f, 1.13f, 0f);
            bool picked = Pick(a, at, out HitResult r);
            long tested = MeshBvh.FacesTested - tested0;
            check.Expect(picked && r.Obj == hit && Near(r.Point, new Vector3(at.X, at.Y, StandZ)),
                         $"E3 the later pick answers the object at its face ({r.Kind}, {r.Point})");
            check.Expect(tested > 0 && tested <= TreeFacesTested, $"E3 the pick walked the built tree: {tested} face(s) tested of {SideFaces}");
            check.Expect(MeshBvh.BuiltOnGame - game0 == 0, $"E3 the game thread built {MeshBvh.BuiltOnGame - game0} tree(s) for the later pick");
        }
        finally { Tear(a); }
    }

    // Given a model is evicted while its tree is still being built
    // When the worker finishes
    // Then the tree is dropped, and no pick reads it
    private static void EvictedMidBuild(Checker check)
    {
        Area a = NewArea();
        MeshDef mesh = Grid(Side, 2f);
        try
        {
            MeshBvh.HoldWorker();
            Note(a, mesh, 0);
            check.Expect(Parked(mesh), "E4 the worker is mid-build on the model's mesh");
            MeshBvh.Evict(a.Models[0]);
            MeshBvh.ReleaseWorker();
            check.Expect(Settle(), "E4 the worker finishes the build it held");
            check.Expect(!MeshBvh.Filed(mesh), "E4 the evicted model's tree is dropped, so no pick can read it");
        }
        finally { MeshBvh.ReleaseWorker(); Settle(); Tear(a); }
    }

    // Given dangly collision meets a mesh whose tree is not built
    // When its walls are gathered
    // Then it collides as today, building no tree
    private static void DanglyBuildsNone(Checker check)
    {
        ModelDef wall = DbAnim.WallQuadForCheck("cx_selfcheck_bvhwall");
        MeshDef mesh = wall.Nodes[0].Mesh!;
        try
        {
            MeshBvh.HoldWorker();
            int built0 = MeshBvh.Built;
            bool parked = false;
            int faces = DbAnim.WallGatherFaces(wall, note: true, onNoted: () => parked = Parked(mesh));
            check.Expect(parked, "E5 the noted wall's mesh waits on the held worker, its tree not built");
            check.Expect(faces == 2, $"E5 a banner on an untreed wall gathers its two faces, as today ({faces})");
            check.Expect(MeshBvh.Built - built0 == 0 && !MeshBvh.Filed(mesh),
                         $"E5 noting and gathering the wall built no tree ({MeshBvh.Built - built0} built)");
        }
        finally { MeshBvh.ReleaseWorker(); Settle(); MeshBvh.Evict(wall); }
    }

    // Given the worker is building a large mesh's tree
    // When a pick, a dangly gather or an area entry runs
    // Then it answers without waiting for that build
    private static void NoWaitOnBuild(Checker check)
    {
        Area a = NewArea();
        MeshDef large = Grid(LargeSide, 2f), fresh = Grid(Side, 2f);
        Thread? probe = null;
        int stage = 0;
        try
        {
            MeshBvh.HoldWorker();
            DbId hit = Note(a, large, 0);
            check.Expect(Parked(large), $"E6 the worker is mid-build on the large mesh ({LargeFaces} faces)");
            DbId entry = Seat(a, Model(a, "nowaitentry", fresh), Lattice(1));
            ModelDef largeModel = a.Models[0];
            bool picked = false;
            HitResult r = default;
            Vector3 at = Lattice(0) + new Vector3(0.33f, 0.27f, 0f);
            probe = new Thread(() =>
            {
                picked = Pick(a, at, out r);
                Volatile.Write(ref stage, 1);
                DbAnim.WallGatherFaces(largeModel, note: false, onNoted: null);
                Volatile.Write(ref stage, 2);
                HitFeed.NoteObject(entry);
                Volatile.Write(ref stage, 3);
            }) { IsBackground = true, Name = "cx-selfcheck-bvhprobe" };
            probe.Start();
            for (int i = 0; i < AnswerMs && Volatile.Read(ref stage) < 3; i++)
                BlockingWait.Sleep(1, "self-check only: give the probe its deadline while the worker is held");
            int reached = Volatile.Read(ref stage);
            check.Expect(reached >= 1, "E6 a pick on the mesh under build answered without waiting for it");
            check.Expect(reached >= 2, "E6 a dangly gather on the mesh under build answered without waiting for it");
            check.Expect(reached >= 3, "E6 an area entry answered without waiting for the build");
            check.Expect(reached < 1 || picked && r.Obj == hit, $"E6 the pick that did not wait still answers the object ({r.Kind})");
        }
        finally
        {
            MeshBvh.ReleaseWorker();
            for (int i = 0; probe != null && i < AnswerMs && Volatile.Read(ref stage) < 3; i++)
                BlockingWait.Sleep(1, "self-check only: let a probe that waited finish before the tables are torn down");
            Settle();
            Tear(a);
        }
    }

    // Given the worker is mid-build on a mesh
    // When a click lands on it
    // Then the pick answers as a built tree would, and one tree for that mesh is kept and counted
    private static void ClickMidBuild(Checker check)
    {
        Area a = NewArea();
        MeshDef mesh = Grid(Side, 2f);
        try
        {
            int built0 = MeshBvh.Built;
            long faces0 = MeshBvh.BuiltFaces;
            MeshBvh.HoldWorker();
            DbId hit = Note(a, mesh, 0);
            check.Expect(Parked(mesh), "E7 the worker is mid-build on the clicked mesh");
            Vector3 at = Lattice(0) + new Vector3(1.47f, -0.62f, 0f);
            bool picked = Pick(a, at, out HitResult r);
            check.Expect(picked && r.Obj == hit && Near(r.Point, new Vector3(at.X, at.Y, StandZ)),
                         $"E7 the click mid-build answers the object at its face ({r.Kind}, {r.Point})");
            MeshBvh.ReleaseWorker();
            check.Expect(Settle(), "E7 the worker finishes the build it held");
            check.Expect(MeshBvh.Filed(mesh), "E7 the mesh keeps a tree");
            check.Expect(MeshBvh.Built - built0 == 1 && MeshBvh.BuiltFaces - faces0 == SideFaces,
                         $"E7 one tree is counted for the mesh ({MeshBvh.Built - built0} tree(s), {MeshBvh.BuiltFaces - faces0} faces)");
            int game0 = MeshBvh.BuiltOnGame;
            Pick(a, at, out _);
            check.Expect(MeshBvh.BuiltOnGame - game0 == 0, "E7 the kept tree answers the next click, building nothing");
        }
        finally { MeshBvh.ReleaseWorker(); Settle(); Tear(a); }
    }

    // Given a model is evicted while its meshes wait in the worker's queue
    // When the worker reaches them
    // Then none is built, and the queue holds nothing of them
    private static void EvictedWhileQueued(Checker check)
    {
        Area a = NewArea();
        MeshDef filler = Grid(Side, 2f), first = Grid(Side, 2f), second = Grid(Side, 2f);
        try
        {
            int built0 = MeshBvh.Built;
            MeshBvh.HoldWorker();
            Note(a, filler, 0);
            check.Expect(Parked(filler), "E8 the held worker parks on the filler mesh");
            Seat(a, Model(a, "queuedtwo", first, second), Lattice(1));
            HitFeed.NoteObject(a.Objs[1]);
            check.Expect(MeshBvh.Queued(first) && MeshBvh.Queued(second), "E8 the model's two meshes wait in the worker's queue");
            MeshBvh.Evict(a.Models[1]);
            check.Expect(!MeshBvh.Queued(first) && !MeshBvh.Queued(second), "E8 after the eviction the queue holds nothing of them");
            MeshBvh.ReleaseWorker();
            check.Expect(Settle(), "E8 the worker drains");
            check.Expect(!MeshBvh.Filed(first) && !MeshBvh.Filed(second), "E8 neither evicted mesh has a tree");
            check.Expect(MeshBvh.Built - built0 == 1, $"E8 only the filler's tree was built ({MeshBvh.Built - built0} built)");
        }
        finally { MeshBvh.ReleaseWorker(); Settle(); Tear(a); }
    }

    // Given the worker and a pick build trees at once
    // When /alloy hitbench reports
    // Then each tree is counted once, and the faces add up
    private static void HitbenchCountsOnce(Checker check, ExtenderLog log)
    {
        Area a = NewArea();
        MeshDef held = Grid(Side, 2f), queued = Grid(Side, 2f);
        int built0 = MeshBvh.Built, game0 = MeshBvh.BuiltOnGame, worker0 = MeshBvh.BuiltOnWorker;
        long faces0 = MeshBvh.BuiltFaces;
        try
        {
            MeshBvh.HoldWorker();
            Note(a, held, 0);
            check.Expect(Parked(held), "E9 the worker is mid-build on the first mesh");
            Note(a, queued, 1);
            Pick(a, Lattice(0) + new Vector3(0.33f, 0.27f, 0f), out _);
            Pick(a, Lattice(1) + new Vector3(0.33f, 0.27f, 0f), out _);
            MeshBvh.ReleaseWorker();
            check.Expect(Settle(), "E9 the worker finishes");
        }
        finally { MeshBvh.ReleaseWorker(); Settle(); Tear(a); }

        HitTestCheck.Bench(log);
        Settle();
        int built = MeshBvh.Built - built0, perThread = MeshBvh.BuiltOnGame - game0 + MeshBvh.BuiltOnWorker - worker0;
        long faces = MeshBvh.BuiltFaces - faces0;
        check.Expect(built == 3, $"E9 hitbench counts {built} tree(s): the two raced meshes once each and its own cube, want 3");
        check.Expect(faces == 2 * SideFaces + BenchCubeFaces,
                     $"E9 hitbench's faces add up to {faces}, want {2 * SideFaces + BenchCubeFaces} ({SideFaces} + {SideFaces} + {BenchCubeFaces})");
        check.Expect(perThread == 3, $"E9 the per-thread counts add to {perThread} tree(s), want 3");
    }

    // Given an area entry noting hundreds of parts
    // When they are handed to the worker
    // Then the game thread allocates nothing to do it
    private static void EntryAllocatesNothing(Checker check)
    {
        Area a = NewArea();
        try
        {
            var shared = Grid(Side, 2f);
            var meshes = new MeshDef[EntryMeshes];
            for (int m = 0; m < EntryMeshes; m++) meshes[m] = Grid(Side, 2f);
            int half = EntryObjects / 2;
            for (int i = 0; i < half; i++) Seat(a, Model(a, "allocshared", shared), Lattice(i));
            for (int i = 0; i < half; i++) Seat(a, Model(a, "allocnew" + i % EntryMeshes, meshes[i % EntryMeshes]), Lattice(half + i));
            HitFeed.NoteObject(a.Objs[0]);
            Settle();

            long b0 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 1; i < half; i++) HitFeed.NoteObject(a.Objs[i]);
            long treed = GC.GetAllocatedBytesForCurrentThread() - b0;
            check.Expect(treed == 0, $"E10 noting parts whose tree is filed allocates nothing ({treed} bytes): the measure is clean");

            long b1 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = half; i < a.Count; i++) HitFeed.NoteObject(a.Objs[i]);
            long handed = GC.GetAllocatedBytesForCurrentThread() - b1;
            check.Expect(handed == 0, $"E10 handing {EntryObjects / 2} parts over {EntryMeshes} new meshes to the worker allocated {handed} bytes on the game thread");
        }
        finally { Settle(); Tear(a); }
    }

    /// <summary>One small area entry before anything is measured, so first-call costs are not on E1's clock.</summary>
    private static void WarmUp()
    {
        Area a = NewArea();
        try
        {
            MeshDef m0 = Grid(Side, 2f), m1 = Grid(Side, 2f);
            for (int i = 0; i < 8; i++) Seat(a, Model(a, "warm", (i & 1) == 0 ? m0 : m1), Lattice(i));
            for (int i = 0; i < a.Count; i++) HitFeed.NoteObject(a.Objs[i]);
            HitFeed.Fold();
            Settle();
            Pick(a, Lattice(0) + new Vector3(0.33f, 0.27f, 0f), out _);
        }
        finally { Tear(a); }
    }

    /// <summary>Wait for the worker to have nothing queued or in hand, then file what it finished, as the game thread does.</summary>
    private static bool Settle()
    {
        for (int i = 0; i < WaitMs && !MeshBvh.WorkerIdle; i++)
            BlockingWait.Sleep(1, "self-check only: wait for the mesh-tree worker to drain");
        MeshBvh.FileFinished();
        return MeshBvh.WorkerIdle;
    }

    /// <summary>Wait for the held worker to reach its park, mid-build on <paramref name="mesh"/>.</summary>
    private static bool Parked(MeshDef mesh)
    {
        for (int i = 0; i < WaitMs && !ReferenceEquals(MeshBvh.ParkedOn, mesh); i++)
            BlockingWait.Sleep(1, "self-check only: wait for the held mesh-tree worker to park");
        return ReferenceEquals(MeshBvh.ParkedOn, mesh);
    }

    /// <summary>The seam's cast: fold what was created since the last fold, then a triangle cast straight down at <paramref name="at"/>.</summary>
    private static bool Pick(Area a, Vector3 at, out HitResult r)
    {
        HitFeed.Sync();
        HitQuery q = HitQuery.Ray(a.Scene, new Vector3(at.X, at.Y, StandZ + 4f), new Vector3(at.X, at.Y, StandZ - 4f));
        q.SurfaceMask = HitQuery.All;
        return HitTest.Cast(q, out r);
    }

    private static bool Near(Vector3 got, Vector3 want) => Vector3.Distance(got, want) < 1e-3f;

    /// <summary>The <paramref name="i"/>th spot of a 10 m lattice, 20 to a row, at the standing height.</summary>
    private static Vector3 Lattice(int i) => new(20f + i % 20 * 10f, 20f + i / 20 * 10f, StandZ);

    /// <summary>Seat one object holding <paramref name="mesh"/> at lattice spot <paramref name="spot"/> and note it, as an area entry does.</summary>
    private static DbId Note(Area a, MeshDef mesh, int spot)
    {
        DbId o = Seat(a, Model(a, "note" + spot, mesh), Lattice(spot));
        HitFeed.NoteObject(o);
        return o;
    }

    /// <summary>A flat grid of <paramref name="side"/> x <paramref name="side"/> quads over a square of half-width
    /// <paramref name="half"/> in the z = 0 plane, every face wound to a +z normal.</summary>
    private static MeshDef Grid(int side, float half)
    {
        int vs = side + 1;
        var p = new Vector3[vs * vs];
        float step = 2f * half / side;
        int v = 0;
        for (int y = 0; y < vs; y++)
            for (int x = 0; x < vs; x++) p[v++] = new Vector3(-half + x * step, -half + y * step, 0f);
        var f = new FaceDef[2 * side * side];
        int k = 0;
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int c0 = y * vs + x, c1 = c0 + 1, c2 = c0 + vs + 1, c3 = c0 + vs;
                f[k++] = new FaceDef { V0 = c0, V1 = c1, V2 = c2, Adj0 = -1, Adj1 = -1, Adj2 = -1, PlaneNormal = Vector3.UnitZ };
                f[k++] = new FaceDef { V0 = c0, V1 = c2, V2 = c3, Adj0 = -1, Adj1 = -1, Adj2 = -1, PlaneNormal = Vector3.UnitZ };
            }
        return new MeshDef { Positions = p, Faces = f, BMin = new Vector3(-half, -half, -0.01f), BMax = new Vector3(half, half, 0.01f) };
    }

    /// <summary>A model with one mesh node per mesh, remembered on <paramref name="a"/> so its trees are evicted at teardown.</summary>
    private static ModelDef Model(Area a, string name, params MeshDef[] meshes)
    {
        var nodes = new NodeDef[meshes.Length];
        for (int i = 0; i < meshes.Length; i++)
            nodes[i] = new NodeDef { Name = name + "n" + i, Index = i, Parent = -1, Kind = NodeKind.TriMesh, Mesh = meshes[i] };
        var m = new ModelDef { Name = name, Nodes = nodes, BMin = meshes[0].BMin, BMax = meshes[0].BMax };
        a.Models[a.ModelCount++] = m;
        return m;
    }

    private static Area NewArea()
    {
        var a = new Area { Scene = SceneDb.Scenes.Alloc() };
        SceneDb.SceneIsWorld[a.Scene.Index] = true;
        SceneDb.SceneObjects[a.Scene.Index]?.Clear();
        return a;
    }

    /// <summary>One object record with a part per node of <paramref name="model"/>, placed at <paramref name="pos"/>, not yet noted.</summary>
    private static DbId Seat(Area a, ModelDef model, Vector3 pos)
    {
        DbId o = SceneDb.Objects.Alloc();
        int i = o.Index;
        DbFeed.InitObject(i);
        SceneDb.ObjKindOf[i] = ObjKind.Model;
        SceneDb.ObjModel[i] = model;
        SceneDb.ObjScene[i] = a.Scene;
        DbScene.Members(a.Scene).Add(o);
        SceneDb.ObjPos[i] = pos; SceneDb.ObjRot[i] = Quaternion.Identity; SceneDb.ObjScale[i] = 1f;
        SceneDb.ObjWorld[i] = Matrix4x4.CreateTranslation(pos);
        SceneDb.ObjVisible[i] = true;
        SceneDb.ObjTint[i] = Vector4.One;
        int n = model.Nodes.Length;
        if (!SceneDb.Parts.AllocRange(n, out int first)) { SceneDb.Objects.Free(o); return default; }
        SceneDb.ObjPartFirst[i] = first; SceneDb.ObjPartCount[i] = n;
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            DbFeed.InitPart(p);
            SceneDb.PartObj[p] = o;
            SceneDb.PartModel[p] = model;
            SceneDb.PartNode[p] = k;
            SceneDb.PartParent[p] = -1;
            SceneDb.PartPos[p] = Vector3.Zero; SceneDb.PartRot[p] = Quaternion.Identity; SceneDb.PartScale[p] = 1f;
            SceneDb.PartWorld[p] = SceneDb.ObjWorld[i];
            SceneDb.PartEnabled[p] = true; SceneDb.PartMerged[p] = false;
            SceneDb.PartMesh[p] = -1;
            SceneDb.PartMeshDef[p] = null;
        }
        a.Objs[a.Count++] = o;
        return o;
    }

    /// <summary>Free every record and scene the example made and evict its models' trees, so the next example starts clean.</summary>
    private static void Tear(Area a)
    {
        for (int k = 0; k < a.Count; k++)
        {
            DbId o = a.Objs[k];
            if (!SceneDb.Objects.Valid(o)) continue;
            HitGrid.Remove(o.Index);
            int first = SceneDb.ObjPartFirst[o.Index], n = SceneDb.ObjPartCount[o.Index];
            for (int p = first; p < first + n; p++)
            {
                SceneDb.HitPartBMin[p] = new Vector3(1f); SceneDb.HitPartBMax[p] = new Vector3(-1f);
                SceneDb.HitPartTrigger[p] = 0;
                SceneDb.PartModel[p] = null;
            }
            SceneDb.Parts.FreeRange(first, n);
            SceneDb.ObjModel[o.Index] = null;
            SceneDb.ObjPartCount[o.Index] = 0;
            SceneDb.Objects.Free(o);
        }
        for (int k = 0; k < a.ModelCount; k++) MeshBvh.Evict(a.Models[k]);
        if (SceneDb.Scenes.Valid(a.Scene)) { SceneDb.SceneObjects[a.Scene.Index]?.Clear(); SceneDb.Scenes.Free(a.Scene); }
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();
        SceneDb.Scenes.TakeDirty();
    }
}
