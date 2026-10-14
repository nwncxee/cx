using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Radial;
using CX.Ui;

namespace CX.Services.Inventory;

/// <summary>
/// The CX inventory screen. <c>CPanelInventory::SetInventoryVisible</c> is the one funnel every open and close of the
/// player's own panel goes through -- the key, the quick button, the radial, a server push -- so it is hooked: with
/// <c>inventory.enabled</c> on the vanilla panel never activates and the CX window opens instead. Actions are the
/// engine's own senders, each routed through <see cref="ServerPolicy"/> first (<c>inventory.&lt;action&gt;.route</c>).
/// </summary>
public sealed unsafe partial class InventoryService : IExtenderService
{
    private static ExtenderLog? _log;
    private static Window? _window;
    private static readonly InventoryWindow _inv = new();
    private static readonly Skin _skin = InventoryWindow.NewSkin();
    private static VertexBatch? _under, _over, _gridUnder, _gridOver, _lootUnder, _lootOver;
    /// <summary>Icons a frame draws at most: every backpack item and slot, and the loot pane's visible cells.</summary>
    private static readonly InvIcon[] _icons = new InvIcon[InventoryModel.MaxItems + InventoryModel.Slots.Length + 256];
    private static GlyphAtlas? _text, _small;
    private static float _textPx, _smallPx;
    /// <summary>The typeface the two faces were baked in (<c>inventory.typeface</c>).</summary>
    private static string _typeface = FontRegistry.DefaultName;
    private static int _atlasEpoch = -1, _vw, _vh;
    private static bool _layoutDirty = true, _ready, _masterOff, _facesMissing;

    /// <summary>Design px of the window's two faces, × the window scale.</summary>
    private const float TextPx = 15f, SmallPx = 12f;
    /// <summary>How long a scrolled view rests on pages before the ones not sent yet are asked for: a view sweeping past costs
    /// the server nothing.</summary>
    private const float DwellSec = 0.3f;
    /// <summary>How long an asked-for page may send nothing before it counts as arrived: an empty backpack page or container
    /// sends nothing at all. The server's pass for a player is 200 or 20 ms (<c>GetGameObjectUpdateInterval</c>), doubled for
    /// a client behind, and skipped when that update is over budget -- a page that comes later still draws, only late.</summary>
    private const float QuietSec = 1f;
    private static long Ticks(float sec) => (long)(sec * System.Diagnostics.Stopwatch.Frequency);
    /// <summary><see cref="InventoryNative.BagSeq"/> when the backpack page in flight was asked for.</summary>
    private static int _bagSeqAt;
    /// <summary>A container page asked for and still not arrived after this means its <c>'P'</c> is not reaching the hook:
    /// said once, since its loader would spin on with no other sign.</summary>
    private const float StuckSec = 5f;
    private static bool _stuckSaid;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        UiCategory.Declare();
        ServerPolicy.SelfCheck(log);
        Skin.SelfCheck(log);
        InventoryWindow.SelfCheck(log);
        GoldPrompt.SelfCheck(log);
        if (!InventoryNative.Attach(log, hooks)) { log.Info("InventoryService: stood down -- the stock inventory runs"); return; }
        RadialIcons.Init(log);

