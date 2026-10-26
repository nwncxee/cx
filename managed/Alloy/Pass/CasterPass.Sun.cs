using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The sun cascades: the cascade array the receiver samples, the fit the player drags behind it, the
/// static/dynamic split that lets a texel-snapped layer stand, and the readouts that say which half went
/// stale.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>Cascade ceiling. The receiver's <c>uSunVp</c> array is sized from this, so raising it means
    /// raising both -- <see cref="SelfCheck"/> pins them together.</summary>
    internal const int MaxCascades = 4;
    /// <summary>Cascades rendered per frame (<c>shadow.cascades</c>, Boot).</summary>
    private static int _cascades = 4;
    /// <summary>Per-cascade face size in texels (<c>shadow.size</c>, Boot).</summary>
    private static int _mapSize = 2048;
    /// <summary>World units the outermost cascade reaches (<c>/shadow dist</c>). Sun shadow cost is bound by
    /// this and nothing else: the fit follows the player, so an area ten times the size costs the same.</summary>
    private static float _maxDist => Shadows.Dist.V;

    /// <summary>The depth array holding the cascades (0 = none yet). Read by the Alloy colour pass, in the
    /// same 4.6 context, on the frame after the render.</summary>
    internal static uint CascadeTex => _gl.CascadeTex;
    /// <summary>Cascades successfully rendered on the last pass, innermost first.</summary>
    internal static int CascadeCount;
    /// <summary>Row-major light view-projection per cascade, 16 floats each -- the matrix each layer was
    /// actually RENDERED with, snapshotted at submit. Map contents are world-space, so a frame of lag is
    /// invisible; a recomputed matrix that disagreed with the render would not be.</summary>
    internal static readonly float[] CascadeVp = new float[MaxCascades * 16];
    /// <summary>World units per shadow texel per cascade -- the receiver's normal-offset scale.</summary>
    internal static readonly float[] CascadeTexel = new float[MaxCascades];
    /// <summary>Depth range per cascade in world units (<c>2 * radius</c>: the upstream margin is depth-clamped,
    /// not ranged, see <see cref="ShadowMath.DirectionalLightViewProj"/>) -- what turns the receiver's world-unit depth bias
    /// into the device units its comparison runs in.</summary>
    internal static readonly float[] CascadeRange = new float[MaxCascades];

    private static int _sunFull => Shadows.SunFull.V;
    /// <summary><c>/shadow sunlatch N</c>: static cascade fit RE-LATCHES per frame (0 = unbounded). A layer
    /// whose re-latch waits keeps its published fit, which still contains the player by the slack's margin,
    /// and the receiver selects by that fit -- so nothing visible changes while the four spread out. The
    /// outermost is exempt. ponytail: a per-frame count, not a time budget.</summary>
    private static int _sunLatch => Shadows.SunLatch.V;

    /// <summary>The sun's half of the same bake state -- the snapped fit MATRIX is the key, the mover test
    /// the only other term. The fit keeps its LATCHED centre until the focus drifts a fraction of the
    /// cascade's radius: re-centring every frame re-keyed every cascade (the snap quantum is smaller than one
    /// frame of walking) and the hard handover swept across static geometry as a re-resolving ring.</summary>
    private static readonly float[] _sunFitC = new float[MaxCascades * 3];
    private static readonly bool[] _sunFitValid = new bool[MaxCascades];
    /// <summary>The sun's <see cref="ShadowMath.LatchUpX"/> latch.</summary>
    private static bool _sunUpX;
    /// <summary>Re-latch when the focus has drifted this fraction of the cascade radius from the latched
    /// centre. Small on purpose: the receiver's cross-fade weights come from the LIVE focus, and its
    /// full-handover point (90% of the fit radius) is only guaranteed to lie inside the latched fit while the
    /// drift stays under ~5%. The snap keeps the texel grid absolute, so a re-latch changes nothing visible.</summary>
    private const float SunFitSlack = 0.05f;
    /// <summary>The LIVE fit focus of the current sun pass -- what the receiver's cascade cross-fade keys
    /// on, precisely because it moves smoothly where the latched centres step.</summary>
    internal static Vector3 SunFocusLive;
    /// <summary>Per-cascade fit radius, published for the receiver's cross-fade band.</summary>
    internal static readonly float[] CascadeFitRadius = new float[MaxCascades];
    private static readonly bool[] _sunBaked = new bool[MaxCascades];
    /// <summary>Cascades whose static layer this frame re-baked -- the grass casters go in after the flush.</summary>
    private static readonly bool[] _sunRebaked = new bool[MaxCascades];
    private static readonly bool[] _sunDynRebaked = new bool[MaxCascades];
    private static readonly bool[] _sunHadMover = new bool[MaxCascades];
    private static long _sunBakedViews, _sunSkippedViews;

    // --- #306: the sun cascades join the static/dynamic split. The mechanism is the cubes' (#305): the
    // existing cascade array becomes the STATIC half, baked on the predicate; a same-shape dynamic array
    // holds movers only and re-bakes every frame; the receiver composes the two with min(). What makes a
    // cascade reusable at all is the texel snap -- ShadowMath now quantises the whole fit matrix, so the
    // static key is the MATRIX ITSELF and a sub-texel focus move reproduces it bit-identical. Whether the
    // snap actually buys reuse at walking speed is a measurement, not an argument: the period line reports
    // static re-bakes per cascade LAYER, and a layer whose static half is stale essentially every frame
    // degrades itself to the combined bake (today's behaviour, SetAll, no dynamic half) and says so.

    /// <summary>The sun's dynamic depth array (0 = none), one layer per cascade at the SAME face size as the
    /// static array -- the near cascade must carry the player's own shadow at full quality, and a mirror is
    /// the shape with no per-layer resolution bookkeeping. <c>shadow.sundyn</c> 0 disables the sun split
    /// permanently (the cubes keep theirs).</summary>
    internal static uint SunDynTex => _gl.SunDynTex;
    /// <summary>Per cascade layer: 1 = the dynamic half holds a fresh mover bake this frame, 0 = sample the
    /// static half alone. The receiver's <c>uSunDyn</c>; zero is what a program that never sets it reads.</summary>
    internal static readonly float[] SunDynMask = new float[MaxCascades];
    /// <summary>The static half's bake key under the split: the fit matrix each layer was last baked with.
    /// Fully texel-quantised by the ShadowMath snap, so comparing it IS the "snapped centre moved" test,
    /// with the direction and fit folded in for free.</summary>
    private static readonly Matrix4x4[] _sunKeyVp = new Matrix4x4[MaxCascades];
    /// <summary>Which population the recorded static layer holds -- SetAll (combined) or SetStatic. A mode
    /// flip re-keys the layer, or a mover baked in while combined would outlive the mode.</summary>
    private static readonly bool[] _sunKeyCombined = new bool[MaxCascades];
    /// <summary>Layer is DEGRADED to the combined bake: its static half was stale on (nearly) every frame of
    /// the last window, so the split was paying two bakes for one. Decided per window, with hysteresis.</summary>
    private static readonly bool[] _sunCombined = new bool[MaxCascades];
    /// <summary>The degrade window: static bakes per layer over <see cref="SunWin"/> frames. Enter combined
    /// at &gt;= 90% of the window, leave at &lt;= 50% -- the gap is what stops a layer flapping at the edge.</summary>
    private const int SunWin = 120;
    private static readonly int[] _sunWinBaked = new int[MaxCascades];
    private static int _sunWinFrames;
    /// <summary>Static re-bakes per cascade LAYER this period -- the number that says what the texel snap's
    /// reuse rate actually is at walking speed (AC: a reading, not an argument).</summary>
    private static readonly long[] _sunStaticL = new long[MaxCascades];
    private static long _sunDynBakedViews;
    /// <summary><c>shadow.sundyn</c>: 0 = no sun dynamic array, the cascades keep the combined bake.</summary>
    private static bool _sunSplitCfg = true;

    /// <summary>The receiver epoch. The frustum a per-frame view was culled against goes stale as the camera
    /// moves, and a dyn face with no event in it HOLDS -- so the planes are built from the EPOCH's camera
    /// pose, pushed out by its worst-case drift, and the frame the live camera moves past that step or turns
    /// past that angle opens a new epoch and forces every dyn view until a frame's scans reach them all.</summary>
    private const float RxPosStep = 4f, RxAngle = 5f * MathF.PI / 180f;
    private static Vector3 _rxPos, _rxFwd;
    private static float _rxFar, _rxFov, _rxAspect;
    private static bool _rxValid, _rxForce, _rxPending;
    /// <summary>Receiver epochs this period -- forced dyn re-bakes charged to the camera.</summary>
    private static long _rxEpochs;

    /// <summary>Once per frame, ahead of the passes -- publish this frame's receiver planes and decide
    /// whether the epoch moved.</summary>
    private static void ReceiverFrame()
    {
        Vector3 pos = FrameInputs.SceneCamPos, fwd = FrameInputs.SceneCamFwd;
        float far = FrameInputs.CamFar, fov = FrameInputs.CamFovy, aspect = FrameInputs.CamAspect;
        if (!FrameInputs.HasSceneCam || !FrameInputs.HasPerspective || fwd.LengthSquared() < 1e-8f) fwd = Vector3.Zero;
        else fwd = Vector3.Normalize(fwd);
        bool moved = !_rxValid || (pos - _rxPos).LengthSquared() > RxPosStep * RxPosStep
                     || Vector3.Dot(fwd, _rxFwd) < MathF.Cos(RxAngle) || far != _rxFar || fov != _rxFov || aspect != _rxAspect;
        if (moved) { _rxPos = pos; _rxFwd = fwd; _rxFar = far; _rxFov = fov; _rxAspect = aspect; _rxValid = true; _rxEpochs++; _rxPending = true; }
        _rxForce = _rxPending;
        // a degenerate (no-camera) forward writes planes that keep everything
        ShadowMath.ReceiverPlanes(_rxPos, _rxFwd, _rxFov, _rxAspect, FrameInputs.CamNear, _rxFar, RxPosStep + _rxFar * RxAngle, CasterGather.Planes);
    }

    /// <summary>May the colour pass sample the cascades right now? Only when the array was rendered on the
    /// most recent frame tick. That one test covers every way the maps go stale at once: an interior (no sun
    /// -&gt; no render), a vanilla stretch (the pass is gated on <see cref="AlloyGate.Alloy"/>, so the toggle
    /// reverts sun shadows on the very next frame), and a disarm.</summary>
    internal static bool CascadesFresh
        => CascadeCount > 0 && (Renderer.OnVulkan ? !_vk.Cascades.IsNull : _gl.CascadeTex != 0) && _cascadeStamp == _frameSeq;

    /// <summary>How far every cascade's fit reaches TOWARD the light, past its own extent: the full max
    /// shadow distance for all of them, NOT each one's radius. A caster only has to be up-sun of a fragment
    /// to shade it, and innermost-first selection means the fragment takes cascade 0's answer -- so scaling
    /// this per cascade would drop a wall 40u up-sun out of the only cascade the player's feet ever ask.</summary>
    private static float FitMargin() => _maxDist;

    /// <summary>The sun cascades: one concentric fit per layer around the shadow focus, every one of them
    /// baked from the live fit every frame. Cascades are rendered innermost first so a fence stall costs the
    /// FAR ones, whose absence is a missing distant shadow rather than a missing one at the player's feet.</summary>
    private static void SunPass(Vector3 focus)
    {
        Vector3 dir = Vector3.Normalize(FrameInputs.SunDir);
        SunFocusLive = focus;
        int done = 0, latched = 0;
        bool invalidate = _frameInvalidate;
        _sunRan = true; _sunScanned = true;
        // #306: the split runs only when its array exists -- shadow.sundyn 0 or a refused allocation is the
        // combined bake everywhere, exactly as /shadow split 0 spells it.
        bool split = _split && _gl.SunDynTex != 0;
        // The degrade window ticks once per sun frame. A layer whose static half re-baked on >= 90% of the
        // window's frames is paying two bakes where one combined bake would do; it degrades to today's
        // behaviour until the rate drops back under half (walking stops), and the period line says so.
        if (split && ++_sunWinFrames >= SunWin)
        {
            for (int c = 0; c < _cascades; c++)
                _sunCombined[c] = _sunWinBaked[c] >= (_sunCombined[c] ? SunWin / 2 : SunWin * 9 / 10);
            Array.Clear(_sunWinBaked);
            _sunWinFrames = 0;
        }
        bool upX = ShadowMath.LatchUpX(ref _sunUpX, dir);
        for (int i = 0; i < _cascades; i++)
        {
            SunDynMask[i] = 0f;
            float radius = ShadowMath.CascadeRadius(i, _cascades, _maxDist);
            CascadeFitRadius[i] = radius;
            float margin = FitMargin();
            // The latched centre, re-latched only when the focus has drifted past the slack. The receiver
            // selects a cascade by testing the published fit itself, so a latched fit needs no other
            // coordination -- the player's own shadow stays in cascade 0 as long as the drift stays well
            // inside the acceptance border, which the slack guarantees by an order of magnitude.
            Vector3 c = new Vector3(_sunFitC[i * 3], _sunFitC[i * 3 + 1], _sunFitC[i * 3 + 2]);
            // Inner cascades are contained in the next one up, so their drift only shifts which RESOLUTION
            // answers -- but past the outermost there is no shadow at all (the receiver returns lit), so a
            // trailing outermost fit opens an unshadowed band ahead of a walking player. It latches on a
            // quarter of the slack: a few extra full-view bakes per minute of walking, no band worth seeing.
            float slack = radius * (i == _cascades - 1 ? SunFitSlack * 0.25f : SunFitSlack);
            if (!_sunFitValid[i]) c = focus;
            else if ((focus - c).LengthSquared() > slack * slack && (_sunLatch == 0 || latched < _sunLatch || i == _cascades - 1))
            { c = focus; latched++; FrameZones.Count(FrameZones.Ev.SunLatch); }
            // depth range only across the fit sphere: the upstream margin is the CULL's (RenderView, InFit), and
            // DEPTH_CLAMP pancakes what stands in it onto the near plane
            Matrix4x4 vp = ShadowMath.DirectionalLightViewProj(dir, c, radius, 0f, _mapSize, upX);
            bool combined = !split || _sunCombined[i];
            // #282 terms 1+2: the fit itself, as ONE compare on the snapped matrix for both modes. The
            // matrix is a pure function of (dir, latched centre, radius, margin, map size), so it covers
            // every raw term the old combined-mode key compared -- and comparing those raw floats is what
            // kept a degraded layer degraded forever: the raw focus moves every frame the snap would have
            // absorbed, so a combined layer could never stop baking and never re-earn the split.
            // A population flip (SetAll <-> SetStatic) re-keys the layer either way.
            bool keyChanged = !_sunBaked[i] || _sunKeyCombined[i] != combined || vp != _sunKeyVp[i];
            // #282 term 3: a mover inside the fit. NOT a sphere around the focus -- a cascade admits a
            // CYLINDER along the light axis, and a caster standing up-light of the fit shades into it. Same
            // split the walk's own directional cull makes (perpendicular distance vs distance along), so the
            // predicate can only be stale about geometry the view would have culled anyway.
            // #306: under the split, continuous motion lives in the dynamic layer and must not re-bake the
            // static one -- only STATIC-SET EVENTS (appearances, membership flips) and ghosts invalidate it.
            bool ghost = false;
            for (int q = 0; q < _ghostCount && !ghost; q++)
                ghost = InFit(_ghostCx[q] - c.X, _ghostCy[q] - c.Y, _ghostCz[q] - c.Z,
                              _ghostR[q], dir, radius, radius + margin);
            bool staticEvt = ghost || AnyInFit(_sMoverIdx, _sMoverCount, c, dir, radius, margin);
            bool hasMover = combined ? ghost || AnyInFit(_moverIdx, _moverCount, c, dir, radius, margin) : staticEvt;
            bool stale = ViewStale(invalidate, keyChanged, _sunHadMover[i], hasMover);
            _sunHadMover[i] = hasMover;
            if (!stale)
            {   // The layer still describes this world. Nothing to republish: the key is unchanged, so the
                // matrix and the two ranges below are recomputed bit-identical to the ones already published.
                _sunSkippedViews++;
                done++;
            }
            else
            {
                _sunBakedViews++; FrameZones.Count(FrameZones.Ev.SunStatic);
                if (split) _sunStaticL[i]++;
                if (split && DegradeCounts(invalidate, keyChanged, staticEvt)) _sunWinBaked[i]++;
                ChargeBake(WhySun, invalidate, keyChanged);
                bool viewOk = RenderView(CasterMap.SunStatic, _gl.DepthTex, _mapSize, i, &vp,
                                         dir, c, radius, margin, point: false,
                                         set: combined ? CasterGather.SetAll : CasterGather.SetStatic);
                // A cascade that stalled holds last frame's depth under this frame's key, and the cascades
                // after it were never scanned -- so their mover evidence is missing too. One blanket frame
                // covers both.
                if (!viewOk) { _sunBaked[i] = false; _sunScanned = false; break; }
                _sunBaked[i] = true; _sunRebaked[i] = combined;
                _sunKeyCombined[i] = combined;
                _sunKeyVp[i] = vp;
                // The latch commits only beside a successful bake: a stalled view must not leave the next
                // frame fitting against a centre no map was baked at.
                _sunFitC[i * 3] = c.X; _sunFitC[i * 3 + 1] = c.Y; _sunFitC[i * 3 + 2] = c.Z;
                _sunFitValid[i] = true;
                fixed (float* pub = CascadeVp.AsSpan(i * 16)) *(Matrix4x4*)pub = vp;
                CascadeTexel[i] = 2f * radius / _mapSize;
                CascadeRange[i] = 2f * radius;
                done++;
            }
            // #306: the dynamic layer bakes EVERY frame it has movers -- no predicate, its whole content is
            // the movers -- with the SAME fit matrix the published static layer carries (on a skip the
            // recompute above is bit-identical to it, courtesy of the snap), so the receiver's uv/layer/ref
            // carry over unchanged and min() composes the two exactly. No movers in the fit = mask 0, and
            // the receiver never samples the stale layer; a stalled dyn bake leaves the mask 0 too, which is
            // a mover shadow that blinks for a frame, never one that lies.
            if (!combined)
            {
                bool hasDyn = false;
                for (int q = 0; q < _dMoverCount && !hasDyn; q++)
                {
                    int m = _dMoverIdx[q];
                    hasDyn = InFit(_liveCx[m] - c.X, _liveCy[m] - c.Y, _liveCz[m] - c.Z,
                                   _liveR[m], dir, radius, radius + margin);
                }
                // #336: the layer's content is the dynamic SET, which changes on an EVENT in the fit -- a
                // mover, a membership flip, a ghost -- and on a fit matrix it was not baked under. A held-
                // still caster keeps the layer SAMPLED, not re-baked.
                // #196: grass casts into the dynamic layer (its sway is continuous motion), so a grass
                // cascade has movers by definition and re-bakes every frame
                bool grass = GrassField.CastsInto(i);
                hasDyn |= grass;
                if (!hasDyn || !_sunBaked[i]) _sunDynValid[i] = false;
                else if (_dynEvents && !invalidate && !_rxForce && !grass && _sunDynValid[i] && _sunDynVp[i] == vp
                         && !EventInFit(c, dir, radius, margin))
                    SunDynMask[i] = 1f;
                // The cadence ladder: a far cascade's dynamic layer holds one frame under the same fit.
                else if (i >= _sunFull && !invalidate && !_rxForce && _sunDynValid[i] && _sunDynVp[i] == vp && ((_frameSeq + i) & 1) != 0)
                { SunDynMask[i] = 1f; _sunDynSkipped++; }
                else if (RenderView(CasterMap.SunDyn, _gl.SunDynTex, _mapSize, i, &vp, dir, c, radius, margin, point: false,
                                    set: CasterGather.SetDynamic, receiver: true))
                { SunDynMask[i] = 1f; _sunDynBakedViews++; _sunDynValid[i] = true; _sunDynVp[i] = vp; _sunDynRebaked[i] = grass; }
                else _sunDynValid[i] = false;
            }
        }
        TraceFit(focus, dir, latched);
        _gl.CascadeTex = _gl.DepthTex;
        // CascadeCount is the ONLY thing bounding the receiver's read: a stall at cascade i leaves layers
        // >= i holding last frame's depth and CascadeVp[>= i] holding last frame's matrix, and the
        // receiver's loop simply never reaches them. Same invariant covers the uniform upload, which
        // sends only `done` matrices into a mat4[4] and leaves the tail stale on purpose.
        CascadeCount = done;
        if (done > 0) _cascadeStamp = _frameSeq;
    }

    /// <summary>The cascade fit's inputs, logged only when one of them MOVES (<c>shadow.trace</c>). A still
    /// scene changes none of them, so a line here per frame names what the shadows are following; the
    /// <c>still</c> count is the cadence, which is what separates "it alternates every other frame" from "it
    /// drifts".</summary>
    private static void TraceFit(Vector3 focus, Vector3 dir, int latched)
    {
        if (!Shadows.Trace.V || _traceLines >= TraceCap) return;
        var c0 = new Vector3(_sunFitC[0], _sunFitC[1], _sunFitC[2]);
        if ((focus - _traceFocus).LengthSquared() < 1e-10f && (dir - _traceDir).LengthSquared() < 1e-12f
            && (c0 - _traceC0).LengthSquared() < 1e-10f && _traceMovers == _moverCount + _dMoverCount)
        { _traceStill++; return; }
        _traceLines++;
        _log?.Info($"CasterPass: [fit] f={_frameSeq} still={_traceStill} focus=({focus.X:F3},{focus.Y:F3},{focus.Z:F3}) "
                   + $"dir=({dir.X:F4},{dir.Y:F4},{dir.Z:F4}) c0=({c0.X:F3},{c0.Y:F3},{c0.Z:F3}) latched={latched} "
                   + $"cam=({FrameInputs.CameraPos.X:F2},{FrameInputs.CameraPos.Y:F2},{FrameInputs.CameraPos.Z:F2}) "
                   + $"fwd=({FrameInputs.SceneCamFwd.X:F3},{FrameInputs.SceneCamFwd.Y:F3},{FrameInputs.SceneCamFwd.Z:F3}) "
                   + $"fovy={FrameInputs.CamFovy:F1} movers={_moverCount}/{_sMoverCount}/{_dMoverCount} ghosts={_ghostCount} "
                   + $"dyn0={SunDynMask[0]:F0} baked0={_sunBaked[0]} comb0={_sunCombined[0]}");
        _traceFocus = focus; _traceDir = dir; _traceC0 = c0; _traceMovers = _moverCount + _dMoverCount;
        _traceStill = 0;
    }

    private const int TraceCap = 600;
    private static int _traceLines, _traceStill, _traceMovers;
    private static Vector3 _traceFocus, _traceDir, _traceC0;

    private static float SmoothStep(float a, float b, float x)
    {
        float t = Math.Clamp((x - a) / (b - a), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    /// <summary>The two live numbers the fit decision rests on, both off the LATCHED scene camera and never
    /// the present-time capture: the scene's field of view, and the view DEPTH of the shadow focus. Concentric
    /// cascades centre cascade 0 ON the focus, while any frustum-split fit must spend cascade 0 on the slab
    /// nearest the camera -- the empty ground behind the PC. This line re-opens the question if it changes.</summary>
    private static string FitReadout()
    {
        if (!FrameInputs.HasSceneCam || !FrameInputs.HasFocus || !(FrameInputs.SceneCamP11 > 0f)) return "";
        float depth = Vector3.Dot(FrameInputs.Focus - FrameInputs.SceneCamPos, FrameInputs.SceneCamFwd);
        float fovy = 2f * MathF.Atan(1f / FrameInputs.SceneCamP11) * 180f / MathF.PI;
        return $" (scene cam fov {fovy:F0}deg, focus at view depth {depth:F1}u, concentric cascade 0 " +
               $"half-extent {ShadowMath.CascadeRadius(0, _cascades, _maxDist):F0}u)";
    }

    /// <summary>The sun RECEIVER's side of the publish, cold and once per period: the one clause that
    /// separates "no cascade was baked" from "the cascades were published and the receiver read none of
    /// them". A publication the receiver did not take is a gate fault; one it took against an empty map is a
    /// bake fault, and nothing else tells the two apart. It compares the PREVIOUS frame's stamp.</summary>
    private static string SunRxReadout()
        => $" | [sunrx] published={_cascadeStamp == _frameSeq - 1} count={CascadeCount} tex={_gl.CascadeTex} " +
           $"stamp={_cascadeStamp} seq={_frameSeq} interior={FrameInputs.Interior} hasSun={FrameInputs.HasSun} " +
           $"| c0 baked={_sunBaked[0]} fit={_sunFitValid[0]} mover={_sunHadMover[0]} " +
           $"c=({_sunFitC[0]:F1},{_sunFitC[1]:F1},{_sunFitC[2]:F1}) r={CascadeFitRadius[0]:F1} " +
           $"vpcol3=({CascadeVp[12]:F3},{CascadeVp[13]:F3},{CascadeVp[14]:F3},{CascadeVp[15]:F3}) " +
           $"dyn={SunDynMask[0]:F0} | {AlloyRenderer.SunRxNote()}";

    /// <summary>The per-layer static re-bake rates, "L0 0,2/L1 0,1/..." -- cold, period line only.</summary>
    private static string SunLayerReadout()
    {
        string s = "";
        for (int i = 0; i < _cascades; i++)
            s += (i > 0 ? "/" : "") + $"L{i} {(float)_sunStaticL[i] / _frames:F1}";
        return s;
    }

    /// <summary>Which layers are currently degraded to the combined bake, e.g. "CC--".</summary>
    private static string SunCombinedReadout()
    {
        string s = "";
        for (int i = 0; i < _cascades; i++) s += _sunCombined[i] ? "C" : "-";
        return s;
    }
}
