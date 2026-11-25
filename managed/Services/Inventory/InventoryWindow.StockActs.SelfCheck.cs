using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Framework;

namespace CX.Services.Inventory;

internal sealed partial class InventoryWindow
{
    private const uint Sword = 101, Helm = 105, Potion = 106, Bag = 107, Dagger = 108, Belt = 109, WornHelm = 104, WornSword = 110, Gem = 201, LootChest = 0x500;   // not-an-offset: object ids

    /// <summary>Off-device: the window acting as the stock panel does: coloured names, a drop on a worn item, the double-click
    /// and a drop from outside the backpack. Oracle: hand-written fixtures, the stock behaviour each Then names.</summary>
    internal static void StockActsSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "InventoryWindow.StockActsSelfCheck");
        Func<long> clock = Clock;
        long now = 1_000_000;
        Clock = () => now;
        try { Run(check); }
        finally { Clock = clock; ServerPolicy.Clear(); }
        log.Info(check.Ok ? "InventoryWindow.StockActsSelfCheck: passed" : "InventoryWindow.StockActsSelfCheck: FAILED");

        void Run(Checker check)
        {
            ServerPolicy.Clear();
            var w = new InventoryWindow();
            var acts = new List<InvAction>();
            w.Act = acts.Add;
            w.NameOf = oid => oid == Belt ? "<cwwþ>Magical Belt</c>" : "Item";
            w.Word = static _ => "?";
            InventoryModel m = w.Model;
            m.Items[0] = new InvItem { Oid = Sword, X = 0, Y = 0, W = 1, H = 3, SlotMask = 0x30, BaseId = 1 };   // not-an-offset: slot masks
            m.Items[1] = new InvItem { Oid = Helm, X = 2, Y = 0, W = 2, H = 2, SlotMask = 0x1, BaseId = 17 };
            m.Items[2] = new InvItem { Oid = Potion, X = 4, Y = 0, W = 1, H = 1, BaseId = 49, CastSpell = true };
            m.Items[3] = new InvItem { Oid = Bag, X = 5, Y = 0, W = 2, H = 2, BaseId = 66, Container = true };
            m.Items[4] = new InvItem { Oid = Dagger, X = 0, Y = 3, W = 1, H = 2, SlotMask = 0x30, BaseId = 22 };   // not-an-offset: slot masks
            m.Items[5] = new InvItem { Oid = Belt, X = 7, Y = 0, W = 1, H = 1, SlotMask = 0x400, BaseId = 21 };   // not-an-offset: slot mask
            m.Count = 6;
            m.Equipped[Slot("head")] = new InvItem { Oid = WornHelm, X = -1, Y = -1, W = 2, H = 2, SlotMask = 0x1 };
            m.Equipped[Slot("righthand")] = new InvItem { Oid = WornSword, X = -1, Y = -1, W = 1, H = 3, SlotMask = 0x30 };   // not-an-offset: slot masks
            Array.Fill(m.Fresh, true);
            w.Layout(1f, false, true, 4000, 4000);
            w.X = 0f; w.Y = 0f;
            w.Show();
            float Cx(int cx) => (6f + 33f * cx + 10f) * w.S;
            float Cy(int ly) => (24f + 6f + 198f + 6f + 33f * ly + 10f) * w.S;
            float SlotX(string id) => (6f + InventoryModel.Slots[Slot(id)].X + 5f) * w.S;
            float SlotY(string id) => (30f + InventoryModel.Slots[Slot(id)].Y + 5f) * w.S;
            void Drag(float x0, float y0, float x1, float y1)
            {
                acts.Clear();
                w.MouseButton(true, 1, x0, y0, 0); w.MouseMove(x1, y1); w.MouseButton(false, 1, x1, y1, 0);
                now += Stopwatch.Frequency;
            }
            void DoubleClick(float x, float y, long gap)
            {
                acts.Clear();
                w.MouseButton(true, 1, x, y, 0, 1); w.MouseButton(false, 1, x, y, 0);
                now += gap;
                w.MouseButton(true, 1, x, y, 0, 2); w.MouseButton(false, 1, x, y, 0);
                now += Stopwatch.Frequency;
            }

            // Given an item named <cwwþ>Magical Belt</c>
            // When the player hovers it in the CX inventory
            // Then the tooltip reads Magical Belt in that colour, with no code visible
            w.MouseMove(Cx(7), Cy(0));
            check.Expect(w._tip.Text == "Magical Belt" && w._tipRgb == 0x7777fe,
                         $"a coloured name's tooltip shows the name in its colour, no code ('{w._tip.Text}', 0x{w._tipRgb:x})");
            w.MouseMove(5000f, 5000f);

            // Given that item
            // When the player searches for belt
            // Then it matches, and a search for cww does not
            w.Search = "belt"; w.RefreshSearch();
            bool belt = !w._dim.Contains(Belt);
            w.Search = "cww"; w.RefreshSearch();
            bool code = w._dim.Contains(Belt);
            w.Search = ""; w.RefreshSearch();
            check.Expect(belt && code, $"search reads the name without its colour code (belt matches {belt}, cww misses {code})");

            // Given a container with a coloured name
            // When the player opens it
            // Then the loot pane's title shows no code
            w.Loot.Reset(LootChest, now); w.Loot.Title = "<cÿ×\u0000>Treasure Chest</c>";
            check.Expect(w.LootTitle() == "Treasure Chest", $"the loot pane's title shows a coloured name without its code ('{w.LootTitle()}')");
            w.Loot.Reset(InventoryModel.Invalid, now);

            // Given a helmet worn and another helmet in the backpack
            // When the player drops the new helmet on the head slot
            // Then the new helmet is worn and the old one goes to the backpack
            Drag(Cx(2), Cy(0), SlotX("head"), SlotY("head"));
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Equip, Helm, 0x1),
                         $"a helmet dropped on the worn one equips, the server swapping the old out ({Said(acts)})");

            // Given a sword in the right hand
            // When the player drops a helmet on that slot
            // Then it is refused with the cannot-equip sound, as in the stock client
            Drag(Cx(2), Cy(0), SlotX("righthand"), SlotY("righthand"));
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused && acts[0].Sound == CannotEquip,
                         $"a helmet on the right hand is refused with the cannot-equip sound ({Said(acts)})");

            // Given a potion in the backpack
            // When the player double-clicks it
            // Then the target cursor for its spell shows
            DoubleClick(Cx(4), Cy(0), Stopwatch.Frequency / 10);
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Aim && acts[0].Oid == Potion,
                         $"a double-click on a cast-spell item raises its target cursor ({Said(acts)})");

            // Given a bag in the backpack
            // When the player double-clicks it
            // Then the bag opens
            DoubleClick(Cx(5), Cy(0), Stopwatch.Frequency / 10);
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Use && acts[0].Oid == Bag, $"a double-click on a bag opens it ({Said(acts)})");

            // Given a helmet in the backpack
            // When the player double-clicks it
            // Then nothing happens, as in the stock client
            DoubleClick(Cx(2), Cy(0), Stopwatch.Frequency / 10);
            check.Expect(acts.Count == 0, $"a double-click on an item with nothing to use does nothing ({Said(acts)})");

            // Given any item
            // When the player double-clicks it at the speed their system's double-click setting allows
            // Then it counts as a double-click
            DoubleClick(Cx(5), Cy(0), Stopwatch.Frequency);
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Use, $"a second click SDL counts as a double-click is one, however slow ({Said(acts)})");

            // Given a worn helmet
            // When the player drops it on an occupied backpack cell
            // Then it is taken off and lands in a free cell the server picks
            Drag(SlotX("head"), SlotY("head"), Cx(0), Cy(0));
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Unequip, WornHelm, X: AutoCell, Y: AutoCell),
                         $"a worn helmet dropped on a taken cell unequips to a cell the server picks ({Said(acts)})");

            // Given an open chest
            // When the player drops a chest item on an occupied backpack cell
            // Then it lands in a free cell the server picks
            LootModel l = w.Loot;
            l.Reset(LootChest, now); l.Pages = 1; l.Fresh[0] = true;
            l.Items[0] = new InvItem { Oid = Gem, X = 0, Y = 0, W = 1, H = 1, BaseId = 30 };
            l.Count = 1;
            float lx = w.X + w.W + (6f + 10f) * w.S, ly = w.Y + (30f + 10f) * w.S;
            Drag(lx, ly, Cx(0), Cy(0));
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Take, Gem, X: AutoCell, Y: AutoCell, Other: LootChest),
                         $"a chest item dropped on a taken cell lands where the server picks ({Said(acts)})");
            l.Reset(InventoryModel.Invalid, now);

            // Given a bag in the backpack
            // When the player drops a dagger on the bag
            // Then the dagger goes into the bag
            Drag(Cx(0), Cy(3), Cx(5), Cy(0));
            check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Put, Dagger, X: AutoCell, Y: AutoCell, Other: Bag),
                         $"a dagger dropped on a bag goes into it ({Said(acts)})");

            // Given a backpack item
            // When the player drops it on another occupied backpack cell
            // Then it is refused, as in the stock client
            Drag(Cx(0), Cy(3), Cx(2), Cy(0));
            check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused, $"a backpack item on another item's cell is refused ({Said(acts)})");
        }
    }

    private static int Slot(string id)
    {
        for (int i = 0; i < InventoryModel.Slots.Length; i++) if (InventoryModel.Slots[i].Id == id) return i;
        return -1;
    }

    private static string Said(List<InvAction> acts) => acts.Count == 0 ? "nothing" : string.Join(", ", acts);
}
