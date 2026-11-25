using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Framework;

namespace CX.Services.Inventory;

internal sealed partial class InventoryWindow
{
    private const uint Sword = 101, Helm = 105, Potion = 106, Bag = 107, Dagger = 108, Belt = 109, WornHelm = 104, WornSword = 110, Pouch = 111, Gem = 201, LootChest = 0x500;   // not-an-offset: object ids

    /// <summary>Off-device: the window acting as the stock panel does, one suite per behaviour: coloured names, a drop on a
    /// worn item, the double-click, a drop from outside the backpack and a trade offer. Oracle: hand-written fixtures, the stock behaviour each Then names.</summary>
    internal static void StockActsSelfCheck(ExtenderLog log)
    {
        Suite(log, "InventoryWindow.NamesSelfCheck", Names);
        Suite(log, "InventoryWindow.SwapSelfCheck", Swap);
        Suite(log, "InventoryWindow.DoubleClickSelfCheck", DoubleClicks);
        Suite(log, "InventoryWindow.DropsSelfCheck", Drops);
        Suite(log, "InventoryWindow.BarterSelfCheck", Barter);
    }

    private static void Suite(ExtenderLog log, string name, Action<Checker, Bench> run)
    {
        var check = new Checker(log, name);
        Func<long> clock = Clock;
        var b = new Bench();
        Clock = () => b.Now;
        try { ServerPolicy.Clear(); b.Build(); run(check, b); }
        finally { Clock = clock; ServerPolicy.Clear(); }
        log.Info(check.Ok ? $"{name}: passed" : $"{name}: FAILED");
    }

    /// <summary>The fixture each suite starts from: a window over a backpack of six items, a worn helmet and sword.</summary>
    private sealed class Bench
    {
        public long Now = 1_000_000;
        public readonly InventoryWindow W = new();
        public readonly List<InvAction> Acts = new();

        public void Build()
        {
            InventoryWindow w = W;
            w.Act = Acts.Add;
            w.NameOf = oid => oid == Belt ? "<cww\u00fe>Magical Belt</c>" : "Item";
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
        }

        public float Cx(int cx) => (6f + 33f * cx + 10f) * W.S;
        public float Cy(int ly) => (24f + 6f + 198f + 6f + 33f * ly + 10f) * W.S;
        public float SlotX(string id) => (6f + InventoryModel.Slots[Slot(id)].X + 5f) * W.S;
        public float SlotY(string id) => (30f + InventoryModel.Slots[Slot(id)].Y + 5f) * W.S;

        public void Drag(float x0, float y0, float x1, float y1)
        {
            Acts.Clear();
            W.MouseButton(true, 1, x0, y0, 0); W.MouseMove(x1, y1); W.MouseButton(false, 1, x1, y1, 0);
            Now += Stopwatch.Frequency;
        }

        public void DoubleClick(float x, float y, long gap, int secondClicks = 2)
        {
            Acts.Clear();
            W.MouseButton(true, 1, x, y, 0, 1); W.MouseButton(false, 1, x, y, 0);
            Now += gap;
            W.MouseButton(true, 1, x, y, 0, secondClicks); W.MouseButton(false, 1, x, y, 0);
            Now += Stopwatch.Frequency;
        }
    }

    private static void Names(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        // Given an item named <cwwþ>Magical Belt</c>
        // When the player hovers it in the CX inventory
        // Then the tooltip reads Magical Belt in that colour, with no code visible
        w.MouseMove(b.Cx(7), b.Cy(0));
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
        w.Loot.Reset(LootChest, b.Now); w.Loot.Title = "<cÿ×\u0000>Treasure Chest</c>";
        check.Expect(w.LootTitle() == "Treasure Chest", $"the loot pane's title shows a coloured name without its code ('{w.LootTitle()}')");
        w.Loot.Reset(InventoryModel.Invalid, b.Now);
    }

