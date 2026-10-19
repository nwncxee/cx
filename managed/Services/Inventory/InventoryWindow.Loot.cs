using System;
using CX.Framework;
using CX.Ui;
using static CX.Ui.PanelChrome;

namespace CX.Services.Inventory;

internal sealed partial class InventoryWindow
{
    // ---- the loot pane: an open container's contents, attached beside the window ----

    /// <summary>The open container, filled by the service; the pane shows while it is open.</summary>
    public readonly LootModel Loot = new();
    public bool LootShown => Open && Loot.Open;
    /// <summary>A <see cref="InvActKind.Take"/>'s cell when the server picks it: the stock container double-click sends
    /// <c>RepositoryMove(item, 0xff, 0xff, INVALID)</c> (<c>CGuiContainer::HandleDoubleClick</c> @0x68c1f0).</summary>
    public const int AutoCell = 0xff;   // not-an-offset: a wire cell
    /// <summary><see cref="InvIcon.Clip"/>: clipped to the backpack grid, or to the loot grid.</summary>
    internal const int ClipGrid = 1, ClipLoot = 2;

    private float _lootTop, _lootTarget;
    private bool _lootScrolling;
    private int _lootMaxRows = LootModel.Rows;
    private float _vw;
    /// <summary>A server's title for every container (<c>inventory.loot.label</c>), empty for the container's own name.</summary>
    private string _lootLabel = "";
    /// <summary>The TLK's Container: the title of a container with no name.</summary>
    private string _containerWord = "";

    private const float LootGridW = LootModel.Cols * Cell, LootGridY = TitleH + Pad;
    private const float LootWDesign = Pad + LootGridW + TabGap + ScrollW + Pad;

    private int LootRows => Loot.Pages * LootModel.Rows;
    /// <summary>Rows in view: all of them up to the window's height, one page at least.</summary>
    private int LootViewRows => Math.Clamp(LootRows, LootModel.Rows, Math.Max(LootModel.Rows, _lootMaxRows));
    private float LootMaxTop => Math.Max(0, LootRows - LootViewRows);
    private float LootW => LootWDesign * S;
    private float LootH => (LootGridY + LootViewRows * Cell + Pad) * S;
    /// <summary>Right of the window when the screen has room there, else left when that has, else whichever side has more.</summary>
    private bool LootRight => _vw <= 0f || X + W + LootW <= _vw || X < LootW && _vw - X - W >= X;
    private float LootX => LootRight ? X + W : X - LootW;
    private float Lx(float d) => LootX + d * S;
    private float Ly(float d) => Y + d * S;

    private bool InLootPane(float px, float py) => LootShown && px >= LootX && px < LootX + LootW && py >= Y && py < Y + LootH;

    private InvHit LootHit(float px, float py)
    {
        float dx = (px - LootX) / S, dy = (py - Y) / S;
        if (dy < TitleH)
            return dx >= LootWDesign - Pad - CloseW && dx < LootWDesign - Pad && dy >= 2f && dy < 2f + CloseW
                ? new(InvHitKind.LootClose) : new(InvHitKind.LootTitle);
        if (dy >= LootGridY && dy < LootGridY + LootViewRows * Cell)
        {
            if (dx >= Pad && dx < Pad + LootGridW)
                return new(InvHitKind.LootCell, -1, Math.Min(LootModel.Cols - 1, (int)((dx - Pad) / Cell)),
                           Math.Min(LootRows - 1, (int)MathF.Floor(_lootTop + (dy - LootGridY) / Cell)));
            if (dx >= Pad + LootGridW + TabGap && dx < Pad + LootGridW + TabGap + BarW / S) return new(InvHitKind.LootScroll);
        }
        return new(InvHitKind.Loot);
    }

    /// <summary>Screen origin of loot cell (<paramref name="cx"/>, global <paramref name="gy"/>), false when all
    /// <paramref name="h"/> rows from it are scrolled out of view.</summary>
    private bool LootCellRect(int cx, int gy, out float x0, out float y0, int h = 1)
    {
        float r = gy - _lootTop;
        x0 = y0 = 0f;
        if (r <= -Math.Max(1, h) || r >= LootViewRows) return false;
        x0 = Lx(Pad + cx * Cell);
        y0 = Ly(LootGridY + r * Cell);
        return true;
    }

    /// <summary>The loot grid's screen rect, in whole px, for the owner's scissor.</summary>
    public void LootClip(out int x, out int y, out int w, out int h)
    {
        x = (int)MathF.Floor(Lx(Pad)); y = (int)MathF.Floor(Ly(LootGridY));
        w = (int)MathF.Ceiling(Lx(Pad + LootGridW)) - x; h = (int)MathF.Ceiling(Ly(LootGridY + LootViewRows * Cell)) - y;
    }

