using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Framework;
using CX.Ui;
using static CX.Ui.PanelChrome;

namespace CX.Services.Inventory;

/// <summary>What the player asked the inventory to do; the service turns each into an engine send (or a
/// <see cref="ServerPolicy"/> route).</summary>
internal enum InvActKind { Equip, Unequip, Move, Merge, Split, Drop, Use, Radial, Close, Refused, Gold, Take, Put, CloseLoot }

/// <summary>One request out of the window. <see cref="Mask"/> is an equip-slot bitmask (a Drop's source slot, 0 from the
/// backpack), <see cref="X"/>/<see cref="Y"/> a backpack cell (global row; a Put's is a container cell, a Take's
/// <see cref="InventoryWindow.AutoCell"/> when the server picks), a split's count, or a Use / Radial / Drop's screen
/// point; <see cref="Other"/> a merge's second object or the open container; <see cref="Text"/> a refusal's reason.</summary>
internal readonly record struct InvAction(InvActKind Kind, uint Oid = 0, uint Mask = 0, int X = 0, int Y = 0, uint Other = 0, string Text = "");

/// <summary>An item icon the owner draws after the chrome batch (the <c>CascadeMenu.Icons</c> pattern): object id, rect,
/// alpha, the item's footprint in cells (which part of its padded sheet is art), and the grid it sits in, if any
/// (<see cref="InventoryWindow.ClipGrid"/> / <see cref="InventoryWindow.ClipLoot"/>: clipped to that grid's rect).</summary>
internal readonly record struct InvIcon(uint Oid, float X, float Y, float W, float H, float Alpha, int CellsW, int CellsH, int Clip);

/// <summary>What a point in the window is. The <c>Loot*</c> kinds are the loot pane's.</summary>
internal enum InvHitKind { None, Title, Close, Search, Tab, Scroll, Cell, Slot, Gold, Chrome, Loot, LootTitle, LootClose, LootCell, LootScroll }

internal readonly record struct InvHit(InvHitKind Kind, int Index = -1, int X = -1, int Y = -1);

/// <summary>
/// The CX inventory window, GL- and engine-free: the vanilla panel's layout in design px scaled for the screen, the
/// drag-and-drop rules of <c>ProcessItemDrop</c> / <c>CGuiInvbox::HandleDragDropAsDest</c>, and the emit into a chrome
/// batch, a skin and an icon list. Every component carries a policy id: a server may hide it, move it or relabel it
/// (<c>inventory.&lt;id&gt;.visible|dx|dy|label</c>), limit the pages or make the window read-only; see <c>docs/ui-policy-contract.md</c>.
/// </summary>
internal sealed partial class InventoryWindow
{
    // ---- design metrics (vanilla px; × Scale on screen) ----
    private const float Pad = 6f, TitleH = 24f, EquipW = 360f, EquipH = 198f, Gap = 6f, Cell = 33f, TabW = 26f,
                        TabGap = 4f, ScrollW = 18f, BarH = 22f, CloseW = 20f, AcH = 16f;
    /// <summary>Rows one wheel notch scrolls in the scrolled view.</summary>
    private const float WheelRows = 2f;
    /// <summary>Rows a second the scrolled view runs while a drag hovers its top or bottom edge.</summary>
    private const float EdgeRowsPerSec = 8f;
    /// <summary>The scrolled view's glide toward its target (1/s; ~95 % in 3/rate).</summary>
    private const float GlideRate = 18f;
    /// <summary>Two clicks on one item within this are a double-click.</summary>
    private const float DoubleClickSec = 0.4f;
    /// <summary>Pointer travel before a press becomes a drag (design px).</summary>
    private const float DragSlop = 4f;
    private const int ButtonLeft = 1, ButtonRight = 3;
    private const int KmodCtrl = 0x00C0, KmodShift = 0x0003;                                      // not-an-offset: SDL KMOD_CTRL / KMOD_SHIFT
    /// <summary>The refusal of a drop onto a page the server has not sent yet.</summary>
    private const string Loading = "That page is still loading.";
    /// <summary>A move the server has not answered within this is forgotten (the item draws where the engine says).</summary>
    private const float PendingSec = 3f;
    private const int MaxPending = 16;
    private const uint KeyEscape = 0x1B, KeyReturn = 0x0D, KeyBackspace = 0x08;                   // not-an-offset: SDL keycodes
    private const uint KeyPageUp = 0x4000004B, KeyPageDown = 0x4000004E, KeyF = 0x66;             // not-an-offset: SDL keycodes

    internal static Func<long> Clock = Stopwatch.GetTimestamp;

    public readonly InventoryModel Model = new();
    /// <summary>Where requests go (the service; the self-check's recorder).</summary>
    public Action<InvAction>? Act;
    /// <summary>An item's display name, cold (tooltip, search). The service asks the engine.</summary>
    public Func<uint, string>? NameOf;

