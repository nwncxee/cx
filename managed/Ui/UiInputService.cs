using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Ui;

/// <summary>
/// The framework's raw-input layer: it owns the SDL/engine input hooks, the centralized keyboard gate, the
/// environmental chord gates and the pointer cursor, feeding every captured event into the <see cref="UiHost"/>
/// arbiter so no service owns capture. Precedence: focused content → chord consumers → gameplay keybinds.
/// </summary>
/// <remarks>State is static (the <c>[UnmanagedCallersOnly]</c> handlers demand it); the hooks are cold, so they may allocate.</remarks>
public sealed unsafe class UiInputService : IExtenderService
{
    private static ExtenderLog? _log;

    // ---- SDL_Event layout (verified against WinMessageHandler; same offsets the old capture used) ----
    private const uint SdlKeyDown = 0x300, SdlKeyUp = 0x301, SdlTextInput = 0x303;  // not-an-offset: SDL key/event code
    private const uint SdlMouseMotion = 0x400, SdlMouseDown = 0x401, SdlMouseUp = 0x402, SdlMouseWheel = 0x403;  // not-an-offset: SDL key/event code

    // NUI keyboard-focus probe: ctx->active (0x6730) is the focused window; win->edit.active (0x18c) is nonzero
    // while an edit box has focus. NUI runs a parallel SDL pump, so a key typed in a NUI field still reaches us.
    /// <summary>nuklear's window list, as <c>nk_window_is_any_hovered</c> @0xee69f0 walks it: ctx->begin, win->flags
    /// (skipped when hidden|closed), win->bounds (x,y,w,h floats in SDL window pixels -- nk_sdl_handle_event feeds
    /// the raw event x/y), win->next. Minimized windows show only a header, so they cover nothing.</summary>
    private const uint NkWinHiddenClosed = 0x4400, NkWinMinimized = 0x10000;  // not-an-offset: nuklear window flags
    // Native (vanilla) dialog probe: *(gui+0x70) non-null while a native conversation panel is up.
    private const int UiCursorId = 3;   // engine walk/default ground pointer

    private static bool _focused = true;
    private static readonly HashSet<uint> _keysDown = new();   // held keys, for the auto-repeat gate on chords

    private static IntPtr _exoInputGlobal, _appMgr;
    // Resolved at type init, not Initialize: the engine-GUI probes serve MouseLookService before Initialize runs.
    private static readonly IntPtr _nuklearGlobal = Native.Sym(SymbolMap.Nuklear);
    private static readonly NkWindowIsAnyHoveredFn _nkAnyHovered = EngineFn.NkWindowIsAnyHovered;
    private static readonly IntPtr _guiManGlobal = Native.Sym(SymbolMap.GuiMan);         // &g_pGuiMan
    private static bool _kbHeld;   // our view of the engine keyboard-capture gate; edges drive the modal reset

    // Trampolines to the engine originals.
    private static HandleWMKeyDownFn _keyOrig;
    /// <summary>Chain slot on the SDL pump (MouseLookService sits above us); <c>*_winMsg</c> is the next layer.</summary>
    private static WinMessageHandlerFn* _winMsg;
    private static OnGainFocusFn _gainOrig;
    private static OnLostFocusFn _lostOrig;
    private static UpdateMouseCursorAppearanceFn _origUpdateCursor;
    private static SetMouseCursorFn _origSetCursor;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _exoInputGlobal  = Native.Sym(SymbolMap.ExoInput);
        _appMgr          = Native.Sym(SymbolMap.AppManager);

        UiHost.KeyboardGate = KbGate;   // centralized engine keyboard gate: one owner for all consumers

        _keyOrig    = hooks.InstallHandleWMKeyDown(&OnKeyDown);
        _winMsg     = hooks.ChainWinMessageHandler(&OnWinMessage, 0);
        _gainOrig   = hooks.InstallOnGainFocus(&OnGain);
        _lostOrig   = hooks.InstallOnLostFocus(&OnLost);

        // Cursor fold needs BOTH hooks: OnUpdateCursor decides "over UI", OnSetMouseCursor forces the pointer.
        // A Zero trampoline means Install did not hook the symbol (prepare failed / unresolved), so the handler is
        // never active -- but only arm the fold when both resolved, mirroring the old CursorService clean bail, so
        // OnUpdateCursor does no useless work when the forcing half is missing.
        _origUpdateCursor = hooks.InstallUpdateMouseCursorAppearance(&OnUpdateCursor);
        if (_origUpdateCursor != null)
        {
            _origSetCursor = hooks.InstallSetMouseCursor(&OnSetMouseCursor);
            _cursorFoldActive = _origSetCursor != null;
            if (!_cursorFoldActive)
                log.Info("UiInputService: cursor fold disabled (SetMouseCursor hook unavailable)");
        }

