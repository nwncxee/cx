using System;
using System.Diagnostics;
using CX.Framework;
using CX.Ui;
using static CX.Ui.PanelChrome;

namespace CX.Services.Inventory;

internal sealed partial class InventoryWindow
{
    // ---- the game-art skin ----

    /// <summary>The inventory's own skin parts, after <see cref="NativeChrome"/>'s common chrome.</summary>
    internal const int KCell = NativeChrome.Count, KSlot = KCell + 1, KGold = KSlot + 1, KGoldHot = KGold + 1,
                       KGoldCursor = KGoldHot + 1, KLootCell = KGoldCursor + 1, KSil = KLootCell + 1;

    /// <summary>The inventory's skin: the native chrome, then its own art -- the 32x32 cell tile (twice: backpack and loot
    /// cells are scissored apart), the gold button's two faces, the coin cursor and the 14 silhouettes. ponytail: the cell tile's 4-texel slot frame is read off the art, not the MDL.</summary>
    internal static Skin NewSkin()
    {
        var extra = new SkinPart[KSil - NativeChrome.Count + InventoryModel.Slots.Length];
        int k = 0;
        extra[k++] = new("cell", "gui_inv_bkgnd2");
        extra[k++] = new("slot", "gui_inv_bkgnd2", SlotArtInset, SlotArtInset, SlotArtInset, SlotArtInset);
        extra[k++] = new("gold", "ctl_btn_jnl_inv", SrcX: GoldArtX, SrcY: GoldArtY, SrcW: GoldArtSize, SrcH: GoldArtSize);
        extra[k++] = new("goldhot", "ctl_btn_jnl_inv", SrcX: GoldArtX, SrcY: GoldArtY + GoldArtSize, SrcW: GoldArtSize, SrcH: GoldArtSize);
        extra[k++] = new("goldcursor", "ir_buy");
        extra[k++] = new("lootcell", "gui_inv_bkgnd2");
        for (int i = 0; i < InventoryModel.Slots.Length; i++)
            extra[k++] = new("sil." + InventoryModel.Slots[i].Id, InventoryModel.Slots[i].Silhouette);
        return NativeChrome.NewSkin(extra);
    }

    /// <summary>The cell tile's own frame in texels, drawn 1:1: what art inside a slot insets by.</summary>
    private const int SlotArtInset = 4;
    /// <summary>The window art's nine-slice border, drawn 1:1: what a part inside a framed rect insets by.</summary>
    internal const float FrameInset = 3f;

    /// <summary>The tint of a native part that lights when hovered: the header label's grey idle, white hot.</summary>
    internal static uint ArtTint(bool hot) => Theme.Rgba(hot ? Role.Hi : Role.Tx2);

    /// <summary>The native button face: pressed or not, hovered or not, as NUI's selectable has all four.</summary>
    internal static int ButtonFace(bool on, bool hot)
        => on ? hot ? NativeChrome.ButtonOnHot : NativeChrome.ButtonOn : hot ? NativeChrome.ButtonHot : NativeChrome.Button;

    /// <summary>The stock gold button's face in <c>ctl_btn_jnl_inv</c> (128x128), texels from the top-left: the UVs of
    /// <c>ctl_inv_btn_gold.mdl</c>'s <c>Plane234</c> (u 0.6953..0.9922 after the wrap; v 0.2969..0.5938 up, 0..0.2969 the
    /// gold-framed hilite/down state below it).</summary>
    private const int GoldArtX = 89, GoldArtY = 52, GoldArtSize = 38;

    /// <summary>The line colour of the <c>gui_inv_bkgnd2</c> tile (sampled off a live frame): the tile draws only its top
    /// and left edge, so the grid's and the equip area's right and bottom edges are drawn in it.</summary>
    private const uint CellLine = 0x333333;   // not-an-offset: a colour
    private static readonly string[] TabText = { "I", "II", "III", "IV", "V", "VI" };
    /// <summary>Stack counts drawn as numbers; larger ones read "999+".</summary>
    private const int MaxCountText = 999;
    private static readonly string[] Counts = new string[MaxCountText + 1];
    private string _goldText = "", _weightText = "";
    private int _goldFor = int.MinValue, _weightFor = int.MinValue;
    private string _maxFor = "";