    public bool Open { get; private set; }
    /// <summary>Set by the owner each frame: the game-art skin drew it, so its scrollbars are laid out and hit-tested as
    /// the native bar is drawn rather than as the flat one.</summary>
    public bool NativeArt;
    /// <summary>Screen origin and the effective scale.</summary>
    public float X, Y, S = 1f;
    public float W => _wDesign * S;
    public float H => _hDesign * S;
    /// <summary>The view's first row (global). Tabbed, a whole page; scrolled, fractional while the view glides through
    /// the pages -- which stay pages underneath (a footprint never crosses one; dividers show where they meet).</summary>
    public float Top { get; private set; }
    /// <summary>One page tall, as the vanilla grid.</summary>
    public const int ViewRows = InventoryModel.Rows;
    /// <summary>The scrolled mode: a scrollbar where the tabs were.</summary>
    public bool Scrolled { get; private set; }
    /// <summary>The page mostly on screen: the tab lit, and where Ctrl+click and a tab drop look for room first.</summary>
    public int Page => Math.Min(PageLimit - 1, (int)(Top + (Scrolled ? ViewRows / 2f : 0f)) / InventoryModel.Rows);
    private float MaxTop => (PageLimit - 1) * InventoryModel.Rows;
    private float _target;
    private long _tickAt;
    /// <summary>A server's page ceiling (<c>inventory.backpack.pages</c>).</summary>
    public int PageLimit { get; private set; } = InventoryModel.Pages;
    public bool ReadOnly { get; private set; }
    public bool SearchOn { get; private set; } = true;
    public bool Dragging => _drag;
    public bool Moving => _moving;
    public string Search { get; private set; } = "";
    public bool SearchFocused { get; private set; }

    private float _hDesign, _wDesign = Pad + EquipW + Pad;
    private int _policyEpoch = -1;

    // ---- components (design rects before scale; policy-adjusted) ----
    private struct Box { public float X0, Y0, X1, Y1; public bool On; public string Label; }
    private Box _title, _close, _search, _equip, _grid, _tabs, _scroll, _ac, _gold, _goldButton, _weight;
    private static readonly int Chest = Array.FindIndex(InventoryModel.Slots, d => d.Id == "chest");
    private readonly Box[] _slots = new Box[InventoryModel.Slots.Length];

    // ---- interaction ----
    private bool _press, _drag, _moving, _scrolling, _dragFromLoot;
    private float _pressX, _pressY, _moveDx, _moveDy;
    private uint _dragOid;
    private int _dragFromSlot = -1, _dragW = 1, _dragH = 1, _grabCx, _grabCy;
    private float _mx = -1f, _my = -1f;
    private uint _lastClickOid = InventoryModel.Invalid;
    private long _lastClickAt;
    private readonly Tooltip _tip = new();
    private readonly HashSet<uint> _dim = new();
    private int _dimEpoch = -1, _dimLootEpoch = -1;
    private string _dimFor = "";

    // ---- open / layout ----

    /// <summary>Show the window, first visible page <paramref name="page"/>. Cold.</summary>
    public void Show(int page = 0) { Open = true; SetPage(page); }

    /// <summary>Hide it and drop any gesture in progress.</summary>
    public void Hide()
    {
        Open = false; _press = _drag = _moving = _scrolling = _lootScrolling = false; SearchFocused = false; _tip.Cut();
    }

    /// <summary>Recompute every rect: <paramref name="scale"/> = UI scale × the size knob, shrunk so the window fits
    /// <paramref name="vw"/>×<paramref name="vh"/>; the origin is clamped on screen. Reads the server policy. Cold.</summary>
    public void Layout(float scale, bool scrolled, bool search, float vw, float vh)
    {
        _policyEpoch = ServerPolicy.Epoch;
        _vw = vw; _vh = vh;
        _lootLabel = ServerPolicy.Str("inventory.loot.label", "");
        int page = Page;
        PageLimit = Math.Clamp((int)ServerPolicy.Float("inventory.backpack.pages", InventoryModel.Pages), 1, InventoryModel.Pages);
        Scrolled = scrolled && PageLimit > 1;
        ReadOnly = ServerPolicy.Bool("inventory.readonly", false);
        SearchOn = search && ServerPolicy.Bool("inventory.search.visible", true);
        if (!SearchOn) { Search = ""; SearchFocused = false; }

        float gridW = Cell * InventoryModel.Cols, gridH = ViewRows * Cell;
        float gridY = TitleH + Pad + EquipH + Gap;
        float barY = gridY + gridH + Gap;
        float sideX = Pad + gridW + TabGap;
        _wDesign = Math.Max(Pad + EquipW + Pad, sideX + TabW + Pad);
        _hDesign = barY + BarH + Pad;
        _lootMaxRows = (int)((_hDesign - LootGridY - Pad) / Cell);

        _title = Comp("title", 0f, 0f, _wDesign, TitleH, "Inventory");
        _close = Comp("close", _wDesign - Pad - CloseW, 2f, _wDesign - Pad, 2f + CloseW, "");
        _search = SearchOn ? Comp("search", 120f, 3f, _wDesign - Pad - CloseW - Gap, TitleH - 3f, "Search") : default;
        _equip = Comp("equip", Pad, TitleH + Pad, Pad + EquipW, TitleH + Pad + EquipH, "");
        for (int i = 0; i < _slots.Length; i++)
        {
            SlotDef d = InventoryModel.Slots[i];
            Box b = Comp("slot." + d.Id, _equip.X0 + d.X, _equip.Y0 + d.Y, _equip.X0 + d.X + d.W, _equip.Y0 + d.Y + d.H, d.Label);
            b.On &= _equip.On;
            _slots[i] = b;
        }
        ref Box chest = ref _slots[Chest];
        _ac = Comp("ac", chest.X0, chest.Y1 - AcH, chest.X1, chest.Y1, "");
        _ac.On &= chest.On;
        _grid = Comp("backpack", Pad, gridY, Pad + gridW, gridY + gridH, "");
        _scroll = Scrolled ? Comp("scroll", sideX, gridY, sideX + ScrollW, gridY + gridH, "") : default;
        _tabs = Comp("tabs", sideX, gridY, sideX + TabW, gridY + gridH, "");
        _tabs.On &= !Scrolled && PageLimit > 1;
        _weight = Comp("weight", Pad, barY, _wDesign / 2f, barY + BarH, "lb");
        _goldButton = Comp("goldbutton", _wDesign - Pad - BarH, barY, _wDesign - Pad, barY + BarH, "Drop gold");
        _gold = Comp("gold", _wDesign / 2f, barY, _goldButton.X0 - Gap, barY + BarH, "gp");
        _goldButton.On &= _gold.On;

        float fit = Math.Min(vw > 0 ? (vw - 8f) / _wDesign : scale, vh > 0 ? (vh - 8f) / _hDesign : scale);
        S = Math.Max(0.25f, Math.Min(scale, fit));
        ClampOnScreen(vw, vh);
        Top = _target = Scrolled ? Math.Clamp(_target, 0f, MaxTop) : Math.Min(page, PageLimit - 1) * InventoryModel.Rows;
    }

