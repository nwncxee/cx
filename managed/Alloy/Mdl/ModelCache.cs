using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using CX.Framework;

namespace CX.Alloy.Mdl;

/// <summary>The model cache over <see cref="MdlParser"/> -- the engine's <c>FindModel</c> @0x44c2a0,
/// refcounts, supermodel chains and animation lookup. Game-thread API, parses on its worker, bytes through
/// <see cref="IModelSource"/> so it stays engine-free. Zero references does NOT free a model: it enters a
/// byte-budgeted resident set (<see cref="ModelKnobs.Budget"/>), and a supermodel cycle is CUT, loudly.</summary>
public static partial class ModelCache
{
    /// <summary>The sorted model table: the engine's <c>Models</c> array, same key and same ordering.</summary>
    private static readonly List<Slot> _table = new();
    /// <summary>Slot by model identity, for <see cref="Release"/> and the binding cache.</summary>
    private static readonly Dictionary<ModelDef, Slot> _bySlot = new(ReferenceEqualityComparer.Instance);
    /// <summary>Zero-reference models, least recently used LAST: the eviction order, as a list so a trim is
    /// O(evicted) rather than O(table) per victim. Each slot owns its node for the slot's whole life, so going
    /// idle and being acquired again move a node that is already allocated.</summary>
    private static readonly LinkedList<Slot> _lru = new();

    /// <summary>One model's cache state. The <see cref="ModelDef"/> is immutable apart from
    /// <see cref="ModelDef.Name"/> and <see cref="ModelDef.Super"/>, which this file owns.</summary>
    private sealed class Slot
    {
        public ModelDef Def = null!;
        /// <summary>References held by objects and by child models (a model refs its supermodel), plus the one
        /// <see cref="Loader"/> reference a freshly parsed model carries.</summary>
        public int Refs;
        public long Bytes;
        /// <summary>The slot's place in <see cref="_lru"/> while it is idle; null while it is held.</summary>
        public LinkedListNode<Slot>? Node;
        /// <summary>Supermodel chain depth (the engine's <c>Model+0x58</c>), 0 for a chain root.</summary>
        public int Depth;
        /// <summary>The chain is resolved for good: either it is complete or a cycle / a chain cap / too many
        /// failed tries settled it. A TRANSIENT failure does NOT latch, so a later acquire retries.</summary>
        public bool Chained;
        /// <summary>The chain is being resolved right now: the cycle guard, which is what the engine gets for
        /// free from inserting a model before parsing it.</summary>
        public bool Chaining;
        /// <summary>Failed chain resolutions so far. Past <see cref="MaxChainTries"/> the model keeps whatever
        /// chain it has and says so, rather than probing the resource chain on every acquire forever.</summary>
        public int ChainTries;
        /// <summary>The reference a just-parsed model carries so a read-ahead cannot be evicted before the
        /// thing that asked for it arrives; the first <see cref="Acquire"/> takes it over, and
        /// <see cref="RetireLoaderRefs"/> drops it if nothing ever does.</summary>
        public bool Loader;
        /// <summary>The drip the loader reference was taken at.</summary>
        public long LoaderAt;
        /// <summary>Left the name index at a module edge while still held: no lookup finds it, and it is
        /// evicted the moment its last reference drops instead of idling.</summary>
        public bool Detached;
    }

    /// <summary>A model file's chain cannot be longer than this. The engine has no ceiling because it relies on
    /// insert-before-parse to terminate; CX resolves the chain after the parse, so it needs one.</summary>
    private const int MaxChain = 64;
    /// <summary>Attempts to resolve a chain whose supermodel does not load before the model settles for what it
    /// has. Three, because the interesting transient is a read that lost a race, not a hundred of them.</summary>
    private const int MaxChainTries = 3;
    /// <summary>How long <see cref="Acquire"/> will wait for its own worker before giving up. A wait that long
    /// is a broken mechanism, not a slow disk, and it is reported rather than hung on.</summary>
    private const int WaitMs = 10_000;
    /// <summary>Lines one CAUSE prints before it only counts. Per cause, not one budget for all of them: a
    /// chatty cause used to be able to spend the whole budget and hide every other refusal in the session.</summary>
    private const int LogCapPerCause = 4;
    /// <summary>Drips a read-ahead's own reference survives unasked-for. ~10 s at 60 fps, which is past any
    /// area load; the same argument as <c>ModelLoad</c>'s sweep age.</summary>
    private const int LoaderGraceDrips = 600;

    private static ExtenderLog? _log;
    private static IModelSource? _source;
    /// <summary>Bumped on every source change, stamped on every job: a parse that was in flight across the
    /// change describes bytes from a world that is gone, and is dropped rather than filed.</summary>
    private static int _sourceGen;
    /// <summary>Chain resolutions nested right now: the only real bound on the recursion, because the chain
    /// walks through <see cref="Acquire"/> and each level starts a fresh one.</summary>
    private static int _chaining;
    /// <summary><see cref="Pump"/> calls, i.e. frames with a drip: the loader-reference clock.</summary>
    private static long _drip;

