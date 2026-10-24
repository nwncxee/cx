using System;
using System.Threading;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>Alloy's GL 4.6 context on its OWN thread, drawing frame N while the game thread simulates N+1
/// (ADR 0028 section 5). The 4.6 context is made current ONCE here and never released; the game thread keeps
/// the engine's 3.3 context. <c>RenderSnapshot.AwaitDrained</c> is the FENCE and the whole mutual exclusion:
/// from its return to the next publish the renderer is parked, so the engine's context is the game thread's.</summary>
internal static class RenderThread
{
    private static ExtenderLog? _log;
    private static Thread? _thread;
    private static volatile bool _run, _stoodDown;
    private static long _frames, _faults;
    private static string _reason = "";

    /// <summary>Draw one published frame. Set by seam-feed (Alloy's frame entry over the DB); until it is,
    /// there is nothing to run and the thread refuses to start rather than spin.</summary>
    internal static Action<RenderSnapshot.Frame>? DrawFrame;

    /// <summary>Precondition: <c>GuiCapture</c>'s one static bank of buffers is not written by two threads in
    /// the same frame. <b>UNMET</b>, and the one thing the fence cannot fix -- <c>NuiFlush</c> fills the bank
    /// on the GAME thread inside <c>CGuiMan::UpdateAndRender</c> while <c>GuiCapture.Lead</c> and
    /// <c>GuiPass.Flush</c> rotate and RESET it on the render thread. The fix is to ping-pong the NUI bank.</summary>
    internal static bool GuiBuffersOwned;

    /// <summary>Precondition: <c>TextObjects</c>/<c>TextQuads</c> document state is published to the frame
    /// rather than rebuilt under its reader -- <c>TextQuads.Issue</c> walks <c>d.Quads</c> through UNCHECKED
    /// pointer arithmetic against a <c>d.Runs</c>/<c>d.RunCount</c> the builder rewrites. <b>MET</b>: a build
    /// fills the unpublished slot and publishes it whole, so the issuer takes the list in one reference read.</summary>
    internal static bool TextDocsPublished;

    /// <summary>Precondition: the 2D UI's ordering against this thread is settled. <b>MET</b>, set by
    /// <c>SeamDraw.Install</c> -- <c>AwaitDrained</c> runs first in <c>OnBoundary</c>, so every later
    /// <c>PrePresent</c> participant, the hand-back and the present are inside the parked window and the 2D UI
    /// draws after the world frame it composites over (N-1 once pipelined). It stays on the GAME thread.</summary>
    internal static bool UiOrderSettled;

    /// <summary>Precondition: the frame's INPUTS are this thread's, and nothing under the draw reads or writes
    /// ENGINE memory. <b>UNMET</b>. The named reads are closed into <see cref="FrameHoist"/>, read once on the
    /// game thread at the fold and handed across by value; the rest of <c>FrameInputs</c> is read LIVE, and
    /// what remains is engine WRITES and list walks under the draw (the list below).</summary>
    internal static bool FrameInputsPublished;

    /// <summary>Precondition: framebuffer 0 and the engine's GL state caches have ONE owner in the window the
    /// fence does not cover. <b>UNMET</b>, the sibling of <see cref="GuiBuffersOwned"/>: the frame composites
    /// into <see cref="PresentTarget"/>'s image, but the blit that puts it on screen must run in the ENGINE's
    /// context and this thread has none -- so the hand-back is still the game thread's to make.</summary>
    internal static bool ScreenHandoffSettled;

