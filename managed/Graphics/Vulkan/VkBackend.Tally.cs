using System.Collections.Generic;
using System.Diagnostics;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Pipelines the frame thread built since the last period line, under the loading hold or past it, and
    /// the material pipelines past it in the first seconds after a hold lifted.</summary>
    private sealed class TallyState
    {
        public int Frame, Material, Held;
        public double FrameMs, WorstMs;
        /// <summary>The timestamp the last hold lifted at, 0 once its window's line is written; and the material
        /// pipelines built on the frame since.</summary>
        public long LiftedAt;
        public int AfterLift, OthersAfterLift;
        public bool UnderHold;
        /// <summary>The first material pipelines the window counted, by label: what the prewarm missed; and the first
        /// of the rest, which no prewarm builds yet.</summary>
        public readonly List<string> Missed = new(), Others = new();
    }

    private readonly TallyState _tally = new();

    /// <summary>The window after a loading screen lifts in which a material pipeline built on the frame means the
    /// prewarm missed it.</summary>
    private const double AfterLiftSeconds = 10;

    /// <summary>The loading hold is up: a pipeline built now is counted apart, as the hold's own work. Set every frame;
    /// the frame the hold lifts opens the window <see cref="AfterLiftSeconds"/> long.</summary>
    internal bool UnderHold
    {
        get => _tally.UnderHold;
        set
        {
            TallyState t = _tally;
            if (t.UnderHold && !value) { t.LiftedAt = Stopwatch.GetTimestamp(); t.AfterLift = t.OthersAfterLift = 0; t.Missed.Clear(); t.Others.Clear(); }
            t.UnderHold = value;
        }
    }

    /// <summary>The window after the last lift is still open.</summary>
    private bool InWindow(TallyState t) => t.LiftedAt != 0 && Stopwatch.GetElapsedTime(t.LiftedAt).TotalSeconds < AfterLiftSeconds;

    /// <summary>Labels of missed material pipelines the after-lift line names.</summary>
    private const int MissedNamed = 5;

    /// <summary>One pipeline the frame thread just built, from <paramref name="since"/>; <paramref name="material"/>
    /// for one the area's prewarm owns, named <paramref name="what"/>.</summary>
    private void Tallied(long since, bool material, string what = "")
    {
        TallyState t = _tally;
        if (t.UnderHold) { t.Held++; return; }
        double ms = Stopwatch.GetElapsedTime(since).TotalMilliseconds;
        t.Frame++;
        t.FrameMs += ms;
        if (ms > t.WorstMs) t.WorstMs = ms;
        if (!material)
        {
            if (!InWindow(t)) return;
            t.OthersAfterLift++;
            if (t.Others.Count < MissedNamed) t.Others.Add(what);
            return;
        }
        t.Material++;
        if (!InWindow(t)) return;
        t.AfterLift++;
        if (t.Missed.Count < MissedNamed) t.Missed.Add(what);
    }

    /// <summary>The period's line: pipelines built on the frame, their milliseconds and the slowest, the material ones
    /// among them, and those built under the loading hold; once the window after a lift has closed, its own line goes
    /// to the log first. Starts the next period.</summary>
    internal string PipelinePeriod()
    {
        TallyState t = _tally;
        if (t.LiftedAt != 0 && !InWindow(t))
        {
            _log.Always($"VkBackend: [vkpipe] after the loading screen: {t.AfterLift} material pipeline{(t.AfterLift == 1 ? "" : "s")} "
                      + $"built on the frame in the first {AfterLiftSeconds:F0} s"
                      + (t.Missed.Count > 0 ? $", the first {string.Join("; ", t.Missed)}" : "")
                      + $"; {t.OthersAfterLift} other pipeline{(t.OthersAfterLift == 1 ? "" : "s")}"
                      + (t.Others.Count > 0 ? $", the first {string.Join("; ", t.Others)}" : ""));
            t.LiftedAt = 0;
        }
        string line = $"[vkpipe] {t.Frame} pipeline{(t.Frame == 1 ? "" : "s")} built on the frame in {t.FrameMs:F2} ms "
                    + $"(worst {t.WorstMs:F2} ms), {t.Material} of them a material's; {t.Held} under the loading hold";
        t.Frame = t.Material = t.Held = 0;
        t.FrameMs = t.WorstMs = 0;
        return line;
    }
}