    /// <summary>Fired just before a model leaves the cache, so whoever uploaded its meshes can let them go --
    /// <c>MeshArena.Release</c> subscribes. Eviction, NOT refcount zero, is the edge (ADR 0028 section 3): a
    /// model at zero references is still resident and still drawable.</summary>
    public static Action<ModelDef>? Evicting;

    private static long _bytes, _idleBytes;
    private static long _hits, _misses, _parsed, _waitTicks, _evicted, _absent, _cycles, _missingSupers,
                        _overRelease, _timeouts, _nosource, _prefetched, _prefetchHits, _renamed, _stale,
                        _staleSource, _loaderDropped, _offerHits;

    // ---- lifecycle ----

    /// <summary>Take the log and read the knobs. Called FIRST and unconditionally, before any source is
    /// installed: a session where the feed cannot attach is exactly the session whose refusals have to be
    /// readable, and a null log here would have dropped every one of them.</summary>
    public static void Attach(ExtenderLog log)
    {
        _log = log;
        _ = ModelKnobs.Budget;
    }

    /// <summary>Install the source every parse reads through. A source change clears the cache and invalidates
    /// every parse in flight: the bytes behind a resident model are no longer its own.</summary>
    public static void SetSource(IModelSource? source)
    {
        if (ReferenceEquals(_source, source)) return;
        if (_source != null) Clear();
        _source = source;
        _sourceGen++;
        // A new source is a new set of causes, so the per-cause log budget starts over. Without this the
        // self-check fixtures (which install a source of their own at boot) spend the whole session's budget
        // on names from a fixture, and the live session's refusals -- the ones a crash report needs -- are
        // silent.
        _said.Clear();
    }

    /// <summary>Drop the whole table, references and all, and abandon every parse in flight: a module unload or
    /// a source change, where every resident model names content that is gone. Each model's
    /// <see cref="Evicting"/> still fires.</summary>
    public static void Clear()
    {
        for (int i = _table.Count - 1; i >= 0; i--) Evict(_table[i], i);
        _table.Clear();
        _bySlot.Clear();
        _lru.Clear();
        _loaders = 0;
        ClearBindings();
        ClearResidentMeshes();
        lock (Gate)
        {
            _queue.Clear();
            _flight.Clear();
            _done.Clear();
            _sourceGen++;   // anything the worker is mid-parse on publishes under the old generation
            Monitor.PulseAll(Gate);
        }
        _bytes = _idleBytes = 0;
    }

    /// <summary>The module edge: the next module resolves every resref through its own haks and NWSync
    /// manifest, so no model read for the previous one may answer a lookup. Idle models are evicted now; one
    /// still held leaves the name index and is evicted at its last release, so held models are never pulled
    /// from under their objects. Parses in flight are abandoned. Returns (evicted, detached).</summary>
    public static (int Evicted, int Detached) ModuleEdge()
    {
        for (int i = 0; i < _table.Count && _loaders != 0; i++)
            if (_table[i].Loader) DropLoaderRef(_table[i]);
        int evicted = 0, detached = 0;
        for (int i = _table.Count - 1; i >= 0; i--)
        {
            if (i >= _table.Count) continue;   // an eviction's supermodel release may shrink the table under us
            Slot s = _table[i];
            if (s.Refs == 0) { Evict(s, i); evicted++; continue; }
            s.Detached = true;
            _table.RemoveAt(i);
            detached++;
        }
        lock (Gate)
        {
            _queue.Clear();
            _flight.Clear();
            _done.Clear();
            _sourceGen++;
            Monitor.PulseAll(Gate);
        }
        return (evicted, detached);
    }

    // ---- the engine's three members ----

    /// <summary>The engine's <c>FindModel</c> name gate: a null, empty or <c>"NULL"</c> name is not a model
    /// (<c>strcasecmp(name, "NULL")</c> @0x44c2fd), and every other name is the lower-cased resref the table is
    /// keyed on. Allocation-free for a name that is already lower-case, which every name the engine and CX pass
    /// is -- a hit must not cost a string.</summary>
    private static string? Norm(string? resref)
    {
        if (string.IsNullOrEmpty(resref)) return null;
        bool upper = false;
        for (int i = 0; i < resref.Length; i++) if ((uint)(resref[i] - 'A') < 26u) { upper = true; break; }
        string n = upper ? resref.ToLowerInvariant() : resref;
        return n == "null" ? null : n;
    }

