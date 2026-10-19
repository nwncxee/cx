using System;
using CX.Framework;

namespace CX.Ui;

/// <summary>One row of a cascade level: a label, an optional engine-texture icon (resref, "" for none, or an owner-
/// minted <see cref="IconHandle"/> for an icon the owner resolves itself, such as a composite item icon), a
/// uses-left count ("" for none), a submenu mark, and the owner's handle. The owner builds and owns the arrays.</summary>
public struct CascadeItem
{
    public string Label;
    public string Icon;
    /// <summary>Opaque to the widget: non-zero means the row has an icon the owner draws from this handle (reported
    /// back as <see cref="IconQuad.Handle"/>) instead of from <see cref="Icon"/>.</summary>
    public IntPtr IconHandle;
    public bool Disabled;
    public bool HasChildren;
    public string Count;
    public object? Tag;

    public CascadeItem(string label, string icon = "", bool disabled = false, bool hasChildren = false, string count = "", object? tag = null)
    { Label = label; Icon = icon; Disabled = disabled; HasChildren = hasChildren; Count = count; Tag = tag; }
}

public enum CascadeActionKind { None, Activate, RequestChildren, Context, CloseAll, Drop }

/// <summary>What an input did: nothing; a leaf picked (on the release of an undragged left press); a submenu row
/// wants its children (the owner materializes them and calls <see cref="CascadeMenu.OpenChild"/>); a right-button
/// press on a row; the menu closed itself; or a row was dragged and dropped -- <see cref="Level"/>/<see cref="Row"/>
/// the source, <see cref="ToLevel"/>/<see cref="ToIndex"/> the gap it landed in (0..n, before row ToIndex).</summary>
public readonly struct CascadeAction
{
    public readonly CascadeActionKind Kind;
    public readonly int Level, Row, ToLevel, ToIndex;
    private CascadeAction(CascadeActionKind kind, int level, int row, int toLevel = -1, int toIndex = -1)
    { Kind = kind; Level = level; Row = row; ToLevel = toLevel; ToIndex = toIndex; }
    public static readonly CascadeAction None = default;
    public static readonly CascadeAction CloseAll = new(CascadeActionKind.CloseAll, -1, -1);
    public static CascadeAction Activate(int level, int row) => new(CascadeActionKind.Activate, level, row);
    public static CascadeAction RequestChildren(int level, int row) => new(CascadeActionKind.RequestChildren, level, row);
    public static CascadeAction Context(int level, int row) => new(CascadeActionKind.Context, level, row);
    public static CascadeAction Drop(int level, int row, int toLevel, int toIndex) => new(CascadeActionKind.Drop, level, row, toLevel, toIndex);
}

/// <summary>A visible icon square for the owner's texture pass (the widget draws no textures itself).</summary>
public readonly struct IconQuad
{
    public readonly string Resref;
    /// <summary>The row's <see cref="CascadeItem.IconHandle"/>, carried through untouched; zero = draw <see cref="Resref"/>.</summary>
    public readonly IntPtr Handle;
    public readonly float X, Y, Size, Alpha;
    public IconQuad(string resref, float x, float y, float size, float alpha, IntPtr handle = default)
    { Resref = resref; Handle = handle; X = x; Y = y; Size = size; Alpha = alpha; }
}

/// <summary>The cascade's look for one draw: the fills as RGBA, the text colour (RGB + alpha, as
/// <see cref="UiText.Draw"/> takes them), and the open animation (<see cref="AnimMs"/> 0 = none) against the
/// owner's clock.</summary>
public struct CascadeStyle
{
    public (float r, float g, float b, float a) Bg, Frame, Hover;
    public uint TextRgb;
    public float TextAlpha;
    /// <summary>Whole-menu multiplier on every alpha.</summary>
    public float Alpha;
    public int AnimMs;
    public long NowMs;

    /// <summary>A knob swatch (RRGGBBAA) as the fill tuple.</summary>
    public static (float r, float g, float b, float a) Rgba(uint c)
        => (((c >> 24) & 0xFF) / 255f, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f);

    /// <summary>The current <see cref="Theme"/>, no animation.</summary>
    public static CascadeStyle Current => new()
    {
        Bg = Rgba(Theme.Rgba(Role.ListBg, 0xfa)), Frame = Rgba(Theme.Rgba(Role.Tx2)), Hover = Rgba(Theme.Rgba(Role.Selected)),
        TextRgb = Theme.Rgb(Role.Tx), TextAlpha = 1f, Alpha = 1f,
    };
}

