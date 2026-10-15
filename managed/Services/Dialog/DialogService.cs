using System.Runtime.CompilerServices;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Dialog;

/// <summary>
/// Native-C# NPC conversation UI, the chatbox pattern applied to dialogs: SkipOriginal the GUI-layer entry
/// points so the vanilla <c>CGuiInGameChatDialog</c> panel is never created, capture the already-decoded
/// text/replies the message layer hands them, and draw an owned overlay in ChatService's SwapBuffers pass. The
/// message layer runs unmodified (voice-over, camera, speaker tracking stay); layout is cold, the draw hot.
/// </summary>
public sealed unsafe class DialogService : IExtenderService
{
    private static ExtenderLog? _log;

    private const string EndDialogLabel = "[End Dialog]";

    // ---- captured content (written cold in the hooks, read cold in Rebuild) ----
    private static volatile bool _open;                 // input-modal: keys/clicks captured while true
    private static uint _dialogId, _speakerId, _pageId;
    private static bool _continueFlag, _isEndDialog;
    private static string _npcText = "";
    private static string _speakerName = "";            // creature/object name for the title band ("" = none)
    private static string _portraitResref = "";
    private static Reply[] _replies = System.Array.Empty<Reply>();

    // Rebuild's parse cache: parsed runs reused across re-layouts until the content reference changes.
    private static string? _cachedNpcText;
    private static Reply[]? _cachedReplies;
    private static ColorRun[] _npcRuns = System.Array.Empty<ColorRun>();
    private static ColorRun[][] _replyRuns = System.Array.Empty<ColorRun[]>();

    private static int _hover = -1;                     // hovered reply display index (-1 = none)
    private static volatile float _scroll;              // px scrolled from content top; clamped by Draw

    // Text selection over the NPC body block (replies stay clickable), via the framework's opt-in selectable-text
    // capability. Dialog turns it on for its NPC body; Ctrl+C copies the selected runes, else the whole node.
    private static readonly SelectableText _sel = new();

    // ---- window rect: DialogKnobs.X/Y/W once Placed, else derived from the viewport each Rebuild ----
    /// <summary>Other windows' rects captured at move-drag start, for window-to-window snapping.</summary>
    private static (float X, float Y, float W, float H)[] _snapRects = System.Array.Empty<(float, float, float, float)>();
    private const float MinW = 320f;

    // ---- animation ----
    private static volatile bool _visible;              // logical open state (drives the window fade)
    private static volatile float _winAlphaTarget;      // 1 = open, 0 = closing
    private static volatile float _contentAlphaTarget = 1f;  // 1 = shown, 0 = fading out (a reply was picked)
    private static volatile int _openGen, _contentGen;  // bumped on open / on each new node
    // hot-thread only:
    private static float _windowAlpha, _contentAlpha = 1f, _bodyH;
    private static long _animClockMs; private static bool _animPrimed;
    private static int _lastOpenGen, _lastContentGen;
    private static volatile float _bodyHPub;            // last eased body height, for input-thread geometry

    // ---- grab / pointer (input thread) ----
    private enum Grab { None, Move, Resize, Scroll, SelectText }
    private static Grab _grab;
    private static float _gx, _gy, _gw, _gh, _gScroll;
    private static bool _pressConsumed;
    private static int _lastMx, _lastMy;

    private readonly record struct Reply(string Text, uint ServerIndex, bool Selectable);

    /// <summary>Hot-drawable snapshot. Content geometry is <em>content-relative</em> (y=0 at the content top);
    /// Draw offsets it by <c>bodyTop - scroll</c> and scissors it to the body. Chrome (bg/border/scrollbar/grip)
    /// is emitted per-frame in Draw at the eased height; only the fixed title glyphs (the name) are baked here.</summary>
    private sealed class DialogFrame
    {
        public float[] ContentVerts = System.Array.Empty<float>();
        public int ContentCount;
        public HitRect[] Hits = System.Array.Empty<HitRect>();   // reply rows, in content coords
        public string PortraitResref = "";
        public float Pcx, Pcy, Pw, Ph;                           // portrait rect: x screen, y content-relative
        public float[] TitleVerts = System.Array.Empty<float>(); // name glyphs, screen coords
        public int TitleCount;
        public float ContentH;
        public float PanelX, PanelY, PanelW, TitleH;
        public Block NpcBlock;                                   // the laid-out npc body, for text-selection hit-testing
        public float NpcX, NpcTop, NpcW;                         // npc column: screen x, content-relative top, wrap width
        public bool NpcJustify;                                  // justify state baked into this layout (affects glyph x)
    }
    private readonly record struct HitRect(float X, float Y, float W, float H, int Index);
    private static volatile DialogFrame? _frame;

    /// <summary>The MODAL gate: true while an NPC conversation window is on screen and interactive, including the
    /// await-node gap after a reply is picked, so digit-spam / clicks in that gap don't leak to the world
    /// (HotkeyInputService reads it to route keys here). Distinct from <c>_open</c>, the SELECTION gate, which
    /// drops the instant a reply is chosen. False during the fade-out, so a closing window lets clicks through.</summary>
    public static bool IsOpen => _visible;

    /// <summary>The window's art for the native look; one window, one skin.</summary>
    private static readonly Skin _skin = NativeChrome.NewSkin();

    private static CloseDialogFn _closeOrig;
    private static ShowDialogEntryFn _entryOrig;
    private static ShowDialogRepliesFn _repliesOrig;
    private static ShowReplyChosenFn _chosenOrig;
    private static ForceDialogVisibleFn _forceOrig;
    /// <summary>The current node is the stock panel's: decided at <see cref="OnShowEntry"/> from
    /// <c>dialog.enabled</c>, followed by the replies / chosen / force hooks until the next entry.</summary>
    private static volatile bool _engineOwns;
    private static Window? _window;   // the framework Window Dialog draws through

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        SelfCheck(log);
        SelectableText.SelfCheck(log);
        UiCategory.Declare();   // the UI groups declare in panel order whether or not the hooks arm

        if (!SymbolMap.Require(log, "DialogService", RequiredSymbols)) return;   // whole or nothing
        if (!EngineLayout.Require(log, "DialogService", "CNWCObject", "CGuiInGame")) return;   // the portrait slot + the stock panel slot