    /// <summary>Every unmet precondition as one refusal line, or null when all are met. Each precondition is a
    /// FACT about what the frame touches rather than a capability, so none can be checked from here: each is
    /// set by whoever makes it true, and setting one that is not true is the way to turn this into corruption.</summary>
    private static string? Unmet()
    {
        string s = "";
        if (!GuiBuffersOwned)
            s += "GuiCapture's one static bank is written by both threads in the same frame -- NuiFlush "
               + "captures nuklear at nk_sdl_render on the GAME thread while DbGui/TextQuads.Issue append the "
               + "panel layers and GuiCapture.Lead + GuiPass.Flush rotate and RESET the bank on the render "
               + "thread. The fix is a NUI bank ping-ponged off the snapshot's frame; ";
        if (!TextDocsPublished)
            s += "TextObjects/TextQuads document state is rebuilt on the game thread while the draw walks it "
               + "through unchecked pointer arithmetic; ";
        if (!UiOrderSettled)
            s += "UiHost's 2D draw runs in the ENGINE context at the present, FrameRegistry's "
               + "Start/PrePresent/PostPresent contract assumes the world frame is already resolved, and "
               + "SeamDraw draws the CX windows only on the game thread's path; ";
        if (!FrameInputsPublished)
            s += "the draw still reads and WRITES engine memory: CameraService.SettleCollision "
               + "read-modify-writes CNWCModule's camera flags from RouteParts, DbKeyhole.Runtime writes five "
               + "ShaderExposedRuntimeData fields, Particles saves and restores the engine's GL bind-state "
               + "cache, GrassField walks g_GrassInfo and calls AurTextureGet per material per frame, "
               + "FrameSnapshot reads CurrentScene plus a CAurTexture, and AlloyRenderer.Target issues two "
               + "glGetFloatv queries in the ENGINE's context -- plus about forty small-value reads of the "
               + "runtime block and the global shader-constant tables. CX-188 hoisted the area walk, the "
               + "attenuation latch, the fog words and the wind write; the list above is what is left; ";
        if (!ScreenHandoffSettled)
            s += "the present hand-back must run in the ENGINE's context and this thread has none, so the "
               + "frame this thread composites would reach framebuffer 0 only through the game thread's half "
               + "of the boundary; ";
        return s.Length == 0 ? null : s.TrimEnd(' ', ';');
    }

