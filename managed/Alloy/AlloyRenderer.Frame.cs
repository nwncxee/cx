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

    /// <summary>GL_BLEND back to what <see cref="_blendOn"/> says, after a pass that toggled it itself: the batch
    /// loops (<c>Issue.IssueBatches</c>) read the tracked flag as what is already set.</summary>
    private static void HandBackBlend() { if (_blendOn) Gl.Enable(Gl.BLEND); else Gl.Disable(Gl.BLEND); }

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
        // #257/ADR 0032: LatchFrameTarget read this in the ENGINE's context before the view's bracket opened.
        ref Target t = ref _target;
        HoldPassTarget(in t);   // #375: the ONE read per pass; the post stack rebinds from this copy
        // #391: TWO viewports. `display` is the engine's -- where the resolve and the post stack go, and what
        // the 2D UI draws at. `viewport` is the RENDER one every owned pass sizes from: the display scaled by
        // alloy.scale, origin 0 (the owned targets are their own allocations, so an offset would only mean
        // drawing into their corner).
        display[0] = t.Vp0; display[1] = t.Vp1; display[2] = t.Vp2; display[3] = t.Vp3;
        viewport[0] = 0; viewport[1] = 0;
        viewport[2] = PostChain.RenderSize(display[2]); viewport[3] = PostChain.RenderSize(display[3]);
        bool srgb = t.Srgb;
        // Sized to the DEFAULT framebuffer -- the WINDOW, from the engine's own screen globals, which is what
        // the hand-back blits 1:1 and the size GuiPass's flush keys on too: two sources for one quantity made
        // every flush reallocate the image (and lose the world frame in it) wherever they disagreed. The pass
        // VIEWPORT can be smaller; the world then lands in the image where it would land in the window.
        if (GuiPass.Screen(out int dw, out int dh)) PresentTarget.Ensure(dw, dh);
        float* engClear = stackalloc float[4] { t.Clear0, t.Clear1, t.Clear2, t.Clear3 };
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
        // a Vulkan frame whose TAA resolve did not build draws unjittered
        if (PostChain.TaaOn && !_capturing && !PostStack.TaaVkFailed)
        {
            PostChain.Jitter(_frameSeq, PostChain.RenderSize(t.Vp2), PostChain.RenderSize(t.Vp3), out jx, out jy);
            proj.M31 += jx * proj.M34; proj.M32 += jy * proj.M34;
        }
        PostChain.JitterX = jx; PostChain.JitterY = jy;
        _projMtx = proj;   // #347: what a spliced stock file reads as m_proj
        Matrix4x4 vp = view * proj;
        // every froxel input comes off THIS pass's projection, never the capture side (see TryPerspective)
        bool perspective = AlloyLights.TryPerspective(proj, out float p00, out float p11,
                                                      out float near, out float far);
        // #120: this pass's eye, for the specular view vector. Same row-vector convention as above, so the
        // inverse view's translation IS the eye. Pass-local state, read off this pass's own stacks -- the
        // SCENE camera is not derived here any more (#257 reads it whole at the anchor), so there is no
        // contest between passes left to resolve. Ahead of the frustum, which measures its fog clamp from it.
        if (perspective && Matrix4x4.Invert(view, out Matrix4x4 camToWorld)) _eyePos = camToWorld.Translation;
        SetPassFrustum(vp, perspective);   // #330: the route gate's planes, this pass's own
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
        FrameZones.Add(FrameZones.Zone.Enter, tEnter);
        Step("deferred bracket work: texture deletes, uploads and handle mints");
        long tOwn = FrameZones.Now;
        // #211's queued texture deletes, CX-117's registry uploads/mints, the master PLT palette and the
        // per-record colour rows: the frame's context-bound deferred work, declared once in Arm and run here
        // because the colour bracket is normally the frame's FIRST. Whichever bracket runs it first is the one
        // that does it, and GlContextService's health bracket then stands down for the frame -- its own, the
        // GUI flush's and the render thread's all route through the one entry point for that reason.
        GlContextService.RunInBracketHere();
        Step("ApplySwaps");
        ApplySwaps();   // #386: swapped texture names take their new handles here, in the context that owns residency
        FrameZones.Add(FrameZones.Zone.Own, tOwn);
        long tPrep = FrameZones.Now, lights0 = FrameZones.Ticks(FrameZones.Zone.Lights);
        Step("EnsureProgram");
        Programs.DecideBindlessVk(); ContentPrograms.EnsureWanted();
        // #117/#119: the lighting rig, the GL state and the batch BUILD are bracket preamble -- both
        // colour-bracket passes draw the same staged batches, and building twice would revalidate (and
        // re-stamp OwnGen for) every Alloy part twice. The passes come off FrameGraph, not off call order.
        if (lightingOn)
        {
            long tLights = FrameZones.Now;
            Step("the lighting rig");
            AlloyLights.Upload(_frameSeq);
            // The cluster buffers are only meaningful once THIS pass's build has run: a skipped build
            // leaves them holding another camera's binning (or, before the first dispatch, undefined
            // BufferData contents), and a lit part would then sample arbitrary light indices.
            lightingOn = AlloyLights.Build(view, p00, p11, near, far, viewport[2], viewport[3], tally: true);
            FrameZones.Add(FrameZones.Zone.Lights, tLights);
        }
        // #292: cull ON is the engine's default -- Scene::RenderDrawBucket @0x46d060 leaves
        // Enable(CULL_FACE) + SetCullFace(BACK) behind on every exit, and that is the state its
        // sub-list 0 (everything not marked `twosided`) draws in. The batch loops override it per batch;
        // this states the default the passes with no batch of their own inherit (GlPreamble).
        _blendOn = false;
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
        Step("the water program");
        _waterArmed = FrameSnapshot.Usable && lightingOn && true;
        FrameZones.Add(FrameZones.Zone.Prep, tPrep, FrameZones.Ticks(FrameZones.Zone.Lights) - lights0);
        // #139/#132: from here to PassId.PostProcess the colour passes draw into an RGBA16F target with a
        // depth attachment of Alloy's own, and everything they write is LINEAR. Asked BEFORE anything is
        // staged, because a refusal is no longer survivable: there is no display-space variant of the
        // Alloy stages to fall back to, so the whole pass stands down and the engine renders it vanilla.
        // Staging first would stamp OwnGen on parts the iterator then suppresses and nobody draws.
        long tPostBegin = FrameZones.Now;
        Step("PostChain.Begin");
        bool postOk = PostChain.Begin(viewport, engClear);
        FrameZones.Add(FrameZones.Zone.Post, tPostBegin);
        if (!postOk) { DbNoDraw("PostChain.Begin refused"); return false; }
        long tPump = FrameZones.Now;
        Step("the arena pumps");
        GpuFrameStart();
        // #287's scale, hoisted ahead of the staging walk: the automatic-LOD pick reads it there, and the
        // cull dispatch below is handed the same figure.
        _lodScale = perspective ? p11 * viewport[3] * 0.5f : 0f;
        MeshChart.Pump(); // ...and of the chart arenas (#673)
        CX.Alloy.Db.MeshArena.Pump();   // CX-114: the one model-space arena -- frees land and both mirrors go up
        FrameZones.Add(FrameZones.Zone.Pump, tPump);
        long tBuild = FrameZones.Now;
        Step("BuildBatches");
        int groups = BuildBatches(lightingOn);
        FrameZones.Add(FrameZones.Zone.Build, tBuild);
        // CX-120: the one point both scene layers converge on with the frame's inputs final and its records
        // staged -- so the two modes' dumps are of the same thing and can be diffed line for line.
        FrameDiffDump();
        CreatureParityDump();   // CX-120: the composed body, per node, in both layers' one line format
        if (groups <= 0) { DbNoDraw("BuildBatches produced no batch"); return false; }
        long tUp = FrameZones.Now;
        Step("the record and handle upload");
        // The MAIN view's render size and unjittered camera, noted before anything stages: every native stage's pass,
        // and every program a probe face or a reflection draws, reads this one camera (the cx* frame uniforms).
        if (!_capturing) StageResources.NoteView(viewport, &vpClean, _eyePos);
        StageSky();
        bool drawsUp = Issue.UploadDraws();
        Db.PltColors.Upload();   // the frame-start push ran before this frame's PLT rows were staged
        Db.ContentTable.Upload();
        // CX-120: with the frame's records staged AND pushed, a record that still differs from the mirror is
        // one the dirty list never named -- the change-driven build's own bug class.
        StaleAuditTick();
        if (!drawsUp) return false;
        FrameZones.Add(FrameZones.Zone.Upload, tUp);
        // The frame's head for native stages: every stage a host staged above or drew last frame gets its images
        // (re)made and its passes run here, before the first draw pass and outside every other GPU term.
        if (!_capturing)
        {
            Step("the native stages' passes");
            GpuBegin(GpuTerm.Passes);
            StageResources.RunFrame(viewport, _log);
            GpuEnd(GpuTerm.Passes);
        }
        // #243: Alloy's own visibility, dispatched twice -- frustum-only for the depth prepass, then
        // frustum + hi-Z for the colour pass once the prepass has filled the depth the pyramid is
        // built from.
        // #290: staged from EVERY batch now, not the opaque prefix. _gOpaqueEnd is handed over as
        // the COMPACTING boundary instead: batches above it are sorted back-to-front by a second
        // dispatch rather than appended by the atomic.
        long tCull = FrameZones.Now;
        Step("the cull dispatch");
        // The batch list's draw count, read off the last batch rather than from _drawN -- StageSky
        // appends to _drawN below and its draws have no source record and no batch of their own.
        int cullDraws = _gStart[groups - 1] + _gLen[groups - 1];
        // #287: pixels per unit of world radius at unit depth, the LOD test's one input. Zero when
        // this pass has no perspective projection, and the test then stands down.
        float lodScale = _lodScale;
        // the camera the Vulkan frame draws through, so the pyramid tests against the depth it reduces
        CullPassVk.Stage(_capturing ? 0 : groups, cullDraws, _dSrc, _dSph, _recOwnCap + MaxSkyDraws,
                         lodScale, _gOpaqueEnd, _eyePos, &vp);
        FrameZones.Add(FrameZones.Zone.Cull, tCull);
        f.View = view; f.Proj = proj; f.Vp = vp; f.VpClean = vpClean;
        f.Jx = jx; f.Jy = jy; f.Near = near; f.Far = far; f.P00 = p00; f.P11 = p11;
        f.Perspective = perspective; f.LightingOn = lightingOn; f.Srgb = srgb; f.Groups = groups;
        PresentTarget.MarkComposed();
        return true;
    }

    /// <summary>Bind the colour frame's target, viewport and depth state again after something bound its own.</summary>
    private static void ReenterColourFrame(int* viewport, bool srgb, bool cull)
    {
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, PostChain.Fbo);
        Gl.Viewport(viewport[0], viewport[1], viewport[2], viewport[3]);
        new PassState(depth: true, depthWrite: true, cull: cull, srgb: srgb).Apply();
    }

    /// <summary>The world frame: <see cref="BeginOwnedFrame"/> stages the batches, then <see cref="VulkanFrame"/>
    /// records and submits every pass in FrameGraph order.</summary>
    private static void DrawOwnedInner()
    {
        int* display = stackalloc int[4];
        int* viewport = stackalloc int[4];
        if (!BeginOwnedFrame(display, viewport, out OwnedFrame f)) return;
        long tVk = FrameZones.Now;
        // CX-273: the decal tests' view-depth terms
        DecalLayer.SetCamera(in f.Proj);
        VkFrameCamera(in f.View, in f.Proj);
        VulkanFrame.Run(_log, f.Vp, f.VpClean, f.View, f.Proj, viewport, display, f.Near, f.Far, f.LightingOn,
                        f.Perspective, f.Groups);
        FrameZones.Add(FrameZones.Zone.Vulkan, tVk);
    }


    /// <summary>The colour bracket's step trace: world frames it still names, and the frame it is naming.</summary>
    private sealed class StepTraceState
    {
        public int FramesLeft = StepTraceFrames, Frame = -1;
    }
    private static StepTraceState _steps = new();

    /// <summary>World frames whose colour-bracket steps <see cref="Step"/> names, from the first.</summary>
    private const int StepTraceFrames = 5;

    /// <summary>Under <c>alloy.gldebug=1</c>, for the first world frames: name the step about to run, after waiting
    /// for the GPU. The debug log is synchronous, so a CPU crash or a driver reset leaves its step as the last line.</summary>
    internal static void Step(string what)
    {
        if (!GlContextService.Debug) return;
        StepTraceState s = _steps;
        if (s.Frame != _frameSeq)
        {
            if (s.FramesLeft <= 0) return;
            s.FramesLeft--; s.Frame = _frameSeq;
        }
        // wait for the GPU at every traced step: a driver reset then ends the log AT the faulting stage
        (int now, int peak) = BindlessLedger.ResidentCount;
        _log?.Always($"AlloyRenderer: [gldebug] frame {_frameSeq}: {what} ({now} handles resident, peak {peak})");
    }

    /// <summary>A pass's name for <see cref="Step"/>; only reached under <c>alloy.gldebug=1</c>.</summary>
    internal static string PassName(PassId id) => GlContextService.Debug ? id.ToString() : "";

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
        for (int g = _gBlendEnd; g < _gWaterEnd; g++)
                for (int d = _gStart[g], e = d + _gLen[g]; d < e; d++)
                {
                    ref Sphere s = ref _dSph[_dSlot[d]];
                    if (s.Radius >= NeverCull || !SphereRect(vp, vw, vh, p00, p11, s.Center.X, s.Center.Y, s.Center.Z, s.Radius,
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
    /// composite, or it would occlude what OIT accumulates. A RUN of batches a call; <c>blenddepth 0</c> off.</summary>
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
        // uDrawBase is the RUN's start, so a run may only grow where the next batch's commands begin exactly
        // where the last one's ended.
        int runStart = 0, runLen = 0;
        uint runMode = 0;
        int was = Submit.At(Submit.Stage.BlendDepth);
        void Flush()
        {
            if (runLen == 0) return;
            if (indirect) CullPass.DrawRun(runMode, runStart, runLen);
            else MultiDrawRange(runMode, runStart, runLen);
            runLen = 0;
        }
        for (int g = from; g < to; g++)
        {
            if (Issue._onlyGobSet && _gGob[g] != Issue._onlyGob) continue;
            if (!BlendLaysDepth(g)) continue;
            int s = _gStart[g];
            float cut = AlloyClasses.Discard(_gCls[g]);
            bool compacted = indirect && !CullPass.Sorted(g);
            bool extend = runLen > 0 && !compacted && s == runStart + runLen && _gMode[g] == runMode
                          && cut == boundCut && VaoName(_gVao[g], _gCls[g]) == boundVao
                          && (_gNoCull[g] ? 0 : 1) == boundCull
                          && (_bindless || cut < 0f || _gTex[g] == boundTex);
            if (extend) { runLen += _gLen[g]; continue; }
            Flush();
            if (cut != boundCut) { Gl.Uniform1f(Programs._depthFeatCut, cut); boundCut = cut; }
            if (!_bindless && cut >= 0f && _gTex[g] != boundTex)
            { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_gTex[g]); boundTex = _gTex[g]; }
            BindBatchState(g, ref boundCull, ref boundVao);
            Gl.Uniform1i(Programs._depthFeatBase, s);
            // A compacted batch's survivor count lives in the parameter buffer the CPU cannot read, so it
            // submits alone; only the sorted region's dense commands merge.
            if (compacted) { CullPass.Draw(_gMode[g], g, s, _gLen[g]); continue; }
            runStart = s; runMode = _gMode[g]; runLen = _gLen[g];
        }
        Flush();
        Submit.Back(was);
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
    /// alpha-tested and depth-writing in bucket 1; a content pair whose depth is its own is out, its draw moves it.</summary>
    private static bool BlendLaysDepth(int g)
        => !ContentPrograms.OwnDepth(_gFeat[g]) && (!_bindless || _gLit[g]);
    /// <summary>The frame's water batches, depth only, under <paramref name="vp"/> into whatever target the
    /// caller attached -- the cover map's roof over a pool's bed. The prepass program on the
    /// staged records; the caster bracket bound the record buffer, this binds the staged slot order it needs.</summary>
    internal static void DrawWaterOccluders(Matrix4x4* vp)
    {
        if (_gWaterEnd <= _gBlendEnd || !Programs.EnsureDepthProgram() || !Issue.BindStagedSlots()) return;
        Gl.UseProgram(Programs._progDepth);
        Gl.UniformMatrix4fv(Programs._depthVp, 1, 0, (float*)vp);
        int was = Submit.At(Submit.Stage.Water);
        uint boundVao = 0;
        for (int g = _gBlendEnd; g < _gWaterEnd; g++)
        {
            int s = _gStart[g];
            Gl.Uniform1i(Programs._depthBase, s);
            uint vao = VaoName(_gVao[g], _gCls[g]);
            if (vao != boundVao) { Gl.BindVertexArray(vao); boundVao = vao; }
            MultiDrawRange(_gMode[g], s, _gLen[g]);
        }
        Submit.Back(was);
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
        int was = Submit.At(retest ? Submit.Stage.Retest : Submit.Stage.Prepass);
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
                    MultiDrawRange(_gMode[g], s, _gLen[g]);
                drawn += _gLen[g];
            }
        }
        // The feature sweep: cutout batches (their holes are the colour pass's own discard, made here with the
        // same texel and threshold), dissolving or not -- the stage runs CxKeyhole too, off the lit vertex stage's
        // own vWorld/vDepth, so a keyholed hedge keeps its depth (and its AO) as the camera closes in -- and every
        // content pair whose depth is its own, through its depth twin, whose vertex text and coverage are its colour
        // program's own. Depth-only LESS writes are order-independent, so this runs as its own sweep.
        GpuEnd(GpuTerm.PrepassKey);
        if (_prepassFeat && Programs.EnsureDepthFeatProgram())
        {
            Submit.At(Submit.Stage.PrepassFeat);
            GpuBegin(GpuTerm.PrepassFeat);
            int bound = -1, boundTex = -1; float boundCut = float.NaN;
            int baseLoc = -1, cutLoc = -1;
            for (int g = 0; g < groups; g++)
            {
                if (!FeatPrepassable(g) || (_gDis[g] && !_prepassKey)) continue;
                // #153: only lit records carry handles, and under bindless the stage reads nothing else
                if (_bindless && !_gLit[g]) continue;
                int want = ContentPrograms.OwnDepth(_gFeat[g]) ? _gFeat[g] : 0;
                if (want != bound)
                {
                    bound = want;
                    boundCut = float.NaN;
                    if (want == 0)
                    {
                        Gl.UseProgram(Programs._progDepthFeat);
                        Gl.UniformMatrix4fv(Programs._depthFeatVp, 1, 0, (float*)vp);
                        Gl.Uniform1f(Programs._depthFeatMip, Renderer.CutMip.V);
                        Gl.Uniform1i(Programs._depthFeatTex, 0);
                        UploadFeatureTerms(Programs._depthFeatFeat, viewport);
                        baseLoc = Programs._depthFeatBase; cutLoc = Programs._depthFeatCut;
                    }
                    else
                    {
                        ref LitLoc L = ref BindDepthTwin(want, vp, viewport);
                        baseLoc = L.Base; cutLoc = L.Cut;
                    }
                    Gl.ActiveTexture(Gl.TEXTURE0);
                    Db.PltPalettes.Bind();   // a PLT cutout's alpha is its palette's, resolved by the stage
                }
                int s = _gStart[g];
                _gPrepassed[g] = true;
                Gl.Uniform1i(baseLoc, s);
                float cut = AlloyClasses.Discard(_gCls[g]);
                if (cut != boundCut) { Gl.Uniform1f(cutLoc, cut); boundCut = cut; }
                if (!_bindless && cut >= 0f && _gTex[g] != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)_gTex[g]); boundTex = _gTex[g]; }
                BindBatchState(g, ref boundCull, ref boundVao);
                if (indirect) CullPass.Draw(_gMode[g], g, s, _gLen[g]);
                else
                    MultiDrawRange(_gMode[g], s, _gLen[g]);
                drawn += _gLen[g]; skipped -= _gLen[g]; if (!retest) Counters._prepassFeatDraws += _gLen[g];
            }
            GpuEnd(GpuTerm.PrepassFeat);
        }
        Submit.Back(was);
        if (indirect) { CullPass.UnbindForDraw(); Issue.BindStagedSlots(); }
        if (idsWanted) IdTarget.End();   // unconditional past Begin: the draw-buffer state is the FBO's
        Gl.ColorMask(1, 1, 1, 1);
        // Counted from what this loop submitted, not from the pass's total draw count: since #126 that total
        // spans the blending classes too, and the prepass walks the opaque range alone.
        // #243: under the cull this is what was STAGED, not what the GPU kept -- the count that decides is in
        // the parameter buffer and does not come back. The period line's cull clause carries the kept figure;
        // this one is the ceiling it culls from, and reading it as "draws" would overstate the pass.
        if (!retest) { Counters._prepassDraws += drawn; Counters._prepassSkipped += skipped; }
        DepthResource.Publish(PassGen);
    }

    /// <summary>Bind content pair <paramref name="feat"/>'s depth twin and the terms it reads at <paramref name="vp"/>; the
    /// caller binds the PLT palettes and sets the per-batch base and cut through the locations returned.</summary>
    internal static ref LitLoc BindDepthTwin(int feat, Matrix4x4* vp, int* viewport)
    {
        ref LitLoc L = ref ContentPrograms.DepthLoc(feat);
        Gl.UseProgram(ContentPrograms.DepthProgram(feat));
        Gl.UniformMatrix4fv(L.Vp, 1, 0, (float*)vp);
        Gl.Uniform1f(L.AlphaMip, Renderer.CutMip.V);
        Gl.Uniform1i(L.Tex, 0);
        UploadFeatureTerms(L.Feat, viewport);
        Db.ContentTable.Bind();
        ContentPrograms.BindTwinExtras(feat, cast: false, layered: false);
        return ref L;
    }

    /// <summary>Does the feature sweep of the prepass take this batch? The cutout class, and every content pair whose
    /// depth is its own -- through the pair's depth twin, which runs its vertex half and its coverage, and only where the
    /// handle table carries its maps to it.</summary>
    private static bool FeatPrepassable(int g)
    {
        int f = _gFeat[g];
        // a blended class must not write depth ahead of the colour pass: it would occlude what it blends over
        if (AlloyClasses.Blends(_gCls[g])) return false;
        // CX-189: nor a coverage cutout -- a 0.2-cut depth laid first would hide the background its fringe blends over
        if (_gA2c[g]) return false;
        if (ContentPrograms.OwnDepth(f)) return _bindless && ContentPrograms.DepthProgram(f) != 0;
        return _gCls[g] == (byte)AlloyClass.Cutout;
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

    /// <summary>The split washes' fully covered interiors, solid and lit like the cutouts beside them: a covered
    /// texel needs no blending, and in the blended pass each one cost a forward-lit fragment. That pass then draws
    /// only each wash's graded edge.</summary>
    private static void WashInteriors(Matrix4x4* vp, int* viewport, float near, float far)
    {
        if (_capturing || _gOpaqueEnd >= _gBlendEnd) return;
        GpuBegin(GpuTerm.Washes);
        Gl.DepthMask(1);
        Gl.DepthFunc(Gl.LEQUAL);
        Issue.WashInteriors(_gOpaqueEnd, _gBlendEnd, vp, viewport, near, far);
        GpuEnd(GpuTerm.Washes);
    }

    /// <summary>The viewport-space rectangle the coverage tail's bounding spheres project into, padded a pixel.
    /// A part with no cull sphere of its own (skinned, dangly, a displacing feature -- most hair) takes its
    /// object's world sphere, whose radius the exporter wrote over the animated geometry. ponytail: a part with
    /// neither, or one reaching behind the eye, takes the whole frame.</summary>
    private static void CoverageRect(Matrix4x4* vp, int* viewport, out int x, out int y, out int w, out int h)
    {
        float lx = 1f, ly = 1f, hx = -1f, hy = -1f;
        bool full = false;
        {
            for (int g = _gA2cStart; g < _gOpaqueEnd && !full; g++)
                for (int k = _gStart[g], e = k + _gLen[g]; k < e && !full; k++)
                {
                    int slot = _dSlot[k];
                    Sphere sp = _dSph[slot];
                    var s = new Vector4(sp.Center, sp.Radius);
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