    /// <summary>Has the server policy moved since <see cref="Layout"/> read it.</summary>
    public bool PolicyStale => _policyEpoch != ServerPolicy.Epoch;

    private static Box Comp(string id, float x0, float y0, float x1, float y1, string label)
    {
        string k = "inventory." + id;
        float dx = ServerPolicy.Float(k + ".dx", 0f), dy = ServerPolicy.Float(k + ".dy", 0f);
        return new Box { X0 = x0 + dx, Y0 = y0 + dy, X1 = x1 + dx, Y1 = y1 + dy, On = ServerPolicy.Bool(k + ".visible", true),
                         Label = ServerPolicy.Str(k + ".label", label) };
    }

    public void ClampOnScreen(float vw, float vh)
    {
        if (vw > 0) X = Window.ClampAxis(X, W, vw);
        if (vh > 0) Y = Window.ClampAxis(Y, H, vh);
    }

    public bool Contains(float px, float py) => Open && (px >= X && py >= Y && px < X + W && py < Y + H || InLootPane(px, py));

    // ---- geometry ----

    private float Sx(float d) => X + d * S;
    private float Sy(float d) => Y + d * S;

    /// <summary>A scrollbar's width on screen: the native bar's whole-px 18, else the design width scaled.</summary>
    private float BarW => NativeArt ? NativeChrome.BarWidth(S) : ScrollW * S;

    /// <summary>The row step a press at <paramref name="py"/> on the native bar's arrows asks for; 0 off them or flat.</summary>
    private int ArrowAt(float py, float y0, float y1) => NativeArt ? NativeChrome.Arrow(y0, y1, BarW, py) : 0;

    /// <summary>The view's first row for the pointer at <paramref name="py"/>, its thumb centred on the pointer: over the
    /// native bar's own track (arrows and padding, read back from <see cref="NativeChrome.ThumbRect"/>) between
    /// <paramref name="y0"/> and <paramref name="y1"/>, or the flat bar's whole height.</summary>
    private float ScrollAt(float py, float y0, float y1, float view, float total)
    {
        float max = Math.Max(0f, total - view);
        if (!NativeArt) return Math.Clamp((py - y0) / Math.Max(1f, y1 - y0) * total - view / 2f, 0f, max);
        NativeChrome.ThumbRect(0f, y0, BarW, y1, 0f, view, total, out _, out float t0, out _, out float t1);
        NativeChrome.ThumbRect(0f, y0, BarW, y1, max, view, total, out _, out float tEnd, out _, out _);
        float travel = tEnd - t0;
        return travel <= 0f ? 0f : Math.Clamp((py - (t1 - t0) / 2f - t0) / travel * max, 0f, max);
    }

    /// <summary>Screen origin of cell (<paramref name="cx"/>, global <paramref name="gy"/>), false when all
    /// <paramref name="h"/> rows from it are scrolled out of view (an item's origin may sit above the view).</summary>
    private bool CellRect(int cx, int gy, out float x0, out float y0, int h = 1)
    {
        float r = gy - Top;
        x0 = y0 = 0f;
        if (r <= -Math.Max(1, h) || r >= ViewRows) return false;
        x0 = Sx(_grid.X0 + cx * Cell);
        y0 = Sy(_grid.Y0 + r * Cell);
        return true;
    }

    private bool In(in Box b, float px, float py) => b.On && px >= Sx(b.X0) && px < Sx(b.X1) && py >= Sy(b.Y0) && py < Sy(b.Y1);

