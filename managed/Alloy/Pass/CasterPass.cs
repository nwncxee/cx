using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>
/// The one Alloy submission path every rigid shadow caster goes through (ADR 0019). Tiles, placeables and
/// creatures are the same thing here -- a part with an indexed VDO, an index range and a model matrix. The
/// cube POPULATION is split in two, a static array baked on the predicate and a small per-frame dynamic pool
/// composed with min() in the receiver; a view re-bakes only when its depth stopped describing the world.
/// </summary>
public sealed unsafe partial class CasterPass : IExtenderService
{
    /// <summary>The live-list capacity IS the ownership table's: a caster is an ownership slot, so a smaller
    /// number here would silently bake a fraction of what the colour pass draws -- which reads as "shadows go
    /// missing in big areas", not as a cap.</summary>
    private const int MaxCasters = AlloyRenderer.MaxOwned;
    private const int LogEvery = 900, FirstLogAt = 120;
    /// <summary>Faces per cube.</summary>
    private const int CubeFaces = 6;
    /// <summary>A <c>DrawElementsIndirectCommand</c> is five uints -- count, instanceCount, firstIndex,
    /// baseVertex, baseInstance -- and the buffer is tightly packed, so the stride is 20 bytes.
    /// <c>firstIndex</c> is in INDICES, not bytes: the byte-offset APIs elsewhere in this repo multiply the
    /// VDO's element offset by 2 and this one must not.</summary>
    private const int CmdUints = 5, CmdBytes = CmdUints * 4;
    /// <summary>The bake reads the COLOUR path's per-draw records and its bone block, at the colour path's
    /// own binding points. There is no second record buffer and no second bone ring.</summary>
    private const uint BindDraw = AlloyRenderer.BindDraw, BindBones = AlloyRenderer.BindBones;
    /// <summary>Slope of the cone that circumscribes a 90deg cube face -- its corners sit at
    /// <c>|perp| = sqrt(2) * depth</c>. Deliberately a HAIR above sqrt(2): a caster exactly on the diagonal
    /// makes the comparison an equality, and in float <c>MathF.Sqrt(2)^2 == 1.9999999</c>, so the true
    /// sqrt(2) rejects the very corner it is meant to keep.</summary>
    internal const float FaceConeSlope = 1.4143f;
    internal const uint GlUnsignedShort = 0x1403;   // not-an-offset: GL enum

    /// <summary>The cadence LADDER, not a blanket rate: cube ranks below <c>/shadow dynfull</c> and sun
    /// cascades below <c>/shadow sunfull</c> bake their dynamic half every frame, the rest every other frame
    /// with the parity staggered. A roam, an invalidation, a re-key or an unbaked page never skips; events on
    /// a skipped frame are carried in the page's pending mask and baked the next.</summary>
    private static int _dynFull => Shadows.DynFull.V;
    /// <summary><c>/shadow staticcap N</c>: static cube slot re-bakes per frame (0 = unbounded). A held slot
    /// republishes its standing page and stays stale -- its mover evidence carries in <c>_cubeHadMover</c> --
    /// so it bakes on a later frame, nearest rank first. Re-keys and blanket invalidates never hold: six
    /// 512px faces of the whole static world per slot is the single biggest hitch term the ledger read.</summary>
    private static int _staticCap => Shadows.StaticCap.V;
    private static long _cubeDynSkipped, _sunDynSkipped;
    private const int AllFaces = (1 << CubeFaces) - 1;
    /// <summary>The sun's dynamic layer holds a bake for the fit matrix beside it, so a frame with
    /// dynamic casters in the fit but no event among them samples it without re-baking.</summary>
    private static readonly bool[] _sunDynValid = new bool[MaxCascades];
    private static readonly Matrix4x4[] _sunDynVp = new Matrix4x4[MaxCascades];
    /// <summary>The A/B: 0 = every dynamic view re-bakes every frame it has dynamic casters.</summary>
    private static bool _dynEvents => Shadows.DynEvents.V;
    /// <summary>Dynamic pages for the nearest <c>/shadow dynranks</c> cube lights only (rank = the
    /// nearest-first cube order). A far torch keeps its static cube and shadows no mover; with every face
    /// of every page stale every frame (the tileset's animated props), the page count is the view count.
    /// Boot = every cube.</summary>
    private static int _dynRanks => Shadows.DynRanks.V;
    private static long _cubeDynBakedFaces;
    /// <summary>Pair tiles this period's frames baked, for the period line's clause.</summary>
    private static long _pairBakedTiles;
    /// <summary>Slot is in COMBINED mode: it has movers but no pool page (pool exhausted), so it bakes both
    /// halves into the static array every frame -- the per-slot fallback to pre-split behaviour. A mode
    /// change re-keys the slot, or a mover baked in while combined would outlive the mode.</summary>
    private static readonly bool[] _cubeCombined = new bool[MaxCubes];
    /// <summary>Frame each OWNERSHIP slot was last a dynamic caster on -- the set-transition detector. A
    /// caster whose membership flips (starts or stops moving) is a static-set event: the static bake must
    /// shed it or adopt it, within one predicate cycle.</summary>
    private static readonly int[] _dynStamp = new int[MaxCasters];
    private static long _cubeDynBakedPages;

