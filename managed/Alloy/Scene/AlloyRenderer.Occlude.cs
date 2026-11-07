using System;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>Occlusion fade instead of the camera pull-in (<c>UpdateNormalCameraCollision</c>): a fan of <see cref="OccRays"/> segments from the
/// camera focus to the camera is marched through <c>Scene::HitCheck</c> each frame, <see cref="OccHitsPerRay"/> hits each, and the route walk
/// fades the PLACEABLE parts behind a hit toward <c>walls.floor</c> -- into the blended (OIT) run, class untouched so the caster pass sees them
/// whole. The pull-in is off via <see cref="CameraFlagDisableCollision"/>; only the bit this session set is cleared.</summary>
public sealed unsafe partial class AlloyRenderer
{
    private const int OccRays = 5, OccHitsPerRay = 3, OccMaxHits = OccRays * OccHitsPerRay;
    private const float OccNudge = SceneRay.Nudge;
    /// <summary>The fade's ease rate per second (a knob would only ever be set to this).</summary>
    private const float OccRate = 8f;
    /// <summary>Seconds a part counts as hit after its last ray hit: a ray grazing an edge flips hit/miss
    /// frame to frame, and without the hold the screen-door re-thresholds every frame (flicker).</summary>
    private const float OccHold = 0.25f;

    private static bool _occArmed;
    private static long _occLastTick;
    private static string _occWhy = "never ran";

    private static IntPtr _occAppMgr;
    private static GetModuleFn _occGetModule;

    /// <summary>This frame's hits: the placeable each ray landed on, as its database handle.</summary>
    private static readonly uint[] _occHitObj = new uint[OccMaxHits];
    private static int _occHitN;
    // The per-slot fade and its hold are RouteGate's rows -- the route memo reads both per candidate.
    private static long _occFrames, _occCalls, _occTagged;

    private static void OccludeInit(ExtenderLog log)
    {
        CameraKnobs.Declare();
        SceneRay.Init();
        _occAppMgr = Native.Sym(SymbolMap.AppManager);
        _occGetModule = EngineFn.GetModule;
        _occArmed = SceneRay.Armed && _occAppMgr != IntPtr.Zero && _occGetModule != null && _runtime != null;
        _occWhy = _occArmed ? "armed" : "symbols unresolved";
        log.Info($"AlloyRenderer: walls.fade {(Walls.Fade.V ? "on" : "off")} ({_occWhy})");
        Walls.G.Status = () => FormattableString.Invariant(
            $"fade {(Walls.Fade.V ? "on" : "off")} radius {Walls.Radius.V:F2} floor {Walls.Floor.V:F2} ({_occWhy}; ")
            + $"{(_occFrames == 0 ? 0 : _occCalls / _occFrames)} casts, {(_occFrames == 0 ? 0 : _occTagged / _occFrames)} parts tagged per frame)"
            + FormattableString.Invariant($" | arm {CameraService.ArmFraction:0.00} (margin {Walls.Margin.V:0.##} in {Walls.ArmIn.V:0.#} out {Walls.ArmOut.V:0.#} radius {Walls.ArmRadius.V:0.##})");
    }

    /// <summary>The CNWCModule, through the engine's own chain (the UiHostService gate's derefs).</summary>
    private static IntPtr OccludeModule()
    {
        IntPtr mgr = *(IntPtr*)_occAppMgr;
        if (mgr == IntPtr.Zero) return IntPtr.Zero;
        IntPtr app = *(IntPtr*)mgr;
        return app == IntPtr.Zero ? IntPtr.Zero : _occGetModule(app);
    }

    /// <summary>The object the world camera follows (the PC), as the shell the cast's ignore list takes: the
    /// follow behaviour's target, which the DB world pass captured for this view. Zero for a camera that
    /// follows nothing (a cutscene), which casts no fan.</summary>
    private static IntPtr OccludeTargetGob() => (IntPtr)Facade.ObjectShell(new DbId(_dbCamFollow));