    /// <summary>Kilograms in a pound (the international avoirdupois pound).</summary>
    private const double KgPerLb = 0.45359237;
    /// <summary>The weight line: carried (<paramref name="tenths"/> of a pound, the creature field) over the stock label's max
    /// (<paramref name="maxLb"/>, the engine's <c>"%d"</c> pounds), in pounds as the stock panel rounds them, or in kilograms
    /// to one decimal when <paramref name="kg"/> (the max too, parsed back from the label; an unparsable one shows as is).
    /// Cold: on a change only.</summary>
    internal static string WeightText(int tenths, string maxLb, bool kg, string lbLabel)
    {
        var inv = System.Globalization.CultureInfo.CurrentCulture;
        if (!kg)
        {
            int cur = (int)MathF.Floor(tenths / 10f + 0.5f);
            return maxLb.Length > 0 ? $"{cur} / {maxLb} {lbLabel}" : $"{cur} {lbLabel}";
        }
        string curKg = (tenths / 10.0 * KgPerLb).ToString("0.0", inv);
        if (maxLb.Length == 0) return $"{curKg} kg";
        string maxKg = int.TryParse(maxLb, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int m)
            ? (m * KgPerLb).ToString("0.0", inv) : maxLb;
        return $"{curKg} / {maxKg} kg";
    }

