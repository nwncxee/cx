using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Framework;

namespace CX.Services.Inventory;

internal sealed partial class InventoryWindow
{
    private const uint Sword = 101, Helm = 105, Potion = 106, Bag = 107, Dagger = 108, Belt = 109, WornHelm = 104, WornSword = 110, Pouch = 111, StoreHelm = 301, StoreRing = 302, Gem = 201, LootChest = 0x500;   // not-an-offset: object ids

    /// <summary>Off-device: the window acting as the stock panel does, one suite per behaviour: coloured names, a drop on a
    /// worn item, the double-click, a drop from outside the backpack, a trade offer and a store drag. Oracle: hand-written fixtures, the stock behaviour each Then names.</summary>
    internal static void StockActsSelfCheck(ExtenderLog log)
    {
        Suite(log, "InventoryWindow.NamesSelfCheck", Names);
        Suite(log, "InventoryWindow.SwapSelfCheck", Swap);
        Suite(log, "InventoryWindow.DoubleClickSelfCheck", DoubleClicks);
        Suite(log, "InventoryWindow.DropsSelfCheck", Drops);
        Suite(log, "InventoryWindow.BarterSelfCheck", Barter);
        Suite(log, "InventoryWindow.BuySelfCheck", Buy);
        Suite(log, "InventoryWindow.StoreMotionSelfCheck", StoreMotion);
        Suite(log, "InventoryWindow.StoreHighlightApprovedExamples.SelfCheck", StoreHighlight);
        Suite(log, "InventoryWindow.StoreUnlitSelfCheck", StoreUnlit);
        Suite(log, "InventoryWindow.FootprintEdgeSelfCheck", FootprintEdge);
        Suite(log, "InventoryWindow.AimSelfCheck", Aim);
        Suite(log, "InventoryWindow.TakeAllSelfCheck", TakeAll);
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

        // Given a 2x2 helmet dragged over the player's side of a trade
        // When it is released past the grid's right edge, left of its origin, or 146 pixels into a 10-row grid
        // Then it is offered in the last column it fits, the first, and cell 4, as CalculateDropPosition's 33-pixel cells give
        int right = DropCell(2.76f, 1.50f, 2, 3), left = DropCell(1.00f, 1.50f, 2, 3), cell = DropCell(3.78f, 2.00f, 2, 10);
        check.Expect(right == 1 && left == 0 && cell == 4,
                     $"a helmet past either edge stays in the grid and rounds at 33-pixel cells (right {right}, left {left}, 146 px {cell})");
    }

    /// <summary>Oracle: the engine moves the icon it drags (a store item, a quickbar button) on the pointer motion it is
    /// handed, and freezes it on motion the window takes.</summary>
    private static void StoreMotion(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        (uint, int, int) drag = default;
        bool button = false;
        w.StoreDrag = () => drag;
        w.EngineDragging = () => drag.Item2 > 0 || button;

        // Given the pointer rests on a backpack item, its tooltip up
        // When the engine starts dragging a store helmet and the pointer moves on the item
        // Then the tooltip closes, as the engine's drag icon is what the pointer carries
        w.MouseMove(b.Cx(7), b.Cy(0));
        bool before = w._tip.Hovering;
        drag = (StoreHelm, 2, 2);
        w.MouseMove(b.Cx(7), b.Cy(0));
        check.Expect(before && !w._tip.Hovering, $"a store drag closes the backpack tooltip (up before {before}, after {w._tip.Hovering})");

        // Given a store is open and the engine is dragging a store helmet
        // When the pointer moves over the window's grid
        // Then the window leaves the motion to the engine, so the engine's icon follows the pointer
        bool taken = w.MouseMove(b.Cx(4), b.Cy(5));
        check.Expect(!taken, "pointer motion over the window during a store drag reaches the engine");

        // Given the same drag
        // When the helmet is let go over cell (4, 5)
        // Then it is bought into the cell the pointer moved to
        b.Acts.Clear();
        w.MouseButton(false, 1, b.Cx(4), b.Cy(5), 0);
        check.Expect(Said(b.Acts) == Said([new InvAction(InvActKind.Buy, StoreHelm, X: 3, Y: 4)]),
                     $"the helmet is bought where the pointer moved ({Said(b.Acts)})");

        // Given the engine is dragging a quickbar button, no store item
        // When the pointer moves over the window's grid
        // Then the window leaves the motion to the engine, so the button's icon follows the pointer too
        drag = default;
        button = true;
        check.Expect(!w.MouseMove(b.Cx(4), b.Cy(5)), "pointer motion over the window during any engine drag reaches the engine");

        // Given no engine drag
        // When the pointer moves over the grid
        // Then the window takes the motion, as it does to show its tooltips
        button = false;
        check.Expect(w.MouseMove(b.Cx(4), b.Cy(5)), "with no store drag the window takes motion over it");
    }