/// <summary>One open list of the cascade: its rows, its viewport (<see cref="List"/> is the rect), the hovered
/// row, the parent row it hangs off (-1 at the root), and when it first drew (-1 until then).</summary>
public sealed class CascadeLevel
{
    public long OpenedMs = -1;
    public CascadeItem[] Items = Array.Empty<CascadeItem>();
    public readonly ScrollColumn List = new();
    public int Hover = -1;
    public int ParentRow = -1;
    /// <summary>Any row carries an icon, so the icon column is laid out.</summary>
    public bool HasIcons;
    /// <summary>Submenu mark codepoint: U+203A, or '&gt;' when the face lacks it.</summary>
    public int Mark = '>';
    /// <summary>Drill mode (<see cref="CascadeMenu.Drill"/>): the level carries a Back band above its rows, labelled
    /// with the parent row it came from; the band's height is the row height.</summary>
    public bool HasBack;
    public string BackLabel = "";
    public bool BackHover;
    /// <summary>A drag from another level may drop into this one (the host sets it on a level it owns the rows of,
    /// e.g. a folder list); the root always accepts outside drill mode.</summary>
    public bool DropTarget;
    public float X => List.X;
    public float Y => List.Y;
    public float W => List.W;
    public float H => List.H;
    /// <summary>Top of the whole level: the Back band's when it has one, else the rows'.</summary>
    public float Top(float itemH) => HasBack ? List.Y - itemH : List.Y;
    public bool InBack(float mx, float my, float itemH) => HasBack && mx >= List.X && mx < List.X + List.W && my >= List.Y - itemH && my < List.Y;
}

/// <summary>
/// A nested dropdown with an icon column: a root list opened at a point, then child lists opened beside the
/// parent row that owns them, flipped to the parent's left and clamped at the viewport edge. Children are lazy --
/// a submenu row yields <see cref="CascadeActionKind.RequestChildren"/> and the owner answers with
/// <see cref="OpenChild"/>. Pure: pixels and keys in, actions out; icons come back as rects for the owner to draw.
/// </summary>
public sealed partial class CascadeMenu
{
    /// <summary>Rows a level shows at most before it scrolls.</summary>
    public const int MaxVisible = 12;
    /// <summary>Nesting ceiling; a deeper <see cref="OpenChild"/> is refused. ponytail: vanilla's radial is 3 deep;
    /// the level pool and the icon buffer size off this one const, so raising it is the whole upgrade.</summary>
    public const int MaxDepth = 8;
    /// <summary>Default hover dwell before a submenu row asks for its children.</summary>
    public const long DefaultDwellMs = 250;
    /// <summary>Hover dwell before a submenu row asks for its children; 0 = the first Tick after the hover.</summary>
    public long DwellMs = DefaultDwellMs;
    /// <summary>Alpha multiplier of a disabled row.</summary>
    public const float DisabledAlpha = 0.4f;
    /// <summary>Alpha multipliers of the row being dragged and of its ghost label at the pointer.</summary>
    public const float DragSourceAlpha = 0.5f, GhostAlpha = 0.85f;
    /// <summary>Half-thickness of the drop-gap line, in design px.</summary>
    private const float GapLinePx = 1f;
    private const int LeftButton = 1;
    /// <summary>How far a list slides as it fades in: the root from above, a child from its parent's edge.</summary>
    public static float SlidePx => 8f * Metrics.Scale;
    private const uint KeyRight = 0x4000004F, KeyLeft = 0x40000050, KeyDown = 0x40000051, KeyUp = 0x40000052;  // not-an-offset: SDL key/event code
    private const uint KeyEscape = 0x1B, KeyReturn = 0x0D, KeyKpEnter = 0x40000058;  // not-an-offset: SDL key/event code
    /// <summary>Back mark: U+2039, or '&lt;' when the face lacks it.</summary>
    private const int BackMark = 0x2039;  // not-an-offset: a Unicode codepoint
    private const int RightButton = 3;

    public static float Pad => 4f * Metrics.Scale;
    /// <summary>Pointer travel from a left press that turns it into a row drag.</summary>
    public static float DragPx => 4f * Metrics.Scale;
    /// <summary>The dragged row's ghost label sits this far right of the pointer.</summary>
    private static float GhostDx => 12f * Metrics.Scale;
    /// <summary>Widest level; longer labels clip.</summary>
    public static float MaxW => 360f * Metrics.Scale;
    /// <summary>Inset of the icon square inside its row-height cell.</summary>
    public static float IconInset => 2f * Metrics.Scale;
    /// <summary>Narrowest level, in row heights, so a list of one-letter labels still reads as a menu.</summary>
    private const float MinWRows = 4f;