    /// <summary>What is under (<paramref name="px"/>, <paramref name="py"/>).</summary>
    public InvHit HitTest(float px, float py)
    {
        if (InLootPane(px, py)) return LootHit(px, py);
        if (!Open || px < X || py < Y || px >= X + W || py >= Y + H) return default;
        if (In(_close, px, py)) return new(InvHitKind.Close);
        if (SearchOn && In(_search, px, py)) return new(InvHitKind.Search);
        if (In(_title, px, py)) return new(InvHitKind.Title);
        if (In(_goldButton, px, py)) return new(InvHitKind.Gold);
        for (int i = 0; i < _slots.Length; i++) if (In(_slots[i], px, py)) return new(InvHitKind.Slot, i);
        if (In(_grid, px, py))
            return new(InvHitKind.Cell, -1, Math.Min(InventoryModel.Cols - 1, (int)((px - Sx(_grid.X0)) / (Cell * S))),
                       Math.Min(PageLimit * InventoryModel.Rows - 1, (int)MathF.Floor(Top + (py - Sy(_grid.Y0)) / (Cell * S))));
        if (Scrolled && _scroll.On && px >= Sx(_scroll.X0) && px < Sx(_scroll.X0) + BarW && py >= Sy(_scroll.Y0) && py < Sy(_scroll.Y1))
            return new(InvHitKind.Scroll);
        if (In(_tabs, px, py))
        {
            int t = (int)((py - Sy(_tabs.Y0)) / (Cell * S));
            if (t >= 0 && t < PageLimit) return new(InvHitKind.Tab, t);
        }
        return new(InvHitKind.Chrome);
    }

    // ---- pages ----

    /// <summary>Bring page <paramref name="p"/> into the view (scrolled: glide to it). The server hears of it only when the
    /// page is asked for (<see cref="ItemGrid.Want"/>).</summary>
    public void SetPage(int p)
    {
        p = Math.Clamp(p, 0, PageLimit - 1);
        _target = p * InventoryModel.Rows;
        if (!Scrolled) Top = _target;
    }

    /// <summary>The backpack pages with a row on screen.</summary>
    public void BagView(out int first, out int last)
    {
        first = (int)Top / InventoryModel.Rows;
        last = Math.Min(PageLimit - 1, ((int)MathF.Ceiling(Top + ViewRows) - 1) / InventoryModel.Rows);
    }

    /// <summary>The scrollbar thumb follows the pointer, centred on it, without the glide.</summary>
    private void ScrollTo(float py)
        => Top = _target = ScrollAt(py, Sy(_scroll.Y0), Sy(_scroll.Y1), ViewRows, PageLimit * InventoryModel.Rows);

    /// <summary>Per frame, before the emit: the scrolled view glides toward its target and runs while a drag hovers its top
    /// or bottom edge; the loot pane's view does the same (<see cref="LootTick"/>). Alloc-free.</summary>
    public void Tick()
    {
        long now = Clock();
        float dt = _tickAt == 0 ? 0f : Math.Min(0.1f, (now - _tickAt) / (float)Stopwatch.Frequency);
        _tickAt = now;
        if (!Open) return;
        LootTick(dt);
        if (!Scrolled) return;
        if (_drag && _mx >= Sx(_grid.X0) && _mx < Sx(_grid.X1))
        {
            float edge = Cell * S / 2f;
            int run = _my < Sy(_grid.Y0) + edge ? -1 : _my > Sy(_grid.Y1) - edge ? 1 : 0;
            if (run != 0) Top = _target = Math.Clamp(Top + run * EdgeRowsPerSec * dt, 0f, MaxTop);
        }
        Top += (_target - Top) * (1f - MathF.Exp(-GlideRate * dt));
        if (MathF.Abs(_target - Top) < 0.005f) Top = _target;
    }

    // ---- input ----

