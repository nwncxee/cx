using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Quickbar;

/// <summary>What one engine quick button shows, read for the CX bar each frame.</summary>
internal readonly record struct QuickSlot(byte Type, uint Item, bool Disabled, int Outline, string Icon, string Overlay, string Count);

/// <summary>
/// The engine side of the CX quickbar. The engine's <c>CPanelQuickBar</c> stays alive off-screen and keeps doing all of
/// the work -- the server sync, counts, ghosting, every action -- while CX draws its 36 buttons. Two hooks copy what a
/// button keeps only as Aurora models (the icon's resref, the count text); the GUI hit-test answers a CX slot with its
/// engine button, so a click, a right-click menu or a drop there is the engine's own; its tooltip text is CX's to show.
/// </summary>
internal static unsafe class QuickbarNative
{
    /// <summary>The trampolines and the per-button copies, one lifetime: the session.</summary>
    private sealed class QuickbarNativeState
    {
        public QuickButtonSetIconFn SetIcon;
        public QuickButtonSetCountTextFn SetCount;
        public QuickbarClickButtonFn Click;
        public GuiObjectHitCheckFn* Hit;
        public GuiManShowTooltipTextFn* Show;
        /// <summary>Set around CX's own <c>HandleTriggerTooltip</c> call: the text is caught here, not shown.</summary>
        public bool Catching;
        /// <summary>Every setter a slot swap calls resolved; off, the bar works and its buttons stay put.</summary>
        public bool SwapReady;
        public string Caught = "";
        public readonly string[] Icon = new string[Buttons], Overlay = new string[Buttons], Count = new string[Buttons];
        public QuickbarNativeState() { Array.Fill(Icon, ""); Array.Fill(Overlay, ""); Array.Fill(Count, ""); }
    }

    private static QuickbarNativeState _s = new();

    internal const int Buttons = EngineLayout.QuickbarButtonCount;
    /// <summary>Below tab-target's layer (50), which hides the GUI from a fire at the lock.</summary>
    private const int HitPriority = 10;
    /// <summary>The interactivity mask <c>CGuiMan::HandleMouseWheel</c> hit-tests with.</summary>
    private const int WheelMask = 8;
    /// <summary>The disabled bit of <see cref="EngineLayout.GuiObjectFlags"/>.</summary>
    private const uint FlagDisabled = 1;
    /// <summary><see cref="QuickSlot.Type"/> of an item button (<c>HandleLeftButton</c>'s case 1).</summary>
    internal const byte TypeItem = 1;
    /// <summary><see cref="QuickSlot.Outline"/>: none, an equipped item, an active mode.</summary>
    internal const int OutlineNone = 0, OutlineEquip = 1, OutlineMode = 2;
    private static ReadOnlySpan<byte> AnimEquip => "qbEquip\0"u8;
    private static ReadOnlySpan<byte> AnimMode => "qbMode\0"u8;

    /// <summary>Outside alloy.hitcheck's tooltip probe (0): a bubble swallowed here never shows, so the probe has nothing to track.</summary>
    private const int ShowPriority = 10;