    private readonly CascadeLevel[] _lv = new CascadeLevel[MaxDepth];
    private readonly IconQuad[] _icons = new IconQuad[MaxDepth * (MaxVisible + 1)];
    private int _iconN;
    private GlyphAtlas? _atlas;
    private float _vpW, _vpH, _mx, _my;
    private int _dwellLevel = -1, _dwellRow = -1;
    private long _dwellSince = -1;
    private bool _dwellDone;
    private int _pressLevel = -1, _pressRow = -1;
    private float _pressX, _pressY;

    /// <summary>Off, a left press never becomes a row drag (the owner's layout is locked); a click still activates.</summary>
    public bool DragEnabled = true;
    /// <summary>Drill-down mode: a child replaces its parent in place, under a Back band that returns to it (rows,
    /// scroll and hover kept); only the deepest level shows, takes input and reports icons. Submenu rows open on the
    /// click's release, never on the dwell (the pointer would land on the new list and descend again), and a drag
    /// drops in the row's own list only. Set before <see cref="OpenAt"/>.</summary>
    public bool Drill;
    /// <summary>A row drag is in progress (not the scroll thumb: that is <see cref="Dragging"/>). Hover, the dwell
    /// and level closes all hold while it runs.</summary>
    public bool RowDrag { get; private set; }
    /// <summary>The drop target while <see cref="RowDrag"/>: the level under the pointer (-1 = none, a release
    /// cancels) and the gap index in it, 0..n.</summary>
    public int DropLevel { get; private set; } = -1;
    public int DropIndex { get; private set; }

    public CascadeMenu() { for (int i = 0; i < MaxDepth; i++) _lv[i] = new CascadeLevel(); }

    /// <summary>Open levels; 0 when closed.</summary>
    public int Depth { get; private set; }
    public bool Open => Depth > 0;
    /// <summary>Row height, set at <see cref="OpenAt"/>: the face's line height plus <see cref="Pad"/> each side.</summary>
    public float ItemH { get; private set; }
    public CascadeLevel Level(int i) => _lv[i];
    /// <summary>Icon squares fully inside their level band as of the last <see cref="Draw"/>.</summary>
    public ReadOnlySpan<IconQuad> Icons => new(_icons, 0, _iconN);

    /// <summary>Open the root at a point (the cursor), clamped into the viewport; the face sizes every level.</summary>
    public void OpenAt(CascadeItem[] items, float x, float y, GlyphAtlas atlas, float viewportW, float viewportH)
    {
        _atlas = atlas; _vpW = viewportW; _vpH = viewportH;
        ItemH = atlas.LineHeight + 2f * Pad;
        long opened = Depth > 0 ? _lv[0].OpenedMs : -1;   // a re-layout of an open root does not re-animate
        Depth = 0;
        Place(_lv[0], Layout(_lv[0], items), x, y, -1);
        _lv[0].OpenedMs = opened;
        Depth = 1;
        ResetDwell();
    }

    /// <summary>The owner's answer to a RequestChildren: open <paramref name="items"/> beside row
    /// <paramref name="row"/> of <paramref name="level"/>, closing every deeper level first (a sibling's child
    /// closes when another opens). False when refused (closed, bad row, or at <see cref="MaxDepth"/>).</summary>
    public bool OpenChild(int level, int row, CascadeItem[] items)
    {
        if (!Open || (uint)level >= (uint)Depth || level + 1 >= MaxDepth || (uint)row >= (uint)_lv[level].Items.Length) return false;
        long opened = level + 1 < Depth && _lv[level + 1].ParentRow == row ? _lv[level + 1].OpenedMs : -1;
        CloseTo(level);
        var parent = _lv[level];
        parent.Hover = row;
        var child = _lv[level + 1];
        float w = Layout(child, items);
        if (Drill)
        {
            string back = parent.Items[row].Label ?? "";
            float bw = Pad + (_atlas!.TryGet(BackMarkOf(_atlas), out Glyph bg) ? bg.Advance : 0f) + Pad + UiText.Measure(_atlas, back) + Pad;
            if (bw > w) w = bw > MaxW ? MaxW : bw;
            Place(child, w, parent.X, parent.Top(ItemH), row, back);
        }
        else
        {
            float x = parent.X + parent.W;
            if (x + w > _vpW) x = parent.X - w;
            Place(child, w, x, RowTop(level, row), row);
        }
        child.OpenedMs = opened;
        Depth = level + 2;
        ResetDwell();
        return true;
    }

