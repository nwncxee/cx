using System;
using System.Threading;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// Texture streaming -- for putting the player in sooner, not for VRAM. The engine builds a texture the frame
/// after it is asked for: <c>AurTextureGet</c> appends to <c>RefreshTextures</c> and
/// <c>AurTextureBuildAndStoreAll</c> drains the whole queue at every <c>AurTextureMakeCurrent</c> (the top of
/// <c>Scene::Render</c>, every <c>UpdateScreen</c> tick), all of it ahead of the AreaLoaded send. Here it spreads.
/// </summary>
internal static unsafe class TexStream
{
    private static ExtenderLog? _log;
    private static AurTextureBuildAndStoreAllFn _origDrain;
    private static AreaLoadAreaFn* _loadAreaSlot;
    private static MaterialInitializeTexturesFn _origInitTextures;
    /// <summary>Queued entries a model material asked for (<see cref="OnInitTextures"/>): the only ones that may wait.
    /// Everything else -- the 2D GUI, fonts, portraits -- builds when the engine would. GUI panels are models too, so
    /// their textures land here as well; <see cref="OnBind"/> builds any the engine itself binds before it draws.</summary>
    private static readonly System.Collections.Generic.HashSet<IntPtr> _world = new();
    private static SendAreaLoadedFn* _sentSlot, _placedSlot;
    private static EngineList _queue;

    /// <summary>Budget per drain from the end of <c>LoadArea</c> until the player is placed. ponytail: a const; a knob
    /// if a rig wants its load frames faster or its textures sooner.</summary>
    private const int LoadingMs = 12;
    /// <summary>Budget per drain once the player is placed: textures still queued trickle in without a hitch.</summary>
    private const int PlacedMs = 4;
    /// <summary>Outside <see cref="LightmapBake"/>'s link (0): sees the engine's own send, held or not.</summary>
    private const int SentPriority = 100;
    /// <summary>Inside it: sees only the send that actually leaves, the one that places the player.</summary>
    private const int PlacedPriority = -10;
    /// <summary><c>LoadArea</c> is shared with <see cref="LightmapBake"/>, so this one chains, outermost: the
    /// load's timing then spans the whole call.</summary>
    private const int LoadAreaPriority = 50;