    /// <summary>Arm the five hooks; false (and a logged reason) leaves the stock bar the only one.</summary>
    public static bool Attach(ExtenderLog log, HookService hooks)
    {
        if (!SymbolMap.Require(log, "CX quickbar", SymbolMap.QuickButtonSetIcon, SymbolMap.QuickButtonSetCountText, SymbolMap.QuickbarClickButton,
                               SymbolMap.QuickButtonHandleLeftButton, SymbolMap.GuiObjectHitCheck, SymbolMap.GuiMan,
                               SymbolMap.GuiManShowTooltipText)) return false;
        if (!EngineLayout.Require(log, "CX quickbar", "CGuiQuickButton", "CGuiButton", "CGuiObject", "CGuiIcon", "CPanelQuickBar", "CGuiInGame", "CGuiDragSource")) return false;
        if (DragMove == IntPtr.Zero) log.Always("QuickbarService: no CGuiDragSource::HandleMouseMoveCaptured -- a drop on the CX bar's grip falls through and a dragged icon draws under the bar");
        _s.SwapReady = SymbolMap.Require(log, "CX quickbar slot drag", SymbolMap.QuickbarSendButton, SymbolMap.QuickbarSetEmpty, SymbolMap.QuickbarSetItem,
            SymbolMap.QuickbarSetSkill, SymbolMap.QuickbarSetFeat, SymbolMap.QuickbarSetDialog, SymbolMap.QuickbarSetAttack, SymbolMap.QuickbarSetEmote,
            SymbolMap.QuickbarSetModeToggle, SymbolMap.QuickbarSetQuickChat, SymbolMap.QuickbarSetCancelPoly, SymbolMap.QuickbarSetFamiliar,
            SymbolMap.QuickbarSetExamine, SymbolMap.QuickbarSetBarter, SymbolMap.QuickbarSetRollDie, SymbolMap.QuickbarSetAssociate,
            SymbolMap.QuickButtonSetSpell, SymbolMap.QuickButtonSetSla);
        _s.SetIcon = hooks.InstallQuickButtonSetIcon(&OnSetIcon);
        _s.SetCount = hooks.InstallQuickButtonSetCountText(&OnSetCount);
        _s.Click = hooks.InstallQuickbarClickButton(&OnClick);
        _s.Hit = hooks.ChainGuiObjectHitCheck(&OnHit, HitPriority);
        _s.Show = hooks.ChainGuiManShowTooltipText(&OnShowTip, ShowPriority);
        if (_s.SetIcon != null && _s.SetCount != null && _s.Click != null && _s.Hit != null && _s.Show != null) return true;
        log.Always("QuickbarService: the CX quickbar stands down -- a hook was refused (see the hook: lines above)");
        return false;
    }

    // ---- the hooks ----

    [UnmanagedCallersOnly]
    private static void OnSetIcon(IntPtr button, IntPtr icon, IntPtr overlay)
    {
        int b = Index(button);
        if (b >= 0) { _s.Icon[b] = ResRef(icon); _s.Overlay[b] = ResRef(overlay); }
        _s.SetIcon(button, icon, overlay);
    }

    [UnmanagedCallersOnly]
    private static void OnSetCount(IntPtr button, IntPtr text)
    {
        int b = Index(button);
        if (b >= 0) _s.Count[b] = Native.EngineText(Native.ReadExo(text));   // before the forward: the Win64 callee destroys its copy
        _s.SetCount(button, text);
    }

    /// <summary>The game's own quickbar keys stand down while the CX bar shows: its bindings replace them.</summary>
    [UnmanagedCallersOnly]
    private static void OnClick(IntPtr panel, byte slot)
    {
        if (QuickbarService.CxActive) return;
        _s.Click(panel, slot);
    }

    /// <summary>A point over the CX bar is its nearest slot's engine button (nothing while CX drags one slot onto another, so the
    /// pressed button sees no release over itself and does not fire), whatever the engine found there (the CX bar draws
    /// over the stock GUI); the wheel (mask 8) is left alone so it still zooms. GUI coordinates are y-up.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnHit(IntPtr self, int x, int y, int fresh, int mask)
    {
        IntPtr hit = (*_s.Hit)(self, x, y, fresh, mask);
        if (!QuickbarService.CxShowing || self == IntPtr.Zero || mask == WheelMask) return hit;
        if (QuickbarService.SlotDragging) return QuickbarService.OverBar(x, new CGuiMan(self).VpHeight - y) ? IntPtr.Zero : hit;
        int b = QuickbarService.SlotAt(x, new CGuiMan(self).VpHeight - y);
        IntPtr button = b < 0 ? IntPtr.Zero : Button(b);
        return button != IntPtr.Zero ? button : hit;
    }