    /// <summary>Place a laid-out level with its top-left at the point, clamped into the viewport; a
    /// <paramref name="back"/> label puts a Back band above the rows.</summary>
    private void Place(CascadeLevel lv, float w, float x, float y, int parentRow, string? back = null)
    {
        int n = lv.Items.Length;
        float h = (n < MaxVisible ? n : MaxVisible) * ItemH, band = back != null ? ItemH : 0f;
        lv.List.Reset();
        lv.List.SetRect(Window.ClampAxis(x, w, _vpW), Window.ClampAxis(y, h + band, _vpH) + band, w, h);
        lv.List.ContentH = n * ItemH;
        lv.Hover = -1; lv.ParentRow = parentRow; lv.OpenedMs = -1;
        lv.HasBack = back != null; lv.BackLabel = back ?? ""; lv.BackHover = false; lv.DropTarget = false;
    }

    private static int BackMarkOf(GlyphAtlas atlas) => atlas.TryGet(BackMark, out _) ? BackMark : '<';

    private static bool HasIcon(in CascadeItem it) => it.IconHandle != IntPtr.Zero || !string.IsNullOrEmpty(it.Icon);

    /// <summary>Measure a level: icon column, widest label + count + mark, the gutter when it scrolls. Cold.</summary>
    private float Layout(CascadeLevel lv, CascadeItem[] items)
    {
        var atlas = _atlas!;
        lv.Items = items;
        lv.HasIcons = false;
        for (int i = 0; i < items.Length; i++) if (HasIcon(in items[i])) { lv.HasIcons = true; break; }
        lv.Mark = atlas.TryGet(0x203A, out Glyph mg) ? 0x203A : '>';
        float markW = atlas.TryGet(lv.Mark, out mg) ? mg.Advance : 0f;
        float w = 0f, iconCol = lv.HasIcons ? ItemH : 0f;
        for (int i = 0; i < items.Length; i++)
        {
            ref CascadeItem it = ref items[i];
            float rw = iconCol + Pad + UiText.Measure(atlas, it.Label ?? "") + Pad;
            if (!string.IsNullOrEmpty(it.Count)) rw += UiText.Measure(atlas, it.Count) + Pad;
            if (it.HasChildren) rw += markW + Pad;
            if (rw > w) w = rw;
        }
        if (items.Length > MaxVisible) w += Theme.IsNative ? NativeChrome.BarWidth(Metrics.Scale) : ScrollColumn.BarW;
        if (w < MinWRows * ItemH) w = MinWRows * ItemH;
        return w > MaxW ? MaxW : w;
    }

    /// <summary>Keep levels 0..<paramref name="level"/>, close the rest.</summary>
    public void CloseTo(int level)
    {
        int keep = level + 1;
        if (keep < 0) keep = 0;
        for (int i = keep; i < Depth; i++) { _lv[i].List.MouseUp(); _lv[i].Hover = -1; _lv[i].BackHover = false; _lv[i].Items = Array.Empty<CascadeItem>(); }
        if (keep < Depth) Depth = keep;
        if (_dwellLevel >= Depth) ResetDwell();
    }

    /// <summary>Close every level. The widget calls this itself only for
    /// <see cref="CascadeActionKind.CloseAll"/>; after Activate or Context the owner decides.</summary>
    public void Close() { CloseTo(-1); _iconN = 0; ResetDwell(); ResetPress(); }

    private void ResetPress() { _pressLevel = -1; _pressRow = -1; RowDrag = false; DropLevel = -1; }

    private void ResetDwell() { _dwellLevel = -1; _dwellRow = -1; _dwellSince = -1; _dwellDone = false; }

    /// <summary>Screen top of a row (may lie outside its level while scrolled).</summary>
    public float RowTop(int level, int row) => _lv[level].List.ToScreen(row * ItemH);

    public bool Contains(float mx, float my) => LevelAt(mx, my) >= 0;

