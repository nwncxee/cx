using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Radial;

/// <summary>
/// The radial seam and the dropdown on it: skip <c>CRadialMenu::ActivateInternal</c>, take the engine-built tree and
/// show it as a <see cref="CascadeMenu"/> in one registered <see cref="Window"/>. Every root builds its whole top ring
/// and calls <c>Activate</c> last, so at the hook <c>this</c> is a complete root with its XY and target inside and no
/// GUI modality engaged; a submenu materializes lazily, never cached across opens, and every close tears the tree down.
/// </summary>
public sealed unsafe partial class RadialService : IExtenderService
{
    private static ExtenderLog? _log;
    private static RadialActivateInternalFn _orig;
    private static StartLoadBarFn* _loadBarSlot;
    private static IntPtr _guiMan;
    /// <summary>The one-shot layout probe ran (first open).</summary>
    private static bool _probed;
    /// <summary>Rot latch: the probe failed or a hook threw -- every open forwards to vanilla for the session.</summary>
    private static bool _vanilla;
    /// <summary>The root CX currently holds: from the hook until the close that tears it down.</summary>
    private static IntPtr _held;
    private static int _ctorAtLastOpen = -1;
    private static bool _depthWarned, _nullChildWarned;
    /// <summary>A leaf or entry callback is on the stack: an open arriving now is the handler's own, forwarded.</summary>
    private static bool _inInvoke;

    /// <summary>The cascade's window: rect-less, never focusable, registered only after the unit gates pass so a
    /// stood-down service leaves nothing in the roll-call. <see cref="Window.SuppressChords"/> is set at open and
    /// cleared at close, on the one thread; <see cref="Window.CapturesPointer"/> puts it ahead of the engine GUI.</summary>
    private static Window? _window;
    private static readonly CascadeMenu _menu = new();
    /// <summary>The row-action popup: open beside the row of <see cref="_ctxLevel"/>/<see cref="_ctxRow"/>.</summary>
    private static readonly CascadeMenu _ctx = new();
    private static int _ctxLevel = -1, _ctxRow = -1;
    /// <summary>The kind of the held root, for the layout rows. ponytail: one vptr = one kind, so the self, other-creature,
    /// party and portrait rings all file under Creature; a ring discriminator in the row key is the upgrade.</summary>
    private static RootKind _kind;
    /// <summary>The face and viewport the cascade opened with (a face change closes it), for the popup and re-layouts.</summary>
    private static GlyphAtlas? _atlas;
    private static float _vpW, _vpH;
    /// <summary>The engine menu behind each open cascade level: the root at 0, a materialized child deeper. Cleared
    /// at every close -- no engine pointer survives past it.</summary>
    private static readonly IntPtr[] _levelMenu = new IntPtr[CascadeMenu.MaxDepth];
    /// <summary>The vanilla slot path of the menu behind each level: empty at the root, a hoisted submenu's child
    /// carries its pin's path. Reorders are keyed by it.</summary>
    private static readonly int[]?[] _levelPath = new int[CascadeMenu.MaxDepth][];
    /// <summary>The folder a level lists, null for an engine level. A folder level's menu handle is the root's:
    /// its rows are hoists, each walked from the root.</summary>
    private static readonly string?[] _levelFolder = new string?[CascadeMenu.MaxDepth];
    /// <summary>The popup's corner, for re-laying it while a name is typed.</summary>
    private static float _ctxX, _ctxY;
    /// <summary>The name editor: the popup shows one <c>Name: …</c> row while set; keys edit, Enter commits to
    /// <see cref="NameEdit.Commit"/>, Esc or a press outside cancels. ponytail: ASCII from SDL keycodes (the pump
    /// already swallows every key while the cascade is open); SDL_TEXTINPUT and the keyboard-owner path are the upgrade.</summary>
    private static NameEdit? _naming;
    private sealed class NameEdit { public readonly System.Text.StringBuilder Buf = new(); public Action<string> Commit = null!; }
    /// <summary>A popup child row: a folder pick (<see cref="Name"/> "" = the top list) or a new folder to name.</summary>
    private sealed record FolderPick(string Name, bool New);
    private const int NameMax = 24;
    /// <summary>The root kinds "Also show on" offers: the world roots whose slot 1 builds the caster's own
    /// <c>CClientSpellMenu</c> (<c>CreateClientSpellMenu</c> @0x73b360, <c>CGuiPlaceableMenu::CreateMenuByID</c>
    /// @0x7ecc30, <c>CGuiDoorMenu::CreateMenuByID</c> @0x7569f0, <c>CNWCTile::CreateMenuByID</c> @0x80c160). The item
    /// root's slot 1 is the item's own spell list, and the quickbar and chat roots have no target: not offered.</summary>
    private static readonly (RootKind Kind, string Label)[] ShowOnKinds =
        { (RootKind.Creature, "Creatures"), (RootKind.Placeable, "Placeables"), (RootKind.Door, "Doors"), (RootKind.Plain, "Ground") };
    private static VertexBatch? _batch, _ctxBatch;
    private static int _epoch;
    /// <summary>A press was consumed, so its release is ours too (else the engine sees an orphan up).</summary>
    private static bool _pressOwned;
    /// <summary>The dwell request <see cref="Draw"/> saw, served at PostPresent; -1 when none.</summary>
    private static int _deferLevel = -1, _deferRow = -1;
    /// <summary>A close <see cref="Draw"/> decided on (one of the reason consts), served at PostPresent.</summary>
    private static string? _deferClose;

    private enum RowAction { PinToTop = 1, Unpin, MoveUp, MoveDown, Hide, Unhide, Reset, MoveTo, ShowOn, Rename, RemoveFolder, Everywhere }
    /// <summary>The root kinds "Show everywhere (as self)" pins on: every root with a target of its own.</summary>
    private static readonly RootKind[] EverywhereKinds = { RootKind.Creature, RootKind.Placeable, RootKind.Door, RootKind.Item, RootKind.Plain, RootKind.Trap };
    private const int LoadBarPriority = 50;
    private const int ServicePriority = 40;
    private const string CloseLeaf = "leaf", CloseEsc = "esc", CloseAway = "away", CloseArea = "area", CloseReenter = "reenter",
                         CloseOff = "off", CloseFont = "font";
    private static CascadeStyle _style;

    private static readonly string[] RequiredSymbols =
    {
        SymbolMap.RadialActivateInternal, SymbolMap.RadialCreateSubMenu, SymbolMap.RadialRecursiveAutoDelete,
        SymbolMap.GuiCurrentEventObject, SymbolMap.RadialMenuDepth,
        SymbolMap.StartLoadBar, SymbolMap.GuiMan,
        SymbolMap.VtCRadialMenu, SymbolMap.VtCGuiCreatureMenu, SymbolMap.VtCClientSpellMenu, SymbolMap.VtCRadialSpellMenu,
        SymbolMap.VtCClientTrapMenu, SymbolMap.VtCGuiItemMenu, SymbolMap.VtCGuiPlaceableMenu, SymbolMap.VtCGuiDoorMenu,
        SymbolMap.VtCGuiQuickbarMenu, SymbolMap.VtCGuiChatWindowMenu,
    };

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        UiCategory.Declare();   // the UI groups (radial among them) declare in panel order whether or not the hook arms
        _ = RadialKnobs.G;
        RadialTree.SelfCheck(log);
        RadialPins.SelfCheck(log);
        SelfActions.SelfCheck(log);
        SelfCheck(log);