    /// <summary>The loot scrollbar's thumb follows the pointer, centred on it, without the glide.</summary>
    private void LootScrollTo(float py)
        => _lootTop = _lootTarget = ScrollAt(py, Ly(LootGridY), Ly(LootGridY + LootViewRows * Cell), LootViewRows, LootRows);

    /// <summary>From <see cref="Tick"/>: the loot view glides toward its target, runs while a drag hovers the grid's top or
    /// bottom edge, and stays inside the pages when their count drops.</summary>
    private void LootTick(float dt)
    {
        if (!LootShown) return;
        if (_drag && _mx >= Lx(Pad) && _mx < Lx(Pad + LootGridW))
        {
            float edge = Cell * S / 2f, gy0 = Ly(LootGridY), gy1 = Ly(LootGridY + LootViewRows * Cell);
            int run = _my >= gy0 - edge && _my < gy0 + edge ? -1 : _my > gy1 - edge && _my <= gy1 + edge ? 1 : 0;
            if (run != 0) _lootTarget = _lootTop = Math.Clamp(_lootTop + run * EdgeRowsPerSec * dt, 0f, LootMaxTop);
        }
        _lootTarget = Math.Clamp(_lootTarget, 0f, LootMaxTop);
        _lootTop += (_lootTarget - _lootTop) * (1f - MathF.Exp(-GlideRate * dt));
        if (MathF.Abs(_lootTarget - _lootTop) < 0.005f) _lootTop = _lootTarget;
    }

    /// <summary>The loot pages with a row on screen.</summary>
    public void LootView(out int first, out int last)
    {
        first = (int)_lootTop / LootModel.Rows;
        last = Math.Min(Loot.Pages - 1, ((int)MathF.Ceiling(_lootTop + LootViewRows) - 1) / LootModel.Rows);
    }

    /// <summary>The drop rules over a loot cell, as the stock container grid has them: onto a like stack -> Merge (the grid's
    /// merge ignores the source); a free footprint on a page the server has sent -> Put (<c>CGuiContainer::ProcessItemDrop</c>'s
    /// <c>RepositoryMove</c> to the container); a worn item is refused (the stock grid ignores an equip slot's drag).</summary>
    private InvAction? LootDrop(InvHit h, bool describe)
    {
        uint oid = _dragOid;
        InvItem it = Info(oid);
        if (_dragFromSlot >= 0) return new InvAction(InvActKind.Refused, oid, Text: describe ? "Unequip it first." : "");
        Footprint(h, LootModel.Cols, LootModel.Rows, out int x, out int y);
        int under = Loot.ItemAt(h.X, h.Y);
        if (under >= 0 && Loot.Items[under].Oid != oid && Mergeable(Loot.Items[under], it))
            return new InvAction(InvActKind.Merge, Loot.Items[under].Oid, Other: oid);
        if (Loot.Blank(y / LootModel.Rows)) return new InvAction(InvActKind.Refused, oid, Text: describe ? Loading : "");
        if (!Loot.Fits(x, y, _dragW, _dragH, oid, Loot.Pages)) return new InvAction(InvActKind.Refused, oid, Text: describe ? "No room there." : "");
        if (_dragFromLoot && it.X == x && it.Y == y) return null;
        return new InvAction(InvActKind.Put, oid, X: x, Y: y, Other: Loot.Oid);
    }

    /// <summary>Double-click or Ctrl+click on a loot item: into the backpack wherever the server finds room.</summary>
    private void TakeAuto(uint oid)
    {
        if (!ReadOnly) Act?.Invoke(new InvAction(InvActKind.Take, oid, X: AutoCell, Y: AutoCell, Other: Loot.Oid));
    }