    private static void Buy(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        List<InvAction> acts = b.Acts;
        (uint, int, int) drag = default;
        w.StoreDrag = () => drag;

        // Given a store is open and the CX inventory is showing
        // When the player drags a store item onto a free backpack cell
        // Then the item is bought with no confirm and lands in that cell
        drag = (StoreHelm, 2, 2);
        bool passed = Release(b, b.Cx(4), b.Cy(5));
        check.Expect(acts.Count == 1 && acts[0] == new InvAction(InvActKind.Buy, StoreHelm, X: 3, Y: 4),
                     $"a 2x2 store helmet released over cell (4, 5) is bought into (3, 4), the footprint centred on the pointer ({Said(acts)})");

        // Given a store is open
        // When the player drags a store item onto an occupied cell or the window frame
        // Then the item is bought and the server picks its cell
        drag = (StoreRing, 1, 1);
        Release(b, b.Cx(0), b.Cy(1));
        string onSword = Said(acts);
        Release(b, w.X + w.W / 2f, w.Y + 10f * w.S);
        string onTitle = Said(acts);
        drag = (StoreHelm, 2, 2);
        Release(b, b.Cx(1), b.Cy(0));
        string overlaps = Said(acts);
        string auto = Said([new InvAction(InvActKind.Buy, StoreRing, X: AutoCell, Y: AutoCell)]);
        check.Expect(onSword == auto && onTitle == auto && overlaps == Said([new InvAction(InvActKind.Buy, StoreHelm, X: AutoCell, Y: AutoCell)]),
                     $"a store item on the sword, on the title bar, or a helmet whose footprint covers the sword, is bought for the server to place (sword {onSword}, title {onTitle}, helmet {overlaps})");

        // Given a store is open
        // When the player drags a store item and lets go over the world
        // Then nothing is bought, as in the stock client
        drag = (StoreRing, 1, 1);
        bool worldPassed = Release(b, w.X + w.W + 200f, w.Y + 10f);
        check.Expect(acts.Count == 0 && !worldPassed, $"a store item let go over the world buys nothing ({Said(acts)})");

        // Given the engine's store drag ends on a release over the window
        // When the release is handed back
        // Then the engine sees it, so its drag ends rather than sticking to the pointer
        check.Expect(!passed, "a release over the window during a store drag passes on to the engine");

        // Given a store helmet dragged over the window
        // When it is let go nine tenths of the way across cell (4, 5)
        // Then it is bought into (4, 4): the footprint's centre is nearer that column than the one to its left
        drag = (StoreHelm, 2, 2);
        Release(b, b.Cx(4) + 0.4f * (b.Cx(5) - b.Cx(4)), b.Cy(5));
        string near = Said(acts);
        check.Expect(near == Said([new InvAction(InvActKind.Buy, StoreHelm, X: 4, Y: 4)]),
                     $"a helmet let go nine tenths across cell (4, 5) is bought into (4, 4), the nearer column ({near})");

        // Given the window shows the backpack's second page
        // When a store helmet is let go over its cell (4, 5), or a ring over its top-left cell
        // Then they are bought into (3, 10) and (0, 6), the page's own rows
        w.Top = w._target = InventoryModel.Rows;
        drag = (StoreHelm, 2, 2);
        Release(b, b.Cx(4), b.Cy(5));
        string helm2 = Said(acts);
        drag = (StoreRing, 1, 1);
        Release(b, b.Cx(0), b.Cy(0));
        string ring2 = Said(acts);
        check.Expect(helm2 == Said([new InvAction(InvActKind.Buy, StoreHelm, X: 3, Y: 10)]) && ring2 == Said([new InvAction(InvActKind.Buy, StoreRing, X: 0, Y: 6)]),
                     $"on page 2 a store helmet is bought into (3, 10) and a ring into (0, 6) (helmet {helm2}, ring {ring2})");

        // Given the second page has not arrived from the server yet
        // When a store ring is let go over one of its cells
        // Then the server places it, since the window cannot see what that page holds
        w.Model.Fresh[1] = false;
        Release(b, b.Cx(0), b.Cy(0));
        string blank = Said(acts);
        w.Model.Fresh[1] = true;
        w.Top = w._target = 0f;
        check.Expect(blank == Said([new InvAction(InvActKind.Buy, StoreRing, X: AutoCell, Y: AutoCell)]),
                     $"a ring bought onto a page still loading is left to the server to place ({blank})");

        // Given no store drag is live
        // When the player releases the button over a free cell
        // Then nothing is bought
        drag = default;
        Release(b, b.Cx(4), b.Cy(5));
        check.Expect(acts.Count == 0, $"a bare release over the window buys nothing ({Said(acts)})");
    }

