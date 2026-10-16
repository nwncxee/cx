using System;

namespace CX.Services.Inventory;

/// <summary>One item as the window sees it, copied out of the engine each frame (pure data).</summary>
internal struct InvItem
{
    public uint Oid;
    /// <summary>Backpack cell: column, and the GLOBAL top row as displayed (<c>page * Rows + localRow</c>, row 0 on top;
    /// the wire counts from the bottom, see <see cref="InventoryModel.Flip"/>). -1 when the item sits in an equip slot.</summary>
    public int X, Y;
    /// <summary>Footprint in cells (<c>baseitems.2da</c> InvSlotWidth/Height).</summary>
    public int W, H;
    public int Stack;
    public int BaseId;
    /// <summary>Equip slots the base item accepts (the <c>INVENTORY_SLOT_*</c> bitmask).</summary>
    public uint SlotMask;
    /// <summary>A move is in flight, awaiting the server's confirm (the engine's ghost flag).</summary>
    public bool Ghost;
    public bool Stackable;
    /// <summary>Double-click means <c>UseObject</c> (<c>CNWBaseItem+0x80</c>).</summary>
    public bool Usable;
}

/// <summary>One equip slot of the vanilla panel: its policy id, label (the TLK's word at <c>StrRef</c>, else
/// <c>Label</c>), <c>INVENTORY_SLOT_*</c> mask, its box in the vanilla layout (px, y-down, relative to the equip area;
/// <c>inv_equip.gui</c> × 100) and its empty-slot silhouette.</summary>
internal readonly record struct SlotDef(string Id, uint StrRef, string Label, uint Mask, float X, float Y, float W, float H, string Silhouette);

/// <summary>A paged item grid: <see cref="GridCols"/> × <see cref="GridRows"/> cells a page, the pages stacked in global
/// rows (<c>page * GridRows + localRow</c>, row 0 on top as displayed); a footprint never crosses a page, as the stock
/// grid holds one page at a time. The backpack and an open container share it: the server streams one page and every
/// switch costs a full resend, so a page is asked for only where the view rests or an action lands, one request at a time.</summary>
internal class ItemGrid
{
    public readonly int GridCols, GridRows;
    public readonly InvItem[] Items;
    public int Count;
    /// <summary>Bumps whenever the item set or a position changes; derived state (filter matches) keys off it.</summary>
    public int Epoch;
    /// <summary>Pages the server has sent since the grid opened.</summary>
    public readonly bool[] Fresh;
    /// <summary>The page the server streams (the one asked for last), the page asked for and not yet arrived (-1 when
    /// none), and when it was asked (Stopwatch ticks).</summary>
    public int Selected, Awaiting = -1;
    public long AskedAt;
    /// <summary>A page an item action touched while the server streams another: asked for next, ahead of the view, or
    /// the action's result never shows (the server sends only its page).</summary>
    public int Focus = -1;
    private int _viewFirst = -1, _viewLast = -1;
    private long _restAt;

    protected ItemGrid(int cols, int rows, int max, int pages)
    {
        GridCols = cols; GridRows = rows; Items = new InvItem[max]; Fresh = new bool[pages];
    }

    /// <summary>The grid (re)opened with the server on page <paramref name="selected"/>: nothing is fresh, nothing asked.</summary>
    public void Restart(int selected)
    {
        Array.Clear(Fresh);
        Selected = selected; Awaiting = Focus = _viewFirst = _viewLast = -1;
    }

    /// <summary>Page <paramref name="page"/> was asked for at <paramref name="now"/>; the server streams it from here on.</summary>
    public void Ask(int page, long now)
    {
        Selected = Awaiting = page; AskedAt = now;
        if (Focus == page) Focus = -1;
    }

    /// <summary>Page <paramref name="page"/> arrived whole.</summary>
    public void Arrived(int page)
    {
        if ((uint)page < (uint)Fresh.Length) Fresh[page] = true;
        if (page == Awaiting) Awaiting = -1;
    }

    /// <summary>Forget what the server sent of every page but the streamed one: an item went where the server chose, which
    /// may be a page not streamed.</summary>
    public void Stale()
    {
        for (int p = 0; p < Fresh.Length; p++) if (p != Selected) Fresh[p] = false;
    }

    /// <summary>Mark page <paramref name="page"/> for asking next when the server streams another.</summary>
    public void Touch(int page)
    {
        if ((uint)page < (uint)Fresh.Length && page != Selected) Focus = page;
    }