    /// <summary>Off-device: the thread refuses to start while any precondition is unset, and the refusal names
    /// EVERY unmet one. A single flag would let whoever met one arm the others' races.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "RenderThread.SelfCheck");
        bool g = GuiBuffersOwned, t = TextDocsPublished, u = UiOrderSettled, f = FrameInputsPublished,
             sc = ScreenHandoffSettled;
        GuiBuffersOwned = TextDocsPublished = UiOrderSettled = FrameInputsPublished = ScreenHandoffSettled = false;
        string? all = Unmet();
        check.Expect(all != null, "with nothing met, the thread refuses");
        check.Expect(all != null && all.Contains("GuiCapture") && all.Contains("TextObjects")
                     && all.Contains("UiHost") && all.Contains("CameraService") && all.Contains("framebuffer 0"),
                     "the refusal names EVERY unmet precondition, not the first");
        // One at a time, each way round: a refusal that drops a precondition is how a race gets armed by
        // whoever met theirs first, and a flag wired to the wrong clause is the same bug wearing a name.
        string[] marker = ["GuiCapture", "TextObjects", "UiHost", "CameraService", "framebuffer 0"];
        for (int k = 0; k < marker.Length; k++)
        {
            GuiBuffersOwned = k != 0; TextDocsPublished = k != 1; UiOrderSettled = k != 2;
            FrameInputsPublished = k != 3; ScreenHandoffSettled = k != 4;
            string? one = Unmet();
            check.Expect(one != null && one.Contains(marker[k]),
                         $"four met still refuses, naming the fifth ({marker[k]})");
            for (int j = 0; j < marker.Length; j++)
                if (j != k)
                    check.Expect(one != null && !one.Contains(marker[j]),
                                 $"...and names ONLY it, not {marker[j]}");
        }
        GuiBuffersOwned = TextDocsPublished = UiOrderSettled = FrameInputsPublished = ScreenHandoffSettled = true;
        check.Expect(Unmet() == null, "all of them met is the only state that does not refuse");
        GuiBuffersOwned = g; TextDocsPublished = t; UiOrderSettled = u; FrameInputsPublished = f;
        ScreenHandoffSettled = sc;
        log.Info(check.Ok ? "RenderThread.SelfCheck: passed"
                          : "RenderThread.SelfCheck: FAILED -- the render thread's preconditions do not gate "
                            + "the start, so alloy.renderthread=1 could arm a race the refusal is meant to name");
        return check.Ok;
    }

    /// <summary>Is the render thread the one drawing? False = the single-threaded snapshot path, which is
    /// either the knob's choice or a completed stand-down. A stand-down IN FLIGHT still reads true: the game
    /// thread must not take the context back until the render thread has let go of it.</summary>
    internal static bool Live => _started && !_stoodDown;

    private static volatile bool _started;

    /// <summary>Called by the frame boundary once per frame: once a stand-down has been raised, block here
    /// until the render thread has dropped the context, then let the single-threaded path resume. Near-free
    /// otherwise, and LATCHED rather than conditioned on the stand-down still being in flight -- a thread that
    /// finished standing down before the boundary would otherwise skip the settle for the session.</summary>
    internal static void SettleIfStandingDown()
    {
        if (!_downRequested || _settled) return;
        _settled = true;
        AwaitDown();
    }

    private static volatile bool _settled;

    /// <summary>Why the thread is not running, for the period line and <c>/alloy</c>.</summary>
    internal static string Reason => _stoodDown ? _reason : _run ? "" : "alloy.renderthread=0";

    /// <summary>Try to start, once, at a DB frame that already has a 4.6 context. Called every frame from the
    /// boundary and near-free after the first: the context is created inside
    /// <c>GlContextService.OnFrame</c>, which runs AFTER this service's Install, so a start at boot would
    /// find it unavailable and stand the thread down for the session (that was the bug).</summary>
    internal static void StartWhenReady(ExtenderLog? log)
    {
        if (_tried || _stoodDown) return;
        if (!CX.Alloy.Renderer.RenderThread.Boot) { _tried = true; return; }
        if (!GlContextService.Available) return;   // not yet; ask again next frame
        _tried = true;
        Start(log ?? _log!);
    }

    private static bool _tried;

    /// <summary>Start the thread, if the knob asked for it and everything it needs is in hand. Game thread,
    /// with the 4.6 context NOT current.</summary>
    private static void Start(ExtenderLog log)
    {
        _log = log;
        if (!CX.Alloy.Renderer.RenderThread.Boot) return;
        if (!Renderer.On)
        {
            // Not a stand-down: with Alloy off the engine draws, and taking the 4.6 context to a second
            // thread would break GlContextService's per-frame bracket, whose MakeCurrent would then be
            // refused on every frame -- a render thread with no scene database has nothing to draw from and
            // everything to break.
            log.Info("RenderThread: alloy.renderthread=1 but alloy.enabled=0 -- the render thread draws the "
                   + "scene database's frame, and with Alloy off there is no such frame. Not started.");
            return;
        }
        if (!GlContextService.Available)
        {
            DownBeforeStart("the GL 4.6 context is not available (alloy.gl46=0, or it failed) -- there is no context to "
               + "hold on a second thread");
            return;
        }
        if (DrawFrame == null)
        {
            DownBeforeStart("no frame entry was registered (seam-feed did not set RenderThread.DrawFrame)");
            return;
        }
        // THE preconditions -- see GuiBuffersOwned. None is checkable from here, so each is asserted as a
        // refusal rather than discovered as corruption, and the refusal names every unmet one at once.
        string? unmet = Unmet();
        if (unmet != null)
        {
            DownBeforeStart(unmet + ". Not a capability problem: this is game-thread state the draw still "
                          + "reads, and it has to stop being that first");
            return;
        }
        if (GlContextService.Inside)
        {
            DownBeforeStart("the 4.6 bracket was OPEN at boot -- the context cannot be taken to another thread while the "
               + "game thread holds it");
            return;
        }
        // Claim the context HERE, on the game thread, before the render thread exists: otherwise the game
        // thread's own bracket can open in the gap and the render thread stands down over a race it lost.
        if (!GlContextService.ReserveForOtherThread())
        {
            DownBeforeStart("the 4.6 context could not be reserved -- the game thread's bracket was open");
            return;
        }
        _run = true;
        _started = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "CX.Alloy.Render" };
        _thread.Start();
        log.Info("RenderThread: starting -- Alloy's 4.6 context moves to its own thread and the per-frame "
               + "Enter/Leave bracket stops running on the game thread (alloy.renderthread=1)");
    }

    /// <summary>Ask the thread to stand down. Loud, once, with the cause named -- but this only RAISES the
    /// request: <see cref="Live"/> stays true and frames keep crossing until the render thread has actually
    /// dropped the context and said so. Clearing <c>Live</c> first lets the game thread <c>MakeCurrent</c> a
    /// context the renderer still holds, which is a GLX <c>BadAccess</c> and Xlib's handler exits the process.</summary>
    private static void RequestDown(string why)
    {
        if (_downRequested) return;
        _downRequested = true;
        _reason = why;
        _run = false;
        _log?.Always($"RenderThread: standing down -- {why}. Alloy will draw on the game thread from the same "
                   + "snapshot; the frame is correct and costs engine + Alloy instead of max(engine, Alloy).");
        // Or the stand-down never completes and SettleIfStandingDown blocks the game thread for ever.
        RenderSnapshot.Stop();   // wake the thread out of Take()
    }

    /// <summary>The render thread has released the context and exited; the game thread may own it again. Only
    /// now does <see cref="Live"/> go false.</summary>
    private static void CompleteDown()
    {
        _stoodDown = true;
        lock (_downGate) { _released = true; Monitor.PulseAll(_downGate); }
    }

    /// <summary>Stand down from the GAME thread (a start that never got off the ground): there is no thread
    /// holding anything, so the two halves collapse.</summary>
    private static void DownBeforeStart(string why)
    {
        RequestDown(why);
        CompleteDown();
        RenderSnapshot.Resume();
    }

    /// <summary>Wait for the render thread to drop the context, then let the single-threaded path resume.
    /// Called on the GAME thread from the boundary when a stand-down is in flight.</summary>
    private static void AwaitDown()
    {
        lock (_downGate)
            while (!_released) Monitor.Wait(_downGate, 1000);
        RenderSnapshot.Resume();
    }

    private static readonly object _downGate = new();
    private static volatile bool _downRequested, _released;

    private static void Loop()
    {
        // FIRST, before any bracket this thread can open: its zones go to their OWN row. Added to the game
        // thread's they would make the period line's residual (frame time minus every top-level zone) read
        // negative on every frame this thread drew, which is the instrument failing exactly where the knob is
        // judged.
        FrameZones.UseRow(FrameZones.RowRender);
        if (!GlContextService.MakeCurrentHere())
        {
            GlContextService.CancelReservation();
            RequestDown("MakeCurrent(4.6 context) on the render thread was refused -- the driver will not hand "
                      + "the context to a second thread on this drawable");
            CompleteDown();
            return;
        }
        _log?.Info("RenderThread: LIVE -- the 4.6 context is current on this thread for the rest of the session");
        try
        {
            while (_run)
            {
                RenderSnapshot.Frame? f = RenderSnapshot.Take();
                if (f == null) break;
                try
                {
                    // The work that needs A context but not the pass chain -- #211's texture retires, the
                    // texture registry's drain, the arena pump, the palette and glyph-atlas builds -- used to
                    // ride the game thread's health bracket. That bracket never opens now, so it rides here,
                    // on the thread that holds the context, once per frame and before the draw.
                    GlContextService.RunInBracketHere();
                    DrawFrame!(f);
                    _frames++;
                }
                catch (Exception e)
                {
                    _faults++;
                    // A fault in the renderer's own bracket is this thread's whole contract failing: it may
                    // have left GL state, and the game thread cannot fix it from another context.
                    RequestDown($"the frame draw threw -- {e.GetType().Name}: {e.Message}");
                }
                finally { RenderSnapshot.Release(); }
            }
        }
        finally
        {
            GlContextService.ReleaseCurrentHere();
            CompleteDown();
        }
    }

    /// <summary>Session end (context loss, or the renderer's teardown): wake the thread out of its wait, let
    /// it drop the context, and WAIT for it -- the game thread may own the context again the instant this
    /// returns, so returning early is the same BadAccess as a racy stand-down.</summary>
    internal static void Shutdown()
    {
        if (!_started) return;
        _run = false;
        RenderSnapshot.Stop();
        _thread?.Join(2000);
        _thread = null;
        _stoodDown = true;
        RenderSnapshot.Resume();
    }

    internal static string Stats(int _)
        => Live ? $"renderthread: LIVE, {_frames} frames drawn off-thread"
               + (GlContextService.HeldHereDepth != 0 ? $", {GlContextService.HeldHereDepth} UNBALANCED 4.6 brackets" : "")
         : _stoodDown ? $"renderthread: STOOD DOWN ({_reason}), {_faults} faults"
         // The preconditions' LIVE state, and this is the only place it can honestly be printed: each is set
         // by whoever makes it true, during that file's Install, which runs after the self-check does.
         : $"renderthread: off ({Reason}) -- met: gui={GuiBuffersOwned} text={TextDocsPublished} "
         + $"uiorder={UiOrderSettled} inputs={FrameInputsPublished} screen={ScreenHandoffSettled}";
}
