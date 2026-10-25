using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Every pipeline the area's content materials name (<see cref="ContentPipelines"/>), compiled and drawn
/// once while the area loads: the compile goes to the driver's threads as the pair is named, and the one draw is
/// what finishes a program the driver leaves unfinished until its first use. Render thread, inside the bracket.</summary>
internal static class ContentPrewarm
{
    /// <summary>Draws one frame may spend warming. The compiles are the load's cost either way; this keeps the
    /// draws off a single frame, since each one finishes a program.</summary>
    private const int DrawsPerFrame = 8;

    /// <summary>The area's prewarm: the keys drawn, the ones still owed, and what it cost.</summary>
    private sealed class PrewarmState
    {
        internal readonly HashSet<PipelineKey> Seen = new();
        internal readonly List<PipelineKey> Queue = new();
        internal int Serial = -1;
        /// <summary>Pairs the last walk found unbuilt: their twins are not knowable, so the walk repeats.</summary>
        internal int Unsettled = 1;
        internal int Pairs, Warmed;
        internal double Ms;
        internal bool Said;
    }

    private static PrewarmState _st = new();

    /// <summary>Walk the area's content recipes for keys nothing has warmed, then warm up to this frame's share.
    /// Cheap on a settled area: the walk runs only while a pair is still building or the content set moved.</summary>
    internal static void Pump(ExtenderLog? log)
    {
        PrewarmState s = _st;
        int serial = DrawRecipe.ContentSerial;
        if (s.Unsettled > 0 || serial != s.Serial)
        {
            s.Serial = serial;
            s.Pairs = ContentPipelines.Enumerate(s.Seen, s.Queue, out s.Unsettled);
        }
        if (s.Queue.Count == 0)
        {
            if (!s.Said && s.Unsettled == 0 && s.Warmed > 0)
            {
                s.Said = true;
                log?.Always($"ContentPrewarm: {s.Warmed} content pipeline(s) over {s.Pairs} recipe(s) compiled and "
                          + $"drawn once ({s.Ms:F0} ms of draws) -- whatever the driver defers to a first draw is "
                          + $"spent here rather than at the frame a part first appears. {Inventory(s)}");
            }
            return;
        }
        long t0 = Stopwatch.GetTimestamp();
        int i = 0, drawn = 0;
        while (i < s.Queue.Count && drawn < DrawsPerFrame)
        {
            if (!Warm(s.Queue[i])) { i++; continue; }
            s.Queue.RemoveAt(i);
            s.Warmed++;
            drawn++;
        }
        s.Ms += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>The area's key set by variant: the inventory a pipeline-per-key backend would build.</summary>
    private static string Inventory(PrewarmState s)
    {
        Span<int> by = stackalloc int[5];
        foreach (PipelineKey k in s.Seen) by[(int)k.Variant]++;
        return $"The area's pipelines: {by[0]} forward, {by[1]} deferred, {by[2]} depth, "
             + $"{by[3] + by[4]} caster, over {s.Seen.Count} key(s)";
    }

    /// <summary>Draw one key's program once under its own state (<see cref="ProgramWarm"/>). False leaves the key
    /// queued: its vertex format has no arena VAO yet.</summary>
    private static bool Warm(in PipelineKey k)
    {
        uint prog = ContentPrograms.ProgramOf(k.Feat, k.Variant);
        if (prog == 0) return true;   // the pair carries no such variant: there is nothing to warm
        uint vao = MeshArena.Vao(k.Format);
        if (vao == 0) return false;
        ProgramWarm.Draw(prog, vao, k.Depth, k.Raster, k.Blend, k.Coverage,
                         Issue.WashOffsetFactor, Issue.WashOffsetUnits);
        return true;
    }

    /// <summary>The world went away: the next area's recipes name their own keys, and a pipeline warmed for the
    /// old area is warm in the driver, not in this table.</summary>
    internal static void AreaEdge() => _st = new PrewarmState();

    /// <summary>The context took every program name with it, so nothing is warm any more.</summary>
    internal static void ContextLost() => _st = new PrewarmState();

    /// <summary>The period line's clause: pipelines warmed, owed, and pairs still building.</summary>
    internal static string Note()
    {
        PrewarmState s = _st;
        if (s.Warmed == 0 && s.Queue.Count == 0) return "";
        return $"prewarm: {s.Warmed} pipeline(s) warm, {s.Queue.Count} owed"
             + (s.Unsettled == 0 ? "" : $", {s.Unsettled} recipe(s) still building") + "; ";
    }
}