    /// <summary>A press or release. <paramref name="mods"/> is the SDL modifier state. Consumed when over the window, or
    /// while a gesture it started is live.</summary>
    public bool MouseButton(bool down, int button, float px, float py, int mods)
    {
        if (!Open) return false;
        _mx = px; _my = py;
        if (!down) return Release(button, px, py);
        if (!Contains(px, py)) { if (SearchFocused) SearchFocused = false; return false; }
        _tip.Cut();
        InvHit h = HitTest(px, py);
        if (h.Kind != InvHitKind.Search) SearchFocused = false;
        uint oid = ItemUnder(h);

        if (button == ButtonRight)
        {
            if (oid != InventoryModel.Invalid) Act?.Invoke(new InvAction(InvActKind.Radial, oid, X: (int)px, Y: (int)py));
            return true;
        }
        if (button != ButtonLeft) return true;

        switch (h.Kind)
        {
            case InvHitKind.Close: Act?.Invoke(new InvAction(InvActKind.Close)); return true;
            case InvHitKind.LootClose: Act?.Invoke(new InvAction(InvActKind.CloseLoot, Other: Loot.Oid)); return true;
            case InvHitKind.Search: SearchFocused = true; return true;
            case InvHitKind.Tab: SetPage(h.Index); return true;
            case InvHitKind.Scroll:
                if (ArrowAt(py, Sy(_scroll.Y0), Sy(_scroll.Y1)) is int step and not 0)
                    _target = Math.Clamp(_target + step, 0f, Math.Max(0f, PageLimit * InventoryModel.Rows - ViewRows));
                else { _scrolling = true; ScrollTo(py); }
                return true;
            case InvHitKind.LootScroll:
                if (ArrowAt(py, Ly(LootGridY), Ly(LootGridY + LootViewRows * Cell)) is int lstep and not 0)
                    _lootTarget = Math.Clamp(_lootTarget + lstep, 0f, LootMaxTop);
                else { _lootScrolling = true; LootScrollTo(py); }
                return true;
            case InvHitKind.Gold: if (!ReadOnly) Act?.Invoke(new InvAction(InvActKind.Gold)); return true;
            case InvHitKind.Title or InvHitKind.LootTitle:
                _moving = true; _moveDx = px - X; _moveDy = py - Y; return true;
        }
        if (oid == InventoryModel.Invalid) return true;
        bool loot = h.Kind == InvHitKind.LootCell;

        if (oid == _lastClickOid && Clock() - _lastClickAt < (long)(DoubleClickSec * Stopwatch.Frequency))
        {
            _lastClickOid = InventoryModel.Invalid;
            if (loot) TakeAuto(oid);
            else Act?.Invoke(new InvAction(InvActKind.Use, oid, X: (int)px, Y: (int)py));
            return true;
        }
        if ((mods & KmodCtrl) != 0)
        {
            if (loot) TakeAuto(oid);
            else QuickMove(oid);
            return true;
        }
        if (ReadOnly) return true;
        if (!loot && (mods & KmodShift) != 0 && Info(oid) is { Stackable: true, Stack: > 1 } stack)
        {
            Act?.Invoke(new InvAction(InvActKind.Split, oid, X: stack.Stack / 2));
            return true;
        }

        _press = true; _pressX = px; _pressY = py; _dragOid = oid;
        _dragFromLoot = loot;
        _dragFromSlot = h.Kind == InvHitKind.Slot ? h.Index : -1;
        InvItem it = Info(oid);
        _dragW = Math.Max(1, it.W); _dragH = Math.Max(1, it.H);
        if (_dragFromSlot < 0) { _grabCx = h.X - it.X; _grabCy = h.Y - it.Y; }
        else { _grabCx = (_dragW - 1) / 2; _grabCy = (_dragH - 1) / 2; }
        return true;
    }

    /// <summary>Pointer motion; true when consumed. A drag keeps its state everywhere but consumes motion only over the
    /// window, so outside it the engine's own hover highlights the drop target.</summary>
    public bool MouseMove(float px, float py)
    {
        if (!Open) return false;
        _mx = px; _my = py;
        if (_moving) { X = px - _moveDx; Y = py - _moveDy; return true; }
        if (_scrolling) { ScrollTo(py); return true; }
        if (_lootScrolling) { LootScrollTo(py); return true; }
        if (_press && !_drag && (MathF.Abs(px - _pressX) > DragSlop * S || MathF.Abs(py - _pressY) > DragSlop * S))
        {
            if (Info(_dragOid).Ghost) _press = false;
            else _drag = true;
        }
        if (_drag)
        {
            if (HitTest(px, py) is { Kind: InvHitKind.Tab } t && t.Index != Page) SetPage(t.Index);
            return Contains(px, py);   // outside, the engine hovers as usual: the quickbar button and world object light up
        }
        if (!Contains(px, py)) { _tip.Leave(); return false; }
        HoverTip(px, py);
        return true;
    }

    /// <summary>Wheel over the grid, scrollbar or tabs: a page per notch tabbed, <see cref="WheelRows"/> rows scrolled; over
    /// the loot pane's grid or scrollbar, <see cref="WheelRows"/> rows of it. SDL sign: + = away from the player = up.</summary>
    public bool MouseWheel(int dy)
    {
        if (!Open || !Contains(_mx, _my)) return false;
        InvHit h = HitTest(_mx, _my);
        if (h.Kind is InvHitKind.LootCell or InvHitKind.LootScroll && dy != 0)
            _lootTarget = Math.Clamp(_lootTarget + (dy > 0 ? -WheelRows : WheelRows), 0f, LootMaxTop);
        else if (h.Kind is InvHitKind.Cell or InvHitKind.Tab or InvHitKind.Scroll && dy != 0)
        {
            if (!Scrolled) SetPage(Page + (dy > 0 ? -1 : 1));
            else _target = Math.Clamp(_target + (dy > 0 ? -WheelRows : WheelRows), 0f, MaxTop);
        }
        return true;
    }

    /// <summary>Keys: typing into a focused search box; Ctrl+F focuses it; Esc closes (or leaves the box / a drag);
    /// PageUp/PageDown turn a page. <paramref name="mods"/> is the SDL modifier state. Returns consumed.</summary>
    public bool Key(uint key, int mods = 0)
    {
        if (!Open) return false;
        if (key == KeyEscape)
        {
            if (_drag || _press) { _drag = _press = false; return true; }
            if (SearchFocused) { SearchFocused = false; return true; }
            Act?.Invoke(new InvAction(InvActKind.Close));
            return true;
        }
        if (key == KeyF && (mods & KmodCtrl) != 0 && SearchOn && _search.On) { SearchFocused = true; return true; }
        if (SearchFocused)
        {
            if (key == KeyReturn) SearchFocused = false;
            else if (key == KeyBackspace && Search.Length > 0) Search = Search[..^1];
            return true;
        }
        if (key == KeyPageUp) { SetPage(Page - 1); return true; }
        if (key == KeyPageDown) { SetPage(Page + 1); return true; }
        return false;
    }

