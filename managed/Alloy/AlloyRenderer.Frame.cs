using CX.Alloy.Db;
using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The FRAME DISPATCH. What the Alloy bracket does once per pass and in what order: the prepass, the
/// opaque block, the pass-slot walk that hands each ADR 0011 slot to its owner, and the probe capture that
/// rides the same staged batches. Hot: nothing on this path allocates.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The bail-out a pass takes AFTER the shim has already taken the engine's frame away. Nothing to
    /// hand back to, and the renderer is chosen at boot, so this is a HARD FAILURE: the reason is logged and
    /// put on screen naming the restart. A no-op before the shim's window, where returning really does leave
    /// the engine's own frame intact.</summary>
    private static void NoFrameBehindUs(string why)
    {
        AlloyGate.Fail(why);
    }

    /// <summary>Why this frame drew nothing: "the world is empty" needs its reason named on the period line
    /// rather than inferred from counters. One string assignment on a path that is already returning.</summary>
    internal static string DbNoDrawWhy = "";
    private static bool _saidLitTerms;
    private static long _dbNoDrawN;
    private static void DbNoDraw(string why)
    {
        DbNoDrawWhy = why;
        _dbNoDrawN++;
    }

    /// <summary>The DB-mode draw refusal, for the period line.</summary>
    internal static string DbNoDrawNote()
        => _dbNoDrawN == 0 ? "" : $" | NOT DRAWN x{_dbNoDrawN}: {DbNoDrawWhy}";

    /// <summary>The view this frame is drawing right now, taken from the published snapshot. Set by
    /// <c>SeamDraw</c> around one view's pass and cleared after; <see cref="DbViewValid"/> is the gate.</summary>
    internal static AlloyView DbView;
    /// <inheritdoc cref="DbView"/>
    internal static bool DbViewValid;

    internal static void DrawOwned()
    {
        long tAlloy = FrameZones.Now;
        PlaceableHold.Tick();   // after this frame's deletes and re-adds, before the draw that answers them
        try { DrawOwnedInner(); } finally { FrameZones.Add(FrameZones.Zone.Alloy, tAlloy); }
    }

    /// <summary>What <see cref="BeginOwnedFrame"/> hands the pass list: this pass's camera, the
    /// projection terms every stage fogs and lights from, and the verdicts the setup reached. A plain
    /// stack struct -- nothing on this path allocates.</summary>
    private struct OwnedFrame
    {
        internal Matrix4x4 View, Proj, Vp, VpClean;
        internal float Jx, Jy, Near, Far, P00, P11;
        internal int Groups;
        internal bool Perspective, LightingOn, Srgb;
    }

    /// <summary>Everything the pass list needs before its first slot runs -- the snapshot revoke, the target
    /// and viewport latch, the pose, this pass's camera, the own walk inside the 4.6 bracket and the batch
    /// build. False = the pass is over before it began, having already left the bracket; on true the caller
    /// owns the bracket and closes it. Hot: allocates nothing.</summary>
    private static bool BeginOwnedFrame(int* display, int* viewport, out OwnedFrame f)
    {
        f = default;
        long tSetup = FrameZones.Now, pose0 = FrameZones.Ticks(FrameZones.Zone.Pose);
        // #308 follow-up: consumed FIRST, above every early return -- a flag left standing across a refused
        // colour frame would make the NEXT frame's colour pass skip the pose against a shadow bracket that
        // may have sat that frame out, and every skinned part would draw a stale (or bind) pose.
        bool posedByShadow = PosedByShadow;
        PosedByShadow = false;
        // #129: last pass's snapshot is not this pass's. Revoked first thing, so every path out of here that
        // never reaches PassId.FbCopy leaves Alloy water disarmed -- and BucketGate's bucket-6 gate reads
        // the same flag, so water is simply not drawn on exactly those passes (#285: a hole, not vanilla).
        FrameSnapshot.Revoke();
        if (StreamRing.Refused) _batchUnavailable = true;   // CX-45: the ring failed the renderer (AlloyGate.Fail) when it was refused
        if (Programs._progFailed || _batchUnavailable) { DbNoDraw("program failed or multidraw unavailable"); return false; }   // EndFrame revokes + disarms; never pay the bracket for nothing
        // Checked BEFORE anything stamps OwnGen: BuildBatches marks parts as drawn, which is only honest while
        // the issue phase cannot fail. Missing entry points are the one way it could.
        if (Gl.MultiDrawElementsBaseVertex == null || Gl.BindBufferBase == null || !StreamRing.EntryPoints)
        {
            // #263: the verdict froze before this frame, so nothing re-reads DrawCapable. Say it here or the
            // world goes bare with no line at all.
            _batchUnavailable = true;
            AlloyGate.Fail("glMultiDrawElementsBaseVertex, glBindBufferBase or a stream-ring entry point (buffer storage, map, bind range, fence) is missing");
            DbNoDraw("a GL entry point is missing");
            return false;
        }

        // #257: where the frame goes is LATCHED, not interrogated. Every frame used to open with nine
        // GL queries -- viewport, framebuffer binding, four attachment params, sRGB, and the two clear
        // values -- because the engine owned the frame's output configuration and could change it under us.
        // Under the cutover it does not: the answer is the same every frame until something that CANNOT
        // happen silently happens (a resize, an area change, a context loss). So the queries run once per
        // such generation, and once every LogEvery frames they run again as an ASSERTION -- re-read,
        // compared, and any drift logged and adopted. What survives unchanged is the argument for reading
        // the clear values in ENGINE context at all: a GL share list shares objects, not per-context state,
        // so glClearColor is not queryable from inside the 4.6 bracket -- that context has its own.
        ref Target t = ref ThisFrameTarget();
        HoldPassTarget(in t);   // #375: the ONE read per pass; the post stack rebinds from this copy
        // #391: TWO viewports. `display` is the engine's -- where the resolve, the post stack and the
        // depth hand-back go, and what the 2D UI draws at. `viewport` is the RENDER one every owned pass
        // sizes from: the display scaled by alloy.scale, origin 0 (the owned targets are their own
        // allocations, so an offset would only mean drawing into their corner).
        display[0] = t.Vp0; display[1] = t.Vp1; display[2] = t.Vp2; display[3] = t.Vp3;
        viewport[0] = 0; viewport[1] = 0;
        viewport[2] = PostChain.RenderSize(display[2]); viewport[3] = PostChain.RenderSize(display[3]);
        bool srgb = t.Srgb;
        // #456: sized to the DEFAULT framebuffer, before any pass binds a target.
        PresentTarget.Ensure(display[2], display[3]);
        float* engClear = stackalloc float[4] { t.Clear0, t.Clear1, t.Clear2, t.Clear3 };
        float engClearDepth = t.ClearDepth;
        // ...and the refusals that need no bracket at all, ahead of the preamble they would otherwise
        // waste -- PoseSkinned alone solves every Alloy skinmesh's pose.
        if (!PostChain.Precheck(display)) { DbNoDraw("PostChain.Precheck refused (no HDR target for this viewport)"); return false; }
        // Only now: a pass about to hand the frame back would have the engine solve each of these poses
        // again on its own walk, so the solve is not worth doing for it. #262: the solve no longer enters
        // engine code at all, but it still reads engine animation state, so it stays on this side.
        // #308 follow-up: the shadow bracket poses first (ShadowPose) so the bake and this pass skin with
        // the SAME frame's bones; re-solving here would tear them apart again. The solve only runs here on
        // a frame the shadow bracket sat out (flag consumed at the top of this method).
        if (!posedByShadow)
        {
            long tPose = FrameZones.Now;
            PoseSkinned();
            FrameZones.Add(FrameZones.Zone.Pose, tPose);
        }

        // row-major v*M convention: clip = v * Model * View * Proj; raw bytes read column-major by GLSL are
        // the transpose, so uVP * d.model * vec4(aPos,1) is exact -- (M*V*P)^T = (V*P)^T * M^T, which is why
        // #84 can hoist view*proj to a per-program uniform and leave only the model matrix per draw.
        _sceneEnv = AlloyEnvMap.Present();

        // CX-109: the view and projection are ALLOY's, built at this view's Camera::RenderScene out of the
        // camera object's own fields rather than read back off m_aurMtxStack. view.cameraloop 0 is the A/B,
        // and AlloyCamera.Describe() prints the largest element difference against the engine's own pair.
        Matrix4x4 view, proj;
        // CX-120: in DB mode the view is the one the SNAPSHOT recorded for the view being drawn, not
        // AlloyCamera.WorldView. WorldView is measured at the engine's Camera::RenderScene, which does not run
        // in DB mode, and it is single-view by construction -- so every view, world or GUI panel, would be
        // drawn through one camera. SeamDraw sets this for the duration of one view's pass.
        if (DbViewValid)
        {
            view = DbView.View;
            proj = DbView.Proj;
        }
        else if (AlloyCamera.Owned)
        {
            ref readonly AlloyView wv = ref AlloyCamera.WorldView;
            // CX-45: no quiet fallback. A pass with no measured camera would draw the world through an
            // identity view -- geometry everywhere and nothing recognisable -- so it sits the frame out.
            if (!wv.Valid)
            { NoFrameBehindUs("Alloy has no measured world camera (Camera::RenderScene never reached a world scene)"); DbNoDraw("no measured world camera"); return false; }
            view = wv.View;
            proj = wv.Proj;
            // The parity instrument, at the ONE place both matrices are meant to describe the same camera.
            AlloyCamera.NoteParity();
        }
        else if (!AlloyCamera.EngineMatrices(out view, out proj))
        { NoFrameBehindUs("m_aurMtxStack is unresolved and view.cameraloop is off"); DbNoDraw("no matrix stack and cameraloop off"); return false; }
        // #389: the ONE site the projection is read, so the ONE site the TAA jitter goes in: an NDC offset is
        // a shear of the clip x/y by w, and in this row-vector convention w = z*M34, so the offset lands in
        // M31/M32 scaled by M34 (sign-agnostic). Every owned stage, the hosted stock text (_projMtx) and the
        // cull see the same jittered camera; the velocity pass takes it back out. Zero with TAA off.
        float jx = 0f, jy = 0f;
        Matrix4x4 vpClean = view * proj;
        if (PostChain.TaaOn && !_capturing)
        {
            PostChain.Jitter(_frameSeq, PostChain.RenderSize(t.Vp2), PostChain.RenderSize(t.Vp3), out jx, out jy);
            proj.M31 += jx * proj.M34; proj.M32 += jy * proj.M34;
        }
        PostChain.JitterX = jx; PostChain.JitterY = jy;
        _projMtx = proj;   // #347: what a spliced stock file reads as m_proj
        Matrix4x4 vp = view * proj;
        // every froxel input comes off THIS pass's projection, never the capture side (see TryPerspective)
        bool perspective = AlloyLights.TryPerspective((float*)&proj, out float p00, out float p11,
                                                      out float near, out float far);
        // #120: this pass's eye, for the specular view vector. Same row-vector convention as above, so the
        // inverse view's translation IS the eye. Pass-local state, read off this pass's own stacks -- the
        // SCENE camera is not derived here any more (#257 reads it whole at the anchor), so there is no
        // contest between passes left to resolve. Ahead of the frustum, which measures its fog clamp from it.
        if (perspective && Matrix4x4.Invert(view, out Matrix4x4 camToWorld)) _eyePos = camToWorld.Translation;
        SetPassFrustum((float*)&vp, perspective);   // #330: the route gate's planes, this pass's own
        // Cap 5 is the engine's own lit predicate (Ghidra MatStatic @0x450560 / RenderFlat @0x450eb0): with
        // it off every part gets flat white, so the flat variant IS vanilla for this pass.
        // CX-120: the last term is the ENGINE's lighting cap (RenderInterface::IsEnabled(5)), which only the
        // engine's own render path ever enables -- and in DB mode that path does not run, so it reads 0 and
        // every lit part in the world was skipped as "unlit-pass". It is an input about what the ENGINE would
        // draw, and in DB mode the engine draws nothing: Alloy's own stages decide lighting. Same shape as the
        // RenderSinglePass window: an engine state consulted in a mode where the engine has no state.
        // CX-120: say WHY the rig stood down, once. "unlit-pass N" names the symptom; these are the terms.
        if (!_saidLitTerms && (!perspective || _litCount == 0 || !_lighting || Programs._litFailed))
        {
            _saidLitTerms = true;
            _log?.Always($"AlloyRenderer: [db] the lighting rig stood down -- perspective={perspective} "
                       + $"p00={p00:F4} p11={p11:F4} near={near:F3} far={far:F3} litCount={_litCount} "
                       + $"lighting={_lighting} litFailed={Programs._litFailed} "
                       + $"proj.M11={proj.M11:F4} proj.M22={proj.M22:F4} proj.M34={proj.M34:F4} "
                       + $"proj.M33={proj.M33:F4} proj.M43={proj.M43:F4} fovy={AlloyCamera.WorldView.Fovy:F3} "
                       + $"vp={AlloyCamera.WorldView.W}x{AlloyCamera.WorldView.H}. Every lit part is skipped "
                       + "while this holds, which is an empty world.");
        }
        bool lightingOn = _litCount > 0 && _lighting && !Programs._litFailed && perspective;

        FrameZones.Add(FrameZones.Zone.Setup, tSetup, FrameZones.Ticks(FrameZones.Zone.Pose) - pose0);
        long tEnter = FrameZones.Now;
        if (!GlContextService.Enter()) { NoFrameBehindUs("the 4.6 context could not be made current"); DbNoDraw("the 4.6 context could not be made current"); return false; }
        FrameZones.Add(FrameZones.Zone.Enter, tEnter);
        GpuCheckpoint("the previous frame's GPU work is complete at this bracket's entry");
        long tOwn = FrameZones.Now;
        // #211's queued texture deletes, CX-117's registry uploads/mints, the master PLT palette and the
        // per-record colour rows: the frame's context-bound deferred work, declared once in Arm and run here
        // because the colour bracket is normally the frame's FIRST. Whichever bracket runs it first is the one
        // that does it, and GlContextService's health bracket then stands down for the frame -- its own, the
        // GUI flush's and the render thread's all route through the one entry point for that reason.
        GlContextService.RunInBracketHere();
        OwnedBuffers.ClearDirty();   // #260: geometry uploaded itself at the engine's commit; close the window
        ClearItemCommits();
        ApplySwaps();   // #386: swapped texture names take their new handles here, in the context that owns residency
        OwnedBuffers.HighWater();    // ...and report the mark the funnel recorded, where a string is affordable
        FrameZones.Add(FrameZones.Zone.Own, tOwn);
        long tPrep = FrameZones.Now, lights0 = FrameZones.Ticks(FrameZones.Zone.Lights);
        if (!Programs.EnsureProgram()) { DbNoDraw("the world program did not build"); GlContextService.Leave(); return false; }
        BindTarget();
        // #117/#119: the lighting rig, the GL state and the batch BUILD are bracket preamble -- both
        // colour-bracket passes draw the same staged batches, and building twice would revalidate (and
        // re-stamp OwnGen for) every Alloy part twice. The passes come off FrameGraph, not off call order.
        if (lightingOn)
        {
            long tLights = FrameZones.Now;
            AlloyLights.Upload(_frameSeq);
            // The cluster buffers are only meaningful once THIS pass's build has run: a skipped build
            // leaves them holding another camera's binning (or, before the first dispatch, undefined
            // BufferData contents), and a lit part would then sample arbitrary light indices.
            lightingOn = AlloyLights.Build((float*)&view, p00, p11, near, far, viewport[2], viewport[3], tally: true)
                         && Programs._progLit46 != 0;
            FrameZones.Add(FrameZones.Zone.Lights, tLights);
        }
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        // #292: cull ON is the engine's default -- Scene::RenderDrawBucket @0x46d060 leaves
        // Enable(CULL_FACE) + SetCullFace(BACK) behind on every exit, and that is the state its
        // sub-list 0 (everything not marked `twosided`) draws in. The batch loops override it per batch;
        // this states the default the passes with no batch of their own inherit.
        new PassState(depth: true, depthWrite: true, cull: true, srgb: srgb).Apply();
        _blendOn = false;
        Gl.ActiveTexture(Gl.TEXTURE0);
        Counters._lastPixels = viewport[2] * viewport[3];
        // #129: built HERE, not at the pass, because staging has to know. A water part staged for a pass
        // whose program never built would be stamped as owned (and so suppressed in the engine's walk)
        // and then never drawn -- so the decision is made before BuildBatches, which skips the class
        // outright when it is false.
        // ...and on the LIGHT RIG, for a reason the class check cannot supply: the water stage calls
        // CxClusteredLight unconditionally, and the cluster buffers are only meaningful once THIS
        // pass's build has run. BuildBatches skips lit batches on !lightingOn, but a water batch is
        // keyed on its class rather than on _gLit, so without this term /alloy light 0 (or a failed
        // lit program, or a build that refused) would leave the water program reading undefined
        // BufferData contents -- an unbounded per-fragment light loop, not a dim sea.
        _waterArmed = FrameSnapshot.Usable && lightingOn && Programs.EnsureWaterProgram();
        FrameZones.Add(FrameZones.Zone.Prep, tPrep, FrameZones.Ticks(FrameZones.Zone.Lights) - lights0);
        // #139/#132: from here to PassId.PostProcess the colour passes draw into an RGBA16F target that
        // shares the engine's depth, and everything they write is LINEAR. Asked BEFORE anything is
        // staged, because a refusal is no longer survivable: there is no display-space variant of the
        // Alloy stages to fall back to, so the whole pass stands down and the engine renders it vanilla.
        // Staging first would stamp OwnGen on parts the iterator then suppresses and nobody draws.
        long tPostBegin = FrameZones.Now;
        bool postOk = PostChain.Begin(viewport, engClear, engClearDepth);
        FrameZones.Add(FrameZones.Zone.Post, tPostBegin);
        if (!postOk) { DbNoDraw("PostChain.Begin refused"); GlContextService.Leave(); return false; }
        long tPump = FrameZones.Now;
        GpuFrameStart();
        TickCycleTextures();
        // #287's scale, hoisted ahead of the staging walk: the automatic-LOD pick reads it there, and the
        // cull dispatch below is handed the same figure.
        _lodScale = perspective ? p11 * viewport[3] * 0.5f : 0f;
        MeshLod.Pump();   // the one GL edge of the LOD arena: collect what the worker finished
        MeshChart.Pump(); // ...and of the chart arenas (#673)
        CX.Alloy.Db.MeshArena.Pump();   // CX-114: the one model-space arena -- frees land and both mirrors go up
        FrameZones.Add(FrameZones.Zone.Pump, tPump);
        long tBuild = FrameZones.Now;
        int groups = BuildBatches(lightingOn);
        FrameZones.Add(FrameZones.Zone.Build, tBuild);
        // CX-120: the one point both scene layers converge on with the frame's inputs final and its records
        // staged -- so the two modes' dumps are of the same thing and can be diffed line for line.
        FrameDiffDump();
        CreatureParityDump();   // CX-120: the composed body, per node, in both layers' one line format
        if (groups <= 0) { DbNoDraw("BuildBatches produced no batch"); Gl.BindVertexArray(0); GlContextService.Leave(); return false; }
        long tUp = FrameZones.Now;
        StageSky();
        bool drawsUp = Issue.UploadDraws();
        Db.PltColors.Upload();   // the frame-start push ran before this frame's PLT rows were staged
        Db.TintTerms.Upload();
        // CX-120: with the frame's records staged AND pushed, a record that still differs from the mirror is
        // one the dirty list never named -- the change-driven build's own bug class.
        StaleAuditTick();
        if (!drawsUp)
        {
            PaintStreamFailures(display);
            Gl.BindVertexArray(0); GlContextService.Leave(); return false;
        }
        // #308 follow-up: the shadow bracket already uploaded this frame's block; re-orphaning it
        // here would throw away the very pose the bake just sampled from.
        if (!posedByShadow) Issue.UploadBones();
        FrameZones.Add(FrameZones.Zone.Upload, tUp);
        // #243: Alloy's own visibility, dispatched twice -- frustum-only for the depth prepass, then
        // frustum + hi-Z for the colour pass once the prepass has filled the depth the pyramid is
        // built from.
        // #290: staged from EVERY batch now, not the opaque prefix. _gOpaqueEnd is handed over as
        // the COMPACTING boundary instead: batches above it are sorted back-to-front by a second
        // dispatch rather than appended by the atomic.
        long tCull = FrameZones.Now;
        // The batch list's draw count, read off the last batch rather than from _drawN -- StageSky
        // appends to _drawN below and its draws have no source record and no batch of their own.
        int cullDraws = _gStart[groups - 1] + _gLen[groups - 1];
        // #287: pixels per unit of world radius at unit depth, the LOD test's one input. Zero when
        // this pass has no perspective projection, and the test then stands down.
        float lodScale = _lodScale;
        // #303: the spheres go up whatever the cull mode -- the shadow gather reads the same buffer.
        fixed (float* sph = _dSph)
            CullPass.UploadSpheres(sph, _recOwnCap + MaxSkyDraws);
        fixed (uint* src = _dSrc)
            // A probe capture draws the same batches six more times from six other cameras, so its
            // faces cannot ride a list culled for THIS one -- passing zero groups stands the cull
            // down for the whole pass rather than leaving a stale list armed behind it.
            if (CullPass.Begin(_capturing ? 0 : groups, cullDraws, src,
                               lodScale, _gOpaqueEnd, _eyePos))
            {
                GpuBegin(GpuTerm.Cull);
                // #489: hiZ here means the PRIOR pass's pyramid; Dispatch stands it down when none is resident.
                CullPass.Dispatch(CullPass.PhasePrepass, &vp, hiZ: true, PassGen);
                GpuEnd(GpuTerm.Cull);
            }
        FrameZones.Add(FrameZones.Zone.Cull, tCull);
        f.View = view; f.Proj = proj; f.Vp = vp; f.VpClean = vpClean;
        f.Jx = jx; f.Jy = jy; f.Near = near; f.Far = far; f.P00 = p00; f.P11 = p11;
        f.Perspective = perspective; f.LightingOn = lightingOn; f.Srgb = srgb; f.Groups = groups;
        PresentTarget.MarkComposed();
        return true;
    }

    /// <summary>The ADR 0011 slot walk: one case per pass, in FrameGraph order, over the batches
    /// <see cref="BeginOwnedFrame"/> staged. Inside the 4.6 bracket that method entered.</summary>
    private static void DrawOwnedInner()
    {
        int* display = stackalloc int[4];
        int* viewport = stackalloc int[4];
        if (!BeginOwnedFrame(display, viewport, out OwnedFrame f)) return;
        Matrix4x4 view = f.View, proj = f.Proj, vp = f.Vp, vpClean = f.VpClean;
        float jx = f.Jx, jy = f.Jy, near = f.Near, far = f.Far, p00 = f.P00, p11 = f.P11;
        bool perspective = f.Perspective, lightingOn = f.LightingOn, srgb = f.Srgb;
        int groups = f.Groups;
        // #322: the loop's own CPU work, with the brackets that fire inside it (multidraw issue,
        // the colour cull dispatch, post, the probe capture) subtracted out -- same parent/child
        // pattern as the Probe bracket below.
        long issue0 = FrameZones.Ticks(FrameZones.Zone.Issue), cull0 = FrameZones.Ticks(FrameZones.Zone.Cull);
        long post0 = FrameZones.Ticks(FrameZones.Zone.Post), probe0 = FrameZones.Ticks(FrameZones.Zone.Probe);
        long lm0 = FrameZones.Ticks(FrameZones.Zone.Lightmap);
        long tPasses = FrameZones.Now;
        // #375: the post bracket opens at PostProcess and closes at PostStack, two graph slots.
        long tPost = 0; bool postOpen = false;
        // #390: did the velocity pass run at the AO slot this pass? If not, PostProcess runs it.
        bool motionRan = false;
        foreach ((PassId id, Bracket bracket) in FrameGraph.Passes)
        {
            if (bracket != Bracket.Colour) continue;
            long tId = FrameZones.Now;
            long kid0 = PassChildTicks;
            switch (id)
            {
                case PassId.DepthPrepass:
                    long tPre = FrameZones.Now;
                    DepthPrepassPass(_gOpaqueEnd, &vp, viewport, CullPass.PhasePrepass);
                    // #243: the pyramid, from the depth that pass just wrote. Inside the same frame
                    // on purpose -- the colour cull tests it, and a pyramid carried from last frame
                    // pops at the silhouette of every moving occluder.
                    if (DepthResource.Filled(PassGen))
                    {
                        long tHiz = FrameZones.Now;
                        GpuBegin(GpuTerm.HiZ);
                        CullPass.BuildHiZ(PostChain.DepthTex, viewport[2], viewport[3], viewport,
                                          PostChain.Fbo, PassGen, &vp);
                        GpuEnd(GpuTerm.HiZ);
                        FrameZones.Add(FrameZones.Zone.Hiz, tHiz);
                        // #489: phase two -- what phase one's STALE pyramid rejected, re-tested
                        // against the fresh one and drawn now, so a wrong rejection never reaches
                        // the screen. Skipped whole when phase one was frustum-only (nothing marked).
                        if (CullPass.RetestPending)
                        {
                            GpuBegin(GpuTerm.Cull);
                            CullPass.Dispatch(CullPass.PhaseRetest, &vp, hiZ: true, PassGen);
                            GpuEnd(GpuTerm.Cull);
                            GpuBegin(GpuTerm.PrepassRetest);
                            DepthPrepassPass(_gOpaqueEnd, &vp, viewport, CullPass.PhaseRetest);
                            GpuEnd(GpuTerm.PrepassRetest);
                        }
                    }
                    FrameZones.Add(FrameZones.Zone.Prepass, tPre);
                    break;
                case PassId.AmbientOcclusion:
                    // #388/#390: the velocity target off the PREPASS depth, ahead of the AO pass that
                    // reprojects through it. Its own term -- TIME_ELAPSED cannot nest in the AO one.
                    // A frame with no prepass depth takes the PostProcess site below instead.
                    if (PostChain.MotionWanted && DepthResource.Filled(PassGen) && !_capturing)
                    {
                        GpuBegin(GpuTerm.Motion);
                        MotionVectors.Run(PostChain.DepthTex, &vp, &vpClean, jx, jy, viewport, PostChain.Fbo, _frameSeq);
                        MoverMotionPass(&vp, jx, jy, viewport);
                        GpuEnd(GpuTerm.Motion);
                        motionRan = true;
                    }
                    GpuBegin(GpuTerm.Ao);
                    SsaoPass.Run(PostChain.DepthTex, DepthResource.Filled(PassGen), &proj, viewport, PostChain.Fbo, _frameSeq);
                    GpuEnd(GpuTerm.Ao);
                    break;
                case PassId.OpaqueOwned:
                    // Second dispatch, and it is unconditional rather than folded into the prepass
                    // case: a frame graph without a prepass in it must still leave the colour pass a
                    // list to draw, and hi-Z simply stands down when there is no depth to build from.
                    long tCull2 = FrameZones.Now;
                    GpuBegin(GpuTerm.Cull);
                    CullPass.Dispatch(CullPass.PhaseColour, &vp, hiZ: DepthResource.Filled(PassGen), PassGen);
                    GpuEnd(GpuTerm.Cull);
                    FrameZones.Add(FrameZones.Zone.Cull, tCull2);
                    // CX-51: the reflection records ride the colour pass from here to Reflections
                    if (perspective && !_capturing) Ssr.Begin(viewport);
                    // CX-188: ...and the G-buffer rides the opaque and cutout passes, which are the only two
                    // the CX_DEFERRED twin draws in. A stale G0 is a lit pixel, so the clear is unconditional
                    // for the frames the twin may draw in; the lanes are indexed by gl_FragCoord, which an
                    // offset viewport would shear against the compute pass's own texel grid (Ssr's refusal).
                    GBuffer.Written = false; GBuffer.LightLive = false;   // a frame without the deferred pass reads no record
                    if (lightingOn && perspective && !_capturing && Lighting.Deferred.V && (_lightViz == 0 || _lightViz == VizLightFrom)
                        && viewport[0] == 0 && viewport[1] == 0 && GBuffer.Ensure(viewport[2], viewport[3]))
                    {
                        GBuffer.BeginFrame();
                        GBuffer.AttachColourPass();
                    }
                    OpaqueOwnedPass(_gOpaqueEnd, &vp, viewport, near, far);
                    // The GPU-side cluster stats belong to the SCREEN's build. Harvested here rather
                    // than after the bracket because the probe capture rebuilds the clusters once per
                    // face, and reading afterwards would report cube face 6's overflow as the frame's.
                    if (_takeOverflow)
                    { _takeOverflow = false; Counters._clusterOverflow += AlloyLights.TakeClusterOverflow(); }
                    break;
                case PassId.GrassOwned:
                    long tGrassDraw = FrameZones.Now;
                    GrassField.Draw(&vp, viewport, near, far, lightingOn);   // brackets its own two GPU terms
                    FrameZones.Add(FrameZones.Zone.Grass, tGrassDraw);
                    // Grass draws unblended by construction and toggles GL_BLEND itself; hand the
                    // tracked state back exactly as it was, or the next pass's own bookkeeping
                    // (Issue.IssueBatches reads _blendOn as "what is already set") starts from a lie.
                    if (_blendOn) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
                    break;
                case PassId.SkyOwned:
                    if (!_capturing) FrameTap.Take("opaque", viewport);
                    GpuBegin(GpuTerm.Sky);
                    if (!_capturing && !Matrix4x4.Invert(vp, out SkyInvVp)) SkyInvVp = default;
                    Ssr.Pause();
                    SkyOwnedPass(&vp, viewport);
                    Ssr.Resume();
                    GpuEnd(GpuTerm.Sky);
                    if (!_capturing) FrameTap.Take("sky", viewport);
                    break;
                case PassId.CutoutOwned:
                    // CX-188: the cutout class is the last G-buffer writer; close the attachment after it
                    CutoutOwnedPass(_gOpaqueEnd, &vp, viewport, near, far);
                    GBuffer.Detach();
                    break;
                case PassId.CoverageOwned:
                    if (_a2cHeld) CoverageOwnedPass(&vp, viewport, near, far);
                    break;
                case PassId.DeferredLights:
                    // CX-188: two dispatches, timed apart -- step 3's per-pixel shadow record first, then the
                    // light walk that reads it. The walk needs the record's barrier, so they neither reorder
                    // nor share a bracket.
                    if (lightingOn && perspective && !_capturing && GBuffer.Written)
                    {
                        GpuBegin(GpuTerm.DeferredShadow);
                        DeferredShadows.Run(&vp, &proj, viewport, near, far);
                        GpuEnd(GpuTerm.DeferredShadow);
                    }
                    GpuBegin(GpuTerm.Deferred);
                    if (lightingOn && perspective && !_capturing && GBuffer.Written)
                        DeferredLights.Run(&vp, &view, &proj, viewport, near, far);
                    GpuEnd(GpuTerm.Deferred);
                    // CX-188 step 4: the hot-light debt, right after the pass whose walk no longer takes it.
                    // Its own term because TIME_ELAPSED cannot nest, and its own dispatch because a tile with
                    // no creature shadow over it exits before reading one G-buffer texel.
                    GpuBegin(GpuTerm.PairDebt);
                    if (lightingOn && perspective && !_capturing && GBuffer.Written)
                        PairDebt.Run(&vp, &view, &proj, viewport, near, far);
                    GpuEnd(GpuTerm.PairDebt);
                    break;
                case PassId.Reflections:
                    GpuBegin(GpuTerm.Ssr);
                    if (lightingOn && perspective && !_capturing) PlanarPass(&vp, near, far, viewport);
                    Ssr.Run(viewport, &view, &proj, near, far, _lightViz);
                    GpuEnd(GpuTerm.Ssr);
                    // the pass leaves GL_BLEND off; hand the tracked state back as the next pass expects it
                    if (_blendOn) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND);
                    break;
                case PassId.BlendedOwned:
                    BlendProfile.Poll(_log);   // in the 4.6 bracket: the queries are that context's names
                    Issue.LightRecBegin();
                    GpuBegin(GpuTerm.Blended);
                    // OIT (OitPass): the whole class in one unsorted, non-depth-writing pass into the
                    // accumulation pair, composited once -- the per-creature run loop #490 replaced. With
                    // no depth written during it, no layer can hide another; the depth vanilla writes for
                    // this class is laid once the composite is down.
                    // /alloy oit 2 (#490) sorts per pixel through linked lists on the same bracket.
                    if (_gBlendEnd > _gOpaqueEnd && !_capturing && OitPass.Begin(PostChain.DepthTex, viewport))
                    {
                        Issue._oitOn = true;
                        // the accumulation alone draws with whatever a diagnostic has pointed the lit slots at
                        using (var step = Ablation.BlendedStep())
                        {
                            bool blendCounting = _qBlendSamples.Begin(Gl.SAMPLES_PASSED);
                            bool primCounting = _qBlendPrims.Begin(Gl.PRIMITIVES_GENERATED);
                            bool fragCounting = _qBlendFrags.Begin(Gl.FRAGMENT_SHADER_INVOCATIONS);
                            if (!step.Skip) Issue.TransparentPass(_gOpaqueEnd, _gBlendEnd, &vp, viewport, near, far);
                            if (fragCounting) _qBlendFrags.End(Gl.FRAGMENT_SHADER_INVOCATIONS);
                            if (primCounting) _qBlendPrims.End(Gl.PRIMITIVES_GENERATED);
                            if (blendCounting) _qBlendSamples.End(Gl.SAMPLES_PASSED);
                        }
                        _blendGroups += _gBlendEnd - _gOpaqueEnd; _blendFrames++;
                        Issue._oitOn = false;
                        // the class's term split four ways (TIME_ELAPSED cannot nest): Blended is the
                        // accumulation alone, and the composite, the hosted batches and the depth sweep each own one
                        GpuEnd(GpuTerm.Blended);
                        GpuBegin(GpuTerm.BlendedComposite);
                        OitPass.End(viewport);
                        GpuEnd(GpuTerm.BlendedComposite);
                        _blendOn = true;
                        GpuBegin(GpuTerm.BlendedHosted);
                        // ...then the batches the accumulation stepped over, and only if it stepped over any:
                        // the pass knows its own count, and with no hosted (or non-SrcAlpha) batch in the
                        // blended range this was a walk of the whole range to draw nothing. The two state
                        // calls are the ones that walk would have made, which the water copy and the water
                        // pass after it both read.
                        if (Issue.OitDeferred > 0)
                        {
                            Issue._onlyHosted = true;
                            Issue.TransparentPass(_gOpaqueEnd, _gBlendEnd, &vp, viewport, near, far);
                            Issue._onlyHosted = false;
                        }
                        else { Gl.DepthMask(0); Gl.Enable(Gl.BLEND); }
                        GpuEnd(GpuTerm.BlendedHosted);
                        GpuBegin(GpuTerm.BlendedDepth);
                        // ...and then the depth vanilla writes for this class, which the copy below and the
                        // water pass reading it both need. Last, so nothing here occluded the composite.
                        BlendedDepthSweep(_gOpaqueEnd, _gBlendEnd, &vp, viewport);
                        GpuEnd(GpuTerm.BlendedDepth);
                    }
                    else
                    {
                        Issue.TransparentPass(_gOpaqueEnd, _gBlendEnd, &vp, viewport, near, far, depthWrite: true);
                        GpuEnd(GpuTerm.Blended);
                    }
                    Issue.LightRecEnd();
                    break;
                case PassId.FbCopy:
                    // Only when there is water to read it. The copy is a full-screen blit of colour
                    // AND depth -- cheap, but not free, and every frame without a water surface on
                    // screen would be paying for a texture nothing samples.
                    if (_gWaterEnd > _gBlendEnd && !_capturing)
                    {
                        // The blended class left GL_BLEND on and the depth mask off; the blit does
                        // not care, but leaving the tracked state honest does -- Issue.IssueBatches below
                        // reads _blendOn as "what is already set".
                        long tBlit = FrameZones.Now;
                        GpuBegin(GpuTerm.WaterCopy);
                        if (perspective && WaterRect(&vp, viewport, p00, p11, out int rx0, out int ry0, out int rx1, out int ry1))
                            FrameSnapshot.Copy(viewport, rx0, ry0, rx1, ry1);
                        else FrameSnapshot.Copy(viewport);
                        GpuEnd(GpuTerm.WaterCopy);
                        FrameZones.Add(FrameZones.Zone.Blit, tBlit);
                    }
                    break;
                case PassId.WaterOwned:
                    GpuBegin(GpuTerm.Water);
                    Issue.WaterOwnedPass(_gBlendEnd, _gWaterEnd, &vp, (float*)&view, (float*)&proj,
                                   viewport, near, far);
                    GpuEnd(GpuTerm.Water);
                    break;
                case PassId.AdditiveOwned:
                    GpuBegin(GpuTerm.Additive);
                    Issue.TransparentPass(_gWaterEnd, groups, &vp, viewport, near, far);
                    GpuEnd(GpuTerm.Additive);
                    // The blending classes leave the depth mask off and GL_BLEND on; the probe
                    // capture that follows draws the opaque set again and must find neither.
                    Gl.Disable(Gl.BLEND); _blendOn = false; Gl.DepthMask(1);
                    break;
                case PassId.BeamsOwned:
                    // Beams bind a program and a VAO of their own and hand the state back exactly as
                    // the additive arm left it; nothing here tracks _blendOn on their behalf.
                    GpuBegin(GpuTerm.Beams);
                    Beams.Draw(&view, &proj);
                    GpuEnd(GpuTerm.Beams);
                    break;
                case PassId.ParticlesOwned:
                    // Particles bind a program, a VAO and a buffer of their own and hand the state
                    // back the way the beams left it -- blend off, depth mask on, culling off.
                    GpuBegin(GpuTerm.Particles);
                    // #336: the particle pass's fill -- samples over pixels is its overdraw, the
                    // number that says whether a half-resolution pass would pay.
                    bool partCounting = _qPartSamples.Begin(Gl.SAMPLES_PASSED);
                    long blit0 = FrameZones.Ticks(FrameZones.Zone.Blit);
                    long tParts = FrameZones.Now;
                    Particles.Draw(&view, &proj, viewport, near, far, lightingOn);
                    FrameZones.Add(FrameZones.Zone.Parts, tParts,
                                   FrameZones.Ticks(FrameZones.Zone.Blit) - blit0);
                    if (partCounting) _qPartSamples.End(Gl.SAMPLES_PASSED);
                    GpuEnd(GpuTerm.Particles);
                    break;
                case PassId.HalosOwned:
                    Halos.Draw(&view, &proj, viewport);
                    break;
                case PassId.WeatherOwned:
                    GpuBegin(GpuTerm.Weather);
                    Weather.Draw(&view, &proj, viewport, _eyePos, AreaWind(), near, far, lightingOn);
                    GpuEnd(GpuTerm.Weather);
                    break;
                case PassId.ProbeCapture:
                    if (!lightingOn) break;
                    CaptureProbe(_gOpaqueEnd, far);
                    long tLm = FrameZones.Now;
                    LightmapBake.Run(_capturing, _frameSeq);   // #674: its own targets, restored below with the rest
                    FrameZones.Add(FrameZones.Zone.Lightmap, tLm);
                    // The capture bound its own framebuffer and viewport; whatever runs after the
                    // bracket must find the engine's target again, not a 128px cube face.
                    BindTarget();
                    Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
                    if (srgb) Gl.Enable(Gl.FRAMEBUFFER_SRGB); else Gl.Disable(Gl.FRAMEBUFFER_SRGB);
                    break;
                case PassId.PostProcess:
                    if (!PostChain.Armed) break;
                    tPost = FrameZones.Now; postOpen = true;
                    // #388: the camera velocity target, off the finished depth and this pass's
                    // view-projection. Its own GPU term, ahead of the post one -- TIME_ELAPSED
                    // queries cannot nest.
                    if (PostChain.MotionWanted && !_capturing)
                    {
                        GpuBegin(GpuTerm.Motion);
                        if (!motionRan)
                        {
                            MotionVectors.Run(PostChain.DepthTex, &vp, &vpClean, jx, jy, viewport, PostChain.Fbo, _frameSeq);
                            MoverMotionPass(&vp, jx, jy, viewport);
                        }
                        // CX-48: grass and particles correct the field over their own pixels. Here, past both
                        // of the camera field's sites and past the classes themselves, so what the resolve
                        // reads is complete whichever site painted it.
                        EffectMotionPass(&vp, &view, &proj, jx, jy, viewport);
                        GpuEnd(GpuTerm.Motion);
                    }
                    // #493: the vis-ID viz over the finished frame, before the resolve reads it.
                    if (_lightViz == IdTarget.VizMode && !_capturing) IdTarget.Paint(viewport);
                    GpuBegin(GpuTerm.Post);
                    // Bloom runs against the HDR target and binds its own attachments; the engine's
                    // target has to come back before the composite writes into it, and with it the
                    // viewport and the sRGB-encode state the pass was entered with.
                    PostChain.Bloom();
                    // #375: Begin binds its own framebuffer to attach; the engine's comes back for the depth hand-back
                    // #391/#389: the stack is told both sizes; when it upsamples, the resolve's
                    // colour draw lands at the render viewport and the taa stage writes the display.
                    ToonStage.SetProj(&proj);
                    FbPostStage.SetCamera(&view, &proj);
                    uint into = PostStack.Begin(viewport[2], viewport[3], display[2], display[3]);
                    RebindFrameTarget();
                    PostChain.Composite(display, into, PostStack.Upsampling ? viewport : display);
                    // Closed HERE and re-opened at PostStack, though the bracket is conceptually one span
                    // over two graph slots: a zone opened in one loop iteration and added in another credits
                    // the whole span to the second, which then reports negative self time and is dropped.
                    // The zone's total is the sum of the two spans either way.
                    FrameZones.Add(FrameZones.Zone.Post, tPost);
                    break;
                case PassId.PostStack:
                    if (!postOpen) break;
                    tPost = FrameZones.Now;
                    // The last live stage rebinds the engine's target itself; a no-op with none.
                    PostStack.Run(display, PostStack.Upsampling ? viewport : display);
                    // #232 second mode: the owned outline+fill highlight, drawn into the finished
                    // display-space frame with the depth Composite blitted to it. Not a FrameGraph
                    // slot of its own: it is part of finishing the post output, like Bloom.
                    // #389: the JITTERED camera for the mask (a biased LEQUAL against the jittered depth;
                    // the clean camera z-fought it, live), and the jitter handed over so the composite
                    // reads the mask back into the accumulated frame's space.
                    Highlight.OutlineHighlightPass(groups, &vp, &view, &proj, jx, jy, viewport, display);
                    // CX-83: the area-transition highlight, in the slot the engine's DoHighlightTriggers had.
                    TriggerHighlight.Draw(&vp, display);
                    GpuEnd(GpuTerm.Post);
                    FrameZones.Add(FrameZones.Zone.Post, tPost);
                    break;
            }
            // SELF time: the same five child zones Zone.Passes subtracts, subtracted here too, so the rows
            // and the zone are one partition instead of a parent and its children counted twice.
            long self = FrameZones.Now - tId - (PassChildTicks - kid0);
            // Negative = a child bracket credited to this pass that did not run inside it, which is a broken
            // partition and not something to swallow: clamped here and counted for the period line.
            if (self < 0) { _passSelfNeg++; self = 0; }
            _passCpu[(int)id] += self;
        }
        long tTail = FrameZones.Now;
        PaintStreamFailures(display);
        GpuSpanEnd();
        _passTail += FrameZones.Now - tTail;
        FrameZones.Add(FrameZones.Zone.Passes, tPasses,
                       FrameZones.Ticks(FrameZones.Zone.Issue) - issue0
                       + FrameZones.Ticks(FrameZones.Zone.Cull) - cull0
                       + FrameZones.Ticks(FrameZones.Zone.Post) - post0
                       + FrameZones.Ticks(FrameZones.Zone.Probe) - probe0
                       + FrameZones.Ticks(FrameZones.Zone.Lightmap) - lm0);
        Gl.BindVertexArray(0);
        GpuCheckpoint("the world pass is complete on the GPU");
        GlContextService.Leave();
    }

    /// <summary>Under <c>alloy.gldebug=1</c> only: wait for the GPU here and say so, so a driver timeout's log
    /// ends at the last stage the GPU finished rather than at the last line the CPU queued.</summary>
    private static void GpuCheckpoint(string what)
    {
        if (!GlContextService.Debug || Gl.Finish == null) return;
        Gl.Finish();
        _log?.Always($"AlloyRenderer: [gldebug] frame {_frameSeq}: {what}");
    }

    /// <summary>Ticks the zones <see cref="FrameZones.Zone.Passes"/> subtracts have taken this frame, so a pass
    /// row can subtract the same ones and mean self time.</summary>
    private static long PassChildTicks
        => FrameZones.Ticks(FrameZones.Zone.Issue) + FrameZones.Ticks(FrameZones.Zone.Cull)
         + FrameZones.Ticks(FrameZones.Zone.Post) + FrameZones.Ticks(FrameZones.Zone.Probe)
         + FrameZones.Ticks(FrameZones.Zone.Lightmap);

    /// <summary>The screen rectangle this pass's water draws can cover, from their cull spheres projected
    /// through the pass camera, with a margin for the water stage's refraction offset. False = take the whole
    /// frame: a sphere without finite bounds, one crossing the near plane, or the dial off.</summary>
    private static bool WaterRect(Matrix4x4* vp, int* viewport, float p00, float p11,
                                  out int x0, out int y0, out int x1, out int y1)
    {
        x0 = y0 = x1 = y1 = 0;
        if (!FrameSnapshot.Rect) return false;
        float vw = viewport[2], vh = viewport[3];
        float minx = vw, miny = vh, maxx = 0f, maxy = 0f;
        fixed (float* sph = _dSph)
            for (int g = _gBlendEnd; g < _gWaterEnd; g++)
                for (int d = _gStart[g], e = d + _gLen[g]; d < e; d++)
                {
                    float* s = sph + (long)_dSlot[d] * 4;
                    if (s[3] >= NeverCull || !SphereRect(vp, vw, vh, p00, p11, s[0], s[1], s[2], s[3],
                                                          out float ax, out float ay, out float bx, out float by))
                        return false;
                    minx = MathF.Min(minx, ax); maxx = MathF.Max(maxx, bx);
                    miny = MathF.Min(miny, ay); maxy = MathF.Max(maxy, by);
                }
        // the refraction reads a few pixels around the surface; a texel past the margin is last frame's
        float m = vh * FrameSnapshot.RectMargin;
        x0 = Math.Max((int)(minx - m), 0); y0 = Math.Max((int)(miny - m), 0);
        x1 = Math.Min((int)(maxx + m) + 1, viewport[2]); y1 = Math.Min((int)(maxy + m) + 1, viewport[3]);
        return x1 > x0 && y1 > y0;
    }

    /// <summary>One sphere's screen extent in pixels: the centre through the row-vector view-projection, the
    /// radius scaled by the projection's diagonal at the centre's clip depth. False when the sphere reaches
    /// the near plane, where the extent is unbounded.</summary>
    internal static bool SphereRect(Matrix4x4* vp, float vw, float vh, float p00, float p11,
                                    float x, float y, float z, float r,
                                    out float x0, out float y0, out float x1, out float y1)
    {
        x0 = y0 = x1 = y1 = 0f;
        var clip = Vector4.Transform(new Vector4(x, y, z, 1f), *vp);
        if (clip.W <= r) return false;
        float cx = (clip.X / clip.W * 0.5f + 0.5f) * vw, cy = (clip.Y / clip.W * 0.5f + 0.5f) * vh;
        float ex = r * p00 / clip.W * 0.5f * vw, ey = r * p11 / clip.W * 0.5f * vh;
        x0 = cx - ex; x1 = cx + ex; y0 = cy - ey; y1 = cy + ey;
        return true;
    }

    /// <summary>Off-device: a unit sphere ten units down the view axis under a 90-degree lens covers the
    /// middle tenth of the frame, and a sphere on the near plane is refused.</summary>
    private static bool SphereRectSelfCheck(ExtenderLog log)
    {
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 100f);
        var vp = Matrix4x4.Identity * proj;
        bool ok = SphereRect(&vp, 1000f, 1000f, proj.M11, proj.M22, 0f, 0f, -10f, 1f,
                             out float x0, out float y0, out float x1, out float y1)
                  && MathF.Abs(x0 - 450f) < 1f && MathF.Abs(x1 - 550f) < 1f
                  && MathF.Abs(y0 - 450f) < 1f && MathF.Abs(y1 - 550f) < 1f
                  && !SphereRect(&vp, 1000f, 1000f, proj.M11, proj.M22, 0f, 0f, -0.5f, 1f, out _, out _, out _, out _);
        if (!ok) log.Info($"AlloyRenderer.SphereRectSelfCheck: FAILED ({x0:F1},{y0:F1})-({x1:F1},{y1:F1})");
        return ok;
    }

    /// <summary>The blended class's DEPTH -- depth only, at the colour draw's LEQUAL, through the opaque
    /// prepass's alpha-testing stage (<see cref="BlendLaysDepth"/>), because under <see cref="OitPass"/>
    /// nothing in the class writes depth and water saw a lake bed through an already-shaded head. AFTER the
    /// composite: laid first it would occlude the surfaces OIT accumulates. <c>/alloy blenddepth 0</c> off.</summary>
    private static void BlendedDepthSweep(int from, int to, Matrix4x4* vp, int* viewport)
    {
        if (to <= from || !_blendDepth || !_prepassFeat || !Programs.EnsureDepthFeatProgram()) return;
        bool indirect = CullPass.Active;
        // The COLOUR phase's commands, not the prepass phase's: this lays depth for exactly the batches the
        // blended colour draw kept, and the two phases test against different pyramids (#489).
        if (indirect) CullPass.BindForDraw(CullPass.PhaseColour);
        Gl.ColorMask(0, 0, 0, 0);
        Gl.DepthMask(1);
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.Disable(Gl.BLEND); _blendOn = false;
        Gl.UseProgram(Programs._progDepthFeat);
        Gl.UniformMatrix4fv(Programs._depthFeatVp, 1, 0, (float*)vp);
        Gl.Uniform1f(Programs._depthFeatMip, Renderer.CutMip.V);
        Gl.Uniform1i(Programs._depthFeatTex, 0);
        Gl.ActiveTexture(Gl.TEXTURE0);
        UploadFeatureTerms(Programs._depthFeatFeat, viewport);
        uint boundVao = 0;
        int boundCull = -1, boundTex = -1;
        float boundCut = float.NaN;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int g = from; g < to; g++)
        {
            if (Issue._onlyGobSet && _gGob[g] != Issue._onlyGob) continue;
            if (!BlendLaysDepth(g)) continue;
            int s = _gStart[g];
            Gl.Uniform1i(Programs._depthFeatBase, s);
            float cut = AlloyClasses.Discard(_gCls[g]);
            if (cut != boundCut) { Gl.Uniform1f(Programs._depthFeatCut, cut); boundCut = cut; }
            if (!_bindless && cut >= 0f && _gTex[g] != boundTex)
            { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_gTex[g]); boundTex = _gTex[g]; }
            BindBatchState(g, ref boundCull, ref boundVao);
            if (indirect) CullPass.Draw(_gMode[g], g, s, _gLen[g]);
            else
                Gl.MultiDrawElementsBaseVertex(_gMode[g], counts + s, GlUnsignedShort,
                                               (void**)(offs + s), _gLen[g], bases + s);
        }
        if (indirect) { CullPass.UnbindForDraw(); Issue.BindStagedSlots(); }
        Gl.ColorMask(1, 1, 1, 1);
        // Hand back what the blended class left and the copy, water and additive passes read as already set.
        Gl.DepthMask(0);
        Gl.Enable(Gl.BLEND); _blendOn = true;
    }

    /// <summary>A solid red band into the frame target for each upload <see cref="StreamRing"/> failed this
    /// frame -- top edge the draw slots, left the cull source, right the previous bones. A scissored clear: no
    /// program, nothing that can itself fail, and the 4.6 context's state is Alloy's alone.</summary>
    private static void PaintStreamFailures(int* display)
    {
        int bands = StreamRing.TakePaint();
        if (bands == 0) return;
        int x = display[0], y = display[1], w = display[2], h = display[3], t = Math.Max(8, h / 90);
        RebindFrameTarget();
        Gl.ColorMask(1, 1, 1, 1);
        Gl.ClearColor(1f, 0f, 0f, 1f);
        Gl.Enable(Gl.SCISSOR_TEST);
        if ((bands & 1 << (int)StreamRing.Site.DrawSlots) != 0) { Gl.Scissor(x, y + h - t, w, t); Gl.Clear(Gl.COLOR_BUFFER_BIT); }
        if ((bands & 1 << (int)StreamRing.Site.CullSource) != 0) { Gl.Scissor(x, y, t, h); Gl.Clear(Gl.COLOR_BUFFER_BIT); }
        if ((bands & 1 << (int)StreamRing.Site.PrevBones) != 0) { Gl.Scissor(x + w - t, y, t, h); Gl.Clear(Gl.COLOR_BUFFER_BIT); }
        Gl.Disable(Gl.SCISSOR_TEST);
    }

    /// <summary>Does <see cref="BlendedDepthSweep"/> lay this batch's depth? Only through the alpha-testing
    /// feature stage (texture alpha times the per-draw multiplier against the class's 0.2), or a faded-out
    /// surface would stamp solid depth over everything drawn after it. A WASH is in -- vanilla draws that part
    /// alpha-tested and depth-writing in bucket 1; the roof pair and flowmap are out, their draw slides it.</summary>
    private static bool BlendLaysDepth(int g)
    {
        int f = _gFeat[g];
        if (f == (int)ShaderFeature.Flowmap || f == (int)ShaderFeature.RoofKn || f == (int)ShaderFeature.RoofKnM) return false;
        return !_bindless || _gLit[g];
    }
    /// <summary>The frame's water batches, depth only, under <paramref name="vp"/> into whatever target the
    /// caller attached -- the cover map's roof over a pool's bed. The prepass program on the
    /// staged records; the caster bracket bound the record buffer, this binds the staged slot order it needs.</summary>
    internal static void DrawWaterOccluders(Matrix4x4* vp)
    {
        if (_gWaterEnd <= _gBlendEnd || !Programs.EnsureDepthProgram() || !Issue.BindStagedSlots()) return;
        Gl.UseProgram(Programs._progDepth);
        Gl.UniformMatrix4fv(Programs._depthVp, 1, 0, (float*)vp);
        uint boundVao = 0;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        for (int g = _gBlendEnd; g < _gWaterEnd; g++)
        {
            int s = _gStart[g];
            Gl.Uniform1i(Programs._depthBase, s);
            uint vao = VaoName(_gVao[g], _gCls[g]);
            if (vao != boundVao) { Gl.BindVertexArray(vao); boundVao = vao; }
            Gl.MultiDrawElementsBaseVertex(_gMode[g], counts + s, GlUnsignedShort, (void**)(offs + s), _gLen[g], bases + s);
        }
        Gl.BindVertexArray(0);
    }

    /// <summary><c>/alloy blenddepth</c>: the blended class's depth sweep (default on) -- the A/B for a
    /// translucent surface water or an additive effect draws over.</summary>
    private static bool _blendDepth => Renderer.BlendDepth.V;

    /// <summary>The depth prepass over the opaque range, submitting cull output set <paramref name="phase"/>.
    /// Called twice under the cull -- <see cref="CullPass.PhasePrepass"/> for phase one's survivors, then
    /// <see cref="CullPass.PhaseRetest"/> for the re-tested rejects. The retest is bracketed by the caller's
    /// <see cref="GpuTerm.PrepassRetest"/> (one query at a time) and adds nothing to the staged counters.</summary>
    private static void DepthPrepassPass(int groups, Matrix4x4* vp, int* viewport, int phase)
    {
        bool retest = phase == CullPass.PhaseRetest;
        if (!retest) Array.Clear(_gPrepassed, 0, groups);
        if (!_depthPrepass || !Programs.EnsureDepthProgram()) return;
        int skipped = 0, drawn = 0;
        bool indirect = CullPass.Active;
        if (!indirect && retest) return;
        GpuBegin(GpuTerm.Prepass);
        // #243: a list the pyramid did not produce. The prepass is what the hi-Z pyramid is built from, so
        // it cannot be culled against the pyramid it produces; #489 tests phase one against the PRIOR
        // pass's pyramid instead and corrects that pyramid's rejections in phase two. A part the prepass
        // skips is one the pyramid does not know about, which only ever makes the colour cull more
        // conservative. The dispatch spans every batch since #290; this loop still walks the opaque range
        // alone, so the blending batches' commands in this phase's buffer are simply never submitted.
        if (indirect) CullPass.BindForDraw(phase);
        // #493: the vis-ID attachment rides these draws; draw buffer 0 is NONE under it, so the colour mask
        // stays open for attachment 1 alone. A probe capture is its own target and never gets one.
        // Re-attached for the retest too (End detached it after phase one): with the mask open and no
        // attachment 1, the stage's output would land undefined in the colour attachment.
        bool idsWanted = !_capturing && PostChain.Armed;
        bool ids = idsWanted && IdTarget.Begin(viewport[2], viewport[3], clear: !retest);
        if (ids) Gl.ColorMask(1, 1, 1, 1); else Gl.ColorMask(0, 0, 0, 0);
        Gl.DepthFunc(Gl.LESS);
        uint boundVao = 0;
        // #292: the prepass makes the SAME per-batch cull decision as the colour pass, off the same
        // _gNoCull, because #119's constraint is about the depth the colour pass then tests LEQUAL against.
        // That constraint says every owned vertex stage must compute gl_Position character-for-character
        // identically; culling is the other half of it -- an identical clip position on a triangle only one
        // of the two passes rasterises still loses the fragment. Same batches, same state, same depth.
        int boundCull = -1;
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        // #322: TWO SWEEPS -- all plain batches under one program bind, then all keyhole ones under the
        // other. Dissolve membership is per-frame camera state (thousands of parts keyhole at once under a
        // roofed area), so the cut order interleaves the two programs, and binding per alternation was the
        // measured Prepass tower (~5,5ms CPU). Depth-only LESS writes are order-independent, so the reorder
        // costs nothing; each program now binds at most once per pass.
        for (int sweep = 0; sweep < 2; sweep++)
        {
            bool dis = sweep == 1;
            bool bound = false;
            // #336: the keyhole sweep timed as its own term, and skippable (/alloy prepasskey 0).
            if (dis) { GpuEnd(GpuTerm.Prepass); if (!_prepassKey) break; GpuBegin(GpuTerm.PrepassKey); }
            for (int g = 0; g < groups; g++)
            {
                if (ShaderFeatures.OutOfPrepass(_gFeat[g])) { if (sweep == 0) skipped += _gLen[g]; continue; }
                // #126: a cutout discards, so its prepass depth would be its SOLID silhouette and the colour
                // pass's LEQUAL would cull whatever stands in its holes -- grass turning the ground behind it
                // into a rectangle. ADR 0011 leaves cutouts-in-the-prepass a MEASUREMENT question (they cost
                // a texture fetch per fragment to save shading later); this is the measurement's off side,
                // and Counters._prepassSkipped is what says how much overdraw it leaves on the table.
                // ...and the blending classes: prepassed, a blended part's SOLID silhouette lands in the depth
                // buffer (and the hi-Z pyramid) before any colour pass, and every blended surface behind it
                // is rejected -- a floor decal under a light shaft, a translucent creature behind a window.
                // ADR 0011: blended tests against the OPAQUE set's depth; the per-creature one-layer sweep is
                // BlendedDepthPrepass's job, and water writes its own depth in its own pass.
                if (_gCls[g] >= (byte)AlloyClass.Cutout) { if (sweep == 0) skipped += _gLen[g]; continue; }
                if (_gDis[g] != dis) continue;
                if (dis && Programs._progDepthKey == 0) { skipped += _gLen[g]; continue; }
                if (!bound)
                {
                    bound = true;
                    Gl.UseProgram(dis ? Programs._progDepthKey : Programs._progDepth);
                    Gl.UniformMatrix4fv(dis ? Programs._depthKeyVp : Programs._depthVp, 1, 0, (float*)vp);
                    if (dis) UploadFeatureTerms(Programs._depthKeyFeat, viewport);
                }
                int s = _gStart[g];
                _gPrepassed[g] = true;
                if (dis && !retest) Counters._prepassKeyed += _gLen[g];
                Gl.Uniform1i(dis ? Programs._depthKeyBase : Programs._depthBase, s);
                BindBatchState(g, ref boundCull, ref boundVao);
                if (indirect) CullPass.Draw(_gMode[g], g, s, _gLen[g]);
                else
                    Gl.MultiDrawElementsBaseVertex(_gMode[g], counts + s, GlUnsignedShort,
                                                   (void**)(offs + s), _gLen[g], bases + s);
                drawn += _gLen[g];
            }
        }
        // The feature sweep: cutout batches (their holes are the colour pass's own discard, made here with the
        // same texel and threshold) and wind-displaced ones (the lit program's own vertex text displaces them
        // identically), dissolving or not -- the stage runs CxKeyhole too, off the lit vertex stage's own
        // vWorld/vDepth, so a keyholed hedge keeps its depth (and its AO) as the camera closes in. Roof features keep their anchor discard and player-relative clip out of it, and
        // flowmap slides its uv; those three stay excluded. Depth-only LESS writes are order-independent, so
        // this runs as its own sweep under one program bind.
        GpuEnd(GpuTerm.PrepassKey);
        if (_prepassFeat && Programs.EnsureDepthFeatProgram())
        fixed (int* counts = _dCount, bases = _dBase)
        fixed (nint* offs = _dOff)
        {
            GpuBegin(GpuTerm.PrepassFeat);
            bool bound = false; int boundTex = -1; float boundCut = float.NaN;
            for (int g = 0; g < groups; g++)
            {
                if (!FeatPrepassable(g) || (_gDis[g] && !_prepassKey)) continue;
                // #153: only lit records carry handles, and under bindless the stage reads nothing else
                if (_bindless && !_gLit[g]) continue;
                if (!bound)
                {
                    bound = true;
                    Gl.UseProgram(Programs._progDepthFeat);
                    Gl.UniformMatrix4fv(Programs._depthFeatVp, 1, 0, (float*)vp);
                    Gl.Uniform1f(Programs._depthFeatMip, Renderer.CutMip.V);
                    Gl.Uniform1i(Programs._depthFeatTex, 0);
                    Gl.ActiveTexture(Gl.TEXTURE0);
                    UploadFeatureTerms(Programs._depthFeatFeat, viewport);
                    Db.PltPalettes.Bind();   // a PLT cutout's alpha is its palette's, resolved by the stage
                }
                int s = _gStart[g];
                _gPrepassed[g] = true;
                Gl.Uniform1i(Programs._depthFeatBase, s);
                float cut = AlloyClasses.Discard(_gCls[g]);
                if (cut != boundCut) { Gl.Uniform1f(Programs._depthFeatCut, cut); boundCut = cut; }
                if (!_bindless && cut >= 0f && _gTex[g] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_gTex[g]); boundTex = _gTex[g]; }
                BindBatchState(g, ref boundCull, ref boundVao);
                if (indirect) CullPass.Draw(_gMode[g], g, s, _gLen[g]);
                else
                    Gl.MultiDrawElementsBaseVertex(_gMode[g], counts + s, GlUnsignedShort,
                                                   (void**)(offs + s), _gLen[g], bases + s);
                drawn += _gLen[g]; skipped -= _gLen[g]; if (!retest) Counters._prepassFeatDraws += _gLen[g];
            }
            GpuEnd(GpuTerm.PrepassFeat);
        }
        if (indirect) { CullPass.UnbindForDraw(); Issue.BindStagedSlots(); }
        if (idsWanted) IdTarget.End();   // unconditional past Begin: the draw-buffer state is the FBO's
        Gl.ColorMask(1, 1, 1, 1);
        // Counted from what this loop submitted, not from the pass's total draw count: since #126 that total
        // spans the blending classes too, and the prepass walks the opaque range alone.
        // #243: under the cull this is what was STAGED, not what the GPU kept -- the count that decides is in
        // the parameter buffer and does not come back. The period line's cull clause carries the kept figure;
        // this one is the ceiling it culls from, and reading it as "draws" would overstate the pass.
        if (!retest) { Counters._prepassDraws += drawn; Counters._prepassSkipped += skipped; }
        // #214: OUR depth attachment, not the engine's -- a texture by construction, so consumers get a
        // sampleable name rather than whatever the engine happened to allocate.
        DepthResource.Publish((int)Gl.ATTACHMENT_TEXTURE, (int)PostChain.DepthTex, viewport, PassGen);
    }

    /// <summary>Does the feature sweep of the prepass take this batch? The cutout class and every displacing
    /// feature the plain sweep skipped -- minus the roof pair (anchor discard, player-relative clip) and
    /// flowmap (a slid diffuse uv would punch holes the colour pass does not).</summary>
    private static bool FeatPrepassable(int g)
    {
        int f = _gFeat[g];
        if (f == (int)ShaderFeature.Flowmap || f == (int)ShaderFeature.RoofKn || f == (int)ShaderFeature.RoofKnM) return false;
        // a blended class must not write depth ahead of the colour pass: it would occlude what it blends over
        if (AlloyClasses.Blends(_gCls[g])) return false;
        // CX-189: nor a coverage cutout -- a 0.2-cut depth laid first would hide the background its fringe blends over
        if (_gA2c[g]) return false;
        // CX-218: nor a slot-1 coverage batch -- this stage tests the DIFFUSE alpha, which on those materials
        // is the env level, so it would lay solid depth under the holes the colour pass punches and those
        // holes would show the clear colour. Slower, never wrong; _gCovN names the upgrade path.
        if (_gCovN[g]) return false;
        return _gCls[g] == (byte)AlloyClass.Cutout || ShaderFeatures.OutOfPrepass(f);
    }

    /// <summary>ADR 0011 step 2, the Alloy opaque set: submit the staged batches, shaded. LEQUAL rather than
    /// EQUAL against the prepass depth -- all three Alloy vertex stages declare <c>invariant gl_Position</c>
    /// over the identical clip expression (<see cref="ClipExpr"/>), so equal is what they produce, but LEQUAL
    /// is also correct when the prepass stood down, which EQUAL is not.</summary>
    private static void OpaqueOwnedPass(int opaqueEnd, Matrix4x4* vp, int* viewport, float near, float far)
    {
        Gl.DepthFunc(Gl.LEQUAL);
        // SAMPLES_PASSED covers the OPAQUE set alone, so the overdraw figure stays comparable with #119's
        // baseline. #357 moved the cutout run out into its own pass behind the sky, which is also what
        // keeps #325's separate opaque/cutout GPU terms apart.
        bool counting = _qSamples.Begin(Gl.SAMPLES_PASSED);
        IssueClass(0, _gCutoutStart, GpuTerm.Opaque, GpuTerm.OpaqueKey, vp, viewport, near, far);
        if (counting) _qSamples.End(Gl.SAMPLES_PASSED);
    }

    /// <summary>The cutout class, in vanilla's own slot -- bucket 1 follows <c>RenderSkyBoxes</c>. It draws
    /// unblended (see <see cref="AlloyClasses.Blends"/>), so the order is kept only because it is the engine's.
    /// Depth mask back ON (the sky left it off) at the opaque set's LEQUAL, and out of the SAMPLES_PASSED
    /// count, which stays the opaque set's overdraw baseline.</summary>
    private static void CutoutOwnedPass(int opaqueEnd, Matrix4x4* vp, int* viewport, float near, float far)
    {
        // CX-189: the coverage tail waits for its own layer whenever one can arm; a capture draws it here, cut
        _a2cHeld = !_capturing && _gA2cStart < opaqueEnd && CoverageLayer.Armed;
        if (!_capturing && !_a2cHeld) CoverageLayer.Idle();
        Gl.DepthMask(1);
        Gl.DepthFunc(Gl.LEQUAL);
        if (_gA2cStart > _gCutoutStart)
            IssueClass(_gCutoutStart, _gA2cStart, GpuTerm.Cutout, GpuTerm.CutoutKey, vp, viewport, near, far);
        // ...and a tail the layer is not taking, on its own term: it sits past the keyhole partition IssueClass splits on
        if (_a2cHeld || _gA2cStart >= opaqueEnd) return;
        GpuBegin(GpuTerm.Coverage);
        Issue.IssueColour(_gA2cStart, opaqueEnd, vp, viewport, near, far);
        GpuEnd(GpuTerm.Coverage);
    }

    /// <summary><see cref="CutoutOwnedPass"/> left the <c>NWN_ALPHA_MODE 2</c> tail for the coverage layer.</summary>
    private static bool _a2cHeld;

    /// <summary><see cref="PassId.CoverageOwned"/>: the held coverage tail through <see cref="CoverageLayer"/>,
    /// scissored to its parts' screen rectangle. A layer that will not open this frame still draws the parts, as
    /// cutouts straight into the frame -- forward lit, since the G-buffer has closed.</summary>
    private static void CoverageOwnedPass(Matrix4x4* vp, int* viewport, float near, float far)
    {
        _a2cHeld = false;
        CoverageRect(vp, viewport, out int x, out int y, out int w, out int h);
        if (w <= 0 || h <= 0) return;
        GpuBegin(GpuTerm.Coverage);
        bool layer = CoverageLayer.Begin(viewport, x, y, w, h);
        Issue._coverPass = layer;
        Issue._forwardOnly = true;
        Gl.DepthMask(1);
        Gl.DepthFunc(Gl.LEQUAL);
        Issue.IssueColour(_gA2cStart, _gOpaqueEnd, vp, viewport, near, far);
        Issue._coverPass = false;
        Issue._forwardOnly = false;
        if (layer) CoverageLayer.End(viewport);
        GpuEnd(GpuTerm.Coverage);
    }

    /// <summary>The viewport-space rectangle the coverage tail's bounding spheres project into, padded a pixel.
    /// A part with no cull sphere of its own (skinned, dangly, a displacing feature -- most hair) takes its
    /// object's world sphere, whose radius the exporter wrote over the animated geometry. ponytail: a part with
    /// neither, or one reaching behind the eye, takes the whole frame.</summary>
    private static void CoverageRect(Matrix4x4* vp, int* viewport, out int x, out int y, out int w, out int h)
    {
        float lx = 1f, ly = 1f, hx = -1f, hy = -1f;
        bool full = false;
        fixed (float* sph = _dSph)
        {
            for (int g = _gA2cStart; g < _gOpaqueEnd && !full; g++)
                for (int k = _gStart[g], e = k + _gLen[g]; k < e && !full; k++)
                {
                    int slot = _dSlot[k];
                    float* sp = sph + (long)slot * 4;
                    var s = new Vector4(sp[0], sp[1], sp[2], sp[3]);
                    if (s.W >= NeverCull)
                    {
                        int part = _oDbPart[slot];
                        s = part >= 0 ? SceneDb.ObjSphere[SceneDb.PartObj[part].Index] : default;
                        if (!(s.W > 0f)) { full = true; break; }
                    }
                    float r = s.W;
                    for (int c = 0; c < 8; c++)
                    {
                        var p = new Vector4(s.X + ((c & 1) != 0 ? r : -r), s.Y + ((c & 2) != 0 ? r : -r),
                                            s.Z + ((c & 4) != 0 ? r : -r), 1f);
                        Vector4 q = Vector4.Transform(p, *vp);
                        if (q.W <= 1e-4f) { full = true; break; }
                        float nx = q.X / q.W, ny = q.Y / q.W;
                        lx = MathF.Min(lx, nx); hx = MathF.Max(hx, nx);
                        ly = MathF.Min(ly, ny); hy = MathF.Max(hy, ny);
                    }
                }
        }
        if (full) { lx = -1f; ly = -1f; hx = 1f; hy = 1f; }
        lx = MathF.Max(lx, -1f); ly = MathF.Max(ly, -1f); hx = MathF.Min(hx, 1f); hy = MathF.Min(hy, 1f);
        if (hx <= lx || hy <= ly) { x = 0; y = 0; w = 0; h = 0; return; }
        int x0 = (int)MathF.Floor((lx * 0.5f + 0.5f) * viewport[2]) - 1, x1 = (int)MathF.Ceiling((hx * 0.5f + 0.5f) * viewport[2]) + 1;
        int y0 = (int)MathF.Floor((ly * 0.5f + 0.5f) * viewport[3]) - 1, y1 = (int)MathF.Ceiling((hy * 0.5f + 0.5f) * viewport[3]) + 1;
        x0 = Math.Max(x0, 0); y0 = Math.Max(y0, 0); x1 = Math.Min(x1, viewport[2]); y1 = Math.Min(y1, viewport[3]);
        x = viewport[0] + x0; y = viewport[1] + y0; w = x1 - x0; h = y1 - y0;
    }

    /// <summary>One opaque class's batches under two GPU terms -- the plain run, then the keyhole dissolve
    /// partition at its tail. The seam keeps the bound program and its rig: nothing between the two ranges
    /// binds one, so only per-batch state is re-sent.</summary>
    private static void IssueClass(int from, int to, GpuTerm plain, GpuTerm keyed,
                                   Matrix4x4* vp, int* viewport, float near, float far)
    {
        int tail = from;
        while (tail < to && !_gDis[tail]) tail++;
        Issue._matchDepth = _depthEqual;
        GpuBegin(plain);
        Issue.IssueColour(from, tail, vp, viewport, near, far);
        GpuEnd(plain);
        if (tail < to)
        {
            Issue._holdProg = true;
            GpuBegin(keyed);
            Issue.IssueColour(tail, to, vp, viewport, near, far);
            GpuEnd(keyed);
        }
        Issue._matchDepth = false;
    }

    /// <summary>Capture the area's IBL probe -- the same staged batches, drawn six more times into a small cube
    /// from the player's position. At the END of the colour bracket, after both on-screen passes: the light
    /// binning is per-pass state each face overwrites, and the next pass rebuilds it anyway. Gated by
    /// <see cref="EnvProbe.Wanted"/>; the frame meters are saved and restored, or capture frames would lie.</summary>
    private static void CaptureProbe(int groups, float far)
    {
        long tProbe = FrameZones.Now;
        // The six faces submit through Issue.IssueBatches, which books its own zone -- so the parent subtracts it
        // exactly as the shim subtracts the Alloy frame. Without this a capture frame counts the submission
        // twice, and a capture frame is precisely the shape that latches the worst-frame snapshot.
        long issue0 = FrameZones.Ticks(FrameZones.Zone.Issue);
        try { CaptureProbeInner(groups, far); }
        finally { FrameZones.Add(FrameZones.Zone.Probe, tProbe, FrameZones.Ticks(FrameZones.Zone.Issue) - issue0); }
    }

    private static void CaptureProbeInner(int groups, float far)
    {
        // the dome stages after the area light: a probe captured before it has only the clear for a sky
        bool domeArrived = EnvProbe.Ready && !_probeDome && _skyN > 0;
        if (!domeArrived && !EnvProbe.Wanted(_eyePos, _frameSeq)) return;
        if (!ProbeLightSettled() || !EnvProbe.LightsSettled(AlloyLights.LastLightCount)) return;
        if (!EnvProbe.Begin(_eyePos, _frameSeq, far, ProbeClear())) return;

        long draws = Counters._draws, batches = Counters._batches;
        float buildMs = AlloyLights.LastBuildMs;   // the [alloy] line's cluster timer is the SCREEN's build
        Vector3 eye = _eyePos;
        _capturing = true;
        _eyePos = EnvProbe.Origin;
        (float fnear, float ffar) = EnvProbe.FacePlanes;
        int* faceVp = stackalloc int[4] { 0, 0, EnvProbe.Size, EnvProbe.Size };
        bool ok = true;
        Gl.DepthFunc(Gl.LEQUAL);   // the sky survives only where no face geometry wrote depth
        for (int f = 0; f < 6 && ok; f++)
        {
            ok = EnvProbe.Face(f, out Matrix4x4 vp);
            if (!ok) break;
            Matrix4x4 view = EnvProbe.FaceView(f);
            // A cube face is exactly 90deg, so the projection's p00 and p11 are both cot(45) = 1.
            AlloyLights.Build((float*)&view, 1f, 1f, fnear, ffar, EnvProbe.Size, EnvProbe.Size, tally: false);
            Gl.ColorMask(1, 1, 1, 1);
            Issue.IssueBatches(0, groups, (float*)&vp, faceVp, fnear, ffar);
            Gl.ColorMask(1, 1, 1, 0);   // the dome is infinitely far: the clear's alpha 0 already says so
            // the real sky behind the world, so a wet street mirrors the dome and not the clear colour; the
            // dome is parked at this frame's camera, which is where the probe stands
            // CX-88: the WHOLE dome, hosted stage included -- an Alloy sky program takes uVP like every
            // other Alloy draw, so a cube face is just another view. #136's engine-program replay could not
            // be pointed at one, which is what the ProbeClear sky stand-in was for.
            SkyOwnedPass(&vp, faceVp);
            Gl.DepthMask(1);   // the sky pass leaves the mask off, and the next face's depth clear honours it
        }
        Gl.ColorMask(1, 1, 1, 1);
        if (ok) { EnvProbe.End(); _probeDome = _skyN > 0; } else Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        _capturing = false;
        _eyePos = eye;
        Counters._draws = draws; Counters._batches = batches;
        AlloyLights.LastBuildMs = buildMs;
    }

    /// <summary>The planar reflection -- the opaque set mirrored across the ground at the player's feet into
    /// <see cref="Ssr"/>'s half-res target, filling the holes a screen-space trace leaves. Probe-capture flags
    /// plus <see cref="_planar"/>, mirrored eye, winding flipped, no local light, at <c>light.ssrplanarres</c>;
    /// the lit stage discards what lies under <c>uMirrorZ</c>. ponytail: one plane, the player's.</summary>
    private static void PlanarPass(Matrix4x4* vp, float near, float far, int* viewport)
    {
        if (_runtime == null || !Ssr.BeginPlanar(viewport[2], viewport[3], out int pw, out int ph)) return;
        float z = Rt.PlayerPos.Z;
        // the reflection across z = h, row vectors (p' = p M): z' = 2h - z
        Matrix4x4 m = Matrix4x4.Identity;
        m.M33 = -1f; m.M43 = 2f * z;
        Matrix4x4 mVp = m * *vp;
        long draws = Counters._draws, batches = Counters._batches;
        Vector3 eye = _eyePos;
        int* pvp = stackalloc int[4] { 0, 0, pw, ph };
        _capturing = _planar = true;
        _mirrorZ = z;
        _eyePos = new Vector3(eye.X, eye.Y, 2f * z - eye.Z);
        new PassState(depth: true, depthWrite: true, cull: true, srgb: false).Apply();
        Gl.DepthFunc(Gl.LEQUAL);
        Gl.FrontFace(Gl.CW);
        // the colour phase's list is this camera's: re-cull for the mirror, dropping what is off its frustum, under
        // the plane, or smaller than light.ssrplanarcull mirror pixels (the threshold is in main-view pixels)
        bool indirect = CullPass.Active && CullPass.Dispatch(CullPass.PhaseMirror, &mVp, hiZ: false, PassGen,
                                                             Lighting.SsrPlanarCull.V * Lighting.SsrPlanarRes.V, z);
        if (indirect) CullPass.BindForDraw(CullPass.PhaseMirror);
        Issue._holdProg = false;
        AlloyLights.BorrowedTable = true;
        try { Issue.IssueBatches(0, _gOpaqueEnd, (float*)&mVp, pvp, near, far, indirect); }
        finally
        {
            AlloyLights.BorrowedTable = false;
            if (indirect) { CullPass.UnbindForDraw(); Issue.BindStagedSlots(); }
            Gl.FrontFace(Gl.CCW);
            _capturing = _planar = false;
            _eyePos = eye;
            Counters._draws = draws; Counters._batches = batches;
            Ssr.EndPlanar(z);
            Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        }
    }

    /// <summary>The area light the last frame saw, for <see cref="ProbeLightSettled"/>.</summary>
    private static Vector3 _probeLightWas;
    /// <summary>Did the live probe capture a dome? One that did not re-captures when the dome stages.</summary>
    private static bool _probeDome;

    /// <summary>Is the area's light worth capturing? The engine fades ambient and sun in from zero over the
    /// first seconds of an area, with no dome staged yet; a probe captured there is black, and nothing
    /// re-captures it until the player walks <c>MoveDist</c>. So: wait for the fade to stop moving, and for
    /// either a dome or a light that is not black. A genuinely black area never captures, which reads the same.</summary>
    private static bool ProbeLightSettled()
    {
        Vector3 l = (FrameInputs.HasAreaAmbient ? FrameInputs.AreaAmbient : default)
                  + (FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuse : default);
        bool still = Vector3.DistanceSquared(l, _probeLightWas) < ProbeSettleEps * ProbeSettleEps;
        _probeLightWas = l;
        return still && (_skyN > 0 || MathF.Max(l.X, MathF.Max(l.Y, l.Z)) > ProbeDarkFloor);
    }

    /// <summary>Frame-to-frame change in area light below which the fade-in is over.</summary>
    private const float ProbeSettleEps = 1e-4f;
    /// <summary>Area light (ambient + sun, brightest channel) below which a domeless area is still fading in.</summary>
    private const float ProbeDarkFloor = 1e-3f;

    /// <summary>What the probe's faces are cleared to -- the sky stand-in where no dome covers, i.e. an area
    /// with the skybox off or one whose sky refused to stage. The sky's own average when fog has read one,
    /// else the area's ambient plus half its directional: a black clear would make outdoor metal reflect a
    /// black dome.</summary>
    private static Vector3 ProbeClear()
    {
        if (SceneFog.TrySky(out Vector3 sky)) return sky;
        Vector3 amb = FrameInputs.HasAreaAmbient
            ? FrameInputs.AreaAmbient * AlloyLights.AmbientScale
            : new Vector3(DefaultAmbient);
        Vector3 sun = FrameInputs.HasAreaDiffuse ? FrameInputs.AreaDiffuse * AlloyLights.SunScale : default;
        return amb + sun * 0.5f;
    }
}
