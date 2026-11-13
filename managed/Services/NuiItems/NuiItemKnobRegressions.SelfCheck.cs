using CX.Framework;
using CX.Services.Nui;

namespace CX.Services.NuiItems;

/// <summary>
/// The item surfaces' self-checks run at every boot, so they leave the player's NUI switch as the player set it, and pass
/// whichever way it is set. Found in a live harness run: a player who turned the NUI fixes off had them back on after boot.
/// Oracle: the switch set off by hand, and each suite's own passed line.
/// </summary>
internal static class NuiItemKnobRegressions
{
    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiItemKnobRegressions.SelfCheck");
        bool was = NuiKnobs.Enabled.V;
        NuiKnobs.Enabled.Set(false);
        bool menu = NuiItemMenuApprovedExamples.Passes(log);
        check.Expect(!NuiKnobs.Enabled.V, "the menu's examples leave the NUI switch off");
        bool drag = NuiItemDragApprovedExamples.Passes(log);
        check.Expect(!NuiKnobs.Enabled.V, "the drag's examples leave the NUI switch off, as the player set it");
        check.Expect(menu && drag, $"both suites pass with the NUI switch off (menu {menu}, drag {drag})");
        NuiKnobs.Enabled.Set(was);
        log.Info(check.Ok ? "NuiItemKnobRegressions.SelfCheck: passed" : "NuiItemKnobRegressions.SelfCheck: FAILED");
    }
}