    /// <summary><c>strcasecmp</c>, byte for byte: lower-case each ASCII character and subtract. .NET's
    /// <c>OrdinalIgnoreCase</c> UPPER-cases, which orders <c>'_'</c> (0x5f) on the other side of the letters --
    /// a consistent table either way, but this one is the engine's.</summary>
    internal static int Strcasecmp(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int ca = a[i], cb = b[i];
            if ((uint)(ca - 'A') < 26u) ca += 32;
            if ((uint)(cb - 'A') < 26u) cb += 32;
            if (ca != cb) return ca < cb ? -1 : 1;
        }
        return a.Length == b.Length ? 0 : a.Length < b.Length ? -1 : 1;
    }

    /// <summary>The engine's <c>BinarySearchModel(name, lo, hi)</c>: the index of the slot whose name matches,
    /// or -1. Inclusive bounds, as the engine's are.</summary>
    internal static int BinarySearchModel(string name, int lo, int hi)
    {
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            int c = Strcasecmp(name, _table[mid].Def.Name);
            if (c == 0) return mid;
            if (c < 0) hi = mid - 1; else lo = mid + 1;
        }
        return -1;
    }

    /// <summary>The engine's <c>BinaryInsertModel</c>: the sorted position for <paramref name="m"/>, found by
    /// the same walk, then one insert. The slot carries the LOADER reference (see <see cref="Slot.Loader"/>)
    /// rather than the engine's zero, because CX's table has an eviction the engine's has not: a read-ahead
    /// inserted at zero references could be trimmed before whatever asked for it arrived.</summary>
    internal static void InsertModel(ModelDef m)
    {
        int lo = 0, hi = _table.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (Strcasecmp(m.Name, _table[mid].Def.Name) < 0) hi = mid - 1; else lo = mid + 1;
        }
        var s = new Slot { Def = m, Bytes = Footprint(m), Refs = 1, Loader = true, LoaderAt = _drip };
        _loaders++;
        s.Node = new LinkedListNode<Slot>(s);
        _table.Insert(lo, s);
        _bySlot[m] = s;
        _bytes += s.Bytes;
        NoteResidentMeshes(m, 1);
    }

    /// <summary>The model named <paramref name="resref"/> if it is resident, WITHOUT taking a reference: the
    /// engine's <c>BinaryFindModel</c>. Its supermodel chain is resolved once <see cref="Acquire"/> or
    /// <see cref="Resolve"/> has needed it.</summary>
    public static ModelDef? FindModel(string? resref)
    {
        string? n = Norm(resref);
        if (n == null) return null;
        int i = BinarySearchModel(n, 0, _table.Count - 1);
        return i < 0 ? null : _table[i].Def;
    }

    // ---- refcounts ----

    /// <summary>The engine's <c>Model::AddRef</c>. A model that was idle leaves the eviction list.</summary>
    private static void AddRef(Slot s)
    {
        if (s.Refs++ == 0 && s.Node!.List != null) { _lru.Remove(s.Node); _idleBytes -= s.Bytes; }
    }

    /// <summary>The engine's <c>Model::DelRef</c>, minus the delete: zero references means idle, not dead.</summary>
    private static void DelRef(Slot s)
    {
        if (s.Refs == 0)
        {
            _overRelease++;
            Say("overrelease", $"ModelCache: {s.Def.Name} released with no reference held -- the release is dropped");
            return;
        }
        if (--s.Refs == 0) GoIdle(s);
    }

    private static void GoIdle(Slot s)
    {
        if (s.Detached) { Evict(s, -1); return; }
        if (s.Node!.List == null) { _lru.AddFirst(s.Node); _idleBytes += s.Bytes; }
    }

    /// <summary>Drop one reference taken by <see cref="Acquire"/>. Trims the resident set afterwards, which is
    /// what makes "released by refcount on an area change" give the memory back and not just the reference.</summary>
    public static void Release(ModelDef model)
    {
        if (model == null) return;
        if (!_bySlot.TryGetValue(model, out Slot? s))
        {
            _overRelease++;
            Say("orphan-release", $"ModelCache: {model.Name} released but is not in the table -- the release is dropped");
            return;
        }
        DelRef(s);
        Trim();
    }

    // ---- acquire ----

    /// <summary>The model named <paramref name="resref"/> with its supermodel chain resolved, one reference
    /// added; null when no such model exists or it failed to parse (logged once per cause). Synchronous,
    /// because <c>NewCAurObject</c> wants its model now: a miss queues the parse on the worker and WAITS. The
    /// main thread never parses -- a parse that cannot be handed over is a counted refusal.</summary>
    public static ModelDef? Acquire(string resref) => Acquire(resref, out _);

    /// <inheritdoc cref="Acquire(string)"/>
    /// <param name="why">Why a null answer is null. The caller decides what a refusal of ITS model means (the
    /// seam tells "no such model" from "CX could not read one the engine would have"), and the cache's own log
    /// is capped per cause.</param>
    public static ModelDef? Acquire(string resref, out string? why)
    {
        why = null;
        string? n = Norm(resref);
        if (n == null) { why = "the name is empty or the literal \"NULL\""; return null; }
        Drain();
        int i = BinarySearchModel(n, 0, _table.Count - 1);
        if (i < 0)
        {
            _misses++;
            if (!Load(n, out why)) return null;
            i = BinarySearchModel(n, 0, _table.Count - 1);
            if (i < 0) { why = "parsed, but the table does not hold it"; return null; }
        }
        else _hits++;
        Slot s = _table[i];
        if (s.Loader) { s.Loader = false; _loaders--; }   // the loader's own reference becomes this caller's
        else AddRef(s);
        Chain(s);
        return s.Def;
    }

    /// <summary>Read-ahead from the area message, where <c>ModelLoad</c> already has the tile and static
    /// placeable lists before the engine does: queue the parse now so the later <see cref="Acquire"/> is a hit.
    /// Never waits.</summary>
    public static void Prefetch(string resref)
    {
        string? n = Norm(resref);
        if (n == null || _source == null) return;
        // Already resident: the second thing that wants this model does not re-read or re-parse it. Counted,
        // because that IS the cache's perf claim -- a tile grid offers the same model once per placement, and
        // a second area (or a re-entry) offers it again.
        if (BinarySearchModel(n, 0, _table.Count - 1) >= 0) { _offerHits++; return; }
        if (Start(n, out _) != null) _prefetched++;
    }

    /// <summary>File every parse the worker finished, read ahead for the supermodels they named, retire the
    /// read-aheads nothing asked for, and trim. Cheap when nothing finished: one lock and one count. Called
    /// from the frame drip and from every <see cref="Acquire"/>.</summary>
    public static void Pump()
    {
        _drip++;
        Drain();
        RetireLoaderRefs();
        Trim();
    }

    /// <summary>Just the publish half of <see cref="Pump"/>: what an <see cref="Acquire"/> needs, without the
    /// drip clock or a trim that could evict the very read-ahead it is about to take.</summary>
    private static void Drain()
    {
        while (true)
        {
            Job? j;
            lock (Gate) j = _done.Count > 0 ? _done.Dequeue() : null;
            if (j == null) return;
            Publish(j);
        }
    }

    /// <summary>One finished job onto the table, then the read-ahead for its supermodel. The insert comes
    /// BEFORE the chain is resolved, as <c>Model::Model</c> has it, so a cycle finds the half-built model
    /// instead of recursing.</summary>
    private static void Publish(Job j)
    {
        lock (Gate) if (_flight.TryGetValue(j.Name, out Job? f) && ReferenceEquals(f, j)) _flight.Remove(j.Name);
        if (j.Published) return;
        j.Published = true;
        if (j.Gen != _sourceGen)
        {
            _staleSource++;
            Say("stale-source", $"ModelCache: {j.Name}: parsed against a source that has since been replaced -- dropped");
            return;
        }
        if (j.Def == null)
        {
            _absent++;
            Say("parse-refused", $"ModelCache: {j.Name}: {j.Why ?? "no model"}");
            return;
        }
        // The resource may have been aliased or the tables rebuilt while the worker read: the source re-proves
        // its own token here, on this thread, and a model whose bytes are no longer the ones asked for is
        // dropped rather than filed under a name it does not belong to.
        if (_source != null && !_source.StillValid(j.Token, out string? why))
        {
            _stale++;
            Say("stale-resource", $"ModelCache: {j.Name}: {why ?? "the resource moved while it was read"} -- dropped");
            return;
        }
        if (BinarySearchModel(j.Name, 0, _table.Count - 1) >= 0) return;   // two jobs raced; the first one won
        // Filed under the resref that was ASKED for. A model file whose internal name disagrees is what the
        // engine logs as "Model Names Differ" (@0x44c5f0) and then keeps the FILE's name for, so every later
        // lookup of the requested resref misses and re-reads it. CX keys on the request and counts the gap.
        string inner = j.Def.Name;
        if (inner.Length != 0 && Strcasecmp(inner, j.Name) != 0)
        {
            _renamed++;
            Say("name-differs", $"ModelCache: model names differ: the file says {inner}, it was asked for as "
                              + $"{j.Name} -- filed under {j.Name}");
        }
        j.Def.Name = j.Name;
        InsertModel(j.Def);
        if (j.Def.SuperName.Length != 0) Prefetch(j.Def.SuperName);
    }

    /// <summary>Parse <paramref name="name"/> now: queue it (or join the read-ahead already in flight) and
    /// wait. False means the model is not available, the reason has been counted and <paramref name="why"/>
    /// names it.</summary>
    private static bool Load(string name, out string? why)
    {
        why = null;
        if (_source == null)
        {
            _nosource++;
            why = "no model source is installed";
            Say("nosource", "ModelCache: no model source installed -- nothing can be parsed");
            return false;
        }
        Job? j = Start(name, out why);
        if (j == null) return false;
        if (j.Queued) _prefetchHits++;
        long t0 = Stopwatch.GetTimestamp();
        bool ok;
        lock (Gate)
        {
            long deadline = Environment.TickCount64 + WaitMs;
            while (!j.Finished)
            {
                int left = (int)(deadline - Environment.TickCount64);
                if (left <= 0) break;
                Monitor.Wait(Gate, left);
            }
            ok = j.Finished;
        }
        _waitTicks += Stopwatch.GetTimestamp() - t0;
        if (!ok)
        {
            _timeouts++;
            why = $"the parse worker did not answer in {WaitMs} ms";
            Say("timeout", $"ModelCache: {name}: the parse worker did not answer in {WaitMs} ms -- the model is refused");
            return false;
        }
        Drain();
        Publish(j);
        if (BinarySearchModel(name, 0, _table.Count - 1) >= 0) return true;
        why = j.Why ?? "the parse produced no model";
        return false;
    }

    // ---- supermodel chains ----

    /// <summary>Resolve <paramref name="s"/>'s chain as far as it goes, taking the reference a model holds on
    /// its supermodel (the engine's <c>setsupermodel</c> @0x44cd20 does the same <c>AddRef</c>). Two marks:
    /// <see cref="Slot.Chaining"/> is the cycle guard, while <see cref="Slot.Chained"/> latches only on a
    /// SETTLED answer -- a supermodel that merely failed this time leaves the model unlatched to retry.</summary>
    private static void Chain(Slot s)
    {
        if (s.Chained || s.Chaining) return;
        string sup = s.Def.SuperName;
        if (Norm(sup) == null) { s.Chained = true; s.Depth = 0; return; }
        if (_chaining >= MaxChain)
        {
            _cycles++;
            s.Chained = true;
            Say("chain-cap", $"ModelCache: {s.Def.Name}: supermodel chain deeper than {MaxChain} -- cut at {sup}");
            return;
        }
        s.Chaining = true;
        ModelDef? super;
        _chaining++;
        try { super = Acquire(sup); }
        finally { _chaining--; s.Chaining = false; }
        if (super == null)
        {
            _missingSupers++;
            if (++s.ChainTries >= MaxChainTries) s.Chained = true;
            Say("nosuper", $"ModelCache: {s.Def.Name}: supermodel {sup} did not load -- its animations are "
                         + (s.Chained ? "unavailable" : "unavailable for now; the next acquire retries"));
            return;
        }
        if (ReferenceEquals(super, s.Def) || Reaches(super, s.Def))
        {
            _cycles++;
            s.Chained = true;
            Say("cycle", $"ModelCache: {s.Def.Name}: supermodel {sup} closes a cycle -- the link is cut");
            Release(super);
            return;
        }
        s.Def.Super = super;
        s.Depth = _bySlot.TryGetValue(super, out Slot? up) ? up.Depth + 1 : 1;
        // The engine's beginmodelgeom @0x447f90 runs MaxTree::SynchronizeTree(model, supermodel) at PARSE
        // time, re-keying the model's node numbers off its supermodel's by name. CX's parser has no chain to
        // do that against, so it is the cache's job and it belongs exactly here: the recursive Acquire above
        // returned a supermodel whose own chain is already resolved, so its numbers are already the chain's.
        if (!s.Def.NodeIdsSynced)
        {
            MdlPost.Resync(s.Def, super);
            _resynced++;
        }
        s.Chained = true;
        ForgetResolution(s.Def);   // a memo taken before the chain existed answered a shorter chain
    }

    /// <summary>Models whose node numbers were re-keyed off their supermodel. On the period line because a
    /// zero here with chained models present would mean the part trees are numbering off file order.</summary>
    private static long _resynced;

    /// <summary>Resolve the chain of <paramref name="model"/> if the cache owns it. What keeps a
    /// <see cref="FindModel"/> or read-ahead handle from memoising a chain-less animation set.</summary>
    private static void EnsureChained(ModelDef model)
    {
        if (_bySlot.TryGetValue(model, out Slot? s)) Chain(s);
    }

    /// <summary>Does <paramref name="from"/>'s chain already contain <paramref name="target"/>? The cycle test,
    /// bounded by <see cref="MaxChain"/> so a chain already cut cannot hang it.</summary>
    private static bool Reaches(ModelDef from, ModelDef target)
    {
        ModelDef? m = from;
        for (int i = 0; i < MaxChain && m != null; i++, m = m.Super)
            if (ReferenceEquals(m, target)) return true;
        return false;
    }

    // ---- the resident set ----

    /// <summary>Drop the read-ahead reference of anything nothing has asked for in
    /// <see cref="LoaderGraceDrips"/> drips: a model offered by an area message that never became an object
    /// was not going to be, and its reference would pin it past the budget for the session. Counted rather
    /// than scanned for, because this runs on every drip.</summary>
    private static void RetireLoaderRefs()
    {
        if (_loaders == 0) return;
        for (int i = 0; i < _table.Count; i++)
        {
            Slot s = _table[i];
            if (!s.Loader || _drip - s.LoaderAt < LoaderGraceDrips) continue;
            DropLoaderRef(s);
        }
    }

    /// <summary>Give up one read-ahead's own reference.</summary>
    private static void DropLoaderRef(Slot s)
    {
        s.Loader = false;
        _loaders--;
        _loaderDropped++;
        DelRef(s);
    }

    /// <summary>Slots still carrying <see cref="Slot.Loader"/>.</summary>
    private static int _loaders;

    /// <summary>Evict idle models, least recently used first, until the zero-reference tail fits the budget.</summary>
    private static void Trim()
    {
        long cap = BudgetBytes;
        while (_idleBytes > cap && _lru.Last != null)
        {
            Slot victim = _lru.Last.Value;
            int at = IndexOf(victim);
            Evict(victim, at);
        }
    }

    /// <summary>The victim's index in the sorted table. A binary search on its own name, which is unique in the
    /// table by construction, so this is O(log n) and not the linear scan the first cut used. A name that does
    /// NOT find its own slot would mean the sort key moved under the table, which is a broken mechanism and not
    /// a reason to leave a row behind: the scan finds it and says so.</summary>
    private static int IndexOf(Slot s)
    {
        int i = BinarySearchModel(s.Def.Name, 0, _table.Count - 1);
        if (i >= 0 && ReferenceEquals(_table[i], s)) return i;
        for (int k = 0; k < _table.Count; k++)
            if (ReferenceEquals(_table[k], s))
            {
                _misplaced++;
                Say("misplaced", $"ModelCache: {s.Def.Name} is not where its own key says it is -- the table's "
                               + "sort order has moved under it");
                return k;
            }
        return -1;
    }

    private static long _misplaced;

    /// <summary>Let one model go: the arena ranges first (<c>MeshArena.Release</c> through
    /// <see cref="Evicting"/>), then its bindings, then the reference it held on its supermodel -- which may
    /// put the supermodel on the tail for the next pass.</summary>
    private static void Evict(Slot s, int indexInTable)
    {
        if (indexInTable >= 0 && indexInTable < _table.Count && ReferenceEquals(_table[indexInTable], s))
            _table.RemoveAt(indexInTable);
        _bySlot.Remove(s.Def);
        if (s.Loader) { s.Loader = false; _loaders--; }
        if (s.Node!.List != null) { _lru.Remove(s.Node); _idleBytes -= s.Bytes; }
        _bytes -= s.Bytes;
        NoteResidentMeshes(s.Def, -1);
        Forget(s.Def);
        try { Evicting?.Invoke(s.Def); }
        catch (Exception e) { Say("evict-hook", $"ModelCache: {s.Def.Name}: the evict hook threw -- {e.Message}"); }
        ModelDef? super = s.Def.Super;
        s.Def.Super = null;
        if (super != null && _bySlot.TryGetValue(super, out Slot? up)) DelRef(up);
        _evicted++;
    }

    /// <summary>An approximate CPU footprint: what the budget is spent on. Vertex, index and key data dominate
    /// by orders of magnitude, so the arrays are measured and the per-object overhead is only estimated.
    /// Allocation-free: every loop is over an array.</summary>
    private static long Footprint(ModelDef m)
    {
        long n = 512;
        foreach (NodeDef nd in m.Nodes)
        {
            n += 256;
            MeshDef? mesh = nd.Mesh;
            if (mesh != null)
            {
                n += (long)mesh.Positions.Length * 12 + (long)mesh.Normals.Length * 12
                   + (long)mesh.Tangents.Length * 16 + (long)mesh.Colors.Length * 4
                   + (long)mesh.Indices.Length * 4 + (long)mesh.Faces.Length * 56;
                for (int i = 0; i < mesh.Uv.Length; i++) n += (long)mesh.Uv[i].Length * 8;
            }
            SkinDef? sk = nd.Skin;
            if (sk != null) n += (long)sk.Weights.Length * 16 + (long)sk.Bones.Length * 4;
            AnimMeshDef? am = nd.AnimMesh;
            if (am != null)
                for (int i = 0; i < am.Positions.Length; i++) n += (long)am.Positions[i].Length * 12;
            AabbDef? ab = nd.Aabb;
            if (ab != null) n += (long)ab.Tree.Length * 44;
            foreach (ControllerTrack c in nd.Controllers) n += (long)c.Times.Length * 4 + (long)c.Values.Length * 4;
        }
        foreach (AnimDef a in m.Anims)
        {
            n += 128 + (long)a.Events.Length * 32;
            foreach (AnimNode an in a.Nodes)
            {
                n += 96;
                foreach (ControllerTrack c in an.Controllers) n += (long)c.Times.Length * 4 + (long)c.Values.Length * 4;
            }
        }
        return n;
    }

    // ---- the worker ----

    private static readonly object Gate = new();
    private static readonly Queue<Job> _queue = new();
    private static readonly Queue<Job> _done = new();
    private static readonly Dictionary<string, Job> _flight = new(StringComparer.Ordinal);
    /// <summary>One background parse thread for the process's life. Not started and stopped per area: the
    /// thread is blocked on <see cref="Gate"/> when idle (it costs a stack and nothing else), and a stop/start
    /// edge per area buys a thread create in exchange for a window where a parse cannot be queued. ponytail: a
    /// second worker if a profile ever shows the queue is the wait rather than the disk.</summary>
    private static Thread? _worker;

    /// <summary>One parse in flight. The token is whatever the source's main-thread half resolved: nothing in
    /// it may point into engine memory, because the worker reads it.</summary>
    private sealed class Job
    {
        public string Name = "";
        public object Token = null!;
        public ModelDef? Def;
        public string? Why;
        /// <summary>The source generation this job was queued under (see <see cref="_sourceGen"/>).</summary>
        public int Gen;
        /// <summary>True once a read-ahead queued it, so a later wait counts as a read-ahead HIT rather than a
        /// cold miss.</summary>
        public bool Queued;
        /// <summary>Set by the one <see cref="Publish"/> that files it: the waiter and the pump race.</summary>
        public bool Published;
        /// <summary>The worker is done with it. Guarded by <see cref="Gate"/> and announced with a pulse, so a
        /// waiter needs no per-job event to dispose (and none to leak).</summary>
        public bool Finished;
    }

    /// <summary>Locate on this thread (the half that may touch the engine), then queue. Returns the job already
    /// in flight for the same name rather than queueing a second read of the same file.</summary>
    private static Job? Start(string name, out string? why)
    {
        why = null;
        lock (Gate) if (_flight.TryGetValue(name, out Job? already)) { already.Queued = true; return already; }
        object? token;
        try { token = _source!.Locate(name, out why); }
        catch (Exception e) { token = null; why = e.Message; }
        if (token == null)
        {
            _absent++;
            why ??= "not found";
            Say("not-located", $"ModelCache: {name}: {why}");
            return null;
        }
        var j = new Job { Name = name, Token = token, Gen = _sourceGen };
        lock (Gate)
        {
            _flight[name] = j;
            _queue.Enqueue(j);
            if (_worker == null)
            {
                _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "cx-model" };
                _worker.Start();
            }
            Monitor.PulseAll(Gate);
        }
        return j;
    }

    /// <summary>The parse thread. Reads and parses through <see cref="IModelSource"/> and touches nothing
    /// else: no engine memory, no GL, no table.</summary>
    private static void WorkerLoop()
    {
        while (true)
        {
            Job j;
            lock (Gate)
            {
                while (_queue.Count == 0) Monitor.Wait(Gate);
                j = _queue.Dequeue();
            }
            IModelSource? src = _source;
            try
            {
                if (src == null) j.Why = "the model source was withdrawn";
                else j.Def = src.Parse(j.Token, out j.Why);
            }
            catch (Exception e) { j.Def = null; j.Why = e.Message; }
            Interlocked.Increment(ref _parsed);
            lock (Gate)
            {
                _done.Enqueue(j);
                j.Finished = true;
                Monitor.PulseAll(Gate);
            }
        }
    }

    // ---- reporting ----

    /// <summary>Resident models, references held and bytes, for the census and the self-checks.</summary>
    internal static int Resident => _table.Count;
    /// <summary>Parses the worker has finished but nothing has filed yet. The self-checks wait on it;
    /// nothing else reads it.</summary>
    internal static int Finished { get { lock (Gate) return _done.Count; } }
    internal static int RefsOf(ModelDef m) => _bySlot.TryGetValue(m, out Slot? s) ? s.Refs : 0;
    internal static int DepthOf(ModelDef m) => _bySlot.TryGetValue(m, out Slot? s) ? s.Depth : -1;
    internal static long Hits => _hits;
    internal static long Misses => _misses;
    internal static long Parsed => _parsed;
    internal static long Cycles => _cycles;
    internal static long MissingSupers => _missingSupers;
    internal static long OverReleases => _overRelease;
    internal static long Evictions => _evicted;
    internal static long Renamed => _renamed;
    internal static long StaleResources => _stale;
    internal static long StaleSources => _staleSource;
    internal static long LoaderDropped => _loaderDropped;
    internal static long Resynced => _resynced;
    /// <summary>Log a named refusal, each CAUSE up to <see cref="LogCapPerCause"/> times: a quiet hole is never
    /// the answer, a flood is not either, and one shared budget let the chattiest cause hide the rest.
    /// <para><paramref name="cause"/> is a FIXED string per call site, never the reason text: a reason that
    /// carries a model name or a path would make every model its own cause and the cap meaningless.</para></summary>
    private static void Say(string cause, string line)
    {
        _said.TryGetValue(cause, out int n);
        if (n >= LogCapPerCause) return;
        _said[cause] = n + 1;
        _log?.Always(line);
    }

    private static readonly Dictionary<string, int> _said = new(StringComparer.Ordinal);

    /// <summary>The period line's clause. Cold -- once per report.</summary>
    internal static string Stats(int frames)
    {
        double wait = _waitTicks * 1000.0 / Stopwatch.Frequency;
        string s = $"models resident {_table.Count} ({_bytes >> 20} MiB, {_idleBytes >> 20} idle) parsed {_parsed} "
                 + $"hits {_hits} misses {_misses} waitms {wait.ToString("F1", CultureInfo.InvariantCulture)} evicted {_evicted}";
        if (_prefetched + _offerHits != 0) s += $" offers {_prefetched + _offerHits} (parsed {_prefetched}, already cached {_offerHits}, waited-on {_prefetchHits})";
        if (_loaderDropped != 0) s += $" unasked {_loaderDropped}";
        if (_absent != 0) s += $" absent {_absent}";
        if (_renamed != 0) s += $" namediffer {_renamed}";
        if (_stale != 0) s += $" stale {_stale}";
        if (_staleSource != 0) s += $" stalesource {_staleSource}";
        if (_resynced != 0) s += $" resynced {_resynced}";
        if (_cycles != 0) s += $" cycles {_cycles}";
        if (_missingSupers != 0) s += $" nosuper {_missingSupers}";
        if (_overRelease != 0) s += $" overrelease {_overRelease}";
        if (_timeouts != 0) s += $" timeouts {_timeouts}";
        if (_nosource != 0) s += $" nosource {_nosource}";
        if (_misplaced != 0) s += $" misplaced {_misplaced}";
        s += " " + CensusClause();
        string v = VerifyClause?.Invoke() ?? "";
        return v.Length == 0 ? s : s + " " + v;
    }

    /// <summary>The engine-side comparison (<c>CX.Alloy.ModelVerify</c>), installed only in a session: the one
    /// place this stream reads engine memory, kept out of <c>Alloy/Mdl/</c> so the cache and the parser still
    /// build and run with no client at all. Null off-device, and then there is nothing to report.</summary>
    internal static Func<string>? VerifyClause;
    /// <inheritdoc cref="VerifyClause"/>
    internal static Action? RunVerify;

    /// <summary>The area edge: report the census, then let the resident set back down to its budget. Nothing
    /// here RELEASES a reference -- an object's own destroy does that (ADR 0028 section 3) -- so a model still
    /// held after the edge is a counted number on the line, not something this silently frees. The read-ahead
    /// references ARE dropped: the area that offered them is over.</summary>
    internal static void AreaEdge()
    {
        for (int i = 0; i < _table.Count && _loaders != 0; i++)
            if (_table[i].Loader) DropLoaderRef(_table[i]);
        int held = 0;
        for (int i = 0; i < _table.Count; i++) if (_table[i].Refs > 0) held++;
        Trim();
        RunVerify?.Invoke();
        _log?.Info($"[models] resident={_table.Count} held={held} parsed={_parsed} hits={_hits} misses={_misses} "
                 + $"waitms={(_waitTicks * 1000.0 / Stopwatch.Frequency).ToString("F1", CultureInfo.InvariantCulture)} "
                 + $"evicted={_evicted} {CensusClause()}");
    }

    /// <summary>The resident-set budget in bytes. <see cref="BudgetOverride"/> is for the self-checks ONLY:
    /// a check may not <c>Set</c> a knob, because the knob surface persists a set to the user's real
    /// <c>cx.cfg</c>.</summary>
    private static long BudgetBytes => BudgetOverride ?? ((long)ModelKnobs.Budget.V << 20);
    internal static long? BudgetOverride;

    /// <summary>Reset every counter. The self-checks run against a clean slate; nothing else calls it.</summary>
    internal static void ResetCounters()
    {
        _hits = _misses = _parsed = _waitTicks = _evicted = _absent = _cycles = _missingSupers = 0;
        _overRelease = _timeouts = _nosource = _prefetched = _prefetchHits = _renamed = _stale = _offerHits = 0;
        _staleSource = _loaderDropped = _misplaced = _resynced = 0;
        RestoreResolveCounters(0, 0);
        _said.Clear();
        _drip = 0;
    }
}
