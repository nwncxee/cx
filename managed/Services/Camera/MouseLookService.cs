using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Ui;
using CX.Graphics;

namespace CX.Services.Camera;

/// <summary>
/// Mouse-look on right-drag: the engine's one mouse orbit is middle-drag, and this makes right-drag the same orbit
/// with the cursor captured -- SDL relative mode, every motion swallowed and fed to the engine's impulse path, so
/// locks and limits stay engine-owned; a press under <c>threshold</c> is replayed so the radial menu still opens.
/// The action camera forwards events rewritten to the screen centre. Outermost chain layer, allocates nothing.
/// </summary>
public sealed unsafe class MouseLookService : IExtenderService
{
    private static ExtenderLog? _log;
    private static WinMessageHandlerFn* _next;
    private static readonly IntPtr _guiMan = Native.Sym(SymbolMap.GuiMan);

    // ---- SDL_Event layout (same rows UiInputService reads; motion adds the relative pair) ----
    private const uint SdlWindowEvent = 0x200, SdlMouseMotion = 0x400, SdlMouseDown = 0x401, SdlMouseUp = 0x402;  // not-an-offset: SDL key/event code
    private const byte SdlWindowFocusLost = 0xd, SdlButtonMiddle = 2, SdlButtonRight = 3;  // not-an-offset: SDL key/event code
    /// <summary><c>sizeof(SDL_Event)</c> in SDL2: the held press is copied whole for the replay.</summary>
    private const int SdlEventSize = 56;

    /// <summary>Middle-drag's degrees per pixel at 1920 wide: turn = dx * -75 * 0.2 / (W/1920/0.01), tilt =
    /// -dy * 75 * 0.15 / (W/1080/0.01), both over the viewport WIDTH (engine's own formula, W/1080 included).</summary>
    internal const float TurnPerPx = -288f, TiltPerPx = -121.5f;

    private enum State { Idle, Pending, Looking }
    private static State _state;
    private static byte _button;
    private static int _originX, _originY;
    /// <summary>The held right press, copied whole at capture and replayed on a still release. One block for
    /// the life of the hook (a fixed buffer cannot be a static field), so the handler still allocates nothing.</summary>
    private static byte* _held;

    private static int _looks, _clicks, _actions;
    /// <summary>Action camera held (relative mode ours) and the centre the events are pinned to.</summary>
    private static bool _action;
    /// <summary>Is the action camera actually held this frame (the knob is on AND nothing is pausing it)?
    /// What <see cref="HeadTrackService"/> turns the head on.</summary>
    internal static bool ActionHeld => _action;
    private static int _cx, _cy;
    private static SdlGetModStateFn _modState;
    /// <summary>SDL <c>KMOD_LALT | KMOD_RALT</c>: the hold that frees the cursor.</summary>
    private const int KmodAlt = 0x300;  // not-an-offset: SDL key/event code
    private const uint SdlKeyDown = 0x300;  // not-an-offset: SDL key/event code
    /// <summary>SDL_KeyboardEvent: <c>repeat</c> byte and <c>keysym.sym</c>; the Alt keycodes.</summary>
    private const uint SdlkLAlt = 0x400000E2, SdlkRAlt = 0x400000E6;  // not-an-offset: SDL key/event code
    /// <summary>Alt as a toggle (the mode's <c>alttoggle</c>): the cursor is freed until the next Alt press.</summary>
    private static bool _altFree;
    private static WalkPlayerToPointFn _walkOrig;
    private static FeedbackHitCheckHostileFn _pickHostileOrig;
    private static FeedbackHitCheckNonHostileFn _pickNonHostileOrig;
    private static int _walksRefused, _selfHidden;
    /// <summary>The toggle bind per camera mode: an SDL keycode, or a mouse button carried negative (-4 = X1,
    /// -5 = X2); 0 = none. Indexed by the engine's camera mode byte, like <see cref="CameraModes.ByMode"/>.</summary>
    private static readonly int[] _toggleCodes = new int[3];
    private static int ToggleCode => CameraService.Mode is >= 0 and < 3 ? _toggleCodes[CameraService.Mode] : 0;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraKnobs.Declare();
        if (!EngineLayout.Require(log, "MouseLook", "CGuiMan")) return;
        if (!SymbolMap.Require(log, "MouseLook", SymbolMap.WinMessageHandler, SymbolMap.TurnCamera, SymbolMap.TiltCamera,
                               SymbolMap.SdlSetRelativeMouseMode, SymbolMap.SdlWarpMouseInWindow,
                               SymbolMap.GuiMan, SymbolMap.GuiObjectHitCheck, SymbolMap.GetModule, SymbolMap.AppManager)) return;
        _modState = EngineFn.SdlGetModState;
        Crosshair.Register();
        ActionKeyChanged();
        UiHost.AddChordConsumer(new ActionToggle());
        // The action camera's two refusals: no click-to-move, and the player is never what the crosshair picks.
        if (SymbolMap.Require(log, "MouseLook.action", SymbolMap.WalkPlayerToPoint, SymbolMap.FeedbackHitCheckHostile, SymbolMap.FeedbackHitCheckNonHostile))
        {
            _walkOrig = hooks.InstallWalkPlayerToPoint(&OnWalkPlayerToPoint);
            _pickHostileOrig = hooks.InstallFeedbackHitCheckHostile(&OnPickHostile);
            _pickNonHostileOrig = hooks.InstallFeedbackHitCheckNonHostile(&OnPickNonHostile);
            log.Info((_walkOrig == null || _pickHostileOrig == null || _pickNonHostileOrig == null)
                ? "MouseLook: action camera refusals partly unhooked (walk/pick install refused) -- click-to-move or self-pick may survive it"
                : "MouseLook: action camera hooks WalkPlayerToPoint + FeedbackHitCheck{Hostile,NonHostile}");
        }
        AimAssist.Init(log, hooks);
        ActionFeedback.Init(log, hooks);