    /// <summary>The engine's bubble for a quick button stands down while the CX bar shows (CX draws its own), and the text of
    /// CX's own trigger is caught instead of shown. <c>TriggerToolTip</c> stores the hit object at +0x68 before it calls.</summary>
    [UnmanagedCallersOnly]
    private static void OnShowTip(IntPtr self, IntPtr text, int instant)
    {
        if (_s.Catching) { _s.Caught = Native.ReadExo(text); return; }
        if (QuickbarService.CxShowing && self != IntPtr.Zero && Index(new CGuiMan(self).TooltipObject) >= 0) return;
        (*_s.Show)(self, text, instant);
    }

    /// <summary>Button <paramref name="b"/>'s tooltip as the engine words it (its own <c>HandleTriggerTooltip</c>), colour
    /// tokens dropped; "" when it has none. Allocates: call on a hover change, not per frame.</summary>
    public static string TipText(int b)
    {
        IntPtr p = Button(b);
        if (p == IntPtr.Zero) return "";
        _s.Caught = ""; _s.Catching = true;
        try { new CGuiQuickButton(p).HandleTriggerTooltip(); }
        finally { _s.Catching = false; }
        if (_s.Caught.Length == 0) return "";
        var sb = new System.Text.StringBuilder();
        foreach (var run in Chat.Core.ColorRunParser.Parse(_s.Caught)) sb.Append(run.Text);
        return sb.ToString().Trim();
    }

    // ---- reads ----

    private static readonly IntPtr DragMove = Native.Sym(SymbolMap.DragSourceMoveCaptured);

    /// <summary>The engine's live drag past its slop and the <c>CGuiIcon*</c> it drags (zero when it shows none): the mouse
    /// captor whose <c>HandleMouseMoveCaptured</c> slot is the drag source's, which no derived source overrides.</summary>
    public static bool EngineDrag(out IntPtr icon)
    {
        icon = IntPtr.Zero;
        IntPtr gm = Native.Sym(SymbolMap.GuiMan);
        if (gm == IntPtr.Zero || *(IntPtr*)gm == IntPtr.Zero || DragMove == IntPtr.Zero) return false;
        IntPtr src = new CGuiMan(*(IntPtr*)gm).MouseCapture;
        if (src == IntPtr.Zero || *(IntPtr*)(*(IntPtr*)src + EngineLayout.DragMoveSlot) != DragMove) return false;
        var drag = new CGuiDragSource(src);
        if (drag.Dragging == 0) return false;
        icon = drag.Icon;
        return true;
    }

    public static bool EngineDragging() => EngineDrag(out _);

    /// <summary>The live <c>CPanelQuickBar*</c>, or zero outside a module.</summary>
    public static IntPtr Panel()
    {
        IntPtr app = PlayerIdentity.App();
        if (app == IntPtr.Zero) return IntPtr.Zero;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        return intern.IsNull || intern.InGameGui.IsNull ? IntPtr.Zero : intern.InGameGui.Quickbar;
    }

    /// <summary>Engine button <paramref name="b"/> (set × 12 + column), or zero.</summary>
    public static IntPtr Button(int b) => Button(Panel(), b);

    /// <summary>Button <paramref name="b"/> of a panel the caller already walked to.</summary>
    public static IntPtr Button(IntPtr panel, int b)
        => panel == IntPtr.Zero || (uint)b >= Buttons ? IntPtr.Zero : panel + EngineLayout.QuickbarButtons + b * EngineLayout.QuickbarButtonStride;

    private static int Index(IntPtr button)
    {
        IntPtr panel = Panel();
        if (panel == IntPtr.Zero) return -1;
        long off = (long)button - (long)panel - EngineLayout.QuickbarButtons;
        int stride = EngineLayout.QuickbarButtonStride;
        return off < 0 || off % stride != 0 || off / stride >= Buttons ? -1 : (int)(off / stride);
    }

    /// <summary>Button <paramref name="b"/> of <paramref name="panel"/> as it stands. Render thread; allocates nothing.</summary>
    public static QuickSlot Read(IntPtr panel, int b)
    {
        IntPtr p = Button(panel, b);
        if (p == IntPtr.Zero) return default;
        var qb = new CGuiQuickButton(p);
        IntPtr anim = new CGuiButton(p).NormalAnim;
        int outline = anim == IntPtr.Zero ? OutlineNone : NameIs(anim, AnimEquip) ? OutlineEquip : NameIs(anim, AnimMode) ? OutlineMode : OutlineNone;
        return new QuickSlot(qb.Type, qb.ItemOid, (new CGuiObject(p).Flags & FlagDisabled) != 0, outline, _s.Icon[b], _s.Overlay[b], _s.Count[b]);
    }

