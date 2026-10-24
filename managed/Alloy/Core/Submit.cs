using CX.Framework;

namespace CX.Alloy;

/// <summary>Per-period GL submission accounting: how many DRAW CALLS the frame makes, and how many commands
/// those calls carry, split by pass. The batch count on the [alloy] line counts the colour pass alone, where
/// the driver's CPU cost is the call count over EVERY pass of the frame -- so this is the number a submission
/// change is judged by.</summary>
internal static class Submit
{
    /// <summary>Which pass the calls counted right now belong to. <see cref="Stage.Other"/> is everything
    /// that has not claimed a bracket -- the GUI, the probe capture, the mirror, the debug overlays.</summary>
    internal enum Stage { Other, Prepass, Retest, PrepassFeat, Colour, Hosted, BlendDepth, Water, Caster }

    /// <summary>One label per <see cref="Stage"/>, and the table the count arrays are sized from: the labels
    /// are the artifact, so a stage added without one cannot silently share a slot.</summary>
    private static readonly string[] Labels =
        { "other", "prepass", "retest", "prepassfeat", "colour", "hosted", "blenddepth", "water", "caster" };

    private sealed class SubmitState
    {
        public int Stage;
        public readonly long[] Calls = new long[Labels.Length];
        public readonly long[] Cmds = new long[Labels.Length];
    }

    private static SubmitState _s = new();

    /// <summary>Count the calls that follow against <paramref name="stage"/>, and hand back what was current
    /// so a nested bracket can put it back.</summary>
    internal static int At(Stage stage)
    {
        int was = _s.Stage;
        _s.Stage = (int)stage;
        return was;
    }

    /// <summary>Restore what <see cref="At"/> returned.</summary>
    internal static void Back(int was) => _s.Stage = was;

    /// <summary>One GL draw call, carrying <paramref name="cmds"/> draw commands. Hot: two adds at a
    /// constant index, nothing allocated.</summary>
    internal static void Call(int cmds)
    {
        var s = _s;
        s.Calls[s.Stage]++;
        s.Cmds[s.Stage] += cmds;
    }

    /// <summary>...for a submission site that belongs to one stage wherever it is called from, so it needs no
    /// bracket of its own.</summary>
    internal static void CallAt(Stage stage, int cmds)
    {
        var s = _s;
        s.Calls[(int)stage]++;
        s.Cmds[(int)stage] += cmds;
    }

    internal static void ResetPeriod() => _s = new SubmitState();

    /// <summary>The period text: the frame's total call count first, because that is the one number the
    /// driver's CPU time tracks, then the per-stage split and the commands each stage's calls carried.</summary>
    internal static string Line(int frames)
    {
        var s = _s;
        if (frames <= 0) return "gl submission: no frames";
        long calls = 0, cmds = 0;
        for (int i = 0; i < Labels.Length; i++) { calls += s.Calls[i]; cmds += s.Cmds[i]; }
        var sb = new System.Text.StringBuilder();   // cold: the period line
        sb.Append("gl submission: ").Append(((float)calls / frames).ToString("F0"))
          .Append(" draw calls/frame carrying ").Append(((float)cmds / frames).ToString("F0")).Append(" commands (");
        bool first = true;
        for (int i = 0; i < Labels.Length; i++)
        {
            if (s.Calls[i] == 0) continue;
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(Labels[i]).Append(' ').Append(((float)s.Calls[i] / frames).ToString("F0"))
              .Append('/').Append(((float)s.Cmds[i] / frames).ToString("F0"));
        }
        if (first) sb.Append("nothing submitted");
        return sb.Append(')').ToString();
    }

    /// <summary>Off-device: a call lands on the stage that was current, the split sums to the total, and a
    /// restored bracket counts where it did before. Oracle: hand-counted calls against a hand-written
    /// expected line.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "Submit.SelfCheck");
        check.Expect(Labels.Length == (int)Stage.Caster + 1, "every stage has a label");
        SubmitState keep = _s;
        _s = new SubmitState();
        int was = At(Stage.Colour);
        Call(7); Call(3);
        int inner = At(Stage.Caster);
        Call(100);
        Back(inner);
        Call(1);
        Back(was);
        Call(5);                                 // lands on Other, which At(Colour) displaced
        string line = Line(1);
        check.Expect(line == "gl submission: 5 draw calls/frame carrying 116 commands "
                           + "(other 1/5, colour 3/11, caster 1/100)",
                     $"the split is per stage and sums to the total (got \"{line}\")");
        _s = new SubmitState();
        check.Expect(Line(4) == "gl submission: 0 draw calls/frame carrying 0 commands (nothing submitted)",
                     "a period that submitted nothing says so rather than printing an empty list");
        check.Expect(Line(0) == "gl submission: no frames", "...and a period of no frames divides by nothing");
        _s = keep;
        log.Info(check.Ok ? "Submit.SelfCheck: passed" : "Submit.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