        _next = hooks.ChainWinMessageHandler(&OnWinMessage, 100);
        if (_next == null) { log.Info("MouseLook: standing down (WinMessageHandler chain refused)"); return; }
        _held = (byte*)NativeMemory.AllocZeroed(SdlEventSize);
        log.Info(!MouseLook.Enabled.V ? "MouseLook: chained, this mode's mouselook=0 -- the stock middle-drag runs until it is turned on"
            : MouseLook.Right.V
            ? $"MouseLook: right-drag orbits (sensitivity {MouseLook.Sens.V}, invert {MouseLook.Invert.V}, threshold {MouseLook.Threshold.V}px); radial menu = a still right-click; middle-drag captured"
            : "MouseLook: middle-drag captured (cursor held in place); right-drag off (opt in with the mode's Right-drag row)");
    }

    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (ev == IntPtr.Zero) return Forward(ev);
        if (!MouseLook.Enabled.V) { if (_state != State.Idle) Release(); return Forward(ev); }
        // Only while the action camera would otherwise be up: Alt+Tab, an Alt chord in a dialog and any Alt the
        // GUI or a CX window owns the keyboard for are not the cursor toggle.
        if (MouseLook.AltToggle.V && ActionGateOpen() && *(uint*)ev == SdlKeyDown && ((SdlEvent*)ev)->Repeat == 0
            && ((SdlEvent*)ev)->SymU is SdlkLAlt or SdlkRAlt) _altFree = !_altFree;
        bool want = ActionWanted();
        if (want != _action) SetAction(want);
        if (_action) return OnActionEvent(ev);
        switch (*(uint*)ev)
        {
            case SdlMouseDown:
                byte b = ((SdlEvent*)ev)->Button;
                if (_state != State.Idle || (b != SdlButtonMiddle && !(b == SdlButtonRight && MouseLook.Right.V))) break;
                int mx = ((SdlEvent*)ev)->X, my = ((SdlEvent*)ev)->Y;
                if (UiInputService.EngineGuiWantsPress() || UiHost.HitTest(mx, my) != null) break;   // the GUI's press
                _originX = mx; _originY = my; _button = b;
                if (b == SdlButtonMiddle)
                {
                    // The engine's own orbit button: it keeps the press, we keep the cursor.
                    _state = State.Looking; _looks++;
                    EngineFn.SdlSetRelativeMouseMode(1);
                    return Forward(ev);
                }
                Buffer.MemoryCopy((void*)ev, _held, SdlEventSize, SdlEventSize);
                _state = State.Pending;
                return 0;
            case SdlMouseMotion:
                if (_state == State.Idle) break;
                if (_state == State.Pending)
                {
                    int dx = ((SdlEvent*)ev)->X - _originX, dy = ((SdlEvent*)ev)->Y - _originY;
                    int threshold = MouseLook.Threshold.V;
                    if (dx * dx + dy * dy <= threshold * threshold) break;   // a still click so far: the engine tracks it
                    _state = State.Looking; _looks++;
                    EngineFn.SdlSetRelativeMouseMode(1);
                    return 0;   // this event's delta already moved the (soon warped-back) cursor; start clean
                }
                if (CameraService.GateUp || FreeCameraService.Active) { Release(); return 0; }
                IntPtr module = CameraService.Module();
                if (module != IntPtr.Zero)
                {
                    float w = ViewportWidth();
                    float k = MouseLook.Sens.V / w;
                    var mod = new CNWCModule(module);
                    mod.TurnCamera(((SdlEvent*)ev)->XRel * TurnPerPx * k, 1);
                    mod.TiltCamera(((SdlEvent*)ev)->YRel * TiltPerPx * k * (MouseLook.Invert.V ? -1f : 1f), 1);
                }
                return 0;
            case SdlMouseUp:
                if (_state == State.Idle || ((SdlEvent*)ev)->Button != _button) break;
                if (_button == SdlButtonMiddle)
                {
                    // CExoInputInternal::AddEvent turns a button event's x/y into a mouse-move BEFORE the button
                    // action, and the engine's orbit is still armed on the up: under relative mode that x/y is
                    // SDL's drifted, window-clamped position, so the up would carry the whole drag as one turn.
                    Release();
                    ((SdlEvent*)ev)->X = _originX; ((SdlEvent*)ev)->Y = _originY;
                    return Forward(ev);
                }
                if (_state == State.Pending)
                {
                    // A click, not a drag: the engine sees press then release, and opens the radial as it always did.
                    _state = State.Idle; _clicks++;
                    Forward((IntPtr)_held);
                    return Forward(ev);
                }
                Release();
                return 0;
            case SdlWindowEvent:
                if (((SdlEvent*)ev)->WindowKind == SdlWindowFocusLost && _state != State.Idle) Release();
                break;
        }
        return Forward(ev);
    }

    // ---- the action camera ----

    private static bool ActionWanted() => ActionGateOpen() && !AltFreed();

    /// <summary>Everything the action camera needs except the Alt release itself -- the gate the Alt toggle is
    /// allowed to flip in, so an Alt pressed out of game, in a dialog or while anything owns the keyboard is
    /// just an Alt.</summary>
    private static bool ActionGateOpen()
        => MouseLook.Action.V && GameGate.InGame && CameraService.Mode >= 0 && !CameraService.GateUp && !FreeCameraService.Active
           && !UiHost.AnyKeyboardOwner && !UiHost.AnySuppressChords;

    private static bool AltFreed()
        => MouseLook.AltToggle.V ? _altFree : _modState != null && (_modState() & KmodAlt) != 0;

    private static void SetAction(bool on)
    {
        if (on)
        {
            if (_state != State.Idle) Release();
            ViewportCentre(out _cx, out _cy);
            EngineFn.SdlSetRelativeMouseMode(1); _actions++;
        }
        else
        {
            EngineFn.SdlSetRelativeMouseMode(0);
            EngineFn.SdlWarpMouseInWindow(IntPtr.Zero, _cx, _cy);   // the cursor appears where the engine believes it is
        }
        _action = on;
        Crosshair.Visible = on;
    }

    /// <summary>Every event under the action camera: motion turns the camera and reaches the engine as "the cursor
    /// is at the centre, it did not move"; buttons reach it at the centre; the rest is untouched. The centre is
    /// the aim point, which <see cref="AimAssist"/> moves off the exact centre when it has a candidate -- one
    /// packed load, so the hot path stays two field writes either way.</summary>
    private static int OnActionEvent(IntPtr ev)
    {
        if (!AimAssist.Point(out int ax, out int ay)) { ax = _cx; ay = _cy; }
        switch (*(uint*)ev)
        {
            case SdlMouseMotion:
                IntPtr module = CameraService.Module();
                if (module != IntPtr.Zero)
                {
                    float k = MouseLook.Sens.V / ViewportWidth();
                    var mod = new CNWCModule(module);
                    mod.TurnCamera(((SdlEvent*)ev)->XRel * TurnPerPx * k, 1);
                    mod.TiltCamera(((SdlEvent*)ev)->YRel * TiltPerPx * k * (MouseLook.Invert.V ? -1f : 1f), 1);
                }
                ((SdlEvent*)ev)->X = ax; ((SdlEvent*)ev)->Y = ay;
                ((SdlEvent*)ev)->XRel = 0; ((SdlEvent*)ev)->YRel = 0;
                break;
            case SdlMouseDown:
            case SdlMouseUp:
                ((SdlEvent*)ev)->X = ax; ((SdlEvent*)ev)->Y = ay;
                break;
            case SdlWindowEvent:
                // The latch too: a toggled-free cursor must not survive the alt-tab that freed it.
                if (((SdlEvent*)ev)->WindowKind == SdlWindowFocusLost) { _altFree = false; SetAction(false); }
                break;
        }
        return Forward(ev);
    }

    [UnmanagedCallersOnly]
    private static int OnWalkPlayerToPoint(IntPtr app, float x, float y, float z, int flags, uint obj, int extra)
    {
        if (_action) { _walksRefused++; return 0; }   // 0 = the engine's own "not walkable" verdict
        return _walkOrig(app, x, y, z, flags, obj, extra);
    }

    [UnmanagedCallersOnly]
    private static int OnPickHostile(IntPtr app, IntPtr* obj, IntPtr point, int* p3, int p4)
        => TabTargetService.OverridePick(obj, point, out int v) ? v : HidePlayer(_pickHostileOrig(app, obj, point, p3, p4), obj);

    [UnmanagedCallersOnly]
    private static int OnPickNonHostile(IntPtr app, IntPtr* obj, IntPtr point, int* p3, int p4)
        => TabTargetService.OverridePick(obj, point, out int v) ? v : HidePlayer(_pickNonHostileOrig(app, obj, point, p3, p4), obj);

    /// <summary>Both world picks are hooked (so <see cref="TabTargetService"/>'s fire-at-the-lock substitution
    /// has a seam to stand on).</summary>
    internal static bool PicksHooked => _pickHostileOrig != null && _pickNonHostileOrig != null;

    /// <summary>Under the action camera the crosshair sits on the player half the time: a pick that landed on the
    /// player creature becomes "nothing" (the engine's 0 verdict), so no self-hover, self-tooltip or self-select.</summary>
    private static int HidePlayer(int verdict, IntPtr* obj)
    {
        if (!_action || verdict == 0 || obj == null || *obj == IntPtr.Zero || *obj != CameraService.PlayerCreature()) return verdict;
        *obj = IntPtr.Zero; _selfHidden++;
        return 0;
    }

    /// <summary>Re-parse every mode's <c>actionkey</c>: "MouseN" is a button, anything else goes through SDL's own
    /// key-name table (cold: a chord edit, not a hot hook).</summary>
    internal static void ActionKeyChanged()
    {
        for (int i = 0; i < _toggleCodes.Length; i++) _toggleCodes[i] = ParseKey(CameraModes.ByMode[i].ActionKey.V);
    }

    private static int ParseKey(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) return 0;
        if (name.StartsWith("mouse", StringComparison.OrdinalIgnoreCase) && int.TryParse(name.AsSpan(5), out int btn) && btn is >= 1 and <= 5)
            return -btn;
        if (EngineFn.SdlGetKeyFromName == null) return 0;
        // SDL_GetKeyFromName answers a one-character name with that character; a key event's sym is lower-case.
        if (name.Length == 1) name = name.ToLowerInvariant();
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        int code;
        fixed (byte* p = utf8) code = EngineFn.SdlGetKeyFromName(p);
        if (code == 0) _log?.Info($"MouseLook: actionkey '{name}' is not an SDL key name; nothing bound");
        return code;
    }

    /// <summary>The chord that flips the live mode's <c>action</c>: the key or mouse button named by its
    /// <c>actionkey</c>, fired when no window consumed it.</summary>
    private sealed class ActionToggle : IChordConsumer
    {
        public int Priority => 1;
        public bool WantsKey(int key) => ToggleCode > 0 && key == ToggleCode;
        public void Fire(uint key, int sdlMods) { if (ToggleCode > 0 && key == ToggleCode) Flip(); }
        public void FireMouse(int button, int sdlMods) { if (ToggleCode < 0 && button == -ToggleCode) Flip(); }
        private static void Flip() => MouseLook.Action.Set(!MouseLook.Action.V);
    }

    /// <summary>The action toggle's <c>changed:</c>: the off edge frees the cursor at once (the on edge waits for
    /// the next event, which reads the knob).</summary>
    internal static void ActionChanged() { _altFree = false; if (!MouseLook.Action.V && _action) SetAction(false); }

    /// <summary>The master's <c>changed:</c>: the off edge releases a look or a held press at once, so the
    /// cursor is never left captured waiting for the next mouse event. Nothing on the on edge -- the next press
    /// reads <c>MouseLook.Enabled.V</c>.</summary>
    internal static void MasterChanged() { if (!MouseLook.Enabled.V) { Release(); if (_action) SetAction(false); } }

    /// <summary>End a look or a held press: cursor back where the drag began, relative mode off, state idle.</summary>
    private static void Release()
    {
        if (_state == State.Looking)
        {
            EngineFn.SdlSetRelativeMouseMode(0);
            EngineFn.SdlWarpMouseInWindow(IntPtr.Zero, _originX, _originY);   // NULL window = the one with mouse focus
        }
        _state = State.Idle;
    }


    /// <summary>The screen point the action camera pins every event to, in the SDL window pixels the engine's
    /// own hit-tests run in. The one source of that space: <see cref="AimAssist"/> projects into it.</summary>
    internal static void Centre(out int x, out int y) { x = _cx; y = _cy; }

    /// <summary>The viewport centre in SDL window pixels, whether or not the action camera is up.</summary>
    internal static void ViewportCentre(out int x, out int y)
    {
        var gm = new CGuiMan(_guiMan != IntPtr.Zero ? *(IntPtr*)_guiMan : IntPtr.Zero);
        x = !gm.IsNull ? gm.VpWidth / 2 : 960;
        y = !gm.IsNull ? gm.VpHeight / 2 : 540;
    }

    internal static float ViewportWidth()
    {
        var gm = new CGuiMan(_guiMan != IntPtr.Zero ? *(IntPtr*)_guiMan : IntPtr.Zero);
        int w = !gm.IsNull ? gm.VpWidth : 0;
        return w > 0 ? w : 1920f;
    }

    private static int Forward(IntPtr ev) => (*_next)(ev);

    /// <summary>The mouse-look line of the live mode's status.</summary>
    internal static string Report() =>
        FormattableString.Invariant($"{(MouseLook.Enabled.V ? "on" : "OFF (the stock middle-drag)")}; right-drag {(MouseLook.Right.V ? "on" : "off")}, middle-drag {(MouseLook.Enabled.V ? "captured" : "stock")}; sensitivity {MouseLook.Sens.V:0.##} invert {(MouseLook.Invert.V ? 1 : 0)} threshold {MouseLook.Threshold.V}px | {_looks} look(s), {_clicks} replayed click(s), state {_state}; action camera {(MouseLook.Action.V ? (_action ? "on" : "paused") : "off")}, {_actions} capture(s), {_walksRefused} walk(s) refused, {_selfHidden} self-pick(s) hidden | {AimAssist.Report()} | {ActionFeedback.Report()} | {HeadTrackService.Fragment}");
}

