using CX.Framework;

namespace CX.Ui;

/// <summary>A face a service asks for in its Initialize, before UiHostService has made the sheet, is in the sheet's
/// first bake: Interact's feedback line and the combat text's peak face ask that early. Oracle: the face asked here.</summary>
internal static class TextRigEarlyFace
{
    /// <summary>The face the check asks for: the default typeface at a size no service asks for.</summary>
    private const float Px = 7.5f;
    /// <summary>The default-typeface face asked through the overload that names no typeface, at a size no service asks for.</summary>
    private const float DefaultPx = 8.5f;
    private const string Owner = "cx-earlyfacecheck", Other = "cx-earlyfacecheck2";

    /// <summary>Asks for the face before any service has initialized; null, as no sheet exists yet.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool AskEarly(ExtenderLog log)
    {
        var check = new Checker(log, "TextRigEarlyFace.SelfCheck");
        check.Expect(!TextRig.Ready, "before the services no sheet exists, so the face is asked ahead of it");
        check.Expect(TextRig.Face(FontRegistry.DefaultName, Px, Owner) == null && TextRig.Face(FontRegistry.DefaultName, Px, Other) == null,
                     "a face two owners ask for before the sheet exists comes back null to each");
        check.Expect(TextRig.Face(DefaultPx, Owner) == null, "a default-typeface face asked before the sheet exists comes back null");
        log.Info(check.Ok ? "TextRigEarlyFace.SelfCheck: asked" : "TextRigEarlyFace.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>After the services: the face asked before them is in the sheet's bake list, held by both owners until the
    /// second lets go.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool BakedFirst(ExtenderLog log)
    {
        var check = new Checker(log, "TextRigEarlyFace.SelfCheck");
        check.Expect(TextRig.Asks(FontRegistry.DefaultName, Px), $"a face asked before the sheet existed is in its first bake at {Px}px");
        check.Expect(TextRig.Asks(FontRegistry.DefaultName, DefaultPx), $"a default-typeface face asked before the sheet existed is in its first bake at {DefaultPx}px");
        TextRig.ReleaseFace(DefaultPx, Owner);
        check.Expect(!TextRig.Asks(FontRegistry.DefaultName, DefaultPx), "its owner letting go drops the default-typeface face");
        TextRig.ReleaseFace(FontRegistry.DefaultName, Px, Owner);
        check.Expect(TextRig.Asks(FontRegistry.DefaultName, Px), "one owner letting go leaves the face the other still holds");
        TextRig.ReleaseFace(FontRegistry.DefaultName, Px, Other);
        check.Expect(!TextRig.Asks(FontRegistry.DefaultName, Px), "the last owner letting go drops the face from the bake list");
        log.Info(check.Ok ? "TextRigEarlyFace.SelfCheck: passed" : "TextRigEarlyFace.SelfCheck: FAILED");
        return check.Ok;
    }
}
