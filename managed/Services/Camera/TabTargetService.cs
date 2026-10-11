using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Camera;

/// <summary>
/// Tab-target mode: one <b>locked target</b>, picked by the cycle key or a left-click, that every target-asking
/// quickbar button fires at with no second click. The decisions are <see cref="TabTargetCore"/>'s; this shell does
/// the engine work at six seams -- keys, candidates, indicator, camera, fire and click-to-lock, each documented
/// where it sits. Nothing allocates, and the lock is one id behind <see cref="Volatile"/>.
/// </summary>
public sealed unsafe class TabTargetService : IExtenderService
{
    private static ExtenderLog? _log;
    private static bool _ready;
    private static WinMessageHandlerFn* _next;
    private static PerformDefaultActionOnObjectFn _defaultActionOrig;
    /// <summary>Chain slot on the GUI hit-test, above the CX quickbar's proxy: a fire at the lock sees no GUI at all.</summary>
    private static GuiObjectHitCheckFn* _guiHit;
    private static IntPtr _colEnemy, _colFriend, _colNeutral, _colPlaceable, _colDoor;

    /// <summary>Between the mouse-look layer (100) and the framework input layer (0).</summary>
    private const int ChainPriority = 50;
    private const uint SdlKeyDown = 0x300, SdlKeyUp = 0x301;  // not-an-offset: SDL key/event code
    /// <summary>SDL_KeyboardEvent: <c>repeat</c>, <c>keysym.scancode</c>, <c>keysym.sym</c>.</summary>
    private const int EvRepeatOff = 0xd, EvScancodeOff = 0x10, EvSymOff = 0x14;  // not-an-offset: SDL_Event ABI, not an engine class
    private const int KmodShift = 0x0003;  // not-an-offset: SDL key/event code
    /// <summary>SDL scancode range of the modifier keys, never a "release fires" key.</summary>
    private const int ScancodeLCtrl = 0xE0, ScancodeRGui = 0xE7;  // not-an-offset: SDL key/event code
    /// <summary>The lock's hilite channel: above hover/Tab (2) and VFX (3), so those show through.</summary>
    private const byte LockChannel = 4;
    private const int Glow = 1;
    /// <summary>Attitude byte: 1 friend, 0 neutral, else hostile.</summary>
    private const byte AttitudeNeutral = 0, AttitudeFriend = 1;
    /// <summary>The world pick's "an object" verdict (PerformLButtonDownAction's <c>== 2</c> branch).</summary>
    private const int PickObject = 2;

    // ---- the lock ----
    private static uint _lock;
    /// <summary>The locked object id, 0 for none. Written on the input thread, read by the tick.</summary>
    internal static uint Lock => Volatile.Read(ref _lock);
    /// <summary>The area the lock was taken in: a different current area clears it.</summary>
    private static IntPtr _lockArea;
    private static readonly uint[] _ring = new uint[TabTargetCore.MaxCandidates];
    private static int _ringCount;
    private static readonly TabTargetCore.Candidate[] _live = new TabTargetCore.Candidate[TabTargetCore.MaxCandidates];

    // ---- the fire ----
    /// <summary>The CNWCObject* the pick hooks answer while a lock fire is in flight; Zero otherwise.</summary>
    private static IntPtr _fireAt;
    /// <summary>A Preview mode waiting for its release: the CActionTarget and the scancode whose key-up fires it
    /// (0 = none pending on a key).</summary>
    private static IntPtr _pending;
    private static int _pendingKey;
    /// <summary>The last non-modifier scancode pressed: a hotkey opens its mode a frame after its key-down, so the
    /// most recent press still held at activation is the button, not a movement key held since before.</summary>
    private static int _lastKeyDown;
    private static int _cycleCode, _clearCode, _lockCode;

    private enum Aim { None, Once, Locked }
    /// <summary>What the camera does about the lock right now (<c>tabtarget.camera</c>, then the lock key).</summary>
    private static Aim _aim;
    private const int CameraTurn = 1, CameraLock = 2;

