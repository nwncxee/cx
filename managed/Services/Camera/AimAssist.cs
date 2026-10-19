using System;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;

namespace CX.Services.Camera;

/// <summary>
/// The action camera's two targeting aids plus the crosshair's cursor read, owned by <see cref="MouseLookService"/>
/// rather than discovered as a service. Aim assist (<c>assist</c>) projects the area's objects against the live
/// camera and biases where mouse events are forwarded, so the ENGINE still does every pick; auto-target
/// (<c>autotarget</c>) resolves a live targeting mode as a click would. Neither moves the camera.
/// </summary>
internal static unsafe class AimAssist
{
    /// <summary>The walk and the auto-target hook are up. <see cref="_cursorReady"/> is the crosshair's own,
    /// narrower gate: the cursor-manager chain only.</summary>
    private static bool _ready, _cursorReady;

    /// <summary>Resolved once: the walk runs every frame the action camera is held.</summary>
    private static readonly GetGameObjectFn _getGameObject = EngineFn.GetGameObject;
    private static ActivateTargetModeFn _activateOrig;
    private static ActionTargetLMouseDownFn _lmouse;

    /// <summary>Object type bytes worth aiming at. The walk's fourth type, item (6, the loose-loot class the
    /// pickup cursor is for), has no constant on purpose: loot is picked by pointing, not by aiming.</summary>
    private const byte TypeCreature = 5, TypePlaceable = 9, TypeDoor = 0xa;  // not-an-offset: an object type enum
    /// <summary>The synthetic allowed-target type meaning "a point on the ground" (AoE placement).</summary>
    private const int GroundTargetType = 2;
    /// <summary>Metres added to the object's ground position for the aim point -- there is no cheap height
    /// field on CNWCObject, and the crosshair tolerance is pixels wide. ponytail: the engine's own projected
    /// bubble anchor would be exact, but it exists only for objects the nameplate feature has already touched
    /// and lands in a different screen space (bottom-left, camera viewport, GUI-scaled).</summary>
    private const float CreatureLift = 1.0f, PlaceableLift = 0.5f, DoorLift = 1.0f;
    /// <summary>Pixels of screen distance one metre of range is worth when the candidates are scored, so a near
    /// object beats a far one that happens to sit dead centre. ponytail: this IS the whole occlusion story --
    /// nothing tests line of sight, so a target behind a wall still scores (it simply picks nothing when the
    /// engine's own pick runs there); the upgrade path is a SceneRay eye-to-candidate check on the best few.</summary>
    private const float AssistDepthPxPerM = 3f;
    /// <summary>No candidate. Not a valid packed point (x = -1).</summary>
    private const long NoPoint = -1L;
    /// <summary>This frame's assist point, packed <c>(x &lt;&lt; 32) | (uint)y</c> so the event path reads both
    /// halves in one aligned load rather than catching a half-updated pair.</summary>
    private static long _point = NoPoint;

    /// <summary>Backing field of <see cref="AssistTarget"/>; written by the frame tick, read from the event
    /// path and the crosshair draw, so both ends go through <see cref="Volatile"/>.</summary>
    private static uint _target;

    /// <summary>The object id the assist picked this frame, 0 when none. Read by the crosshair.</summary>
    internal static uint AssistTarget => Volatile.Read(ref _target);

    private static int _hits, _fires, _scanned;

