using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Ui;
using static CX.Ui.PanelChrome;

namespace CX.Services.Inventory;

/// <summary>The paper doll tells a slot the server has not sent from an empty one. Oracle: the approved example, read off
/// the frame the window emits (the loader's dots in a slot box, the icons it hands out).</summary>
internal sealed partial class InventoryWindow
{
    private static void UnsentSlots(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        InventoryModel m = w.Model;

        // Given a fresh login wearing a helmet, two rings, an amulet and boots
        // When the player opens a chest before ever opening the inventory
        // Then the ring, amulet and boots slots show the spinner the backpack shows for a page that hasn't loaded yet, never an empty slot
        m.Equipped[Slot("leftring")].Oid = InventoryModel.Invalid;
        m.Equipped[Slot("rightring")].Oid = InventoryModel.Invalid;
        m.Equipped[Slot("neck")].Oid = InventoryModel.Invalid;
        m.Equipped[Slot("boots")].Oid = InventoryModel.Invalid;
        w.Loot.Reset(LootChest, b.Now);
        w.Tick();
        (Dictionary<string, int> dots, List<uint> icons) = DollFrame(b);
        check.Expect(dots["leftring"] >= LoaderDots && dots["rightring"] >= LoaderDots && dots["neck"] >= LoaderDots && dots["boots"] >= LoaderDots,
                     $"a chest opened first: the ring, amulet and boots slots the server never sent draw the loader ({Dots(dots)})");
        check.Expect(dots["head"] == 0 && icons.Contains(WornHelm),
                     $"the helmet the game shows on the character draws in its slot, with no loader ({Dots(dots)}, helmet drawn {icons.Contains(WornHelm)})");
    }

    /// <summary>One emitted frame: the loader dots (accent quads) inside each paper-doll box, by slot id, and the icons drawn.</summary>
    private static (Dictionary<string, int> Dots, List<uint> Icons) DollFrame(Bench b)
    {
        var atlas = new GlyphAtlas { LineHeight = 18, Ascent = 14 };
        for (int cp = 32; cp < 127; cp++) atlas.SetTestGlyph(cp, 8f);
        var under = new VertexBatch(0f, 0f); var over = new VertexBatch(0f, 0f); var gu = new VertexBatch(0f, 0f);
        var go = new VertexBatch(0f, 0f); var lu = new VertexBatch(0f, 0f); var lo = new VertexBatch(0f, 0f);
        var icons = new InvIcon[64];
        int n = b.W.Emit(under, over, gu, go, lu, lo, null, atlas, atlas, icons);
        var dots = new Dictionary<string, int>();
        var quads = Quads(under);
        quads.AddRange(Quads(over));
        for (int i = 0; i < InventoryModel.Slots.Length; i++)
        {
            float x0 = b.SlotX(InventoryModel.Slots[i].Id) - 5f * b.W.S, y0 = b.SlotY(InventoryModel.Slots[i].Id) - 5f * b.W.S;
            float x1 = x0 + InventoryModel.Slots[i].W * b.W.S, y1 = y0 + InventoryModel.Slots[i].H * b.W.S;
            int k = 0;
            foreach ((float x, float y, uint rgb) in quads) if (rgb == Accent && x >= x0 && x < x1 && y >= y0 && y < y1) k++;
            dots[InventoryModel.Slots[i].Id] = k;
        }
        var drawn = new List<uint>();
        for (int i = 0; i < n; i++) drawn.Add(icons[i].Oid);
        return (dots, drawn);
    }

    private static string Dots(Dictionary<string, int> d)
        => $"left ring {d["leftring"]}, right ring {d["rightring"]}, neck {d["neck"]}, boots {d["boots"]}, head {d["head"]} dots";
}