    private static int _cycles, _clicks, _clears, _drops, _lockFires, _cursorFires, _releaseFires;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraKnobs.Declare();
        TabTargetCore.SelfCheck(log);
        TabTarget.G.Status = Report;
        if (EngineFn.GetGameObject == null) return;
        if (!EngineLayout.Require(log, "TabTarget", "CNWCObject", "CNWCArea", "CNWCCreature", "CGameObject", "CClientExoAppInternal",
                                  "CActionTarget", "Camera", "CNWCModule", "CClientExoApp", "CGuiMan")) return;
        if (!SymbolMap.Require(log, "TabTarget", SymbolMap.WinMessageHandler, SymbolMap.PerformDefaultActionOnObject,
                               SymbolMap.GuiObjectHitCheck, SymbolMap.SdlGetKeyboardState, SymbolMap.SdlGetKeyFromName,
                               SymbolMap.SdlGetModState, SymbolMap.GetGameObject, SymbolMap.GetModule, SymbolMap.AppManager,
                               SymbolMap.HiliteEnemy, SymbolMap.HiliteFriend, SymbolMap.HiliteNeutral,
                               SymbolMap.HilitePlaceable, SymbolMap.HiliteDoor)) return;
        if (!AimAssist.Ready || !MouseLookService.PicksHooked)
        { log.Info("TabTarget: standing down (the action camera's target-mode or pick hooks are not up)"); return; }

