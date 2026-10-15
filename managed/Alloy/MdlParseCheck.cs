using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Alloy.Mdl;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The live parse check: for every model the engine loads, parse the SAME bytes with CX on a worker and
/// compare the trees node for node -- the live resource chain (haks, NWSync, override), not a corpus. The seam is
/// <c>IODispatcher::ReadSync</c>, the one funnel a model MISS goes through; its tree is snapshotted on the MAIN
/// thread because a <c>Model*</c> does not survive an area edge. Every engine read is here, not in <c>Alloy/Mdl/</c>.</summary>
internal static unsafe class MdlParseCheck
{
    internal static readonly KnobGroup G;
    /// <summary>Compare every engine model load against CX's own parse and report per area.</summary>
    internal static readonly Knob<bool> Check;

    static MdlParseCheck()
    {
        G = KnobGroup.Declare("mdl", "Graphics", "Model parser");
        G.Status = Describe;
        Check = G.Toggle("check", "Compare CX's model parse with the engine's", false, Tier.ChatOnly,
            help: "Diagnostic: every model the engine reads is also parsed by CX on its own worker and the two "
                + "trees are compared node for node -- kinds, names, node numbers, hierarchy, controllers and "
                + "vertex counts. Reports per area to cx.log with the CX worker time next to the engine's "
                + "main-thread parse time. Off by default: it doubles the read of every model.");
    }

    /// <summary>What a worker needs: the bytes' location, the resref, and the engine's tree as a VALUE.</summary>
    private sealed class Job
    {
        public string Name = "";
        public object? Token;
        public Tree Engine = new();
    }

    /// <summary>The engine's tree, flattened pre-order on the main thread. Nothing in it points into engine
    /// memory, so the worker may read it after the model is gone.</summary>
    private sealed class Tree
    {
        public readonly List<string> Name = new();
        public readonly List<uint> Kind = new();
        public readonly List<int> Part = new();
        public readonly List<int> Parent = new();
        public readonly List<int> Kids = new();
        public readonly List<int> Verts = new();
        public readonly List<int> Faces = new();
        /// <summary>Per node, its controllers as (engine id, key count, columns, bezier) quadruples, flat.</summary>
        public readonly List<int> Ctl = new();
        public readonly List<int> CtlAt = new();
        /// <summary>Every controller's key TIMES then its VALUES, flat, in the same order as
        /// <see cref="Ctl"/> -- read out of the node's own float pool, which is a plain pointer.</summary>
        public readonly List<float> Keys = new();
        public readonly List<int> KeysAt = new();
    }

    private const int MaxQueued = 512;
    /// <summary>Nodes a snapshot will walk before it gives up: a model with more is not a model.</summary>
    private const int MaxNodes = 4096;
    /// <summary>Floats one controller's keys may contribute to a snapshot.</summary>
    private const int MaxKeys = 1 << 16;

    private static ExtenderLog? _log;
    private static IoReadSyncFn* _next;
    private static readonly Queue<Job> _queue = new();
    private static readonly object Gate = new();
    private static readonly AutoResetEvent Woke = new(false);
    private static Thread? _worker;
    private static bool _armed;

    private static long _engineTicks, _parseTicks, _cmpTicks, _snapTicks;
    private static int _seen, _compared, _dropped, _refused, _failedParse, _fromAscii, _fromBinary, _refusals;
    /// <summary>How deep the engine is inside its own <c>ReadSync</c>: a model reads its supermodel from
    /// inside its own parse, so only the OUTERMOST call's time is the engine's cost for one model.</summary>
    [ThreadStatic] private static int _depth;
    /// <summary>Mismatches by kind: node count, kind, name, node number, hierarchy, controllers, vertices,
    /// faces. One counter per category, reported on the period line.</summary>
    private static readonly int[] _bad = new int[9];
    private static readonly string[] Cats =
        ["nodecount", "kind", "name", "nodenum", "tree", "controllers", "verts", "faces", "keys"];