    private static string CountText(int n)
    {
        if ((uint)n > MaxCountText) return "999+";
        return Counts[n] ??= n.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Emit one frame. <paramref name="skin"/> is null for the flat look (or a refused skin): fills go into
    /// <paramref name="under"/> instead, drawn after the skin and before the icons, <paramref name="over"/> after them;
    /// the grid's geometry goes into <paramref name="gridUnder"/>/<paramref name="gridOver"/> (clipped to
    /// <see cref="GridClip"/>), the loot pane's into <paramref name="lootUnder"/>/<paramref name="lootOver"/> (<see cref="LootClip"/>). Returns the icon count; allocates nothing once warm.</summary>
    public int Emit(VertexBatch under, VertexBatch over, VertexBatch gridUnder, VertexBatch gridOver, VertexBatch lootUnder, VertexBatch lootOver,
                    Skin? skin, GlyphAtlas text, GlyphAtlas small, InvIcon[] icons)
    {
        if (!Open) return 0;
        int n = 0;
        float s = S;
        float x0 = X, y0 = Y, x1 = X + W, y1 = Y + H;
        float lh = text.LineHeight, slh = small.LineHeight;

        if (skin != null) skin.Draw(NativeChrome.Frame, x0, y0, x1, y1);
        else { Fill(under, x0, y0, x1, y1, PanelBg, 0.97f); Outline(under, x0, y0, x1, y1, Border); }

        if (_title.On)
        {
            if (skin != null) skin.Draw(NativeChrome.Header, Sx(_title.X0), Sy(_title.Y0), Sx(_title.X1), Sy(_title.Y1));
            else Fill(under, Sx(_title.X0) + 1f, Sy(_title.Y0) + 1f, Sx(_title.X1) - 1f, Sy(_title.Y1), HeaderBg);
            UiText.Draw(under, text, _title.Label, Sx(_title.X0 + Pad), Mid(_title, lh, text.Ascent), Tx, SearchOn && _search.On ? Sx(_search.X0) : Sx(_close.X0));
        }
        if (_close.On) CloseButton(under, skin, text, Sx(_close.X0), Sy(_close.Y0), Sx(_close.X1), Sy(_close.Y1));
        if (SearchOn && _search.On)
        {
            float sx0 = Sx(_search.X0), sy0 = Sy(_search.Y0), sx1 = Sx(_search.X1), sy1 = Sy(_search.Y1);
            if (skin != null) skin.Draw(NativeChrome.Edit, sx0, sy0, sx1, sy1);
            else { Fill(under, sx0, sy0, sx1, sy1, Input); Outline(under, sx0, sy0, sx1, sy1, SearchFocused ? Accent2 : Frame); }
            float base_ = sy0 + (sy1 - sy0 - slh) / 2f + small.Ascent;
            float end = Search.Length > 0 ? UiText.Draw(under, small, Search, sx0 + 4f * s, base_, Tx, sx1 - 4f * s)
                                          : UiText.Draw(under, small, _search.Label, sx0 + 4f * s, base_, Tx3, sx1 - 4f * s);
            if (SearchFocused)
            {
                float cx = Search.Length > 0 ? end + 1f : sx0 + 4f * s;
                Fill(under, cx, sy0 + 3f * s, cx + Math.Max(1f, s), sy1 - 3f * s, Hi);
            }
        }

        for (int i = 0; i < _slots.Length; i++)
        {
            ref Box b = ref _slots[i];
            if (!b.On) continue;
            float bx0 = Sx(b.X0), by0 = Sy(b.Y0), bx1 = Sx(b.X1), by1 = Sy(b.Y1);
            InvItem held = Model.Equipped[i];
            if (skin != null) skin.Draw(KSlot, bx0, by0, bx1, by1);
            else { Fill(under, bx0, by0, bx1, by1, ListBg); Outline(under, bx0, by0, bx1, by1, Frame); }
            if (i == Chest && _ac.On) by1 = Math.Min(by1, Sy(_ac.Y0));
            if (held.Oid == InventoryModel.Invalid)
            {
                if (skin != null) FitDraw(skin, KSil + i, bx0, by0, bx1, by1);
                else UiText.Draw(under, small, b.Label, bx0 + 3f * s, by0 + 2f * s + small.Ascent, Tx3, bx1 - 2f * s);
            }
            if (held.Oid != InventoryModel.Invalid && !(_drag && held.Oid == _dragOid) && n < icons.Length)
            {
                float iw = Math.Max(1, held.W) * Cell * s, ih = Math.Max(1, held.H) * Cell * s;
                float k = Math.Min(1f, Math.Min((bx1 - bx0 - 4f * s) / iw, (by1 - by0 - 4f * s) / ih));
                iw *= k; ih *= k;
                icons[n++] = new InvIcon(held.Oid, (bx0 + bx1 - iw) / 2f, (by0 + by1 - ih) / 2f, iw, ih, ItemAlpha(held), held.W, held.H, 0);
            }
        }

        if (_grid.On)
        {
            float c = Cell * s, lw = Math.Max(1f, MathF.Round(s)), gx0 = Sx(_grid.X0), gx1 = Sx(_grid.X1);
            int first = (int)MathF.Floor(Top), last = Math.Min(PageLimit * InventoryModel.Rows - 1, (int)MathF.Ceiling(Top + ViewRows) - 1);
            for (int gy = first; gy <= last; gy++)
            {
                bool read = !Model.Blank(gy / InventoryModel.Rows);
                for (int cx = 0; cx < InventoryModel.Cols; cx++)
                {
                    CellRect(cx, gy, out float cx0, out float cy0);
                    if (skin != null) skin.Draw(KCell, cx0, cy0, cx0 + c, cy0 + c, read ? 0xffffffffu : BlankTint);
                    else Fill(gridUnder, cx0 + 1f, cy0 + 1f, cx0 + c, cy0 + c, Input, read ? 1f : 0.35f);
                }
            }
            for (int p = first / InventoryModel.Rows; p <= last / InventoryModel.Rows; p++)
                if (Model.Blank(p)) PageLoader(gridOver, p * InventoryModel.Rows, InventoryModel.Rows, Top, ViewRows, gx0, gx1, Sy(_grid.Y0), s);
            if (Scrolled)
            {
                for (int gy = first; gy <= last; gy++)
                {
                    if (gy % InventoryModel.Rows != 0 || gy == first && Top == first) continue;
                    CellRect(0, gy, out _, out float py0);
                    Fill(gridOver, gx0, py0 - lw, gx1, py0 + lw, Accent, 0.85f);
                    PageTag(gridOver, small, TabText[gy / InventoryModel.Rows], gx0, py0 + lw, s);
                }
                PageTag(gridOver, small, TabText[first / InventoryModel.Rows], gx0, Sy(_grid.Y0), s);
            }
            for (int i = 0; i < Model.Count && n < icons.Length; i++)
            {
                ref InvItem it = ref Model.Items[i];
                if (_drag && it.Oid == _dragOid) continue;
                if (!CellRect(it.X, it.Y, out float ix, out float iy, it.H)) continue;
                float iw = Math.Max(1, it.W) * c, ih = Math.Max(1, it.H) * c;
                icons[n++] = new InvIcon(it.Oid, ix, iy, iw, ih, ItemAlpha(it), it.W, it.H, ClipGrid);
                StackCount(gridOver, small, it, ix + iw, iy + ih, s);
            }
        }
        if (skin != null)
        {
            if (_grid.On) EdgeRB(under, Sx(_grid.X0), Sy(_grid.Y0), Sx(_grid.X1), Sy(_grid.Y1), s);
            if (_equip.On) EdgeRB(under, Sx(_equip.X0), Sy(_equip.Y0), Sx(_equip.X1), Sy(_equip.Y1), s);
        }
        for (int i = 0; i < _slots.Length; i++)
        {
            InvItem held = Model.Equipped[i];
            if (!_slots[i].On || !held.Stackable || held.Stack < 2) continue;
            string t = CountText(held.Stack);
            UiText.Draw(over, small, t, Sx(_slots[i].X1) - UiText.Measure(small, t) - 3f * s, Sy(_slots[i].Y1) - 3f * s - slh + small.Ascent, Hi, float.MaxValue);
        }

        if (_tabs.On)
            for (int t = 0; t < PageLimit; t++)
            {
                float tx0 = Sx(_tabs.X0), ty0 = Sy(_tabs.Y0 + t * Cell), tx1 = Sx(_tabs.X1), ty1 = Sy(_tabs.Y0 + (t + 1) * Cell) - 1f;
                bool on = t == Page, hot = _mx >= tx0 && _mx < tx1 && _my >= ty0 && _my < ty1;
                if (skin != null) skin.Draw(ButtonFace(on, hot), tx0, ty0, tx1, ty1);
                else { Fill(under, tx0, ty0, tx1, ty1, on ? NavOn : hot ? Frame2 : CatBg); Outline(under, tx0, ty0, tx1, ty1, ColBorder); }
                string lbl = TabText[t];
                UiText.Draw(under, small, lbl, (tx0 + tx1 - UiText.Measure(small, lbl)) / 2f, ty0 + (ty1 - ty0 - slh) / 2f + small.Ascent, on ? Hi : Tx2, float.MaxValue);
            }

        if (Scrolled && _scroll.On)
            Scrollbar(under, skin, Sx(_scroll.X0), Sy(_scroll.Y0), Sx(_scroll.X0) + BarW, Sy(_scroll.Y1), Top, ViewRows, PageLimit * InventoryModel.Rows, _scrolling);
        if (_ac.On)
            UiText.Draw(under, small, Model.ArmorClass, (Sx(_ac.X0) + Sx(_ac.X1) - UiText.Measure(small, Model.ArmorClass)) / 2f, Mid(_ac, slh, small.Ascent), Hi, Sx(_ac.X1));
        if (_goldButton.On)
        {
            float bx0 = Sx(_goldButton.X0), by0 = Sy(_goldButton.Y0), bx1 = Sx(_goldButton.X1), by1 = Sy(_goldButton.Y1);
            bool hot = In(_goldButton, _mx, _my);
            if (skin != null) skin.Draw(hot ? KGoldHot : KGold, bx0, by0, bx1, by1);
            else
            {
                Fill(under, bx0, by0, bx1, by1, hot ? Frame2 : CatBg); Outline(under, bx0, by0, bx1, by1, ColBorder);
                float cw = (bx1 - bx0) * 0.5f, ch = (by1 - by0) * 0.12f, cx = (bx0 + bx1 - cw) / 2f, cy = by1 - (by1 - by0) * 0.25f;
                for (int c = 0; c < 3; c++) Fill(under, cx + (c == 1 ? ch : 0f), cy - (c + 1) * ch * 1.6f, cx + cw + (c == 1 ? ch : 0f), cy - (c + 1) * ch * 1.6f + ch, Warn);
            }
        }
        if (_gold.On)
        {
            if (_goldFor != Model.Gold) { _goldFor = Model.Gold; _goldText = $"{Model.Gold:N0} {_gold.Label}"; }
            UiText.Draw(under, text, _goldText, Sx(_gold.X1) - UiText.Measure(text, _goldText), Mid(_gold, lh, text.Ascent), Warn, float.MaxValue);
        }
        if (_weight.On)
        {
            if (_weightFor != Model.WeightTenths || !ReferenceEquals(_maxFor, Model.MaxWeight))
            {
                _weightFor = Model.WeightTenths; _maxFor = Model.MaxWeight;
                _weightText = WeightText(Model.WeightTenths, Model.MaxWeight, false, _weight.Label);
            }
            UiText.Draw(under, text, _weightText, Sx(_weight.X0), Mid(_weight, lh, text.Ascent), Tx2, Sx(_weight.X1));
        }

        n = EmitLoot(under, lootUnder, lootOver, skin, text, small, icons, n);
        EmitOver(over, gridOver, lootOver, small);
        return n;
    }

    /// <summary>A close button over the rect: the NUI X inside the frame's border (brighter when hot), or a flat "x" on the
    /// accent when hot.</summary>
    private void CloseButton(VertexBatch under, Skin? skin, GlyphAtlas text, float x0, float y0, float x1, float y1)
    {
        bool hot = _mx >= x0 && _mx < x1 && _my >= y0 && _my < y1;
        if (skin != null) { skin.Draw(NativeChrome.Close, x0 + FrameInset, y0 + FrameInset, x1 - FrameInset, y1 - FrameInset, ArtTint(hot)); return; }
        if (hot) Fill(under, x0, y0, x1, y1, Accent);
        UiText.Draw(under, text, "x", (x0 + x1 - UiText.Measure(text, "x")) / 2f, y0 + (y1 - y0 - text.LineHeight) / 2f + text.Ascent, Hi, float.MaxValue);
    }

    /// <summary>A vertical scrollbar over the rect: the native bar NUI draws (trough, arrows, tiled thumb), or the flat
    /// track whose thumb spans rows <paramref name="top"/>..+<paramref name="view"/> of <paramref name="total"/>, lit while
    /// hovered or <paramref name="held"/>.</summary>
    private void Scrollbar(VertexBatch under, Skin? skin, float x0, float y0, float x1, float y1, float top, float view, float total, bool held)
    {
        bool hot = held || _mx >= x0 && _mx < x1 && _my >= y0 && _my < y1;
        if (skin != null) { NativeChrome.Scrollbar(skin, x0, y0, x1, y1, top, view, total, hot); return; }
        float ty0 = y0 + (y1 - y0) * top / total, ty1 = y0 + (y1 - y0) * Math.Min(1f, (top + view) / total);
        Fill(under, x0, y0, x1, y1, Track);
        Fill(under, x0 + 1f, ty0, x1 - 1f, ty1, hot ? Accent2 : Accent);
    }

    /// <summary>A stack's count in the bottom-right corner of its icon, whose bottom-right is (<paramref name="x1"/>,
    /// <paramref name="y1"/>): light on a one-pixel black shadow, so it reads over any icon art.</summary>
    private void StackCount(VertexBatch b, GlyphAtlas small, in InvItem it, float x1, float y1, float s)
    {
        if (!it.Stackable || it.Stack < 2) return;
        string t = CountText(it.Stack);
        float a = ItemAlpha(it), sh = Math.Max(1f, MathF.Round(s));
        float x = x1 - UiText.Measure(small, t) - 2f * s - sh, y = y1 - 2f * s - sh - small.LineHeight + small.Ascent;
        UiText.Draw(b, small, t, x + sh, y + sh, 0x000000, float.MaxValue, a);   // not-an-offset: a colour
        UiText.Draw(b, small, t, x, y, Hi, float.MaxValue, a);
    }

    /// <summary>The skin tint of a cell on a page the server has not sent yet.</summary>
    private const uint BlankTint = 0x707070ffu;   // not-an-offset: an rgba tint
    /// <summary>Dots on the loader's ring, and how long it takes to turn once.</summary>
    private const int LoaderDots = 10;
    private const float LoaderTurnSec = 0.9f;

    /// <summary>A loader over a page the server has not sent yet: centred on the visible part of its <paramref name="rows"/>
    /// rows from global row <paramref name="row0"/>, in a grid showing <paramref name="viewRows"/> rows from
    /// <paramref name="top"/> across <paramref name="x0"/>..<paramref name="x1"/> below <paramref name="y0"/>.</summary>
    private static void PageLoader(VertexBatch b, int row0, int rows, float top, float viewRows, float x0, float x1, float y0, float s)
    {
        float c = Cell * s;
        float r0 = Math.Max(row0, top), r1 = Math.Min(row0 + rows, top + viewRows);
        if (r1 <= r0) return;
        Spinner(b, (x0 + x1) / 2f, y0 + ((r0 + r1) / 2f - top) * c, 0.4f * c, s);
    }

    /// <summary>A ring of <see cref="LoaderDots"/> dots of radius <paramref name="r"/> round (<paramref name="cx"/>,
    /// <paramref name="cy"/>): a bright head sweeping clockwise, the tail shrinking and fading behind it.</summary>
    private static void Spinner(VertexBatch b, float cx, float cy, float r, float s)
    {
        double turns = Clock() / (double)Stopwatch.Frequency / LoaderTurnSec;
        float head = (float)(turns - Math.Floor(turns)) * MathF.Tau, dot = Math.Max(1.5f, 2.2f * s);
        for (int i = 0; i < LoaderDots; i++)
        {
            float k = 1f - i / (float)LoaderDots, ang = head - i * MathF.Tau / LoaderDots, d = dot * (0.45f + 0.55f * k);
            float x = cx + MathF.Sin(ang) * r, y = cy - MathF.Cos(ang) * r;
            Fill(b, x - d, y - d, x + d, y + d, Accent, 0.1f + 0.9f * k);
        }
    }

    /// <summary>The grid's screen rect, in whole px, for the owner's scissor.</summary>
    public void GridClip(out int x, out int y, out int w, out int h)
    {
        x = (int)MathF.Floor(Sx(_grid.X0)); y = (int)MathF.Floor(Sy(_grid.Y0));
        w = (int)MathF.Ceiling(Sx(_grid.X1)) - x; h = (int)MathF.Ceiling(Sy(_grid.Y1)) - y;
    }

    /// <summary>A scrolled view's page hint <paramref name="t"/> (the tab's numeral; a loot page's number) on a dark plate at a
    /// divider (or pinned to the top for the page the view starts in).</summary>
    private static void PageTag(VertexBatch b, GlyphAtlas small, string t, float x, float y, float s)
    {
        float w = UiText.Measure(small, t) + 8f * s;
        Fill(b, x, y, x + w, y + small.LineHeight + 2f * s, PanelBg, 0.85f);
        UiText.Draw(b, small, t, x + 4f * s, y + s + small.Ascent, Accent, float.MaxValue);
    }

    /// <summary>Drag feedback (the footprint under the pointer, green where it lands, red where it cannot; the slots that take
    /// the item lit, as <c>ShowValidSlots</c> does), the hover outline, and the tooltip; over the loot grid,
    /// <see cref="LootOver"/>.</summary>
    private void EmitOver(VertexBatch over, VertexBatch gridOver, VertexBatch lootOver, GlyphAtlas small)
    {
        float s = S;
        if (_drag)
        {
            InvItem it = Info(_dragOid);
            for (int i = 0; i < _slots.Length; i++)
                if (_slots[i].On && (it.SlotMask & InventoryModel.Slots[i].Mask) != 0)
                    Fill(over, Sx(_slots[i].X0), Sy(_slots[i].Y0), Sx(_slots[i].X1), Sy(_slots[i].Y1), Ok, 0.18f);
            InvHit h = HitTest(_mx, _my);
            if (h.Kind == InvHitKind.LootCell) LootOver(lootOver, h);
            else if (h.Kind == InvHitKind.Cell)
            {
                InvAction? a = Drop(h, true, describe: false);
                Footprint(h, InventoryModel.Cols, InventoryModel.Rows, out int x, out int y);
                uint rgb = a is { Kind: InvActKind.Refused } ? Warn : Ok;
                if (a is { Kind: InvActKind.Merge }) { int u = Model.ItemAt(h.X, h.Y); x = Model.Items[u].X; y = Model.Items[u].Y; }
                for (int dy = 0; dy < _dragH; dy++)
                    for (int dx = 0; dx < _dragW; dx++)
                        if (x + dx < InventoryModel.Cols && CellRect(x + dx, y + dy, out float cx0, out float cy0) && (y + dy) / InventoryModel.Rows == y / InventoryModel.Rows)
                            Fill(gridOver, cx0 + 1f, cy0 + 1f, cx0 + Cell * s, cy0 + Cell * s, rgb, 0.35f);
            }
            else if (h.Kind == InvHitKind.Slot)
                Outline(over, Sx(_slots[h.Index].X0), Sy(_slots[h.Index].Y0), Sx(_slots[h.Index].X1), Sy(_slots[h.Index].Y1),
                        Drop(h, true, describe: false) is { Kind: InvActKind.Refused } ? Warn : Ok);
            return;
        }
        if (Contains(_mx, _my))
        {
            InvHit h = HitTest(_mx, _my);
            if (h.Kind == InvHitKind.LootCell) LootOver(lootOver, h);
            else if (h.Kind == InvHitKind.Cell && Model.ItemAt(h.X, h.Y) is int i and >= 0)
            {
                ref InvItem it = ref Model.Items[i];
                if (CellRect(it.X, it.Y, out float ix, out float iy, it.H))
                    Outline(gridOver, ix, iy, ix + Math.Max(1, it.W) * Cell * s, iy + Math.Max(1, it.H) * Cell * s, Hi, 0.8f);
            }
            else if (h.Kind == InvHitKind.Slot)
                Outline(over, Sx(_slots[h.Index].X0), Sy(_slots[h.Index].Y0), Sx(_slots[h.Index].X1), Sy(_slots[h.Index].Y1), Hi, 0.8f);
        }
        _tip.Draw(over, small, small.Ascent, C(SubBg), Tx, Math.Max(X + W, LootShown ? LootX + LootW : 0f) + 400f * s, Y + H + 200f * s);
    }

    /// <summary>The drag ghost: the dragged item's icon under the pointer, where the grabbed cell was.</summary>
    public bool DragIcon(out InvIcon icon)
    {
        icon = default;
        if (!_drag) return false;
        float c = Cell * S;
        icon = new InvIcon(_dragOid, _mx - (_grabCx + 0.5f) * c, _my - (_grabCy + 0.5f) * c, _dragW * c, _dragH * c, 0.85f, _dragW, _dragH, 0);
        return true;
    }

    private float ItemAlpha(in InvItem it)
    {
        if (it.Ghost) return 0.4f;
        if (_dim.Count > 0 && _dim.Contains(it.Oid)) return 0.22f;
        return 1f;
    }

    /// <summary>The right and bottom edge lines the cell tile does not draw, of the screen rect.</summary>
    private static void EdgeRB(VertexBatch under, float x0, float y0, float x1, float y1, float s)
    {
        float lw = Math.Max(1f, MathF.Round(s));
        Fill(under, x1 - lw, y0, x1, y1, CellLine);
        Fill(under, x0, y1 - lw, x1, y1, CellLine);
    }

    private float Mid(in Box b, float lineH, float ascent) => Sy(b.Y0) + ((b.Y1 - b.Y0) * S - lineH) / 2f + ascent;

    /// <summary>A silhouette centred in its box at the art's aspect (the <c>inv_slot_*</c> sheets are padded to powers of
    /// two, so a stretch would distort them), faint.</summary>
    private static void FitDraw(Skin skin, int part, float x0, float y0, float x1, float y1)
    {
        float inset = SlotArtInset, bw = x1 - x0 - 2f * inset, bh = y1 - y0 - 2f * inset, a = skin.Aspect(part);
        if (a <= 0f || bw <= 0f || bh <= 0f) return;
        float w = Math.Min(bw, bh * a), h = w / a;
        float cx = (x0 + x1) / 2f, cy = (y0 + y1) / 2f;
        skin.Draw(part, cx - w / 2f, cy - h / 2f, cx + w / 2f, cy + h / 2f, 0xffffff99u);  // not-an-offset: rgba tint, 60 % alpha
    }
}