    /// <summary>Bakes BY REASON, [family * 3 + reason] over families sun=0/cube=1 and the first term of
    /// <see cref="ViewStale"/> that fired -- blanket=0, key=1, mover=2. A SKIP needs no such breakdown: it is
    /// the conjunction, every term false at once, so the skip counts already say it. Which term is paying for
    /// the bakes is what they cannot say, and a re-fitting cascade and a room with one creature differ.</summary>
    private static readonly long[] _bakeWhy = new long[6];
    private const int WhySun = 0, WhyCube = 3;

    /// <summary>Charge one bake to the first <see cref="ViewStale"/> term that fired; the order matches the
    /// predicate's own short-circuit, so the charge is the reason a reader would name.</summary>
    private static void ChargeBake(int family, bool invalidate, bool keyChanged)
        => _bakeWhy[family + (invalidate ? 0 : keyChanged ? 1 : 2)]++;

    /// <summary>Presented frames since arming; the freshness clock for <see cref="CascadesFresh"/>.</summary>
    private static int _frameSeq;
    private static int _cascadeStamp = -1, _cubeStamp = -1;

    private static ExtenderLog? _log;
    private static bool _armed, _disarmed;
    private static uint* _primTable;

    // --- lifecycle + diagnostics
    private static readonly EdgeState _was = new();
    private static bool _loggedOnce, _noSunLogged;
    private static int _frames;
    private static long _mdiCalls;
    /// <summary>CPU ticks the bake's own 4.6 context bracket costs, printed on the period line. The view's
    /// bracket is open by the time the bake runs (ADR 0032), so this pair is a depth bump and the figure
    /// reads ~0; it stays because the view's draw runs its passes even when the view's own Enter was
    /// refused, which makes each pass its own gate. A non-zero figure is that case happening.</summary>
    private static long _ctxTicks, _ctxFrames;
    private static long _cubeLightsSum;

    /// <summary>Views rendered on the last frame -- the views the re-bake predicate found STALE, not every
    /// view the budget holds (<c>/shadow rebake 0</c> puts it back to all of them) -- plus the CPU
    /// milliseconds the shadow slot took. Those are CPU SUBMISSION only; <see cref="CasterGlState.QShadow"/> measures the
    /// GPU side.</summary>
    internal static int LastViews;
    internal static float LastShadowMs;
    /// <summary>The period's average bake GPU time, for the [alloy] gpu total.</summary>
    internal static double ShadowGpuMs => _gl.QShadow.Measured ? _gl.QShadow.Avg / 1e6 : 0.0;
    /// <summary>The same meter's running totals, for a caller taking its OWN window rather than the period's
    /// -- <c>/alloy bench</c>, whose steps are far shorter than a period line. The bake is the one GPU cost
    /// that is not a <see cref="AlloyRenderer.GpuTerm"/>, and <c>lightmap.movers</c> moves it (the pair
    /// tiles), so a bench step blind to it cannot split what that knob removes.</summary>
    internal static ulong ShadowGpuSum => _gl.QShadow.Sum;
    internal static int ShadowGpuN => _gl.QShadow.N;
    private static long _viewSum;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        if (!Shadows.Casters.Boot)
        { log.Info("CasterPass: stood down (shadow.casters=0)"); return; }
        if (!SelfCheck(log)) return;

        // Restart knobs, read through Boot: every one of these sizes an allocation or gates the arming, so
        // it is a boot decision. The look knobs -- cascade distance and friends -- are the group's Live rows.
        _mapSize = Shadows.Size.Boot;
        _cascades = Shadows.Cascades.Boot;
        _cubeLights = Shadows.Cubes.Boot;
        _cubeSize = Shadows.CubeSize.Boot;
        // #305: the dynamic pool's shape. The page default is the cube budget itself: the first live run
        // saturated a 16-page pool in a 32-cube street and every overflow slot fell back to the combined bake
        // -- the exact static-bake churn the split exists to remove, for 24 MB saved. Shrink shadow.dyncount
        // on a small card.
        _cubeDynSize = Shadows.DynSize.Boot;
        _cubeDynPages = Shadows.DynCount.Boot;
        // #306: the sun's half of the split sizes an allocation too (a cascades x size mirror).
        _sunSplitCfg = Shadows.SunDyn.Boot;
        _dynPageSlot = new int[Math.Max(_cubeDynPages, 1)];
        _dynPageMask = new int[_dynPageSlot.Length];
        _dynPageValid = new bool[_dynPageSlot.Length];
        Array.Fill(_dynPageSlot, -1);
        Array.Fill(_dynPageOf, -1);

        _primTable = (uint*)Native.Sym(SymbolMap.PrimTypeTranslationTable);
        if (_primTable == null)
        { log.Info("CasterPass: primitive-type table unresolved -- Alloy caster submission unavailable"); return; }

        // Repack moves a VDO's item indices and offsets under us. Every other consumer of these tables
        // already invalidates on it (#88 chain: draw funnels 50, interim cube 40, Alloy colour 30) and this pass
        // must too -- a stale item index does not read as an empty draw, it reads as ANOTHER part's index
        // range, which submits out-of-range indices to the GPU.
        _repackSlot = hooks.ChainVdoRepack(&OnVdoRepack, RepackPriority);
        if (_repackSlot == null)
        { log.Info("CasterPass: could not chain VdoRepack -- refusing to arm on stale index ranges"); return; }

        CasterGather.Init(log);