    private static void Swap(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        List<InvAction> acts = b.Acts;
        // Given a helmet worn and another helmet in the backpack
        // When the player drops the new helmet on the head slot
        // Then the new helmet is worn and the old one goes to the backpack
        b.Drag(b.Cx(2), b.Cy(0), b.SlotX("head"), b.SlotY("head"));
        check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Equip, Helm, 0x1),
                     $"a helmet dropped on the worn one equips, the server swapping the old out ({Said(acts)})");

        // Given a sword in the right hand
        // When the player drops a helmet on that slot
        // Then it is refused with the cannot-equip sound, as in the stock client
        b.Drag(b.Cx(2), b.Cy(0), b.SlotX("righthand"), b.SlotY("righthand"));
        check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused && acts[0].Sound == CannotEquip,
                     $"a helmet on the right hand is refused with the cannot-equip sound ({Said(acts)})");
    }

    private static void DoubleClicks(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        List<InvAction> acts = b.Acts;
        // Given a potion in the backpack
        // When the player double-clicks it
        // Then the target cursor for its spell shows
        b.DoubleClick(b.Cx(4), b.Cy(0), Stopwatch.Frequency / 10);
        check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Aim && acts[0].Oid == Potion,
                     $"a double-click on a cast-spell item raises its target cursor ({Said(acts)})");

        // Given a bag in the backpack
        // When the player double-clicks it
        // Then the bag opens
        b.DoubleClick(b.Cx(5), b.Cy(0), Stopwatch.Frequency / 10);
        check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Use && acts[0].Oid == Bag, $"a double-click on a bag opens it ({Said(acts)})");

        // Given a helmet in the backpack
        // When the player double-clicks it
        // Then nothing happens, as in the stock client
        b.DoubleClick(b.Cx(2), b.Cy(0), Stopwatch.Frequency / 10);
        check.Expect(acts.Count == 0, $"a double-click on an item with nothing to use does nothing ({Said(acts)})");

        // Given any item
        // When the player double-clicks it at the speed their system's double-click setting allows
        // Then it counts as a double-click
        b.DoubleClick(b.Cx(5), b.Cy(0), Stopwatch.Frequency);
        check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Use, $"a second click SDL counts as a double-click is one, however slow ({Said(acts)})");

        // Given a bag in the backpack
        // When the player clicks it twice, slower than their system's double-click setting
        // Then the bag stays shut
        b.DoubleClick(b.Cx(5), b.Cy(0), Stopwatch.Frequency / 10, secondClicks: 1);
        check.Expect(acts.Count == 0, $"two clicks SDL counts as single ones are no double-click, however fast ({Said(acts)})");
    }

    private static void Drops(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        List<InvAction> acts = b.Acts;
        // Given a worn helmet
        // When the player drops it on an occupied backpack cell
        // Then it is taken off and lands in a free cell the server picks
        b.Drag(b.SlotX("head"), b.SlotY("head"), b.Cx(0), b.Cy(0));
        check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Unequip, WornHelm, X: AutoCell, Y: AutoCell),
                     $"a worn helmet dropped on a taken cell unequips to a cell the server picks ({Said(acts)})");

        // Given an open chest
        // When the player drops a chest item on an occupied backpack cell
        // Then it lands in a free cell the server picks
        LootModel l = w.Loot;
        l.Reset(LootChest, b.Now); l.Pages = 1; l.Fresh[0] = true;
        l.Items[0] = new InvItem { Oid = Gem, X = 0, Y = 0, W = 1, H = 1, BaseId = 30 };
        l.Count = 1;
        float lx = w.X + w.W + (6f + 10f) * w.S, ly = w.Y + (30f + 10f) * w.S;
        b.Drag(lx, ly, b.Cx(0), b.Cy(0));
        check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Take, Gem, X: AutoCell, Y: AutoCell, Other: LootChest),
                     $"a chest item dropped on a taken cell lands where the server picks ({Said(acts)})");

        // Given an open chest and a bag in the backpack
        // When the player drops a chest item on the bag
        // Then it goes into the bag, and draws ghosted where it was until the server answers
        b.Drag(lx, ly, b.Cx(5), b.Cy(0));
        bool intoBag = acts.Count == 1 && acts[0] == new InvAction(InvActKind.Put, Gem, X: AutoCell, Y: AutoCell, Other: Bag);
        if (intoBag) { w.MarkPending(acts[0]); w.ApplyPending(); }
        check.Expect(intoBag && l.Items[0].X == 0 && l.Items[0].Y == 0 && l.Items[0].Ghost,
                     $"a chest item dropped on a bag goes into it, ghosted where it was ({Said(acts)}, at {l.Items[0].X},{l.Items[0].Y})");
        l.Reset(InventoryModel.Invalid, b.Now);

        // Given a bag open in the loot pane
        // When the player drops a dagger on that bag in the backpack
        // Then it is refused, as in the stock client
        l.Reset(Bag, b.Now);
        b.Drag(b.Cx(0), b.Cy(3), b.Cx(5), b.Cy(0));
        check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused, $"a drop on the bag open in the loot pane is refused ({Said(acts)})");
        l.Reset(InventoryModel.Invalid, b.Now);

        // Given two bags in the backpack
        // When the player drops one bag on the other
        // Then it is refused: a bag does not go inside a bag
        InventoryModel m = w.Model;
        m.Items[6] = new InvItem { Oid = Pouch, X = 1, Y = 3, W = 1, H = 1, BaseId = 66, Container = true };
        m.Count = 7;
        b.Drag(b.Cx(1), b.Cy(3), b.Cx(5), b.Cy(0));
        check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused, $"a bag dropped on a bag is refused ({Said(acts)})");
        m.Count = 6;

        // Given a bag in the backpack
        // When the player drops a dagger on the bag
        // Then the dagger goes into the bag
        b.Drag(b.Cx(0), b.Cy(3), b.Cx(5), b.Cy(0));
        check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Put, Dagger, X: AutoCell, Y: AutoCell, Other: Bag),
                     $"a dagger dropped on a bag goes into it ({Said(acts)})");

        // Given a worn helmet and a bag in the backpack
        // When the player drops the helmet on the bag
        // Then the helmet is taken off into the bag, as the stock grid sends it
        b.Drag(b.SlotX("head"), b.SlotY("head"), b.Cx(5), b.Cy(0));
        check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Unequip, WornHelm, X: AutoCell, Y: AutoCell, Other: Bag),
                     $"a worn helmet dropped on a bag is taken off into it ({Said(acts)})");

        // Given a backpack item
        // When the player drops it on another occupied backpack cell
        // Then it is refused, as in the stock client
        b.Drag(b.Cx(0), b.Cy(3), b.Cx(2), b.Cy(0));
        check.Expect(acts.Count == 1 && acts[0].Kind == InvActKind.Refused, $"a backpack item on another item's cell is refused ({Said(acts)})");

        // Given a worn helmet and a backpack of several pages
        // When the player drops the helmet on an occupied cell
        // Then every backpack page but the one shown is asked for again, since the server may place it on any
        b.Drag(b.SlotX("head"), b.SlotY("head"), b.Cx(0), b.Cy(0));
        if (acts.Count == 1) w.MarkPending(acts[0]);
        int other = m.Selected == 0 ? 1 : 0;
        check.Expect(!m.Fresh[other] && m.Fresh[m.Selected], $"an unequip to a cell the server picks makes the other pages stale (page {other} fresh {m.Fresh[other]})");
    }

    /// <summary>The 'InvM' tag of the other player's side of a trade, a plain <c>CGuiMultiPageMultiInvbox</c>.</summary>
    private const int TheirSideTag = 0x496e764d;   // not-an-offset: 'InvM'

    private static void Barter(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        List<InvAction> acts = b.Acts;
        // Given a trade is open with another player
        // When the player drags a backpack item from the CX window onto a free cell of their side
        // Then the item is offered at that cell
        b.Drag(b.Cx(2), b.Cy(0), 5000f, 5000f);
        bool offered = acts.Count == 1 && OffersOn(acts[0], BarterBoxTag);
        int x = DropCell(2.10f, 1.50f, 2, 3), y = DropCell(3.60f, 2.00f, 2, 10);
        check.Expect(offered && x == 1 && y == 4,
                     $"a 2x2 helmet released 60 by 160 pixels into the player's side is offered at (1, 4) ({Said(acts)}, at {x},{y})");

        // Given a trade is open
        // When the player drags a worn or looted item onto it
        // Then nothing is offered, as in the stock client
        b.Drag(b.SlotX("head"), b.SlotY("head"), 5000f, 5000f);
        bool worn = acts.Count == 1 && OffersOn(acts[0], BarterBoxTag);
        LootModel l = w.Loot;
        l.Reset(LootChest, b.Now); l.Pages = 1; l.Fresh[0] = true;
        l.Items[0] = new InvItem { Oid = Gem, X = 0, Y = 0, W = 1, H = 1, BaseId = 30 };
        l.Count = 1;
        b.Drag(w.X + w.W + (6f + 10f) * w.S, w.Y + (30f + 10f) * w.S, 5000f, 5000f);
        bool looted = acts.Count == 1 && OffersOn(acts[0], BarterBoxTag);
        l.Reset(InventoryModel.Invalid, b.Now);
        check.Expect(!worn && !looted, $"neither a worn helmet nor a chest's gem is offered (worn {worn}, looted {looted})");

        // Given a trade is open
        // When the player drops an item on the other player's side
        // Then nothing is offered
        b.Drag(b.Cx(2), b.Cy(0), 5000f, 5000f);
        bool theirs = acts.Count == 1 && OffersOn(acts[0], TheirSideTag);
        check.Expect(acts.Count == 1 && !theirs, $"a backpack item released on the other player's side is not offered ({Said(acts)})");
    }

    private static int Slot(string id)
    {
        for (int i = 0; i < InventoryModel.Slots.Length; i++) if (InventoryModel.Slots[i].Id == id) return i;
        return -1;
    }

    private static string Said(List<InvAction> acts) => acts.Count == 0 ? "nothing" : string.Join(", ", acts);
}