    /// <summary>The pane: frame, title (the container's name) and close button, the grid with a rule and number where each
    /// page begins (a page not sent yet dims under a loader), icons and stack counts, the scrollbar. The grid's geometry goes into
    /// <paramref name="lu"/> / <paramref name="lo"/> and the skin's <see cref="KLootCell"/>, which the owner draws clipped to
    /// <see cref="LootClip"/>. Returns the icon count.</summary>
    private int EmitLoot(VertexBatch under, VertexBatch lu, VertexBatch lo, Skin? skin, GlyphAtlas text, GlyphAtlas small, InvIcon[] icons, int n)
    {
        if (!LootShown) return n;
        float s = S, x0 = LootX, y0 = Y, x1 = x0 + LootW, y1 = y0 + LootH, lh = text.LineHeight;
        if (skin != null) { skin.Draw(NativeChrome.Frame, x0, y0, x1, y1); skin.Draw(NativeChrome.Header, x0, y0, x1, Ly(TitleH)); }
        else { Fill(under, x0, y0, x1, y1, PanelBg, 0.97f); Outline(under, x0, y0, x1, y1, Border); Fill(under, x0 + 1f, y0 + 1f, x1 - 1f, Ly(TitleH), HeaderBg); }
        float bx0 = Lx(LootWDesign - Pad - CloseW);
        CloseButton(under, skin, text, bx0, Ly(2f), Lx(LootWDesign - Pad), Ly(2f + CloseW));
        string title = _lootLabel.Length > 0 ? _lootLabel : Loot.Title.Length > 0 ? Loot.Title : _containerWord;
        UiText.Draw(under, text, title, Lx(Pad), y0 + (TitleH * s - lh) / 2f + text.Ascent, Tx, bx0 - 2f * s);

        float c = Cell * s, lw = Math.Max(1f, MathF.Round(s)), gx0 = Lx(Pad), gx1 = Lx(Pad + LootGridW), gy0 = Ly(LootGridY);
        int first = (int)MathF.Floor(_lootTop), last = Math.Min(LootRows - 1, (int)MathF.Ceiling(_lootTop + LootViewRows) - 1);
        for (int gy = first; gy <= last; gy++)
        {
            bool read = !Loot.Blank(gy / LootModel.Rows);
            for (int cx = 0; cx < LootModel.Cols; cx++)
            {
                LootCellRect(cx, gy, out float qx, out float qy);
                if (skin != null) skin.Draw(KLootCell, qx, qy, qx + c, qy + c, read ? 0xffffffffu : BlankTint);   // not-an-offset: an rgba tint
                else Fill(lu, qx + 1f, qy + 1f, qx + c, qy + c, Input, read ? 1f : 0.35f);
            }
        }
        if (Loot.Pages > 1)
        {
            for (int gy = first; gy <= last; gy++)
            {
                if (gy % LootModel.Rows != 0 || gy == first && _lootTop == first) continue;
                LootCellRect(0, gy, out _, out float py0);
                Fill(lo, gx0, py0 - lw, gx1, py0 + lw, Accent, 0.85f);
                PageTag(lo, small, CountText(gy / LootModel.Rows + 1), gx0, py0 + lw, s);
            }
            PageTag(lo, small, CountText(first / LootModel.Rows + 1), gx0, gy0, s);
        }
        for (int i = 0; i < Loot.Count && n < icons.Length; i++)
        {
            ref InvItem it = ref Loot.Items[i];
            if (_drag && it.Oid == _dragOid) continue;
            if (!LootCellRect(it.X, it.Y, out float ix, out float iy, it.H)) continue;
            float iw = Math.Max(1, it.W) * c, ih = Math.Max(1, it.H) * c;
            icons[n++] = new InvIcon(it.Oid, ix, iy, iw, ih, ItemAlpha(it), it.W, it.H, ClipLoot);
            StackCount(lo, small, it, ix + iw, iy + ih, s);
        }
        for (int p = first / LootModel.Rows; p <= last / LootModel.Rows; p++)
            if (Loot.Blank(p)) PageLoader(lo, p * LootModel.Rows, LootModel.Rows, _lootTop, LootViewRows, gx0, gx1, gy0, s);
        if (skin != null) EdgeRB(under, gx0, gy0, gx1, Ly(LootGridY + LootViewRows * Cell), s);
        float sx0 = Lx(Pad + LootGridW + TabGap);
        Scrollbar(under, skin, sx0, gy0, sx0 + BarW, Ly(LootGridY + LootViewRows * Cell), _lootTop, LootViewRows, LootRows, _lootScrolling);
        return n;
    }

    /// <summary>Drag feedback and the hover outline over the loot grid (the owner's <see cref="EmitOver"/> calls it for a
    /// <see cref="InvHitKind.LootCell"/> hit).</summary>
    private void LootOver(VertexBatch lo, InvHit h)
    {
        float s = S;
        if (_drag)
        {
            InvAction? a = LootDrop(h, describe: false);
            Footprint(h, LootModel.Cols, LootModel.Rows, out int x, out int y);
            if (a is { Kind: InvActKind.Merge }) { int u = Loot.ItemAt(h.X, h.Y); x = Loot.Items[u].X; y = Loot.Items[u].Y; }
            uint rgb = a is { Kind: InvActKind.Refused } ? Warn : Ok;
            for (int dy = 0; dy < _dragH; dy++)
                for (int dx = 0; dx < _dragW; dx++)
                    if (x + dx < LootModel.Cols && (y + dy) / LootModel.Rows == y / LootModel.Rows && LootCellRect(x + dx, y + dy, out float cx0, out float cy0))
                        Fill(lo, cx0 + 1f, cy0 + 1f, cx0 + Cell * s, cy0 + Cell * s, rgb, 0.35f);
            return;
        }
        if (Loot.ItemAt(h.X, h.Y) is int i and >= 0 && LootCellRect(Loot.Items[i].X, Loot.Items[i].Y, out float ix, out float iy, Loot.Items[i].H))
            Outline(lo, ix, iy, ix + Math.Max(1, Loot.Items[i].W) * Cell * s, iy + Math.Max(1, Loot.Items[i].H) * Cell * s, Hi, 0.8f);
    }
}