    /// <summary>Arm the aim assist and the auto-target. From <see cref="MouseLookService"/>'s Initialize, which
    /// has already gated on its own symbols; the offsets and the two CActionTarget symbols are gated here, and
    /// a refusal leaves the action camera itself untouched (plain centre pick, plain crosshair). The cursor read
    /// behind <see cref="CursorId"/> is gated first and on its own rows, so it outlives every refusal below it.</summary>
    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _cursorReady = EngineLayout.Require(log, "MouseLook.crosshair", "CClientExoApp", "CClientExoAppInternal",
                                            "CCachedMouseCursorManager")
                       && SymbolMap.Require(log, "MouseLook.crosshair", SymbolMap.AppManager);
        if (_getGameObject == null) return;
        if (!EngineLayout.Require(log, "MouseLook.assist", "CNWCObject", "CNWCArea", "CNWCCreature", "CGameObject",
                                  "CClientExoAppInternal", "CCachedMouseCursorManager", "CActionTarget",
                                  "Camera", "CNWCModule", "CClientExoApp")) return;
        if (!SymbolMap.Require(log, "MouseLook.assist", SymbolMap.ActivateTargetMode, SymbolMap.ActionTargetLMouseDown,
                               SymbolMap.GetGameObject, SymbolMap.GetModule, SymbolMap.AppManager)) return;

        _lmouse = EngineFn.ActionTargetLMouseDown;
        _activateOrig = hooks.InstallActivateTargetMode(&OnActivateTargetMode);
        _ready = _activateOrig != null && _lmouse != null;
        if (!_ready) { log.Info("MouseLook.assist: standing down (ActivateTargetMode install refused)"); return; }