    /// <summary>The page to ask for now, -1 for none: nothing while a request is in flight; else the touched page; else,
    /// once the view has shown pages <paramref name="first"/>..<paramref name="last"/> for <paramref name="dwell"/> ticks,
    /// the first of them not fresh. A view sweeping past asks for nothing.</summary>
    public int Want(int first, int last, long now, long dwell)
    {
        if (Awaiting >= 0) return -1;
        if (Focus == Selected) Focus = -1;
        if (Focus >= 0) return Focus;
        if (first != _viewFirst || last != _viewLast) { _viewFirst = first; _viewLast = last; _restAt = now; }
        if (now - _restAt < dwell) return -1;
        for (int p = Math.Max(0, first); p <= last && p < Fresh.Length; p++) if (!Fresh[p]) return p;
        return -1;
    }

    /// <summary>Nothing is known of page <paramref name="page"/>: not sent since the grid opened, and no item remembered on
    /// it. It draws a loader and takes no drop.</summary>
    public bool Blank(int page)
    {
        if ((uint)page >= (uint)Fresh.Length || Fresh[page]) return false;
        for (int i = 0; i < Count; i++) if (Items[i].Y / GridRows == page) return false;
        return true;
    }

    /// <summary>A <paramref name="h"/>-row footprint's top display row from its wire row, and back (an involution): the stock
    /// <c>CGuiMultiInvbox::RepositionModel</c> places wire row <c>y</c> at <c>+y*33</c> up the y-up GUI, so wire row 0 is
    /// each <paramref name="rows"/>-row page's bottom.</summary>
    public static int Flip(int y, int h, int rows) => y / rows * rows + rows - y % rows - h;

    /// <summary>Index of the item covering cell (<paramref name="x"/>, global <paramref name="y"/>), -1 when free.</summary>
    public int ItemAt(int x, int y)
    {
        for (int i = 0; i < Count; i++)
        {
            ref InvItem it = ref Items[i];
            if (x >= it.X && x < it.X + it.W && y >= it.Y && y < it.Y + it.H) return i;
        }
        return -1;
    }

    /// <summary>Index of the item <paramref name="oid"/>, -1 when absent.</summary>
    public int Find(uint oid)
    {
        for (int i = 0; i < Count; i++) if (Items[i].Oid == oid) return i;
        return -1;
    }

    /// <summary>Does a <paramref name="w"/>×<paramref name="h"/> footprint at (<paramref name="x"/>, global <paramref name="y"/>)
    /// fit inside one of the first <paramref name="pages"/> pages and overlap nothing but <paramref name="ignore"/>?</summary>
    public bool Fits(int x, int y, int w, int h, uint ignore, int pages)
    {
        int local = y % GridRows;
        if (x < 0 || y < 0 || x + w > GridCols || local + h > GridRows || y / GridRows >= pages) return false;
        for (int i = 0; i < Count; i++)
        {
            ref InvItem it = ref Items[i];
            if (it.Oid == ignore) continue;
            if (x < it.X + it.W && x + w > it.X && y < it.Y + it.H && y + h > it.Y) return false;
        }
        return true;
    }

    /// <summary>The first cell a <paramref name="w"/>×<paramref name="h"/> item fits among the first <paramref name="pages"/>
    /// pages, page <paramref name="fromPage"/> first, skipping a <see cref="Blank"/> one; false when they are full. Column by column, bottom row up, as the
    /// stock <c>CGuiMultiInvbox::FindEmptyIconPosition</c> walks.</summary>
    public bool FirstFree(int w, int h, int fromPage, int pages, out int x, out int y)
    {
        for (int p = 0; p < pages; p++)
        {
            int page = (fromPage + p) % pages;
            if (Blank(page)) continue;
            for (int cx = 0; cx + w <= GridCols; cx++)
                for (int ly = GridRows - h; ly >= 0; ly--)
                    if (Fits(cx, page * GridRows + ly, w, h, InventoryModel.Invalid, pages)) { x = cx; y = page * GridRows + ly; return true; }
        }
        x = y = -1;
        return false;
    }
}

/// <summary>The inventory as data: backpack items across all pages, the 14 equip slots, and the panel's text. Filled by
/// <see cref="InventoryNative"/>, read by <see cref="InventoryWindow"/>. No engine reference, so the self-check drives it.</summary>
internal sealed class InventoryModel : ItemGrid
{
    public const int Cols = 10, Rows = 6, Pages = 6;
    /// <summary>The engine's invalid object id.</summary>
    public const uint Invalid = 0x7f000000;   // not-an-offset: OBJECT_INVALID
    public const int MaxItems = 512;