    /// <summary>Typed text into the focused search box.</summary>
    public void Type(string s)
    {
        if (SearchFocused && Search.Length < 32) Search += s;
    }

    private bool Release(int button, float px, float py)
    {
        if (button != ButtonLeft) return Contains(px, py);
        if (_moving) { _moving = false; return true; }
        if (_scrolling) { _scrolling = false; return true; }
        if (_lootScrolling) { _lootScrolling = false; return true; }
        if (!_press) return Contains(px, py);
        bool wasDrag = _drag;
        _press = _drag = false;
        if (!wasDrag) { _lastClickOid = _dragOid; _lastClickAt = Clock(); return true; }
        InvAction? a = Drop(HitTest(px, py), Contains(px, py));
        if (a is { } act) Act?.Invoke(act);
        return true;
    }

    /// <summary>The drop rules for the dragged item over <paramref name="h"/>: a cell -> Move / Unequip / Take (out of
    /// the loot) / Merge onto a like stack; a tab -> the first free cell from that page on; a slot -> Equip / Merge; a
    /// loot cell -> <see cref="LootDrop"/>; outside the window -> Drop at the release point. The per-frame preview
    /// passes <paramref name="describe"/> false so a refusal builds no text.</summary>
    internal InvAction? Drop(InvHit h, bool inside, bool describe = true)
    {
        uint oid = _dragOid;
        InvItem it = Info(oid);
        if (!inside)
            return new InvAction(InvActKind.Drop, oid, _dragFromSlot >= 0 ? InventoryModel.Slots[_dragFromSlot].Mask : 0u, (int)_mx, (int)_my,
                                 Other: _dragFromLoot ? Loot.Oid : 0u);
        if (h.Kind == InvHitKind.LootCell) return LootDrop(h, describe);
        InvActKind toBag = _dragFromSlot >= 0 ? InvActKind.Unequip : _dragFromLoot ? InvActKind.Take : InvActKind.Move;
        uint from = _dragFromLoot ? Loot.Oid : 0u;
        bool inBag = _dragFromSlot < 0 && !_dragFromLoot;
        if (h.Kind == InvHitKind.Cell)
        {
            Footprint(h, InventoryModel.Cols, InventoryModel.Rows, out int x, out int y);
            int under = Model.ItemAt(h.X, h.Y);
            if (under >= 0 && Model.Items[under].Oid != oid && Mergeable(Model.Items[under], it))
                return new InvAction(InvActKind.Merge, Model.Items[under].Oid, Other: oid);
            if (Model.Blank(y / InventoryModel.Rows)) return new InvAction(InvActKind.Refused, oid, Text: describe ? Loading : "");
            if (!Model.Fits(x, y, _dragW, _dragH, oid, PageLimit)) return new InvAction(InvActKind.Refused, oid, Text: describe ? "No room there." : "");
            if (inBag && it.X == x && it.Y == y) return null;
            return new InvAction(toBag, oid, X: x, Y: y, Other: from);
        }
        if (h.Kind == InvHitKind.Tab)
        {
            if (inBag && it.Y / InventoryModel.Rows == h.Index) return null;
            if (!Model.FirstFree(_dragW, _dragH, h.Index, PageLimit, out int x, out int y))
                return new InvAction(InvActKind.Refused, oid, Text: describe ? "Your backpack is full." : "");
            return new InvAction(toBag, oid, X: x, Y: y, Other: from);
        }
        if (h.Kind == InvHitKind.Slot)
        {
            if (h.Index == _dragFromSlot) return null;
            SlotDef d = InventoryModel.Slots[h.Index];
            InvItem held = Model.Equipped[h.Index];
            if (held.Oid != InventoryModel.Invalid && Mergeable(held, it))
                return new InvAction(InvActKind.Merge, held.Oid, Other: oid);
            if ((it.SlotMask & d.Mask) == 0) return new InvAction(InvActKind.Refused, oid, Text: describe ? $"That can't be worn in the {d.Label.ToLowerInvariant()} slot." : "");
            if (held.Oid != InventoryModel.Invalid) return new InvAction(InvActKind.Refused, oid, Text: describe ? $"The {d.Label.ToLowerInvariant()} slot is taken." : "");
            return new InvAction(InvActKind.Equip, oid, d.Mask);
        }
        return null;
    }

    /// <summary>Where the dragged footprint lands for a pointer over cell <paramref name="h"/> of a
    /// <paramref name="cols"/>-wide grid of <paramref name="rows"/>-row pages: the grabbed cell under the pointer, clamped
    /// into the pointer's page as the stock <c>CalculateDropPosition</c> clamps into the grid.</summary>
    private void Footprint(InvHit h, int cols, int rows, out int x, out int y)
    {
        int page = h.Y / rows;
        x = Math.Clamp(h.X - _grabCx, 0, Math.Max(0, cols - _dragW));
        y = page * rows + Math.Clamp(h.Y % rows - _grabCy, 0, Math.Max(0, rows - _dragH));
    }

