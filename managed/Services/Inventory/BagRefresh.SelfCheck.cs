using System;
using CX.Framework;

namespace CX.Services.Inventory;

/// <summary>Found beside the backpack-refresh examples: a bought item that weighs nothing changes only the gold, and it still shows on a
/// backpack page the server is not streaming.</summary>
internal sealed partial class InventoryWindow
{
    private static void WeightlessBuy(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        InventoryModel m = w.Model;
        w.SetPage(0);
        BagServer server = PotionStack(b);
        server.Truth.Add(new InvItem { Oid = Gem, X = 6, Y = 4, W = 1, H = 1, BaseId = 77 });
        m.Gold -= 50;
        for (int i = 0; i < 4; i++) server.Frame(b);
        check.Expect(m.Find(Gem) >= 0,
                     $"a weightless gem bought onto page 1 while page 2 streams shows, from the gold change alone (pages asked [{string.Join(" ", server.Asked)}])");
    }
}
