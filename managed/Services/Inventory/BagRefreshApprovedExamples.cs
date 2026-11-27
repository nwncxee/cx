using System;
using System.Collections.Generic;
using CX.Framework;

namespace CX.Services.Inventory;

/// <summary>A change the server makes on a backpack page it is not streaming shows without a reopen. Oracle: the approved
/// examples, against a stand-in server that holds the backpack's truth and resends a page only when it is asked for, as
/// the server's inventory GUI does.</summary>
internal sealed partial class InventoryWindow
{
    private const uint StorePotion = 303, WornRing = 112, WornAmulet = 114;   // not-an-offset: object ids

    /// <summary>The server's side of the backpack: every item it holds, the window's model updated only for a page asked for.</summary>
    private sealed class BagServer
    {
        public readonly List<InvItem> Truth = new();
        public readonly List<int> Asked = new();

        public BagServer(InventoryModel m) { for (int i = 0; i < m.Count; i++) Truth.Add(m.Items[i]); }

        /// <summary>One frame of the window's stream: the page the window wants is asked for and resent whole.</summary>
        public void Frame(Bench b)
        {
            InventoryWindow w = b.W;
            InventoryModel m = w.Model;
            w.Tick();
            w.BagView(out int first, out int last);
            int want = m.Want(first, last, b.Now, 0);
            if (want < 0) return;
            Asked.Add(want);
            m.Ask(want, b.Now);
            int n = 0;
            for (int i = 0; i < m.Count; i++) if (m.Items[i].Y / InventoryModel.Rows != want) m.Items[n++] = m.Items[i];
            foreach (InvItem it in Truth) if (it.Y / InventoryModel.Rows == want) m.Items[n++] = it;
            m.Count = n;
            m.Arrived(want);
        }

        public void Settle(Bench b) { for (int i = 0; i < 8; i++) Frame(b); Asked.Clear(); }

        public void Stack(uint oid, int stack)
        {
            for (int i = 0; i < Truth.Count; i++) if (Truth[i].Oid == oid) Truth[i] = Truth[i] with { Stack = stack };
        }
    }

    private static int StackShown(InventoryModel m, uint oid)
    {
        int i = m.Find(oid);
        return i < 0 ? -1 : m.Items[i].Stack;
    }

    /// <summary>The backpack's potion made a 5-stack, the server streaming page 1, every page sent.</summary>
    private static BagServer PotionStack(Bench b)
    {
        InventoryModel m = b.W.Model;
        int p = m.Find(Potion);
        m.Items[p].Stackable = true; m.Items[p].Stack = 5;
        m.Gold = 100; m.WeightTenths = 50;
        var server = new BagServer(m);
        m.Ask(1, b.Now); m.Arrived(1);
        server.Settle(b);
        return server;
    }

    private static void StackOffStream(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        InventoryModel m = w.Model;

        // Given the backpack scrolled to show its first two pages, with a stack of 5 potions on the first
        // When a script adds 3 potions to that stack
        // Then the stack shows 8 without reopening the window
        w.Layout(1f, true, true, 4000, 4000);
        w.Top = w._target = 3f;
        BagServer server = PotionStack(b);
        server.Stack(Potion, 8);
        m.WeightTenths += 3;
        for (int i = 0; i < 4; i++) server.Frame(b);
        check.Expect(StackShown(m, Potion) == 8,
                     $"3 potions a script adds to a 5-stack on page 1 while the server streams page 2 show as 8 (shows {StackShown(m, Potion)}, pages asked [{string.Join(" ", server.Asked)}])");
    }

    private static void DragBuyStack(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        InventoryModel m = w.Model;
        (uint, int, int) drag = default;
        w.StoreDrag = () => drag;

        // Given a store open and a stack of potions in the backpack
        // When the player drags the same potion from the store onto the window
        // Then the stack count grows without reopening the window
        w.SetPage(0);
        BagServer server = PotionStack(b);
        drag = (StorePotion, 1, 1);
        Release(b, b.Cx(4), b.Cy(0));
        List<InvAction> bought = new(b.Acts);
        foreach (InvAction a in bought) w.MarkPending(a);
        drag = default;
        server.Stack(Potion, 6);
        m.Gold -= 10; m.WeightTenths += 1;
        for (int i = 0; i < 4; i++) server.Frame(b);
        check.Expect(bought.Count == 1 && bought[0].Kind == InvActKind.Buy && StackShown(m, Potion) == 6,
                     $"a store potion dropped on the backpack's 5-stack shows the stack grown to 6 (bought {Said(bought)}, shows {StackShown(m, Potion)}, pages asked [{string.Join(" ", server.Asked)}])");
    }

    private static void LoadoutSwap(Checker check, Bench b)
    {
        InventoryWindow w = b.W;
        InventoryModel m = w.Model;

        // Given a backpack fuller than one page and the window open
        // When a script takes off the helmet, a ring and the amulet
        // Then all three show in the backpack without reopening the window
        m.Items[m.Count++] = new InvItem { Oid = Gem, X = 0, Y = 6, W = 1, H = 1, BaseId = 77 };
        m.Equipped[Slot("leftring")] = new InvItem { Oid = WornRing, X = -1, Y = -1, W = 1, H = 1, SlotMask = 0x180 };   // not-an-offset: slot masks
        m.Equipped[Slot("neck")] = new InvItem { Oid = WornAmulet, X = -1, Y = -1, W = 1, H = 1, SlotMask = 0x200 };
        w.SetPage(0);
        var server = new BagServer(m);
        m.Ask(1, b.Now); m.Arrived(1);
        server.Settle(b);
        server.Truth.Add(new InvItem { Oid = WornHelm, X = 7, Y = 4, W = 2, H = 2, SlotMask = 0x1 });   // not-an-offset: slot masks
        server.Truth.Add(new InvItem { Oid = WornRing, X = 6, Y = 5, W = 1, H = 1, SlotMask = 0x180 });
        server.Truth.Add(new InvItem { Oid = WornAmulet, X = 5, Y = 5, W = 1, H = 1, SlotMask = 0x200 });
        m.Equipped[Slot("head")].Oid = InventoryModel.Invalid;
        m.Equipped[Slot("leftring")].Oid = InventoryModel.Invalid;
        m.Equipped[Slot("neck")].Oid = InventoryModel.Invalid;
        for (int i = 0; i < 4; i++) server.Frame(b);
        bool helm = m.Find(WornHelm) >= 0, ring = m.Find(WornRing) >= 0, amulet = m.Find(WornAmulet) >= 0;
        check.Expect(helm && ring && amulet,
                     $"a helmet, ring and amulet a script takes off land on page 1 while page 2 streams, and all three show (helmet {helm}, ring {ring}, amulet {amulet}, pages asked [{string.Join(" ", server.Asked)}])");
    }
}
