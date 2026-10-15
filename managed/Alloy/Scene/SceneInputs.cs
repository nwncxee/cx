using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// What feeds the Alloy shadow pipeline; it renders nothing -- <see cref="CasterPass"/> is the ONE shadow
/// system. Here live the shared per-frame shadow focus (published as <c>FrameInputs.Focus</c>), the
/// departed-light fade the engine has no edge for, and the engine reads the database has no column for.
/// </summary>
/// <remarks>Hot: the shadow slot runs once a frame and allocates nothing.</remarks>
public sealed unsafe class SceneInputs : IExtenderService
{
    /// <summary>How far ahead of the camera the map centre marches (<c>/shadow focusdist</c>).</summary>
    private static float _focusDist => Shadows.FocusDist.V;
    private static ExtenderLog? _log;
    private static bool _enabled;
    /// <summary>How far <see cref="DbShadowSlot"/> runs, for one-restart bisection on radeonsi. 2 = focus
    /// only, 3 = + cube bind, 4 = + caster bake (full). <c>shadow.stage</c>, Boot.</summary>
    private static int _stage = 4;

    private static bool _localLoggedOnce;   // slice 6: one-shot log of the first frame that records point lights

    // ---- the shared shadow focus ----

    // One map centre per presented frame, published as FrameInputs.Focus for CasterPass's cascade fit:
    // the database's camera-follow target when there is one, else the camera-ground focus.
    // Verified via Ghidra: current area = *(CClientExoApp::GetModule(*g_pAppManager) + 0xf8) (the engine's own
    // HandleServerToPlayerArea chain); CNWCArea+8 = the ARE Flags DWORD (CNWSArea::PackAreaIntoMessage writes
    // m_nFlags@+8 -> CNWCArea::LoadArea reads it into +8), bit 0 = interior.
    private static IntPtr _appMgr;                    // &g_pAppManager (deref per use)
    private static GetModuleFn _getModule;

    private static bool _passDoneThisFrame;
    private static bool _haveFocus;
    private static Vector3 _focus;
    private static int _focusSource = -1;     // 0=camera-target, 2=camera-ground; log on transition
    private static int _focusFrames;          // frames held on the current source (reported at the next transition)

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // Restart knobs (shadow.enabled, shadow.stage): this arms the feed, so it is a boot decision,
        // read through Boot.
        _enabled = Shadows.Enabled.Boot;
        _stage = Shadows.Stage.Boot;
        if (_stage < 4) log.Info($"SceneInputs: shadow.stage={_stage} -- the shadows feed stops after {(_stage < 3 ? "the focus" : "the cube bind (no bake)")}");

        ShadowMath.SelfCheck(log);   // pure math, runs headless
        SkyMath.SelfCheck(log);      // ...and the overhead sky view's fit and horizon estimate
        FadeSelfCheck(log);          // ...and so does the fade-out bookkeeping
        FrameRead.SelfCheck(log);    // ...and the camera conventions the scene feed derives from
        // CurrentArea() is DB mode's fog and interior source too, so it resolves whatever the shadows say.
        _appMgr = Native.Sym(SymbolMap.AppManager);
        _getModule = EngineFn.GetModule;

        if (!_enabled) return;

        _getGspF = EngineFn.RiGetGlobalShaderParamf;