    private static bool NameIs(IntPtr s, ReadOnlySpan<byte> z)
    {
        for (int i = 0; i < z.Length; i++) if (((byte*)s)[i] != z[i]) return false;
        return true;
    }

    private static string ResRef(IntPtr icon)
    {
        if (icon == IntPtr.Zero) return "";
        ReadOnlySpan<byte> name = new CGuiIcon(icon).Resref;
        int n = name.IndexOf((byte)0);
        return System.Text.Encoding.ASCII.GetString(n < 0 ? name : name[..n]).ToLowerInvariant();
    }

    /// <summary>Press button <paramref name="b"/> as its key would: the stock key's own gates first -- no player creature,
    /// or one the player commands (<c>HandleInputEvent</c>'s test), and the Options panel shut.</summary>
    public static void Press(int b)
    {
        IntPtr button = Button(b);
        if (button == IntPtr.Zero || GameGate.OptionsOpen || EngineModal()) return;
        IntPtr app = PlayerIdentity.App();
        uint me = app == IntPtr.Zero ? Inventory.InventoryModel.Invalid : new CClientExoApp(app).Internal.PlayerCreatureId;
        IntPtr cre = me == Inventory.InventoryModel.Invalid ? IntPtr.Zero : EngineFn.GetGameObject(app, me);
        if (cre != IntPtr.Zero && new CNWCCreature(cre).AiFlags == 0) return;
        EngineFn.QuickButtonHandleLeftButton(button);
    }

    // ---- slot swap ----

    /// <summary>A button's content in the setters' own terms: <c>SendButtonUpdateToServer</c>'s reads.</summary>
    private readonly record struct Content(byte Type, uint Item, uint Item2, int Param, long Spell, uint Target);

    private static Content Take(IntPtr p)
    {
        var q = new CGuiQuickButton(p);
        return new Content(q.Type, q.ItemOid, q.ItemOid2, q.Param, q.Spell, q.Target);
    }

    /// <summary>The types a swap re-sets through their own setter: empty, item, spell, skill, feat, dialog, attack, emote,
    /// mode, roll, familiar, associate, examine, barter, quick chat, cancel polymorph, spell-like ability. ponytail: the
    /// command line (two owned strings) and the DM buttons stay put; copy their strings first to move them.</summary>
    public static bool Movable(byte type) => type is 0 or 1 or 2 or 3 or 4 or 6 or 7 or 8 or 0xa or 0x25 or 0x26 or 0x27 or 0x28 or 0x29 or 0x2a or 0x2b or 0x2c;

    public static bool SwapReady => _s.SwapReady;

    /// <summary>The type engine button <paramref name="b"/> holds (0 empty).</summary>
    public static byte TypeOf(int b) { IntPtr p = Button(b); return p == IntPtr.Zero ? (byte)0 : new CGuiQuickButton(p).Type; }

    /// <summary>Swap engine buttons <paramref name="a"/> and <paramref name="b"/> the way the server's own set does: both set
    /// with the send gate shut, then one update each. False (nothing changed) when either holds a type <see cref="Movable"/> refuses.</summary>
    public static bool Swap(int a, int b, ExtenderLog? log)
    {
        IntPtr panel = Panel();
        IntPtr pa = Button(panel, a), pb = Button(panel, b);
        if (pa == IntPtr.Zero || pb == IntPtr.Zero || a == b) return false;
        Content ca = Take(pa), cb = Take(pb);
        if (!Movable(ca.Type) || !Movable(cb.Type)) return false;
        var qp = new CPanelQuickBar(panel);
        int gate = qp.SendGate;
        qp.SendGate = 0;
        try { Put(panel, a, pa, cb); Put(panel, b, pb, ca); }
        finally { qp.SendGate = gate; }
        EngineFn.QuickbarSendButton(panel, pa);
        EngineFn.QuickbarSendButton(panel, pb);
        if (new CGuiQuickButton(pa).Type != cb.Type || new CGuiQuickButton(pb).Type != ca.Type)
            log?.Always($"QuickbarService: swapping buttons {a} and {b} left types {new CGuiQuickButton(pa).Type}/{new CGuiQuickButton(pb).Type}, wanted {cb.Type}/{ca.Type} -- the engine refused a setter");
        return true;
    }