        log.Info(_winMsg != null
            ? "UiInputService: input capture owned (keys/mouse/focus) -- routing through the UiHost arbiter"
            : "UiInputService: input capture not installed (Chain refused: symbol unresolved or single-owned)");
    }

    // ---- the centralized keyboard gate (was ChatService.KeyboardMode) --------------------------
    /// <summary>Drive the engine's gameplay-key gate for the keyboard owner. On the off→on edge only, also run
    /// vanilla's <c>TurnOffKeyboardModal</c> (the step CGuiEdit::SetFocus does after KeyboardModeOn) to clear the
    /// WASD drive latch and send AbortDriveControl -- without it a keyup gated mid-walk leaves the player stuck
    /// moving. Edge-gated so a re-sync doesn't re-hit the HUD on every key.</summary>
    private static void KbGate(bool on)
    {
        if (_exoInputGlobal == IntPtr.Zero) return;
        IntPtr inst = *(IntPtr*)_exoInputGlobal;   // g_pExoInput may be null early in-game
        if (inst == IntPtr.Zero) return;
        if (on)
        {
            if (EngineFn.KeyboardModeOn != null) EngineFn.KeyboardModeOn(inst);
            // TurnOffKeyboardModal double-derefs g_pAppManager unguarded -> gate on a live app manager.
            if (!_kbHeld && EngineFn.TurnOffKeyboardModal != null && _appMgr != IntPtr.Zero && *(IntPtr*)_appMgr != IntPtr.Zero)
                EngineFn.TurnOffKeyboardModal();
        }
        else if (EngineFn.KeyboardModeOff != null) EngineFn.KeyboardModeOff(inst);
        _kbHeld = on;
    }

    private static int RawMods() => EngineFn.SdlGetModState != null ? EngineFn.SdlGetModState() : 0;

    /// <summary>Raw-key probe, reporting the framework-level gates. OFF by default -- it logs every keydown,
    /// so the whole point of the feature (not being a keylogger) forbids it as a default. Flipped by
    /// <c>hotkeys.keydiag</c>, which <see cref="CX.Services.Hotkeys.Hotkeys"/> owns; volatile because the command runs on the input
    /// thread and the pump reads it on its own.</summary>
    internal static volatile bool KeyDiag;

    // ---- key path: HandleWMKeyDownMessage --------------------------------------------------------
    [UnmanagedCallersOnly]
    private static int OnKeyDown(uint key, uint flags)
    {
        int mods = RawMods();
        if (KeyDiag)
            _log?.Info($"[keydiag] key={key} focused={_focused} owner={UiHost.AnyKeyboardOwner} " +
                       $"suppress={UiHost.AnySuppressChords} nui={NuiEditFocused()} kbmode={KeyboardModeActive()} natdlg={NativeDialogOpen()}");
        // Precedence: a window (keyboard owner or selective modal) eats it -> don't run the engine original.
        // Unless a NUI edit box has focus: NUI draws over us and runs its own SDL pump, so a key routed to our
        // keyboard owner as well would land in BOTH fields at once.
        if (!NuiEditFocused() && UiHost.DispatchKey(key, mods)) return 0;
        // Then a game edit box's selection keys (ui.textedit), ahead of the engine's own handling of the same key.
        if (!NuiEditFocused() && CX.Alloy.Db.EditSelect.OnKey(key, mods)) return 0;
        // Chord layer: only on the fresh press (auto-repeat gate), and only when the gate is clear.
        bool fresh = _keysDown.Add(key);
        if (fresh && ChordGateClear()) UiHost.FireChords(key, mods);
        return _keyOrig != null ? _keyOrig(key, flags) : 0;   // observe, never consume gameplay
    }

    /// <summary>The environmental chord gate: hotkeys fire only while the app is focused and no other text sink or
    /// modal owns input -- the keyboard owner (chat typing), a window withholding chords (our dialog), a NUI edit
    /// box, the engine keyboard mode (debug console / vanilla field), or a native conversation panel. Any null link
    /// reads as "clear". Keys the owner ate never reach here, so the owner check only matters for mouse chords.</summary>
    internal static bool ChordGateClear()
        => _focused && !UiHost.AnyKeyboardOwner && !UiHost.AnySuppressChords
           && !NuiEditFocused() && !KeyboardModeActive() && !NativeDialogOpen();

    // ---- SDL pump: WinMessageHandler ------------------------------------------------------------
    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (ev != IntPtr.Zero)
        {
            switch (*(uint*)ev)
            {
                case SdlKeyDown:
                    // Swallow a modal window's key HERE, before CExoInput::AddEvent queues the scancode keybind
                    // (it runs pre-HandleWMKeyDown with no modifier check). Only when no full owner is typing
                    // (an owner suppresses keybinds via the keyboard-mode gate). Keyups still pass -> no stuck key.
                    if (!UiHost.AnyKeyboardOwner
                        && UiHost.DispatchKeyPumpSwallow(((SdlEvent*)ev)->SymU, RawMods()))
                        return 0;
                    break;
                case SdlKeyUp:
                    _keysDown.Remove(((SdlEvent*)ev)->SymU);
                    break;
                case SdlTextInput:
                    // ...and the same for finished text: a focused NUI edit box owns the keyboard outright.
                    if (!NuiEditFocused())
                    {
                        if (!UiHost.AnyKeyboardOwner) CX.Alloy.Db.EditSelect.BeforeText();   // typing replaces a game edit box's selection
                        UiHost.DispatchText((nint)((SdlEvent*)ev)->Text);   // finished UTF-8 -> the keyboard owner's edit line
                    }
                    break;
                case SdlMouseMotion:
                {
                    int mx = ((SdlEvent*)ev)->X, my = ((SdlEvent*)ev)->Y;
                    if (UiHost.HoverToFocus) UiHost.HoverFocusAt(mx, my);
                    if (UiHost.DispatchMouseMotion(mx, my))
                    {
                        // Swallowing motion (a settings panel open, a drag in progress) starves nuklear's own
                        // feed, which hangs off CExoInput::AddEvent below us -- its mouse position would freeze
                        // and NuiHovered() would then answer from wherever the pointer last was NOT over us.
                        // Hand it the motion directly; it is state, not an action, so nothing acts twice.
                        if (EngineFn.NkSdlCopyEvent != null) EngineFn.NkSdlCopyEvent(ev);
                        return 0;
                    }
                    break;
                }
                case SdlMouseDown:
                {
                    byte btn = ((SdlEvent*)ev)->Button;
                    int mx = ((SdlEvent*)ev)->X, my = ((SdlEvent*)ev)->Y;
                    IntPtr gui = VanillaGuiAt(GuiPressMask);
                    bool ours = MenuWindowAt(mx, my);
                    if (KeyDiag)
                        _log?.Info($"[keydiag] mouse btn={btn} at ({mx},{my}) owner={UiHost.AnyKeyboardOwner} kbmode={KeyboardModeActive()} gui=0x{gui:x} menuOurs={ours}");
                    if (UiHost.DispatchMouseButton(true, btn, mx, my, captureOnly: true)) return 0;   // Window.CapturesPointer: ahead of the GUI yield
                    // NUI (or a vanilla GUI object: radial menu, tooltip, panel) draws over us -> it gets the
                    // press. Blur first: this is a click elsewhere, and FocusAt (skipped here) is what would
                    // otherwise have released our windows' focus.
                    if (NuiHovered() && btn == SdlButtonLeft) { CX.Services.Nui.HeldImageService.OnPress(); CX.Services.Nui.NuiModelView.OnPress(); }
                    if (NuiHovered() || (gui != IntPtr.Zero && !ours)) { UiHost.Blur(); break; }
                    if (!UiHost.HoverToFocus) UiHost.FocusAt(mx, my);   // click-to-focus (default)
                    if (UiHost.DispatchMouseButton(true, btn, mx, my)) return 0;
                    if (ChordGateClear()) UiHost.FireMouseChords(btn, RawMods());
                    break;
                }
                case SdlMouseUp:
                {
                    CX.Alloy.Db.EditSelect.MouseUp();
                    if (((SdlEvent*)ev)->Button == SdlButtonLeft) CX.Services.Nui.NuiModelView.OnRelease();
                    byte btn = ((SdlEvent*)ev)->Button;
                    int mx = ((SdlEvent*)ev)->X, my = ((SdlEvent*)ev)->Y;
                    if (UiHost.DispatchMouseButton(false, btn, mx, my)) return 0;
                    break;
                }
                case SdlMouseWheel:
                    Pointer(out float x, out float y);
                    if (UiHost.DispatchMouseWheel(((SdlEvent*)ev)->WheelY, captureOnly: true, x, y)) return 0;
                    if (NuiHovered() && CX.Services.Nui.NuiModelView.OnWheel(((SdlEvent*)ev)->WheelY)) return 0;
                    if (NuiHovered() || (VanillaGuiAt(GuiWheelMask) != IntPtr.Zero && !MenuWindowUnderPointer())) break;   // ...and the scroll
                    if (UiHost.DispatchMouseWheel(((SdlEvent*)ev)->WheelY)) return 0;
                    break;
            }
        }
        return _winMsg != null ? (*_winMsg)(ev) : 0;
    }

    // ---- focus edges ----------------------------------------------------------------------------
    // self is CClientExoApp* -- cache it so the player resolver can walk to the live in-game GUI.
    [UnmanagedCallersOnly] private static void OnGain(IntPtr self) { _focused = true;  ClientApi.LiveExoApp = self; if (_gainOrig != null) _gainOrig(self); }
    [UnmanagedCallersOnly] private static void OnLost(IntPtr self) { _focused = false; ClientApi.LiveExoApp = self; _keysDown.Clear(); if (_lostOrig != null) _lostOrig(self); }

    // ---- cursor fold (was CursorService) --------------------------------------------------------
    private static bool _forceDefault, _prevOverUi, _cursorFoldActive;

    [UnmanagedCallersOnly]
    private static void OnUpdateCursor(IntPtr @this, int param1)
    {
        if (!_cursorFoldActive)   // forcing half absent -> forward untouched, do no hit-test work
        {
            if (_origUpdateCursor != null) _origUpdateCursor(@this, param1);
            return;
        }
        int mx = 0, my = 0;
        if (EngineFn.SdlGetMouseState != null) EngineFn.SdlGetMouseState(&mx, &my);
        bool overUi = UiHost.HitTest(mx, my) != null;   // any window's UI under the pointer
        if (overUi != _prevOverUi)
        {
            _prevOverUi = overUi;
            _log?.Info($"[cursor] overUi {!overUi}->{overUi} at ({mx},{my})");
        }
        if (overUi && param1 == 0) param1 = 1;
        _forceDefault = overUi;
        if (_origUpdateCursor != null) _origUpdateCursor(@this, param1);
    }

    // ponytail: over any UI forces the walk cursor (id 3); distinct grip/link cursors
    // need the real engine cursor ids (Ghidra the SetMouseCursor callers) -- add when a report asks for them.
    [UnmanagedCallersOnly]
    private static void OnSetMouseCursor(IntPtr @this, int cursorId, int hotspot, int p3, int p4, int p5)
    {
        if (_origSetCursor == null) return;
        if (CX.Services.Nui.HeldImageService.HoldsCursor) _origSetCursor(@this, CX.Services.Nui.HeldImageService.HeldCursor, 0, 1, 0, p5);
        else if (_forceDefault) _origSetCursor(@this, UiCursorId, 0, 1, 0, p5);
        else _origSetCursor(@this, cursorId, hotspot, p3, p4, p5);
    }

    // ---- environmental probes -------------------------------------------------------------------
    /// <summary>True while the pointer is over a NUI window. Those draw ON TOP of our below-NUI windows, so they take the press
    /// and the wheel; without this a NUI window dragged over the chatbox would look on top and still lose every click
    /// to it. Only the acquisition edges consult it -- motion and button-up stay unconditional, so a drag started on
    /// our window still finishes across a NUI one. Nuklear's hover answer, or its window list where that call is not exported.</summary>
    private static bool NuiHovered()
    {
        if (_nuklearGlobal == IntPtr.Zero) return false;
        if (_nkAnyHovered == null)
        {
            if (EngineFn.SdlGetMouseState == null) return false;
            int mx = 0, my = 0;
            EngineFn.SdlGetMouseState(&mx, &my);
            return NuiCovers(mx, my);
        }
        IntPtr ctx = *(IntPtr*)_nuklearGlobal;
        return ctx != IntPtr.Zero && _nkAnyHovered(ctx) != 0;
    }

    /// <summary>Interactivity masks <c>CGuiMan::HandleLMouseDown</c> / <c>HandleMouseWheel</c> pass to the hit check.</summary>
    private const byte SdlButtonLeft = 1;  // not-an-offset: SDL_BUTTON_LEFT
    private const int GuiPressMask = 1, GuiWheelMask = 8;

    /// <summary>Would the ENGINE's GUI take a press right now -- a NUI window under the pointer or a vanilla GUI
    /// object at its tracked position? The engine gets first refusal on a right-drag.</summary>
    internal static bool EngineGuiWantsPress() => NuiHovered() || VanillaGuiAt(GuiPressMask) != IntPtr.Zero;

    /// <summary>The exemption from the engine-GUI yield. At the main menu the viewport-filling
    /// <c>CMainMenuBackgroundPanel</c> accepts every interactivity bit, so <see cref="VanillaGuiAt"/> is non-null
    /// under EVERY press and the yield would hand the gear's and the panel's presses to the engine. Over one of our
    /// windows the press is ours, elsewhere the engine's buttons keep winning; the Options panel needs it the same way.</summary>
    private static bool MenuWindowAt(int x, int y) => GearSurface && UiHost.HitTest(x, y) != null;

    /// <summary>The two surfaces the gear and the settings panel stand on: the main menu and the in-game Options panel.</summary>
    private static bool GearSurface => GameGate.AtMainMenu || GameGate.OptionsOpen;

    /// <summary>SDL's pointer, for the wheel event that has none.</summary>
    private static void Pointer(out float x, out float y)
    {
        int ix = 0, iy = 0;
        if (EngineFn.SdlGetMouseState != null) EngineFn.SdlGetMouseState(&ix, &iy);
        x = ix; y = iy;
    }

    /// <summary>The wheel event carries no position: the same exemption at SDL's pointer.</summary>
    private static bool MenuWindowUnderPointer()
    {
        if (!GearSurface || EngineFn.SdlGetMouseState == null) return false;
        int mx = 0, my = 0;
        EngineFn.SdlGetMouseState(&mx, &my);
        return UiHost.HitTest(mx, my) != null;
    }

    /// <summary>The vanilla GUI object under the engine's tracked pointer, or null. Exactly the call the engine's own
    /// press/wheel handler is about to make, so "would the engine find something" is answered by the engine.
    /// Acquisition edges only, like <see cref="NuiHovered"/>. ponytail: while our motion handler swallows motion the
    /// tracked position goes stale; if that bites live, pass fresh=1 with the SDL coords (Y flipped via GuiManVpH).</summary>
    private static IntPtr VanillaGuiAt(int mask)
    {
        if (_guiManGlobal == IntPtr.Zero || EngineFn.GuiObjectHitCheck == null) return IntPtr.Zero;
        var man = new CGuiMan(*(IntPtr*)_guiManGlobal);
        if (man.IsNull) return IntPtr.Zero;
        return man.GuiObjectHitCheck(man.MouseX, man.MouseY, 0, mask);
    }

    /// <summary>True while the app has window focus (the engine's focus-gain/lost edges).</summary>
    internal static bool AppFocused => _focused;

    /// <summary>True when a shown NUI window covers the point (SDL window pixels) -- it draws over our below-NUI
    /// windows there. The context is anchored on both OSes; the window-list offsets are read off the Linux build.</summary>
    internal static bool NuiCovers(float x, float y)
    {
        if (_nuklearGlobal == IntPtr.Zero) return false;
        IntPtr ctx = *(IntPtr*)_nuklearGlobal;
        if (ctx == IntPtr.Zero) return false;
        for (IntPtr w = ((UiNkContext*)ctx)->Begin; w != IntPtr.Zero; w = ((UiNkWindow*)w)->Next)
        {
            if ((((UiNkWindow*)w)->Flags & (NkWinHiddenClosed | NkWinMinimized)) != 0) continue;
            float* b = ((UiNkWindow*)w)->Bounds;
            if (x >= b[0] && x < b[0] + b[2] && y >= b[1] && y < b[1] + b[3]) return true;
        }
        return false;
    }

    private static bool NuiEditFocused()
    {
        if (_nuklearGlobal == IntPtr.Zero) return false;
        IntPtr ctx = *(IntPtr*)_nuklearGlobal;
        if (ctx == IntPtr.Zero) return false;
        IntPtr win = ((UiNkContext*)ctx)->ActiveWindow;
        return win != IntPtr.Zero && ((UiNkWindow*)win)->EditActive != 0;
    }

    private static bool KeyboardModeActive()
    {
        if (_exoInputGlobal == IntPtr.Zero || EngineFn.GetKeyboardMode == null) return false;
        IntPtr inst = *(IntPtr*)_exoInputGlobal;
        return inst != IntPtr.Zero && EngineFn.GetKeyboardMode(inst) != 0;
    }

    private static bool NativeDialogOpen()
    {
        if (_appMgr == IntPtr.Zero) return false;
        IntPtr mgr = *(IntPtr*)_appMgr;                if (mgr == IntPtr.Zero) return false;
        IntPtr app = *(IntPtr*)mgr;                    if (app == IntPtr.Zero) return false;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal; if (intern.IsNull) return false;
        CGuiInGame gui = intern.InGameGui;
        return !gui.IsNull && gui.Dialog != IntPtr.Zero;
    }
}