    /// <summary>Every hook took: the queue is ours to spread and the transitions ours to time.</summary>
    private static bool _armed;
    /// <summary>Streaming is on: every hook took.</summary>
    internal static bool On => _armed;

    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _queue = new EngineList(Native.Sym(SymbolMap.RefreshTextures));
        if (_queue.IsNull) { log.Always("TexStream: REFUSED -- RefreshTextures unresolved; the engine drains its texture queue whole and no [load] line is written"); return; }
        _origDrain = hooks.InstallAurTextureBuildAndStoreAll(&OnDrain);
        _loadAreaSlot = hooks.ChainAreaLoadArea(&OnLoadArea, LoadAreaPriority);
        _sentSlot = hooks.ChainSendAreaLoaded(&OnSent, SentPriority);
        _placedSlot = hooks.ChainSendAreaLoaded(&OnPlaced, PlacedPriority);
        _origInitTextures = hooks.InstallMaterialInitializeTextures(&OnInitTextures);
        if (_origDrain == null || _loadAreaSlot == null || _sentSlot == null || _placedSlot == null || _origInitTextures == null)
        {
            log.Always($"TexStream: REFUSED -- drain {(_origDrain != null ? "ok" : "MISSING")}, LoadArea {(_loadAreaSlot != null ? "ok" : "MISSING")}, "
                       + $"Material::InitializeTextures {(_origInitTextures != null ? "ok" : "MISSING")}, "
                       + $"AreaLoaded {(_sentSlot != null && _placedSlot != null ? "ok" : "MISSING")}; the engine drains its texture queue whole");
            return;
        }
        _armed = true;
        log.Info($"TexStream: watching the texture queue -- {PlacedMs} ms of builds a frame once placed; a [load] line per area transition");
    }

    // ---- the drain ----

    /// <summary>Entries set aside while the engine's drain runs on the one left in the queue.</summary>
    private const int HoldCap = 16384;
    private static readonly IntPtr[] _hold = new IntPtr[HoldCap];
    private static bool _overflowSaid;

    [UnmanagedCallersOnly]
    private static void OnDrain()
    {
        long t0 = Stopwatch.GetTimestamp();
        long r0 = FrameZones.Ticks(FrameZones.Zone.TexRead), d0 = FrameZones.Ticks(FrameZones.Zone.TexDecode),
             u0 = FrameZones.Ticks(FrameZones.Zone.Tex);
        int built;
        if (!On || _queue.Count == 0)
        {
            built = _t.Active ? Buildable(_queue) : 0;
            _origDrain();
        }
        else
        {
            long budget = BudgetMs * Stopwatch.Frequency / 1000;
            built = Drain(_queue, t0 + budget, &Now, &IsWorld, &BuildOne, &OrigDrain, _hold);
            if (_queue.Count == 0) _world.Clear();   // a texture freed while queued leaves its address here; none is queued now
            if (built < 0) { built = -built; if (!_overflowSaid) { _overflowSaid = true; _log?.Always($"TexStream: the texture queue passed {HoldCap} entries -- drained whole this once (the engine's own drain), streaming resumes below it"); } }
        }
        Account(Stopwatch.GetTimestamp() - t0, built,
                FrameZones.Ticks(FrameZones.Zone.TexRead) - r0,
                FrameZones.Ticks(FrameZones.Zone.TexDecode) - d0,
                FrameZones.Ticks(FrameZones.Zone.Tex) - u0);
    }

    /// <summary>What a drain may spend building world textures. Zero inside <c>LoadArea</c> (the engine suspends
    /// its own builds there anyway) and zero under the lightmap hold, which is itself a per-frame drain clock: a
    /// millisecond spent building there stretches the hold, so the work is paid at a worse rate, not saved.
    /// <see cref="LoadingMs"/> covers the segment to the AreaLoaded send, <see cref="PlacedMs"/> a placed player.</summary>
    private static int BudgetMs => _inLoadArea || LightmapBake.Held ? 0
                                 : _t.Active && _t.Placed == 0 ? LoadingMs : PlacedMs;

    private static long Now() => Stopwatch.GetTimestamp();
    private static void BuildOne(IntPtr basic) { _world.Remove(basic); EngineFn.AurTextureBuildAndStoreTexture(basic); }
    private static bool IsWorld(IntPtr basic) => _world.Contains(basic);
    private static void OrigDrain() => _origDrain();

    /// <summary>One drain under a deadline: a non-<paramref name="deferrable"/> entry is built at once, as the
    /// engine would; deferrable ones go newest first off the tail while the clock is short of
    /// <paramref name="deadline"/>. One entry is left for <paramref name="engineDrain"/>, which returns at once on
    /// an EMPTY queue -- its bookkeeping needs something; the oldest never starves. Negated: drained whole.</summary>
    internal static int Drain(EngineList q, long deadline, delegate*<long> clock, delegate*<IntPtr, bool> deferrable,
                              delegate*<IntPtr, void> build, delegate*<void> engineDrain, IntPtr[] hold)
    {
        int n0 = q.Count;
        if (n0 > hold.Length) { int all = Buildable(q); engineDrain(); return -all; }
        // the ones that may not wait, out of the queue in order; the deferrable ones close up behind them
        int w = 0, m = 0;
        for (int i = 0; i < n0; i++)
        {
            IntPtr e = q[i];
            if (deferrable(e)) q[w++] = e;
            else if (new CAurTextureBasic(e).FlagNoDrain == 0) hold[m++] = e;
        }
        q.Count = w;
        int built = 0;
        IntPtr last = m > 0 ? hold[m - 1] : IntPtr.Zero;
        for (int k = 0; k < m - 1; k++) { build(hold[k]); built++; }   // appends land on the tail
        while (q.Count >= (last == IntPtr.Zero ? 2 : 1) && clock() < deadline)
        {
            IntPtr e = q[q.Count - 1];
            q.Count--;
            if (new CAurTextureBasic(e).FlagNoDrain != 0) continue;
            build(e);   // may append: the tail read above stays right because it was removed first
            built++;
        }
        int n = q.Count;
        if (last == IntPtr.Zero && n == 0) return built;   // nothing left for the engine (every entry was a skip)
        // the one the engine's drain gets: the last may-not-wait entry, else the oldest; the rest aside, in order
        int h = 0;
        IntPtr one = last;
        for (int i = 0; i < n; i++)
            if (one == IntPtr.Zero && i == 0) one = q[0];
            else hold[h++] = q[i];
        if (new CAurTextureBasic(one).FlagNoDrain == 0) built++;
        q[0] = one;
        q.Count = 1;
        engineDrain();
        for (int i = 0; i < h; i++) q[i] = hold[i];
        q.Count = h;
        return built;
    }

    /// <summary>What the engine's drain would build of <paramref name="q"/> right now.</summary>
    private static int Buildable(EngineList q)
    {
        int n = 0;
        for (int i = 0; i < q.Count; i++) if (new CAurTextureBasic(q[i]).FlagNoDrain == 0) n++;
        return n;
    }

    /// <summary>A model material's texture requests: whatever they appended to the queue is world content, the only
    /// kind streaming may leave white for a while. Cold -- model load.</summary>
    [UnmanagedCallersOnly]
    private static void OnInitTextures(IntPtr mat, IntPtr t0, IntPtr t1, IntPtr t2, IntPtr t3)
    {
        int before = _armed ? _queue.Count : 0;
        _origInitTextures(mat, t0, t1, t2, t3);
        if (!_armed) return;
        for (int i = before; i < _queue.Count; i++) { _world.Add(_queue[i]); TexLoad.Offer(_queue[i]); }
    }

    // ---- the engine's own binds ----

    // ---- the transition ----

    /// <summary>One area transition's timeline: LoadArea entry, its return, the engine's AreaLoaded send, the send
    /// that left (player placed), and per segment the drain's builds and ticks.</summary>
    private struct Transition
    {
        public bool Active, Failed;
        public long LoadStart, LoadEnd, Sent, Placed;
        public int QueuedAtPlace;
        /// <summary>Inside <c>LoadArea</c>: <see cref="TexLoad.Offer"/>'s main-thread ticks, the engine's own
        /// <c>CExoResMan::Demand</c> ticks, and the ticks <c>cx-load</c> was busy beside it.</summary>
        public long Offers, Demands, Busy, DemMdl, DemOther, DemCalls;
    }
    private static Transition _t;
    /// <summary>Per segment of <see cref="_t"/> (<see cref="Segment"/>): the drain's ticks and builds.</summary>
    private static readonly long[] _ticks = new long[4];
    private static readonly int[] _builds = new int[4];
    /// <summary>The same drains split into the three halves of a build: file read, decode, GL upload. Sampled as
    /// <see cref="FrameZones"/> deltas across the drain, so what they miss is what <see cref="_ticks"/> holds and
    /// they do not -- the engine's own bookkeeping between builds.</summary>
    private static readonly long[] _read = new long[4], _decode = new long[4], _upload = new long[4];
    private static bool _inLoadArea;

    [UnmanagedCallersOnly]
    private static int OnLoadArea(IntPtr area, IntPtr camera, IntPtr loadscreen)
    {
        if (!_armed) return (*_loadAreaSlot)(area, camera, loadscreen);
        if (_t.Active) Report("a new area load began before the last drained");
        // Ordering: a session's first area drains before the first Alloy frame would swap the decoder slots.
        DdsReader.Tick();
        TexLoad.SwapRaster();
        _t = default;
        Array.Clear(_ticks); Array.Clear(_builds);
        Array.Clear(_read); Array.Clear(_decode); Array.Clear(_upload);
        ResSources.Reset();
        _t.Active = true;
        long offer0 = TexLoad.OfferTicks, busy0 = Interlocked.Read(ref TexLoad.BusyTicks), dem0 = CX.Services.Perf.EngineZonesService.DemandTicks,
             mdl0 = CX.Services.Perf.EngineZonesService.DemandMdlTicks, other0 = CX.Services.Perf.EngineZonesService.DemandOtherTicks, calls0 = CX.Services.Perf.EngineZonesService.DemandCalls;
        CX.Services.Perf.ModelZonesService.MarkLoadStart();
        ModelLoad.OnLoadArea(area);
        _t.LoadStart = Stopwatch.GetTimestamp();
        _inLoadArea = true;
        int ok = (*_loadAreaSlot)(area, camera, loadscreen);
        _inLoadArea = false;
        CX.Services.Perf.ModelZonesService.MarkLoadEnd();
        _t.LoadEnd = Stopwatch.GetTimestamp();
        _t.Offers = TexLoad.OfferTicks - offer0;
        _t.Busy = Interlocked.Read(ref TexLoad.BusyTicks) - busy0;
        _t.Demands = CX.Services.Perf.EngineZonesService.DemandTicks - dem0;
        _t.DemMdl = CX.Services.Perf.EngineZonesService.DemandMdlTicks - mdl0;
        _t.DemOther = CX.Services.Perf.EngineZonesService.DemandOtherTicks - other0;
        _t.DemCalls = CX.Services.Perf.EngineZonesService.DemandCalls - calls0;
        ModelLoad.OnLoadAreaEnd(area, ok != 0);   // every peek has run, so the area key is foldable
        if (ok == 0) { _t.Failed = true; Report("LoadArea failed"); }
        return ok;
    }

    [UnmanagedCallersOnly]
    private static void OnSent(IntPtr msg)
    {
        if (_t.Active && _t.Sent == 0) _t.Sent = Stopwatch.GetTimestamp();
        (*_sentSlot)(msg);
    }

    [UnmanagedCallersOnly]
    private static void OnPlaced(IntPtr msg)
    {
        if (_t.Active && _t.Placed == 0)
        {
            _t.Placed = Stopwatch.GetTimestamp();
            if (_t.Sent == 0) _t.Sent = _t.Placed;
            _t.QueuedAtPlace = _queue.Count;
        }
        (*_placedSlot)(msg);
    }

    /// <summary>An area transition is open: from <c>LoadArea</c> until its queue drains after placement.</summary>
    internal static bool InTransition => _t.Active;

    /// <summary>The segment a drain now belongs to: inside LoadArea, to the send, under the hold, after placement.</summary>
    private static int Segment => _inLoadArea ? 0 : _t.Sent == 0 ? 1 : _t.Placed == 0 ? 2 : 3;

    private static void Account(long ticks, int built, long read, long decode, long upload)
    {
        if (!_t.Active) return;
        int s = Segment;
        _ticks[s] += ticks; _builds[s] += built;
        _read[s] += read; _decode[s] += decode; _upload[s] += upload;
    }

    /// <summary>Once a frame at the frame anchor: close the transition, and write its <c>[load]</c> line, once
    /// the queue has drained after placement.</summary>
    internal static void Tick()
    {
        if (_t.Active && _t.Placed != 0 && _queue.Count == 0) Report(null);
    }

    private static void Report(string? cut)
    {
        var t = _t;
        _t.Active = false;
        double f = Stopwatch.Frequency, ms = f / 1000;
        long end = Stopwatch.GetTimestamp();
        string Seg(int s, string what, long from, long to)
            => $"{what} {(to > from ? (to - from) / f : 0):F2} s ({_builds[s]} builds, {_ticks[s] / ms:F0} ms"
             + (_builds[s] == 0 ? ")" : $": read {_read[s] / ms:F0} / decode {_decode[s] / ms:F0} / upload {_upload[s] / ms:F0} ms)");
        string models = CX.Services.Perf.ModelZonesService.LoadClause();
        string line = $"TexStream: [load] {Seg(0, "LoadArea", t.LoadStart, t.LoadEnd)} [offers {t.Offers / ms:F0} ms, engine demands {t.Demands / ms:F0} ms over {t.DemCalls} (models {t.DemMdl / ms:F0}, other {t.DemOther / ms:F0}), cx-load busy {t.Busy / ms:F0} ms{(models.Length > 0 ? ", " + models : "")}]";
        if (!t.Failed)
        {
            line += $" | to the send {Seg(1, "", t.LoadEnd, t.Sent == 0 ? end : t.Sent).TrimStart()}";
            line += $" | held {Seg(2, "", t.Sent, t.Placed == 0 ? end : t.Placed).TrimStart()}{LightmapBake.HeldClause()}";
            line += t.Placed == 0 ? " | never placed"
                  : $" | placed {(t.Placed - t.LoadStart) / f:F2} s after LoadArea began, {t.QueuedAtPlace} textures still queued, drained {Seg(3, "", t.Placed, end).TrimStart()} later";
        }
        line += $" | streaming {(On ? $"on, {PlacedMs} ms/frame (to the send {LoadingMs}, under the hold 0)" : "off")}{(cut != null ? $" -- cut short: {cut}" : "")}";
        string src = ResSources.Line();
        if (src.Length > 0) line += $" | {src}";
        string after = CX.Services.Perf.ModelZonesService.AfterClause();
        if (after.Length > 0) line += $" | after LoadArea {after}";
        _log?.Info(line);
    }

    private static readonly long _epoch = Stopwatch.GetTimestamp();
    /// <summary>Seconds since load, from 1 so that 0 can mean "never arrived": the clock the colour pass's
    /// <c>uNow</c> reads.</summary>
    internal static float Clock => 1f + (float)((double)(Stopwatch.GetTimestamp() - _epoch) / Stopwatch.Frequency);

    internal static string Note()
        => !_armed ? "" : $", texstream {(On ? $"{PlacedMs} ms" : "off")}: {_queue.Count} queued";

    // ---- self-check ----

    private static long _fakeClock;
    private static long FakeClock() => _fakeClock++;
    private static IntPtr* _fakeBuilt;
    private static int _fakeBuiltN;
    private static void FakeBuild(IntPtr e) => _fakeBuilt[_fakeBuiltN++] = e;
    private static EngineList _fakeQ;
    private static bool FakeWorld(IntPtr e) => *(byte*)e != 0;
    /// <summary>The engine's drain as decompiled: build every entry with the skip byte clear, newest first, count to 0.</summary>
    private static void FakeEngineDrain()
    {
        for (int i = _fakeQ.Count - 1; i >= 0; i--)
            if (new CAurTextureBasic(_fakeQ[i]).FlagNoDrain == 0) FakeBuild(_fakeQ[i]);
        _fakeQ.Count = 0;
    }

    /// <summary><see cref="Drain"/> over a stand-in queue of six: four world entries (one the engine would skip) and
    /// two GUI ones. A three-tick budget: the first GUI entry is built outright, the two newest buildable world ones
    /// in the budget, the last GUI entry by the engine's drain, and the oldest world entry goes back. Then a zero
    /// budget still builds one -- that oldest -- and empties the queue.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        const int n = 6, obj = 256;   // stand-in objects, past every CAurTextureBasic field read
        byte* texs = (byte*)NativeMemory.AllocZeroed(n * obj);
        IntPtr* items = stackalloc IntPtr[8];
        byte* head = stackalloc byte[16];
        IntPtr* built = stackalloc IntPtr[8];
        IntPtr T(int i)   // the queue array is rewritten in place, so compare against these
        {
            fixed (byte* p = new Span<byte>(texs, n * obj).Slice(i * obj, obj)) return (IntPtr)p;
        }
        for (int i = 0; i < n; i++) { items[i] = T(i); *(byte*)T(i) = (byte)(i == 1 || i == 5 ? 0 : 1); }   // byte 0 = world
        new CAurTextureBasic(T(3)).FlagNoDrain = 1;
        _fakeQ = new EngineList((IntPtr)head);
        _fakeQ.Items = (IntPtr)items;
        _fakeQ.Count = n;
        _fakeBuilt = built; _fakeBuiltN = 0; _fakeClock = 0;
        var hold = new IntPtr[8];
        int got = Drain(_fakeQ, 3, &FakeClock, &FakeWorld, &FakeBuild, &FakeEngineDrain, hold);
        bool ok = got == 4 && _fakeBuiltN == 4 && built[0] == T(1) && built[1] == T(4) && built[2] == T(2) && built[3] == T(5)
                  && _fakeQ.Count == 1 && _fakeQ[0] == T(0);
        _fakeBuiltN = 0;
        got = Drain(_fakeQ, 0, &FakeClock, &FakeWorld, &FakeBuild, &FakeEngineDrain, hold);
        ok &= got == 1 && _fakeBuiltN == 1 && built[0] == T(0) && _fakeQ.Count == 0;
        log.Info(ok ? "TexStream.SelfCheck: passed" : "TexStream.SelfCheck: FAILED");
        NativeMemory.Free(texs);
        _fakeQ = default;
        return ok;
    }
}
