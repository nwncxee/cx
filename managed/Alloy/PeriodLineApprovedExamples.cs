using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The period lines say what the frame did. Oracle: hand-written phrases and counts, read off the lines the
/// log actually receives.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Every line <paramref name="body"/> sends to <paramref name="log"/>, on a log gate held open.</summary>
    private static List<string> LinesFrom(ExtenderLog log, Action body)
    {
        var lines = new List<string>();
        bool gate = ExtenderLog.Enabled;
        Action<string>? tap = log.Tap;
        ExtenderLog.Enabled = true;
        log.Tap = lines.Add;
        try { body(); }
        finally { log.Tap = tap; ExtenderLog.Enabled = gate; }
        return lines;
    }

    private static string AllOf(List<string> lines) => string.Join("\n", lines);

    private static List<string> ShimPeriod(ExtenderLog log, int calls)
    {
        ExtenderLog? was = RspShim._log;
        RspShim._log = log;
        try { return LinesFrom(log, () => { for (int k = 0; k < calls; k++) RspShim.Period(); }); }
        finally { RspShim._log = was; }
    }

    internal static bool PeriodLineSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.PeriodLineApprovedExamples");

        // Given a client that has drawn 900 frames in a world
        // When the period ends
        // Then cx.log holds an [rspshim] line naming the engine draws swallowed per frame and the draws that passed
        {
            ShimPeriod(log, LogEvery);
            for (int k = 0; k < 2 * LogEvery; k++) RspShim.CountSwallowed();
            string line = AllOf(ShimPeriod(log, LogEvery));
            check.Expect(line.Contains("[rspshim]") && line.Contains("2 engine draws swallowed/frame") && line.Contains("PASSED 0"),
                         $"E1: the period's line names swallowed and passed (got \"{line}\")");
            check.Expect(ShimPeriod(log, LogEvery - 1).Count == 0, "E1: before 900 frames there is no [rspshim] line");
            ShimPeriod(log, 1);
        }

        // Given an engine draw that reaches the engine's own funnel on a frame the engine owns (a handed-back movie)
        // When the period ends
        // Then the [rspshim] line's passed count includes it
        {
            RspShim.CountPassed();
            string passed = AllOf(ShimPeriod(log, LogEvery));
            check.Expect(passed.Contains("PASSED 1"), $"E2: a draw that got past is counted (got \"{passed}\")");
            string none = AllOf(ShimPeriod(log, LogEvery));
            check.Expect(none.Contains("PASSED 0"), $"E2: a period with no movie reads passed 0 (got \"{none}\")");
        }

        // Given any client session
        // When the [alloy] period line prints
        // Then it has no gl submission clause, no mesh-sharing census and no other clause fed only by counters nothing writes
        {
            ExtenderLog? was = _log;
            int frames = Counters._frames;
            long written = Counters._recWritten;
            _log = log;
            Counters._frames = 900; Counters._recWritten = 1800;
            string text;
            try { text = AllOf(LinesFrom(log, PeriodReport)); }
            finally { _log = was; Counters._frames = frames; Counters._recWritten = written; }
            foreach (string gone in new[] { "gl submission", "mesh sharing", "shape repeats", "lit left to the engine", "enrolls=", "/0 parts owned" })
                check.Expect(!text.Contains(gone), $"E3: the [alloy] line still prints \"{gone}\"");
            check.Expect(text.Contains("records 2/frame written"), "E3: every clause with a live counter still prints the same number");
        }

        // Given a session with Alloy on Vulkan
        // When the player types /alloy
        // Then the reply says the session has no GL context, how many imports are redirected and how many engine textures and buffers are held
        {
            check.Expect(Renderer.Status().Contains("NullGl: "), "E4: the /alloy line does not say what the GL stubs hold");
            check.Expect(NullGl.Describe(true, true, 3, 4, 5)
                         == "NullGl: no GL context; 3 import(s) redirected, 4 engine texture(s) held, 5 engine buffer(s) in host memory",
                         "E4: the words for a session with no GL context");
            check.Expect(NullGl.Describe(true, false, 0, 0, 0) == "NullGl: the engine has its GL context",
                         "E4: a session Vulkan refused says the engine has its GL context");
        }

        log.Info(check.Ok ? "AlloyRenderer.PeriodLineApprovedExamples: passed" : "AlloyRenderer.PeriodLineApprovedExamples: FAILED -- see above");
        return check.Ok;
    }
}