/// <summary>The action camera's crosshair: a small four-arm reticle at the viewport centre, drawn as a registered
/// window with no rect (it hits nothing), only while <see cref="Visible"/>. Contextual: the engine's live mouse
/// cursor id (<see cref="AimAssist.CursorId"/>) is the same field the vanilla cursor reads, so the reticle takes
/// that verb's colour and arm length; four corner ticks appear while the assist holds a candidate.</summary>
internal static class Crosshair
{
    internal static volatile bool Visible;
    private static Window? _window;
    private static VertexBatch? _batch;
    /// <summary>Arm length, gap from the centre and thickness, in px at GUI scale 1.</summary>
    private const float Arm = 8f, Gap = 3f, Thick = 2f;
    /// <summary>Assist brackets: half-size of the corner square and the length of each tick, same units.</summary>
    private const float Bracket = 14f, BracketTick = 5f;

    private const byte FamPlain = 0, FamAttack = 1, FamTalk = 2, FamUse = 3, FamMagic = 4, FamHeal = 5, FamRefused = 6;

    /// <summary>Cursor id -&gt; verb family. An odd id is a verb's "up" art and the next even one its pressed
    /// twin, so every verb is one pair; a <c>no&lt;verb&gt;</c> is its own following pair, not the even half.
    /// Anything this table does not name -- default/walk/nowalk, the 59..90 movement arrows and the 93+ module
    /// custom cursors -- is the plain cross.</summary>
    private static readonly byte[] Family = BuildFamilies();