        FrameRegistry.Global.Register(FramePhase.PostPresent, 60, Tick, "AimAssist", log);
        log.Info("MouseLook.assist: aim assist ticking per frame; auto-target hooks CActionTarget::ActivateTargetMode");
    }

    /// <summary>This frame's assist point, or false for "aim at the exact centre". One aligned load.</summary>
    internal static bool Point(out int x, out int y)
    {
        long p = Volatile.Read(ref _point);
        if (p == NoPoint) { x = 0; y = 0; return false; }
        x = (int)(p >> 32); y = (int)p;
        return true;
    }

    /// <summary>The live mouse cursor id the engine last set (<c>CCachedMouseCursorManager+0x1cb0</c>), or 0
    /// when the chain is not up. A field read, no hook: the engine's own per-frame
    /// <c>UpdateMouseCursorAppearance</c> keeps it current, including while a targeting mode overrides it. On
    /// <see cref="_cursorReady"/> alone: the crosshair does not want the assist's rows or its hook.</summary>
    internal static int CursorId()
    {
        if (!_cursorReady) return 0;
        IntPtr app = CameraService.App();
        if (app == IntPtr.Zero) return 0;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        if (intern.IsNull) return 0;
        CCachedMouseCursorManager mgr = intern.CursorManager;
        return mgr.IsNull ? 0 : mgr.CurrentCursor;
    }

    // ---- the per-frame walk ----

    private static void Clear() { Volatile.Write(ref _target, 0); Volatile.Write(ref _point, NoPoint); }

    /// <summary>One frame of aim assist: project the area's own object list (the one <c>HiliteAreaObjects</c> walks)
    /// against the live camera and keep the best score inside the mode's <c>assistpx</c> -- centre distance plus
    /// range at <see cref="AssistDepthPxPerM"/>, so a near candidate wins a tie. Hot (every held frame), allocates
    /// nothing. A placeable's hilite flag reads through <c>AsNWCPlaceable</c>, every other class's off CNWCObject.</summary>
    private static void Tick()
    {
        if (!_ready || !MouseLookService.ActionHeld || !MouseLook.Assist.V || MouseLook.AssistPx.V <= 0) { Clear(); return; }

        IntPtr module = CameraService.Module();
        IntPtr app = CameraService.App();
        if (module == IntPtr.Zero || app == IntPtr.Zero) { Clear(); return; }
        var mod = new CNWCModule(module);
        CX.Engine.Camera cam = mod.Camera;
        CNWCArea area = mod.CurrentArea;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        if (cam.IsNull || area.IsNull || intern.IsNull) { Clear(); return; }

        MouseLookService.Centre(out int cx, out int cy);
        if (!Projector.Read(cam, cx, cy, out Projector view)) { Clear(); return; }

        int n = area.ObjectCount;
        uint* ids = (uint*)area.ObjectIds;
        if (n <= 0 || ids == null) { Clear(); return; }

        uint playerId = intern.PlayerCreatureId;
        float radius2 = MouseLook.AssistPx.V * MouseLook.AssistPx.V;
        float best = float.MaxValue;
        long bestPoint = NoPoint;
        uint bestId = 0;

        for (int i = 0; i < n; i++)
        {
            uint id = ids[i];
            if (id == playerId || id == TypingSignal.ObjectInvalid) continue;
            var go = new CGameObject(_getGameObject(app, id));
            if (go.IsNull) continue;
            CNWCObject obj = go.AsNWCObject();
            if (obj.IsNull || obj.Visible == 0) continue;
            byte type = obj.Type;
            float lift;
            switch (type)
            {
                case TypeCreature: lift = CreatureLift; break;
                case TypePlaceable: lift = PlaceableLift; break;
                case TypeDoor: lift = DoorLift; break;
                default: continue;
            }
            CNWCObject hil = type != TypePlaceable ? obj : new CGameObject(obj.Ptr).AsNWCPlaceable();
            if (hil.IsNull || hil.Highlightable == 0) continue;
            Vector3 w = obj.Position;
            if (!view.Screen(new Vector3(w.X, w.Y, w.Z + lift), out float sx, out float sy, out float ez)) continue;
            float dx = sx - cx, dy = sy - cy, d2 = dx * dx + dy * dy;
            if (d2 >= radius2) continue;
            float score = MathF.Sqrt(d2) + ez * AssistDepthPxPerM;
            if (score >= best) continue;
            best = score; bestId = id; bestPoint = ((long)(int)sx << 32) | (uint)(int)sy;
        }

        _scanned = n;
        // The id first: the event path reads the point, so a torn pair must never name a target the point misses.
        Volatile.Write(ref _target, bestId);
        Volatile.Write(ref _point, bestPoint);
        if (bestId != 0) _hits++;
    }

    // ---- auto-target ----

    /// <summary>Post-hook: the mode is up and registered, so resolving it now takes exactly the path a click would.
    /// Nothing reads the CActionTarget after this returns -- every caller of ActivateTargetMode calls it last, and
    /// HandleLMouseDown touches no member after the fire callback that may delete the subclass. ponytail: the
    /// <c>s_bJustCastSelfTargetSpells</c>/<c>Items</c> statics would skip the mode, but are unexported on both builds.</summary>
    [UnmanagedCallersOnly]
    private static void OnActivateTargetMode(IntPtr self)
    {
        _activateOrig(self);
        if (!_ready || self == IntPtr.Zero) return;
        // Tab-target's cast mode supersedes the auto-target toggle while it is on.
        if (TabTargetService.OnActivate(self)) return;
        if (!MouseLook.AutoTarget.V || !MouseLookService.ActionHeld) return;
        uint hovered = HoveredOid();
        if (!TakesAnObject(self) || hovered == 0 || hovered == TypingSignal.ObjectInvalid) return;
        _fires++;
        _lmouse(self);
    }

    /// <summary>The auto-target hook is installed, so <see cref="Resolve"/> may be called.</summary>
    internal static bool Ready => _ready;

    /// <summary>Resolve a live mode as a click would (<c>HandleLMouseDown</c>). Nothing may touch
    /// <paramref name="self"/> after this returns: the fire callback may delete it.</summary>
    internal static void Resolve(IntPtr self) { _fires++; _lmouse(self); }

    /// <summary>Whether this mode accepts an OBJECT and not only a ground point: a spell whose only allowed
    /// target is the synthetic type 2 is an AoE that wants aiming, so it is left up.</summary>
    internal static bool TakesAnObject(IntPtr self)
    {
        var mode = new CActionTarget(self);
        int n = mode.AllowedCount;
        int* a = (int*)mode.Allowed;
        if (a == null) return false;
        for (int i = 0; i < n; i++) if (a[i * 2] != GroundTargetType) return true;   // stride 8: {type, cursor}
        return false;
    }

    /// <summary>The id under the cursor, mirrored onto the player creature by the engine's own
    /// <c>UpdateMouseCursorAppearance</c>; <see cref="TypingSignal.ObjectInvalid"/>, or 0 before the field has
    /// ever been written, when nothing is hovered. Under the action camera the engine's cursor IS the crosshair
    /// (or the assist point), so this is what it points at.</summary>
    private static uint HoveredOid()
    {
        IntPtr cre = CameraService.PlayerCreature();
        return cre == IntPtr.Zero ? TypingSignal.ObjectInvalid : new CNWCCreature(cre).HoveredOid;
    }

    /// <summary>The mode status fragment.</summary>
    internal static string Report() => !_ready ? "assist off (stood down)"
        : FormattableString.Invariant($"assist {(MouseLook.Assist.V ? $"{MouseLook.AssistPx.V}px" : "off")}, {_hits} hit(s) over {_scanned} object(s); auto-target {(MouseLook.AutoTarget.V ? "on" : "off")}, {_fires} fire(s)");
}