        // The per-presented-frame edge. Was a zero-size UiHost Window abused as a draw slot (#169); #170 gives
        // it the real thing. Priority 0: it ran inside the window roll-call, i.e. before the #80 context bracket.
        FrameRegistry.Global.Register(FramePhase.PrePresent, 0, Draw, "SceneInputs.frameEdge", log);
        log.Info("SceneInputs: shadow focus armed, feeding CasterPass (shadow.enabled=0 disables)");
    }

    // ---- #336: the fade-out the engine has no edge for ----
    // A light that APPEARS ramps: the engine grows its intensity, and since the lit radius is
    // range * intensity (GetLightAdjustedRadius) the pool grows out of the source. A light that GOES OUT
    // is simply unlinked from the master list, so it pops. This is the missing half, on Alloy's side: the
    // last known state of a light that left the list is re-emitted for a few frames with its intensity
    // scaled down, which shrinks the pool back into the source on the same curve.

    /// <summary>Frames a departed light takes to fade. <c>/light fadeout</c>; 0 = pop, as before.</summary>
    internal static int FadeFrames => Lighting.Fadeout.V;
    internal const int FadeFramesBoot = 20;
    /// <summary>Lights allowed to fade at once. More than this vanishing in ONE frame is a load edge -- an
    /// area transition unlinks the whole list -- and the ghosts are dropped rather than played out.</summary>
    private const int MaxGhosts = 16;

    private static IntPtr[] _prevId = new IntPtr[64];
    private static Vector4[] _prevPosW = new Vector4[64];
    private static Vector3[] _prevCol = new Vector3[64];
    private static bool[] _prevAmb = new bool[64];
    private static int _prevN;

    private static readonly IntPtr[] _ghostId = new IntPtr[MaxGhosts];
    private static readonly Vector4[] _ghostPosW = new Vector4[MaxGhosts];
    private static readonly Vector3[] _ghostCol = new Vector3[MaxGhosts];
    private static readonly bool[] _ghostAmb = new bool[MaxGhosts];
    private static readonly int[] _ghostLeft = new int[MaxGhosts];
    private static int _ghostN;

    /// <summary>World edge: the last set and its ghosts are the departed area's lights, at its positions; none
    /// of them may fade out inside the next one.</summary>
    internal static void Revoke() { _prevN = 0; _ghostN = 0; }

    /// <summary>The fade bookkeeping, headless. It is a set difference over two flat arrays with a
    /// swap-remove in the middle, and every way it can go wrong is silent -- a ghost that never retires is a
    /// light that never goes out, a missed leaver is the pop this exists to remove.</summary>
    internal static void FadeSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "SceneInputs.FadeSelfCheck");

        // Plain field writes on the knob, not Sets: nothing runs and nothing is written to the file.
        int fadeWas = FadeFrames, prevWas = _prevN, ghostWas = _ghostN;
        Lighting.Fadeout.V = 3; _prevN = 0; _ghostN = 0;

        void Lit(params int[] ids)
        {
            FrameInputs.BeginAreaLights(ids.Length);
            foreach (int id in ids)
                FrameInputs.RecordAreaLight(id, new Vector3(id, 0f, 0f), 10f, Vector3.One);
            FadeDepartedLights();
        }

        Lit(1, 2);
        check.Expect(FrameInputs.LightCount == 2 && _ghostN == 0, "two lit lights, nothing fading");
        Lit(1);
        check.Expect(_ghostN == 1 && FrameInputs.LightCount == 2, "a departed light is still emitted, once");
        check.Expect(FrameInputs.AreaLightPosRange[1].W < 10f, "...at a shrunken radius");
        check.Expect(FrameInputs.AreaLightPosRange[1].W > 0f, "...and not to nothing on its first frame");
        Lit(1); Lit(1); Lit(1);
        check.Expect(_ghostN == 0 && FrameInputs.LightCount == 1, "and retires after FadeFrames frames");
        Lit(1, 2); Lit(1);
        check.Expect(_ghostN == 1, "light 2 fades again");
        Lit(1, 2);
        check.Expect(_ghostN == 0 && FrameInputs.LightCount == 2, "a light that comes back drops its ghost");
        // The load edge: more leavers than the cap in one frame is an area transition, not a torch.
        Lit(1, 2, 3, 4); Lighting.Fadeout.V = 3; _ghostN = 0;
        var many = new int[MaxGhosts + 4];
        for (int i = 0; i < many.Length; i++) many[i] = 100 + i;
        Lit(many); Lit(1);
        check.Expect(_ghostN == 0, "a whole list vanishing at once fades nothing");
        Lighting.Fadeout.V = 0; Lit(1, 2); Lit(1);
        check.Expect(_ghostN == 0 && FrameInputs.LightCount == 1, "fadeout 0 pops, as before");

        Lighting.Fadeout.V = fadeWas; _prevN = prevWas; _ghostN = ghostWas;
        FrameInputs.BeginAreaLights(0);
        log.Info(check.Ok ? "SceneInputs.FadeSelfCheck: passed" : "SceneInputs.FadeSelfCheck: FAILED -- see above");
    }

    /// <summary>Is this light in the set just admitted? The master list keeps its order frame to frame, so
    /// the paired index walks in step and the full scan is the reorder path, not the common one.</summary>
    private static bool StillLit(IntPtr id, int cur, ref int j)
    {
        if (j < cur && FrameInputs.AreaLightId[j] == id) { j++; return true; }
        for (int k = 0; k < cur; k++)
            if (FrameInputs.AreaLightId[k] == id) { j = k + 1; return true; }
        return false;
    }

    /// <summary>Age the fading lights, emit them, and take this frame's set as the next comparison. Called
    /// with <see cref="FrameInputs.LightCount"/> holding the lights the walk actually admitted -- the ghosts
    /// are appended after the snapshot, so a ghost never seeds another ghost.</summary>
    private static void FadeDepartedLights()
    {
        int cur = FrameInputs.LightCount;
        // A light that came back keeps nothing of its ghost; the live entry above is already the real one.
        for (int i = 0; i < _ghostN; i++)
        {
            int j0 = 0;
            if (--_ghostLeft[i] > 0 && !StillLit(_ghostId[i], cur, ref j0)) continue;
            _ghostN--;
            _ghostId[i] = _ghostId[_ghostN]; _ghostPosW[i] = _ghostPosW[_ghostN];
            _ghostCol[i] = _ghostCol[_ghostN]; _ghostAmb[i] = _ghostAmb[_ghostN]; _ghostLeft[i] = _ghostLeft[_ghostN];
            i--;
        }
        if (FadeFrames > 0)
        {
            int gone = 0, j = 0;
            for (int i = 0; i < _prevN; i++)
            {
                if (StillLit(_prevId[i], cur, ref j)) continue;
                if (++gone > MaxGhosts - _ghostN) { _ghostN = 0; break; }
                _ghostId[_ghostN] = _prevId[i]; _ghostPosW[_ghostN] = _prevPosW[i];
                _ghostCol[_ghostN] = _prevCol[i]; _ghostAmb[_ghostN] = _prevAmb[i]; _ghostLeft[_ghostN++] = FadeFrames;
            }
        }

        if (cur > _prevId.Length)
        {
            _prevId = new IntPtr[cur * 2];
            _prevPosW = new Vector4[cur * 2];
            _prevCol = new Vector3[cur * 2];
            _prevAmb = new bool[cur * 2];
        }
        _prevN = cur;
        Array.Copy(FrameInputs.AreaLightId, _prevId, cur);
        Array.Copy(FrameInputs.AreaLightPosRange, _prevPosW, cur);
        Array.Copy(FrameInputs.AreaLightColor, _prevCol, cur);
        Array.Copy(FrameInputs.AreaLightAmbient, _prevAmb, cur);

        for (int i = 0; i < _ghostN; i++)
        {
            // Intensity is the engine's own ramp on the way in, and the radius rides it -- so scaling both
            // by the same k is what makes the pool shrink INTO the source instead of dimming in place.
            // +1 so the first ghost frame is already below full: the light was at full strength the frame
            // before, and re-emitting it unchanged would spend one frame of the fade standing still.
            float k = (float)_ghostLeft[i] / (FadeFrames + 1);
            var pw = _ghostPosW[i];
            FrameInputs.RecordAreaLight(_ghostId[i], new Vector3(pw.X, pw.Y, pw.Z), pw.W * k, _ghostCol[i] * k, _ghostAmb[i]);
        }
    }
    /// <summary>RenderInterface::GetGlobalShaderParamf -- param 0x12 = the engine's light-radius multiplier.</summary>
    private static RiGetGlobalShaderParamfFn _getGspF;

    /// <summary>Latch the two constants the engine's point-light attenuation is built from, out of the
    /// global-param array the radius multiplier comes from (<c>GLRender::SetUniforms</c> @0x4cd341 reads
    /// 0x12/0x13/0x14). Read on every walk -- they are player-changeable graphics settings -- and by the area
    /// key at <c>LoadArea</c>'s return, which on the login load runs before any walk. Returns the multiplier.</summary>
    internal static float LatchAtten()
    {
        float radiusMult = _getGspF != null ? _getGspF(0x12) : 1f;
        if (!(radiusMult > 0f)) radiusMult = 1f;
        if (_getGspF != null)
            CX.Alloy.AlloyLights.LatchAtten(_getGspF(0x13), _getGspF(0x14), radiusMult);
        return radiusMult;
    }
    /// <summary>The light walk's tail: the departure fade, the bake's static-set close, and the count the
    /// settle checks read. They live here rather than inline in one walk's body so that any light SOURCE
    /// closes the walk identically.</summary>
    internal static void FinishWalk()
    {
        FadeDepartedLights();
        LightmapBake.EndWalk();
    }

    /// <summary>CPU-side light-&gt;cube bind: cube slot per AREA light (-1 = no cube), the one thing that tells
    /// the Alloy lit stage which cube layer a light's shadow lives in. Driven from the pass slot AFTER the
    /// bake, so the mapping and the cube contents describe the same frame.</summary>
    private static void BindLightCubes()
    {
        int bound = 0;
        for (int i = 0; i < FrameInputs.LightCount; i++)
            if ((FrameInputs.AreaLightSlot[i] = CasterPass.CubeSlotOf(FrameInputs.AreaLightId[i])) >= 0) bound++;
        // A cube nothing binds to is written every frame and read by no one, and the receiver's -1 path
        // returns "lit" in silence -- so rendered-cube counts alone cannot tell a working shadow from an
        // absent one. This is the number that can.
        FrameInputs.LightsWithCube = bound;
    }

    /// <summary>The map centre for this frame -- computed once and reused by the re-walk and every depth-pass
    /// sub-frame so they align. Prefers the camera-follow target (the real PC, camera-independent), falls back
    /// to the LAST known PC position (sticky, so a non-follow subframe does not snap the map and shimmer),
    /// camera-ground only until the PC is ever seen. Source TRANSITIONS are logged. Reset each frame.</summary>
    private static Vector3 ShadowFocus()
    {
        if (!_haveFocus)
        {
            int src;
            // Two LIVE sources only (shadows v2): the camera-follow target when there is one, else
            // camera-ground. The old sticky-last bridge could freeze the focus at a stale point (#67);
            // nothing keyed on focus needs sub-frame stability anymore -- selection/LOD anchor on the camera.
            // CX-120: in DB mode the follow target is a database column, not an engine Gob chain -- the
            // engine reader below returns null for every frame of it, and the fit then sat 24u straight
            // DOWN from the camera (camera-ground against an identity camera rotation), i.e. underground,
            // which spends cascade 0 on dirt and pancakes the rest onto the near plane.
            if (DbFollowFocus() is Vector3 pp) { _focus = pp; src = 0; }
            else { _focus = ShadowMath.CameraGroundFocus(FrameInputs.CameraPos, FrameInputs.CameraRot, _focusDist); src = 2; }
            if (src != _focusSource)
            {
                _log?.Info($"SceneInputs: focus source -> {FocusName(src)} at ({_focus.X:F1},{_focus.Y:F1},{_focus.Z:F1}) " +
                           $"(prev {FocusName(_focusSource)} held {_focusFrames} frames)");
                _focusSource = src; _focusFrames = 0;
            }
            // CX-120 bring-up: a focus that JUMPS vertically, in either scene mode, named with its source.
            if (MathF.Abs(_focus.Z - _lastFocusZ) > 1f)
            {
                _log?.Info($"SceneInputs: [focusjump] {FocusName(src)} z {_lastFocusZ:F3} -> {_focus.Z:F3} "
                           + $"at ({_focus.X:F3},{_focus.Y:F3}) frame {_focusFrames}");
                _lastFocusZ = _focus.Z;
            }
            _focusFrames++;
            _haveFocus = true;
            FrameInputs.Focus = _focus; FrameInputs.HasFocus = true;   // the nearest-K key for light recording
        }
        return _focus;
    }

    /// <summary>The shadow half of the frame anchor: the shared shadow focus and the world-caster bake,
    /// called from the DB world pass after the light rig and before the colour pass.
    /// <para>The light walk and the interior read are NOT here: the database owns both
    /// (<c>DbLights.Walk</c>, <c>SceneFeed</c>).</para></summary>
    internal static void DbShadowSlot(DbId camera)
    {
        _dbCamera = camera;
        if (!_enabled || _passDoneThisFrame) return;
        if (_stage < 2) { _passDoneThisFrame = true; return; }
        ShadowFocus();
        if (_stage < 3) { _passDoneThisFrame = true; return; }
        // Net of Pose: the bake poses every owned skinmesh first and Pose has its own row.
        long tBake = FrameZones.Now, pose0 = FrameZones.Ticks(FrameZones.Zone.Pose);
        if (_stage >= 4) CasterPass.OnPassSlot();
        FrameZones.Add(FrameZones.Zone.Bake, tBake, FrameZones.Ticks(FrameZones.Zone.Pose) - pose0);
        // AFTER the bake, so a light and the cube it points at come from the SAME bake: binding before it
        // would map this frame's lights onto last frame's slot assignment.
        BindLightCubes();
        _passDoneThisFrame = true;
    }

    /// <summary>The current CNWCArea, or the null view while the chain is unavailable. Walks the engine's own
    /// chain (**g_pAppManager -&gt; CClientExoApp::GetModule -&gt; module+0xf8; the global needs TWO derefs --
    /// CAppManager first, its first field is the CClientExoApp*), every deref guarded. Compared, never
    /// retained. Alloc-free, once per frame.</summary>
    internal static CNWCArea CurrentArea()
    {
        if (_appMgr == IntPtr.Zero || _getModule == null) return default;
        IntPtr mgr = *(IntPtr*)_appMgr;          // CAppManager*
        if (mgr == IntPtr.Zero) return default;
        IntPtr app = *(IntPtr*)mgr;              // CClientExoApp* (mgr's first field -- the UiHostService gate chain)
        if (app == IntPtr.Zero) return default;
        var module = new CNWCModule(_getModule(app));
        return module.IsNull ? default : module.CurrentArea;
    }

    private static string FocusName(int s) => s switch { 0 => "camera-target", 2 => "camera-ground", _ => "none" };

    /// <summary>The focus height the <c>[focusjump]</c> tripwire last reported, so it fires on an EDGE rather
    /// than once per frame of a standing disagreement.</summary>
    private static float _lastFocusZ;

    /// <summary>The world camera the DB pass is drawing, published by it so the focus can ask the database
    /// what that camera follows. Default (invalid) outside a DB world pass.</summary>
    private static DbId _dbCamera;

    /// <summary>The world camera the DB pass last drew, for a diagnostic that needs to ask what it follows.</summary>
    internal static DbId DbWorldCamera => _dbCamera;

    /// <summary>The object the world camera follows, which is the PC. One column read; null when the camera
    /// follows nothing (a cutscene camera, or before the chase behaviour has solved), and the caller falls
    /// back to the camera-ground focus.</summary>
    private static Vector3? DbFollowFocus()
    {
        if (!SceneDb.Cameras.Valid(_dbCamera)) return null;
        DbId target = SceneDb.CamFollow[_dbCamera.Index];
        return SceneDb.Objects.Valid(target) ? SceneDb.ObjPos[target.Index] : null;
    }

    /// <summary>Per-presented-frame edge from the UiHost roll-call: re-arm the once-per-frame shadow slot.
    /// The light snapshots are NOT reset here -- they restart at the top of the database's own walk, which is
    /// the same slot that consumes them.</summary>
    private static void Draw()
    {
        _haveFocus = false;         // recompute the shared map centre next frame
        _passDoneThisFrame = false; // re-arm the once-per-frame shadow slot
        LogLocalLights();           // one-shot dump of the recorded point lights
    }

    /// <summary>One-shot dump of the shadow-worthy point lights the database walk recorded, so the feed is provable
    /// independently of whether CasterPass got cubes out of it. Cold -- the string build runs once.</summary>
    private static void LogLocalLights()
    {
        if (_localLoggedOnce || FrameInputs.LocalLightCount == 0 || _log == null) return;
        _localLoggedOnce = true;
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < FrameInputs.LocalLightCount; i++)
        {
            Vector3 p = FrameInputs.LocalLightPos[i];
            sb.Append($" [{i}]=({p.X:F1},{p.Y:F1},{p.Z:F1}) r{FrameInputs.LocalLightRange[i]:F1}");
        }
        _log.Info($"SceneInputs: {FrameInputs.LocalLightCount} point light(s) recorded (slice 6):{sb}");
    }

}