        if (!SymbolMap.Require(log, "RadialService", RequiredSymbols)) return;
        if (!EngineLayout.Require(log, "RadialService", "CRadialMenu", "CGuiCallback", "CClientSpellMenu", "CGuiCreatureMenu", "CGuiIcon", "CGuiTextBubble", "StringGob", "CGuiMan")) return;
        if (!RadialTree.Init(log)) { log.Always("RadialService: standing down -- RadialTree.Init could not resolve its unit"); return; }
        if (!SelfActions.Init(log)) log.Info("RadialService: self pins unavailable (SelfActions unit unresolved); rows grey, Show everywhere not offered");
        _guiMan = Native.Sym(SymbolMap.GuiMan);

        _window = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            CapturesPointer = true,
            DrawContent = Draw,
            OnMouseButton = OnMouseButton,
            OnMouseMotion = OnMouseMotion,
            OnMouseWheel = OnMouseWheel,
            OnKey = OnKey,
            HitTestOverride = (px, py) => (_menu.Open && _menu.Contains(px, py)) || (_ctx.Open && _ctx.Contains(px, py)),
        };
        FrameRegistry.Global.Register(FramePhase.PostPresent, ServicePriority, Service, "Radial.service", log);

        _orig = hooks.InstallRadialActivateInternal(&OnActivateInternal);
        _loadBarSlot = hooks.ChainStartLoadBar(&OnStartLoadBar, LoadBarPriority);
        log.Info($"RadialService: ActivateInternal hooked (radial.enabled={(RadialKnobs.Enabled.V ? "on" : "off")}, cascade window registered); " +
                 $"StartLoadBar chained={(_loadBarSlot != null ? "yes" : "no")}");
    }

    // ---- hooks (cold: the main thread, once per right-click) ------------------------------------

    /// <summary>A screen point (SDL px) the next root opens at instead of the engine's last mouse position, set around a
    /// synchronous <c>ActivateRadialMenu</c> by a CX window the engine does not see the pointer over (the inventory).</summary>
    internal static (int X, int Y)? OpenAt;

    /// <summary>The seam. Anything this cannot own forwards to vanilla intact: the master off, the rot latch, a hook
    /// that threw, an open from INSIDE a leaf handler (the tree on the stack must not be torn down under it) and an
    /// open with <c>static_nMenuDepth != 0</c> (an engine menu's descent, whose child must not be freed under a live
    /// parent). A second open while a tree is held closes the cascade and releases that root first.</summary>
    [UnmanagedCallersOnly]
    private static void OnActivateInternal(IntPtr menu)
    {
        if (OpenAt is var (ax, ay) && menu != IntPtr.Zero)
        {
            IntPtr gm = _guiMan != IntPtr.Zero ? *(IntPtr*)_guiMan : IntPtr.Zero;
            var m = new CRadialMenu(menu);
            m.MenuX = ax;
            m.MenuY = (gm != IntPtr.Zero ? new CGuiMan(gm).VpHeight : TextRig.ViewportH) - ay;
        }
        if (!RadialKnobs.Enabled.V || _vanilla || menu == IntPtr.Zero || _inInvoke) { Forward(menu); return; }
        int depth = RadialTree.MenuDepth;
        if (depth != 0)
        {
            if (!_depthWarned) { _depthWarned = true; _log?.Info($"[radial] static_nMenuDepth={depth} at open -- an engine menu is live, forwarding"); }
            Forward(menu);
            return;
        }
        try
        {
            if (!_probed)
            {
                _probed = true;
                if (!RootLooksRight(menu))
                {
                    _vanilla = true;
                    _log?.Always("[radial] layout probe FAILED on the first root (m_bAutoDelete != 1, or parent/refcount " +
                                 "non-zero on a fresh root) -- vanilla for this session");
                    Forward(menu);
                    return;
                }
            }
            Close(CloseReenter);
            _held = menu;
            LogTree(menu);
            if (!OpenCascade(menu))
            {
                _held = IntPtr.Zero;   // nothing to show it with (no window / atlas, or an empty ring): vanilla draws this one
                Forward(menu);
            }
        }
        catch (Exception e)
        {
            _vanilla = true;
            _log?.Always($"[radial] hook threw, vanilla for this session: {e.GetType().Name}: {e.Message}");
            _menu.Close(); DropModal();
            if (_held == menu) _held = IntPtr.Zero;
            Forward(menu);   // intact on every throw path above: the engine draws this one
        }
    }

    /// <summary>Area edge: a held tree must not outlive the area whose objects its handlers point at. Not while a
    /// leaf handler is on the stack (its <c>this</c> may be the root): <see cref="Leaf"/> closes the moment it returns.
    /// Returns the engine's <c>CGuiAreaLoadscreen*</c>: callers deref it (<c>ContentIndex::Play</c> -> <c>+0x2d0</c>).</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnStartLoadBar(IntPtr app, int state, IntPtr image, int rebuild)
    {
        if ((_held != IntPtr.Zero || _menu.Open) && !_inInvoke)
        {
            _log?.Info("[radial] area edge with a held tree -- releasing");
            Close(CloseArea);
        }
        StartLoadBarFn next = *_loadBarSlot;
        return next != null ? next(app, state, image, rebuild) : IntPtr.Zero;
    }

    private static void Forward(IntPtr menu) { if (_orig != null) _orig(menu); }

    /// <summary>The first-open layout latch. No <c>CRadialMenu</c> row carries an <c>Expect</c>, so
    /// <c>EngineLayout.Probe</c> would pass vacuously; assert what a fresh root must hold instead: <c>m_bAutoDelete =
    /// 1</c> before <c>Activate</c>, with <c>m_pParent</c> (+0x358) and the refcount (+0x874) zeroed by the ctor
    /// @0x583460 and written only by <c>HandleMenuAction</c> / <c>CreateSubMenu</c> on a descent.</summary>
    private static bool RootLooksRight(IntPtr menu)
    {
        var m = new CRadialMenu(menu);
        return m.AutoDelete == 1 && m.Parent == IntPtr.Zero && m.RefCount == 0;
    }

    // ---- open / close ---------------------------------------------------------------------------

    /// <summary>Build the root rows and open the cascade at the menu's XY. That XY is engine GUI space -- the SDL
    /// point with <c>y = GuiManVpH - y</c> (<c>HandleInputEvent</c> @0x6658dc), no scale -- so it is flipped back
    /// through the same viewport height. False when nothing can show it (no window or atlas, an empty ring).</summary>
    private static bool OpenCascade(IntPtr menu)
    {
        GlyphAtlas? atlas = RadialKnobs.Font.Face(TextRig.Atlas, cold: true);
        if (_window == null || atlas == null) return false;
        RadialPins.Load(_log, PlayerIdentity.Key());
        _kind = RadialTree.Classify(menu);
        Array.Clear(_levelMenu); Array.Clear(_levelPath); Array.Clear(_levelFolder);
        _levelMenu[0] = menu; _levelPath[0] = Array.Empty<int>();
        CascadeItem[] items = ItemsFor(menu, Array.Empty<int>());
        if (items.Length == 0) { Array.Clear(_levelMenu); Array.Clear(_levelFolder); return false; }
        var (mx, my) = RadialTree.Position(menu);
        int vpW = TextRig.ViewportW, vpH = TextRig.ViewportH;
        IntPtr gm = _guiMan != IntPtr.Zero ? *(IntPtr*)_guiMan : IntPtr.Zero;
        if (gm != IntPtr.Zero) { vpW = new CGuiMan(gm).VpWidth; vpH = new CGuiMan(gm).VpHeight; }
        _atlas = atlas; _vpW = vpW; _vpH = vpH;
        _epoch = TextRig.AtlasEpoch;
        _pressOwned = false; _deferLevel = _deferRow = -1; _deferClose = null;
        _menu.DragEnabled = !RadialKnobs.Lock.V; _menu.DwellMs = RadialKnobs.Dwell.V; _menu.Drill = RadialKnobs.Mode.V == RadialKnobs.ModeDrill;
        _menu.OpenAt(items, mx, vpH - my, atlas, vpW, vpH);
        _window.SuppressChords = true;
        return true;
    }


    /// <summary>Close the cascade, drop the modal gate and every per-level handle, tear the held tree down. Idempotent;
    /// the reason is the <c>logs=1</c> close line (leaf / esc / away / area / reenter / off / font).</summary>
    private static void Close(string reason)
    {
        bool wasOpen = _menu.Open;
        if (wasOpen) _menu.Close();
        DropModal();
        if (!wasOpen && _held == IntPtr.Zero) return;
        _log?.Info($"[radial] close reason={reason}");
        Release();
    }

    private static void CloseCtx() { _ctx.Close(); _ctxLevel = _ctxRow = -1; _naming = null; }

    private static void DropModal()
    {
        if (_window != null) _window.SuppressChords = false;
        CloseCtx();
        Array.Clear(_levelMenu); Array.Clear(_levelPath); Array.Clear(_levelFolder);
        _deferLevel = _deferRow = -1; _deferClose = null;
    }

    /// <summary>Tear the held root down (all materialized children with it) and drop every pointer.</summary>
    private static void Release()
    {
        SelfActions.Release();
        IntPtr root = _held;
        if (root == IntPtr.Zero) return;
        _held = IntPtr.Zero;
        var (before, after) = RadialTree.Teardown(root);
        _log?.Info($"[radial] teardown root=0x{(long)root:x} ctor {before}->{after} depth={RadialTree.MenuDepth}");
    }

    // ---- input (cold; the pump, on the one thread) ----------------------------------------------

    private static bool OnMouseButton(bool down, int button, int x, int y)
    {
        if (!_menu.Open)
        {
            if (!down && _pressOwned) { _pressOwned = false; return true; }
            return false;
        }
        if (!RadialKnobs.Enabled.V) { Close(CloseOff); _pressOwned = down; return true; }
        if (!down)
        {
            _pressOwned = false;
            if (_ctx.Open) CtxAct(_ctx.MouseUp()); else Act(_menu.MouseUp(), CloseAway);
            return true;
        }
        _pressOwned = true;
        if (_ctx.Open) { CtxAct(_ctx.MouseDown(x, y, button)); return true; }
        Act(_menu.MouseDown(x, y, button), CloseAway);
        return true;
    }

    private static bool OnMouseMotion(int x, int y)
    {
        if (!_menu.Open) return false;
        if (_ctx.Open) _ctx.MouseMove(x, y); else _menu.MouseMove(x, y);
        return true;
    }

    private static bool OnMouseWheel(int dy)
    {
        if (!_menu.Open) return false;
        if (!_ctx.Open) _menu.MouseWheel(dy);
        return true;
    }

    /// <summary>Every key is consumed while open (arrows never reach the camera, Esc never reaches the engine menu).</summary>
    private static bool OnKey(uint sdlKey, int mods)
    {
        if (!_menu.Open) return false;
        if (!RadialKnobs.Enabled.V) { Close(CloseOff); return true; }
        if (_naming != null) { NameKey(sdlKey, mods); return true; }
        if (_ctx.Open) { CtxAct(_ctx.Key(sdlKey)); return true; }
        Act(_menu.Key(sdlKey), CloseEsc);
        return true;
    }

    private static void Act(CascadeAction a, string closeReason)
    {
        switch (a.Kind)
        {
            case CascadeActionKind.Activate: Leaf(a.Level, a.Row); break;
            case CascadeActionKind.RequestChildren: Descend(a.Level, a.Row); break;
            case CascadeActionKind.Context: OpenContext(a.Level, a.Row); break;
            case CascadeActionKind.Drop: DropRow(a.Level, a.Row, a.ToLevel, a.ToIndex); break;
            case CascadeActionKind.CloseAll: Close(closeReason); break;
        }
    }

    /// <summary>The popup's own input: a picked row runs its action (a <see cref="RowAction"/>, a <see cref="RootKind"/>
    /// under "Also show on", a <see cref="FolderPick"/> under "Move to"); a submenu row cascades its picks; a press
    /// outside it (or Esc) just closes the popup, the press spent; a right press on a popup row is nothing.</summary>
    private static void CtxAct(CascadeAction a)
    {
        if (a.Kind == CascadeActionKind.CloseAll) { CloseCtx(); return; }
        if (a.Kind == CascadeActionKind.RequestChildren) { CtxDescend(a.Level, a.Row); return; }
        if (a.Kind != CascadeActionKind.Activate || _naming != null) return;
        switch (_ctx.Level(a.Level).Items[a.Row].Tag)
        {
            case RowAction act: RowAct(act); break;
            case RootKind kind: ShowOn(kind); break;
            case FolderPick pick: if (pick.New) BeginName("", name => MoveTo(name)); else MoveTo(pick.Name); break;
        }
    }

    /// <summary>The picks under a popup submenu row: the root kinds less this one, or the folders less the row's own
    /// (plus the top list when it is in one) and a new folder.</summary>
    private static void CtxDescend(int level, int row)
    {
        if (_ctx.Level(level).Items[row].Tag is not RowAction act) return;
        var items = new List<CascadeItem>(6);
        if (act == RowAction.ShowOn)
        {
            bool walkable = RowOf(_ctxLevel, _ctxRow, out _, out _, out int[] pp, out RadialRowTag? hh) && (hh != null ? !hh.Pin.IsFolder : pp.Length > 1);   // a top-level slot (filed or not) has a one-step path: as self only
            if (walkable) foreach (var (kind, label) in ShowOnKinds) if (kind != _kind) items.Add(new CascadeItem(label, tag: kind));
            if (SelfActions.Ready && SelfKeyOf(_ctxLevel, _ctxRow) != null) items.Add(new CascadeItem("Everywhere (as self)", tag: RowAction.Everywhere));
        }
        else if (act == RowAction.MoveTo)
        {
            string own = FiledOf(_ctxLevel, _ctxRow, out _)?.In ?? "";
            if (own.Length > 0) items.Add(new CascadeItem("Top level", tag: new FolderPick("", false)));
            foreach (RadialPin f in RadialPins.Folders(_kind)) if (f.Folder != own) items.Add(new CascadeItem(f.Folder, tag: new FolderPick(f.Folder, false)));
            items.Add(new CascadeItem("New folder\u2026", tag: new FolderPick("", true)));
        }
        if (items.Count > 0) _ctx.OpenChild(level, row, items.ToArray());
    }

    /// <summary>The engine row behind a cascade row, through its tag: an <c>int</c> is a vanilla slot of that level's
    /// menu; a <see cref="RadialRowTag"/> is a hoist with its own materialized parent; null (the item header) is no
    /// row. <paramref name="path"/> is the row's vanilla slot path.</summary>
    private static bool RowOf(int level, int row, out IntPtr menu, out int slot, out int[] path, out RadialRowTag? hoist)
    {
        menu = IntPtr.Zero; slot = -1; path = Array.Empty<int>(); hoist = null;
        if ((uint)level >= (uint)_menu.Depth || (uint)row >= (uint)_menu.Level(level).Items.Length) return false;
        object? tag = _menu.Level(level).Items[row].Tag;
        if (tag is int s)
        {
            int[] parent = _levelPath[level] ?? Array.Empty<int>();
            path = new int[parent.Length + 1];
            parent.CopyTo(path, 0);
            path[^1] = s;
            menu = _levelMenu[level]; slot = s;
            return true;
        }
        if (tag is RadialRowTag t) { menu = t.Menu; slot = t.Slot; path = t.Pin.Path; hoist = t; return true; }
        return false;
    }

    private static int SlotOf(int level, int row) => RowOf(level, row, out _, out int slot, out _, out _) ? slot : -1;

    /// <summary>Leading rows with no slot (the item root's header): never counted in a list's positions.</summary>
    private static int Lead(CascadeItem[] items)
    {
        int n = 0;
        while (n < items.Length && items[n].Tag == null) n++;
        return n;
    }

    /// <summary>A leaf row: invoke the engine triple in its event-object bracket, then close and tear down -- the
    /// order <c>HandleMenuAction</c> keeps (the handler runs on a live tree; <c>Deactivate</c> follows). A hoisted
    /// row that failed validation is refused here too, whatever its Disabled flag.</summary>
    private static void Leaf(int level, int row)
    {
        if (!RowOf(level, row, out IntPtr m, out int slot, out _, out RadialRowTag? h)) return;
        if (h != null && !h.Valid) { _log?.Info("[radial] refused: hoisted row is unvalidated"); return; }
        if (h != null && h.Pin.IsFolder) { Descend(level, row); return; }
        if (h != null && h.Pin.IsSelf)
        {
            _inInvoke = true;
            try { SelfActions.Fire(h.Pin); } finally { _inInvoke = false; }
            Close(CloseLeaf);
            return;
        }
        if (m == IntPtr.Zero) { Close(CloseLeaf); return; }
        Invoke(m, level, slot, _menu.Level(level).Items[row].Label);
        Close(CloseLeaf);
    }

    /// <summary>The leaf call, logged only when the slot's entry is set (the sentinel case calls nothing, so a
    /// creator-only descent prints no invoke line).</summary>
    private static bool Invoke(IntPtr m, int level, int slot, string label)
    {
        if (_log != null && ExtenderLog.Enabled)
        {
            RadialSlot s = RadialTree.ReadSlot(m, slot);
            if (s.Fn != IntPtr.Zero)
                _log.Info($"[radial] invoke level={level} slot={slot} \"{label}\" fn=0x{(long)s.Fn:x} param=0x{s.Param:x}");
        }
        _inInvoke = true;
        try { return RadialTree.Invoke(m, slot); }
        finally { _inInvoke = false; }
    }

    /// <summary>A submenu row (click, Enter/Right, or the served dwell): materialize the child through the engine's
    /// creator, cascade its rows beside the row, then invoke the slot's entry as <c>HandleMenuAction</c> does after a
    /// descent (the no-op sentinel for the usual creator-only slot). An empty child, or a declined creator, cascades
    /// the <see cref="NoActions"/> row instead (the declined case invokes nothing, as before).</summary>
    private static void Descend(int level, int row)
    {
        if (!_menu.Open || !RowOf(level, row, out IntPtr parent, out int slot, out int[] path, out RadialRowTag? h)) return;
        if (h != null && h.Pin.IsFolder)
        {
            if (_menu.OpenChild(level, row, OrEmpty(FolderItems(h.Pin.Folder))))
            { _levelMenu[level + 1] = _levelMenu[0]; _levelPath[level + 1] = Array.Empty<int>(); _levelFolder[level + 1] = h.Pin.Folder; _menu.Level(level + 1).DropTarget = true; }
            return;
        }
        if (parent == IntPtr.Zero || (h != null && !h.Valid)) return;
        int ctor0 = RadialTree.CtorCounter;
        long t0 = Stopwatch.GetTimestamp();
        IntPtr child = RadialTree.Materialize(parent, slot);
        double us = (Stopwatch.GetTimestamp() - t0) * 1_000_000.0 / Stopwatch.Frequency;
        _log?.Info($"[radial] materialize level={level} slot={slot} child=0x{(long)child:x} took={us:F0}us ctor+={RadialTree.CtorCounter - ctor0}");
        if (child == IntPtr.Zero)
        {
            if (!_nullChildWarned) { _nullChildWarned = true; _log?.Info($"[radial] creator declined at level={level} slot={slot} -- No actions shown (logged once)"); }
            _menu.OpenChild(level, row, NoActions);
            return;
        }
        if (_menu.OpenChild(level, row, OrEmpty(ItemsFor(child, path)))) { _levelMenu[level + 1] = child; _levelPath[level + 1] = path; _levelFolder[level + 1] = null; }
        Invoke(parent, level, slot, _menu.Level(level).Items[row].Label);
    }

    /// <summary>The one-row list an empty submenu opens: disabled, null tag, so every input on it is inert.</summary>
    private static readonly CascadeItem[] NoActions = { new("No actions", disabled: true) };
    private static CascadeItem[] OrEmpty(CascadeItem[] items) => items.Length > 0 ? items : NoActions;

    // ---- layout (cold) -------------------------------------------------------------------------

    /// <summary>The rows of the engine menu at <paramref name="path"/> under the layout: the vanilla rows less this
    /// list's hides (never all of them: a list that would empty shows every row) and the slots filed in a folder, plus
    /// this list's reorders and -- at the root -- its hoists, each walked from the root through the engine's creators
    /// and validated at the leaf; a declined step or a mismatch yields a greyed row (<see cref="RadialPins.Compose"/>).</summary>
    private static CascadeItem[] ItemsFor(IntPtr menu, int[] path)
    {
        CascadeItem[] vanilla = RadialTree.BuildItems(menu);
        if (path.Length == 0 && _log != null)
        {
            CascadeItem hdr = RadialIcons.HeaderItem(menu, _kind, _log);   // the item's own composite icon, null tag = no row
            if (hdr.Label != null) { var w = new CascadeItem[vanilla.Length + 1]; w[0] = hdr; vanilla.CopyTo(w, 1); vanilla = w; }
        }
        var rows = RadialPins.ForList(_kind, path);
        var filed = path.Length == 0 ? RadialPins.Filed(_kind) : null;
        if (rows.Count == 0 && (filed == null || filed.Count == 0)) return vanilla;
        var fixedRows = new List<(RadialPin, CascadeItem)>(rows.Count);
        CascadeItem[] shown = vanilla;
        foreach (RadialPin pin in rows) if (pin.Hidden) shown = Without(shown, pin.Path[^1]);
        if (filed != null) foreach (RadialPin pin in filed) shown = Without(shown, pin.Path[^1]);
        if (SlotRows(shown) > 0 || rows.Exists(p => p.IsHoist || p.IsFolder)) vanilla = shown;   // hides that would empty the list are ignored: there must be a row to unhide from
        for (int i = 0; i < rows.Count; i++)
        {
            RadialPin pin = rows[i];
            if (pin.Hidden) continue;
            if (pin.IsFolder)
            {
                int n = RadialPins.ForList(_kind, ReadOnlySpan<int>.Empty, pin.Folder).Count;
                fixedRows.Add((pin, new CascadeItem(pin.Folder, hasChildren: true, count: n.ToString(), tag: new RadialRowTag { Pin = pin, Slot = -1, Valid = true })));
                continue;
            }
            if (!pin.IsHoist)
            {
                if (SlotRow(vanilla, pin.Path[^1]) is CascadeItem it) fixedRows.Add((pin, it));
                continue;
            }
            fixedRows.Add((pin, Resolve(menu, pin, i)));
        }
        return RadialPins.Compose(vanilla, fixedRows);
    }

    /// <summary>A hoist's row: walked from <paramref name="root"/> through the engine's creators (a repeat step returns
    /// the child already made), read at the leaf and validated; a declined step or a mismatch yields the greyed row.</summary>
    private static CascadeItem Resolve(IntPtr root, RadialPin pin, int i)
    {
        if (pin.IsSelf)
        {
            bool known = SelfActions.Resolve(pin, out bool valid);
            _log?.Info($"[radial] pin {i} root={_kind} {pin.Fn} {(known ? valid ? "self-valid" : "self-greyed" : "self-unknown")}");
            return known
                ? new CascadeItem(pin.LeafLabel, pin.Icon, !valid, false, "", new RadialRowTag { Slot = -1, Pin = pin, Valid = valid })
                : RadialPins.Greyed(pin);
        }
        IntPtr m = root;
        int k = 0;
        for (; k < pin.Path.Length - 1 && m != IntPtr.Zero; k++) m = RadialTree.Materialize(m, pin.Path[k]);
        string where = $"[radial] pin {i} root={_kind} path={RadialPins.PathText(pin.Path)}";
        if (m == IntPtr.Zero) { _log?.Info($"{where} declined-at-step={k - 1}"); return RadialPins.Greyed(pin); }
        RadialSlot leaf = RadialTree.ReadSlot(m, pin.Path[^1]);
        if (!RadialPins.Validate(pin, leaf))
        {
            bool paramOff = ((leaf.Param ^ pin.Param) & ~RadialPins.TransientBit) != 0;
            _log?.Info($"{where} mismatch({(paramOff ? "param" : "fn")})");
            return RadialPins.Greyed(pin);
        }
        _log?.Info($"{where} resolved");
        return new CascadeItem(leaf.Label, leaf.Icon, leaf.Disabled, leaf.IsSubMenu, leaf.Count,
            new RadialRowTag { Menu = m, Slot = leaf.Index, Pin = pin, Valid = true });
    }

    /// <summary>A folder's rows: its member hoists, each resolved from the held root, and its filed top-level
    /// slots, each the root's own row (greyed when this root has no such slot), in the position model.</summary>
    private static CascadeItem[] FolderItems(string folder)
    {
        IntPtr root = _levelMenu[0];
        var rows = RadialPins.ForList(_kind, ReadOnlySpan<int>.Empty, folder);
        var fixedRows = new List<(RadialPin, CascadeItem)>(rows.Count);
        CascadeItem[]? vanilla = null;
        for (int i = 0; i < rows.Count; i++)
        {
            RadialPin pin = rows[i];
            fixedRows.Add((pin, root == IntPtr.Zero ? RadialPins.Greyed(pin) : pin.IsHoist ? Resolve(root, pin, i)
                : SlotRow(vanilla ??= RadialTree.BuildItems(root), pin.Path[^1]) ?? RadialPins.Greyed(pin)));
        }
        return RadialPins.Compose(Array.Empty<CascadeItem>(), fixedRows);
    }

    /// <summary>The vanilla row of slot <paramref name="slot"/> among <paramref name="items"/>, if the menu has one.</summary>
    private static CascadeItem? SlotRow(CascadeItem[] items, int slot)
    {
        foreach (CascadeItem it in items) if (it.Tag is int s && s == slot) return it;
        return null;
    }

    private static int SlotRows(CascadeItem[] items)
    {
        int n = 0;
        foreach (CascadeItem it in items) if (it.Tag is int) n++;
        return n;
    }

    /// <summary>The list without the row of vanilla slot <paramref name="slot"/>: a hidden row.</summary>
    private static CascadeItem[] Without(CascadeItem[] items, int slot)
    {
        int i = Array.FindIndex(items, it => it.Tag is int s && s == slot);
        if (i < 0) return items;
        var r = new CascadeItem[items.Length - 1];
        Array.Copy(items, r, i);
        Array.Copy(items, i + 1, r, i, r.Length - i);
        return r;
    }

    /// <summary>Right press on a row: the row-action popup at the row's right edge -- Pin to top (a top-level row
    /// reads Move to top), Unpin, Move up / down within the row's list, Hide (never the list's last row, so a list
    /// cannot be emptied), Move to ▸, Also show on ▸ (a top-level slot as self only), Show hidden, Reset layout for
    /// this root kind; a folder row gets Rename, Move up / down, Remove. No popup on a header or unclassified root.</summary>
    private static void OpenContext(int level, int row)
    {
        GlyphAtlas? atlas = _atlas;
        if (atlas == null || RadialKnobs.Lock.V || _kind == RootKind.Unknown || !RowOf(level, row, out _, out _, out int[] path, out RadialRowTag? h)) return;
        CascadeLevel lv = _menu.Level(level);
        int lead = Lead(lv.Items), last = lv.Items.Length - 1;
        var items = new List<CascadeItem>(9);
        if (h != null && h.Pin.IsFolder)
        {
            items.Add(new CascadeItem("Rename\u2026", tag: RowAction.Rename));
            items.Add(new CascadeItem("Move up", disabled: row <= lead, tag: RowAction.MoveUp));
            items.Add(new CascadeItem("Move down", disabled: row >= last, tag: RowAction.MoveDown));
            items.Add(new CascadeItem("Remove folder", tag: RowAction.RemoveFolder));
        }
        else
        {
            RadialPin? hoist = h?.Pin ?? RadialPins.Find(_kind, path, hoist: true);
            bool inFolder = _levelFolder[level] != null;
            if (hoist == null) items.Add(new CascadeItem(level == 0 || inFolder ? "Move to top" : "Pin to top", disabled: level == 0 && row == lead, tag: RowAction.PinToTop));
            else items.Add(new CascadeItem("Unpin", tag: RowAction.Unpin));
            items.Add(new CascadeItem("Move up", disabled: row <= lead, tag: RowAction.MoveUp));
            items.Add(new CascadeItem("Move down", disabled: row >= last, tag: RowAction.MoveDown));
            if (h == null && !inFolder) items.Add(new CascadeItem("Hide", disabled: SlotRows(lv.Items) < 2, tag: RowAction.Hide));
            items.Add(new CascadeItem("Move to", hasChildren: true, tag: RowAction.MoveTo));
            if (hoist != null || path.Length > 1 || (SelfActions.Ready && SelfKeyOf(level, row) != null))
                items.Add(new CascadeItem("Also show on", hasChildren: true, tag: RowAction.ShowOn));
            int hidden = _levelFolder[level] != null ? 0 : RadialPins.HiddenCount(_kind, _levelPath[level] ?? Array.Empty<int>());
            if (hidden > 0) items.Add(new CascadeItem($"Show hidden ({hidden})", tag: RowAction.Unhide));
        }
        items.Add(new CascadeItem("Reset layout", tag: RowAction.Reset));
        _menu.CloseTo(level);   // the popup takes the child's place beside the row
        _ctxLevel = level; _ctxRow = row; _deferLevel = _deferRow = -1;
        _ctxX = lv.X + lv.W; _ctxY = _menu.RowTop(level, row);
        _ctx.OpenAt(items.ToArray(), _ctxX, _ctxY, atlas, _vpW, _vpH);
    }

    /// <summary>Run a popup row against the row it was opened on, save the layout, and re-compose the touched level
    /// (the top for a hoist change or a reset, the row's own for a move or a hide).</summary>
    private static void RowAct(RowAction a)
    {
        int level = _ctxLevel, row = _ctxRow;
        if (a == RowAction.Everywhere) { ShowEverywhere(); return; }
        if (a == RowAction.Rename)
        {
            if (RowOf(level, row, out _, out _, out _, out RadialRowTag? f) && f != null && f.Pin.IsFolder)
                BeginName(f.Pin.Folder, name => { RadialPins.RenameFolder(f.Pin, name); RadialPins.Save(_log, $"renamed folder root={_kind} to=\"{name}\""); Relayout(0, -1); });
            return;
        }
        CloseCtx();
        if (!RowOf(level, row, out _, out _, out int[] path, out RadialRowTag? h)) return;
        int lead = Lead(_menu.Level(level).Items);
        switch (a)
        {
            case RowAction.PinToTop:
                if (level == 0) MoveRow(0, row, lead); else HoistRow(level, row, Lead(_menu.Level(0).Items));
                return;
            case RowAction.Unpin:
                RadialPin? pin = h?.Pin ?? RadialPins.Find(_kind, path, hoist: true);
                if (pin == null) return;
                RadialPins.Remove(pin);
                RadialPins.Save(_log, $"removed root={_kind} path={RadialPins.PathText(path)}");
                Relayout(0, -1);
                return;
            case RowAction.MoveUp:
            case RowAction.MoveDown:
                MoveRow(level, row, a == RowAction.MoveUp ? row - 1 : row + 1);
                return;
            case RowAction.Hide:
                if (h != null) return;
                RadialPins.Hide(_kind, path);
                RadialPins.Save(_log, $"hidden root={_kind} path={RadialPins.PathText(path)}");
                Relayout(level, -1);
                return;
            case RowAction.Unhide:
                int[] list = _levelPath[level] ?? Array.Empty<int>();
                int shown = RadialPins.Unhide(_kind, list);
                RadialPins.Save(_log, $"unhidden root={_kind} list={RadialPins.PathText(list)} rows={shown}");
                Relayout(level, -1);
                return;
            case RowAction.RemoveFolder:
                if (h == null || !h.Pin.IsFolder) return;
                int back = RadialPins.RemoveFolder(h.Pin);
                RadialPins.Save(_log, $"removed folder root={_kind} \"{h.Pin.Folder}\" members={back}");
                Relayout(0, -1);
                return;
            case RowAction.Reset:
                int n = RadialPins.Reset(_kind);
                _log?.Info($"[radial] layout reset root={_kind} rows={n}");
                RadialPins.Save(_log, "reset");
                Relayout(0, -1);
                return;
        }
    }

    /// <summary>The row's persistable leaf: the hoist it is or that already mirrors it (<paramref name="existing"/>),
    /// else the vanilla row read as a new, unfiled pin with its breadcrumb (the rows that own folder levels are
    /// left out of it). Null when the row is not a leaf CX can persist (a folder, a top-level row that is no self
    /// action, an unnamed handler -- logged once).</summary>
    private static RadialPin? PinOf(int level, int row, out bool existing)
    {
        existing = true;
        if (!RowOf(level, row, out IntPtr m, out int slot, out int[] path, out RadialRowTag? h)) return null;
        if (h != null) return h.Pin.IsFolder ? null : h.Pin;
        RadialPin? mirrored = RadialPins.Find(_kind, path, hoist: true);
        if (mirrored != null) return mirrored;
        existing = false;
        if (m == IntPtr.Zero) return null;
        RadialSlot leaf = RadialTree.ReadSlot(m, slot);
        string fn = RadialPins.FnToken(leaf);
        if (path.Length < 2 && SelfActions.KeyOf(leaf) == null) return null;   // a one-step path is walkable nowhere; a top-level slot files as a reorder (FiledOf), only a self action pins
        if (fn.Length == 0) { RadialPins.WarnUnnamed(_log, leaf); return null; }
        var crumb = new List<string>(level + 1);
        for (int k = 0; k < level; k++) if (_levelFolder[k + 1] == null) crumb.Add(_menu.Level(k).Items[_menu.Level(k + 1).ParentRow].Label);
        crumb.Add(_menu.Level(level).Items[row].Label);
        return new RadialPin { Root = _kind, Path = path, Fn = fn, Param = leaf.Param, Label = RadialPins.Breadcrumb(crumb), Icon = leaf.Icon };
    }

    /// <summary>"Also show on": the popup row pinned at the top of <paramref name="kind"/>'s list with the
    /// same path and validator; the engine builds the same subtree under that root with its own target and greys the
    /// leaf itself when that target is not a valid one (<c>TargetIsValid</c> → <c>DisableEntry</c> in the spell
    /// creators). A row already pinned there is left alone.</summary>
    private static void ShowOn(RootKind kind)
    {
        int level = _ctxLevel, row = _ctxRow;
        CloseCtx();
        RadialPin? pin = PinOf(level, row, out _);
        if (pin == null || kind == _kind) return;
        if (RadialPins.Find(kind, pin.Path, hoist: true) != null) { _log?.Info($"[radial] already shown root={kind} path={RadialPins.PathText(pin.Path)}"); return; }
        RadialPins.AddLike(pin, kind, 0);
        RadialPins.Save(_log, $"shown root={kind} path={RadialPins.PathText(pin.Path)} fn={pin.Fn} param=0x{pin.Param:x}");
    }

    /// <summary>The row's live leaf as a self-action token (a self pin is its own), null when it is not one.</summary>
    private static string? SelfKeyOf(int level, int row)
    {
        if (!RowOf(level, row, out IntPtr m, out int slot, out _, out RadialRowTag? h)) return null;
        if (h != null && h.Pin.IsSelf) return h.Pin.Fn;
        if (h != null && (!h.Valid || h.Pin.IsFolder)) return null;
        if (m == IntPtr.Zero || slot < 0) return null;
        RadialSlot leaf = RadialTree.ReadSlot(m, slot);
        return SelfActions.KeyOf(leaf);
    }

    /// <summary>"Show everywhere (as self)": one self pin at the top of every <see cref="EverywhereKinds"/>
    /// list, this root's included (Creature is every other creature's menu too); a walked hoist already at the path
    /// turns into the self pin where it sits. Fired on a stand-in of the self menu wherever it is, as the self menu
    /// would fire it.</summary>
    private static void ShowEverywhere()
    {
        int level = _ctxLevel, row = _ctxRow;
        CloseCtx();
        string? key = SelfKeyOf(level, row);
        RadialPin? pin = PinOf(level, row, out _);
        if (key == null || pin == null) return;
        var self = new RadialPin { Root = _kind, Path = pin.Path, Fn = key, Param = pin.Param, Label = pin.Label, Icon = pin.Icon };
        int n = 0, turned = 0;
        foreach (RootKind kind in EverywhereKinds)
        {
            RadialPin? had = RadialPins.Find(kind, self.Path, hoist: true);
            if (had != null)
            {
                if (had.IsSelf) continue;
                had.Fn = key; had.Icon = self.Icon; turned++;   // a walked hoist there becomes the self pin in place: same slot, same folder
                continue;
            }
            RadialPins.AddLike(self, kind, 0);
            n++;
        }
        RadialPins.Save(_log, $"shown everywhere {key} path={RadialPins.PathText(self.Path)} param=0x{self.Param:x} added={n} turned={turned}");
        Relayout(0, -1);
    }

    /// <summary>"Move to": the popup row into folder <paramref name="folder"/> ("" = the top list) at its top.</summary>
    private static void MoveTo(string folder)
    {
        int level = _ctxLevel, row = _ctxRow;
        CloseCtx();
        FileRow(level, row, folder, 0, -1);
    }

    /// <summary>The row's fileable pin: <see cref="PinOf"/>, else a top-level slot's reorder (the one it has, or a new
    /// one carrying its label for the greyed row) -- the slot files into a folder as a reorder with <c>in</c> and
    /// leaves the top list. Null for a folder row or a header.</summary>
    private static RadialPin? FiledOf(int level, int row, out bool existing)
    {
        RadialPin? pin = PinOf(level, row, out existing);
        if (pin != null || !RowOf(level, row, out _, out _, out int[] path, out RadialRowTag? h) || h != null || path.Length != 1) return pin;
        pin = RadialPins.Find(_kind, path, hoist: false) is { Hidden: false } r ? r : null;
        existing = pin != null;
        return pin ?? new RadialPin { Root = _kind, Path = path, Label = _menu.Level(level).Items[row].Label };
    }

    /// <summary>File the row into folder <paramref name="folder"/> ("" = the top list) at index <paramref name="at"/>
    /// of it: a hoist or a filed slot is refiled, a deeper vanilla row becomes a hoist, a top-level one a filed reorder.
    /// A folder named here that does not exist yet is made at the top of the top list. The top level relays out with
    /// <paramref name="hover"/> under the pointer.</summary>
    private static void FileRow(int level, int row, string folder, int at, int hover)
    {
        RadialPin? pin = FiledOf(level, row, out bool existing);
        if (pin == null) return;
        if (pin.Label.Length == 0) pin.Label = _menu.Level(level).Items[row].Label;   // a reorder made by Move up/down carries none; the greyed row needs one
        if (folder.Length > 0 && RadialPins.FindFolder(_kind, folder) == null) RadialPins.AddFolder(_kind, folder, 0);
        if (existing) RadialPins.Refile(pin, folder, at);
        else RadialPins.AddLike(pin, _kind, at, folder);
        RadialPins.Save(_log, $"filed root={_kind} path={RadialPins.PathText(pin.Path)} fn={pin.Fn} param=0x{pin.Param:x} in=\"{folder}\" at={at}");
        Relayout(0, hover);
    }

    /// <summary>Open the name editor in the popup's place with <paramref name="initial"/> in the buffer.</summary>
    private static void BeginName(string initial, Action<string> commit)
    {
        _naming = new NameEdit { Commit = commit };
        _naming.Buf.Append(initial);
        ShowName();
    }

    private static void ShowName()
    {
        GlyphAtlas? atlas = _atlas;
        if (atlas == null || _naming == null) return;
        _ctx.OpenAt(new[] { new CascadeItem($"Name: {_naming.Buf}_") }, _ctxX, _ctxY, atlas, _vpW, _vpH);
    }

    /// <summary>SDL keycodes: SDLK_RETURN, SDLK_KP_ENTER (scancode 0x58 | SDLK_SCANCODE_MASK), SDLK_ESCAPE, SDLK_BACKSPACE.</summary>
    private const uint KeyReturn = 0x0D, KeyKpEnter = 0x40000058, KeyEscape = 0x1B, KeyBackspace = 0x08;  // not-an-offset: SDL key/event code
    /// <summary>SDL modifier bits: KMOD_SHIFT (left | right), KMOD_CAPS.</summary>
    private const int ModShift = 0x0003, ModCaps = 0x2000;  // not-an-offset: SDL key/event code

    /// <summary>A key while naming: printable ASCII appends (Shift/Caps upper-case a letter), Backspace deletes,
    /// Enter commits a non-empty trimmed name, Esc cancels. Anything else is swallowed.</summary>
    private static void NameKey(uint key, int mods)
    {
        NameEdit? n = _naming;
        if (n == null) return;
        switch (key)
        {
            case KeyEscape: CloseCtx(); return;
            case KeyReturn:
            case KeyKpEnter:
                string name = n.Buf.ToString().Trim();
                _naming = null; _ctx.Close();
                if (name.Length > 0) n.Commit(name);   // reads _ctxLevel/_ctxRow, so they fall after it
                _ctxLevel = _ctxRow = -1;
                return;
            case KeyBackspace: if (n.Buf.Length > 0) n.Buf.Length--; break;
            default:
                if (key < 0x20 || key > 0x7E || n.Buf.Length >= NameMax) return;
                char c = (char)key;
                if (c >= 'a' && c <= 'z' && ((mods & ModShift) != 0) != ((mods & ModCaps) != 0)) c = (char)(c - 32);
                n.Buf.Append(c);
                break;
        }
        ShowName();
    }

    /// <summary>A drop: into a gap of the row's own list is a reorder to that index; a deeper row into a
    /// gap of the top list is a hoist there; any other pairing is nothing.</summary>
    private static void DropRow(int level, int row, int toLevel, int toIndex)
    {
        if (toLevel == level) { MoveRow(level, row, toIndex > row ? toIndex - 1 : toIndex); return; }
        string? folder = (uint)toLevel < (uint)_levelFolder.Length ? _levelFolder[toLevel] : null;
        if (toLevel == 0) HoistRow(level, row, toIndex);
        else if (folder != null) FileRow(level, row, folder, toIndex, -1);
    }

    /// <summary>Fix the row at index <paramref name="to"/> of its list (clamped past the header), persist, re-compose the level.</summary>
    private static void MoveRow(int level, int row, int to)
    {
        if (!RowOf(level, row, out _, out _, out int[] path, out RadialRowTag? h)) return;
        CascadeLevel lv = _menu.Level(level);
        int lead = Lead(lv.Items);
        if (to < lead) to = lead;
        if (to >= lv.Items.Length || to == row) return;
        RadialPins.Move(_kind, path, h?.Pin, row - lead, to - lead);
        RadialPins.Save(_log, $"moved root={_kind} path={RadialPins.PathText(path)} at={to - lead}");
        Relayout(level, to);
    }

    /// <summary>Hoist a deeper row into the top list at index <paramref name="at"/> (clamped past the header): a
    /// row a hoist already mirrors or a filed slot is refiled there, else the leaf's persistable token and breadcrumb
    /// become the pin; an unnamed handler is refused (logged).</summary>
    private static void HoistRow(int level, int row, int at)
    {
        if (level == 0) return;
        int lead = Lead(_menu.Level(0).Items);
        if (at < lead) at = lead;
        FileRow(level, row, "", at - lead, at);
    }

    /// <summary>Rebuild one level's rows from the engine menu behind it and the layout, in place: the root through
    /// <c>OpenAt</c> at its own corner, a child through <c>OpenChild</c> beside its parent row. The levels below it
    /// close (their parent-row indices would be stale) and their handles drop.</summary>
    private static void Relayout(int level, int hover)
    {
        IntPtr m = _levelMenu[level];
        GlyphAtlas? atlas = _atlas;
        if (m == IntPtr.Zero || atlas == null || level >= _menu.Depth) return;
        string? folder = _levelFolder[level];
        CascadeItem[] items = folder != null ? OrEmpty(FolderItems(folder)) : ItemsFor(m, _levelPath[level] ?? Array.Empty<int>());
        if (items.Length == 0) return;
        if (level == 0) { CascadeLevel lv = _menu.Level(0); _menu.OpenAt(items, lv.X, lv.Y, atlas, _vpW, _vpH); }
        else if (!_menu.OpenChild(level - 1, _menu.Level(level).ParentRow, items)) return;
        for (int i = level + 1; i < _levelMenu.Length; i++) { _levelMenu[i] = IntPtr.Zero; _levelPath[i] = null; _levelFolder[i] = null; }
        if (folder != null) _menu.Level(level).DropTarget = true;
        _deferLevel = _deferRow = -1;   // a queued dwell would index the list just rebuilt
        if ((uint)hover < (uint)items.Length) _menu.Level(level).Hover = hover;
    }

    // ---- render (hot) + the post-present servicer -----------------------------------------------

    /// <summary>Per frame on the render thread, zero-alloc: poll the dwell (a request is queued for
    /// <see cref="Service"/>), note a font re-bake / master off / module gone as a queued close, read the look knobs
    /// into the style, emit the cascade into the reused batch with the face stamped at open, upload it, then draw
    /// the icon quads through the engine-texture path.</summary>
    private static void Draw()
    {
        if (!_menu.Open) return;
        GlyphAtlas? atlas = _atlas;
        if (atlas == null) return;
        CascadeAction a = _ctx.Open ? CascadeAction.None : _menu.Tick(Environment.TickCount64);
        if (a.Kind == CascadeActionKind.RequestChildren) { _deferLevel = a.Level; _deferRow = a.Row; }
        if (_deferClose == null)
        {
            if (!RadialKnobs.Enabled.V) _deferClose = CloseOff;
            else if (!GameGate.InModule) _deferClose = CloseArea;
            else if (_epoch != TextRig.AtlasEpoch) _deferClose = CloseFont;
        }
        if (_deferClose != null) return;
        VertexBatch b = _batch ??= new VertexBatch(atlas.SolidU, atlas.SolidV);
        b.Reset(atlas.SolidU, atlas.SolidV);
        b.Outline = RadialKnobs.Font.HaloPx;
        _style = CascadeStyle.Current;
        _style.Alpha = RadialKnobs.Opacity.V;
        _style.AnimMs = RadialKnobs.Anim.V ? RadialKnobs.AnimMs.V : 0; _style.NowMs = Environment.TickCount64;
        _menu.Draw(b, atlas, atlas.Ascent, in _style);
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
        foreach (IconQuad q in _menu.Icons)
        {
            if (q.Handle != IntPtr.Zero) RadialIcons.Draw(q.Handle, q.X, q.Y, q.Size, q.Alpha);
            else TextRig.DrawIcon(q.Resref, q.X, q.Y, q.Size, q.Size, q.Alpha);
        }
        if (!_ctx.Open) return;
        VertexBatch cb = _ctxBatch ??= new VertexBatch(atlas.SolidU, atlas.SolidV);   // after the icons: the popup is on top of everything
        cb.Reset(atlas.SolidU, atlas.SolidV);
        cb.Outline = b.Outline;
        _ctx.Draw(cb, atlas, atlas.Ascent, in _style);
        TextRig.DrawBatch(cb.AsSpan(), cb.VertexCount);
    }

    /// <summary>PostPresent: serve what <see cref="Draw"/> queued -- a close, else one dwell descent. The draw runs
    /// inside <c>CGuiMan::UpdateAndRender</c>, where the engine never builds a submenu, so both are queued for here
    /// instead -- same thread, after the present, before the next pump -- and the draw itself stays zero-alloc.</summary>
    private static void Service()
    {
        if (!_menu.Open) return;
        string? close = _deferClose;
        if (close != null) { _deferClose = null; Close(close); return; }
        if (_ctx.Open) { _deferLevel = _deferRow = -1; return; }   // a dwell queued the frame before the popup opened
        if (_deferLevel < 0) return;
        int l = _deferLevel, r = _deferRow;
        _deferLevel = _deferRow = -1;
        if (l < _menu.Depth && (uint)r < (uint)_menu.Level(l).Items.Length) Descend(l, r);
    }

    /// <summary>The verification lines for a live session under <c>logs=1</c>: one per open, one per slot. The
    /// event-object and ctor-counter words are the §9.2/§9.1 live probes (should read 0 / a small per-open delta).</summary>
    private static void LogTree(IntPtr menu)
    {
        var log = _log;
        if (log == null || !ExtenderLog.Enabled) return;
        RootKind kind = RadialTree.Classify(menu);
        var (x, y) = RadialTree.Position(menu);
        int ctor = RadialTree.CtorCounter;
        int since = _ctorAtLastOpen < 0 ? -1 : ctor - _ctorAtLastOpen;
        _ctorAtLastOpen = ctor;
        log.Info($"[radial] open kind={kind} root=0x{(long)menu:x} xy=({x},{y}) target=0x{RadialTree.TargetId(menu, kind):x} " +
                 $"evt=0x{(long)RadialTree.EventObject:x} ctor={ctor} sinceLastOpen={since}");
        for (int i = 0; i < RadialTree.SlotCount; i++)
        {
            RadialSlot s = RadialTree.ReadSlot(menu, i);
            string what = s.Kind == SlotKind.Empty ? "empty" : s.IsSubMenu ? "submenu" : "entry";
            log.Info($"[radial]  slot {i}: {what} \"{s.Label}\" icon={s.Icon} count=\"{s.Count}\" disabled={s.Disabled} param=0x{s.Param:x} fn=0x{(long)s.Fn:x}" +
                     (s.Child != IntPtr.Zero ? $" child=0x{(long)s.Child:x}" : ""));
        }
    }

    // ---- self-check ----------------------------------------------------------------------------
}