        _armed = true;
        log.Info($"CasterPass: armed -- Alloy multidraw-indirect caster submission into a {_cascades}x{_mapSize}" +
                 $" sun cascade array out to {_maxDist:F0}u (shadow.cascades/shadow.size, /shadow dist)" +
                 $" and {_cubeLights}x{_cubeSize} local-light shadow cubes (shadow.cubes/shadow.cubesize)" +
                 ", posed casters bake from Alloy's own bone block" +
                 "; needs the part table, so unarmed draw funnels leave it with nothing to enroll");
    }

    /// <summary>The whole re-bake decision for one VIEW, as four independent reasons a recorded depth layer
    /// may no longer describe the world -- a blanket invalidate, the view's own key changing, a mover in it
    /// now, a mover in it last frame -- pulled out so <see cref="SelfCheck"/> can pin each one. A cube face
    /// and a cascade differ only in which volume the mover test runs against.</summary>
    internal static bool ViewStale(bool invalidate, bool keyChanged, bool hadMover, bool hasMover)
        => invalidate || keyChanged || hadMover || hasMover;

    /// <summary>Does a sun bake count toward its layer's degrade window? Only when the STATIC half would have
    /// re-baked too: a combined layer also re-bakes for every creature walking in it, and counting those kept
    /// a far cascade combined (all 23k casters, every frame) for as long as anyone moved in a populated area.</summary>
    internal static bool DegradeCounts(bool invalidate, bool keyChanged, bool staticEvent)
        => invalidate || keyChanged || staticEvent;

    /// <summary>Is any of the first <paramref name="n"/> live casters in <paramref name="idx"/> inside the fit?</summary>
    private static bool AnyInFit(int[] idx, int n, Vector3 c, Vector3 dir, float radius, float margin)
    {
        for (int q = 0; q < n; q++)
        {
            int m = idx[q];
            if (InFit(_liveCx[m] - c.X, _liveCy[m] - c.Y, _liveCz[m] - c.Z, _liveR[m], dir, radius, radius + margin)) return true;
        }
        return false;
    }

    /// <summary>The part-table-wipe blanket -- the one edge with no per-slot story, because every slot dies
    /// at once over dead pointers. Ordinary arrivals and departures need no blanket: a slot is never
    /// re-tenanted within the scan that vacates it, so a departure lapses in place for the vanish sweep to
    /// ghost, and an arrival announces itself as an appeared mover when its record lands.</summary>
    internal static void ShadowInvalidate() => _invalidate = true;

    /// <summary>Does a sphere at <paramref name="dx"/>/<paramref name="dy"/>/<paramref name="dz"/> from the
    /// focus reach into a directional fit? The walk's own cull, term for term (perpendicular distance from
    /// the light axis against reach, distance ALONG it against the fit's extent), so the sun's staleness
    /// predicate and the sun's cull can only ever disagree in the direction that costs a bake.</summary>
    internal static bool InFit(float dx, float dy, float dz, float r, Vector3 dir, float reach, float along)
    {
        float t = dx * dir.X + dy * dir.Y + dz * dir.Z;
        float px = dx - t * dir.X, py = dy - t * dir.Y, pz = dz - t * dir.Z;
        return px * px + py * py + pz * pz <= (reach + r) * (reach + r) && MathF.Abs(t) <= along + r;
    }

    /// <summary>One caster's index range: the mesh arena's draw arguments, which is where every seated
    /// slot's geometry lives.</summary>
    private static bool CasterRange(in AlloyRenderer.CasterFacts f, out int idxCount, out uint first, out uint baseVert)
        => AlloyRenderer.DbCasterRange(f.Slot, out idxCount, out first, out baseVert);

    /// <summary>The world-caster bake, driven ahead of the colour pass that samples it. It runs at the frame
    /// anchor, BEFORE the colour build -- which is where the one-frame record lag comes from, and why
    /// <c>Enroll</c> (the RSP prologue, earlier still) must never move a tenancy stamp for a slot whose part
    /// did not change. Everything below runs inside the GL bracket.</summary>
    public static void OnPassSlot()
    {
        if (!_armed || _disarmed) return;
        _frameSeq++;   // before the early-outs: a frame that renders nothing must READ as stale, not as fresh
        // The overlay's view readout describes THIS frame, so it has to be zeroed ahead of every early-out
        // below -- a frozen count left over from the last frame that rendered reads as a pass still baking.
        LastViews = 0;
        if (_coverStamp == _frameSeq - 1) _coverFreshFrames++;
        if (_cascadeStamp == _frameSeq - 1 && CascadeCount > 0) _cascFreshFrames++;
        // #241, ahead of EVERY early-out below: a frame that does not collect leaves the memo's stamps a
        // frame behind, so the sweep would miss whatever vanished while it was not looking. Any stretch of
        // non-Alloy frames proves it -- the main menu and back is enough, and the first Alloy frame after
        // would otherwise republish the shadows the world had before the walk. #282: the flag now simply
        // survives, because only a complete frame below clears it.
        Lifecycle();
        // AFTER the period line, which reports it: zeroing the timing ahead of Lifecycle made every period
        // line read 0.00 ms, since the frame's own measurement lands further down.
        LastShadowMs = 0f;
        if (!AlloyGate.Alloy || !GameGate.InModule || !GlContextService.Available) return;
        // #302: no enrollment of its own. The population is Alloy's ownership table, scanned on Alloy's
        // cadence -- there is one scan now (#304 deleted the ShadowEnroll zone that separated the two).
        // An empty table is not "nothing to do": whatever was casting last frame has stopped, and leaving
        // the recorded views alone would freeze its shadows in place. Collect's own sweep is what says so,
        // so the empty case runs the collect and then has no views to fill.
        if (AlloyRenderer.OrderCount == 0 && _prevCount == 0) return;

        // #308 follow-up: the pose solve moves to the FRONT of the frame, so the bake below skins with the
        // same bones the colour pass will draw with -- self-shadow flicker was the two disagreeing by one
        // frame. Before Collect, which reads the bone slots this assigns.
        AlloyRenderer.ShadowPose();
        // Collect FIRST: resolving the world is a per-frame fact, not a property of any one view, and #94/#95
        // submit several views off the one collect. #328: the WORLD is re-collected only on an edge -- a
        // caster-generation or record-epoch bump, a repack or release (both reset the generation), or a
        // frame the bracket skipped (every stamp would read as an appearance). Every other frame walks the
        // standing list for its movers, which is two orders of magnitude fewer than the casters.
        bool fresh = _liveGen == AlloyRenderer.CasterGen && _liveEpoch == AlloyRenderer.RecordEpoch
                     && _collectedAt == _frameSeq - 1;
        if (!fresh)
        {
            FrameZones.Count(_liveGen != AlloyRenderer.CasterGen ? FrameZones.Ev.CollectGen
                           : _liveEpoch != AlloyRenderer.RecordEpoch ? FrameZones.Ev.CollectEpoch : FrameZones.Ev.CollectSkip);
            Collect();
        }
        else if (!Refresh()) { FrameZones.Count(FrameZones.Ev.CollectRefresh); Collect(); }
        if (_dumpMovers) { _dumpMovers = false; DumpMovers(fresh); }

        // The sun is the only view with an exterior precondition. The cubes light interiors -- which is where
        // the AC lives (torchlight occluded by walls) -- so they must NOT sit behind this gate.
        bool sun = FrameInputs.HasSun && FrameInputs.HasCamera && !FrameInputs.Interior;
        if (!sun && !_noSunLogged)
        {   // interiors have no sun: a normal state, not a fault. HasSun latches for the SESSION, so the
            // interior test is what stops an interior sampling the last exterior's cascades -- the stamp then
            // goes stale and the receiver drops the sun shadow term.
            _noSunLogged = true;
            _log?.Info("CasterPass: no sun captured yet or interior area -- the sun cascades idle, cubes carry on");
        }
        // The published shadow focus (the camera-follow target, i.e. the PC), falling back to the camera
        // itself. NOT ShadowMath.CameraGroundFocus: marching the camera forward needs the SCENE camera's
        // rotation, and the captured rotation is whatever the last SetViewTransform left -- observed as a
        // UI/ortho pass, whose forward marched the focus to (0, 0, 766) and put every caster off-axis. That
        // is the 100%-cull the live log showed. Whether the #118 pass slot changes that is unverified; the
        // published focus is right either way, so nothing here depends on the answer.
        // ponytail: Focus is published by SceneInputs, which the #98 cutover KEPT -- it renders nothing,
        // it is the feed this pass reads. The camera fallback stands if that walk ever moves in here.
        Vector3 focus = FrameInputs.HasFocus ? FrameInputs.Focus : FrameInputs.CameraPos;

        // An Enter failure must not leave the flag promising an upload that never happened -- the colour
        // build would then skip BOTH the solve and the upload and skin against last frame's GPU block.
        // Normally a depth bump: the world view's bracket is open (ADR 0032), and a real switch here means
        // the view's own Enter was refused -- _ctxTicks on the period line is what says which.
        long tCtx = System.Diagnostics.Stopwatch.GetTimestamp();
        bool entered = GlContextService.Enter();
        _ctxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tCtx;
        if (!entered) { AlloyRenderer.PosedByShadow = false; return; }
        AlloyRenderer.Step("shadow bracket: the pose upload");
        AlloyRenderer.ShadowPoseUpload();   // the GL half of ShadowPose above: this frame's bones, uploaded
        AlloyRenderer.Step("shadow bracket: dead handles and the bake's handle upload");
        OwnedTextures.DropDeadHandles();        // the colour drain is AFTER this bracket; textures die on area load before both
        Issue.UploadHandlesForBake();   // a handle dropped since the last colour upload must not be sampled below
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _gl.QShadowOn = _gl.QShadow.Begin(Gl.TIME_ELAPSED);
        // #286: ONE staging for the whole frame, after the bones (the skin drop is a per-frame fact) and
        // inside the bracket (the mask resolve issues Alloy's texture copy). Every view below is then a
        // uniform block and a dispatch. Stand it down first: a frame that does not stage must not leave the
        // last frame's records armed, which would bake a world that has since moved.
        CasterGather.Stand();
        CasterGatherVk.Stand();
        _gStaged = false;
        _qN = 0;
        // the Vulkan bake's own recording for this frame, which the colour frame appends ahead of its passes
        bool onVk = Renderer.OnVulkan && VkBegin(_log!);
        // ...and GL's half of an A/B frame beside it: VkParity's frame starts at this bracket, so the one
        // question the comparison needs -- is this the A/B frame? -- is answerable here
        if (Renderer.OnVulkan) VkAbArm();
        ReceiverFrame();
        if (sun || (_cubeLights > 0 && FrameInputs.LocalLightCount > 0) || (sun && Weather.CoverWanted) || LightmapBake.WantsGather)
        {
            AlloyRenderer.Step("shadow bracket: caster programs and the gather staging");
            if (Renderer.OnVulkan) { if (onVk) { EnsureHandleMask(); _gStaged = GatherStage(); } }
            else if (EnsureCommon() && CasterGather.Ensure()) { EnsureLayered(); EnsureHandleMask(); _gStaged = GatherStage(); }
            // #303: the compute dispatch is the ONLY selection path. A program that can never build would
            // otherwise idle here forever with the engine's stencil shadows already suppressed.
            else if (CasterGather.Failed) Disarm("the caster gather dispatch failed to build");
        }
        // #282: the blanket invalidate is read by BOTH passes and cleared by the cube scan, so it is snapshot
        // once here -- FrameGraph decides the order of the two, and whichever ran second would otherwise miss it.
        _frameInvalidate = _invalidate || !_rebake;
        _sunRan = _cubeRan = false;
        // #117: the shadow bracket's passes come off FrameGraph, not from the order these two lines sit in.
        foreach ((PassId id, Bracket bracket) in FrameGraph.Passes)
        {
            if (bracket != Bracket.Shadow) continue;
            AlloyRenderer.Step("shadow bracket: " + AlloyRenderer.PassName(id));
            switch (id)
            {
                case PassId.SunCascades:
                    // on Vulkan the arrays are the bake's own, made at the first view that names one
                    if (!sun) break;
                    VkAbTargets(sun: true, cubes: false);
                    if (Renderer.OnVulkan ? onVk : EnsureSun()) SunPass(focus);
                    break;
                case PassId.LocalLightCubes:
                    if (_cubeLights <= 0 || FrameInputs.LocalLightCount <= 0) break;
                    VkAbTargets(sun: false, cubes: true);
                    if (Renderer.OnVulkan ? onVk : EnsureCubes()) CubePass(focus);
                    break;
                case PassId.RainCover:
                    CoverPass(focus, sun);
                    break;
                case PassId.SkyMap:
                    SkyPass(sun);
                    break;
            }
        }
        // the lightmap bake's static cubes for this frame's light steps, queued with the frame's own views
        if (onVk && _gStaged) LightmapBake.VkQueueCubes();
        // #307: the queued stale views flush in one layered pass per target, HERE -- the publish arrays the
        // passes wrote above assumed the bakes land. A failed flush publishes nothing for the frame instead:
        // the receiver samples no maps (a one-frame blink, never a lie) and the blanket re-bakes from scratch.
        AlloyRenderer.Step("shadow bracket: the layered flush");
        if (!FlushLayered())
        {
            _layerFlushFails++;
            CascadeCount = 0; CubeCount = 0; CoverRelease(); SkyRelease();
            Array.Clear(SunDynMask); Array.Clear(CubeDynPage);
            PairShadows.Stand();   // the pair tiles never landed either: no light may be read as paired this frame
            _invalidate = true; _sunScanned = false; _cubeScanned = false;
        }
        // ...and the grass shadows GL's own bake adds, which the Vulkan bake has no twin for yet: an A/B frame
        // leaves them out, or GL's lit colour would carry a shadow Vulkan's cannot
        else if (_gStaged && _gl.Fbo != 0 && !VkParity.Active)
        {   // #196: the combined (no-split) static layer, or the dynamic mirror -- never both, or the frozen
            // static copy and the live one compose into a double shadow
            Gl.Enable(Gl.DEPTH_CLAMP);
            GrassField.DrawShadows(_gl.Fbo, _gl.DepthTex, _mapSize, _sunRebaked, CascadeCount);
            GrassField.DrawShadows(_gl.Fbo, _gl.SunDynTex, _mapSize, _sunDynRebaked, CascadeCount);
        }
        CoverFiltered();
        SkyBaked();
        Array.Clear(_sunRebaked); Array.Clear(_sunDynRebaked);
        // ...and only now may the frame's invalidation be consumed: every pass that ran reached every one of
        // its views, so each of them recorded this frame's movers against its own slots.
        if ((_sunRan || _cubeRan) && (!_sunRan || _sunScanned) && (!_cubeRan || _cubeScanned))
        { _invalidate = false; _rxPending = false; }
        // #303: the culled-vs-drawn sample -- a latent copy of the last view's survivor counts, read back
        // frames later, so the figure reaches the period line without a readback on the draw path.
        CasterGather.Sample();
        if (_gl.QShadowOn) { _gl.QShadow.End(Gl.TIME_ELAPSED); _gl.QShadowOn = false; }
        LastShadowMs = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000f / System.Diagnostics.Stopwatch.Frequency;
        _viewSum += LastViews;
        Gl.Disable(Gl.DEPTH_CLAMP);
        tCtx = System.Diagnostics.Stopwatch.GetTimestamp();
        GlContextService.Leave();
        _ctxTicks += System.Diagnostics.Stopwatch.GetTimestamp() - tCtx;
        _ctxFrames++;
    }

    /// <summary>Cold diagnostic, once per period: the six widest live casters by stored sphere, and how many live
    /// casters belong to a scene that is not a world scene -- the population a whole-map shadow would come from.</summary>
    private static void LogLargestCasters()
    {
        Span<int> top = stackalloc int[6];
        top.Fill(-1);
        int foreign = 0;
        for (int q = 0; q < _liveCount; q++)
        {
            if (AlloyRenderer.CasterNotWorld(_liveSlot[q])) foreign++;
            float r = _liveR[q];
            for (int t = 0; t < top.Length; t++)
            {
                if (top[t] >= 0 && _liveR[top[t]] >= r) continue;
                for (int u = top.Length - 1; u > t; u--) top[u] = top[u - 1];
                top[t] = q;
                break;
            }
        }
        var sb = new System.Text.StringBuilder($"CasterPass: [largest] {_liveCount} live, {foreign} outside a world scene, cover fresh {_coverFreshFrames}/{_frames} frames, cascades fresh {_cascFreshFrames}/{_frames}");
        _coverFreshFrames = 0; _cascFreshFrames = 0;
        foreach (int q in top)
            if (q >= 0) sb.Append("\n    r=").Append(_liveR[q].ToString("F1")).Append(' ').Append(AlloyRenderer.DescribeCasterSlot(_liveSlot[q]));
        _log?.Info(sb.ToString());
    }
    private static int _coverFreshFrames, _cascFreshFrames;

    /// <summary>Module unload, a PartTable wipe and a lost 4.6 context all invalidate every recorded
    /// identity wholesale; the period line is the only evidence the pass runs at all. This runs from the pass
    /// slot, which need not fire outside a module, so TableGen is the load-bearing edge: DrawFunnelService
    /// ticks from the present unconditionally and wipes on module unload.</summary>
    private static void Lifecycle()
    {
        if (!GlContextService.Available) Release("4.6 context lost");
        if (_was.Available && !GlContextService.Available) { ContextLost(); CasterGather.ContextLost(); }
        _was.Available = GlContextService.Available;
        bool inModule = GameGate.InModule;
        if (_was.InModule && !inModule) Release("module unload");
        _was.InModule = inModule;

        int period = _loggedOnce ? LogEvery : FirstLogAt;
        if (++_frames < period) return;
        _loggedOnce = true;
        LogLargestCasters();
        _log?.Info($"CasterPass: [casters] {CascadeCount}/{_cascades} sun cascades at {_mapSize}px out to " +
                   $"{_maxDist:F0}u" + FitReadout() +
                   $", {(float)_cubeLightsSum / _frames:F1}/{_cubeLights} local cubes at {_cubeSize}px" +
                   $", casting off {AlloyRenderer.OrderCount} Alloy ownership slots" +
                   $", {(float)_cubeBakedSlots / _frames:F1} cube slots re-baked and " +
                   $"{(float)_cubeSkippedSlots / _frames:F1} skipped and " +
                   $"{(float)_cubeOffSlots / _frames:F1} off-screen and {(float)_cubeHeldSlots / _frames:F1} held by the static cap and " +
                   $"{(float)_cubeRoamSlots / _frames:F1} roaming per frame " +
                   $"({(float)_cubeTurnovers / _frames:F2} slot turnovers/frame, fade {_fadeFrames}f) " +
                   // #305: static and dynamic bakes SEPARATELY -- static bakes tracking the view count means
                   // the split bought nothing and the invalidation is too coarse; this line is that reading.
                   $"(split {(_split ? "on" : "OFF -- /shadow split 1")}: " +
                   $"{(float)_cubeBakedSlots / _frames:F1} static / {(float)_cubeDynBakedPages / _frames:F1} " +
                   $"dynamic cube bakes ({(float)_cubeDynBakedFaces / _frames:F1} faces, {(float)_cubeDynSkipped / _frames:F1} pages held by cadence) per frame, dyn pool {DynPagesUsed()}/{_cubeDynPages} pages at " +
                   $"{_cubeDynSize}px = {_cubeDynPages * CubeFaces * (long)_cubeDynSize * _cubeDynSize * 4 / (1024 * 1024)} MB " +
                   $"beside the static array's {_cubeLights * CubeFaces * (long)_cubeSize * _cubeSize * 4 / (1024 * 1024)} MB) " +
                   // CX-188 step 4: the tiles that stood in for those pages; PairShadows.Stats says over how
                   // many lights, and PairDebt.Stats what the screen-space debt then cost.
                   (_pairBakedTiles > 0 ? $"({(float)_pairBakedTiles / _frames:F1} pair tiles BAKED/frame) " : "") +
                   $"(sun {(float)_sunBakedViews / _frames:F1} re-baked, " +
                   $"{(float)_sunSkippedViews / _frames:F1} skipped" +
                   // #306: the static re-bake rate PER CASCADE LAYER is the texel snap's measured reuse
                   // rate -- the number the toroidal-escalation verdict reads. A layer marked C degraded
                   // itself to the combined bake (static half stale essentially every frame), which is
                   // today's behaviour and not a wrong shadow.
                   (_gl.SunDynTex != 0
                    ? $"; static per layer {SunLayerReadout()} /frame, " +
                      $"{(float)_sunDynBakedViews / _frames:F1} dynamic bakes/frame ({(float)_sunDynSkipped / _frames:F1} held by cadence), " +
                      $"degraded-to-combined [{SunCombinedReadout()}]"
                    : "; sun split OFF (shadow.sundyn 0)") + ") " +
                   // #307: which submission path the views took, and what it cost.
                   (_layerDial
                    ? _layerProbe > 0 && _gl.ProgLayer != 0
                      ? $"(layered bake: {(float)CasterGather.LayerDispatches / _frames:F1} dispatches over " +
                        $"{(float)CasterGather.LayerViews / _frames:F1} views/frame, " +
                        $"{_gDrawN} staged draws in {_gGroups} batches ({MaskedBatches()} masked)" +
                        (_layerFlushFails > 0 ? $", {_layerFlushFails} flush FAILURES" : "") + ") "
                      : _layerProbe < 0
                        ? "(layered bake UNAVAILABLE -- no ARB_shader_viewport_layer_array, per-view path) "
                        : ""
                    : "(layered bake OFF -- /shadow layered 1) ") +
                   $"(bakes charged to -- sun: blanket {_bakeWhy[WhySun]}, key {_bakeWhy[WhySun + 1]}, " +
                   $"mover {_bakeWhy[WhySun + 2]}; cube: blanket {_bakeWhy[WhyCube]}, key {_bakeWhy[WhyCube + 1]}, " +
                   $"mover {_bakeWhy[WhyCube + 2]}) " +
                   $"(predicate {(_rebake ? "on" : "OFF -- /shadow rebake 1")}, " +
                   $"{(float)_moverSum / _frames:F1} movers/frame -- matrix {(float)_mvMatrix / _frames:F1} (r mean {(_mvMatrix > 0 ? _mvMatrixR / _mvMatrix : 0f):F2} max {_mvMatrixRMax:F2} m = '{(_mvMatrixRMaxSlot >= 0 ? AlloyRenderer.SlotTexNameDb(_mvMatrixRMaxSlot) : "")}', dynmin {Shadows.DynMin.V:F1}), " +
                   $"bones {(float)_mvBone / _frames:F1}, dangly {(float)_mvDangly / _frames:F1}, " +
                   $"appeared {(float)_mvAppear / _frames:F1}, " +
                   $"{(float)_ghostSum / _frames:F1} vanished, " +
                   $"{(float)_noRecordSum / _frames:F1} refused for want of a record" +
                   (_degenSum > 0 ? $", {(float)_degenSum / _frames:F1} DEGENERATE-admitted movers defeating the predicate" : "") +
                   (_nanMovers > 0 ? $", {_nanMovers} NON-FINITE mover sphere(s) kept out of the hot path (must be 0)" : "") + ")" +
                   // ...of how many live casters. Movers are a SUBSET of the live list, never the whole of
                   // it: a still caster still casts. The two numbers meeting is the #241 regression.
                   $" out of {(float)_liveSum / _frames:F1} live " +
                   $"({(float)(_liveSum - _moverSum) / _frames:F1} still)" +
                   // #328: the list is re-collected on a world edge and walked in between. This is the one
                   // number that tells a slow refresh from a churning edge (a fade, a flapping skip) when
                   // Bake reads high: it should be a few per period in a settled area.
                   $", the world re-collected {_collects} of {_frames} frames" +
                   // #336: what the refresh visited against the list it stands over. Tracks the mover count
                   // plus the colour build's changed records; a number near "live" means a source is firing
                   // on every caster (a dirty VDO the whole world shares, the overflow fallback, refreshall 1).
                   $", refresh visited {(float)_visitSum / _frames:F0} of {(float)_liveSum / _frames:F0} casters/frame " +
                   $"({(float)_touchedSum / _frames:F0} records touched by the colour build, dynamic hold {_dynHold} frames)" +
                   (_refreshAll ? " (refreshall 1: every caster)" : "") +
                   $", caster selection in compute over the shared CxCullSphere bounds " +
                   $"(cull {(CasterGather.Mode != 0 ? "on" : "OFF -- /shadow gather 1")})" +
                   (CasterGather.Views > 0
                    ? $" -- {(float)CasterGather.Views / _frames:F1} dispatches/frame over " +
                      $"{_gDrawN} staged draws in {_gGroups} batches ({MaskedBatches()} masked) " +
                      $"({CasterGather.UploadsSkipped}/{CasterGather.UploadsSkipped + CasterGather.Uploads} " +
                      "frame stagings matched the persistent source, nothing uploaded)" +
                      // S8: what a dynamic view's dispatch actually covered, against the staging it used to walk.
                      (_dSrcFrames > 0
                       ? $", dynamic source {(float)_dSrcDraws / _dSrcFrames:F0} of {_gDrawN} draws on " +
                         $"{CasterGather.DynDispatches} of {CasterGather.Views + CasterGather.LayerDispatches} dispatches" +
                         (_dSrcOver > 0 ? $", {_dSrcOver} frames over the {MaxDynSrc} cap (full source)" : "") +
                         (_dSrcHalf > 0 ? $", {_dSrcHalf} frames the movers were over half the staging" : "") +
                         $", audit {_dSrcAudits} frames {_dSrcMiss} miss (must be 0)" +
                         (_dSrcExtra > 0 ? $", {_dSrcExtra} still casters copied into the moving set" : "")
                       : Shadows.DynSrc.V ? ", dynamic source unused this period" : ", dynamic source OFF (/shadow dynsrc 1)")
                    : "") +
                   // #303: the culled-vs-drawn figure -- one view per frame, latently copied and read back
                   // frames later, so it costs no stall on the draw path.
                   (CasterGather.Samples > 0
                    ? $", sampled view kept {(float)CasterGather.SampledKept / CasterGather.Samples:F0}/" +
                      $"{(float)CasterGather.SampledStaged / CasterGather.Samples:F0} staged draws, " +
                      $"{(float)CasterGather.SampledRx / CasterGather.Samples:F0} receiver-culled " +
                      $"(latent sample; {_rxEpochs} camera epochs forced the dyn views)"
                    : "") +
                   // CX-50: ...and what those kept draws BAKED, per detail level -- the bake's twin of
                   // MeshLod.Drew, off the same latent sample. 0 at levels 1 and 2 outdoors means every
                   // view is still baking full detail.
                   SampledTriLine() +
                   // S8: the dynamic source's samples on their own -- averaging ~438 staged draws with ~37k
                   // would describe neither population.
                   (CasterGather.SamplesDyn > 0
                    ? $", on the dynamic source kept {(float)CasterGather.SampledKeptDyn / CasterGather.SamplesDyn:F0}/" +
                      $"{(float)CasterGather.SampledStagedDyn / CasterGather.SamplesDyn:F0} staged draws, " +
                      $"{(float)CasterGather.SampledRxDyn / CasterGather.SamplesDyn:F0} receiver-culled"
                    : "") +
                   // S3-4(c): what the bake's Enter/Leave pair costs, inside the ms-cpu figure beside it. Near
                   // zero means the world view's bracket was already open and this one was a depth bump.
                   (_ctxFrames > 0
                    ? $", 4.6 bracket {(double)_ctxTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / _ctxFrames:F3} ms/frame"
                    : "") +
                   $", {(float)_viewSum / _frames:F1} views baked per frame ({LastShadowMs:F2} ms cpu last" +
                   (_gl.QShadow.Measured ? $", {_gl.QShadow.Avg / 1e6:F3} ms GPU avg" : "") + ")" +
                   $", {(float)_mdiCalls / _frames:F1} multidraw-indirect calls/frame " +
                   $"({(float)_mdiSkipped / _frames:F1} skipped: the batch holds nothing the view's set admits)" +
                   // SL1: the per-frame staged-word rewrite walks the refresh's visited set, and one frame in
                   // StageAuditEvery re-reads every staged draw to prove it missed nothing.
                   $", staged-word audit {_gAudits} of {_frames} frames, {_gStaleWords} stale (must be 0)" +
                   $", {_liveCreatures} posed casters live" +
                   // The cutout casters: env-mapped materials the engine calls opaque without ever reading
                   // their diffuse alpha, so their depth draw runs the same 0.2 discard the colour pass does.
                   // Zero here with foliage on screen means they are all falling out at one of the three
                   // terms after it.
                   $", {_texWithAlpha}/{_texQueried} caster textures carry alpha -> " +
                   $"{_submitMasked} alpha-tested depth draws this period" +
                   // CX-227: and the coverage set inside it -- these cast at CoverageLayer.Cut, dithered,
                   // instead of the engine's 0.2, so a hair card's shadow follows what the card draws.
                   $", {CoverCasters()} coverage casters at the layer's own cut" +
                   $", {AlloyRenderer.HandlesZeroed} handle entries zeroed under a dying texture " +
                   $"({Db.TexRegistry.HandlesDropped} of the handles were the DB registry's own) and " +
                   $"{AlloyRenderer.BakeHandleUploads} bakes uploaded the handle table ahead of the colour bracket (since boot)" +
                   (_maskNoTex + _maskNoProg == 0 ? ""
                    : $" ({_maskNoTex} masked casters have no diffuse this frame, " +
                      $"{_maskNoProg} no cutout program -- each casts nothing)") +
                   SunRxReadout());
        _frames = 0; _mdiCalls = 0; _mdiSkipped = 0; _gAudits = 0; _gStaleWords = 0;
        _dSrcFrames = 0; _dSrcDraws = 0; _dSrcOver = 0; _dSrcAudits = 0; _dSrcMiss = 0; _dSrcHalf = 0; _dSrcExtra = 0;
        _ctxTicks = 0; _ctxFrames = 0;
        CasterGather.DynDispatches = 0;
        _cubeLightsSum = 0; _viewSum = 0;
        _liveSum = 0; _visitSum = 0; _touchedSum = 0; _gl.QShadow.Reset();
        _cubeBakedSlots = 0; _cubeSkippedSlots = 0; _cubeOffSlots = 0; _cubeHeldSlots = 0; _cubeRoamSlots = 0; _cubeTurnovers = 0; _moverSum = 0; _ghostSum = 0; _cubeDynBakedPages = 0; _cubeDynBakedFaces = 0; _pairBakedTiles = 0;
        _sunBakedViews = 0; _sunSkippedViews = 0;
        Array.Clear(_sunStaticL); _sunDynBakedViews = 0; _sunDynSkipped = 0; _cubeDynSkipped = 0;
        CasterGather.LayerDispatches = 0; CasterGather.LayerViews = 0;
        Array.Clear(_bakeWhy);
        _mvMatrix = 0; _mvMatrixR = 0; _mvMatrixRMax = 0; _mvMatrixRMaxSlot = -1; _mvDangly = 0; _mvBone = 0; _mvAppear = 0; _degenSum = 0; _nanMovers = 0; _noRecordSum = 0;
        _submitMasked = 0; _maskNoTex = 0; _maskNoProg = 0; _collects = 0;
        CasterGather.Views = 0; CasterGather.Batches = 0;
        CasterGather.SampledKeptDyn = 0; CasterGather.SampledStagedDyn = 0;
        CasterGather.SamplesDyn = 0; CasterGather.SampledRxDyn = 0;
        CasterGather.SampledKept = 0; CasterGather.SampledStaged = 0; CasterGather.Samples = 0; CasterGather.SampledRx = 0; _rxEpochs = 0;
        Array.Clear(CasterGather.SampledTris);
        CasterGather.UploadsSkipped = 0; CasterGather.Uploads = 0;
    }

}
