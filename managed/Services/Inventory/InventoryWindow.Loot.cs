using System;
using System.Diagnostics;
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

    /// <summary>The Take all button's design span in the loot title bar, left of the close button.</summary>
    private const float TakeAllX1 = LootWDesign - Pad - CloseW - Gap, TakeAllX0 = TakeAllX1 - 60f;
    /// <summary>Take all's pace: no two takes closer than a player clicking.</summary>
    private const float TakePaceSec = 0.5f;
    /// <summary>How long Take all waits on the server's confirmation of a take before it stops.</summary>
    private const float TakeWaitSec = 3f;
    /// <summary>The Take all button shows: <c>inventory.takeall</c> on and the window not read-only.</summary>
    public bool TakeAllOn { get; private set; }
    /// <summary>The container Take all is emptying, INVALID when it is not running; the take in flight, INVALID for none.</summary>
    private uint _takeFrom = InventoryModel.Invalid, _takeOid = InventoryModel.Invalid;
    /// <summary>When <see cref="_takeFrom"/> opened: the same container closed and opened again is a new run.</summary>
    private long _takeOpened;
    private long _takeSentAt, _takeNext;
    private int _taken;

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
        {
            if (dy < 2f || dy >= 2f + CloseW) return new(InvHitKind.LootTitle);
            if (dx >= LootWDesign - Pad - CloseW && dx < LootWDesign - Pad) return new(InvHitKind.LootClose);
            return TakeAllOn && dx >= TakeAllX0 && dx < TakeAllX1 ? new(InvHitKind.LootTakeAll) : new(InvHitKind.LootTitle);
        }
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

    /// <summary>The loot pane's title: the server's label, else the container's name with its colour codes stripped (once per
    /// name: the draw asks every frame), else the TLK's word.</summary>
    private string LootTitle()
    {
        if (_lootLabel.Length > 0) return _lootLabel;
        if (Loot.Title.Length == 0) return _containerWord;
        if (!ReferenceEquals(Loot.Title, _titleRaw)) { _titleRaw = Loot.Title; _titlePlain = Nameplate.PlateLayout.StripColours(_titleRaw, out _); }
        return _titlePlain;
    }

    private string _titleRaw = "", _titlePlain = "";
    private const string TakeAllText = "Take all";

    /// <summary>Double-click or Ctrl+click on a loot item: into the backpack wherever the server finds room.</summary>
    private void TakeAuto(uint oid)
    {
        if (!ReadOnly) Act?.Invoke(new InvAction(InvActKind.Take, oid, X: AutoCell, Y: AutoCell, Other: Loot.Oid));
    }

    /// <summary>Take all runs on the container open now: the opening it started on (a tick no other opening shares).</summary>
    private bool TakingHere => _takeFrom != InventoryModel.Invalid && _takeOpened == Loot.OpenedAt;

    /// <summary>Start Take all on the open container; a press while it runs changes nothing.</summary>
    private void StartTakeAll()
    {
        if (TakingHere) return;
        _takeFrom = Loot.Oid; _takeOpened = Loot.OpenedAt; _takeOid = InventoryModel.Invalid; _taken = 0; _takeNext = 0;
        TakeAllTick(Clock());
    }

    /// <summary>Per frame while Take all runs: count the take the server confirmed (the item left the container), then send
    /// the next once the pace allows, asking for a page not yet sent; stop on a close, a full backpack or a silent server.</summary>
    private void TakeAllTick(long now)
    {
        if (_takeFrom == InventoryModel.Invalid) return;
        if (!TakeAllOn || !TakingHere) { _takeFrom = InventoryModel.Invalid; return; }
        long freq = Stopwatch.Frequency;
        if (_takeOid != InventoryModel.Invalid)
        {
            if (Loot.Find(_takeOid) >= 0)
            {
                if (now - _takeSentAt >= (long)(TakeWaitSec * freq)) TakeAllStopped();
                return;
            }
            _taken++; _takeOid = InventoryModel.Invalid;
        }
        if (now < _takeNext) return;
        int next = -1;
        for (int i = 0; i < Loot.Count; i++)
        {
            ref InvItem it = ref Loot.Items[i];
            if (next < 0 || it.Y < Loot.Items[next].Y || it.Y == Loot.Items[next].Y && it.X < Loot.Items[next].X) next = i;
        }
        if (next < 0)
        {
            for (int p = 0; p < Loot.Pages; p++) if (Loot.Blank(p)) { Loot.Touch(p); return; }
            _takeFrom = InventoryModel.Invalid;
            return;
        }
        if (BackpackFull(Loot.Items[next].W, Loot.Items[next].H)) { TakeAllStopped(); return; }
        _takeOid = Loot.Items[next].Oid; _takeSentAt = now; _takeNext = now + (long)(TakePaceSec * freq);
        TakeAuto(_takeOid);
    }

    /// <summary>No backpack page has room for a <paramref name="w"/>x<paramref name="h"/> item, and every page is known.</summary>
    private bool BackpackFull(int w, int h)
    {
        if (Model.FirstFree(w, h, 0, PageLimit, out _, out _)) return false;
        for (int p = 0; p < PageLimit; p++) if (Model.Blank(p)) return false;
        return true;
    }

    /// <summary>Take all stops short, and says how far it got.</summary>
    private void TakeAllStopped()
    {
        Act?.Invoke(new InvAction(InvActKind.Refused, Text: $"Take all stopped: {_taken} taken, the rest stay in the container."));
        _takeFrom = InventoryModel.Invalid;
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
        if (TakeAllOn)
        {
            float tx0 = Lx(TakeAllX0), ty0 = Ly(2f), tx1 = Lx(TakeAllX1), ty1 = Ly(2f + CloseW);
            bool on = _takeFrom != InventoryModel.Invalid, hot = _mx >= tx0 && _mx < tx1 && _my >= ty0 && _my < ty1;
            if (skin != null) skin.Draw(ButtonFace(on, hot), tx0, ty0, tx1, ty1);
            else { Fill(under, tx0, ty0, tx1, ty1, on ? NavOn : hot ? Frame2 : CatBg); Outline(under, tx0, ty0, tx1, ty1, ColBorder); }
            UiText.Draw(under, small, TakeAllText, (tx0 + tx1 - UiText.Measure(small, TakeAllText)) / 2f, ty0 + (ty1 - ty0 - small.LineHeight) / 2f + small.Ascent,
                        on ? Hi : Tx2, tx1);
            bx0 = tx0;
        }
        string title = LootTitle();
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
