using System;
using System.Diagnostics;
using CX.Framework;

namespace CX.Services.Perf;

/// <summary>
/// The frame-time recorder behind <c>general.frametimes</c> (off by default, and then it costs one knob read per bracket). It keeps the
/// last frames' present-to-present times and the CPU a frame spent on model views in two fixed rings, and every few seconds logs
/// their p50, p95, p99 and max. Nothing allocates per frame.
/// </summary>
public sealed class FrameProbe : IExtenderService
{
    /// <summary>Frames the rings hold; a window closes when they fill or <see cref="WindowSec"/> has passed.</summary>
    internal const int Cap = 1024;
    private const long WindowSec = 5;

    private sealed class ProbeState
    {
        public readonly long[] Frame = new long[Cap], Model = new long[Cap], Sort = new long[Cap];
        public int N;
        public long Sum, ModelNow;
        public ExtenderLog? Log;
    }

    private static ProbeState _s = new();

    public void Initialize(ExtenderLog log, HookService hooks) { _s.Log = log; SelfCheck(log); }

    /// <summary>Is the recorder on? The brackets below read the clock only then.</summary>
    internal static bool On => General.FrameTimes.V;

    /// <summary>Move a span: add the time since <paramref name="t"/> to this frame's model-view CPU, and restart it when
    /// <paramref name="running"/>, stop it otherwise. A span starts from 0.</summary>
    internal static void Lap(ref long t, bool running)
    {
        if (!On) { t = 0; return; }
        long now = Stopwatch.GetTimestamp();
        if (t != 0) _s.ModelNow += now - t;
        t = running ? now : 0;
    }

    /// <summary>One presented frame of <paramref name="dt"/> ticks, folded in; closes and logs the window when it is full or old.</summary>
    internal static void Frame(long dt)
    {
        ProbeState s = _s;
        if (!On) { s.N = 0; s.Sum = 0; s.ModelNow = 0; return; }
        s.Frame[s.N] = dt; s.Model[s.N] = s.ModelNow; s.N++;
        s.Sum += dt; s.ModelNow = 0;
        if (s.N < Cap && s.Sum < WindowSec * Stopwatch.Frequency) return;
        Report(s);
        s.N = 0; s.Sum = 0;
    }

    private static void Report(ProbeState s)
    {
        double tick = 1000.0 / Stopwatch.Frequency;
        Array.Copy(s.Frame, s.Sort, s.N); Array.Sort(s.Sort, 0, s.N);
        string f = Line(s.Sort, s.N, tick);
        Array.Copy(s.Model, s.Sort, s.N); Array.Sort(s.Sort, 0, s.N);
        string m = Line(s.Sort, s.N, tick);
        s.Log?.Always($"[frametimes] n {s.N} span {s.Sum * tick / 1000.0:F1}s frame ms: {f} | model cpu ms: {m}");
    }

    private static string Line(long[] sorted, int n, double tick) =>
        $"p50 {Nearest(sorted, n, 50) * tick:F3} p95 {Nearest(sorted, n, 95) * tick:F3} p99 {Nearest(sorted, n, 99) * tick:F3} max {Nearest(sorted, n, 100) * tick:F3}";

    /// <summary>The nearest-rank percentile of the first <paramref name="n"/> values of the ascending <paramref name="sorted"/>:
    /// the value at rank ceil(pct/100 * n), counting from 1. 0 when n is 0.</summary>
    internal static long Nearest(long[] sorted, int n, int pct)
    {
        if (n <= 0) return 0;
        int rank = (pct * n + 99) / 100;
        return sorted[Math.Clamp(rank, 1, n) - 1];
    }

    /// <summary>Oracle: ranks worked out by hand on twenty literals, p50 the 10th value, p95 the 19th, p99 and max the 20th, and
    /// on five literals, where p50 is the 3rd and p95 the 5th.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        long[] twenty = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 100 };
        check.Expect(Nearest(twenty, 20, 50) == 10, "p50 of twenty is the 10th value");
        check.Expect(Nearest(twenty, 20, 95) == 19, "p95 of twenty is the 19th value");
        check.Expect(Nearest(twenty, 20, 99) == 100, "p99 of twenty is the 20th value, the outlier");
        check.Expect(Nearest(twenty, 20, 100) == 100, "max of twenty is the 20th value");
        long[] five = { 2, 4, 6, 8, 1000 };
        check.Expect(Nearest(five, 5, 50) == 6, "p50 of five is the 3rd value");
        check.Expect(Nearest(five, 5, 95) == 1000, "p95 of five is the 5th value");
        check.Expect(Nearest(five, 3, 99) == 6, "only the first n values count");
        check.Expect(Nearest(five, 0, 50) == 0, "no values read as 0");
        log.Info(check.Ok ? "FrameProbe.SelfCheck: passed" : "FrameProbe.SelfCheck: FAILED");
    }
}