    /// <summary>Per family: r, g, b, arm scale. Stride 4, indexed by the family byte.</summary>
    private static readonly float[] Tint =
    {
        1f,    1f,    1f,    1f,      // plain
        1f,    0.28f, 0.22f, 1.35f,   // attack / kill: red, longer arms
        0.38f, 0.82f, 1f,    1f,      // talk / examine / follow
        1f,    0.85f, 0.28f, 1f,      // use / door / lock / disarm / pickup / action
        0.76f, 0.48f, 1f,    1f,      // magic
        0.42f, 1f,    0.55f, 1f,      // heal
        0.58f, 0.58f, 0.58f, 0.8f,    // refused: dim, short, plus a centre dot
    };

    private static byte[] BuildFamilies()
    {
        byte[] f = new byte[93];
        void P(int odd, byte fam) { f[odd] = fam; f[odd + 1] = fam; }
        P(7, FamAttack); P(51, FamAttack);
        P(11, FamTalk); P(15, FamTalk); P(17, FamTalk);
        P(21, FamUse); P(23, FamUse); P(25, FamUse); P(33, FamUse);
        P(37, FamUse); P(41, FamUse); P(45, FamUse); P(47, FamUse); P(91, FamUse);
        P(29, FamMagic);
        P(55, FamHeal);
        P(9, FamRefused); P(13, FamRefused); P(19, FamRefused); P(27, FamRefused); P(31, FamRefused);
        P(35, FamRefused); P(39, FamRefused); P(43, FamRefused); P(49, FamRefused);
        P(53, FamRefused); P(57, FamRefused);
        return f;
    }