        _inv.Act = OnAction;
        _inv.NameOf = InventoryNative.Name;
        ServerPolicy.AllowKnobs(InventoryKnobs.G.Name + ".");
        _window = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            CapturesPointer = true,
            DrawContent = Draw,
            OnMouseButton = (down, button, x, y) => !InventoryNative.EngineModal() && Input(_inv.MouseButton(down, button, x, y, InventoryNative.Mods())),
            OnMouseMotion = (x, y) => !InventoryNative.EngineModal() && Input(_inv.MouseMove(x, y)),
            OnMouseWheel = dy => !InventoryNative.EngineModal() && Input(_inv.MouseWheel(dy)),
            OnKey = (key, mods) => !InventoryNative.EngineModal() && Input(_inv.Key(key, mods)),
            OnText = p => { _inv.Type(Marshal.PtrToStringUTF8(p) ?? ""); SyncKeyboard(); },
            HitTestOverride = (px, py) => _inv.Contains(px, py) && !InventoryNative.EngineModal(),
        };
        InitGold();
        FrameRegistry.Global.Register(FramePhase.Start, 50, BuildModels, "Inventory.models", log);
        _ready = true;
        log.Info($"InventoryService: armed ({(InventoryKnobs.Enabled.V ? "replacing the stock panel" : "off -- inventory.enabled")})");
    }

    /// <summary>Knob hooks: a size / pages / search change re-lays out on the next frame.</summary>
    internal static void Relayout() => _layoutDirty = true;

    /// <summary>The open window's rect, for the other CX windows to snap to.</summary>
    internal static bool SnapRectOut(out (float X, float Y, float W, float H) r)
    {
        r = (_inv.X, _inv.Y, _inv.W, _inv.H);
        return _inv.Open;
    }

    /// <summary>The master flipped: an open CX window closes (and tells the server) on its next frame, so the stock panel
    /// owns the next open. Deferred: a server policy flips knobs from inside the inbound message handler, where a send
    /// would re-enter the live <c>CNWCMessage</c>.</summary>
    internal static void MasterChanged() => _masterOff = !InventoryKnobs.Enabled.V;

    // ---- the funnel ----

    /// <summary>Should this call be ours: the master is on, it is the player's own panel, and about the player. A container
    /// open is one of these (the container message shows the panel before it turns container mode on). Anything else is
    /// the stock panel's.</summary>
    internal static bool Ours(IntPtr panel, int show, uint oid)
    {
        if (!_ready || !InventoryNative.IsMyPanel(panel)) return false;
        return InventoryKnobs.Enabled.V && (oid == InventoryModel.Invalid || oid == InventoryNative.PlayerId());
    }

    /// <summary><c>SetInventoryVisible(show, notify, oid)</c> on the player's panel. A player-initiated open (notify set)
    /// while the CX window is up is the key pressed again -- <c>ToggleDisplay</c> reads the stock panel's visible flag,
    /// which stays down -- so it closes.</summary>
    internal static void OnSetVisible(bool show, bool notify)
    {
        if (!show) { if (_inv.Open) Close(notify); return; }
        if (_inv.Open) { if (notify) Close(notify: true); return; }
        Open();
    }

    /// <summary>A panel call that is not ours: the stock panel is about to show, so ours steps aside (without telling the
    /// server, which the stock panel does).</summary>
    internal static void Yield()
    {
        if (_inv.Open) Close(notify: false);
    }

    private static void Open()
    {
        _layoutDirty = true;
        _skin.Rearm();
        if (!InventoryKnobs.Placed.V) { _inv.X = -1f; _inv.Y = -1f; }
        else { _inv.X = InventoryKnobs.X.V; _inv.Y = InventoryKnobs.Y.V; }
        _inv.Show(_inv.Page);
        InventoryNative.Adopt();
        InventoryNative.RefreshLabels(_inv.Model, force: true);
        InventoryNative.Status(true);
        _inv.Model.Restart(-1);
        AskBag(_inv.Page, System.Diagnostics.Stopwatch.GetTimestamp());
    }

    /// <summary>Ask the server for backpack page <paramref name="page"/>: <c>SelectPanel</c>, which the server answers
    /// by resending that page whole (<c>CNWSPlayerInventoryGUI::SetPanel</c> clears what it sent) -- but only when the
    /// page changes, so a page it may already be on is selected away and back. The open itself is only
    /// <c>Status(true)</c>: a <c>Status(false)</c> reaches <c>SetOpen(0, 1)</c>, which closes an open container.</summary>
    private static void AskBag(int page, long now)
    {
        if (page == _inv.Model.Selected || _inv.Model.Selected < 0) InventoryNative.SelectPanel(page == 0 ? 1 : 0);
        InventoryNative.SelectPanel(page);
        _bagSeqAt = InventoryNative.BagSeq;
        _inv.Model.Ask(page, now);
    }

    /// <summary>The backpack's stream: the page asked for arrives when the server sends an item on it (the whole page
    /// comes in one update pass) or after <see cref="QuietSec"/> of nothing, whatever it held before and did not send
    /// again is dropped (<see cref="InventoryNative.PruneBagPage"/>), and the next page the window wants is asked for.
    /// ponytail: a page shows what it held when last sent; a periodic re-ask if that bites.</summary>
    private static void BagStream(long now)
    {
        ItemGrid m = _inv.Model;
        if (m.Awaiting >= 0 && (InventoryNative.BagArrived(m.Awaiting, _bagSeqAt) || now - m.AskedAt >= Ticks(QuietSec)))
        {
            InventoryNative.PruneBagPage(m.Awaiting, _bagSeqAt);
            m.Arrived(m.Awaiting);
        }
        _inv.BagView(out int first, out int last);
        int want = m.Want(first, last, now, _inv.Scrolled ? Ticks(DwellSec) : 0);
        if (want < 0) return;
        if (ExtenderLog.Enabled) _log?.Info($"[inv] bag page {m.Selected} -> {want}");
        AskBag(want, now);
    }

    /// <summary>Close the window. An open container closes with it, as the stock panel's hide tears its container down.</summary>
    private static void Close(bool notify)
    {
        _inv.Hide();
        SyncKeyboard();
        if (GameGate.InGame) InventoryNative.CloseContainer(InventoryNative.MyPanel());
        if (notify) InventoryNative.Status(false);
    }

    // ---- input ----

    private static bool _wasMoving;

    /// <summary>After every input: the keyboard follows the search box, and a finished title-bar drag persists the
    /// position (Hidden knobs, one debounced write).</summary>
    private static bool Input(bool consumed)
    {
        SyncKeyboard();
        if (_wasMoving && !_inv.Moving)
        {
            InventoryKnobs.X.Set(_inv.X); InventoryKnobs.Y.Set(_inv.Y);
            if (!InventoryKnobs.Placed.V) InventoryKnobs.Placed.Set(true);
        }
        _wasMoving = _inv.Moving;
        return consumed;
    }

    /// <summary>The search box owns the keyboard exactly while it is focused.</summary>
    private static void SyncKeyboard()
    {
        if (_window == null) return;
        bool want = _inv.Open && _inv.SearchFocused;
        if (want != _window.WantsKeyboard) UiHost.RequestKeyboard(_window, want);
    }

    // ---- actions ----

    /// <summary>A request from the window: Close / CloseLoot / Refused / Gold / Drop are local; every item action first asks
    /// the policy route (<c>inventory.&lt;action&gt;.route</c>): the engine's sender, nothing, or a
    /// <see cref="ServerPolicy.EventMinor"/> event the server handles itself.</summary>
    private static void OnAction(InvAction a)
    {
        if (ExtenderLog.Enabled)
            _log?.Info($"[inv] act {a.Kind} oid=0x{a.Oid:x} mask=0x{a.Mask:x} x={a.X} y={a.Y} other=0x{a.Other:x}");
        switch (a.Kind)
        {
            case InvActKind.Close: Close(notify: true); return;
            case InvActKind.CloseLoot: InventoryNative.UseObject(a.Other); return;
            case InvActKind.Refused: InventoryNative.Feedback(a.Text); return;
            case InvActKind.Gold: OpenGold(); return;
            case InvActKind.Drop: DropInWorld(a); return;
        }
        string name = a.Kind.ToString().ToLowerInvariant();
        int h = Math.Max(1, _inv.Info(a.Oid).H);
        int wireY = a.Kind switch
        {
            InvActKind.Move or InvActKind.Unequip => InventoryModel.Flip(a.Y, h),
            InvActKind.Take => a.X == InventoryWindow.AutoCell ? a.Y : InventoryModel.Flip(a.Y, h),
            InvActKind.Put => ItemGrid.Flip(a.Y, h, LootModel.Rows),
            _ => a.Y,
        };
        switch (ServerPolicy.Route("inventory." + name))
        {
            case PolicyRoute.Deny:
                InventoryNative.Feedback("The server does not allow that here.");
                return;
            case PolicyRoute.Server:
                ServerPolicy.Emit("inventory." + name, Hex(a.Oid), Hex(a.Mask), a.X.ToString(), wireY.ToString(), Hex(a.Other));
                return;
        }
        switch (a.Kind)
        {
            case InvActKind.Equip: InventoryNative.Equip(a.Oid, a.Mask); break;
            case InvActKind.Unequip: InventoryNative.Unequip(a.Oid, a.X, wireY); break;
            case InvActKind.Move: InventoryNative.Move(a.Oid, a.X, wireY, InventoryNative.PlayerId()); break;
            case InvActKind.Take:
                InventoryNative.Move(a.Oid, a.X, wireY, a.X == InventoryWindow.AutoCell ? InventoryModel.Invalid : InventoryNative.PlayerId());
                break;
            case InvActKind.Put: InventoryNative.Move(a.Oid, a.X, wireY, a.Other); break;
            case InvActKind.Merge: InventoryNative.Merge(a.Oid, a.Other); break;
            case InvActKind.Split: InventoryNative.Split(a.Oid, a.X); break;
            case InvActKind.Use: InventoryNative.Use(a.Oid, a.X, a.Y); break;
            case InvActKind.Radial:
                InventoryNative.Radial(a.Oid, a.X, a.Y, _inv.Loot.Find(a.Oid) < 0 ? -1
                                                     : _inv.Loot.IsItem ? InventoryNative.RadialPossessed : InventoryNative.RadialGround);
                break;
        }
        _inv.MarkPending(a);
    }

    private static string Hex(uint v) => v.ToString("x");

    // ---- the frame ----

    /// <summary>Before the window roll-call, while the window is open: every held and looted item's PLT icon gets its engine
    /// model, so cloaks and armour draw on every page, not only the one a hidden stock grid holds.</summary>
    private static void BuildModels()
    {
        if (!_inv.Open) return;
        InventoryModel m = _inv.Model;
        for (int i = 0; i < m.Count; i++) RadialIcons.BuildModel(m.Items[i].Oid);
        for (int i = 0; i < m.Equipped.Length; i++) RadialIcons.BuildModel(m.Equipped[i].Oid);
        LootModel l = _inv.Loot;
        for (int i = 0; i < l.Count; i++) RadialIcons.BuildModel(l.Items[i].Oid);
    }

    /// <summary>The loot stream: the server streams only the container's current page and turns it a page at a time
    /// (next / previous, wrapping), so the stock view is turned by its own arrow handlers, which keep the stock grid's
    /// page in step, the shorter way round to the page the window wants, all turns in one burst (only the last page is
    /// written), once the page asked for last has arrived. A page the server is already on is turned away and back.</summary>
    private static void LootStream(IntPtr panel, long now)
    {
        LootModel m = _inv.Loot;
        if (!m.Open || m.Pages < 2) return;
        if (m.Awaiting >= 0 && now - m.AskedAt >= Ticks(StuckSec) && !_stuckSaid)
        {
            _stuckSaid = true;
            Notice.Show(_log, $"inventory: container page {m.Awaiting + 1} has not arrived in {StuckSec:0} s -- the loot pane is stuck waiting");
        }
        _inv.LootView(out int first, out int last);
        int want = m.Want(first, last, now, Ticks(DwellSec));
        if (want < 0) return;
        int fwd = ((want - m.EnginePage) % m.Pages + m.Pages) % m.Pages, back = m.Pages - fwd;
        if (ExtenderLog.Enabled) _log?.Info($"[inv] loot page {m.EnginePage} -> {want}");
        if (fwd == 0) { InventoryNative.LootStep(panel, true); InventoryNative.LootStep(panel, false); }
        else for (int i = Math.Min(fwd, back); i > 0; i--) InventoryNative.LootStep(panel, fwd <= back);
        m.Ask(want, now);
    }

    private static void Draw()
    {
        if (!_inv.Open) return;
        if (!GameGate.InGame) { Close(notify: false); return; }
        if (_masterOff) { _masterOff = false; Close(notify: true); return; }
        IntPtr panel = InventoryNative.MyPanel();
        if (InventoryNative.EngineModal()) return;
        int vw = TextRig.ViewportW, vh = TextRig.ViewportH;
        if (vw != _vw || vh != _vh || _layoutDirty || _inv.PolicyStale) Layout(vw, vh);
        if (!Faces()) return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        InventoryNative.Fill(_inv.Model);
        InventoryNative.FillLoot(_inv.Loot, panel, now, Ticks(QuietSec));
        BagStream(now);
        LootStream(panel, now);
        _inv.ApplyPending();
        _inv.Tick();
        InventoryNative.RefreshLabels(_inv.Model);
        _inv.RefreshSearch();

        Skin? skin = null;
        if (Theme.IsNative && _skin.Arm(_log, ""))
        {
            _skin.Begin();
            skin = _skin;
        }
        _inv.NativeArt = skin != null;
        _under ??= new VertexBatch(_text.SolidU, _text.SolidV);
        _over ??= new VertexBatch(_text.SolidU, _text.SolidV);
        _gridUnder ??= new VertexBatch(_text.SolidU, _text.SolidV);
        _gridOver ??= new VertexBatch(_text.SolidU, _text.SolidV);
        _lootUnder ??= new VertexBatch(_text.SolidU, _text.SolidV);
        _lootOver ??= new VertexBatch(_text.SolidU, _text.SolidV);
        _under.Reset(_text.SolidU, _text.SolidV);
        _over.Reset(_text.SolidU, _text.SolidV);
        _gridUnder.Reset(_text.SolidU, _text.SolidV);
        _gridOver.Reset(_text.SolidU, _text.SolidV);
        _lootUnder.Reset(_text.SolidU, _text.SolidV);
        _lootOver.Reset(_text.SolidU, _text.SolidV);
        _under.Outline = _over.Outline = _gridUnder.Outline = _gridOver.Outline = _lootUnder.Outline = _lootOver.Outline = Halo;
        int n = _inv.Emit(_under, _over, _gridUnder, _gridOver, _lootUnder, _lootOver, skin, _text, _small, _icons);
        _inv.GridClip(out int gx, out int gy, out int gw, out int gh);
        _inv.LootClip(out int lx, out int ly, out int lw, out int lh);

        skin?.Flush(skip: InventoryWindow.KCell, skip2: InventoryWindow.KLootCell);
        skin?.FlushClipped(InventoryWindow.KCell, gx, gy, gw, gh);
        skin?.FlushClipped(InventoryWindow.KLootCell, lx, ly, lw, lh);
        TextRig.DrawBatch(_under.AsSpan(), _under.VertexCount);
        TextRig.DrawBatch(_gridUnder.AsSpan(), _gridUnder.VertexCount, clip: true, cx: gx, cy: gy, cw: gw, ch: gh);
        TextRig.DrawBatch(_lootUnder.AsSpan(), _lootUnder.VertexCount, clip: true, cx: lx, cy: ly, cw: lw, ch: lh);
        for (int i = 0; i < n; i++)
        {
            InvIcon q = _icons[i];
            bool loot = q.Clip == InventoryWindow.ClipLoot;
            RadialIcons.Draw((IntPtr)q.Oid, q.X, q.Y, q.W, q.H, q.Alpha, q.CellsW, q.CellsH, q.Clip != 0,
                             loot ? lx : gx, loot ? ly : gy, loot ? lw : gw, loot ? lh : gh);
        }
        TextRig.DrawBatch(_gridOver.AsSpan(), _gridOver.VertexCount, clip: true, cx: gx, cy: gy, cw: gw, ch: gh);
        TextRig.DrawBatch(_lootOver.AsSpan(), _lootOver.VertexCount, clip: true, cx: lx, cy: ly, cw: lw, ch: lh);
        TextRig.DrawBatch(_over.AsSpan(), _over.VertexCount);
        if (_inv.DragIcon(out InvIcon d)) RadialIcons.Draw((IntPtr)d.Oid, d.X, d.Y, d.W, d.H, d.Alpha, d.CellsW, d.CellsH);
    }

    /// <summary>The <c>inventory.outline</c> halo radius at the window's text size.</summary>
    private static float Halo => TextStyle.HaloOf(InventoryKnobs.Text.Outline.V, _textPx);

    /// <summary>The window's two faces at its scale, re-fetched when the atlas re-baked; false (and one notice) when they
    /// are not baked.</summary>
    private static bool Faces()
    {
        if (_text != null && _small != null && _atlasEpoch == TextRig.AtlasEpoch) return true;
        _atlasEpoch = TextRig.AtlasEpoch;
        _text = TextRig.BakedFace(_typeface, _textPx); _small = TextRig.BakedFace(_typeface, _smallPx);
        if (_text != null && _small != null) return true;
        if (!_facesMissing) { _facesMissing = true; Notice.Show(_log, $"inventory: the {_textPx}/{_smallPx} px faces are not baked -- the window cannot draw"); }
        return false;
    }

    /// <summary>Re-lay out for the viewport, bake the two faces at the new scale, and centre a never-placed window. Cold.</summary>
    private static void Layout(int vw, int vh)
    {
        _vw = vw; _vh = vh; _layoutDirty = false;
        bool centre = _inv.X < 0f;
        _inv.Layout(Metrics.Scale * InventoryKnobs.Size.V, InventoryKnobs.Mode.V == InventoryKnobs.ModeScrolled, InventoryKnobs.Search.V, vw, vh);
        if (centre) { _inv.X = (vw - _inv.W) / 2f; _inv.Y = (vh - _inv.H) / 2f; _inv.ClampOnScreen(vw, vh); }
        float tp = MathF.Round(Math.Clamp(TextPx * _inv.S, TextRig.MinFontPx, TextRig.AtlasMaxPx));
        float sp = MathF.Round(Math.Clamp(SmallPx * _inv.S, TextRig.MinFontPx, TextRig.AtlasMaxPx));
        string face = InventoryKnobs.Text.Typeface.V;
        if (tp != _textPx || sp != _smallPx || face != _typeface)
        {
            _textPx = tp; _smallPx = sp; _typeface = face;
            TextRig.Face(face, tp); TextRig.Face(face, sp);
            _atlasEpoch = -1;
        }
    }
}
