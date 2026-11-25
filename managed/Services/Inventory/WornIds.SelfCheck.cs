using System;
using CX.Framework;

namespace CX.Services.Inventory;

/// <summary>Proof that the paper doll shows what the stock panel's slot boxes hold. Oracle: hand-written ids, and each
/// slot's place in the creature's array written as a literal (the head mask 0x1 is index 0, the left ring's 0x80 index 7).</summary>
internal static class WornIdsCheck
{
    private const uint Helmet = 0x501, Ring = 0x502, None = InventoryModel.Invalid;   // not-an-offset: object ids

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        int n = InventoryModel.Slots.Length, head = Index("head"), ring = Index("leftring");
        uint[] boxes = new uint[n], worn = new uint[18], ids = new uint[n];

        // Given the CX inventory is open with a helmet worn
        // When a chat command changes the player's appearance
        // Then the head slot still shows the helmet
        Array.Fill(boxes, None); Array.Fill(worn, None);
        boxes[head] = Helmet;
        InventoryNative.WornIds(boxes, worn, ids);
        check.Expect(ids[head] == Helmet, $"an appearance change that empties the creature's head slot leaves the boxed helmet shown (0x{ids[head]:x})");

        Array.Fill(boxes, None);
        worn[7] = Ring;
        InventoryNative.WornIds(boxes, worn, ids);
        check.Expect(ids[ring] == Ring && ids[head] == None,
                     $"with every box empty (a container opened first) the creature's own slots still show (ring 0x{ids[ring]:x}, head 0x{ids[head]:x})");

        log.Info(check.Ok ? "WornIds.SelfCheck: passed" : "WornIds.SelfCheck: FAILED");
    }

    private static int Index(string id)
    {
        for (int i = 0; i < InventoryModel.Slots.Length; i++) if (InventoryModel.Slots[i].Id == id) return i;
        return -1;
    }
}