    /// <summary>Deepest open level under the point, or -1. In drill mode only the deepest level (its Back band
    /// included) is under anything.</summary>
    public int LevelAt(float mx, float my)
    {
        if (!Open) return -1;
        if (Drill) { var top = _lv[Depth - 1]; return top.List.Contains(mx, my) || top.InBack(mx, my, ItemH) ? Depth - 1 : -1; }
        for (int i = Depth - 1; i >= 0; i--) if (_lv[i].List.Contains(mx, my)) return i;
        return -1;
    }

    /// <summary>The deepest level's Back band is under the point (drill mode).</summary>
    private bool BackAt(float mx, float my) => Drill && Open && _lv[Depth - 1].InBack(mx, my, ItemH);

    /// <summary>Row of <paramref name="level"/> under the point (the gutter excluded), or -1.</summary>
    public int ItemAt(int level, float mx, float my)
    {
        var lv = _lv[level];
        if (!lv.List.Contains(mx, my) || lv.List.OverBar(mx, my)) return -1;
        int i = (int)(lv.List.ToContent(my) / ItemH);
        return (uint)i < (uint)lv.Items.Length ? i : -1;
    }

    /// <summary>Motion: a thumb drag, a row drag (armed by a left press, started past <see cref="DragPx"/>: the drop
    /// gap tracks the pointer, nothing else moves), else hover in the level under the pointer (deeper hovers clear,
    /// shallower ones keep, so the row owning an open child stays lit). True when something to redraw changed.</summary>
    public bool MouseMove(float mx, float my)
    {
        if (!Open) return false;
        _mx = mx; _my = my;
        for (int i = 0; i < Depth; i++) if (_lv[i].List.MouseMove(mx, my)) return true;
        if (_pressLevel >= 0 && _pressRow != BackRow && !RowDrag && DragEnabled && (MathF.Abs(mx - _pressX) > DragPx || MathF.Abs(my - _pressY) > DragPx)) RowDrag = true;
        if (RowDrag)
        {
            int at = LevelAt(mx, my);
            DropLevel = at == _pressLevel || (at == 0 && !Drill) || (at > 0 && _lv[at].DropTarget) ? at : -1;   // a drop lands in the row's own list, the top one, or a level that takes drops
            if (DropLevel >= 0)
            {
                var dl = _lv[DropLevel];
                int gap = (int)MathF.Round(dl.List.ToContent(my) / ItemH);
                DropIndex = gap < 0 ? 0 : gap > dl.Items.Length ? dl.Items.Length : gap;
            }
            return true;
        }
        int l = LevelAt(mx, my);
        if (l < 0) { ResetDwell(); return SetBackHover(false); }
        int row = ItemAt(l, mx, my);
        bool changed = SetBackHover(BackAt(mx, my));
        for (int i = l + 1; i < Depth; i++) if (_lv[i].Hover != -1) { _lv[i].Hover = -1; changed = true; }
        if (_lv[l].Hover != row) { _lv[l].Hover = row; changed = true; }
        if (l != _dwellLevel || row != _dwellRow) { _dwellLevel = l; _dwellRow = row; _dwellSince = -1; _dwellDone = false; }
        return changed;
    }

    private bool SetBackHover(bool on)
    {
        if (!Open) return false;
        var top = _lv[Depth - 1];
        if (top.BackHover == on) return false;
        top.BackHover = on;
        return true;
    }

    /// <summary>Release: the thumb lets go; a row drag drops into the gap under the pointer (None when over no
    /// level); an undragged left press released on the leaf it pressed activates it (in drill mode a submenu row
    /// released the same way asks for its children, and the Back band goes up a level).</summary>
    public CascadeAction MouseUp()
    {
        for (int i = 0; i < Depth; i++) _lv[i].List.MouseUp();
        if (!Open || _pressLevel < 0) return CascadeAction.None;
        int l = _pressLevel, row = _pressRow;
        bool drag = RowDrag;
        int toL = DropLevel, toI = DropIndex;
        ResetPress();
        if (drag) return toL >= 0 ? CascadeAction.Drop(l, row, toL, toI) : CascadeAction.None;
        if (row == BackRow) { if (l == Depth - 1 && BackAt(_mx, _my)) CloseTo(l - 1); return CascadeAction.None; }
        if (l >= Depth || (uint)row >= (uint)_lv[l].Items.Length || LevelAt(_mx, _my) != l || ItemAt(l, _mx, _my) != row) return CascadeAction.None;
        ref CascadeItem it = ref _lv[l].Items[row];
        if (it.Disabled) return CascadeAction.None;
        if (it.HasChildren) return Drill ? CascadeAction.RequestChildren(l, row) : CascadeAction.None;
        return CascadeAction.Activate(l, row);
    }

