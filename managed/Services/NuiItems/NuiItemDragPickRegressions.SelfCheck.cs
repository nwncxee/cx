using CX.Framework;
using CX.Services.Nui;

namespace CX.Services.NuiItems;

/// <summary>
/// A plain click stays a plain click (approved example DD8) on an element that is both draggable and declares a
/// <c>held.pick</c> image: the press shows nothing, and the image waits for the drag to travel. Oracle: the ids and image
/// names typed here by hand.
/// </summary>
internal static class NuiItemDragPickRegressions
{
    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiItemDragPickRegressions.SelfCheck");
        HeldPicks.Reset();
        HeldPicks.Set("cell_*", "@item:7f001234");
        HeldPicks.Set("other_*", "iit_belt");
        check.Expect(HeldImageService.PickOnPress("cell_1", static id => id.StartsWith("cell_")) == null,
            "DD8 a press on a draggable cell shows no image: a click there stays the click the server highlights");
        check.Expect(HeldImageService.PickOnPress("other_1", static id => id.StartsWith("cell_")) == "iit_belt",
            "a press on an element that does not drag still shows its pick at once");
        check.Expect(HeldImageService.PickOnPress("cell_1", null) == "@item:7f001234",
            "with no drag running, a pick shows on the press as before");
        HeldPicks.Reset();
        log.Info(check.Ok ? "NuiItemDragPickRegressions.SelfCheck: passed" : "NuiItemDragPickRegressions.SelfCheck: FAILED");
    }
}
