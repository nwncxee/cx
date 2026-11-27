using System;
using CX.Framework;
using CX.Services.Camera;
using CX.Services.Dialog;
using CX.Ui;

namespace CX.Services.Interact;

/// <summary>The player's own settings outlive the Interact and key-row suites: each suite, run on a quiet log after the
/// player's values are in, leaves every setting the suites touch as the player saved it and schedules no cx.cfg write.
/// Oracle: the saved values written here, and the boot hold's write counters.</summary>
internal static class InteractSettingsKept
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "InteractSettingsKept.SelfCheck");
        check.Expect(KnobFile.Holding, "runs inside the boot hold, so the sets it makes and puts back reach no cx.cfg write");
        if (!KnobFile.Holding) { log.Info("InteractSettingsKept.SelfCheck: FAILED"); return false; }
        Knob[] knobs =
        {
            InteractKnobs.Key, InteractKnobs.LootKey, InteractKnobs.Loot, InteractKnobs.Sound,
            CameraModes.Chase.ActionKey, TabTarget.Enabled, DialogKnobs.Enabled,
        };
        string[] were = Array.ConvertAll(knobs, k => k.FormatStored());
        string[] saved = { "G", "H", "0", "gui_select", "[", Flip(were[5]), Flip(were[6]) };
        (string Name, Func<ExtenderLog, bool> Run)[] suites =
        {
            ("LootApprovedExamples", LootApprovedExamples.SelfCheck), ("LootEdges", LootEdges.SelfCheck),
            ("TransitionApprovedExamples", TransitionApprovedExamples.SelfCheck), ("TransitionEdges", TransitionEdges.SelfCheck),
            ("PickUpApprovedExamples", PickUpApprovedExamples.SelfCheck), ("PickUpEdges", PickUpEdges.SelfCheck),
            ("DoorApprovedExamples", DoorApprovedExamples.SelfCheck), ("DoorEdges", DoorEdges.SelfCheck),
            ("TalkApprovedExamples", TalkApprovedExamples.SelfCheck), ("TalkEdges", TalkEdges.SelfCheck),
            ("TargetApprovedExamples", TargetApprovedExamples.SelfCheck), ("TargetEdges", TargetEdges.SelfCheck),
            ("FeedbackApprovedExamples", FeedbackApprovedExamples.SelfCheck), ("FeedbackEdges", FeedbackEdges.SelfCheck),
            ("KeyRowApprovedExamples", KnobPanel.KeyRowApprovedExamples), ("KeyRowUnasked", KnobPanel.KeyRowUnasked),
        };
        var quiet = new ExtenderLog { Tap = _ => { } };
        int writes = KnobFile.Writes, fires = KnobFile.Fires;
        try
        {
            foreach (var (name, run) in suites)
            {
                for (int i = 0; i < knobs.Length; i++) knobs[i].Parse(saved[i]);
                check.Expect(run(quiet), $"{name} passes with the player's own settings in");
                for (int i = 0; i < knobs.Length; i++)
                    check.Expect(knobs[i].FormatStored() == saved[i],
                        $"{name} leaves the player's {knobs[i].Key} at {saved[i]}, not {knobs[i].FormatStored()}");
            }
        }
        finally
        {
            for (int i = 0; i < knobs.Length; i++) knobs[i].Parse(were[i]);
        }
        check.Expect(KnobFile.Writes == writes && KnobFile.Fires == fires,
            $"no cx.cfg write ran or was scheduled: writes {writes} to {KnobFile.Writes}, fires {fires} to {KnobFile.Fires}");
        log.Info(check.Ok ? "InteractSettingsKept.SelfCheck: passed" : "InteractSettingsKept.SelfCheck: FAILED");
        return check.Ok;
    }

    private static string Flip(string toggle) => toggle == "1" ? "0" : "1";
}