    /// <summary>The pressed-row value that stands for the Back band (never a row index).</summary>
    private const int BackRow = -2;

    /// <summary>Wheel scrolls the level under the last pointer position (else the deepest) one row per notch.</summary>
    public bool MouseWheel(int dy)
    {
        if (!Open) return false;
        int l = LevelAt(_mx, _my);
        if (l < 0) l = Depth - 1;
        bool moved = _lv[l].List.MouseWheel(dy, ItemH);
        _lv[l].Hover = -1;   // the rows moved under a still pointer; the next motion re-hovers
        if (_dwellLevel == l) ResetDwell();
        return moved;
    }

    /// <summary>Press: outside every level closes (CloseAll); the gutter scrolls; a row yields Context for the
    /// right button, RequestChildren for an enabled submenu row, else None -- a left press arms the row for a
    /// drag, and a leaf activates on its release (<see cref="MouseUp"/>). A press on a row other than the one owning
    /// an open child closes that child, unless it is a <see cref="CascadeLevel.DropTarget"/> to drag into.</summary>
    public CascadeAction MouseDown(float mx, float my, int button)
    {
        if (!Open) return CascadeAction.None;
        _mx = mx; _my = my;
        int l = LevelAt(mx, my);
        if (l < 0) { Close(); return CascadeAction.CloseAll; }
        var lv = _lv[l];
        if (lv.List.MouseDown(mx, my)) return CascadeAction.None;
        if (BackAt(mx, my))
        {
            if (button == LeftButton) { _pressLevel = l; _pressRow = BackRow; _pressX = mx; _pressY = my; RowDrag = false; DropLevel = -1; }
            return CascadeAction.None;
        }
        int row = ItemAt(l, mx, my);
        if (row < 0) return CascadeAction.None;
        _dwellDone = true;
        bool childHere = l + 1 < Depth && _lv[l + 1].ParentRow == row;
        if (l + 1 < Depth && !childHere && !_lv[l + 1].DropTarget) CloseTo(l);   // a drop-target child stays for the drag this press may start
        if (button == RightButton) return CascadeAction.Context(l, row);
        if (button == LeftButton) { _pressLevel = l; _pressRow = row; _pressX = mx; _pressY = my; RowDrag = false; DropLevel = -1; }
        ref CascadeItem it = ref lv.Items[row];
        if (it.Disabled || !it.HasChildren || Drill) return CascadeAction.None;
        return childHere ? CascadeAction.None : CascadeAction.RequestChildren(l, row);
    }

    /// <summary>Per-frame poll with the owner's clock: once the hovered row has rested <see cref="DwellMs"/> (0 = at once)
    /// (timed from the first Tick after the hover changed), a sibling child closes and a submenu row asks for
    /// its children, once. Zero-alloc.</summary>
    public CascadeAction Tick(long nowMs)
    {
        if (!Open || Drill || RowDrag || _dwellDone || _dwellLevel < 0 || _dwellRow < 0) return CascadeAction.None;
        if (_dwellSince < 0) { _dwellSince = nowMs; if (DwellMs > 0) return CascadeAction.None; }
        if (nowMs - _dwellSince < DwellMs) return CascadeAction.None;
        _dwellDone = true;
        int l = _dwellLevel, row = _dwellRow;
        if (l >= Depth || row >= _lv[l].Items.Length) return CascadeAction.None;
        if (l + 1 < Depth && _lv[l + 1].ParentRow == row) return CascadeAction.None;
        ref CascadeItem it = ref _lv[l].Items[row];
        bool opens = !it.Disabled && it.HasChildren;
        if (l + 1 < Depth && _lv[l + 1].DropTarget && !opens) return CascadeAction.None;   // resting on a leaf beside a drop target keeps it for the drag
        if (l + 1 < Depth) CloseTo(l);
        return opens ? CascadeAction.RequestChildren(l, row) : CascadeAction.None;
    }

