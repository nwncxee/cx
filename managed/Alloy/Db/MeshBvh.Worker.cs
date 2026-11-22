using System;
using System.Collections.Generic;
using System.Threading;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The tree worker: the game thread queues a mesh, one worker thread builds its tree off the frame, and the
/// game thread files what it finished. The gate is held for bookkeeping only, never across a build.</summary>
public sealed partial class MeshBvh
{
    /// <summary>Meshes the queue holds at once; a note past it is left to the first pick, which builds on the spot.</summary>
    private const int QueueCap = 4096;

    /// <summary>Where one mesh's job stands.</summary>
    private enum Job : byte { Queued, InHand, Done }

    /// <summary>The worker's queue, its jobs by mesh, the finished trees waiting to be filed, and the tree census.</summary>
    private sealed class TreeWorkerState
    {
        public readonly Lock Gate = new();
        public readonly AutoResetEvent Wake = new(false);
        /// <summary>Meshes in queue order; a slot whose job was taken, evicted or built by a pick is skipped.</summary>
        public readonly MeshDef?[] Ring = new MeshDef?[QueueCap];
        public int Head, Count;
        public readonly Dictionary<MeshDef, Job> Jobs = new(QueueCap);
        /// <summary>Jobs in <see cref="Job.Queued"/>.</summary>
        public int Waiting;
        public MeshDef? InHand;
        public readonly List<MeshBvh> Done = new(QueueCap);
        public Thread? Worker;
        public int WorkerId = -1;
        public volatile bool Held;
        public volatile MeshDef? Parked;
        public int OnGame, OnWorker, Refused, Evicted;
        public long Faces, QueueFull, Faults;
    }
    private static readonly TreeWorkerState _w = new();

    /// <summary>Usable trees filed that a thread other than the tree worker built: a pick's on-the-spot build.</summary>
    public static int BuiltOnGame => Volatile.Read(ref _w.OnGame);
    /// <summary>Usable trees filed that the tree worker built.</summary>
    public static int BuiltOnWorker => Volatile.Read(ref _w.OnWorker);
    /// <summary>Notes the full queue turned away, and builds the worker lost to an exception.</summary>
    public static long QueueFull => Volatile.Read(ref _w.QueueFull);
    /// <inheritdoc cref="QueueFull"/>
    public static long Faults => Volatile.Read(ref _w.Faults);

    /// <summary>Game thread, allocation-free: queue <paramref name="mesh"/>'s tree for the worker unless it is filed or
    /// already has a job.</summary>
    public static void Prewarm(MeshDef mesh)
    {
        TreeWorkerState w = _w;
        lock (w.Gate)
        {
            if (_cache.ContainsKey(mesh) || w.Jobs.ContainsKey(mesh)) return;
            if (w.Count == QueueCap) { w.QueueFull++; return; }
            w.Ring[(w.Head + w.Count++) % QueueCap] = mesh;
            w.Jobs.Add(mesh, Job.Queued);
            w.Waiting++;
        }
        if (w.Worker == null)
        {
            w.Worker = new Thread(Work) { IsBackground = true, Name = "cx-meshbvh" };
            w.WorkerId = w.Worker.ManagedThreadId;
            w.Worker.Start();
        }
        w.Wake.Set();
    }

    /// <summary>The filed tree for <paramref name="mesh"/>, building and filing it here when it has none and the worker
    /// is not on it; null while the worker holds it mid-build, so the caller walks the faces instead of waiting.</summary>
    public static MeshBvh? For(MeshDef mesh)
    {
        TreeWorkerState w = _w;
        lock (w.Gate)
        {
            if (_cache.TryGetValue(mesh, out MeshBvh? filed)) return filed;
            if (w.Jobs.TryGetValue(mesh, out Job job))
            {
                if (job == Job.InHand) return null;
                if (job == Job.Done) { FileLocked(w); return _cache.GetValueOrDefault(mesh); }
                w.Jobs.Remove(mesh);
                w.Waiting--;
            }
        }
        var b = new MeshBvh();
        b.Build(mesh);
        lock (w.Gate)
        {
            if (_cache.TryGetValue(mesh, out MeshBvh? raced)) return raced;
            File(w, b);
        }
        return b;
    }

    /// <summary>Game thread: file the trees the worker has finished. A tree whose model was evicted meanwhile was
    /// already dropped by <see cref="Evict"/>.</summary>
    public static void FileFinished()
    {
        TreeWorkerState w = _w;
        lock (w.Gate) if (w.Done.Count > 0) FileLocked(w);
    }

    private static void FileLocked(TreeWorkerState w)
    {
        foreach (MeshBvh b in w.Done)
            if (w.Jobs.Remove(b.Mesh)) File(w, b);
        w.Done.Clear();
    }