    /// <summary><c>CPanelInvEquip::pEquipNames</c> order (Linux 0x183c1e0, Win rva 0xe4ffa0): index i is the vanilla
    /// slot i. Boxes are the <c>inv_eqp_*</c> art sizes at their <c>.gui</c> positions.</summary>
    public static readonly SlotDef[] Slots =
    {
        new("bullets",   6759, "",             0x1000,  102f, 130f, 33f, 68f,  "inv_slot_sling"),   // not-an-offset: INVENTORY_SLOT mask
        new("bolts",     6758, "",             0x2000,  135f, 130f, 33f, 68f,  "inv_slot_bolts"),
        new("arrows",    6757, "",             0x800,    69f, 130f, 33f, 68f,  "inv_slot_arrow"),
        new("leftring",  0,    "Left ring",    0x80,    234f, 130f, 33f, 34f,  "inv_slot_ring"),
        new("righthand", 0,    "Right hand",   0x10,      0f,   0f, 69f, 198f, "inv_slot_right"),
        new("belt",      1518, "",             0x400,   168f, 130f, 66f, 68f,  "inv_slot_belt"),
        new("head",      123,  "",             0x1,     234f,   0f, 66f, 78f,  "inv_slot_helm"),
        new("chest",     335,  "",             0x2,      69f,   0f, 99f, 130f, "inv_slot_armor"),
        new("boots",     1520, "",             0x4,     300f, 130f, 60f, 68f,  "inv_slot_boots"),
        new("rightring", 0,    "Right ring",   0x100,   234f, 164f, 33f, 34f,  "inv_slot_ring"),
        new("lefthand",  0,    "Left hand",    0x20,    168f,   0f, 66f, 130f, "inv_slot_left"),
        new("arms",      0,    "Gloves",       0x8,     234f,  78f, 66f, 52f,  "inv_slot_gloves"),
        new("cloak",     2220, "",             0x40,    300f,   0f, 60f, 130f, "inv_slot_cloak"),
        new("neck",      0,    "Neck",         0x200,   267f, 130f, 33f, 68f,  "inv_slot_amulet"),
    };

    /// <summary><see cref="ItemGrid.Flip(int, int, int)"/> for the backpack's 6-row pages.</summary>
    public static int Flip(int y, int h) => Flip(y, h, Rows);

    /// <summary>What each equip slot holds (<see cref="Slots"/> order); <c>Oid == Invalid</c> when empty.</summary>
    public readonly InvItem[] Equipped = new InvItem[Slots.Length];
    public int Gold, WeightTenths;
    public string ArmorClass = "", MaxWeight = "";

    public InventoryModel() : base(Cols, Rows, MaxItems, Pages)
    {
        for (int i = 0; i < Equipped.Length; i++) Equipped[i].Oid = Invalid;
    }

    /// <summary><see cref="ItemGrid.Fits"/> within all six pages.</summary>
    public bool Fits(int x, int y, int w, int h, uint ignore) => Fits(x, y, w, h, ignore, Pages);

    /// <summary>The equip slot holding <paramref name="oid"/>, -1 when none.</summary>
    public int SlotOf(uint oid)
    {
        for (int i = 0; i < Equipped.Length; i++) if (Equipped[i].Oid == oid) return i;
        return -1;
    }
}

/// <summary>An open container's contents as the loot pane shows them: every page seen since it opened. The server streams
/// one page at a time and pages only relative to the current one (<c>CNWSPlayerContainerGUI::SetNextPage</c> /
/// <c>SetPreviousPage</c>, wrapping); a page is fresh once the server's <c>'P'</c> that leads its resend arrived. Filled by
/// <see cref="InventoryNative.FillLoot"/>; no engine reference.</summary>
internal sealed class LootModel : ItemGrid
{
    /// <summary>The stock container grid: <c>CGuiContainer</c>, <c>SetSize(7, 5)</c>, and the server's page math
    /// agrees (<c>CItemRepository::CalculatePage</c> = <c>(width * y + x) / 35</c>).</summary>
    public const int Cols = 7, Rows = 5;
    /// <summary>The page count is a byte on the wire (<c>Update_GuiContainer</c> <c>'P'</c>).</summary>
    public const int MaxPages = 255;
    public const int MaxItems = 2048;

    public bool Open;
    public uint Oid = InventoryModel.Invalid;
    /// <summary>The container's name as the stock panel titles it (placeable, corpse or bag).</summary>
    public string Title = "";
    /// <summary>The container is an item (a bag in an inventory), not a placeable: its radial is the possessed-item one.</summary>
    public bool IsItem;
    public int Pages = 1;
    /// <summary>The page the stock grid holds -- the server's page -- and when the container opened (Stopwatch ticks).</summary>
    public int EnginePage;
    public long OpenedAt;

    public LootModel() : base(Cols, Rows, MaxItems, MaxPages) { }

    /// <summary>Forget everything: a different container (<paramref name="oid"/>) opened at <paramref name="now"/>, or
    /// none (INVALID). The server starts every container on page 0.</summary>
    public void Reset(uint oid, long now)
    {
        Open = oid != InventoryModel.Invalid; Oid = oid; Title = ""; IsItem = false;
        Count = 0; Pages = 1; EnginePage = 0; OpenedAt = now; Epoch++;
        Restart(0);
    }
}
