using System;
using CX.Framework;

namespace CX.Services.Interact;

/// <summary>Edges of the nothing-qualifies feedback no approved example reaches, pressed through
/// <see cref="InteractInput.KeyDown"/> on a world double. Oracle: the resrefs and the note written here.</summary>
internal static class FeedbackEdges
{
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "FeedbackEdges.SelfCheck");
        try
        {
            {
                InteractWorldDouble.Install();
                string? note = InteractKnobs.Sound.Note?.Invoke();
                check.Expect(note == null, $"a sound the game has leaves the row without a note: '{note}'");
            }
            {
                var w = InteractWorldDouble.Install();
                InteractKnobs.Sound.Set("cx_nosuchsound");
                InteractKnobs.Sound.Set("gui_select");
                string? note = InteractKnobs.Sound.Note?.Invoke();
                w.Press('F');
                check.Expect(note == null && w.Sounds.Count == 1 && w.Sounds[0] == "gui_select",
                             $"a missing sound set back to one the game has drops the note and plays: '{note}', {w.Seen}");
            }
            {
                var w = InteractWorldDouble.Install();
                w.Known.Remove("gui_nowalk");
                bool took = w.Press('F');
                string? note = InteractKnobs.Sound.Note?.Invoke();
                check.Expect(took && w.Lines.Count == 1 && w.Sounds.Count == 0 && note == InteractInput.SoundNotFound,
                             $"a sound the game lost after its edit plays nothing at the press and the row says so: '{note}', took {took}, {w.Seen}");
            }
            check.Expect(FeedbackLine.Alpha(0f) == 1f && FeedbackLine.Alpha(2.49f) == 1f && MathF.Abs(FeedbackLine.Alpha(2.8f) - 0.5f) < 1e-4f
                         && FeedbackLine.Alpha(3.1f) == 0f && FeedbackLine.Alpha(60f) == 0f,
                         $"the line holds 2.5s then fades over 0.6s: {FeedbackLine.Alpha(2.49f)} {FeedbackLine.Alpha(2.8f)} {FeedbackLine.Alpha(3.1f)}");
        }
        finally
        {
            InteractWorldDouble.Restore();
        }
        log.Info(check.Ok ? "FeedbackEdges.SelfCheck: passed" : "FeedbackEdges.SelfCheck: FAILED");
        return check.Ok;
    }
}