    private static void Aim(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        bool targeting = true;
        w.Targeting = () => targeting;

        // Given the player has used an item that wants a target
        // When they hover a backpack item, or a worn one, in the CX window
        // Then the cursor is graded on that item, as over the stock panel
        w.MouseMove(b.Cx(2), b.Cy(0));
        bool onHelm = w.Aim(out uint helm);
        w.MouseMove(b.SlotX("head"), b.SlotY("head"));
        bool onWorn = w.Aim(out uint worn);
        check.Expect(onHelm && helm == Helm && onWorn && worn == WornHelm,
                     $"the target cursor over the backpack helmet and the worn one grades them ({onHelm} {helm:x}, {onWorn} {worn:x})");

        // Given the same
        // When they hover an empty cell or the window frame
        // Then the cursor shows no target, and the world behind the window is not graded
        w.MouseMove(b.Cx(1), b.Cy(5));
        bool onEmpty = w.Aim(out uint empty);
        w.MouseMove(w.X + w.W / 2, w.Y + 10f * w.S);
        bool onFrame = w.Aim(out uint frame);
        check.Expect(onEmpty && empty == InventoryModel.Invalid && onFrame && frame == InventoryModel.Invalid,
                     $"the target cursor over an empty cell or the frame grades nothing, the window's own ({onEmpty} {empty:x}, {onFrame} {frame:x})");

        // Given the same
        // When they click the item
        // Then the action fires on it, as it does today
        b.Acts.Clear();
        w.MouseButton(true, 1, b.Cx(2), b.Cy(0), 0); w.MouseButton(false, 1, b.Cx(2), b.Cy(0), 0);
        check.Expect(b.Acts.Count == 1 && b.Acts[0] == new InvAction(InvActKind.Target, Helm), $"a click on the helmet fires the cursor at it ({Said(b.Acts)})");

        // Given a target cursor is up
        // When the pointer leaves the window
        // Then the world behind is graded, as it is today
        w.MouseMove(w.X + w.W + 200f, w.Y + 10f);
        check.Expect(!w.Aim(out _), "the target cursor off the window leaves the grading to the world");

        // Given no target cursor is up
        // When the pointer is over a backpack item
        // Then the window grades nothing
        targeting = false;
        w.MouseMove(b.Cx(2), b.Cy(0));
        check.Expect(!w.Aim(out _), "with no target cursor up the window grades nothing");
    }