    private static bool Mergeable(in InvItem dst, in InvItem src)
        => dst.Stackable && src.Stackable && dst.BaseId == src.BaseId && dst.Oid != src.Oid;

    /// <summary>Ctrl+click: a backpack item goes to the first empty slot that takes it; a worn one to the first free cell
    /// from the current page on.</summary>
    private void QuickMove(uint oid)
    {
        if (ReadOnly) return;
        int slot = Model.SlotOf(oid);
        InvItem it = Info(oid);
        if (slot >= 0)
        {
            if (Model.FirstFree(Math.Max(1, it.W), Math.Max(1, it.H), Page, PageLimit, out int x, out int y))
                Act?.Invoke(new InvAction(InvActKind.Unequip, oid, X: x, Y: y));
            else Act?.Invoke(new InvAction(InvActKind.Refused, oid, Text: "Your backpack is full."));
            return;
        }
        for (int i = 0; i < InventoryModel.Slots.Length; i++)
            if ((it.SlotMask & InventoryModel.Slots[i].Mask) != 0 && Model.Equipped[i].Oid == InventoryModel.Invalid && _slots[i].On)
            {
                Act?.Invoke(new InvAction(InvActKind.Equip, oid, InventoryModel.Slots[i].Mask));
                return;
            }
        Act?.Invoke(new InvAction(InvActKind.Refused, oid, Text: it.SlotMask == 0 ? "That can't be equipped." : "No free slot takes that."));
    }

    /// <summary>The item under a hit, from the backpack, a slot or the loot.</summary>
    private uint ItemUnder(InvHit h)
    {
        if (h.Kind == InvHitKind.Cell) { int i = Model.ItemAt(h.X, h.Y); return i >= 0 ? Model.Items[i].Oid : InventoryModel.Invalid; }
        if (h.Kind == InvHitKind.LootCell) { int i = Loot.ItemAt(h.X, h.Y); return i >= 0 ? Loot.Items[i].Oid : InventoryModel.Invalid; }
        if (h.Kind == InvHitKind.Slot) return Model.Equipped[h.Index].Oid;
        return InventoryModel.Invalid;
    }

    /// <summary>The backpack, worn or loot item <paramref name="oid"/>; default when none.</summary>
    internal InvItem Info(uint oid)
    {
        int i = Model.Find(oid);
        if (i >= 0) return Model.Items[i];
        int s = Model.SlotOf(oid);
        if (s >= 0) return Model.Equipped[s];
        i = Loot.Find(oid);
        return i >= 0 ? Loot.Items[i] : default;
    }

    private void HoverTip(float px, float py)
    {
        InvHit h = HitTest(px, py);
        uint oid = ItemUnder(h);
        if (oid != InventoryModel.Invalid)
        {
            if (_tip.Key is uint k && k == oid) return;
            InvItem it = Info(oid);
            string name = NameOf?.Invoke(oid) ?? "";
            _tip.Hover(oid, it.Stack > 1 ? $"{name} ({it.Stack})" : name, px, py, py + 16f * S);
            return;
        }
        if (h.Kind == InvHitKind.Slot) { _tip.Hover(_slots[h.Index].Label, _slots[h.Index].Label, px, py, py + 16f * S); return; }
        if (h.Kind == InvHitKind.Gold) { _tip.Hover(_goldButton.Label, _goldButton.Label, px, py, py + 16f * S); return; }
        if (InventoryKnobs.Kilograms.V && In(_weight, px, py))
        {
            string kg = WeightText(Model.WeightTenths, Model.MaxWeight, true, "");
            if (_tip.Key is string k && k == kg) return;
            _tip.Hover(kg, kg, px, py, py + 16f * S);
            return;
        }
        _tip.Leave();
    }

    // ---- in-flight moves ----

    /// <summary>Where an in-flight item sits and where it is headed: the backpack, a slot, the loot pane, or out of view.</summary>
    private const byte AtBag = 0, AtSlot = 1, AtLoot = 2, AtNone = 3;
    private struct Pending { public uint Oid; public int FromX, FromY, FromStack, ToX, ToY; public byte From, To; public long At; }
    private readonly Pending[] _pending = new Pending[MaxPending];
    private int _pendingCount;

