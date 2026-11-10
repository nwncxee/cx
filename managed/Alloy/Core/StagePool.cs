using System;
using System.Diagnostics;
using System.Threading;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The stage's worker pool: N persistent threads plus the main thread, each running one static chunk
/// of <see cref="AlloyRenderer.StageRange"/>. A woken worker SPINS on the job generation for up to
/// <see cref="SpinUs"/> before it blocks again, because wake latency can cost as much as the work. Invariant:
/// nothing writes the texture index, the handle hash or the ownership tables while a stage runs.</summary>
internal static class StagePool
{
    /// <summary>Chunks including the main thread's; the knob's ceiling is one less.</summary>
    internal const int MaxChunks = 8;
    /// <summary>How long a worker spins for the job after a pre-wake: the routing walk it hides is ~1ms.</summary>
    private const long SpinUs = 4000;
    /// <summary>...and after finishing a job. The next one is a frame away, so the worker blocks almost at
    /// once: spinning here burned a core per worker for the rest of every frame (six workers read as
    /// three busy cores on the client with the GPU at 30%), and the driver thread wants those cores.</summary>
    private const long SpinAfterJobUs = 100;
    /// <summary>A join that runs this long is a lost worker, not a slow one; the frame re-stages serially.</summary>
    private const long JoinLimitMs = 500;
    private static readonly Thread?[] _threads = new Thread?[MaxChunks];
    private static readonly SemaphoreSlim[] _go = new SemaphoreSlim[MaxChunks];
    /// <summary>1 while the worker spins on the generation, 0 once it is committed to blocking. A release only
    /// goes to a worker at 0: releasing a spinner leaves a stale count, so it wakes straight back into a full
    /// <see cref="SpinUs"/> window with no job. Dekker pair with the publish -- the worker stores 0 then
    /// re-reads the generation, Run stores the generation then reads this, both with full fences.</summary>
    private static readonly int[] _spinning = new int[MaxChunks];
    private static int _pending, _chunks, _gen;
    /// <summary>The generation <see cref="_pending"/> is counting down for, so a straggler from an abandoned run
    /// can tell that its completion is no longer wanted.</summary>
    private static int _pendingGen;
    private static Exception? _fault;
    /// <summary>The first log handed to a run, kept for the paths that have no caller to borrow one from.</summary>
    private static ExtenderLog? _log;
    private static bool _faultLogged, _rearmRefused;
    private static Action<int, int> _job = AlloyRenderer.StageRange;
    /// <summary>When a chunk entered and left its job body, which is how <see cref="Busy"/> tells a worker
    /// still inside one from a worker that has come back.</summary>
    private static readonly long[] _tStart = new long[MaxChunks], _tEnd = new long[MaxChunks];
    /// <summary>The generation a worker starts from, captured by its CREATOR before Start: a thread that reads
    /// it itself can come up after Run has already published the next job and mistake it for seen.</summary>
    private static readonly int[] _seenAtStart = new int[MaxChunks];

    /// <summary>Clear a fault so the knob can bring the pool back; the next Run starts fresh. A worker that
    /// timed out may still be running its old chunk, so a generation it missed is simply the one it skips.</summary>
    internal static void Rearm()
    {
        if (_fault == null) return;
        // ...but never while a worker is still inside a job body. Clearing the fault lets the next run publish,
        // and zeroing the count lets its join finish early -- both while that thread goes on writing the
        // previous job's arrays. A pool in this state stays down, loudly, because that is the honest answer:
        // the worker is wedged, not slow.
        if (Busy())
        {
            if (!_rearmRefused)
            {
                _rearmRefused = true;
                _log?.Always("StagePool: refusing to re-arm -- a worker is still inside a job body from the run "
                           + "that timed out, and letting a new one publish would race it. Threading stays down "
                           + "for this session.");
            }
            return;
        }
        _fault = null; _faultLogged = false; _rearmRefused = false;
        // The stage job again: the self-check swaps its own in, and nothing has published a generation since,
        // so no worker is inside a call.
        _job = AlloyRenderer.StageRange;
        Volatile.Write(ref _pending, 0);
    }

    /// <summary>Is any worker still inside a job body? A chunk stamps <see cref="_tStart"/> before the call and
    /// <see cref="_tEnd"/> after it, so an end older than its start is a worker that has not come back. Both
    /// are zero until a worker's first chunk, which reads as not busy.</summary>
    private static bool Busy()
    {
        for (int w = 1; w < MaxChunks; w++)
            if (Volatile.Read(ref _tEnd[w]) < Volatile.Read(ref _tStart[w])) return true;
        return false;
    }

    /// <summary>Wake the workers ahead of the job so the routing walk hides their wake latency. Cheap when
    /// they are already spinning: the semaphore is capped at one and never over-released.</summary>
    internal static void Prewake(int chunks)
    {
        if (_fault != null) return;
        for (int w = 1; w < chunks; w++)
        {
            Ensure(w);
            if (Volatile.Read(ref _spinning[w]) == 0 && _go[w].CurrentCount == 0) _go[w].Release();
        }
    }