/// <summary>The live camera's screen projection, read once per walk off the Camera object: the stored quaternion is
/// (w, x, y, z) and rotates view-&gt;world, so eye space looks down -Z, and the terms are the engine's own
/// SetPerspectiveTransform. Never <c>Camera::MapToScreen</c> -- its by-value Vector return puts the sret argument in
/// a different place on each ABI. Shared by the aim assist and the tab-target cycle.</summary>
internal unsafe struct Projector
{
    private Vector3 _eye, _fwd, _right, _up;
    private float _cx, _cy, _tanHalf, _aspect, _near;

    /// <summary>A view angle at or below this is taken as a radian half-angle rather than degrees -- the same
    /// hedge <c>ShadowMath.ViewBasis</c> carries, so a stray build cannot project 57x too wide.</summary>
    private const float FovRadianCeiling = 3.2f;

    /// <summary>False when the camera or the centre is not usable this frame.</summary>
    internal static bool Read(CX.Engine.Camera cam, int cx, int cy, out Projector p)
    {
        p = default;
        if (cam.IsNull || cx <= 0 || cy <= 0) return false;
        float fov = cam.ViewAngle;
        if (!(fov > 0f)) return false;
        Quaternion rot = cam.Rot;
        p._eye = cam.Pos;
        p._fwd = Vector3.Transform(new Vector3(0f, 0f, -1f), rot);
        p._right = Vector3.Transform(new Vector3(1f, 0f, 0f), rot);
        p._up = Vector3.Transform(new Vector3(0f, 1f, 0f), rot);
        float vw = cam.ViewW, vh = cam.ViewH;
        p._aspect = vh > 0f ? vw / vh : (float)cx / cy;
        p._tanHalf = MathF.Tan(fov * (fov > FovRadianCeiling ? MathF.PI / 360f : 0.5f));
        p._near = cam.Near;
        if (!(p._near > 0f)) p._near = 0.1f;
        p._cx = cx; p._cy = cy;
        return true;
    }

    /// <summary>World point -&gt; SDL window pixels; false (and nothing mirrored onto the screen) for anything at
    /// or nearer than the near plane. <paramref name="ez"/> is the eye-space depth in metres.</summary>
    internal bool Screen(Vector3 world, out float sx, out float sy, out float ez)
    {
        Vector3 d = world - _eye;
        ez = Vector3.Dot(d, _fwd);
        if (ez <= _near) { sx = sy = 0f; return false; }
        sx = _cx + Vector3.Dot(d, _right) / (ez * _tanHalf * _aspect) * _cx;
        sy = _cy - Vector3.Dot(d, _up) / (ez * _tanHalf) * _cy;
        return true;
    }
}