    /// <summary>Two floats agree to the last decimal a model file carries.</summary>
    private static bool Near(float a, float b) => a == b || MathF.Abs(a - b) <= 2e-5f;
    private static int _logLeft = 12;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (Native.Sym(SymbolMap.IoReadSync) == IntPtr.Zero)
        {
            log.Always("MdlParseCheck: stood down -- IODispatcher::ReadSync unresolved");
            return;
        }
        if (!EngineLayout.Require(log, "MdlParseCheck", "Model", "MdlNode", "MdlNodeTriMesh", "NewController")) return;
        _next = hooks.ChainIoReadSync(&OnReadSync, ChainPriority);
        _armed = _next != null;
        if (!_armed) log.Always("MdlParseCheck: stood down -- the ReadSync chain refused the layer");
    }

    /// <summary>Late in the chain: every other layer's view of a model load is unchanged by a diagnostic.</summary>
    private const int ChainPriority = 90;

    [UnmanagedCallersOnly]
    private static IntPtr OnReadSync(IntPtr self, byte* name)
    {
        bool outermost = _depth++ == 0;
        long t0 = Stopwatch.GetTimestamp();
        IntPtr model;
        try { model = ((IoReadSyncFn)(*_next))(self, name); }
        finally { _depth--; }
        long t1 = Stopwatch.GetTimestamp();
        // A supermodel load nests inside its child's, so counting every level would double-count the
        // engine's work against CX's single parse. Only the outermost call is one model's cost.
        if (outermost) Interlocked.Add(ref _engineTicks, t1 - t0);
        // The material feed's own drain: the game thread is already on I/O here, and the next model is what
        // wants the hint.
        MtrFeed.Pump();
        if (!Check.V || model == IntPtr.Zero || name == null) return model;
        try { Offer(model, name); }
        catch (Exception e)
        {
            Interlocked.Increment(ref _refused);
            if (_logLeft-- > 0) _log?.Info($"MdlParseCheck: offer faulted -- {e.Message}");
        }
        Interlocked.Add(ref _snapTicks, Stopwatch.GetTimestamp() - t1);
        return model;
    }

    /// <summary>Main thread: snapshot the engine's tree, locate the bytes, queue the parse.</summary>
    private static void Offer(IntPtr model, byte* name)
    {
        Interlocked.Increment(ref _seen);
        string resref = Marshal.PtrToStringAnsi((IntPtr)name) ?? "";
        if (resref.Length == 0) return;
        resref = resref.ToLowerInvariant();
        int dot = resref.IndexOf('.');
        if (dot > 0) resref = resref[..dot];

        object? token = ModelFeed.Locate(resref, out string? why);
        if (token == null)
        {
            Interlocked.Increment(ref _refused);
            if (_logLeft-- > 0) _log?.Info($"MdlParseCheck: no bytes for '{resref}' -- {why}");
            return;
        }
        var job = new Job { Name = resref, Token = token };
        Snapshot(new CX.Engine.Model(model), job.Engine);
        lock (Gate)
        {
            if (_queue.Count >= MaxQueued) { _dropped++; return; }
            _queue.Enqueue(job);
        }
        Start();
        Woke.Set();
    }

    private static void Snapshot(CX.Engine.Model m, Tree t)
    {
        IntPtr root = m.Root.Ptr;
        if (root == IntPtr.Zero) return;
        Walk(root, -1, t);
    }

    private static void Walk(IntPtr node, int parent, Tree t)
    {
        if (node == IntPtr.Zero || t.Name.Count >= MaxNodes) return;
        var n = new MdlNode(node);
        int me = t.Name.Count;
        t.Name.Add(n.NameText ?? "");
        uint kind = n.Kind;
        t.Kind.Add(kind);
        t.Part.Add(n.NodeId);
        t.Parent.Add(parent);
        t.Kids.Add(n.ChildCount);
        bool mesh = (kind & EngineLayout.MdlNodeKindTriMesh) == EngineLayout.MdlNodeKindTriMesh
                    && kind != TriggerKind;
        var tri = new MdlNodeTriMesh(node);
        t.Verts.Add(mesh ? tri.VertexCount : 0);
        t.Faces.Add(mesh ? tri.FaceCount : 0);
        t.CtlAt.Add(t.Ctl.Count);
        int cn = n.ControllerCount;
        IntPtr ctl = n.Controllers;
        IntPtr data = n.ControllerData;
        if (ctl != IntPtr.Zero && cn > 0 && cn < 4096)
            for (int i = 0; i < cn; i++)
            {
                NewController r = new NewController(ctl)[i];
                int keys = r.KeyCount;
                int tk = r.TimeKey;
                int vk = r.ValueKey;
                byte fmt = r.Format;
                int cols = fmt & 0xf;
                bool bez = (fmt & 0x10) != 0;
                t.Ctl.Add(r.Id);
                t.Ctl.Add(keys);
                t.Ctl.Add(cols);
                t.Ctl.Add(bez ? 1 : 0);
                t.KeysAt.Add(t.Keys.Count);
                // The float pool is a plain pointer, so the KEYS themselves can be compared -- which is what
                // turns the live check from "the same shape" into "the same animation".
                int per = bez ? cols * 3 : cols;
                long want = (long)keys + (long)keys * per;
                if (data == IntPtr.Zero || keys <= 0 || cols <= 0 || want > MaxKeys) continue;
                for (int k = 0; k < keys; k++) t.Keys.Add(((float*)data)[tk + k]);
                for (int k = 0; k < keys * per; k++) t.Keys.Add(((float*)data)[vk + k]);
            }
        int kids = n.ChildCount;
        IntPtr items = n.Children;
        if (items == IntPtr.Zero || kids <= 0) return;
        for (int i = 0; i < kids; i++) Walk(((IntPtr*)items)[i], me, t);
    }

    /// <summary>The trigger kind passes the engine's mesh test but carries no mesh payload.</summary>
    private const uint TriggerKind = 0x421;   // not-an-offset: the MDL node-type bit set

    private static void Start()
    {
        if (_worker != null) return;
        _worker = new Thread(Loop) { IsBackground = true, Name = "cx-mdlcheck" };
        _worker.Start();
    }

    private static void Loop()
    {
        while (true)
        {
            Job? job = null;
            lock (Gate) if (_queue.Count != 0) job = _queue.Dequeue();
            if (job == null) { Woke.WaitOne(250); continue; }
            try { Compare(job); }
            catch (Exception e)
            {
                Interlocked.Increment(ref _refused);
                if (_logLeft-- > 0) _log?.Info($"MdlParseCheck: worker faulted on '{job.Name}' -- {e.Message}");
            }
        }
    }

    private static void Compare(Job job)
    {
        // Timed apart: the PARSE is what the engine's number compares against, the comparison is the
        // check's own cost and says nothing about the parser.
        long p0 = Stopwatch.GetTimestamp();
        ModelDef? m = ModelFeed.ParseToken(job.Token!, out string? why);
        long p1 = Stopwatch.GetTimestamp();
        Interlocked.Add(ref _parseTicks, p1 - p0);
        if (m == null)
        {
            Interlocked.Increment(ref _failedParse);
            if (_logLeft-- > 0) _log?.Info($"MdlParseCheck: '{job.Name}' did not parse -- {why}");
            return;
        }
        try { CompareTree(job, m); }
        finally { Interlocked.Add(ref _cmpTicks, Stopwatch.GetTimestamp() - p1); }
    }

    private static void CompareTree(Job job, ModelDef m)
    {
        Interlocked.Increment(ref _compared);
        if (m.FromAscii) Interlocked.Increment(ref _fromAscii); else Interlocked.Increment(ref _fromBinary);
        if (m.Refusals != 0)
        {
            Interlocked.Increment(ref _refusals);
            if (_logLeft-- > 0) _log?.Info($"MdlParseCheck: '{job.Name}' parsed with {m.Refusals} refusal(s) -- {m.RefusalNote}");
        }
        Tree t = job.Engine;
        if (m.Nodes.Length != t.Name.Count)
        {
            Fail(0, $"{job.Name}: {m.Nodes.Length} nodes, the engine has {t.Name.Count}");
            return;
        }
        for (int i = 0; i < m.Nodes.Length; i++)
        {
            NodeDef n = m.Nodes[i];
            if (n.ContentFlags != t.Kind[i]) { Fail(1, $"{job.Name}/{n.Name}: kind {n.ContentFlags:x} vs {t.Kind[i]:x}"); continue; }
            if (!string.Equals(n.Name, t.Name[i], StringComparison.OrdinalIgnoreCase))
                Fail(2, $"{job.Name}: node {i} '{n.Name}' vs '{t.Name[i]}'");
            // A model with a supermodel takes its node numbers from the chain, which the cache does, not the
            // parse; only an unsynced model's numbers are the parse's to answer for.
            if (m.NodeIdsSynced || m.SuperName.Length == 0)
                if (n.PartNumber != t.Part[i]) Fail(3, $"{job.Name}/{n.Name}: node number {n.PartNumber} vs {t.Part[i]}");
            if (n.Parent != t.Parent[i] || n.Children.Length != t.Kids[i])
                Fail(4, $"{job.Name}/{n.Name}: parent {n.Parent}/{t.Parent[i]}, children {n.Children.Length}/{t.Kids[i]}");
            int at = t.CtlAt[i];
            int end = i + 1 < t.CtlAt.Count ? t.CtlAt[i + 1] : t.Ctl.Count;
            int have = (end - at) / 4;
            if (n.Controllers.Length != have)
            {
                Fail(5, $"{job.Name}/{n.Name}: {n.Controllers.Length} controllers, the engine has {have}");
                continue;
            }
            for (int c = 0; c < have; c++)
            {
                ControllerTrack k = n.Controllers[c];
                int id = MdlBinary.EngineControllerId(k.Type, n.Kind);
                if (id != t.Ctl[at + c * 4] || k.Times.Length != t.Ctl[at + c * 4 + 1]
                    || k.Columns != t.Ctl[at + c * 4 + 2] || (k.Bezier ? 1 : 0) != t.Ctl[at + c * 4 + 3])
                {
                    Fail(5, $"{job.Name}/{n.Name}: controller {c} id {id}/{t.Ctl[at + c * 4]} keys "
                            + $"{k.Times.Length}/{t.Ctl[at + c * 4 + 1]} cols {k.Columns}/{t.Ctl[at + c * 4 + 2]}");
                    break;
                }
                int ka = t.KeysAt[at / 4 + c];
                int kend = at / 4 + c + 1 < t.KeysAt.Count ? t.KeysAt[at / 4 + c + 1] : t.Keys.Count;
                if (kend - ka != k.Times.Length + k.Values.Length) continue;   // the snapshot skipped it
                bool bad = false;
                for (int q = 0; q < k.Times.Length && !bad; q++)
                    if (!Near(k.Times[q], t.Keys[ka + q]))
                    { Fail(8, $"{job.Name}/{n.Name}: controller {id} t[{q}] {k.Times[q]} vs {t.Keys[ka + q]}"); bad = true; }
                for (int q = 0; q < k.Values.Length && !bad; q++)
                    if (!Near(k.Values[q], t.Keys[ka + k.Times.Length + q]))
                    { Fail(8, $"{job.Name}/{n.Name}: controller {id} v[{q}] {k.Values[q]} vs {t.Keys[ka + k.Times.Length + q]}"); bad = true; }
                if (bad) break;
            }
            if (n.Mesh == null) continue;
            // A zero on the engine's side is an answer, not an excuse: a skin node is welded at instance
            // time and CX welds it at parse time, which is the one documented difference.
            if (n.Mesh.Positions.Length != t.Verts[i] && !(n.Kind == NodeKind.Skin && t.Verts[i] == 0))
                Fail(6, $"{job.Name}/{n.Name}: {n.Mesh.Positions.Length} vertices, the engine has {t.Verts[i]}");
            if (n.Mesh.Faces.Length != t.Faces[i] && !(n.Kind == NodeKind.Skin && t.Verts[i] == 0))
                Fail(7, $"{job.Name}/{n.Name}: {n.Mesh.Faces.Length} faces, the engine has {t.Faces[i]}");
        }
    }

    private static void Fail(int cat, string what)
    {
        Interlocked.Increment(ref _bad[cat]);
        if (_logLeft-- > 0) _log?.Info($"MdlParseCheck MISMATCH [{Cats[cat]}] {what}");
    }

    /// <summary>The world edge: report the area's tally and start the next one clean. The queue is drained
    /// first so a model loaded late in the area still lands in its own line.</summary>
    internal static void AreaEdge()
    {
        MtrFeed.AreaEdge();
        if (!_armed) return;
        int queued;
        lock (Gate) queued = _queue.Count;
        if (_seen == 0 && _compared == 0) return;
        double eng = _engineTicks * 1000.0 / Stopwatch.Frequency;
        double cx = _parseTicks * 1000.0 / Stopwatch.Frequency;
        double cmp = _cmpTicks * 1000.0 / Stopwatch.Frequency;
        double snap = _snapTicks * 1000.0 / Stopwatch.Frequency;
        int bad = 0;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _bad.Length; i++)
        {
            bad += _bad[i];
            if (_bad[i] != 0) sb.Append(' ').Append(Cats[i]).Append('=').Append(_bad[i]);
        }
        _log?.Always($"MdlParseCheck: {(bad == 0 && _compared > 0 ? "PASS" : bad == 0 ? "no models" : "FAIL")} -- "
                     + $"{_compared} of {_seen} models compared ({_fromAscii} from ASCII, {_fromBinary} from the "
                     + $"compiled binary), {bad} mismatches{sb}; "
                     + $"cx PARSE {cx:F1} ms on cx-mdlcheck vs engine {eng:F1} ms on the main thread "
                     + $"(outermost ReadSync only; compare {cmp:F1} ms worker, snapshot {snap:F1} ms main, "
                     + $"{_failedParse} unparsed, {_refusals} with refusals, {_refused} refused, {_dropped} dropped, "
                     + $"{queued} still queued, {MdlPost.OutOfRange} out-of-range index/indices"
                     + (MdlParser.MaterialHintMisses != 0
                        ? $", {MdlParser.MaterialHintMisses} mesh(es) asked for a .mtr hint before it was read"
                        : "") + (MtrFeed.Stats() is { Length: > 0 } ms ? ", " + ms : "") + ")");
        Interlocked.Exchange(ref _engineTicks, 0);
        Interlocked.Exchange(ref _parseTicks, 0);
        Interlocked.Exchange(ref _cmpTicks, 0);
        Interlocked.Exchange(ref _snapTicks, 0);
        _seen = _compared = _dropped = _refused = _failedParse = _fromAscii = _fromBinary = _refusals = 0;
        MdlParser.MaterialHintMisses = 0;
        MdlPost.OutOfRange = 0;
        Array.Clear(_bad);
    }

    /// <summary>The <c>/mdl</c> status line and the settings panel's note.</summary>
    private static string Describe()
    {
        if (!_armed) return "stood down";
        int bad = 0;
        foreach (int b in _bad) bad += b;
        return $"{_compared}/{_seen} compared, {bad} mismatches";
    }

    /// <summary>A period clause for the pass registry.</summary>
    internal static string Stats(int frames) =>
        !_armed || !Check.V ? "" : $"[mdlcheck] {_compared}/{_seen}";
}
