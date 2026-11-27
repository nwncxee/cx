using CX.Framework;

namespace CX.Services.Camera;

public sealed unsafe partial class MouseLookService
{
    /// <summary>The action camera's toggle takes the chord its bind names, a thumb button's as well as a key's, and an
    /// unbound toggle takes none. Oracle: the SDL codes written here.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    internal static bool ActionToggleChordSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MouseLookService.ActionToggleChordSelfCheck");
        check.Expect(ActionToggle.Wants(-4, -4) && !ActionToggle.Wants(-4, 4), "a toggle on Mouse4 takes the thumb button's chord, not the key with its number");
        check.Expect(ActionToggle.Wants('g', 'g') && !ActionToggle.Wants('g', 'h'), "a toggle on G takes G's chord and no other");
        check.Expect(!ActionToggle.Wants(0, 0), "an unbound toggle takes no chord, not even one with no key code");
        log.Info(check.Ok ? "MouseLookService.ActionToggleChordSelfCheck: passed" : "MouseLookService.ActionToggleChordSelfCheck: FAILED");
        return check.Ok;
    }
}