    /// <summary>An item action went to the engine: until the server answers (the item's confirmed cell changes, it leaves
    /// the backpack, slot or loot, or <see cref="PendingSec"/> passes) the item draws ghosted -- at its destination when that
    /// is in the same grid -- and cannot be dragged again: the stock panel's ghost (§5.1), kept here because CX does not
    /// write the engine's pending fields. The pages the action lands on are touched (<see cref="Touch"/>).</summary>
    public void MarkPending(in InvAction a)
    {
        Touch(a);
        if (a.Kind is not (InvActKind.Move or InvActKind.Unequip or InvActKind.Equip or InvActKind.Merge or InvActKind.Split
                           or InvActKind.Drop or InvActKind.Take or InvActKind.Put)) return;
        uint oid = a.Kind == InvActKind.Merge ? a.Other : a.Oid;
        InvItem it = Info(oid);
        byte to = a.Kind switch
        {
            InvActKind.Move or InvActKind.Unequip => AtBag,
            InvActKind.Take => a.X == AutoCell ? AtNone : AtBag,
            InvActKind.Put => AtLoot,
            InvActKind.Equip => AtSlot,
            _ => AtNone,
        };
        if (_pendingCount == MaxPending) Array.Copy(_pending, 1, _pending, 0, --_pendingCount);
        _pending[_pendingCount++] = new Pending { Oid = oid, FromX = it.X, FromY = it.Y, FromStack = it.Stack, From = Where(oid), To = to, ToX = a.X, ToY = a.Y, At = Clock() };
    }

    /// <summary>The server sends each grid's streamed page only, so an action whose result shows on another page touches that
    /// page (asked for next): a move's target, the loot page a take empties, the stack a merge grows, the page a use,
    /// split, drop or radial changes. An item the server places itself (a Take it picks the cell for, the new half of a
    /// split) may land on any page, so every other backpack page goes stale.</summary>
    private void Touch(in InvAction a)
    {
        switch (a.Kind)
        {
            case InvActKind.Move or InvActKind.Unequip: Model.Touch(a.Y / InventoryModel.Rows); return;
            case InvActKind.Put: Loot.Touch(a.Y / LootModel.Rows); return;
            case InvActKind.Take:
                TouchAt(a.Oid);
                if (a.X == AutoCell) Model.Stale();
                else Model.Touch(a.Y / InventoryModel.Rows);
                return;
            case InvActKind.Split: TouchAt(a.Oid); Model.Stale(); return;
            case InvActKind.Merge or InvActKind.Use or InvActKind.Drop or InvActKind.Radial: TouchAt(a.Oid); return;
        }
    }

    /// <summary>Touch the page item <paramref name="oid"/> sits on, in whichever grid holds it.</summary>
    private void TouchAt(uint oid)
    {
        int i = Model.Find(oid);
        if (i >= 0) { Model.Touch(Model.Items[i].Y / InventoryModel.Rows); return; }
        i = Loot.Find(oid);
        if (i >= 0) Loot.Touch(Loot.Items[i].Y / LootModel.Rows);
    }

    private byte Where(uint oid) => Model.Find(oid) >= 0 ? AtBag : Model.SlotOf(oid) >= 0 ? AtSlot : Loot.Find(oid) >= 0 ? AtLoot : AtNone;

    /// <summary>Lay the in-flight moves over freshly filled models. Render thread, alloc-free.</summary>
    public void ApplyPending()
    {
        long now = Clock(), ttl = (long)(PendingSec * Stopwatch.Frequency);
        for (int i = _pendingCount - 1; i >= 0; i--)
        {
            ref Pending p = ref _pending[i];
            ItemGrid? g = p.From == AtBag ? Model : p.From == AtLoot ? Loot : null;
            int gi = g?.Find(p.Oid) ?? -1, si = p.From == AtSlot ? Model.SlotOf(p.Oid) : -1;
            bool settled = p.From == AtSlot ? si < 0
                         : gi < 0 || g!.Items[gi].X != p.FromX || g.Items[gi].Y != p.FromY || g.Items[gi].Stack != p.FromStack;
            if (settled || now - p.At > ttl)
            {
                Array.Copy(_pending, i + 1, _pending, i, --_pendingCount - i);
                continue;
            }
            if (si >= 0) { Model.Equipped[si].Ghost = true; continue; }
            ref InvItem it = ref g!.Items[gi];
            it.Ghost = true;
            if (p.To == p.From) { it.X = p.ToX; it.Y = p.ToY; }
        }
    }

    // ---- search ----

    /// <summary>Recompute which items the search dims -- the loot's too -- when the text or an item set moved. Cold (names
    /// are an engine call); the draw only asks the set.</summary>
    public void RefreshSearch()
    {
        if (_dimEpoch == Model.Epoch && _dimLootEpoch == Loot.Epoch && _dimFor == Search) return;
        _dimEpoch = Model.Epoch; _dimLootEpoch = Loot.Epoch; _dimFor = Search;
        _dim.Clear();
        if (Search.Length == 0 || NameOf == null) return;
        for (int i = 0; i < Model.Count; i++)
            if (!NameOf(Model.Items[i].Oid).Contains(Search, StringComparison.OrdinalIgnoreCase)) _dim.Add(Model.Items[i].Oid);
        for (int i = 0; i < Model.Equipped.Length; i++)
            if (Model.Equipped[i].Oid != InventoryModel.Invalid && !NameOf(Model.Equipped[i].Oid).Contains(Search, StringComparison.OrdinalIgnoreCase))
                _dim.Add(Model.Equipped[i].Oid);
        for (int i = 0; i < Loot.Count; i++)
            if (!NameOf(Loot.Items[i].Oid).Contains(Search, StringComparison.OrdinalIgnoreCase)) _dim.Add(Loot.Items[i].Oid);
    }
}