    /// <summary>Run the job over <paramref name="chunks"/> chunks, chunk 0 on the caller. False after a
    /// worker fault: the caller must redo the work serially, and the pool stays down.</summary>
    internal static bool Run(int chunks, ExtenderLog? log, int testGapMs = 0)
    {
        _log ??= log;
        if (_fault != null) return false;
        // never more chunks than cores: on a one-core box (CI) the spinners would only starve each other
        chunks = Math.Clamp(chunks, 1, Math.Max(1, Environment.ProcessorCount));
        if (chunks == 1) { try { _job(0, 1); } catch (Exception e) { _fault = e; } return _fault == null; }
        Prewake(chunks);
        if (testGapMs > 0) BlockingWait.Sleep(testGapMs, "self-check only: outlive the workers' spin window");
        _chunks = chunks;
        // Both BEFORE the publish. A spinning worker can see the new generation, run a tiny chunk and reach its
        // decrement in less time than a store takes to land, so the generation the count belongs to has to be
        // there already or that worker would decline to report and the join would sit out its whole deadline.
        Volatile.Write(ref _pendingGen, Volatile.Read(ref _gen) + 1);
        Volatile.Write(ref _pending, chunks - 1);
        Interlocked.Increment(ref _gen);   // the job is published; spinning workers see it now
        // ...and a worker whose spin window ran out before this (a slow box, a long routing walk) is blocked
        // on its semaphore with nobody left to release it -- CI lost chunk 1 that way. Release past the
        // publish too; a still-spinning worker just takes one more window.
        for (int w = 1; w < chunks; w++) if (Volatile.Read(ref _spinning[w]) == 0 && _go[w].CurrentCount == 0) _go[w].Release();
        try { _job(0, chunks); }
        catch (Exception e) { _fault = e; }
        // no Sleep(1) in the join: SpinOnce() sleeps a millisecond after 20 iterations, which is the budget
        long deadline = Stopwatch.GetTimestamp() + JoinLimitMs * Stopwatch.Frequency / 1000;
        for (int spins = 0; Volatile.Read(ref _pending) > 0; spins++)
        {
            // yield past the first few spins: on an oversubscribed box the straggler needs this core
            if (spins < 64) Thread.SpinWait(32); else Thread.Yield();
            if (Stopwatch.GetTimestamp() > deadline) { _fault ??= new TimeoutException($"stage join past {JoinLimitMs}ms"); break; }
        }
        if (_fault == null) return true;
        if (!_faultLogged) { _faultLogged = true; log?.Info($"StagePool: worker fault, threading stood down -- {_fault}"); }
        return false;
    }

    private static void Ensure(int w)
    {
        if (_threads[w] != null) return;
        _go[w] = new SemaphoreSlim(0, 1);
        _seenAtStart[w] = Volatile.Read(ref _gen);
        var t = new Thread(Loop) { IsBackground = true, Name = $"cx-stage-{w}" };
        _threads[w] = t;
        t.Start(w);
    }

    private static void Loop(object? arg)
    {
        int w = (int)arg!;
        Affinity.ClearThis();   // created by the (possibly pinned) main thread; the workers must not share its core
        int seen = _seenAtStart[w];
        while (true)
        {
            _go[w].Wait();
            Interlocked.Exchange(ref _spinning[w], 1);
            long spinUntil = Stopwatch.GetTimestamp() + SpinUs * Stopwatch.Frequency / 1_000_000;
            int idle = 0;
            while (true)
            {
                int g = Volatile.Read(ref _gen);
                if (g != seen)
                {
                    seen = g;
                    if (w < _chunks)
                    {
                        Volatile.Write(ref _tStart[w], Stopwatch.GetTimestamp());
                        try { _job(w, _chunks); }
                        catch (Exception e) { _fault = e; }   // an unhandled exception on a NativeAOT worker kills the process
                        Volatile.Write(ref _tEnd[w], Stopwatch.GetTimestamp());
                        // Only for the run this chunk was published for. A worker whose join already timed out
                        // comes back late, and an untagged decrement would land on a LATER run's count and let
                        // its join finish while this thread was still writing that job's arrays.
                        if (Volatile.Read(ref _pendingGen) == seen) Interlocked.Decrement(ref _pending);
                    }
                    spinUntil = Stopwatch.GetTimestamp() + SpinAfterJobUs * Stopwatch.Frequency / 1_000_000;
                }
                else if (Stopwatch.GetTimestamp() > spinUntil)
                {
                    Interlocked.Exchange(ref _spinning[w], 0);   // full fence, then the re-read: a job published meanwhile is ours
                    if (Volatile.Read(ref _gen) == seen) break;
                    Interlocked.Exchange(ref _spinning[w], 1);
                }
                else if ((++idle & 15) == 0) Thread.Yield();
                else Thread.SpinWait(64);
            }
        }
    }

    /// <summary>Four chunks sum disjoint ranges of 1..1000 into one array; the total is the serial sum.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        long[] part = new long[MaxChunks];
        var saved = _job;
        _job = (w, chunks) =>
        {
            int from = 1000 * w / chunks, to = 1000 * (w + 1) / chunks;
            long s = 0; for (int k = from; k < to; k++) s += k + 1;
            part[w] = s;
        };
        // three runs: the second finds the workers spinning; the third publishes 20ms after the pre-wake,
        // past the spin window, so every worker has blocked again and must be released past the publish
        bool ok = Run(4, log) && Run(4, log) && Run(4, log, testGapMs: 20);
        _job = saved;
        long total = 0; foreach (long v in part) total += v;
        ok &= total == 500500;
        log.Info(ok ? "StagePool.SelfCheck: passed (4 chunks, three runs)" : $"StagePool.SelfCheck: FAILED -- sum {total}, fault {_fault}");
        return ok;
    }
}
