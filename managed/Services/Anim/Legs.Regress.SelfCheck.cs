using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>Hands the pose self-check's regressions slot to <see cref="LegsRegress"/>.</summary>
internal static partial class Legs
{
    static partial void Regressions(ExtenderLog log) => LegsRegress.Run(log);
}

/// <summary>The leg solve's approved examples that already held when they were written, kept as regressions on the
/// synthetic rig (<see cref="LegRig"/>). Oracles: the analytic synthetic walk, hand-worked geometry, and the frame with
/// the corrections absent.</summary>
internal static class LegsRegress
{
    /// <summary>Runs every regression; each logs a FAIL line naming its example when it no longer holds.</summary>
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "Legs.Regress.SelfCheck");
        E3(check);
        E5(check);
        E8(check);
        log.Info(check.Ok ? "Legs.Regress.SelfCheck: passed" : "Legs.Regress.SelfCheck: FAILED");
    }

    /// <summary>World units an ankle or the pelvis may stray from the animation and still be drawn as animated.</summary>
    private const float AsAnimated = 1e-4f;

    // Given a character running on flat ground at exactly the pace its run animation was made for
    // When it runs
    // Then its hips and knees are drawn as with Procedural animations off
    /// <summary>The synthetic run at its own pace for 4 s: every frame is the frame with the corrections absent, within a
    /// tenth of a millimetre. Oracle: an unwarped walk on flat ground needs no correction.</summary>
    private static void E3(Checker check)
    {
        var r = new LegRig();
        r.Knobs.Diagonal = r.Knobs.Pivot = false;
        int off = 0, frames = 0;
        while (r.Time < 4f)
        {
            r.Run(1f);
            frames++;
            if (!r.AsOff(AsAnimated)) off++;
        }
        check.Expect(frames > 200 && off == 0, $"E3 a run at its own pace is drawn as animated: {off} of {frames} frames corrected");
    }

    // Given a character standing with one foot on a step 20 cm higher than the other
    // When it stands
    // Then the higher foot rests on the step with its knee bent, and the other leg is straight
    /// <summary>A standing body, every sample under its right sole 0.20 up and its left on the floor: the pelvis is not
    /// lowered, the right ankle is drawn 0.20 up within a centimetre with its leg shorter by over 5 cm, and the left leg
    /// keeps its animated length. Oracle: the hand-placed step.</summary>
    private static void E5(Checker check)
    {
        var r = new LegRig();
        for (int n = 0; n < 60; n++) r.StandStill(LegRig.Flat(0.2f), LegRig.Flat(0f));
        float rise = r.Drawn(true).Z - r.AnimR.Z, bendR = r.Shortened(true), bendL = r.Shortened(false);
        check.Expect(r.P.Body > -LegRig.Still && MathF.Abs(rise - 0.2f) < 0.01f && bendR > 0.05f && MathF.Abs(bendL) < LegRig.Still,
                     $"E5 a foot on a 20 cm step rests on it with its knee bent and the other leg straight: pelvis {r.P.Body:0.0000}, right foot up {rise:0.0000}, legs shortened {bendR:0.0000} right and {bendL:0.0000} left");
    }

    // Given a character running straight ahead
    // When it runs
    // Then its hips face forward and its legs are drawn as with Procedural animations off
    /// <summary>The synthetic run straight ahead at its own pace with the diagonal turn and the pivot on: the hips never
    /// turn off the facing and every frame is the frame with the corrections absent. Oracle: a straight run has no
    /// diagonal and no change of heading to turn the hips by.</summary>
    private static void E8(Checker check)
    {
        var r = new LegRig();
        int off = 0, frames = 0;
        float turn = 0f;
        while (r.Time < 4f)
        {
            r.Run(1f);
            frames++;
            turn = MathF.Max(turn, MathF.Abs(r.P.Turn));
            if (!r.AsOff(AsAnimated)) off++;
        }
        check.Expect(frames > 200 && turn < AsAnimated && off == 0,
                     $"E8 a straight run faces its hips forward and is drawn as animated: hips turned {turn:0.0000}, {off} of {frames} frames corrected");
    }
}
