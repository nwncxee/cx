using CX.Framework;
using CX.Services.Nui;

namespace CX.Services.NuiItems;

/// <summary>
/// The dragged item follows the pointer (approved example DD7) on a server that declares only <c>drag.</c> lines, and for as
/// long as the drag lasts. Oracle: the image names, ids and times typed here by hand.
/// </summary>
internal static class NuiItemDragRegressions
{
    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiItemDragRegressions.SelfCheck");
        CarriedImage(check);
        CarryLasts(check);
        log.Info(check.Ok ? "NuiItemDragRegressions.SelfCheck: passed" : "NuiItemDragRegressions.SelfCheck: FAILED");
    }

    /// <summary>The image carried is the one the pressed cell drew; a <c>held.pick.</c> line for the cell wins over it.</summary>
    private static void CarriedImage(Checker check)
    {
        HeldPicks.Reset();
        check.Expect(ItemDragService.CarryImage("cell_1", "iit_dagger") == "iit_dagger", "DD7 with no pick declared, the drag carries the image the pressed cell drew");
        HeldPicks.Set("cell_*", "@item:7f001234");
        check.Expect(ItemDragService.CarryImage("cell_1", "iit_dagger") == "@item:7f001234", "DD7 a held.pick line for the cell wins over the drawn image");
        HeldPicks.Reset();
    }

    /// <summary>No confirm window, quiet NUI or policy line ends a carry; only the release does.</summary>
    private static void CarryLasts(Checker check)
    {
        var m = new HeldImageModel();
        m.Update(1, true, "", 10, true, 500);
        m.Carry("iit_dagger");
        check.Expect(m.Active && m.Carried && !m.Pending && m.Name == "iit_dagger", "DD7 the carried icon shows at once, at full strength");
        check.Expect(!m.Update(1, true, null, 10, true, 1000) && m.Active, "DD7 ...and stays up while the pointer moves");
        check.Expect(!m.Update(1, true, null, 10, true, 61_000) && m.Active, "DD7 a minute of carrying with no NUI drawn does not drop it");
        check.Expect(!m.Update(2, true, "", 10, true, 61_100) && m.Active, "DD7 a policy line that names no held image does not drop it");
        check.Expect(m.Release() && !m.Active && !m.Carried, "DD7 the release puts it down");

        var quiet = new HeldImageModel();
        quiet.Update(1, true, "", 10, true, 500);
        quiet.Carry("iit_dagger");
        quiet.Release();
        check.Expect(quiet.Stale(1, true), "DD7 once put down, the server's own held image is read again, even with no new line");

        var off = new HeldImageModel();
        off.Carry("iit_dagger");
        check.Expect(off.Update(1, false, "", 10, true, 1000) && !off.Active && !off.Carried, "the held-image knob turned off mid-drag puts the icon down");
    }
}
