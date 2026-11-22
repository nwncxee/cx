using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>Found tests on the tree worker, over the approved examples' fixture: the paths the mutation pass showed no
/// example reached. Oracle: hand-worked face counts and the build counters counted per thread.</summary>
internal static partial class MeshBvhWorkerApprovedExamples
{
    /// <summary>Quads along a side of a mesh past <see cref="MeshBvh.MaxFaces"/>, and its faces by hand (2 x 182 x 182).</summary>
    private const int OverSide = 182, OverFaces = 66248;

    internal static bool FoundSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MeshBvhWorkerFound.SelfCheck");
        if (SceneDb.Scenes.Live != 0)
            return check.Skip($"the seam is live ({SceneDb.Scenes.Live} scene(s)) and the fixture writes to the real tables");

        PickBeforeFold(check);
        SharedRequeuedWhileQueued(check);
        SharedRequeuedAfterBuild(check);
        OverCapFiledUntreed(check);
        FiledNotQueued(check);

        if (check.Ok) log.Info("MeshBvhWorkerFound.SelfCheck: passed");
        return check.Ok;
    }

    /// <summary>A pick after the worker finished a mesh and before a fold filed it files that tree and walks it, and
    /// evicting the model then retires the filed tree and counts it.</summary>
    private static void PickBeforeFold(Checker check)
    {
        Area a = NewArea();
        MeshDef mesh = Grid(Side, 2f);
        try
        {
            MeshBvh.HoldWorker();
            DbId hit = Note(a, mesh, 0);
            check.Expect(Parked(mesh), "F1 the held worker parks on the mesh");
            HitFeed.Fold();
            MeshBvh.ReleaseWorker();
            check.Expect(Idle() && !MeshBvh.Filed(mesh), "F1 the worker finished the mesh and no fold has filed it");
            int game0 = MeshBvh.BuiltOnGame;
            long tested0 = MeshBvh.FacesTested;
            Vector3 at = Lattice(0) + new Vector3(0.21f, -0.43f, 0f);
            bool picked = Pick(a, at, out HitResult r);
            long tested = MeshBvh.FacesTested - tested0;
            check.Expect(picked && r.Obj == hit, $"F1 the pick answers the object ({r.Kind})");
            check.Expect(MeshBvh.Filed(mesh) && tested > 0 && tested <= TreeFacesTested && MeshBvh.BuiltOnGame == game0,
                         $"F1 the pick filed the finished tree and walked it: {tested} of {SideFaces} faces tested, {MeshBvh.BuiltOnGame - game0} built here");
            int evicted0 = MeshBvh.Evicted;
            MeshBvh.Evict(a.Models[0]);
            check.Expect(!MeshBvh.Filed(mesh) && MeshBvh.Evicted - evicted0 == 1,
                         $"F1 evicting the model retires its filed tree ({MeshBvh.Evicted - evicted0} counted)");
        }
        finally { MeshBvh.ReleaseWorker(); Settle(); Tear(a); }
    }

    /// <summary>A mesh two models share, evicted with one while queued and noted again by the other, is built once and
    /// the worker goes idle.</summary>
    private static void SharedRequeuedWhileQueued(Checker check)
    {
        Area a = NewArea();
        MeshDef filler = Grid(Side, 2f), shared = Grid(Side, 2f);
        try
        {
            MeshBvh.HoldWorker();
            Note(a, filler, 0);
            check.Expect(Parked(filler), "F2 the held worker parks on the filler mesh");
            Note(a, shared, 1);
            MeshBvh.Evict(a.Models[1]);
            Note(a, shared, 2);
            check.Expect(MeshBvh.Queued(shared), "F2 the shared mesh waits in the queue again, noted by the model still loaded");
            int worker0 = MeshBvh.BuiltOnWorker;
            MeshBvh.ReleaseWorker();
            check.Expect(Settle(), "F2 the worker drains");
            check.Expect(MeshBvh.Filed(shared) && MeshBvh.BuiltOnWorker - worker0 == 2,
                         $"F2 the filler and the shared mesh are built once each ({MeshBvh.BuiltOnWorker - worker0} built)");
        }
        finally { MeshBvh.ReleaseWorker(); Settle(); Tear(a); }
    }

    /// <summary>A shared mesh whose build finished, evicted with one model and noted again by the other before a fold,
    /// is not filed from the evicted build, and the worker builds it again.</summary>
    private static void SharedRequeuedAfterBuild(Checker check)
    {
        Area a = NewArea();
        MeshDef shared = Grid(Side, 2f), filler = Grid(Side, 2f);
        try
        {
            MeshBvh.HoldWorker();
            Note(a, shared, 0);
            check.Expect(Parked(shared), "F3 the held worker parks on the shared mesh");
            MeshBvh.ReleaseWorker();
            check.Expect(Idle() && !MeshBvh.Filed(shared), "F3 the shared mesh's build finished and is not filed yet");
            MeshBvh.HoldWorker();
            Note(a, filler, 1);
            check.Expect(Parked(filler), "F3 the held worker parks on the filler mesh");
            MeshBvh.Evict(a.Models[0]);
            Note(a, shared, 2);
            MeshBvh.FileFinished();
            check.Expect(!MeshBvh.Filed(shared) && MeshBvh.Queued(shared),
                         "F3 the fold files nothing of the evicted build, and the shared mesh waits in the queue");
            MeshBvh.ReleaseWorker();
            check.Expect(Settle(), "F3 the worker drains");
            check.Expect(MeshBvh.Filed(shared) && MeshBvh.BuilderOf(shared) == MeshBvh.WorkerThreadId,
                         "F3 the worker built the shared mesh again and it is filed");
        }
        finally { MeshBvh.ReleaseWorker(); Settle(); Tear(a); }
    }

    /// <summary>A mesh past the face cap is filed untreed: counted refused and not built, and a pick still answers it.</summary>
    private static void OverCapFiledUntreed(Checker check)
    {
        Area a = NewArea();
        MeshDef big = Grid(OverSide, 2f);
        try
        {
            check.Expect(big.Faces.Length == OverFaces, $"F4 the fixture mesh has {big.Faces.Length} faces, want {OverFaces}");
            int built0 = MeshBvh.Built, refused0 = MeshBvh.Refused;
            long faces0 = MeshBvh.BuiltFaces;
            DbId hit = Note(a, big, 0);
            check.Expect(Settle() && MeshBvh.Filed(big), "F4 the worker files the over-cap mesh");
            check.Expect(MeshBvh.Built == built0 && MeshBvh.BuiltFaces == faces0 && MeshBvh.Refused - refused0 == 1,
                         $"F4 it is counted refused, not built ({MeshBvh.Built - built0} built, {MeshBvh.Refused - refused0} refused)");
            Vector3 at = Lattice(0) + new Vector3(0.37f, 0.61f, 0f);
            bool picked = Pick(a, at, out HitResult r);
            check.Expect(picked && r.Obj == hit && Near(r.Point, new Vector3(at.X, at.Y, StandZ)),
                         $"F4 a pick on the untreed mesh answers the object at its face ({r.Kind}, {r.Point})");
        }
        finally { Tear(a); }
    }

    /// <summary>A part noted over a mesh whose tree is filed queues nothing, and the worker builds no second tree.</summary>
    private static void FiledNotQueued(Checker check)
    {
        Area a = NewArea();
        MeshDef mesh = Grid(Side, 2f);
        try
        {
            Note(a, mesh, 0);
            check.Expect(Settle() && MeshBvh.Filed(mesh), "F5 the worker files the mesh's tree");
            int worker0 = MeshBvh.BuiltOnWorker;
            Note(a, mesh, 1);
            check.Expect(!MeshBvh.Queued(mesh), "F5 a second part over the filed mesh queues nothing");
            check.Expect(Settle() && MeshBvh.BuiltOnWorker == worker0,
                         $"F5 the worker builds no second tree ({MeshBvh.BuiltOnWorker - worker0} built)");
        }
        finally { Tear(a); }
    }

    /// <summary>Wait for the worker to have nothing queued or in hand, filing nothing.</summary>
    private static bool Idle()
    {
        for (int i = 0; i < WaitMs && !MeshBvh.WorkerIdle; i++)
            BlockingWait.Sleep(1, "self-check only: wait for the mesh-tree worker to finish");
        return MeshBvh.WorkerIdle;
    }
}
