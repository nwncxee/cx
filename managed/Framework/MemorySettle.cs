using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;

namespace CX.Framework;

/// <summary>Hands back what a load leaves behind once it goes quiet: the managed heap's garbage and malloc's free pages.
/// With latency=Batch a gen2 may not come for minutes, and free malloc pages stay resident until trimmed.
/// Linux only for the trim: it is glibc's; Windows' heap has no such call and only compacts.</summary>
internal static class MemorySettle
{
    private const long MiB = 1L << 20;
    /// <summary>Managed bytes allocated since the last compaction that make one worth its pause.</summary>
    private const long BurstBytes = 256 * MiB;
    /// <summary>A period allocating less than this is not loading anything.</summary>
    private const long QuietBytes = 32 * MiB;
    /// <summary>New free bytes in malloc that make a trim worth its pause.</summary>
    private const long SlackBytes = 128 * MiB;
    private const int PeriodSeconds = 10;
    /// <summary>Managed growth since the last collection that collects mid-load, the bound on a load's garbage: a load
    /// stalls frames, so this is watched off the frame.</summary>
    private const long LoadGrowthBytes = 768 * MiB;
    private const int WatchMs = 250;

    /// <summary>What a period's end asks for.</summary>
    internal readonly record struct Verdict(bool Compact, bool Trim);

    /// <summary>The books between periods: allocation totals at the last compaction and period end, and malloc's free
    /// bytes at the last trim, which mallinfo goes on counting after the pages themselves are gone.</summary>
    internal sealed class SettleState
    {
        public long Next, SettledAt, PeriodAt, TrimmedSlack;
        public Thread? Watcher;
    }

    private static readonly SettleState _state = new();

    [StructLayout(LayoutKind.Sequential)]
    private struct MallInfo2 { public nuint Arena, Ordblks, Smblks, Hblks, Hblkhd, Usmblks, Fsmblks, Uordblks, Fordblks, Keepcost; }
    [DllImport("libc.so.6", EntryPoint = "mallinfo2")] private static extern MallInfo2 MallInfo();
    [DllImport("libc.so.6", EntryPoint = "malloc_trim")] private static extern int MallocTrim(nuint pad);

    /// <summary>malloc's own figures: bytes in use, bytes it holds, and free bytes among them; zeros off glibc.</summary>
    internal static void Malloc(out long used, out long held, out long free)
    {
        used = held = free = 0;
        if (!OperatingSystem.IsLinux()) return;
        MallInfo2 m = MallInfo();
        used = (long)(m.Uordblks + m.Hblkhd);
        held = (long)(m.Arena + m.Hblkhd);
        free = (long)m.Fordblks;
    }

    /// <summary>One period's end: <paramref name="allocated"/> is the managed total so far, <paramref name="slack"/> the
    /// free bytes malloc holds. Nothing runs while the period is still allocating, so a load is not interrupted.</summary>
    internal static Verdict Step(SettleState s, long allocated, long slack)
    {
        long period = allocated - s.PeriodAt;
        s.PeriodAt = allocated;
        s.TrimmedSlack = Math.Min(s.TrimmedSlack, slack);
        if (period >= QuietBytes) return default;
        bool compact = allocated - s.SettledAt >= BurstBytes;
        bool trim = compact || slack - s.TrimmedSlack >= SlackBytes;
        if (compact) s.SettledAt = allocated;
        if (trim) s.TrimmedSlack = slack;
        return new Verdict(compact, trim);
    }

    /// <summary>The per-present check, from the main thread: a timestamp compare until a period ends.
    /// <paramref name="dropCaches"/> lets go of what a load kept for itself, just before the compaction.</summary>
    internal static void Frame(long now, ExtenderLog? log, Action dropCaches)
    {
        SettleState s = _state;
        s.Watcher ??= Worker.Start("cx-memwatch", () => Watch(log));
        if (now < s.Next) return;
        s.Next = now + PeriodSeconds * Stopwatch.Frequency;
        Malloc(out _, out _, out long slack);
        Verdict v = Step(s, GC.GetTotalAllocatedBytes(), slack);
        if (!v.Compact && !v.Trim) return;
        long t0 = Stopwatch.GetTimestamp(), before = Environment.WorkingSet;
        if (v.Compact)
        {
            dropCaches();
            Compact();
        }
        if (v.Trim && OperatingSystem.IsLinux()) MallocTrim(0);
        log?.Info($"MemorySettle: {(v.Compact ? "compacted and " : "")}trimmed, rss {before / MiB} -> {Environment.WorkingSet / MiB} MiB in {(Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency} ms");
    }

    private static void Compact()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>Pure: the managed heap has grown far enough past <paramref name="floor"/> to collect now.</summary>
    internal static bool LoadDue(long floor, long now) => now - floor >= LoadGrowthBytes;

    /// <summary>The bound on a load's peak: a load's decode buffers die fast, and latency=Batch leaves them for a gen2
    /// that may not come until the settle, a gigabyte or more later.</summary>
    private static void Watch(ExtenderLog? log)
    {
        long floor = GC.GetTotalMemory(false);
        for (;;)
        {
            BlockingWait.Sleep(WatchMs, "between looks at the managed heap's growth");
            long now = GC.GetTotalMemory(false);
            if (!LoadDue(floor, now)) { floor = Math.Min(floor, now); continue; }
            long t0 = Stopwatch.GetTimestamp();
            Compact();
            floor = GC.GetTotalMemory(false);
            log?.Info($"MemorySettle: mid-load collection, managed {now / MiB} -> {floor / MiB} MiB in {(Stopwatch.GetTimestamp() - t0) * 1000 / Stopwatch.Frequency} ms");
        }
    }

    /// <summary>Off-device: when a period asks for what. Oracle: hand-written allocation totals in MiB.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MemorySettle.SelfCheck");
        var s = new SettleState();
        Verdict At(long allocated, long slack) => Step(s, allocated * MiB, slack * MiB);
        check.Expect(At(2000, 0) == default, "a period still loading was interrupted");
        check.Expect(At(2003, 0) == new Verdict(true, true), "a load that went quiet was not settled");
        check.Expect(At(2006, 0) == default, "an idle period settled again");
        check.Expect(At(2009, 300) == new Verdict(false, true), "native memory freed with no managed load was not trimmed");
        check.Expect(At(2012, 300) == default, "free bytes mallinfo still counts after a trim trimmed again");
        check.Expect(At(2015, 100) == default, "malloc reusing its free bytes asked for a trim");
        check.Expect(At(2018, 240) == new Verdict(false, true), "free bytes returning after reuse were not trimmed");
        check.Expect(!LoadDue(1000 * MiB, 1767 * MiB), "767 MiB of growth collected mid-load");
        check.Expect(LoadDue(1000 * MiB, 1768 * MiB), "768 MiB of growth did not collect mid-load");
        check.Expect(!LoadDue(1000 * MiB, 900 * MiB), "a shrinking heap collected mid-load");
        log.Info(check.Ok ? "MemorySettle.SelfCheck: passed" : "MemorySettle.SelfCheck: FAILED");
    }
}