    internal static void Register()
    {
        _window ??= new Window(0, 0, 0, 0) { Focusable = false, DrawContent = Draw, HitTestOverride = (_, _) => false };
    }

    private static unsafe void Draw()
    {
        if (!Visible) return;
        GlyphAtlas? sheet = TextRig.Atlas;
        if (sheet == null) return;
        int id = AimAssist.CursorId();
        byte fam = (uint)id < (uint)Family.Length ? Family[id] : FamPlain;
        int k = fam * 4;
        float r = Tint[k], gr = Tint[k + 1], bl = Tint[k + 2], arms = Tint[k + 3];
        float s = Metrics.GameUiScale < 1f ? 1f : Metrics.GameUiScale;
        float cx = TextRig.ViewportW * 0.5f, cy = TextRig.ViewportH * 0.5f, a = Arm * s * arms, g = Gap * s, t = Thick * s * 0.5f;
        VertexBatch b = _batch ??= new VertexBatch(sheet.SolidU, sheet.SolidV);
        b.Reset(sheet.SolidU, sheet.SolidV);
        // Dark outline under a light cross so it reads on any ground.
        Emit(b, cx, cy, a + 1f, g - 1f, t + 1f, 0f, 0f, 0f, 0.6f);
        Emit(b, cx, cy, a, g, t, r, gr, bl, 0.9f);
        if (fam == FamRefused) b.Rect(cx - t, cy - t, cx + t, cy + t, r, gr, bl, 0.9f);
        if (AimAssist.AssistTarget != 0) Corners(b, cx, cy, Bracket * s, BracketTick * s, t, r, gr, bl, 0.75f);
        ActionFeedback.Draw(b, cx, cy, s, t);
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
    }