    /// <summary>Keys act on the deepest level: Up/Down move the hover (wrapping, scrolled into view);
    /// Right/Enter open a submenu row or activate a leaf; Left closes one level; Esc closes one level, or the
    /// menu at the root (CloseAll). While a row drags, Esc cancels the drag and every other key is inert.</summary>
    public CascadeAction Key(uint sdlKey)
    {
        if (!Open) return CascadeAction.None;
        if (RowDrag) { if (sdlKey == KeyEscape) ResetPress(); return CascadeAction.None; }
        int l = Depth - 1;
        var lv = _lv[l];
        int n = lv.Items.Length;
        switch (sdlKey)
        {
            case KeyDown:
            case KeyUp:
                if (n == 0) return CascadeAction.None;
                int h = lv.Hover;
                if (sdlKey == KeyDown) h = h < 0 || h + 1 >= n ? 0 : h + 1;
                else h = h <= 0 ? n - 1 : h - 1;
                lv.Hover = h;
                float top = h * ItemH;
                if (top < lv.List.Offset) lv.List.Offset = top;
                else if (top + ItemH > lv.List.Offset + lv.List.H) lv.List.Offset = top + ItemH - lv.List.H;
                lv.List.Clamp();
                ResetDwell();
                return CascadeAction.None;
            case KeyRight:
            case KeyReturn:
            case KeyKpEnter:
                if ((uint)lv.Hover >= (uint)n) return CascadeAction.None;
                ref CascadeItem it = ref lv.Items[lv.Hover];
                if (it.Disabled) return CascadeAction.None;
                return it.HasChildren ? CascadeAction.RequestChildren(l, lv.Hover) : CascadeAction.Activate(l, lv.Hover);
            case KeyLeft:
                if (l > 0) CloseTo(l - 1);
                return CascadeAction.None;
            case KeyEscape:
                if (l > 0) { CloseTo(l - 1); return CascadeAction.None; }
                Close();
                return CascadeAction.CloseAll;
        }
        return CascadeAction.None;
    }

    /// <summary>A 1 px frame just inside the rect, as four strips: a filled rect under the bg would tint a
    /// translucent bg with the frame colour.</summary>
    private static void Outline(VertexBatch b, float x0, float y0, float x1, float y1, (float r, float g, float b, float a) c, float alpha)
    {
        float a = c.a * alpha;
        b.Rect(x0, y0, x1, y0 + 1f, c.r, c.g, c.b, a); b.Rect(x0, y1 - 1f, x1, y1, c.r, c.g, c.b, a);
        b.Rect(x0, y0 + 1f, x0 + 1f, y1 - 1f, c.r, c.g, c.b, a); b.Rect(x1 - 1f, y0 + 1f, x1, y1 - 1f, c.r, c.g, c.b, a);
    }

