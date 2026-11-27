using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Services.Inventory;

/// <summary>Regression partners of the unsent-slot loader (CX-769): they held before the loader and must hold beside it.
/// A slot the server has sent empty draws empty, worn items draw once the chest closes, and a new login waits again.</summary>
internal sealed partial class InventoryWindow
{
    private static void SentSlots(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        InventoryModel m = w.Model;

        // Given the chest from E1 open, with the spinners showing
        // When the player closes the chest and opens the inventory
        // Then every worn item shows in its slot
        m.Equipped[Slot("leftring")].Oid = InventoryModel.Invalid;
        m.Equipped[Slot("neck")].Oid = InventoryModel.Invalid;
        w.Loot.Reset(LootChest, b.Now);
        w.Tick();
        w.Loot.Open = false;
        w.Tick();
        m.Equipped[Slot("leftring")] = new InvItem { Oid = 112, X = -1, Y = -1, W = 1, H = 1, SlotMask = 0x180 };   // not-an-offset: an object id, slot masks
        m.Equipped[Slot("neck")] = new InvItem { Oid = 114, X = -1, Y = -1, W = 1, H = 1, SlotMask = 0x200 };
        (Dictionary<string, int> dots, List<uint> icons) = DollFrame(b);
        check.Expect(icons.Contains(112) && icons.Contains(114) && icons.Contains(WornHelm) && dots["leftring"] == 0 && dots["neck"] == 0,
                     $"the chest closed: the ring, amulet and helmet draw in their slots with no loader ({Dots(dots)}, icons [{string.Join(" ", icons)}])");

        // Given a fresh login wearing no ring
        // When the player opens the inventory with no container open
        // Then both ring slots show empty, with no spinner
        var fresh = new Bench();
        Clock = () => fresh.Now;
        fresh.Build();
        fresh.W.Model.Equipped[Slot("leftring")].Oid = InventoryModel.Invalid;
        fresh.W.Model.Equipped[Slot("rightring")].Oid = InventoryModel.Invalid;
        fresh.W.Tick();
        (dots, _) = DollFrame(fresh);
        check.Expect(dots["leftring"] == 0 && dots["rightring"] == 0, $"no ring worn, no container: both ring slots draw empty ({Dots(dots)})");

        fresh.W.WornUnsent();
        fresh.W.Loot.Reset(LootChest, fresh.Now);
        fresh.W.Tick();
        (dots, _) = DollFrame(fresh);
        check.Expect(dots["leftring"] >= LoaderDots, $"a new login with a chest opened first draws the loader again ({Dots(dots)})");
    }
}