        // Second UiHost consumer: Dialog draws through the same z-ordered roll-call ChatService does,
        // as a rect-less window (Focusable=false) whose content-draw IS Draw and whose input delegates ARE its
        // existing modal handlers. It never owns the keyboard (no text entry) but withholds chords while visible
        // (SuppressChords, kept in sync with _visible) so reply digits / ESC / Ctrl+C never leak to gameplay or a
        // hotkey. z is assigned on register, and discovery is name-sorted (Chat < Dialog), so Dialog registers
        // second and paints over chat + is hit-tested before it.
        _window = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            DrawContent = Draw,
            OnMouseButton = OnMouseButton,
            OnMouseMotion = OnMouseMotion,
            OnMouseWheel = OnMouseWheel,
            OnKey = HandleKey,
            HitTestOverride = (px, py) => MouseOverUi((int)px, (int)py),
        };

        TextRig.Rebaked += Rebuild;   // any re-bake moved every UV: the open window re-lays out on the new sheet (cold, the baking thread)

        _entryOrig = hooks.InstallShowDialogEntry(&OnShowEntry);
        _repliesOrig = hooks.InstallShowDialogReplies(&OnShowReplies);
        _chosenOrig = hooks.InstallShowReplyChosen(&OnReplyChosen);
        _closeOrig = hooks.InstallCloseDialog(&OnCloseDialog);
        _forceOrig = hooks.InstallForceDialogVisible(&OnForceVisible);

        log.Info(EngineFn.SendDialogReply != null
            ? "DialogService: hooks installed (Entry/Replies capture, ReplyChosen skip, CloseDialog call-through)"
            : "DialogService: hooks attempted but reply sender unresolved (run-demo / non-Linux) -- render only");
    }

    // ---- hooks (cold: the message thread) -----------------------------------------------------

    /// <summary>The ownership boundary. Off: the node is the stock panel's -- CX's window fades if it was
    /// up and the engine call runs, making its panel at <c>+0x70</c> if it has none. On: CX's; a stock panel still
    /// up from an off interlude is torn down first through the engine's own <c>CloseDialog</c>.</summary>
    [UnmanagedCallersOnly]
    private static long OnShowEntry(IntPtr self, IntPtr text, uint dialogId, uint speakerId)
    {
        if (!DialogKnobs.Enabled.V)
        {
            _engineOwns = true;
            if (_visible) SoftClose();
            return _entryOrig != null ? _entryOrig(self, text, dialogId, speakerId) : 0;
        }
        if (_engineOwns)
        {
            _engineOwns = false;
            if (new CGuiInGame(self).Dialog != IntPtr.Zero && _closeOrig != null) _closeOrig(self);
        }
        try
        {
            _npcText = Native.ReadExo(text);
            _dialogId = dialogId;
            _speakerId = speakerId;
            _portraitResref = ResolvePortrait(speakerId);   // same thread ShowDialogEntry ran on -> safe
            _speakerName = Native.EngineText(ResolveName(speakerId));
            _replies = System.Array.Empty<Reply>();
            _isEndDialog = false;
            _hover = -1;
            _sel.Clear();                                    // new node -> drop any selection into stale text
            _scroll = 0f;                                    // new node -> top
            bool opening = !_visible;
            _open = true;
            _visible = true;
            if (_window != null) _window.SuppressChords = true;   // withhold chords immediately (no first-key leak)
            _winAlphaTarget = 1f;
            _contentAlphaTarget = 1f;
            if (opening) _openGen++;                         // closed -> open: Draw restarts the window fade + stretch
            _contentGen++;                                   // any new node: cross-fade the text in
            _log?.Info($"[dialog] entry dlg={dialogId} spk={speakerId} name=\"{Trunc(_speakerName)}\" portrait={_portraitResref} text=\"{Trunc(_npcText)}\"");
            Rebuild();
        }
        catch (System.Exception e) { _log?.Info($"[dialog] entry hook fault: {e.Message}"); }
        return 0;
    }

    [UnmanagedCallersOnly]
    private static long OnShowReplies(IntPtr self, int numReplies, IntPtr replies, IntPtr indices,
                                      int numOldReplies, uint pageId, int continueFlag)
    {
        if (_engineOwns)
            return _repliesOrig != null ? _repliesOrig(self, numReplies, replies, indices, numOldReplies, pageId, continueFlag) : 0;
        try
        {
            int n = numReplies < 0 ? 0 : numReplies;
            var texts = new string[n];
            var idx = new uint[n];
            uint* ip = (uint*)indices;
            for (int i = 0; i < n; i++)
            {
                texts[i] = Native.ReadExo((IntPtr)Unsafe.AsPointer(ref ((ExoStringRec*)replies)[i]));   // CExoString is 16 bytes
                idx[i] = ip != null ? ip[i] : 0xffffffff;
            }
            _replies = MakeReplies(texts, idx, numOldReplies);
            _isEndDialog = n == 0;
            _pageId = pageId;
            _continueFlag = continueFlag != 0;
            _hover = -1;
            _open = true;
            _contentAlphaTarget = 1f;   // defensive: a replies-only refresh mustn't leave content faded to 0
            // Replies belong to the current entry -- do NOT bump _contentGen (would restart the cross-fade).
            _log?.Info($"[dialog] replies n={numReplies} old={numOldReplies} page={pageId} cont={continueFlag}" +
                       (n > 0 ? $" first=\"{Trunc(texts[0])}\"" : " (End Dialog)"));
            Rebuild();
        }
        catch (System.Exception e) { _log?.Info($"[dialog] replies hook fault: {e.Message}"); }
        return 0;
    }

    /// <summary>The dialog unit. Optional and NOT listed: ForceDialogVisible (an empty body on both
    /// builds, no Windows anchor), GetObjectName + GetGameObject (title band falls back to no name/portrait).</summary>
    private static readonly string[] RequiredSymbols =
    {
        SymbolMap.ShowDialogEntry, SymbolMap.ShowDialogReplies, SymbolMap.ShowReplyChosen, SymbolMap.CloseDialog,
        SymbolMap.SendDialogReply, SymbolMap.AppManager, SymbolMap.ExoStringDtor,
    };

    [UnmanagedCallersOnly]
    private static long OnReplyChosen(IntPtr self, uint a, uint b, IntPtr exo)
    {
        if (_engineOwns && _chosenOrig != null) return _chosenOrig(self, a, b, exo);   // the callee destroys the copy
        if (OperatingSystem.IsWindows()) Native.DestroyExoString(exo);   // Win64: the skipped callee owned the copy
        return 0;
    }

    [UnmanagedCallersOnly]
    private static long OnForceVisible(IntPtr self, float duration)
        => _engineOwns && _forceOrig != null ? _forceOrig(self, duration) : 0;

    /// <summary>Call-through on both paths: the engine deletes its panel (a no-op on null) and resets its modal
    /// state (<c>ResetGuiState(0x298)</c>); CX's window fades if it was up.</summary>
    [UnmanagedCallersOnly]
    private static void OnCloseDialog(IntPtr self)
    {
        SoftClose();
        if (_closeOrig != null) _closeOrig(self);
    }

    private static Reply[] MakeReplies(string[] texts, uint[] indices, int numOld)
    {
        int n = texts.Length;
        if (n == 0) return new[] { new Reply(EndDialogLabel, 0xffffffff, true) };
        int fresh = n - (numOld < 0 ? 0 : numOld);
        if (fresh < 0) fresh = 0;
        var reps = new Reply[n];
        for (int i = 0; i < n; i++)
            reps[i] = new Reply(texts[i], i < indices.Length ? indices[i] : 0xffffffff, i < fresh);
        return reps;
    }

    // ---- input (cold: the SDL input thread, dispatched from HotkeyInputService) ----------------

    /// <summary>Pointer moved. Consumed (true) only while the settings panel is modal or a window/slider/scroll
    /// drag is in progress -- otherwise non-consuming so camera edge-scroll still works under an open dialog.</summary>
    public static bool OnMouseMotion(int x, int y)
    {
        _lastMx = x; _lastMy = y;
        if (!_open) return false;
        switch (_grab)
        {
            case Grab.Move:
            {
                float wx = x - _gx, wy = y - _gy;
                DialogFrame? mf = _frame;
                float ph = mf != null ? mf.TitleH + _bodyHPub : DialogKnobs.MaxHeight.V;
                float pw = mf?.PanelW ?? DialogKnobs.W.V;
                Chat.Core.ChatWindow.SnapRect(ref wx, ref wy, pw, ph);
                if (Chat.Core.ChatWindow.EdgeThresh > 0)
                    Chat.Core.ChatWindow.SnapToRects(ref wx, ref wy, pw, ph, _snapRects);
                DialogKnobs.X.V = wx; DialogKnobs.Y.V = wy;   // plain writes while dragging; the release is the set
                Rebuild(); return true;
            }
            case Grab.Resize:
            {
                float wx = DialogKnobs.X.V, wy = DialogKnobs.Y.V;
                float ww = System.Math.Max(MinW, _gw + (x - _gx));
                float mh = System.Math.Max(DialogKnobs.MaxHMin, _gh + (y - _gy));
                float sp = Chat.Core.ChatWindow.SnapPx;
                if (sp > 0)   // grid-snap the bottom-right corner to the same lattice the move uses
                {
                    float cx = Chat.Core.ChatWindow.SnapCX, cy = Chat.Core.ChatWindow.SnapCY;
                    float rx = cx + (float)System.Math.Round((wx + ww - cx) / sp) * sp;
                    float ry = cy + (float)System.Math.Round((wy + mh - cy) / sp) * sp;
                    ww = System.Math.Max(MinW, rx - wx);
                    mh = System.Math.Max(DialogKnobs.MaxHMin, ry - wy);
                }
                DialogKnobs.W.V = ww; DialogKnobs.MaxHeight.V = System.Math.Min((int)MathF.Round(mh), DialogKnobs.MaxHMax);
                Rebuild(); return true;
            }
            case Grab.SelectText:
            {
                DialogFrame? f = _frame;
                if (f != null) { _sel.Cursor = NpcHitIndex(f, x, y); Rebuild(); }
                return true;
            }
            case Grab.Scroll:
            {
                DialogFrame? f = _frame;
                if (f != null && ScrollGeom(f, out float trackTop, out float thumbH, out float maxScroll))
                {
                    float travel = ScrollTravel(thumbH);
                    if (travel > 0f)
                    {
                        float sp = _gScroll + (y - _gy) * maxScroll / travel;
                        _scroll = sp < 0f ? 0f : (sp > maxScroll ? maxScroll : sp);
                    }
                }
                return true;
            }
        }
        int hit = HitTestContent(_frame, x, y);
        if (hit != _hover) { _hover = hit; Rebuild(); }
        return false;
    }

    /// <summary>Pointer button. While a dialog is open, the left button is consumed (modal): it grabs the
    /// scrollbar/grip/title (unless locked), or picks the reply under it. Owns the button pair via
    /// <see cref="_pressConsumed"/> so a matching up is never dropped; a finished move/resize is the rect's set.</summary>
    public static bool OnMouseButton(bool down, int button, int x, int y)
    {
        if (!_open || button != 1)
        {
            // Await-node gap (window still visible + opaque after a pick): swallow left clicks that land ON the
            // panel so a double-click on a reply can't fall through. Clicks OUTSIDE the panel pass to native/other UI.
            if (_visible && button == 1 && MouseOverUi(x, y)) { _pressConsumed = down; return true; }
            if (!down && _pressConsumed) { _pressConsumed = false; return true; }
            return false;
        }

        if (down)
        {
            DialogFrame? f = _frame;
            if (f != null)
            {
                float titleH = f.TitleH;
                bool locked = DialogKnobs.Lock.V;
                if (OverThumb(f, x, y)) { _pressConsumed = true; _grab = Grab.Scroll; _gy = y; _gScroll = _scroll; return true; }
                if (ArrowStep(f, x, y) is float step and not 0f)
                {
                    float max = System.Math.Max(0f, f.ContentH - _bodyHPub);
                    _scroll = System.Math.Clamp(_scroll + step, 0f, max); _pressConsumed = true; return true;
                }
                if (!locked && OverGrip(f, x, y))
                { _pressConsumed = true; SeedRect(f); _grab = Grab.Resize; _gx = x; _gy = y; _gw = DialogKnobs.W.V; _gh = DialogKnobs.MaxHeight.V; return true; }
                if (!locked && In(f.PanelX, f.PanelY, f.PanelW, titleH, x, y))
                {
                    _pressConsumed = true; SeedRect(f); _grab = Grab.Move; _gx = x - DialogKnobs.X.V; _gy = y - DialogKnobs.Y.V;
                    _snapRects = Chat.Core.ChatWindow.EdgeThresh > 0
                        ? ChatService.GatherSnapRects(null, false)   // includeDialog:false -> excludes ourselves
                        : System.Array.Empty<(float, float, float, float)>();
                    return true;
                }
                int hit = HitTestContent(f, x, y);
                if (hit >= 0) { _pressConsumed = true; Select(hit); return true; }
                if (OverNpcText(f, x, y))
                {
                    _pressConsumed = true; _grab = Grab.SelectText;
                    _sel.Begin(NpcHitIndex(f, x, y));   // a bare click (no drag) collapses -> clears on up
                    Rebuild(); return true;
                }
            }
            // Not over any target: consume only if the point is on our panel; else let it fall through so
            // native screens and our other windows stay clickable. ponytail: world stays clickable during a dialog
            // (native conversation engine already ignores world clicks), fine unless a report says otherwise.
            if (MouseOverUi(x, y)) { _pressConsumed = true; return true; }
            return false;
        }

        bool wasWinDrag = _grab == Grab.Move || _grab == Grab.Resize;
        bool wasSelect = _grab == Grab.SelectText;
        _grab = Grab.None;
        bool consumed = _pressConsumed; _pressConsumed = false;
        if (wasWinDrag) CommitRect();
        if (wasSelect && _sel.Anchor == _sel.Cursor) { _sel.Clear(); Rebuild(); }   // click, not drag
        return consumed;
    }

    /// <summary>Wheel notch over the dialog body: scroll the content. Consumed only when the pointer is over the
    /// panel and there is something to scroll.</summary>
    public static bool OnMouseWheel(int dy)
    {
        if (!_open) return false;
        DialogFrame? f = _frame;
        if (f == null) return false;
        float bodyTop = f.PanelY + f.TitleH;
        if (!In(f.PanelX, bodyTop, f.PanelW, _bodyHPub, _lastMx, _lastMy)) return false;
        float maxScroll = f.ContentH - _bodyHPub; if (maxScroll < 0f) maxScroll = 0f;
        GlyphAtlas? atlas = Face();
        float step = (atlas?.LineHeight ?? 18f) * 3f;
        float sp = _scroll - dy * step;
        _scroll = sp < 0f ? 0f : (sp > maxScroll ? maxScroll : sp);
        return true;
    }

    /// <summary>SDL_Keymod LCTRL | RCTRL (matches HotkeyInputService), for the Ctrl+C copy chord.</summary>
    private const int KmodCtrl = 0x00C0;  // not-an-offset: SDL key/event code

    /// <summary>Keyboard while a dialog is open: 1-9 pick that reply row, 0 picks row 10 (vanilla mapping),
    /// ESC closes locally (vanilla parity: no wire send), Ctrl+C copies the selected/whole npc text. Returns
    /// true when consumed. Called before the gameplay-hotkey path.</summary>
    public static bool HandleKey(uint sdlKey, int mods)
    {
        if (!_visible) return false;
        if (sdlKey == 0x1B) { SoftClose(); return true; }         // ESC always dismisses (even a wedged await-node gap)
        if ((mods & KmodCtrl) != 0 && sdlKey == 0x63) { Copy(); return true; }   // Ctrl+C: selection, else whole node text
        if (!_open) return true;                                  // await-node gap: swallow digits (no selection yet)
        if (sdlKey == 0x30) { Select(9); return true; }
        if (sdlKey >= 0x31 && sdlKey <= 0x39) { Select((int)sdlKey - 0x31); return true; }
        return false;
    }

    /// <summary>True when the pointer is over the dialog window. Wired as the window's hit-test override
    /// so the framework cursor fold shows the UI pointer and click routing reaches us.</summary>
    public static bool MouseOverUi(int x, int y)
    {
        if (!_visible) return false;   // modal gate incl. the await-node gap; false during fade-out
        DialogFrame? f = _frame;
        if (f == null) return false;
        return In(f.PanelX, f.PanelY, f.PanelW, f.TitleH + _bodyHPub, x, y);
    }

    /// <summary>The dialog panel rect (title + visible body) for window snapping. False unless a dialog is up.</summary>
    public static bool SnapRectOut(out (float X, float Y, float W, float H) r)
    {
        DialogFrame? f = _frame;
        if (!_visible || f == null) { r = default; return false; }
        r = (f.PanelX, f.PanelY, f.PanelW, f.TitleH + _bodyHPub);
        return true;
    }

    /// <summary>A <c>dialog.justify</c> set while a node is up: re-lay the open window (cold, the setter's thread).</summary>
    internal static void Relayout() => Rebuild();

    /// <summary>A finished move/resize: the dragged rect becomes a set of each rect knob (clamped, the writer
    /// armed once) and the window counts as placed from now on.</summary>
    private static void CommitRect()
    {
        DialogKnobs.X.Set(DialogKnobs.X.V); DialogKnobs.Y.Set(DialogKnobs.Y.V); DialogKnobs.W.Set(DialogKnobs.W.V);
        DialogKnobs.MaxHeight.Set(DialogKnobs.MaxHeight.V);
        DialogKnobs.Placed.Set(true);
    }

    private static bool In(float x, float y, float w, float h, float px, float py)
        => px >= x && px < x + w && py >= y && py < y + h;

    /// <summary>On the first move/resize (window not yet placed), seed the user rect from the drawn panel so the
    /// drag starts exactly where the auto-laid panel is.</summary>
    private static void SeedRect(DialogFrame f)
    {
        if (DialogKnobs.Placed.V) return;
        DialogKnobs.X.V = f.PanelX; DialogKnobs.Y.V = f.PanelY; DialogKnobs.W.V = f.PanelW;
        DialogKnobs.Placed.V = true;
    }

    /// <summary>Scrollbar geometry (screen px), or false when nothing scrolls. Shared by draw + hit-test.</summary>
    private static bool ScrollGeom(DialogFrame f, out float thumbTop, out float thumbH, out float maxScroll)
    {
        thumbTop = 0f; thumbH = 0f; maxScroll = 0f;
        float bodyH = _bodyHPub, contentH = f.ContentH;
        if (contentH <= DialogKnobs.MaxHeight.V + 0.5f || bodyH <= 0f) return false;   // only scroll past the breakpoint
        maxScroll = contentH - bodyH; if (maxScroll < 0f) maxScroll = 0f;
        float bodyTop = f.PanelY + f.TitleH;
        if (Theme.IsNative)
        {
            float sx = f.PanelX + f.PanelW - ScrollbarW;
            if (!NativeChrome.ThumbRect(sx, bodyTop, f.PanelX + f.PanelW, bodyTop + bodyH, _scroll, bodyH, contentH,
                                        out _, out float ny0, out _, out float ny1)) return false;
            thumbTop = ny0; thumbH = ny1 - ny0;
            return true;
        }
        thumbH = System.Math.Max(SbMinThumb, bodyH * bodyH / contentH);
        float frac = maxScroll > 0f ? _scroll / maxScroll : 0f;
        thumbTop = bodyTop + frac * (bodyH - thumbH);
        return true;
    }

    /// <summary>Thumb travel for a <paramref name="thumbH"/>-tall thumb: the body less both arrows and the thumb's
    /// own padding in the game-art look, else the plain body.</summary>
    private static float ScrollTravel(float thumbH)
    {
        float t = Theme.IsNative ? _bodyHPub - 2f * ScrollbarW - 2f * NativeChrome.PadY - thumbH : _bodyHPub - thumbH;
        return t > 0f ? t : 0f;
    }

    /// <summary>The scroll a press on the game-art bar's arrows asks for (a tenth of the body, as nuklear); 0 elsewhere.</summary>
    private static float ArrowStep(DialogFrame f, float x, float y)
    {
        if (!Theme.IsNative || !ScrollGeom(f, out _, out _, out _)) return 0f;
        float x1 = f.PanelX + f.PanelW, y0 = f.PanelY + f.TitleH;
        if (x < x1 - ScrollbarW || x >= x1 || y < y0 || y >= y0 + _bodyHPub) return 0f;
        return NativeChrome.Arrow(y0, y0 + _bodyHPub, ScrollbarW, y) * _bodyHPub * NativeChrome.ArrowStep;
    }

    private static bool OverThumb(DialogFrame f, float x, float y)
    {
        if (!ScrollGeom(f, out float top, out float h, out _)) return false;
        float sx = f.PanelX + f.PanelW - ScrollbarW;
        return x >= sx && x <= f.PanelX + f.PanelW && y >= top && y <= top + h;
    }

    private static bool OverGrip(DialogFrame f, float x, float y)
    {
        float bx = f.PanelX + f.PanelW - ResizeGripPx, by = f.PanelY + f.TitleH + _bodyHPub - ResizeGripPx;
        return x >= bx && x <= f.PanelX + f.PanelW && y >= by && y <= f.PanelY + f.TitleH + _bodyHPub;
    }

    /// <summary>Reply row under the pointer, or -1. Transforms the screen pointer into content coords (the hit
    /// rects are content-relative) and rejects hits outside the visible body.</summary>
    private static int HitTestContent(DialogFrame? f, float x, float y)
    {
        if (f == null) return -1;
        float bodyTop = f.PanelY + f.TitleH;
        if (y < bodyTop || y > bodyTop + _bodyHPub) return -1;   // outside the body (scrolled off / chrome)
        if (x < f.PanelX || x > f.PanelX + f.PanelW) return -1;
        float cy = y - bodyTop + _scroll;
        HitRect[] hits = f.Hits;
        for (int i = 0; i < hits.Length; i++)
        {
            HitRect h = hits[i];
            if (x >= h.X && x < h.X + h.W && cy >= h.Y && cy < h.Y + h.H) return h.Index;
        }
        return -1;
    }

    /// <summary>True when the screen point falls on the npc body text (the selectable region): inside the visible
    /// body vertically, within the npc column horizontally, and over the block's laid-out lines. Reply rows and
    /// chrome are excluded so a left-press there still picks/drags as before.</summary>
    private static bool OverNpcText(DialogFrame? f, float x, float y)
    {
        if (f == null || f.NpcBlock.Runes == null || f.NpcBlock.Runes.Count == 0) return false;
        GlyphAtlas? atlas = Face(); if (atlas == null) return false;
        float bodyTop = f.PanelY + f.TitleH;
        if (y < bodyTop || y > bodyTop + _bodyHPub) return false;
        if (x < f.NpcX || x > f.NpcX + f.NpcW) return false;
        float cy = y - bodyTop + _scroll;
        return cy >= f.NpcTop && cy < f.NpcTop + f.NpcBlock.Lines.Count * atlas.LineHeight;
    }

    /// <summary>Screen point -> rune index into the npc block, clamped to [0, Runes.Count]. Above the block selects
    /// to the start, below it to the end (so dragging past an edge extends to the boundary). Within a line the
    /// index is chosen by the nearest glyph gap, measured with the same <see cref="RowEdges"/> walk the glyphs use.</summary>
    private static int NpcHitIndex(DialogFrame f, float x, float y)
    {
        GlyphAtlas? atlas = Face();
        Block blk = f.NpcBlock;
        if (atlas == null || blk.Runes == null || blk.Lines.Count == 0) return 0;
        float lh = atlas.LineHeight;
        float bodyTop = f.PanelY + f.TitleH;
        float cy = y - bodyTop + _scroll - f.NpcTop;
        if (cy < 0f) return 0;
        int li = (int)(cy / lh);
        if (li >= blk.Lines.Count) return blk.Runes.Count;
        TextWrapper.LineSpan span = blk.Lines[li];
        TextAlign align = f.NpcJustify ? TextAlign.Justify : TextAlign.Left;
        RowAlign.Compute(align, atlas, blk.Runes, span, f.NpcW, li == blk.Lines.Count - 1, out float xOff, out float extraPerGap);
        var edges = new List<float>();
        SelectableText.RowEdges(atlas, blk.Runes, span.Start, span.End, f.NpcX + xOff, extraPerGap, edges);
        return SelectableText.RowHitIndex(edges, span.Start, span.End - span.Start, x);
    }

    private static void Select(int displayIndex)
    {
        Reply[] replies = _replies;
        if ((uint)displayIndex >= (uint)replies.Length) return;
        Reply r = replies[displayIndex];
        if (!r.Selectable) return;
        if (_isEndDialog) { SoftClose(); return; }        // End Dialog: ends the conversation -> window fades out
        SendReply(_dialogId, r.ServerIndex, 0, _pageId);
        // Mid-conversation pick: the window stays; only the current content fades out (input released) while we
        // wait for the next node, which cross-fades its content in. The window fade is reserved for open/close.
        _open = false;
        _hover = -1;
        _grab = Grab.None;
        _contentAlphaTarget = 0f;
    }

    /// <summary>Ctrl+C: copy the selected npc text to the clipboard; with no selection, the whole node's text.
    /// Source is the color-stripped npc runes, so the clipboard gets clean prose (no <c>&lt;cXXX&gt;</c> codes).</summary>
    private static void Copy()
    {
        DialogFrame? f = _frame;
        List<System.Text.Rune>? runes = f?.NpcBlock.Runes;
        string text;
        if (runes != null)
        {
            int a = 0, c = runes.Count;
            if (_sel.Range(runes.Count, out int sa, out int sb)) { a = sa; c = sb; }
            var acc = new StringBuilder(c - a);
            for (int i = a; i < c; i++) acc.Append(runes[i].ToString());
            text = acc.ToString();
        }
        else text = _npcText;
        SelectableText.Copy(text);   // framework clipboard capability; no-op on empty / unresolved symbol
    }

    /// <summary>Send a reply on the live <c>CNWCMessage</c> as <c>this</c> (major 0x14 / minor 0x03); refuses
    /// when the sender symbol or the message object is missing.</summary>
    private static void SendReply(uint dialogId, uint replyIndex, byte isAbort, uint pageId)
    {
        if (EngineFn.SendDialogReply == null) { _log?.Info("[dialog] reply sender unresolved; not sending"); return; }
        IntPtr msg = ClientApi.Message();
        if (msg == IntPtr.Zero) { _log?.Info("[dialog] no live CNWCMessage; cannot send reply"); return; }
        EngineFn.SendDialogReply(msg, dialogId, replyIndex, isAbort, pageId);
        _log?.Info($"[dialog] sent reply idx={replyIndex} abort={isAbort} page={pageId}");
    }

    /// <summary>Soft close: release input (clicks fall through) and fade the whole window out. Draw retires the
    /// frame once the window alpha reaches 0. Used by every close path -- a chosen reply, ESC / End Dialog, and
    /// the server's CloseDialog.</summary>
    private static void SoftClose()
    {
        _open = false;
        _visible = false;
        _isEndDialog = false;
        _winAlphaTarget = 0f;
        _hover = -1;
        _sel.Clear();
        _grab = Grab.None;
    }

    /// <summary>Hard close: drop all state and hide immediately (no fade). Called at a session boundary
    /// (mid-conversation disconnect) by ChatService, where a fade would linger past teardown.</summary>
    public static void Close()
    {
        _open = false;
        _visible = false;
        _isEndDialog = false;
        _replies = System.Array.Empty<Reply>();
        _npcText = ""; _speakerName = ""; _portraitResref = "";
        _hover = -1; _scroll = 0f; _sel.Clear();
        _grab = Grab.None; _pressConsumed = false;
        _frame = null;
        _windowAlpha = 0f; _winAlphaTarget = 0f; _contentAlpha = 1f; _contentAlphaTarget = 1f;
        _bodyH = 0f; _animPrimed = false;
    }

    // ---- animation (hot thread) ---------------------------------------------------------------

    /// <summary>Advance the three animated scalars toward their targets and publish the body height. Window alpha
    /// fades on open/close; content alpha cross-fades per node; body height eases toward <paramref name="bodyTarget"/>.
    /// Anim disabled -> everything snaps. Hot-thread only; volatile inputs are set by cold threads.</summary>
    private static void AdvanceAnim(long nowMs, float bodyTarget)
    {
        int og = _openGen; if (og != _lastOpenGen) { _lastOpenGen = og; _windowAlpha = 0f; _bodyH = 0f; _animPrimed = false; }
        int cg = _contentGen; if (cg != _lastContentGen) { _lastContentGen = cg; _contentAlpha = 0f; }

        float winTarget = _winAlphaTarget;
        float contentTarget = _contentAlphaTarget;
        if (!DialogKnobs.Anim.V)
        {
            _windowAlpha = winTarget; _contentAlpha = contentTarget; _bodyH = bodyTarget; _animPrimed = false;
            _bodyHPub = _bodyH; return;
        }
        if (!_animPrimed) { _animPrimed = true; _animClockMs = nowMs; _bodyHPub = _bodyH; return; }

        long dtMs = nowMs - _animClockMs; _animClockMs = nowMs;
        if (dtMs > 0)
        {
            float fadeIn = DialogKnobs.FadeIn.V, fadeOut = DialogKnobs.FadeOut.V;
            float sIn = fadeIn > 1f ? dtMs / fadeIn : 1f;
            float sOut = fadeOut > 1f ? dtMs / fadeOut : 1f;
            MoveToward(ref _windowAlpha, winTarget, winTarget >= _windowAlpha ? sIn : sOut);
            MoveToward(ref _contentAlpha, contentTarget, contentTarget >= _contentAlpha ? sIn : sOut);
            // Body height eases exponentially (smoother for size changes) with a tau tied to the fade-in knob.
            float tau = System.Math.Max(20f, fadeIn * 0.4f);
            float k = 1f - (float)System.Math.Exp(-dtMs / tau);
            _bodyH += (bodyTarget - _bodyH) * k;
            if (System.Math.Abs(bodyTarget - _bodyH) < 0.5f) _bodyH = bodyTarget;
        }
        _bodyHPub = _bodyH;
    }

    private static void MoveToward(ref float v, float target, float step)
    {
        if (v < target) { v += step; if (v > target) v = target; }
        else if (v > target) { v -= step; if (v < target) v = target; }
    }

    // ---- render (Rebuild cold; Draw hot) ------------------------------------------------------

    /// <summary>Hot: animate + draw the overlay. Chrome (bg/border/scrollbar/grip) is emitted per-frame at the
    /// eased height into a reused buffer (zero-alloc); content is drawn clipped + scrolled + faded; the title
    /// glyphs draw un-clipped on top. This is the framework Window's content-draw slot, driven by UiHostService
    /// once per frame; it paints only in-game, so a session boundary or the ESC menu suppresses the panel.</summary>
    private static void Draw()
    {
        // Keep the chord-withhold flag in lockstep with the modal gate each frame (the close paths clear _visible
        // off the render thread); the synchronous set on open covers the first-key race before this runs.
        if (_window != null) _window.SuppressChords = _visible;
        if (!GameGate.InGame) return;
        DialogFrame? f = _frame;
        if (f == null) return;
        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null) return;

        float bodyTarget = System.Math.Min(f.ContentH, DialogKnobs.MaxHeight.V);
        AdvanceAnim(Environment.TickCount64, bodyTarget);

        if (_windowAlpha <= 0.001f && _winAlphaTarget <= 0f && !_open)
        {
            // Window fully faded out: retire. (!_open guards a concurrent OnShowEntry republishing the frame.)
            _frame = null;
            _replies = System.Array.Empty<Reply>(); _npcText = ""; _speakerName = ""; _portraitResref = "";
            _isEndDialog = false; _visible = false;
            return;
        }
        if (_windowAlpha <= 0.001f) return;

        float chromeA = _windowAlpha;
        float contentA = _windowAlpha * _contentAlpha;

        float panelX = f.PanelX, panelY = f.PanelY, panelW = f.PanelW, titleH = f.TitleH;
        float bodyTop = panelY + titleH;
        float bodyH = _bodyH;
        float contentH = f.ContentH;
        float maxScroll = contentH - bodyH; if (maxScroll < 0f) maxScroll = 0f;
        float scroll = _scroll; if (scroll < 0f) scroll = 0f; else if (scroll > maxScroll) scroll = maxScroll;
        _scroll = scroll;   // clamp back so input reads a consistent value
        bool scrollable = contentH > DialogKnobs.MaxHeight.V + 0.5f;

        // 1. chrome (per-frame, reused buffer) -> full window at the eased height.
        int n = 0;
        float su = atlas.SolidU, sv = atlas.SolidV;
        float panelH = titleH + bodyH;
        Skin? skin = Theme.IsNative && _skin.Arm(_log, "") ? _skin : null;
        skin?.Begin();
        uint tint = NativeChrome.White(chromeA);
        if (skin != null)
        {
            skin.Draw(NativeChrome.Frame, panelX, panelY, panelX + panelW, panelY + panelH, tint);
            skin.Draw(NativeChrome.Header, panelX, panelY, panelX + panelW, panelY + titleH, tint);
        }
        else
        {
            EmitRect(ref n, su, sv, panelX, panelY, panelX + panelW, panelY + panelH, BgCol);
            // title band + separator
            EmitRect(ref n, su, sv, panelX, panelY, panelX + panelW, panelY + titleH, HeaderCol);
            EmitRect(ref n, su, sv, panelX, panelY + titleH - 0.5f, panelX + panelW, panelY + titleH + 0.5f, SepCol);
            // borders
            EmitBorder(ref n, su, sv, panelX, panelY, panelW, panelH);
        }
        // scrollbar
        if (scrollable && ScrollGeom(f, out float thumbTop, out float thumbH, out _))
        {
            float sx = panelX + panelW - ScrollbarW;
            if (skin != null) NativeChrome.Scrollbar(skin, sx, bodyTop, panelX + panelW, bodyTop + bodyH, scroll, bodyH, contentH, _grab == Grab.Scroll);
            else
            {
                EmitRect(ref n, su, sv, sx, bodyTop, panelX + panelW, bodyTop + bodyH, TrackBgCol);
                EmitRect(ref n, su, sv, sx, thumbTop, panelX + panelW, thumbTop + thumbH, KnobCol);
            }
        }
        // resize grip
        if (!DialogKnobs.Lock.V)
            for (int i = 1; i <= 2; i++)
            {
                float o = i * (ResizeGripPx / 3f);
                EmitRect(ref n, su, sv, panelX + panelW - o - 1f, panelY + panelH - ResizeGripPx + o,
                         panelX + panelW - o, panelY + panelH - o, GripCol);
            }
        skin?.Flush();
        TextRig.DrawBatch(_chrome, n / VertexBatch.Floats, chromeA);

        // 2. content: clipped to the body, offset by (bodyTop - scroll), faded.
        int bx = (int)panelX, by = (int)bodyTop, bw = (int)System.Math.Ceiling(panelW), bh = (int)System.Math.Ceiling(bodyH);
        float offY = bodyTop - scroll;
        if (f.ContentCount > 0)
            TextRig.DrawBatch(f.ContentVerts, f.ContentCount, contentA, 0f, offY, true, bx, by, bw, bh);
        if (f.PortraitResref.Length > 0 && f.Pw > 0f)
            TextRig.DrawPortrait(f.PortraitResref, f.Pcx, f.Pcy + offY, f.Pw, f.Ph, contentA, true, bx, by, bw, bh);

        // 3. title glyphs (the name), fixed, chrome alpha.
        if (f.TitleCount > 0) TextRig.DrawBatch(f.TitleVerts, f.TitleCount, chromeA);
    }

    // ---- chrome emit (reused buffer, zero-alloc after the first grow) --------------------------

    private static float[] _chrome = new float[1024];
    private static void EmitRect(ref int n, float su, float sv, float x0, float y0, float x1, float y1,
                                 (float r, float g, float b, float a) c)
    {
        if (n + 6 * VertexBatch.Floats > _chrome.Length)
            System.Array.Resize(ref _chrome, _chrome.Length * 2);
        float[] v = _chrome;
        // Two triangles (TL, TR, BR / TL, BR, BL) matching VertexBatch.Rect's winding.
        Vtx(v, ref n, x0, y0, su, sv, c); Vtx(v, ref n, x1, y0, su, sv, c); Vtx(v, ref n, x1, y1, su, sv, c);
        Vtx(v, ref n, x0, y0, su, sv, c); Vtx(v, ref n, x1, y1, su, sv, c); Vtx(v, ref n, x0, y1, su, sv, c);
    }
    private static void Vtx(float[] v, ref int n, float x, float y, float su, float sv,
                            (float r, float g, float b, float a) c)
    { v[n++] = x; v[n++] = y; v[n++] = su; v[n++] = sv; v[n++] = c.r; v[n++] = c.g; v[n++] = c.b; v[n++] = c.a; }
    private static void EmitBorder(ref int n, float su, float sv, float x, float y, float w, float h)
    {
        EmitRect(ref n, su, sv, x, y, x + w, y + BorderW, BorderCol);
        EmitRect(ref n, su, sv, x, y + h - BorderW, x + w, y + h, BorderCol);
        EmitRect(ref n, su, sv, x, y, x + BorderW, y + h, BorderCol);
        EmitRect(ref n, su, sv, x + w - BorderW, y, x + w, y + h, BorderCol);
    }

    // Layout constants (design px; the atlas is baked at the game's UI scale, so glyph metrics already scale).
    private const float PanelPad = 14f, ReplyGap = 6f, SepGap = 10f, BottomMargin = 90f, BorderW = 1f;
    private const float ResizeGripPx = 16f, SbMinThumb = 24f, TitlePadY = 3f;
    private const float FlatBarPx = 9f;
    private const float PortraitRows = 4.2f;

    /// <summary>The scrollbar gutter: NUI's bar in the game-art look, else the flat one. Layout, draw and
    /// hit-test all read it, so the three never disagree.</summary>
    private static float ScrollbarW => Theme.IsNative ? NativeChrome.BarWidth(Metrics.Scale) : FlatBarPx;

    // The window's colours by role, re-read per frame so a look or palette change shows on the next one.
    private static (float r, float g, float b, float a) BgCol => PanelChrome.C(Theme.Rgb(Role.PanelBg), 0.93f);
    private static (float r, float g, float b, float a) HeaderCol => PanelChrome.C(Theme.Rgb(Role.HeaderBg), 0.96f);
    private static (float r, float g, float b, float a) BorderCol => PanelChrome.C(Theme.Rgb(Role.Border), 0.95f);
    private static (float r, float g, float b, float a) SepCol => PanelChrome.C(Theme.Rgb(Role.Frame), 0.8f);
    private static (float r, float g, float b, float a) HoverCol => PanelChrome.C(Theme.Rgb(Role.Accent), 0.55f);
    /// <summary>The text-selection highlight.</summary>
    private static (float r, float g, float b, float a) SelCol => PanelChrome.C(Theme.Rgb(Role.Selected), 0.7f);
    private static (float r, float g, float b, float a) TrackBgCol => PanelChrome.C(Theme.Rgb(Role.Track), 0.7f);
    private static (float r, float g, float b, float a) KnobCol => PanelChrome.C(Theme.Rgb(Role.Accent2), 0.95f);
    private static (float r, float g, float b, float a) GripCol => PanelChrome.C(Theme.Rgb(Role.Grip), 0.8f);
    private static uint NpcRgb => Theme.Rgb(Role.Hi);
    private static uint ReplyRgb => Theme.Rgb(Role.Tx);
    private static uint ReplyHoverRgb => Theme.Rgb(Role.Hi);
    private static uint DimRgb => Theme.Rgb(Role.Off);
    private static uint NameRgb => Theme.Rgb(Role.Hi);
    /// <summary>The reply numbering, vanilla's own gold whatever the look.</summary>
    private const uint NumRgb = 0xFFD200;  // not-an-offset: a colour

    /// <summary>The window's text face: <c>dialog.font</c> / <c>dialog.typeface</c>, else the UI face (a miss bakes
    /// on the pool, and the re-bake rebuilds the window).</summary>
    private static GlyphAtlas? Face() => DialogKnobs.Text.Face(TextRig.Atlas);

    /// <summary>Cold: rebuild the drawable snapshot from the captured content and publish it. Content geometry is
    /// content-relative (y=0 at content top); Draw offsets + clips it. Runs on the message / input threads, and on
    /// whichever thread re-baked the sheet (<see cref="TextRig.Rebaked"/>).</summary>
    private static void Rebuild()
    {
        if (!_open) return;                                    // fading out: keep the last frame for Draw to fade
        GlyphAtlas? atlas = Face();
        if (atlas == null) { _frame = null; return; }

        Reply[] replies = _replies;
        string npcText = _npcText;
        string speakerName = _speakerName;
        string portraitResref = _portraitResref;
        int hover = _hover;

        int vpW = TextRig.ViewportW, vpH = TextRig.ViewportH;
        if (vpW <= 0 || vpH <= 0) { vpW = 1280; vpH = 720; }

        float lh = atlas.LineHeight;
        float titleH = lh + 2f * TitlePadY + 2f;

        bool placed = DialogKnobs.Placed.V, justify = DialogKnobs.Justify.V;
        float maxBodyH = DialogKnobs.MaxHeight.V;
        float panelW, panelX;
        if (placed)
        {
            panelW = System.Math.Clamp(DialogKnobs.W.V, MinW, vpW);
            panelX = Window.ClampAxis(DialogKnobs.X.V, panelW, vpW);   // framework on-screen clamp
        }
        else
        {
            panelW = System.Math.Clamp(vpW * 0.52f, 420f, 820f);
            panelX = (vpW - panelW) * 0.5f;
        }

        float textX = panelX + PanelPad;
        float textW = panelW - 2f * PanelPad;
        float scrollGutter = ScrollbarW + 2f;                  // keep body text clear of the scrollbar track

        bool hasPortrait = portraitResref.Length > 0;
        float portraitH = hasPortrait ? PortraitRows * lh : 0f;
        float portraitW = portraitH * TextRig.PortraitAspect;
        float npcX = textX + (hasPortrait ? portraitW + PanelPad : 0f);
        float npcW = textW - (hasPortrait ? portraitW + PanelPad : 0f) - scrollGutter;

        float numGutter = lh * 1.6f;
        float replyTextW = textW - numGutter - scrollGutter;

        // Color-run parse is content-only (never changes while a page is shown); memoize on reference identity
        // so hover/drag Rebuilds re-wrap (width-dependent) without re-parsing. A new page swaps the string/array.
        if (!ReferenceEquals(npcText, _cachedNpcText)) { _cachedNpcText = npcText; _npcRuns = ColorRunParser.Parse(npcText); }
        if (!ReferenceEquals(replies, _cachedReplies))
        {
            _cachedReplies = replies;
            _replyRuns = new ColorRun[replies.Length][];
            for (int i = 0; i < replies.Length; i++) _replyRuns[i] = ColorRunParser.Parse(replies[i].Text);
        }

        // Pass 1: wrap each block; content laid out in content-relative Y (0 = content top).
        var npc = LayoutBlock(atlas, _npcRuns, npcW);
        float headerH = System.Math.Max(npc.Lines.Count * lh, portraitH);
        var replyBlocks = new Block[replies.Length];

        var b = new VertexBatch(atlas.SolidU, atlas.SolidV) { Outline = DialogKnobs.Text.HaloPx };
        var hits = new List<HitRect>();

        float cy = PanelPad;
        float pcx = textX, pcy = cy;
        if (hasPortrait)
            b.Rect(pcx - BorderW, pcy - BorderW, pcx + portraitW + BorderW, pcy + portraitH + BorderW,
                   BorderCol.r, BorderCol.g, BorderCol.b, BorderCol.a);
        float npcTop = cy;
        if (_sel.Range(npc.Runes.Count, out int selA, out int selB))
            EmitSelection(b, atlas, npc, npcX, npcTop, npcW, justify, selA, selB);
        EmitBlock(b, atlas, npc, npcX, cy, NpcRgb, npcW, justify);
        cy += headerH;

        if (replies.Length > 0)
        {
            b.Rect(textX, cy + SepGap * 0.5f - 0.5f, panelX + panelW - PanelPad - scrollGutter,
                   cy + SepGap * 0.5f + 0.5f, SepCol.r, SepCol.g, SepCol.b, SepCol.a);
            cy += SepGap;
        }

        for (int i = 0; i < replies.Length; i++)
        {
            replyBlocks[i] = LayoutBlock(atlas, _replyRuns[i], replyTextW);
            Block rb = replyBlocks[i];
            float rowH = rb.Lines.Count * lh;
            bool selectable = replies[i].Selectable;
            bool hovered = selectable && hover == i;

            if (hovered)
                b.Rect(panelX + PanelPad * 0.5f, cy - ReplyGap * 0.5f, panelX + panelW - PanelPad * 0.5f - scrollGutter,
                       cy + rowH + ReplyGap * 0.5f, HoverCol.r, HoverCol.g, HoverCol.b, HoverCol.a);

            uint numCol = selectable ? NumRgb : DimRgb;
            DrawPlain(b, atlas, $"{i + 1}.", textX, cy + atlas.Ascent, numCol);
            uint bodyCol = !selectable ? DimRgb : hovered ? ReplyHoverRgb : ReplyRgb;
            EmitBlock(b, atlas, rb, textX + numGutter, cy, bodyCol, replyTextW, justify, forceRgb: !selectable || hovered);

            if (selectable)
                hits.Add(new HitRect(panelX + PanelPad * 0.5f, cy - ReplyGap * 0.5f,
                                     panelW - PanelPad - scrollGutter, rowH + ReplyGap, i));
            cy += rowH + ReplyGap;
        }
        float contentH = cy + PanelPad;

        // panelY: top-anchored. Placed = the user's y, clamped so the panel stays fully on-screen -- against the
        // ACTUAL rest height (content, capped at the breakpoint), NOT the full breakpoint, so it can be dragged
        // right to the top (y=0) and bottom (panel bottom at vpH). A taller next node re-clamps it up to stay on
        // screen. Default = a fixed slot above the bottom margin, sized for the breakpoint so the top never jumps.
        float restPanelH = titleH + System.Math.Min(contentH, maxBodyH);
        float panelY = placed
            ? Window.ClampAxis(DialogKnobs.Y.V, restPanelH, vpH)   // framework on-screen clamp
            : System.Math.Max(0f, vpH - BottomMargin - (titleH + maxBodyH));

        // Title band: the speaker name only (nothing else), truncated at the right pad -- screen coords, fixed.
        var tb = new VertexBatch(atlas.SolidU, atlas.SolidV) { Outline = b.Outline };
        float titleBaseline = panelY + TitlePadY + atlas.Ascent + 1f;
        if (speakerName.Length > 0)
            DrawPlainClipped(tb, atlas, speakerName, panelX + PanelPad, titleBaseline, NameRgb,
                             panelX + panelW - PanelPad);

        _frame = new DialogFrame
        {
            ContentVerts = b.ToArray(), ContentCount = b.VertexCount, Hits = hits.ToArray(),
            PortraitResref = hasPortrait ? portraitResref : "",
            Pcx = pcx, Pcy = pcy, Pw = hasPortrait ? portraitW : 0f, Ph = portraitH,
            TitleVerts = tb.ToArray(), TitleCount = tb.VertexCount,
            ContentH = contentH,
            PanelX = panelX, PanelY = panelY, PanelW = panelW, TitleH = titleH,
            NpcBlock = npc, NpcX = npcX, NpcTop = npcTop, NpcW = npcW, NpcJustify = justify,
        };
    }

    private readonly struct Block
    {
        public readonly List<System.Text.Rune> Runes;
        public readonly List<uint> Cols;
        public readonly List<TextWrapper.LineSpan> Lines;
        public Block(List<System.Text.Rune> runes, List<uint> cols, List<TextWrapper.LineSpan> lines)
        { Runes = runes; Cols = cols; Lines = lines; }
    }

    private static Block LayoutBlock(GlyphAtlas atlas, ColorRun[] runs, float maxW)
    {
        var runes = new List<System.Text.Rune>();
        var cols = new List<uint>();
        foreach (ColorRun run in runs)
        {
            uint rgb = ((uint)(run.R * 255f) << 16) | ((uint)(run.G * 255f) << 8) | (uint)(run.B * 255f);
            foreach (System.Text.Rune r in run.Text.EnumerateRunes()) { runes.Add(r); cols.Add(rgb); }
        }
        var lines = TextWrapper.WrapRunes(runes, maxW, atlas.AdvanceOfCached);
        return new Block(runes, cols, lines);
    }

    private static void EmitBlock(VertexBatch b, GlyphAtlas atlas, Block blk, float x, float top, uint rgb,
                                  float maxW, bool justify, bool forceRgb = false)
    {
        float lh = atlas.LineHeight;
        float baseY = top + atlas.Ascent;
        TextAlign align = justify ? TextAlign.Justify : TextAlign.Left;
        int lastLine = blk.Lines.Count - 1;
        var edges = new List<float>();
        for (int li = 0; li < blk.Lines.Count; li++)
        {
            TextWrapper.LineSpan span = blk.Lines[li];
            RowAlign.Compute(align, atlas, blk.Runes, span, maxW, li == lastLine, out float xOff, out float extraPerGap);
            SelectableText.RowEdges(atlas, blk.Runes, span.Start, span.End, x + xOff, extraPerGap, edges);   // shared pen walk
            for (int k = span.Start; k < span.End; k++)
            {
                System.Text.Rune ru = blk.Runes[k];
                if (!atlas.TryGet(ru.Value, out Glyph g)) continue;
                uint c = forceRgb ? rgb : blk.Cols[k];
                b.Glyph(g, edges[k - span.Start], baseY, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
            }
            baseY += lh;
        }
    }

    /// <summary>Emit the text-selection highlight for the npc block into the content batch (behind the glyphs).
    /// <paramref name="selA"/>/<paramref name="selB"/> are normalized rune indices; the rect for each line's slice
    /// is measured with the same <see cref="SelectableText.RowEdges"/> walk the glyphs use. <paramref name="top"/> is the block's
    /// content-relative top (matches the <see cref="EmitBlock"/> call).</summary>
    private static void EmitSelection(VertexBatch b, GlyphAtlas atlas, Block blk, float x, float top,
                                      float maxW, bool justify, int selA, int selB)
    {
        if (selB <= selA) return;
        float lh = atlas.LineHeight;
        TextAlign align = justify ? TextAlign.Justify : TextAlign.Left;
        int lastLine = blk.Lines.Count - 1;
        var edges = new List<float>();
        for (int li = 0; li < blk.Lines.Count; li++)
        {
            TextWrapper.LineSpan span = blk.Lines[li];
            int a = System.Math.Max(selA, span.Start), c = System.Math.Min(selB, span.End);
            if (c <= a) continue;
            RowAlign.Compute(align, atlas, blk.Runes, span, maxW, li == lastLine, out float xOff, out float extraPerGap);
            SelectableText.RowEdges(atlas, blk.Runes, span.Start, span.End, x + xOff, extraPerGap, edges);
            float y = top + li * lh;
            b.Rect(edges[a - span.Start], y, edges[c - span.Start], y + lh, SelCol.r, SelCol.g, SelCol.b, SelCol.a);
        }
    }

    private static void DrawPlain(VertexBatch b, GlyphAtlas atlas, string s, float x, float baseline, uint rgb)
        => DrawPlainClipped(b, atlas, s, x, baseline, rgb, float.MaxValue);

    /// <summary>Draw a plain string, stopping (with an ellipsis) once the pen would pass <paramref name="maxX"/>.</summary>
    private static void DrawPlainClipped(VertexBatch b, GlyphAtlas atlas, string s, float x, float baseline,
                                         uint rgb, float maxX)
    {
        float r = ((rgb >> 16) & 0xFF) / 255f, g = ((rgb >> 8) & 0xFF) / 255f, bl = (rgb & 0xFF) / 255f;
        float penX = x;
        foreach (System.Text.Rune ru in s.EnumerateRunes())
        {
            float adv = atlas.TryGet(ru.Value, out Glyph gl) ? gl.Advance : atlas.AdvanceOf(ru);
            if (penX + adv > maxX)
            {
                if (atlas.TryGet('.', out Glyph dot))
                    for (int d = 0; d < 2 && penX + dot.Advance <= maxX + dot.Advance * 2f; d++)
                    { b.Glyph(dot, penX, baseline, r, g, bl, 1f); penX += dot.Advance; }
                break;
            }
            if (gl.W > 0) b.Glyph(gl, penX, baseline, r, g, bl, 1f);
            penX += adv;
        }
    }


    // ---- helpers ------------------------------------------------------------------------------

    private static string Trunc(string s) => s.Length <= 60 ? s : s.Substring(0, 60) + "...";

    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private static readonly GetGameObjectFn _getGameObject = EngineFn.GetGameObject;
    /// <summary>CClientExoApp::GetObjectName(this, objId, CExoString* out) -- fills a caller-owned CExoString.</summary>
    private static readonly GetObjectNameFn _getObjectName = EngineFn.GetObjectName;
    /// <summary>CExoString::~CExoString(this) -- frees the buffer GetObjectName allocated into our stack CExoString.</summary>
    private static readonly ExoStringDtorFn _exoDtor = EngineFn.ExoStringDtor;

    private const byte PortraitSizeMedium = 3;

    /// <summary>Resolve a speaker object id to its portrait resref (with size suffix), or "" for a speaker with
    /// no portrait -- in which case we draw NO portrait and reclaim the reading width, unlike vanilla which pads
    /// a generic placeholder. Runs on the message thread from inside the ShowDialogEntry hook.</summary>
    private static string ResolvePortrait(uint speakerId)
    {
        if (_appMgr == IntPtr.Zero || _getGameObject == null) return "";
        IntPtr mgr = *(IntPtr*)_appMgr; if (mgr == IntPtr.Zero) return "";
        IntPtr app = *(IntPtr*)mgr;     if (app == IntPtr.Zero) return "";
        IntPtr obj = _getGameObject(app, speakerId);
        if (obj == IntPtr.Zero) return "";
        IntPtr vtbl = *(IntPtr*)obj;
        if (vtbl == IntPtr.Zero) return "";

        var o = new CNWCObject(obj);
        if (o.GetPortraitFn == IntPtr.Zero) return "";
        // CResRef is 17 bytes (char[16] + a length byte at +0x10); a 16-byte buffer smashes the stack canary
        // (the SIGABRT that once crashed the client). Over-allocate to 32.
        byte* buf = stackalloc byte[32];
        for (int i = 0; i < 32; i++) buf[i] = 0;
        o.GetPortrait(buf, PortraitSizeMedium);
        int n = 0; while (n < 16 && buf[n] != 0) n++;
        return n == 0 ? "" : Encoding.ASCII.GetString(buf, n);
    }

    /// <summary>Resolve a speaker object id to its display name for the title band ("" = unnamed). Message
    /// thread, inside the ShowDialogEntry hook; a stale or arbitrary id is safe (the engine null-checks it).
    /// GetObjectName takes a caller-owned CExoString {char* buf@0; uint len@8; uint cap@0xc} that MUST be zeroed
    /// first (the engine frees a non-null buf) and dtor'd after copying out, or a node leaks a heap block.</summary>
    private static string ResolveName(uint speakerId)
    {
        if (_appMgr == IntPtr.Zero || _getObjectName == null || _exoDtor == null) return "";
        IntPtr mgr = *(IntPtr*)_appMgr; if (mgr == IntPtr.Zero) return "";
        IntPtr app = *(IntPtr*)mgr;     if (app == IntPtr.Zero) return "";
        // app is the CClientExoApp*: GetObjectName does the +8 to the internal itself.
        byte* exo = stackalloc byte[16];
        for (int i = 0; i < 16; i++) exo[i] = 0;   // MANDATORY: the engine frees a non-null buf before writing
        _getObjectName(app, speakerId, (IntPtr)exo);
        string name = Native.ReadExo((IntPtr)exo);        // copies out (Latin1); "" when len==0
        _exoDtor((IntPtr)exo);                      // free the engine-allocated buffer (null-safe)
        return name.Trim();
    }

    /// <summary>Off-device proof: reply gating (from the MVP) plus the animation scalars -- window fade
    /// open/close, per-node content cross-fade restart, body-height ease + snap, and the disabled snap.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        Reply[] end = MakeReplies(System.Array.Empty<string>(), System.Array.Empty<uint>(), 0);
        check.Expect(end.Length == 1 && end[0].Selectable && end[0].Text == EndDialogLabel && end[0].ServerIndex == 0xffffffff,
          "zero replies synthesizes a selectable [End Dialog]");
        Reply[] r = MakeReplies(new[] { "a", "b", "c" }, new uint[] { 10, 20, 30 }, 1);
        check.Expect(r[0].Selectable && r[1].Selectable && !r[2].Selectable, "trailing numOld=1 marks the last reply non-selectable");
        check.Expect(r[0].ServerIndex == 10 && r[2].ServerIndex == 30, "server indices map straight through");
        Reply[] allOld = MakeReplies(new[] { "x", "y" }, new uint[] { 1, 2 }, 2);
        check.Expect(!allOld[0].Selectable && !allOld[1].Selectable, "numOld==count leaves nothing selectable");

        // ---- animation scalars (save/restore all the anim statics; the knobs by plain value writes, never a set) ----
        bool sEn = DialogKnobs.Anim.V; int sIn = DialogKnobs.FadeIn.V, sOut = DialogKnobs.FadeOut.V; float sWt = _winAlphaTarget, sCt = _contentAlphaTarget;
        float sWa = _windowAlpha, sCa = _contentAlpha, sBh = _bodyH; long sClk = _animClockMs; bool sPr = _animPrimed;
        int sOg = _openGen, sCg = _contentGen, sLo = _lastOpenGen, sLc = _lastContentGen;

        DialogKnobs.Anim.V = true; DialogKnobs.FadeIn.V = 100; DialogKnobs.FadeOut.V = 100;
        // Open: window fades from 0, body eases from 0, content fades from 0.
        _winAlphaTarget = 1f; _contentAlphaTarget = 1f; _openGen = 1; _lastOpenGen = 0; _contentGen = 1; _lastContentGen = 0;
        _windowAlpha = 1f; _contentAlpha = 1f; _bodyH = 999f; _animPrimed = false;
        AdvanceAnim(1000, 300f);
        check.Expect(_windowAlpha == 0f && _bodyH == 0f && _contentAlpha == 0f, "open (gen bump) restarts window+content fade and body stretch from 0");
        AdvanceAnim(1050, 300f);
        check.Expect(_windowAlpha > 0.4f && _windowAlpha < 0.6f, "window fades in ~half over half the duration");
        check.Expect(_bodyH > 50f && _bodyH < 300f, "body eases toward its target (rising)");
        AdvanceAnim(1400, 300f);
        check.Expect(_windowAlpha == 1f && _contentAlpha == 1f, "window + content fade-in clamp at 1");

        // Mid-conversation reply pick: content fades OUT, window stays up (no window fade on a pick).
        _contentAlphaTarget = 0f;
        AdvanceAnim(1450, 120f);
        check.Expect(_contentAlpha < 1f && _contentAlpha > 0f, "picking a reply fades the content out");
        check.Expect(_windowAlpha == 1f, "picking a reply does NOT fade the window");

        // Next node while open (only content gen bumps): content restarts from 0 toward 1, body re-eases, window stays.
        _contentGen = 2; _contentAlphaTarget = 1f; _bodyH = 300f;
        AdvanceAnim(1500, 120f);
        check.Expect(_contentAlpha < 1f, "a new node restarts the content cross-fade from 0");
        check.Expect(_windowAlpha == 1f, "a new node does NOT restart the window fade");
        check.Expect(_bodyH < 300f && _bodyH > 120f, "body eases down toward the shorter node");

        // End of conversation: window fades out.
        _winAlphaTarget = 0f;
        AdvanceAnim(1560, 120f); AdvanceAnim(1760, 120f);
        check.Expect(_windowAlpha == 0f, "close fades the window out to 0");

        // Disabled -> snap.
        DialogKnobs.Anim.V = false; _windowAlpha = 0.3f; _winAlphaTarget = 1f; _contentAlphaTarget = 1f; _bodyH = 10f; _contentAlpha = 0.2f;
        AdvanceAnim(2000, 250f);
        check.Expect(_windowAlpha == 1f && _bodyH == 250f && _contentAlpha == 1f, "animations disabled -> everything snaps to target");

        DialogKnobs.Anim.V = sEn; DialogKnobs.FadeIn.V = sIn; DialogKnobs.FadeOut.V = sOut; _winAlphaTarget = sWt; _contentAlphaTarget = sCt;
        _windowAlpha = sWa; _contentAlpha = sCa; _bodyH = sBh; _animClockMs = sClk; _animPrimed = sPr;
        _openGen = sOg; _contentGen = sCg; _lastOpenGen = sLo; _lastContentGen = sLc;

        // ---- on-screen clamping + scale invariance: Rebuild derives the panel rect via Window.ClampAxis
        // so it stays fully on-screen for any user X/Y. (Text-selection geometry moved to SelectableText.SelfCheck
        // alongside the lifted capability.)
        {
            const float vpW = 1280f, vpH = 720f, pw = 400f, ph = 300f;
            check.Expect(Window.ClampAxis(-50f, pw, vpW) == 0f, "panel pins to the left edge when x < 0");
            check.Expect(Window.ClampAxis(5000f, pw, vpW) == vpW - pw, "panel pins to the right edge when x overflows");
            check.Expect(Window.ClampAxis(100f, pw, vpW) == 100f, "an already on-screen x is left untouched");
            check.Expect(Window.ClampAxis(-10f, vpH + 200f, vpH) == 0f, "a panel taller than the viewport pins to the top");
            check.Expect(Window.ClampAxis(600f, ph, vpH) == vpH - ph, "y overflowing the bottom pins to keep the panel on-screen");

            // Scale invariance: clamping is screen-space and must NOT read the UI scale, so the result is identical
            // whatever Metrics.Scale is. Compute at 1x and 2x and assert equality -- a future scale-dependent
            // regression in ClampAxis (or a caller folding scale into the bound) flips this and fails the check.
            // Restore the global BEFORE asserting, so a failed check.Expect() can never leak a 2x scale into the process.
            float savedScale = Metrics.Scale;
            Metrics.Scale = 1f; float clampAt1 = Window.ClampAxis(600f, pw, vpW);
            Metrics.Scale = 2f; float clampAt2 = Window.ClampAxis(600f, pw, vpW);
            Metrics.Scale = savedScale;
            check.Expect(clampAt1 == 600f && clampAt2 == 600f,
              $"on-screen clamping is screen space at any UI scale: an x of 600 with a 400-wide panel in 1280 "
              + $"is left alone (1x {clampAt1}, 2x {clampAt2})");
        }

        log.Info(check.Ok ? "DialogService.SelfCheck: passed" : "DialogService.SelfCheck: FAILED");
    }
}