    /// <summary>File one tree and count it for the thread that built it. Under the gate.</summary>
    private static void File(TreeWorkerState w, MeshBvh b)
    {
        _cache[b.Mesh] = b;
        if (!b.Usable) return;
        if (b._builtBy == w.WorkerId) w.OnWorker++;
        else w.OnGame++;
        w.Faces += b.Mesh.Faces.Length;
    }

    /// <summary>Drop the trees and jobs of <paramref name="model"/>'s meshes. Subscribed to the model cache's eviction
    /// edge, the ONLY thing that retires a content-keyed tree; a build the worker has in hand is dropped when it ends.</summary>
    public static void Evict(ModelDef model)
    {
        TreeWorkerState w = _w;
        lock (w.Gate)
            foreach (NodeDef nd in model.Nodes)
            {
                if (nd.Mesh == null) continue;
                if (_cache.Remove(nd.Mesh)) w.Evicted++;
                if (!w.Jobs.Remove(nd.Mesh, out Job job)) continue;
                if (job == Job.Queued) w.Waiting--;
                if (job != Job.Done) continue;
                for (int i = w.Done.Count - 1; i >= 0; i--)
                    if (ReferenceEquals(w.Done[i].Mesh, nd.Mesh)) w.Done.RemoveAt(i);
            }
    }

    /// <summary>True when <paramref name="mesh"/>'s tree is filed, so a pick reads it without building.</summary>
    public static bool Filed(MeshDef mesh)
    {
        lock (_w.Gate) return _cache.ContainsKey(mesh);
    }

    /// <summary>True while a job for <paramref name="mesh"/> waits in the tree worker's queue.</summary>
    public static bool Queued(MeshDef mesh)
    {
        lock (_w.Gate) return _w.Jobs.TryGetValue(mesh, out Job job) && job == Job.Queued;
    }

    /// <summary>True when the tree worker has nothing queued and nothing in hand.</summary>
    public static bool WorkerIdle
    {
        get { lock (_w.Gate) return _w.Waiting == 0 && _w.InHand == null; }
    }

    /// <summary>Self-check seam: the tree worker parks on the next job it takes, mid-build, until <see cref="ReleaseWorker"/>.</summary>
    internal static void HoldWorker() => _w.Held = true;
    /// <inheritdoc cref="HoldWorker"/>
    internal static void ReleaseWorker() => _w.Held = false;
    /// <summary>The mesh the held worker is parked on, mid-build; null while it is not parked.</summary>
    internal static MeshDef? ParkedOn => _w.Parked;

    /// <summary>The managed thread id that built <paramref name="mesh"/>'s filed tree; -1 while none is filed.</summary>
    internal static int BuilderOf(MeshDef mesh)
    {
        lock (_w.Gate) return _cache.TryGetValue(mesh, out MeshBvh? b) ? b._builtBy : -1;
    }

    /// <summary>The tree worker's managed thread id; -1 before it starts.</summary>
    internal static int WorkerThreadId => _w.WorkerId;

    /// <summary>The worker thread: take the next queued mesh, build its tree with the gate released, hand it back.</summary>
    private static void Work()
    {
        Affinity.ClearThis();
        TreeWorkerState w = _w;
        while (true)
        {
            MeshDef? m = Take(w);
            if (m == null) { w.Wake.WaitOne(); continue; }
            if (w.Held)
            {
                w.Parked = m;
                while (w.Held) BlockingWait.Sleep(1, "tree worker, self-check hold only: parked mid-build");
                w.Parked = null;
            }
            MeshBvh? b = new();
            try { b.Build(m); }
            catch (Exception) { b = null; Interlocked.Increment(ref w.Faults); }   // an escaped exception would abort the game
            lock (w.Gate)
            {
                w.InHand = null;
                // A faulted build keeps its job InHand, so a pick walks that mesh's faces and nothing rebuilds it.
                if (b == null || !w.Jobs.TryGetValue(m, out Job job)) continue;
                if (job == Job.Queued) w.Waiting--;
                w.Jobs[m] = Job.Done;
                w.Done.Add(b);
            }
        }
    }

    /// <summary>The next mesh still queued, moved into the worker's hand; null when the queue holds none.</summary>
    private static MeshDef? Take(TreeWorkerState w)
    {
        lock (w.Gate)
            while (w.Count > 0)
            {
                MeshDef? m = w.Ring[w.Head];
                w.Ring[w.Head] = null;
                w.Head = (w.Head + 1) % QueueCap;
                w.Count--;
                if (m == null || !w.Jobs.TryGetValue(m, out Job job) || job != Job.Queued) continue;
                w.Jobs[m] = Job.InHand;
                w.Waiting--;
                w.InHand = m;
                return m;
            }
        return null;
    }
}