    private static void Put(IntPtr panel, int slot, IntPtr p, Content c)
    {
        byte s = (byte)slot;
        EngineFn.QuickbarSetEmpty(panel, s);   // SetButtonItem pairs into a slot already holding one item instead of replacing it
        switch (c.Type)
        {
            case 1: EngineFn.QuickbarSetItem(panel, s, c.Item, c.Param, c.Item2); break;
            case 2: EngineFn.QuickButtonSetSpell(p, c.Spell); break;
            case 3: EngineFn.QuickbarSetSkill(panel, s, c.Param); break;
            case 4: EngineFn.QuickbarSetFeat(panel, s, c.Param); break;
            case 6: EngineFn.QuickbarSetDialog(panel, s); break;
            case 7: EngineFn.QuickbarSetAttack(panel, s); break;
            case 8: EngineFn.QuickbarSetEmote(panel, s, c.Param); break;
            case 0xa: EngineFn.QuickbarSetModeToggle(panel, s, c.Param); break;
            case 0x25: EngineFn.QuickbarSetRollDie(panel, s, c.Param); break;
            case 0x26: EngineFn.QuickbarSetFamiliar(panel, s); break;
            case 0x27: EngineFn.QuickbarSetAssociate(panel, s, c.Param >> 16, (ushort)c.Param, c.Target); break;
            case 0x28: EngineFn.QuickbarSetExamine(panel, s); break;
            case 0x29: EngineFn.QuickbarSetBarter(panel, s); break;
            case 0x2a: EngineFn.QuickbarSetQuickChat(panel, s, c.Param); break;
            case 0x2b: EngineFn.QuickbarSetCancelPoly(panel, s, c.Param); break;
            case 0x2c: EngineFn.QuickButtonSetSla(p, c.Spell); break;
        }
    }

    /// <summary>The item's inventory size in cells (1x1 when it or its base item is gone).</summary>
    public static void ItemCells(uint oid, out int w, out int h)
    {
        w = h = 1;
        IntPtr app = PlayerIdentity.App(), rules = Native.Sym(SymbolMap.Rules);
        IntPtr go = app == IntPtr.Zero ? IntPtr.Zero : EngineFn.GetGameObject(app, oid);
        if (go == IntPtr.Zero || rules == IntPtr.Zero || *(IntPtr*)rules == IntPtr.Zero || new CGameObject(go).Type != GameObjItem) return;
        IntPtr item = new CGameObject(go).AsNWCItem();
        IntPtr bases = new CNWRules(*(IntPtr*)rules).BaseItems;
        var bi = new CNWBaseItem(item == IntPtr.Zero || bases == IntPtr.Zero ? IntPtr.Zero : EngineFn.GetBaseItem(bases, new CNWCItem(item).BaseItem));
        if (bi.IsNull) return;
        w = Math.Max(1, (int)bi.InvSlotWidth); h = Math.Max(1, (int)bi.InvSlotHeight);
    }

    /// <summary>CGameObject type byte of an item (<c>GetItemByGameObjectID</c>'s test).</summary>
    private const byte GameObjItem = 6;

    /// <summary>An engine modal panel is up (a split or gold prompt, a confirm): the stock GUI takes nothing else.</summary>
    public static bool EngineModal()
    {
        IntPtr gm = Native.Sym(SymbolMap.GuiMan);
        return gm != IntPtr.Zero && *(IntPtr*)gm != IntPtr.Zero && new CGuiMan(*(IntPtr*)gm).ModalCount > 0;
    }
}
