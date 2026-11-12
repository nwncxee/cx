using CX.Framework;
using CX.Services.Nui;

namespace CX.Services.NuiItems;

/// <summary>
/// The spell icon the server holds at the pointer while the hub is in target mode (GAME-2244 approved example TC5). Oracle: a
/// hand-driven clock and NUI draw counter, and the icon name typed here; what the model must do with them is TC5's own words,
/// not a value read back from it.
/// </summary>
internal static class NuiItemTargetHeldApprovedExamples
{
    /// <summary>The icon the server holds while target mode is on, written here by hand.</summary>
    private const string SpellIcon = "is_light";

    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiItemTargetHeldApprovedExamples.SelfCheck");
        PointerLeavesAndReturns(check);
        log.Info(check.Ok ? "NuiItemTargetHeldApprovedExamples.SelfCheck: passed" : "NuiItemTargetHeldApprovedExamples.SelfCheck: FAILED");
    }

    // Given the hub in target mode for Light, with its icon at the pointer
    // When I move the pointer over the game world and back onto the hub
    // Then Light's icon is at the pointer again, and target mode is still on
    //
    // A hold the server confirmed lasts until a pick or a cancel, so the stretch with no NUI widget under the pointer
    // must not drop it the way an unanswered pick is dropped: it is still held when the draws resume. Oracle: the clock
    // and the draw counter driven by hand below, well past the model's 500 ms grace.
    private static void PointerLeavesAndReturns(Checker check)
    {
        var m = new HeldImageModel();
        check.Expect(m.Update(1, true, SpellIcon, 10, true, 1000) && m.Active && !m.Pending, "TC5 the server's held.image shows Light's icon, confirmed");

        // The pointer is off the hub: NUI draws no widget under it, the counter stands still for two seconds.
        check.Expect(!m.Update(1, true, null, 10, true, 1400) && m.Active, "TC5 the icon survives the pointer leaving the hub");
        check.Expect(!m.Update(1, true, null, 10, true, 3000) && m.Active && m.Name == SpellIcon, "TC5 two seconds off the hub still leave target mode on, the icon held");

        // The pointer is back over the hub: the widget draws again.
        check.Expect(!m.Update(1, true, null, 11, true, 3100) && m.Active && m.Name == SpellIcon, "TC5 the icon is at the pointer again when the hub draws under it");
    }
}