    /// <summary>The chest's first item; the rest follow it, one id each.</summary>
    private const uint Loot0 = 401;   // not-an-offset: an object id

    private static void TakeAll(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        LootModel l = w.Loot;
        InventoryModel m = w.Model;
        bool knob = InventoryKnobs.TakeAll.V;
        var sent = new List<(long At, InvAction A)>();
        void Capture() => w.Act = a => { sent.Add((b.Now, a)); b.Acts.Add(a); };
        Capture();
        long step = Stopwatch.Frequency / 20, half = Stopwatch.Frequency / 2;

        void Chest(int n, int pages = 1)
        {
            l.Reset(LootChest, b.Now); l.Pages = pages; l.Fresh[0] = true;
            for (int i = 0; i < n; i++) l.Items[i] = new InvItem { Oid = Loot0 + (uint)i, X = i, Y = 4, W = 1, H = 1, BaseId = 30 };
            l.Count = n;
            sent.Clear();
        }
        void Press()
        {
            float x = w.X + w.W + 203f * w.S, y = w.Y + 12f * w.S;
            w.MouseButton(true, 1, x, y, 0); w.MouseButton(false, 1, x, y, 0);
        }
        void Run(float sec, Action? each = null)
        {
            for (int i = 0; i < (int)MathF.Round(sec * 20f); i++) { b.Now += step; w.Tick(); each?.Invoke(); }
        }
        List<(long At, InvAction A)> Takes() => sent.FindAll(s => s.A.Kind == InvActKind.Take);
        List<(long At, InvAction A)> Lines() => sent.FindAll(s => s.A.Kind == InvActKind.Refused && s.A.Text.Length > 0);
        bool Confirm(uint oid)
        {
            int i = l.Find(oid);
            if (i < 0) return false;
            l.Items[i] = l.Items[--l.Count];
            return true;
        }
        void Serve() { List<(long At, InvAction A)> t = Takes(); if (t.Count > 0) Confirm(t[^1].A.Oid); }
        void ServeToPack()
        {
            List<(long At, InvAction A)> t = Takes();
            if (t.Count > 0 && Confirm(t[^1].A.Oid) && m.FirstFree(1, 1, 0, w.PageLimit, out int x, out int y))
                m.Items[m.Count++] = new InvItem { Oid = t[^1].A.Oid, X = x, Y = y, W = 1, H = 1, BaseId = 30 };
        }
        try
        {
            InventoryKnobs.TakeAll.Set(true);
            w.Layout(1f, false, true, 4000, 4000);

            // Given an open chest with five items
            // When the player presses Take all in the loot pane's title bar
            // Then the items move to the backpack one at a time, each only after the server confirms the last, and never
            // faster than one every half second
            Chest(5);
            Press();
            Press();
            Run(0.1f);
            int first = Takes().Count;
            Run(1f);
            List<(long At, InvAction A)> held = Takes();
            check.Expect(first == 1 && held.Count == 1 && held[0].A == new InvAction(InvActKind.Take, Loot0, X: InventoryWindow.AutoCell, Y: InventoryWindow.AutoCell, Other: LootChest),
                         $"Take all sends one take of the first item and holds it until the server confirms ({Said(b.Acts)})");
            Run(4f, Serve);
            List<(long At, InvAction A)> all = Takes();
            long gap = long.MaxValue;
            var oids = new HashSet<uint>();
            for (int i = 0; i < all.Count; i++) { oids.Add(all[i].A.Oid); if (i > 0) gap = Math.Min(gap, all[i].At - all[i - 1].At); }
            check.Expect(all.Count == 5 && oids.Count == 5 && l.Count == 0 && gap >= half,
                         $"the five items are taken one each, no two within half a second ({all.Count} takes, {oids.Count} items, {l.Count} left, closest {gap / (double)Stopwatch.Frequency:0.00} s)");

            // Given a chest with one item low on the left and one a row higher on the right
            // When the player presses Take all
            // Then the higher row is taken first, reading order as the eye scans the pane
            Chest(2);
            l.Items[0].X = 0; l.Items[0].Y = 5; l.Items[1].X = 3; l.Items[1].Y = 4;
            Press();
            Run(0.1f);
            check.Expect(Takes().Count == 1 && Takes()[0].A.Oid == Loot0 + 1, $"Take all takes the top row first ({Said(b.Acts)})");
            w.Hide(); w.Show();

            // Given Take all is running
            // When the backpack fills
            // Then it stops, the rest stay in the chest, and a line says how many were taken
            ServerPolicy.Apply("inventory.backpack.pages=1");
            w.Layout(1f, false, true, 4000, 4000);
            m.Count = 0;
            for (int y = 0; y < InventoryModel.Rows; y++)
                for (int x = 0; x < InventoryModel.Cols; x++)
                    if (y != 0 || x < InventoryModel.Cols - 2) m.Items[m.Count++] = new InvItem { Oid = 600 + (uint)m.Count, X = x, Y = y, W = 1, H = 1, BaseId = 30 };
            Chest(4);
            Press();
            Run(5f, ServeToPack);
            List<(long At, InvAction A)> lines = Lines();
            check.Expect(Takes().Count == 2 && l.Count == 2 && lines.Count == 1 && lines[0].A.Text.Contains("2 taken"),
                         $"a backpack with room for two takes two, leaves two in the chest and says so ({Said(b.Acts)})");
            ServerPolicy.Apply("inventory.backpack.pages=2");
            w.Layout(1f, false, true, 4000, 4000);
            m.Fresh[1] = false;
            Chest(2);
            Press();
            Run(0.1f);
            int unknown = Takes().Count;
            check.Expect(unknown == 1, $"a full first page with the second not sent yet still takes, leaving the room to the server ({Said(b.Acts)})");
            ServerPolicy.Clear();
            b.Build();
            Capture();

            // Given Take all is running
            // When a take goes unconfirmed for 3 seconds
            // Then it stops, the rest stay in the chest, and a line says how many were taken
            Chest(3);
            Press();
            Run(2.9f);
            int early = Lines().Count;
            Run(0.3f);
            lines = Lines();
            Run(2f);
            check.Expect(early == 0 && lines.Count == 1 && lines[0].A.Text.Contains("0 taken") && Takes().Count == 1 && l.Count == 3,
                         $"a take unconfirmed for 3 s stops Take all with a line ({early} early, {Said(b.Acts)})");

            // Given Take all is running
            // When the chest closes, the player walks away, or the player starts any other inventory action
            // Then no further take is sent
            Chest(3);
            Press();
            Run(0.1f);
            Confirm(Loot0);
            l.Reset(InventoryModel.Invalid, b.Now);
            Run(2f);
            int closed = Takes().Count;
            Chest(3);
            Press();
            Run(0.1f);
            Confirm(Loot0);
            w.Hide(); w.Show();
            Run(2f);
            closed += Takes().Count;
            Chest(3);
            Press();
            Run(0.1f);
            Confirm(Loot0);
            b.Now += step;
            Chest(3);
            Run(2f);
            closed += Takes().Count;
            Chest(3);
            Press();
            Run(0.1f);
            Confirm(Loot0);
            w.MouseButton(true, 1, b.Cx(2), b.Cy(0), 0); w.MouseButton(false, 1, b.Cx(2), b.Cy(0), 0);
            Run(2f);
            check.Expect(closed == 2 && Takes().Count == 1, $"a closed chest or window, the same chest closed and opened again, or another press in it, stops Take all ({closed} over the three closes, {Takes().Count} after the press)");

            // Given Take all is running
            // When inventory.takeall is switched off, or another container opens in the pane
            // Then no further take is sent
            Chest(3);
            Press();
            Run(0.1f);
            Confirm(Loot0);
            InventoryKnobs.TakeAll.Set(false);
            w.Layout(1f, false, true, 4000, 4000);
            Run(2f);
            int knobbed = Takes().Count;
            InventoryKnobs.TakeAll.Set(true);
            w.Layout(1f, false, true, 4000, 4000);
            Chest(3);
            Press();
            Run(0.1f);
            Confirm(Loot0);
            uint other = LootChest + 1;
            l.Reset(other, b.Now); l.Fresh[0] = true;
            l.Items[0] = new InvItem { Oid = Loot0 + 7, X = 0, Y = 4, W = 1, H = 1, BaseId = 30 }; l.Count = 1;
            Run(2f);
            check.Expect(knobbed == 1 && Takes().Count == 1, $"switching inventory.takeall off or opening another container stops Take all ({knobbed} with the knob off, {Takes().Count} after the swap)");

            // Given a read-only window (inventory.readonly)
            // When the player presses where Take all would be
            // Then nothing is taken and no line says it stopped
            ServerPolicy.Apply("inventory.readonly=1");
            w.Layout(1f, false, true, 4000, 4000);
            Chest(2);
            Press();
            Run(4f);
            check.Expect(Takes().Count == 0 && Lines().Count == 0, $"a read-only window shows no Take all ({Said(b.Acts)})");
            ServerPolicy.Clear();
            w.Layout(1f, false, true, 4000, 4000);

            // Given a chest whose later pages have not arrived yet
            // When Take all reaches the end of a page
            // Then it turns to the next page and goes on
            Chest(1, pages: 2);
            Press();
            Run(0.1f);
            Confirm(Loot0);
            Run(1f);
            int turned = l.Want(0, 0, b.Now, long.MaxValue / 4);
            l.Ask(1, b.Now); l.Arrived(1);
            l.Items[l.Count++] = new InvItem { Oid = Loot0 + 9, X = 0, Y = 9, W = 1, H = 1, BaseId = 30 };
            Run(1f);
            List<(long At, InvAction A)> paged = Takes();
            check.Expect(turned == 1 && paged.Count == 2 && paged[1].A.Oid == Loot0 + 9,
                         $"page 0 emptied, Take all asks for page 1 and takes from it once it arrives (asked {turned}, {Said(b.Acts)})");

            // Given the Take all button is switched off
            // When the player presses where it would be, closes and reopens the window, and switches it back on
            // Then nothing is taken while it is off, and the button takes again once it is on
            InventoryKnobs.TakeAll.Set(false);
            w.Layout(1f, false, true, 4000, 4000);
            Chest(2);
            float wx = w.X, px = w.X + w.W + 203f * w.S, py = w.Y + 12f * w.S;
            w.MouseButton(true, 1, px, py, 0); w.MouseMove(px + 20f, py); w.MouseButton(false, 1, px + 20f, py, 0);
            Run(1f);
            int off = Takes().Count;
            bool dragged = w.X != wx;
            w.X = wx;
            w.Hide(); w.Show();
            InventoryKnobs.TakeAll.Set(true);
            w.Layout(1f, false, true, 4000, 4000);
            Chest(2);
            Press();
            Run(0.1f);
            check.Expect(off == 0 && dragged && Takes().Count == 1, $"with inventory.takeall off the spot is the title bar and nothing is taken, and on again it takes ({off} off, dragged {dragged}, {Takes().Count} on)");
        }
        finally { InventoryKnobs.TakeAll.Set(knob); w.Act = b.Acts.Add; }
    }

    /// <summary>A release at (<paramref name="x"/>, <paramref name="y"/>) with no press of the window's own; whether it was consumed.</summary>
    private static bool Release(Bench b, float x, float y)
    {
        b.Acts.Clear();
        b.W.MouseMove(x, y);
        return b.W.MouseButton(false, 1, x, y, 0);
    }

    private static int Slot(string id)
    {
        for (int i = 0; i < InventoryModel.Slots.Length; i++) if (InventoryModel.Slots[i].Id == id) return i;
        return -1;
    }

    private static string Said(List<InvAction> acts) => acts.Count == 0 ? "nothing" : string.Join(", ", acts);
}