    private static void Emit(VertexBatch b, float cx, float cy, float a, float g, float t, float r, float gr, float bl, float al)
    {
        b.Rect(cx - a, cy - t, cx - g, cy + t, r, gr, bl, al);
        b.Rect(cx + g, cy - t, cx + a, cy + t, r, gr, bl, al);
        b.Rect(cx - t, cy - a, cx + t, cy - g, r, gr, bl, al);
        b.Rect(cx - t, cy + g, cx + t, cy + a, r, gr, bl, al);
    }

    /// <summary>Four corner ticks (two segments each) on the square of half-size <paramref name="k"/>: the
    /// "aim assist has a target" tell.</summary>
    private static void Corners(VertexBatch b, float cx, float cy, float k, float len, float t,
                                float r, float gr, float bl, float al)
    {
        for (int i = 0; i < 4; i++)
        {
            float sx = (i & 1) == 0 ? -1f : 1f, sy = (i & 2) == 0 ? -1f : 1f;
            float x = cx + sx * k, y = cy + sy * k;
            float x2 = x - sx * len, y2 = y - sy * len;
            b.Rect(MathF.Min(x, x2), y - t, MathF.Max(x, x2), y + t, r, gr, bl, al);
            b.Rect(x - t, MathF.Min(y, y2), x + t, MathF.Max(y, y2), r, gr, bl, al);
        }
    }
}