        _colEnemy = Native.Sym(SymbolMap.HiliteEnemy); _colFriend = Native.Sym(SymbolMap.HiliteFriend);
        _colNeutral = Native.Sym(SymbolMap.HiliteNeutral); _colPlaceable = Native.Sym(SymbolMap.HilitePlaceable);
        _colDoor = Native.Sym(SymbolMap.HiliteDoor);
        _defaultActionOrig = hooks.InstallPerformDefaultActionOnObject(&OnDefaultAction);
        _guiHit = hooks.ChainGuiObjectHitCheck(&OnGuiHitCheck, ChainPriority);
        _next = hooks.ChainWinMessageHandler(&OnWinMessage, ChainPriority);
        if (_defaultActionOrig == null || _guiHit == null || _next == null)
        { log.Info("TabTarget: standing down (a hook install was refused)"); return; }
        KeysChanged();
        FrameRegistry.Global.Register(FramePhase.PostPresent, 61, Tick, "TabTarget", log);
        _ready = true;
        log.Info($"TabTarget: ready ({(TabTarget.Enabled.V ? "on" : "off")}); hooks PerformDefaultActionOnObject + GuiObjectHitCheck, chained on WinMessageHandler");
    }

    private static bool On => _ready && TabTarget.Enabled.V && GameGate.InGame;

    // ---- keys ----

    /// <summary>The key seam: the cycle, clear and lock keys are swallowed on key-down, before
    /// <c>CExoInput::AddEvent</c> queues the scancode keybind, so the stock Tab highlight-all never runs. Only
    /// while the framework's chord gate is clear -- no chat typing, no CX panel, no NUI edit, no dialog.</summary>
    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (ev == IntPtr.Zero || !On) return Forward(ev);
        uint type = *(uint*)ev;
        if (type == SdlKeyUp)
        {
            if (_pending != IntPtr.Zero && _pendingKey != 0 && *(int*)(ev + EvScancodeOff) == _pendingKey) ResolvePending();
            return Forward(ev);
        }
        if (type != SdlKeyDown || *(byte*)(ev + EvRepeatOff) != 0) return Forward(ev);
        int scancode = *(int*)(ev + EvScancodeOff);
        if (scancode is < ScancodeLCtrl or > ScancodeRGui) _lastKeyDown = scancode;
        if (!UiInputService.ChordGateClear()) return Forward(ev);
        int sym = *(int*)(ev + EvSymOff);
        if (_cycleCode != 0 && sym == _cycleCode)
        {
            Cycle((EngineFn.SdlGetModState() & KmodShift) != 0);
            return 0;
        }
        if (_clearCode != 0 && sym == _clearCode && Lock != 0)
        {
            Clear(); _clears++;
            return 0;
        }
        if (_lockCode != 0 && sym == _lockCode)
        {
            LockOn();
            return 0;
        }
        return Forward(ev);
    }

    /// <summary>The cycle key: the next candidate after the lock in ring order (the centre-most with no lock).</summary>
    private static void Cycle(bool reverse)
    {
        uint next = Pick(Lock, reverse, out IntPtr area);
        _cycles++;
        if (next != 0 && next != Lock) SetLock(next, area);
    }

    /// <summary>The lock key: locked on, it lets go (the target stays); otherwise it locks the camera on the
    /// creature nearest the middle of the screen -- whatever is highlighted -- or on the lock when nothing is in view.</summary>
    private static void LockOn()
    {
        if (_aim == Aim.Locked) { _aim = Aim.None; return; }
        uint next = Pick(0, false, out IntPtr area);
        if (next != 0 && next != Lock) SetLock(next, area);
        if (Lock != 0) _aim = Aim.Locked;
    }

    /// <summary>Walk the area once, project, hand the in-view candidates to the Core: the id it lands on after
    /// <paramref name="held"/> (0 = the centre-most, and the ring is rebuilt), or <paramref name="held"/> itself.</summary>
    private static uint Pick(uint held, bool reverse, out IntPtr area)
    {
        area = IntPtr.Zero;
        IntPtr module = CameraService.Module();
        IntPtr app = CameraService.App();
        if (module == IntPtr.Zero || app == IntPtr.Zero) return held;
        var mod = new CNWCModule(module);
        CX.Engine.Camera cam = mod.Camera;
        area = mod.CurrentArea.Ptr;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        IntPtr player = CameraService.PlayerCreature();
        if (cam.IsNull || area == IntPtr.Zero || intern.IsNull || player == IntPtr.Zero) return held;
        MouseLookService.ViewportCentre(out int cx, out int cy);
        if (!Projector.Read(cam, cx, cy, out Projector view)) return held;
        var areaView = new CNWCArea(area);
        int n = areaView.ObjectCount;
        uint* ids = (uint*)areaView.ObjectIds;
        if (n <= 0 || ids == null) return held;
        uint playerId = intern.PlayerCreatureId;
        Vector3 me = new CNWCObject(player).Position;

        int count = 0;
        for (int i = 0; i < n && count < _live.Length; i++)
        {
            uint id = ids[i];
            if (id == playerId || id == TypingSignal.ObjectInvalid) continue;
            CNWCObject obj = Object(app, id, out byte kind);
            if (obj.IsNull || (kind == TabTargetCore.TypeCreature && Dead(obj))) continue;
            Vector3 pos = obj.Position;
            if (!view.Screen(pos, out float sx, out float sy, out _)) continue;
            if (sx < 0f || sy < 0f || sx > 2 * cx || sy > 2 * cy) continue;   // in view only
            float dx = sx - cx, dy = sy - cy;
            _live[count++] = new TabTargetCore.Candidate
            {
                Id = id, ScreenDist = MathF.Sqrt(dx * dx + dy * dy), Range = Vector3.Distance(pos, me),
                Hostile = kind == TabTargetCore.TypeCreature && Hostile(obj), Type = kind,
            };
        }
        return TabTargetCore.Cycle(new ReadOnlySpan<TabTargetCore.Candidate>(_live, 0, count), TabTarget.Filter.V == 0,
                                   TabTarget.Objects.V, TabTarget.Range.V, held, reverse, _ring, ref _ringCount);
    }

    /// <summary>The creature / placeable / door behind <paramref name="id"/>, or Zero: the lock's liveness read
    /// (a faded or invisible target stays locked; only a gone one clears). A placeable the server retired counts
    /// as gone -- vanilla deletes it, Alloy's <c>PlaceableHold</c> keeps it on screen for good; a petrified
    /// creature reads as dead (AI-state word 0) and drops the lock.</summary>
    private static CNWCObject Resolve(IntPtr app, uint id, out byte kind)
    {
        kind = 0;
        CGameObject go = new CClientExoApp(app).GetGameObject(id);
        if (go.IsNull) return default;
        CNWCObject obj = go.AsNWCObject();
        if (obj.IsNull) return default;
        kind = obj.Type;
        if (kind == TabTargetCore.TypePlaceable && obj.Retired == 1) return default;
        return kind is TabTargetCore.TypeCreature or TabTargetCore.TypePlaceable or TabTargetCore.TypeDoor ? obj : default;
    }

    /// <summary><see cref="Resolve"/> narrowed to what Tab may land on: visible and highlightable, the same reads
    /// as the aim assist's walk (the placeable's hilite flag sits on what <c>AsNWCPlaceable</c> answers).</summary>
    private static CNWCObject Object(IntPtr app, uint id, out byte kind)
    {
        CNWCObject obj = Resolve(app, id, out kind);
        if (obj.IsNull || obj.Visible == 0) return default;
        CNWCObject hil = kind != TabTargetCore.TypePlaceable ? obj
            : new CGameObject(obj.Ptr).AsNWCPlaceable();
        return hil.IsNull || hil.Highlightable == 0 ? default : obj;
    }

    private static bool Hostile(CNWCObject creature) => new CNWCCreature(creature.Ptr).Attitude is not (AttitudeNeutral or AttitudeFriend);
    private static bool Dead(CNWCObject creature) => new CNWCCreature(creature.Ptr).AiFlags == 0;

    private static void SetLock(uint id, IntPtr area)
    {
        if (Lock != 0 && Lock != id) UnHilite();
        Volatile.Write(ref _lock, id);
        _lockArea = area;
        if (id == 0) { _ringCount = 0; _aim = Aim.None; return; }
        _aim = TabTarget.Camera.V switch { CameraTurn => Aim.Once, CameraLock => Aim.Locked, _ => Aim.None };
    }

    // ---- the camera ----

    /// <summary>The lock's ground position while the camera should look at it (turn once, or locked on). Read by
    /// <see cref="CameraService"/>'s recenter every frame.</summary>
    internal static bool AimPoint(out float x, out float y, out float z)
    {
        x = y = z = 0f;
        if (_aim == Aim.None || !On) return false;
        IntPtr app = CameraService.App();
        CNWCObject obj = app != IntPtr.Zero ? Resolve(app, Lock, out _) : default;
        if (obj.IsNull) return false;
        Vector3 w = obj.Position;
        x = w.X; y = w.Y; z = w.Z;
        return true;
    }

    /// <summary>The camera is locked on a target: <see cref="CameraDriveService"/> runs the turn and strafe keys
    /// sideways (the creature faces where it runs) instead of orbiting or strafing, the camera being the lock's.</summary>
    internal static bool LockedOn => _aim == Aim.Locked && On && Lock != 0;

    /// <summary>The camera reached the target: a one-shot turn is done.</summary>
    internal static void AimSettled() { if (_aim == Aim.Once) _aim = Aim.None; }

    private static void Clear() => SetLock(0, IntPtr.Zero);

    // ---- the frame tick: validity + indicator ----

    private static void Tick()
    {
        uint id = Lock;
        if (id == 0) return;
        IntPtr module = CameraService.Module();
        IntPtr app = CameraService.App();
        if (!On || module == IntPtr.Zero || app == IntPtr.Zero || new CNWCModule(module).CurrentArea.Ptr != _lockArea) { Clear(); return; }
        CNWCObject obj = Resolve(app, id, out byte kind);
        if (obj.IsNull || (kind == TabTargetCore.TypeCreature && Dead(obj))) { Clear(); return; }
        if (!InView(module, obj)) { Clear(); _drops++; return; }
        Hilite(obj, kind);
        // The Tab plate: MouseOver(1) is a no-op while the plate is already up, so per frame is free.
        obj.MouseOver(1);
    }

    /// <summary>Is the object's ground point inside the viewport? A target that left the screen (the player turned
    /// away, or walked past it) drops its lock; a one-shot turn keeps it in view by construction, a lock-on by force.</summary>
    private static bool InView(IntPtr module, CNWCObject obj)
    {
        CX.Engine.Camera cam = new CNWCModule(module).Camera;
        MouseLookService.ViewportCentre(out int cx, out int cy);
        if (cam.IsNull || !Projector.Read(cam, cx, cy, out Projector view)) return true;   // no camera to judge by: keep
        return view.Screen(obj.Position, out float sx, out float sy, out _)
               && sx >= 0f && sy >= 0f && sx <= 2 * cx && sy <= 2 * cy;
    }

    /// <summary>The engine's own Tab look: the object's script colour when set (x &gt;= 0), else the type's /
    /// attitude's global. Virtual: CNWCCreature overrides Hilite to reach its equipped items too.</summary>
    private static void Hilite(CNWCObject obj, byte kind)
    {
        Span<float> c = obj.HiliteColour;
        if (!(c[0] >= 0f))
        {
            IntPtr g = kind == TabTargetCore.TypePlaceable ? _colPlaceable : kind == TabTargetCore.TypeDoor ? _colDoor
                : new CNWCCreature(obj.Ptr).Attitude switch { AttitudeFriend => _colFriend, AttitudeNeutral => _colNeutral, _ => _colEnemy };
            c = new Span<float>((void*)g, 3);
        }
        Vec3 colour = new() { X = c[0], Y = c[1], Z = c[2] };
        obj.Hilite(VectorArg.Of(&colour), LockChannel, 0f, Glow);
    }

    private static void UnHilite()
    {
        uint id = Lock;
        IntPtr app = CameraService.App();
        if (id == 0 || app == IntPtr.Zero) return;
        CGameObject go = new CClientExoApp(app).GetGameObject(id);
        if (go.IsNull) return;
        CNWCObject obj = go.AsNWCObject();
        if (obj.IsNull) return;
        obj.UnHilite(LockChannel);
        obj.MouseOff();
    }

    // ---- click-to-lock ----

    [UnmanagedCallersOnly]
    private static int OnDefaultAction(IntPtr self, IntPtr obj)
    {
        if (!On || obj == IntPtr.Zero || _fireAt != IntPtr.Zero) return _defaultActionOrig(self, obj);
        IntPtr app = CameraService.App();
        CClientExoAppInternal intern = app != IntPtr.Zero ? new CClientExoApp(app).Internal : default;
        var target = new CNWCObject(obj);
        uint id = target.Id;
        if (intern.IsNull || id == intern.PlayerCreatureId) return _defaultActionOrig(self, obj);
        byte kind = target.Type;
        // A corpse is loot, not a target: its click keeps the vanilla UseObject.
        bool lockable = (kind == TabTargetCore.TypeCreature && !Dead(target))
                        || (TabTarget.Objects.V && kind is TabTargetCore.TypePlaceable or TabTargetCore.TypeDoor);
        if (!lockable) return _defaultActionOrig(self, obj);
        IntPtr module = CameraService.Module();
        SetLock(id, module != IntPtr.Zero ? new CNWCModule(module).CurrentArea.Ptr : IntPtr.Zero);
        _clicks++;
        // The crosshair wins while the action camera is held: the click keeps its vanilla action as well.
        return MouseLookService.ActionHeld ? _defaultActionOrig(self, obj) : 1;
    }

    // ---- the fire ----

    /// <summary>From the <c>ActivateTargetMode</c> post-hook: true when tab-target took the decision (the
    /// legacy auto-target must then stay out). With the action camera held the crosshair is the aim point, so
    /// the lock is presented as absent and the cursor pick (pinned to the crosshair) resolves it.</summary>
    internal static bool OnActivate(IntPtr self)
    {
        if (!On) return false;
        _pending = IntPtr.Zero;
        var mode = (TabTargetCore.CastMode)TabTarget.Cast.V;
        bool hasLock = Lock != 0 && !MouseLookService.ActionHeld;
        bool takesObject = AimAssist.TakesAnObject(self);
        switch (TabTargetCore.Decide(mode, hasLock, takesObject, TabTargetCore.CastEvent.Open))
        {
            case TabTargetCore.CastVerdict.FireAtLock: FireAtLock(self); break;
            case TabTargetCore.CastVerdict.FireAtCursor: _cursorFires++; AimAssist.Resolve(self); break;
            case TabTargetCore.CastVerdict.LeaveUp when mode == TabTargetCore.CastMode.Preview: Arm(self); break;
        }
        return true;
    }

    /// <summary>Preview: remember the mode and the key that opened it -- the last key pressed, if it is still
    /// down. Not down (a mouse click on the button, or a tap already released) = the release already happened,
    /// so fire now.</summary>
    private static void Arm(IntPtr self)
    {
        int n = 0;
        byte* state = EngineFn.SdlGetKeyboardState(&n);
        int key = _lastKeyDown;
        _pendingKey = key > 0 && key < n && state[key] != 0 ? key : 0;
        _pending = self;
        if (_pendingKey == 0) ResolvePending();
    }

    /// <summary>The release: only if the engine still has this mode registered (Escape / right-click cancelled
    /// it otherwise, and the pointer is dead).</summary>
    private static void ResolvePending()
    {
        IntPtr self = _pending;
        _pending = IntPtr.Zero;
        IntPtr app = CameraService.App();
        if (self == IntPtr.Zero || app == IntPtr.Zero) return;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        if (intern.IsNull || intern.ActionTarget.Ptr != self) return;
        bool hasLock = Lock != 0 && !MouseLookService.ActionHeld;
        _releaseFires++;
        switch (TabTargetCore.Decide(TabTargetCore.CastMode.Preview, hasLock, AimAssist.TakesAnObject(self), TabTargetCore.CastEvent.Release))
        {
            case TabTargetCore.CastVerdict.FireAtLock: FireAtLock(self); break;
            case TabTargetCore.CastVerdict.FireAtCursor: _cursorFires++; AimAssist.Resolve(self); break;
        }
    }

    /// <summary>Substitute the pick for the duration of one <c>HandleLMouseDown</c>. Nothing touches
    /// <paramref name="self"/> after the call.</summary>
    private static void FireAtLock(IntPtr self)
    {
        IntPtr app = CameraService.App();
        CNWCObject obj = app != IntPtr.Zero ? Resolve(app, Lock, out _) : default;
        if (obj.IsNull) { _cursorFires++; AimAssist.Resolve(self); return; }
        _fireAt = obj.Ptr; _lockFires++;
        try { AimAssist.Resolve(self); }
        finally { _fireAt = IntPtr.Zero; }
    }

    /// <summary>The pick hooks' first question: is a lock fire in flight? Then the pick IS the lock -- the
    /// object, its position, verdict "object". The <c>int*</c> flag is left alone: HandleLMouseDown passes null
    /// and PerformLButtonDownAction reads it on the ground verdict only.</summary>
    internal static bool OverridePick(IntPtr* obj, IntPtr point, out int verdict)
    {
        IntPtr at = _fireAt;
        verdict = 0;
        if (at == IntPtr.Zero) return false;
        if (obj != null) *obj = at;
        if (point != IntPtr.Zero) { Vector3 w = new CNWCObject(at).Position; float* p = (float*)point; p[0] = w.X; p[1] = w.Y; p[2] = w.Z; }
        verdict = PickObject;
        return true;
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnGuiHitCheck(IntPtr self, int x, int y, int fresh, int mask)
        => _fireAt != IntPtr.Zero ? IntPtr.Zero : (*_guiHit)(self, x, y, fresh, mask);

    // ---- knobs ----

    /// <summary>Re-parse the two key names through SDL's own table (cold).</summary>
    internal static void KeysChanged()
    {
        _cycleCode = Code(TabTarget.CycleKey.V);
        _clearCode = Code(TabTarget.ClearKey.V);
        _lockCode = Code(TabTarget.LockKey.V);
    }

    private static int Code(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || EngineFn.SdlGetKeyFromName == null) return 0;
        // SDL_GetKeyFromName answers a one-character name with that character, but a key event's sym is the
        // LOWER-case letter: "F" would never match the F key.
        if (name.Length == 1) name = name.ToLowerInvariant();
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name + "\0");
        int code;
        fixed (byte* p = utf8) code = EngineFn.SdlGetKeyFromName(p);
        if (code == 0) _log?.Info($"TabTarget: '{name}' is not an SDL key name; nothing bound");
        return code;
    }

    /// <summary>The master's off edge: the lock and any pending preview go at once.</summary>
    internal static void MasterChanged()
    {
        if (TabTarget.Enabled.V || !_ready) return;
        _pending = IntPtr.Zero;
        Clear();
    }

    private static int Forward(IntPtr ev) => (*_next)(ev);

    private static string Report() => !_ready ? "stood down"
        : FormattableString.Invariant($"{(TabTarget.Enabled.V ? "on" : "off")}, cast {TabTarget.Cast.Choices[Math.Max(0, TabTarget.Cast.ChoiceIndex)]}; lock {(Lock == 0 ? "none" : $"0x{Lock:x}")}, camera {_aim}, pending {(_pending == IntPtr.Zero ? "no" : "yes")}; {_cycles} cycle(s), {_clicks} click-lock(s), {_clears} clear(s), {_drops} dropped off-screen; fired {_lockFires} at the lock, {_cursorFires} at the cursor, {_releaseFires} on release");
}
