using System;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;

namespace CX.Alloy;

/// <summary>Occlusion fade instead of the camera pull-in (<c>UpdateNormalCameraCollision</c>): a fan of <see cref="OccludeFan.Rays"/> segments from the
/// camera focus to the camera is cast through <see cref="HitTest.Cast"/> each frame, <see cref="OccludeFan.HitsPerRay"/> hits each, and the route walk
/// fades the PLACEABLE parts behind a hit toward <c>walls.floor</c> -- into the blended (OIT) run, class untouched so the caster pass sees them
/// whole. The pull-in is off via <see cref="CameraFlagDisableCollision"/>; only the bit this session set is cleared.</summary>
public sealed unsafe partial class AlloyRenderer
{
    private static bool _occArmed;
    private static long _occLastTick;
    private static string _occWhy = "never ran";

    private static IntPtr _occAppMgr;
    private static GetModuleFn _occGetModule;

    /// <summary>This frame's hits: the placeable each ray landed on, as its database handle.</summary>
    private static readonly OccludeHits _occHits = new();
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
        _occHits.N = 0;
        OccludeTick();
        if (!_occArmed) return;
        CameraService.SettleCollision(OccludeModule());
        IntPtr scene = SceneRay.WorldScene();
        if (scene == IntPtr.Zero) return;
        IntPtr player = OccludeTargetGob();
        if (player == IntPtr.Zero) return;
        _occFrames++;
        _occCalls += OccludeFan.Cast(Facade.SceneId(scene), Rt.CamFocus, FrameInputs.CameraPos, Facade.ObjectId(player), _occHits);
        SceneRay.Release();
    }

    /// <summary>Route-walk verdict for one opaque/cutout part: ease its fade toward "hit this frame" and
    /// return it (0 = solid; the caller folds it into the part's alpha). Bounded by <see cref="OccludeFan.MaxHits"/>
    /// compares per part.</summary>
    private static float OccludeVerdict(int i)
    {
        ref RouteGate g = ref _gate[i];
        float f = OccludeFan.Verdict(ref g.OccFade, ref g.OccHeld, DbSlot(i) ? _oDbPart[i] : -1, _oLit[i], _occHits, _occDt);
        if (f > 0f) _occTagged++;
        return f;
    }

    private static float _occDt;

    /// <summary>This frame's wall-clock dt, clamped so a hitch does not snap the easing.</summary>
    private static void OccludeTick()
    {
        long now = Stopwatch.GetTimestamp();
        float dt = _occLastTick == 0 ? 1f / 60f : (float)((now - _occLastTick) / (double)Stopwatch.Frequency);
        _occLastTick = now;
        _occDt = Math.Min(dt, 0.1f);
    }
}
