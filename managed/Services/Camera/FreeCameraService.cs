using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat;
using CX.Ui;

namespace CX.Services.Camera;

/// <summary>
/// The free camera: <c>free.enter</c> detaches the camera for screenshots, the engine's own drive keys fly it and
/// Escape returns. The pose goes where <see cref="CameraService"/> writes it, after Control, so picking, nameplates
/// and combat text follow, and the behaviour's yaw/pitch/dist stay untouched so the exit lands on the engine's
/// previous pose; both transitions are cuts. Leashed to the player; every CameraService gate or combat exits it.
/// </summary>
public sealed unsafe class FreeCameraService : IExtenderService
{
    private static ExtenderLog? _log;
    private static WinMessageHandlerFn* _next;

    private const uint SdlWindowEvent = 0x200, SdlKeyDown = 0x300, SdlKeyUp = 0x301, SdlMouseMotion = 0x400, SdlMouseDown = 0x401, SdlMouseUp = 0x402, SdlMouseWheel = 0x403;  // not-an-offset: SDL key/event code
    /// <summary>SDL_KeyboardEvent: repeat byte at +0xd, keysym.scancode at +0x10; button/motion rows as MouseLook's; wheel y at +0x14.</summary>
    private const byte SdlWindowFocusLost = 0xd, SdlButtonLeft = 1, SdlButtonMiddle = 2, SdlButtonRight = 3;  // not-an-offset: SDL key/event code
    /// <summary>SDL scancodes (layout-independent): Escape, Space, C, Z, X, left/right Shift.</summary>
    private const int ScEscape = 41, ScSpace = 44, ScC = 6, ScZ = 29, ScX = 27, ScLShift = 225, ScRShift = 229;
    private const int Fwd = 0, Back = 1, TurnL = 2, TurnR = 3, StrafeL = 4, StrafeR = 5;
    private const float Deg = MathF.PI / 180f, PitchFloor = 1f, PitchCeil = 179f, FastMul = 4f, RollRate = 60f;

    /// <summary>Why the free camera left (or refused), for the frame-end notice.</summary>
    private enum Why { None, User, Gate, Combat, NoCreature, Leash }
    private static readonly string[] WhyNames = { "", "", "camera held down", "in combat", "no player creature", "at the leash" };