    /// <summary>Draw every open level, deepest last: body, frame, the owning-row and hover fills, icons noted for
    /// the owner, labels, counts, submenu marks and the scrollbar, each level fading and sliding in over
    /// <see cref="CascadeStyle.AnimMs"/>. With <paramref name="skin"/> a level is the game's own window art
    /// instead of the body fill and frame. Zero-alloc.</summary>
    public void Draw(VertexBatch b, GlyphAtlas atlas, float baseDrop, in CascadeStyle st, Skin? skin = null)
    {
        _iconN = 0;
        if (!Open) return;
        var bg = st.Bg; var active = st.Hover; var frame = st.Frame;
        uint textRgb = st.TextRgb;
        float tr = ((textRgb >> 16) & 0xFF) / 255f, tg = ((textRgb >> 8) & 0xFF) / 255f, tb = (textRgb & 0xFF) / 255f;
        float vpad = (ItemH - atlas.LineHeight) / 2f, inset = IconInset, pad = Pad;
        for (int i = Drill ? Depth - 1 : 0; i < Depth; i++)
        {
            var lv = _lv[i];
            if (lv.OpenedMs < 0) lv.OpenedMs = st.NowMs;
            float e = st.AnimMs <= 0 ? 1f : (st.NowMs - lv.OpenedMs) / (float)st.AnimMs;
            e = e >= 1f ? 1f : e <= 0f ? 0f : 1f - (1f - e) * (1f - e);
            float alpha = st.Alpha * e, ox = i == 0 ? 0f : (Drill ? 1f : -1f) * (1f - e) * SlidePx, oy = i == 0 ? -(1f - e) * SlidePx : 0f;
            float x0 = lv.X + ox, y0 = lv.Y + oy, x1 = x0 + lv.W, y1 = y0 + lv.H, band = lv.HasBack ? ItemH : 0f;
            lv.List.Native = skin != null;
            if (skin != null) skin.Draw(NativeChrome.Frame, x0 - 1f, y0 - band - 1f, x1 + 1f, y1 + 1f, NativeChrome.White(alpha));
            else
            {
                b.Rect(x0, y0 - band, x1, y1, bg.r, bg.g, bg.b, bg.a * alpha);
                Outline(b, x0 - 1f, y0 - band - 1f, x1 + 1f, y1 + 1f, frame, alpha);
            }
            float fillR = x1 - (lv.List.Overflows ? lv.List.Width : 0f), textClip = fillR - pad;
            float iconCol = lv.HasIcons ? ItemH : 0f;
            bool hasMark = atlas.TryGet(lv.Mark, out Glyph mg);
            float markW = hasMark ? mg.Advance : 0f;
            if (lv.HasBack)
            {
                if (lv.BackHover) b.Rect(x0, y0 - band, x1, y0, active.r, active.g, active.b, active.a * 0.55f * alpha);
                float bl = y0 - band + baseDrop + vpad, a = st.TextAlpha * alpha, bx = x0 + pad;
                if (atlas.TryGet(BackMarkOf(atlas), out Glyph bm)) { b.Glyph(bm, bx, bl, tr, tg, tb, a); bx += bm.Advance + pad; }
                UiText.Draw(b, atlas, lv.BackLabel, bx, bl, textRgb, textClip, a);
                b.Rect(x0, y0 - 1f, x1, y0, frame.r, frame.g, frame.b, frame.a * alpha);
            }
            int owning = i + 1 < Depth ? _lv[i + 1].ParentRow : -1;
            b.ClipY(y0, y1);
            for (int r = 0; r < lv.Items.Length; r++)
            {
                float top = lv.List.ToScreen(r * ItemH) + oy;
                if (top + ItemH <= y0 || top >= y1) continue;
                ref CascadeItem it = ref lv.Items[r];
                if (r == owning) b.Rect(x0, top, fillR, top + ItemH, active.r, active.g, active.b, active.a * alpha);
                else if (r == lv.Hover) b.Rect(x0, top, fillR, top + ItemH, active.r, active.g, active.b, active.a * 0.55f * alpha);
                float a = (it.Disabled ? DisabledAlpha : 1f) * st.TextAlpha * alpha;
                if (RowDrag && i == _pressLevel && r == _pressRow) a *= DragSourceAlpha;
                // Icons draw unclipped in the owner's pass, so only a square wholly inside the band is reported.
                if (iconCol > 0f && HasIcon(in it) && top >= y0 && top + ItemH <= y1 && _iconN < _icons.Length)
                    _icons[_iconN++] = new IconQuad(it.Icon, x0 + inset, top + inset, ItemH - 2f * inset, a, it.IconHandle);
                float baseline = top + baseDrop + vpad, right = textClip;
                if (it.HasChildren && hasMark) { right -= markW; b.Glyph(mg, right, baseline, tr, tg, tb, a); right -= pad; }
                float labelX = x0 + iconCol + pad;
                if (!string.IsNullOrEmpty(it.Count))
                {
                    float cw = UiText.Measure(atlas, it.Count);
                    if (cw + pad <= right - labelX - UiText.Measure(atlas, it.Label ?? ""))   // the count never eats the label: dropped when it would
                    {
                        right -= cw;
                        UiText.Draw(b, atlas, it.Count, right, baseline, textRgb, textClip, a);
                        right -= pad;
                    }
                }
                if (it.Label != null) UiText.Draw(b, atlas, it.Label, labelX, baseline, textRgb, right, a);
            }
            if (RowDrag && i == DropLevel)
            {
                float gy = lv.List.ToScreen(DropIndex * ItemH) + oy, line = GapLinePx * Metrics.Scale;
                if (gy >= y0 - line && gy <= y1 + line) b.Rect(x0, gy - line, fillR, gy + line, tr, tg, tb, st.TextAlpha * alpha);
            }
            b.ClearClip();
            lv.List.Draw(b, active, skin);
        }
        if (RowDrag && _pressLevel < Depth && (uint)_pressRow < (uint)_lv[_pressLevel].Items.Length)
        {
            string? ghost = _lv[_pressLevel].Items[_pressRow].Label;
            if (ghost != null) UiText.Draw(b, atlas, ghost, _mx + GhostDx, _my + baseDrop - atlas.LineHeight / 2f, textRgb, _vpW, GhostAlpha * st.TextAlpha * st.Alpha);
        }
    }
}
