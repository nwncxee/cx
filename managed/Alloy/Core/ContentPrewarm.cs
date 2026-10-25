using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>Every pipeline the area's content materials name (<see cref="ContentPipelines"/>), compiled as the
/// pair is named and drawn once before a part needs it, which is what finishes a program the driver leaves
/// unfinished until its first use. Render thread, in the bracket, at the head of each frame Alloy draws: under
/// the lightmap hold behind the load screen, else on the first played frames, ahead of their batch loop.</summary>
internal static class ContentPrewarm
{
    /// <summary>Draws one frame may spend warming. The compiles are the load's cost either way; this keeps the
    /// draws off a single frame, since each one finishes a program.</summary>
    private const int DrawsPerFrame = 8;

    /// <summary>What one key's turn did: drawn; nothing to draw (the pair carries no such variant); drawn and
    /// refused by the driver; or not yet drawable, which leaves it queued.</summary>
    private enum Turn { Warmed, Dropped, Failed, Retry }

    /// <summary>The area's prewarm: the keys drawn, the ones still owed, and what it cost.</summary>
    private sealed class PrewarmState
    {
        internal readonly HashSet<PipelineKey> Seen = new();
        internal readonly List<PipelineKey> Queue = new();
        internal int Serial = -1;
        /// <summary>Recipes the last walk could not key: their pair has not built, or their mesh has not landed.
        /// The walk repeats while any remains, since neither fact is knowable here.</summary>
        internal int Unsettled = 1;
        internal int Pairs, Warmed, Dropped, Errors;
        internal double Ms;
        internal bool Said;
    }

    private static PrewarmState _st = new();

    /// <summary>Walk the area's content recipes for keys nothing has warmed, then warm up to this frame's share,
    /// with the engine-named block bound first (it carries the noise handle every content program declares).
    /// Cheap on a settled area: the walk runs only while a recipe is unkeyed or the content set moved.</summary>
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
            Say(s, log);
            return;
        }
        // ORDER: the frame's own bind of this block is later than the prewarm, and it carries the noise handle
        AlloyRenderer.BindEngineBlock();
        long t0 = Stopwatch.GetTimestamp();
        int i = 0, drawn = 0;
        while (i < s.Queue.Count && drawn < DrawsPerFrame)
        {
            Turn t = Warm(s.Queue[i], s, log);
            if (t == Turn.Retry) { i++; continue; }
            s.Queue.RemoveAt(i);
            if (t == Turn.Dropped) { s.Dropped++; continue; }
            if (t == Turn.Warmed) s.Warmed++;
            drawn++;
        }
        s.Ms += Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
    }

    /// <summary>The one line the area's prewarm says, once its queue is empty and every recipe is keyed.</summary>
    private static void Say(PrewarmState s, ExtenderLog? log)
    {
        if (s.Said || s.Unsettled != 0 || s.Warmed + s.Dropped + s.Errors == 0) return;
        s.Said = true;
        log?.Always($"ContentPrewarm: {s.Warmed} content pipeline(s) over {s.Pairs} recipe(s) compiled and drawn "
                  + $"once ({s.Ms:F0} ms of draws) -- whatever the driver defers to a first draw is spent here "
                  + $"rather than at the frame a part first appears. {Inventory(s)}"
                  + (s.Dropped == 0 ? "" : $"; {s.Dropped} key(s) had no program to warm")
                  + (s.Errors == 0 ? "" : $". WARN {s.Errors} warm draw(s) raised a GL error -- those programs "
                                        + "still finish at the frame they are first drawn in"));
    }

    /// <summary>The area's key set by variant: the inventory a pipeline-per-key backend would build.</summary>
    private static string Inventory(PrewarmState s)
    {
        Span<int> by = stackalloc int[ContentPipelines.Variants];
        foreach (PipelineKey k in s.Seen) by[(int)k.Variant]++;
        return $"The area's pipelines: {by[(int)ContentVariant.Forward]} forward, "
             + $"{by[(int)ContentVariant.Deferred]} deferred, {by[(int)ContentVariant.Depth]} depth, "
             + $"{by[(int)ContentVariant.Caster] + by[(int)ContentVariant.CasterLayer]} caster, "
             + $"over {s.Seen.Count} key(s)";
    }

    /// <summary>Draw one key's program once under its own state (<see cref="ProgramWarm"/>), counting a GL error
    /// the draw raised. <see cref="Turn.Retry"/> while the key's vertex format has no arena VAO yet.</summary>
    private static Turn Warm(in PipelineKey k, PrewarmState s, ExtenderLog? log)
    {
        uint prog = ContentPrograms.ProgramOf(k.Feat, k.Variant);
        if (prog == 0) return Turn.Dropped;
        uint vao = MeshArena.Vao(k.Format);
        if (vao == 0) return Turn.Retry;
        int errors = ProgramWarm.Draw(prog, vao, k.Depth, k.Raster, k.Blend, k.Coverage,
                                      Issue.WashOffsetFactor, Issue.WashOffsetUnits);
        if (errors == 0) return Turn.Warmed;
        s.Errors++;
        if (s.Errors == 1)
            log?.Always($"ContentPrewarm: WARN the warm draw of content pair {k.Feat}'s {k.Variant} program raised "
                      + $"{errors} GL error(s) -- it is NOT warm, so it finishes at the frame it is first drawn in; "
                      + "later refusals are only counted");
        return Turn.Failed;
    }

    /// <summary>The world went away: the next area's recipes name their own keys, and a pipeline warmed for the
    /// old area is warm in the driver, not in this table.</summary>
    internal static void AreaEdge() => _st = new PrewarmState();

    /// <summary>The context took every program name with it, so nothing is warm any more.</summary>
    internal static void ContextLost() => _st = new PrewarmState();

    /// <summary>The period line's clause: pipelines warmed, owed, and recipes not yet keyed.</summary>
    internal static string Note()
    {
        PrewarmState s = _st;
        if (s.Warmed == 0 && s.Queue.Count == 0) return "";
        return $"prewarm: {s.Warmed} pipeline(s) warm, {s.Queue.Count} owed"
             + (s.Unsettled == 0 ? "" : $", {s.Unsettled} recipe(s) unkeyed")
             + (s.Errors == 0 ? "" : $", {s.Errors} REFUSED") + "; ";
    }
}