    /// <summary>Set by the button, cleared by <see cref="Leave"/>; every consumer reads it hot.</summary>
    public static bool Active { get; private set; }
    private static bool _armed, _weHidUi, _looking, _leashed, _up, _down, _rollL, _rollR, _fast;
    private static byte _ate;
    private static Vector3 _pos;
    private static float _yaw, _pitch, _roll, _fov, _leashR;
    /// <summary>The view angle on entry, put back by <see cref="Leave"/> (the layer only rewrites it while it drives the mode).</summary>
    private static float _fovEntry;
    private static int _originX, _originY, _mx, _my;
    private static Why _pending;
    private static CameraService.Gate _exitGate;
    private static int _entries, _leashHits;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        CameraKnobs.Declare();
        FreeCam.G.Status = Report;
        SelfCheck(log);
        if (!EngineLayout.Require(log, "FreeCamera", "CNWCCreature", "CNWCObject", "CNWCModule", "CClientExoAppInternal", "CGuiInGame", "Camera")) return;
        if (!SymbolMap.Require(log, "FreeCamera", SymbolMap.WinMessageHandler, SymbolMap.GuiInGameToggleOff, SymbolMap.GuiInGameUnToggleOff,
                               SymbolMap.SdlSetRelativeMouseMode, SymbolMap.SdlWarpMouseInWindow, SymbolMap.GetModule, SymbolMap.AppManager, SymbolMap.GetGameObject)) return;
        _next = hooks.ChainWinMessageHandler(&OnWinMessage, 110);
        if (_next == null) { log.Info("FreeCamera: standing down (WinMessageHandler chain refused)"); return; }
        _armed = true;
        FrameRegistry.Global.Register(FramePhase.PostPresent, 51, OnFrameEnd, "FreeCamera", log);
        log.Info("FreeCamera: free.enter armed (outermost WinMessageHandler layer)");
    }

    // ---- the hot half: once per Control while active ------------------------------------------

    /// <summary>Move, turn, leash and write the pose; leave on any gate or combat. <paramref name="g"/> is this
    /// Control's verdict: a GUI panel's camera is skipped, the mode's master off is no gate here (the free camera
    /// has no master of its own), every other non-Active verdict ends the free camera.</summary>
    internal static void Frame(in CameraService.CamSink cam, CameraService.Gate g, float dt)
    {
        if (g == CameraService.Gate.NotWorldCamera) return;
        if (g is not (CameraService.Gate.Active or CameraService.Gate.Off)) { _exitGate = g; Leave(Why.Gate); return; }
        IntPtr cre = CameraService.PlayerCreature();
        if (cre == IntPtr.Zero) { Leave(Why.NoCreature); return; }
        if (new CNWCCreature(cre).CombatState != 0) { Leave(Why.Combat); return; }
        var mod = new CNWCModule(CameraService.Module());
        mod.ChaseSuppressed = 1;
        mod.ChaseWasIdle = 0;

        Span<int> keys = new CClientExoApp(CameraService.App()).Internal.DriveKeys;
        float f = (keys[Fwd] != 0 ? 1f : 0f) - (keys[Back] != 0 ? 1f : 0f);
        float s = (keys[StrafeR] != 0 ? 1f : 0f) - (keys[StrafeL] != 0 ? 1f : 0f);
        float t = (keys[TurnL] != 0 ? 1f : 0f) - (keys[TurnR] != 0 ? 1f : 0f);
        float v = (_up ? 1f : 0f) - (_down ? 1f : 0f);
        float r = (_rollR ? 1f : 0f) - (_rollL ? 1f : 0f);
        _yaw = Wrap(_yaw + t * FreeCam.Turn.V * dt);
        _roll = Wrap(_roll + r * RollRate * dt);
        Quaternion flat = CameraService.Pose(_yaw, _pitch);
        // Forward(q) points from the focus TO the camera; the view looks the other way. Up is the world's.
        Vector3 want = _pos + (-CameraService.Forward(flat) * f + CameraService.Right(flat) * s + Vector3.UnitZ * v) * (FreeCam.Speed.V * (_fast ? FastMul : 1f) * dt);
        float serverMax = mod.CameraMaxDist;
        _leashR = serverMax >= 0f ? serverMax : FreeCam.Leash.V;
        Vector3 anchor = new CNWCObject(cre).Position;
        Vector3 d = want - anchor;
        float len = d.Length();
        if (len > _leashR)
        {
            want = anchor + d * (_leashR / len);
            if (!_leashed) { _leashed = true; _leashHits++; if (_pending == Why.None) _pending = Why.Leash; }
        }
        else _leashed = false;
        _pos = want;

        cam.Place(_pos, PoseRoll(_yaw, _pitch, _roll));
        cam.SetViewAngle(_fov);
    }

    /// <summary>The pose rolled about its own view axis: <c>qZ(yaw) * qX(pitch) * qZ(roll)</c>, Hamilton (System.Numerics'
    /// operator* is the Hamilton product, the expansion <see cref="CameraService.Pose"/> writes out). The camera's local
    /// Z is the focus-to-camera line, so a local-Z turn is a true roll; the engine's own <c>YawPitchRoll</c> third
    /// term is about local Y and would be a head-turn, not a roll.</summary>
    private static Quaternion PoseRoll(float yawDeg, float pitchDeg, float rollDeg)
    {
        if (rollDeg == 0f) return CameraService.Pose(yawDeg, pitchDeg);
        (float sr, float cr) = MathF.SinCos(rollDeg * Deg * 0.5f);
        return CameraService.Pose(yawDeg, pitchDeg) * new Quaternion(0f, 0f, sr, cr);
    }

    private static float Wrap(float deg) => deg - 360f * MathF.Round(deg / 360f);

    /// <summary>End the free camera: GUI back if this session hid it, the entry view angle back (the mode's: the
    /// layer's last write, or the engine's own when the mode's master is off), cursor released, the notice queued.
    /// Safe hot (engine and SDL calls only); the pose snaps back through CameraService's weld on the next Control.</summary>
    private static void Leave(Why why)
    {
        if (!Active) return;
        Active = false;
        StopLook();
        IntPtr module = CameraService.Module();
        IntPtr camPtr = module == IntPtr.Zero ? IntPtr.Zero : new CNWCModule(module).Camera.Ptr;
        // The sink, not the engine view: in DB mode Camera+0x4c4 is republished from the record.
        if (camPtr != IntPtr.Zero)
        {
            var cam = CameraService.CamSink.Of(camPtr);
            if (cam.Ok) cam.SetViewAngle(_fovEntry);
        }
        _up = _down = _rollL = _rollR = false; _ate = 0;
        if (_weHidUi) { IntPtr gui = Gui(); if (gui != IntPtr.Zero) new CGuiInGame(gui).GuiInGameUnToggleOff(); _weHidUi = false; }
        if (why != Why.User) _pending = why;
        AlloyRenderer.CameraCut();
    }

    private static IntPtr Gui()
    {
        IntPtr app = CameraService.App();
        return app == IntPtr.Zero ? IntPtr.Zero : new CClientExoApp(app).Internal.InGameGui.Ptr;
    }

    // ---- the cold half: input -----------------------------------------------------------------

    [UnmanagedCallersOnly]
    private static int OnWinMessage(IntPtr ev)
    {
        if (!Active || ev == IntPtr.Zero) return Forward(ev);
        switch (*(uint*)ev)
        {
            case SdlKeyDown:
            case SdlKeyUp:
                if (ChatService.WantsKey) break;
                bool down = *(uint*)ev == SdlKeyDown;
                switch (((SdlEvent*)ev)->Scancode)
                {
                    case ScEscape: if (down && ((SdlEvent*)ev)->Repeat == 0) Leave(Why.User); return 0;
                    case ScSpace: _up = down; return 0;
                    case ScC: _down = down; return 0;
                    case ScZ: _rollL = down; return 0;
                    case ScX: _rollR = down; return 0;
                    case ScLShift: case ScRShift: _fast = down; break;   // observed, not eaten
                }
                break;
            case SdlMouseDown:
            {
                byte b = ((SdlEvent*)ev)->Button;
                int mx = ((SdlEvent*)ev)->X, my = ((SdlEvent*)ev)->Y;
                if (b is not (SdlButtonLeft or SdlButtonMiddle or SdlButtonRight) || _looking) break;
                if (UiInputService.EngineGuiWantsPress() || UiHost.HitTest(mx, my) != null) break;
                _ate |= (byte)(1 << b);
                if (b == SdlButtonRight) { _looking = true; _originX = mx; _originY = my; EngineFn.SdlSetRelativeMouseMode(1); }
                return 0;   // left: no click-to-move; middle: the engine's orbit would move the behavior, not us
            }
            case SdlMouseUp:
            {
                byte b = ((SdlEvent*)ev)->Button;
                if ((_ate & (1 << b)) == 0) break;
                _ate &= (byte)~(1 << b);
                if (b == SdlButtonRight) StopLook();
                return 0;
            }
            case SdlMouseMotion:
                if (!_looking) { _mx = ((SdlEvent*)ev)->X; _my = ((SdlEvent*)ev)->Y; break; }
                {
                    float kx = MouseLook.Sens.V * FreeCam.Sens.V / MouseLookService.ViewportWidth();
                    _yaw = Wrap(_yaw + ((SdlEvent*)ev)->XRel * MouseLookService.TurnPerPx * kx);
                    _pitch = Math.Clamp(_pitch + ((SdlEvent*)ev)->YRel * MouseLookService.TiltPerPx * kx * (MouseLook.Invert.V ? -1f : 1f), PitchFloor, PitchCeil);
                }
                return 0;
            case SdlMouseWheel:
                if (UiHost.HitTest(_mx, _my) != null) break;
                FreeCam.Speed.Set(FreeCam.Speed.V * (((SdlEvent*)ev)->WheelY > 0 ? 1.25f : 0.8f));
                return 0;
            case SdlWindowEvent:
                if (((SdlEvent*)ev)->WindowKind == SdlWindowFocusLost) { StopLook(); _up = _down = _rollL = _rollR = _fast = false; }
                break;
        }
        return Forward(ev);
    }

    private static void StopLook()
    {
        if (!_looking) return;
        _looking = false;
        EngineFn.SdlSetRelativeMouseMode(0);
        EngineFn.SdlWarpMouseInWindow(IntPtr.Zero, _originX, _originY);
    }

    private static int Forward(IntPtr ev) => (*_next)(ev);

    private static void OnFrameEnd()
    {
        if (_pending == Why.None) return;
        Why w = _pending; _pending = Why.None;
        string why = w == Why.Gate ? CameraService.GateName : WhyNames[(int)w];
        Notice.Show(_log, w == Why.Leash
            ? FormattableString.Invariant($"[camera] free camera at the leash: {_leashR:0.#} m from your character ({(new CNWCModule(CameraService.Module()).CameraMaxDist >= 0f ? "the server's camera limit" : "free.leash")})")
            : $"[camera] free camera off: {why}");
    }

    // ---- the button and the two live knobs (cold) ----

    /// <summary><c>free.fov</c> moved while free: the new angle now, or the entry angle back for 0.</summary>
    internal static void FovChanged() { if (Active) _fov = FreeCam.Fov.V > 0f ? FreeCam.Fov.V : _fovEntry; }
    /// <summary><c>free.roll</c> moved while free: applied now.</summary>
    internal static void RollChanged() { if (Active) _roll = FreeCam.Roll.V; }

    /// <summary>The <c>free.enter</c> press: enter, or leave when already free. The verdict goes to the player as a
    /// <see cref="Notice"/>; a refusal names its reason.</summary>
    internal static void Toggle()
    {
        if (Active) { Leave(Why.User); Notice.Show(_log, "[camera] free camera off"); return; }
        string? refused = Refusal();
        if (refused != null) { Notice.Show(_log, "[camera] free camera refused: " + refused); return; }
        IntPtr module = CameraService.Module();
        CameraService.Rendered(out _pos, out _yaw, out _pitch, out float fov);
        _pitch = Math.Clamp(_pitch, PitchFloor, PitchCeil);
        _fovEntry = fov;
        _fov = FreeCam.Fov.V > 0f ? FreeCam.Fov.V : fov;
        _roll = FreeCam.Roll.V;
        _leashed = false; _pending = Why.None; _ate = 0;
        IntPtr gui = Gui();
        if (gui != IntPtr.Zero && new CGuiInGame(gui).ToggledOff == 0) { new CGuiInGame(gui).GuiInGameToggleOff(); _weHidUi = true; }
        Active = true; _entries++;
        // The open panel owns the keyboard: the move keys would never reach the engine's drive key table.
        KnobPanelService.Close();
        AlloyRenderer.CameraCut();
        float serverMax = new CNWCModule(module).CameraMaxDist;
        Notice.Show(_log, FormattableString.Invariant($"[camera] free camera on: move keys fly, Space/C up/down, Z/X roll, Shift fast, wheel = speed, right-drag looks; leash {(serverMax >= 0f ? serverMax : FreeCam.Leash.V):0.#} m; Escape or /free enter returns"));
    }

    /// <summary>Why entry is refused right now, or null.</summary>
    private static string? Refusal()
    {
        if (!_armed) return "not armed on this platform (see the log)";
        if (!CameraService.Live) return "no live camera yet -- log in first";
        if (CameraService.GateUp) return CameraService.GateName;
        IntPtr cre = CameraService.PlayerCreature();
        if (cre == IntPtr.Zero) return "no player creature";
        if (new CNWCCreature(cre).CombatState != 0) return "in combat - try again after the fight";
        IntPtr module = CameraService.Module();
        if (module == IntPtr.Zero || new CNWCModule(module).Camera.IsNull) return "no world camera";
        return null;
    }

    /// <summary>The bare <c>/free</c> line: state, pose while free, the feel, and the session's counts.</summary>
    private static string Report() => FormattableString.Invariant(
        $"{(Active ? $"ON at ({_pos.X:0.0}, {_pos.Y:0.0}, {_pos.Z:0.0}) yaw {_yaw:0.#} pitch {_pitch:0.#} roll {_roll:0.#} fov {_fov:0.#}{(_leashed ? " AT THE LEASH" : "")}" : "off")} | speed {FreeCam.Speed.V:0.##} fov {(FreeCam.Fov.V > 0 ? FreeCam.Fov.V.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) : "keep")} roll {FreeCam.Roll.V:0.#} leash {FreeCam.Leash.V:0.#} sens {FreeCam.Sens.V:0.##} turn {FreeCam.Turn.V:0} | {_entries} entr{(_entries == 1 ? "y" : "ies")}, {_leashHits} leash contact(s)");

    /// <summary>The roll term against the roll-free pose, off-device: zero roll is bit-identical, and a 90° roll
    /// tips the camera's right onto the vertical (the sign is the knob's to have).</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "FreeCamera.SelfCheck");
        check.Expect(PoseRoll(30f, 60f, 0f) == CameraService.Pose(30f, 60f), "no roll is the engine's pose");
        Vector3 right = CameraService.Right(PoseRoll(0f, 90f, 90f));
        check.Expect(MathF.Abs(MathF.Abs(right.Z) - 1f) < 1e-4f && MathF.Abs(right.X) < 1e-4f, "a 90 roll tips right onto the vertical");
        Vector3 look = -CameraService.Forward(PoseRoll(0f, 90f, 90f));
        check.Expect((look - new Vector3(0f, 1f, 0f)).Length() < 1e-4f, "roll keeps the view direction");
        check.Expect(MathF.Abs(PoseRoll(45f, 70f, 33f).Length() - 1f) < 1e-5f, "the rolled pose is a unit quaternion");
        check.Expect(Wrap(370f) == 10f && Wrap(-190f) == 170f, "yaw wraps");
        log.Info(check.Ok ? "FreeCamera.SelfCheck: passed" : "FreeCamera.SelfCheck: FAILED");
    }
}
