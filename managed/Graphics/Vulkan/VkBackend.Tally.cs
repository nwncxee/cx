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
        public int AfterLift;
        public bool UnderHold;
    }

    private TallyState _tally = new();

    /// <summary>The window after a loading screen lifts in which a material pipeline built on the frame means the
    /// prewarm missed it.</summary>
    private const double AfterLiftSeconds = 10;

    /// <summary>The loading hold is up: a pipeline built now is counted apart, as the hold's own work. Set every frame;
    /// the frame the hold lifts opens the window <see cref="AfterLiftSeconds"/> long, whose line is written as it closes.</summary>
    internal bool UnderHold
    {
        get => _tally.UnderHold;
        set
        {
            TallyState t = _tally;
            if (t.UnderHold && !value) { t.LiftedAt = Stopwatch.GetTimestamp(); t.AfterLift = 0; }
            t.UnderHold = value;
            if (t.LiftedAt != 0 && Stopwatch.GetElapsedTime(t.LiftedAt).TotalSeconds >= AfterLiftSeconds)
            {
                _log.Always($"VkBackend: [vkpipe] after the loading screen: {t.AfterLift} material pipeline{(t.AfterLift == 1 ? "" : "s")} "
                          + $"built on the frame in the first {AfterLiftSeconds:F0} s");
                t.LiftedAt = 0;
            }
        }
    }

    /// <summary>One pipeline the frame thread just built, from <paramref name="since"/>; <paramref name="material"/>
    /// for one the area's prewarm owns.</summary>
    private void Tallied(long since, bool material)
    {
        TallyState t = _tally;
        if (t.UnderHold) { t.Held++; return; }
        double ms = Stopwatch.GetElapsedTime(since).TotalMilliseconds;
        t.Frame++;
        t.FrameMs += ms;
        if (ms > t.WorstMs) t.WorstMs = ms;
        if (!material) return;
        t.Material++;
        if (t.LiftedAt != 0) t.AfterLift++;
    }

    /// <summary>The period's line: pipelines built on the frame, their milliseconds and the slowest, the material ones
    /// among them, and those built under the loading hold. Starts the next period.</summary>
    internal string PipelinePeriod()
    {
        TallyState t = _tally;
        string line = $"[vkpipe] {t.Frame} pipeline{(t.Frame == 1 ? "" : "s")} built on the frame in {t.FrameMs:F2} ms "
                    + $"(worst {t.WorstMs:F2} ms), {t.Material} of them a material's; {t.Held} under the loading hold";
        t.Frame = t.Material = t.Held = 0;
        t.FrameMs = t.WorstMs = 0;
        return line;
    }
}
