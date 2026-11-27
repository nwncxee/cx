using CX.Framework;
using CX.Ui;

namespace CX.Services.CombatText;

/// <summary>The peak face CombatTextService asks for in its Initialize, before UiHostService has made the sheet, is held at
/// its px and in the sheet's first bake, so the animation never settles on the UI face for the session. Oracle: the literal
/// px asked here, and whether this host made a sheet.</summary>
internal static class CombatTextPeakFace
{
    /// <summary>A px no service asks for, so the bake list carries it only because this check asked.</summary>
    private const float Px = 9.5f;
    private const string Owner = "cx-peakfacecheck";

    /// <summary>Before the services: the peak face asked ahead of the sheet is held at its px, not 0.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool AskEarly(ExtenderLog log)
    {
        var check = new Checker(log, "CombatTextPeakFace.SelfCheck");
        check.Expect(!TextRig.Ready, "before the services no sheet exists, so the peak face is asked ahead of it");
        float held = CombatTextService.HeldPeakPx(FontRegistry.DefaultName, Px, Owner);
        check.Expect(held == Px, $"a peak face asked before the sheet exists is held at {Px}px, got {held}px");
        log.Info(check.Ok ? "CombatTextPeakFace.SelfCheck: asked" : "CombatTextPeakFace.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>After the services: the face held ahead of the sheet is in its bake list until its owner lets go.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool InFirstBake(ExtenderLog log)
    {
        var check = new Checker(log, "CombatTextPeakFace.SelfCheck");
        check.Expect(TextRig.Asks(FontRegistry.DefaultName, Px), $"the peak face held before the sheet existed is in its first bake at {Px}px");
        float again = CombatTextService.HeldPeakPx(FontRegistry.DefaultName, Px, Owner);
        check.Expect(again == (TextRig.Ready ? Px : 0f), $"asked again once the services ran it is held at {Px}px with a sheet and 0 without, got {again}px");
        TextRig.ReleaseFace(FontRegistry.DefaultName, Px, Owner);
        check.Expect(!TextRig.Asks(FontRegistry.DefaultName, Px), "its owner letting go drops it from the bake list");
        log.Info(check.Ok ? "CombatTextPeakFace.SelfCheck: passed" : "CombatTextPeakFace.SelfCheck: FAILED");
        return check.Ok;
    }
}
