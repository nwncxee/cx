using CX.Framework;
using CX.Services.Nui;

namespace CX.Services.NuiItems;

/// <summary>
/// What already holds around the item menu and the drag: a session no server declared anything in behaves as a stock one (DD2),
/// and the held pick draws from a press and stays while NUI draws (DD7). Oracle: ids, names and a clock typed in by hand.
/// </summary>
internal static class NuiItemStockRegressions
{
    public static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "NuiItemStockRegressions.SelfCheck");
        NothingDeclared(check);
        HeldPickFollows(check);
        log.Info(check.Ok ? "NuiItemStockRegressions.SelfCheck: passed" : "NuiItemStockRegressions.SelfCheck: FAILED");
    }

    /// <summary>A session with no policy in it: no element picks up an image and every item action goes the engine's own way,
    /// which is what a client without the extender's item work does. Oracle: the ids and action names typed here.</summary>
    private static void NothingDeclared(Checker check)
    {
        ServerPolicy.Clear();
        HeldPicks.Reset();
        check.Expect(HeldPicks.Find("cell_1") == null && HeldPicks.Count == 0, "nothing declared, nothing picked up");
        check.Expect(ServerPolicy.Route("inventory.move") == PolicyRoute.Engine, "an undeclared action goes the engine's way");
        check.Expect(ServerPolicy.Count == 0, "an untouched session holds no property");
    }

    /// <summary>The pick the press shows, and the draws that keep it. Oracle: the image name, the 1000 ms confirm window and the
    /// draw counter, all moved by hand.</summary>
    private static void HeldPickFollows(Checker check)
    {
        HeldPicks.Reset();
        ServerPolicy.Apply("held.pick.cell*=iit_dagger");
        check.Expect(HeldPicks.Find("cell_1") == "iit_dagger", "a declared prefix answers for the cell pressed");
        var m = new HeldImageModel();
        long draw = 7;
        check.Expect(m.Pick("iit_dagger", 1000, 1000, 5, draw) && m.Active && m.Pending, "the press shows the dagger at once");
        check.Expect(!m.Update(1, true, null, ++draw, true, 1500) && m.Active, "the image stays while NUI keeps drawing");
        check.Expect(m.Update(1, true, "", ++draw, true, 1600) && !m.Active, "the server's empty held.image puts it down");
        ServerPolicy.Apply("held.pick.*=");
        HeldPicks.Reset();
        ServerPolicy.Clear();
    }
}