    /// <summary>The fade wants the engine's pull-in off; <see cref="CameraService.SettleCollision"/> is the
    /// one writer of the flag, so a spring-arm session ending cannot hand the pull-in back mid-fade.</summary>
    public static bool OccludeFadeWanted => _occArmed && Walls.Fade.V;

    /// <summary>Once per frame, before the route walk: settle the collision flag, then cast the fan.</summary>
    private static void OccludeFrame()
    {
        _occHitN = 0;
        OccludeTick();
        if (!_occArmed) return;
        CameraService.SettleCollision(OccludeModule());
        if (!Walls.Fade.V) return;
        IntPtr scene = SceneRay.WorldScene();
        if (scene == IntPtr.Zero) return;
        IntPtr player = OccludeTargetGob();
        if (player == IntPtr.Zero) return;
        _occFrames++;
        Vector3 focus = Rt.CamFocus, cam = FrameInputs.CameraPos;
        Vector3 d = cam - focus;
        float len = d.Length();
        if (len < 0.05f) return;
        d /= len;
        Vector3 right = Vector3.Cross(d, Vector3.UnitZ);
        if (right.LengthSquared() < 1e-6f) right = Vector3.UnitX; else right = Vector3.Normalize(right);
        Vector3 up = Vector3.Cross(right, d);
        float radius = Walls.Radius.V;
        for (int r = 0; r < OccRays; r++)
        {
            Vector3 o = r switch
            {
                1 => right * radius, 2 => -right * radius, 3 => up * radius, 4 => -up * radius, _ => Vector3.Zero,
            };
            Vector3 from = focus + o, to = cam + o;
            for (int h = 0; h < OccHitsPerRay; h++)
            {
                _occCalls++;
                if (!SceneRay.Cast(scene, from, to, player, out Vector3 hit, out IntPtr gob)) break;
                if (SceneRay.IsPlaceable(gob))
                {
                    int n = _occHitN++;
                    _occHitObj[n] = Facade.ObjectId(gob).Value;
                }
                from = hit + d * OccNudge;
                if (Vector3.Dot(to - from, d) <= 0f) break;
            }
        }
        SceneRay.Release();
    }

    /// <summary>Route-walk verdict for one opaque/cutout part: ease its fade toward "hit this frame" and
    /// return it (0 = solid; the caller multiplies alpha by 1 - fade). Bounded by <see cref="OccMaxHits"/>
    /// compares per part.</summary>
    private static float OccludeVerdict(int i)
    {
        ref RouteGate g = ref _gate[i];
        float f = g.OccFade;
        bool hit = false;
        if (_occHitN > 0 && _oLit[i])
        {
            uint obj = DbSlot(i) ? SceneDb.PartObj[_oDbPart[i]].Value : 0u;
            if (obj != 0)
                for (int n = 0; n < _occHitN && !hit; n++) hit = _occHitObj[n] == obj;
        }
        if (hit) g.OccHeld = OccHold;
        else if (g.OccHeld > 0f) { g.OccHeld -= _occDt; hit = true; }
        if (!hit && f == 0f) return 0f;
        float target = hit ? 1f - Walls.Floor.V : 0f;
        f += (target - f) * _occStep;
        if (MathF.Abs(target - f) < 0.005f) f = target;
        g.OccFade = f;
        if (f > 0f) _occTagged++;
        return f;
    }

    private static float _occStep, _occDt;

    /// <summary>The easing step for this frame, from wall-clock dt (clamped so a hitch does not snap).</summary>
    private static void OccludeTick()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _occLastTick == 0 ? 1f / 60f : (float)((now - _occLastTick) / (double)Stopwatch.Frequency);
        _occLastTick = now;
        _occDt = Math.Min(dt, 0.1f);
        _occStep = Math.Clamp(_occDt * OccRate, 0f, 1f);
    }
}
