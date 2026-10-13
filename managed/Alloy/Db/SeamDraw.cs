using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>The frame boundary: a <c>PrePresent</c> participant one priority ahead of
/// <see cref="GlContextService"/>'s 4.6 bracket (deliberately last), so the order is latch the window state,
/// fold, publish, draw, UI, present. <see cref="DbFeed.Present"/> is NOT used -- the fold must run inside
/// <see cref="RenderSnapshot.Publish"/>, ahead of <c>TakeDirty</c>, or it sees an empty dirty list.</summary>
internal static class SeamDraw
{
    private static ExtenderLog? _log;
    private static bool _saidNoPass, _registered;
    private static long _frames, _views;
    private static Action<DbId, DbId>? _perView;
    private static bool _saidReasserted, _saidOrder;

    /// <summary>The renderer's per-view draw: Alloy's pass chain for one view of one scene, with the frame's
    /// hoisted engine terms. A delegate TYPE rather than <c>Action</c> because the hoist crosses by <c>in</c>
    /// reference -- a boxing copy per view would be a per-frame allocation on a hot path.</summary>
    internal static DbViewPass? Pass;

    /// <summary><see cref="Pass"/>'s signature.</summary>
    internal delegate void DbViewPass(DbId scene, DbId camera, in FrameHoist hoist);

    /// <summary>Register the boundary. Called from the seam's boot, after the knobs are declared.</summary>
    internal static void Install(ExtenderLog log)
    {
        _log = log;
        if (_registered || !Renderer.On) return;
        _registered = true;
        _perView = PerView;
        DbFeed.Draw = _perView;
        RenderThread.DrawFrame = DrawSnapshot;
        // THE fold, and the one place it happens. RenderSnapshot.Publish invokes this before it takes the
        // dirty lists, which is the only order that works: a fold after TakeDirty sees an empty list and
        // writes nothing, so the frame would publish and draw records nobody rebuilt.
        // The trigger highlight's snapshot too: it reads the engine's PartTriggers, which only the game thread may.
        // CX-188: SceneFeed.FoldWind with them -- it READS the engine scene's WindManager and WRITES the
        // engine's own shader block, and a write to engine memory is the one thing a render thread may never do.
        RenderSnapshot.Fold = f =>
        {
            long t = FrameZones.Now;
            DbAnim.Settle(); DbFeed.FoldFrame(); TriggerHighlight.Enroll(); SceneFeed.FoldWind(f);
            FrameZones.Add(FrameZones.Zone.DbFold, t);
        };
        FrameRegistry.Global.Register(FramePhase.PrePresent, 9, OnBoundary, "SeamDraw.frame", log);
        // CX-188: the file that BUILDS the order is the one that may assert it. OnBoundary fences the render
        // thread first (RenderSnapshot.AwaitDrained), so every PrePresent participant after this one, the
        // hand-back and the present run with the renderer parked; and the 2D UI roll-call now runs in BOTH
        // arms of the boundary, after the world frame it composites over in each.
        RenderThread.UiOrderSettled = true;
        log.Info("SeamDraw: DB-mode frame boundary armed -- the CX scene database's frame runs at the present, "
               + "one priority ahead of the 4.6 bracket");
    }

    /// <summary>The frame, on the game thread: fold, publish, and then either draw it here or leave it to the
    /// render thread. ONE order for both modes -- the only difference is which thread runs
    /// <see cref="DrawSnapshot"/> -- so the self-check exercises the live protocol and not a test-only one.
    /// Hot; allocates nothing.</summary>
    private static void OnBoundary()
    {
        if (!Renderer.On) return;
        long t = FrameZones.Now, top0 = FrameZones.TopLevelTicks();
        try { Boundary(); }
        finally { FrameZones.Add(FrameZones.Zone.Seam, t, FrameZones.TopLevelTicks() - top0); }
    }

    /// <summary>One frame boundary. Re-asserts <c>DbFeed.Draw</c> (the feed's self-check clears it), starts the render thread once a
    /// context exists, then in live mode draws the 2D UI over the finished world frame (frame N-1's) before the publish wakes the
    /// renderer. Without live mode the UI draws after every view, split around NUI's flush.</summary>
    private static void Boundary()
    {
        _frames++;
        if (!ReferenceEquals(DbFeed.Draw, _perView))
        {
            DbFeed.Draw = _perView;
            if (!_saidReasserted)
            {
                _saidReasserted = true;
                _log?.Always("SeamDraw: DbFeed.Draw had been cleared by something else (the feed's own "
                           + "self-check nulls it and does not restore it) -- re-asserted. Nothing was lost "
                           + "this frame, but the boot order is deciding whether the frame draws at all.");
            }
        }
        // Not at Install: the 4.6 context does not exist yet, and a render thread started without one stands down for good.
        RenderThread.StartWhenReady(_log);
        // Blocking here is the handshake: the game thread must not take the context back early.
        RenderThread.SettleIfStandingDown();
        // Latched once: a Live that changed mid-boundary would draw the UI twice or not at all.
        bool live = RenderThread.Live;
        if (live)
        {
            // Past this fence the render thread is parked: the context and framebuffer 0 are this thread's.
            RenderSnapshot.AwaitDrained();
            // Before the publish: the publish wakes the renderer, which would resolve the next world frame into framebuffer 0 under the UI.
            CX.Ui.UiHostService.DrawWindowsForDbFrame();
        }
        // Once a frame, as the engine's own latch is: it is per run of GUI views, not per view.
        DbGui.NewFrame();
        DbCamera.NewFrame();   // latch every camera's previous position, once, as the engine does
        long tPub = FrameZones.Now, fold0 = FrameZones.Ticks(FrameZones.Zone.DbFold);
        RenderSnapshot.Frame f = RenderSnapshot.Publish();   // Fold runs inside, ahead of TakeDirty
        FrameZones.Add(FrameZones.Zone.Publish, tPub, FrameZones.Ticks(FrameZones.Zone.DbFold) - fold0);
        DbFeed.CloseViews();
        if (live) return;                                    // the render thread draws it and releases it
        // try/finally: Publish waits on a held frame, so a throw that skipped Release would freeze the game thread on the next present.
        int nui = 0;
        try { nui = DrawViews(f); }
        finally
        {
            try { DrawGuiLayers(nui); }
            finally { RenderSnapshot.Release(); }
        }
    }

    /// <summary>Draw one published frame: every view in the order the game asked for it, each with the camera
    /// matrices the snapshot recorded at the time of the request. Render thread (or the game thread on a
    /// stand-down).</summary>
    private static void DrawSnapshot(RenderSnapshot.Frame f)
    {
        int nui = DrawViews(f);
        if (Renderer.On) FlushGui(nui);
    }

    /// <summary>The views of a frame, no GUI flush. Returns how many layers NUI had already recorded: they sit ahead
    /// of the panels the views just captured, and <see cref="GuiCapture.Lead"/> needs the count.</summary>
    private static int DrawViews(RenderSnapshot.Frame f)
    {
        // Once: whether the world view leads the list or trails it decides nothing here any more (the GUI
        // flush is at the end either way), but it is the fact every "my panel is behind the world" question
        // starts from, and nothing else prints it.
        if (!_saidOrder && f.ViewCount > 1)
        {
            _saidOrder = true;
            Span<char> order = stackalloc char[RenderSnapshot.MaxViews];
            for (int k = 0; k < f.ViewCount; k++) order[k] = f.Views[k].World ? 'W' : 'g';
            _log?.Info($"SeamDraw: view list order (W = world, g = GUI panel): {new string(order[..f.ViewCount])}");
        }
        int nui = GuiCapture.LayerN;
        bool say = Renderer.HitCheck.Boot && _orderSaid < OrderBudget;
        ulong sig = 14695981039346656037;   // not-an-offset: the FNV-1a 64 offset basis
        for (int k = 0; k < f.ViewCount; k++)
        {
            ref RenderSnapshot.ViewRec v = ref f.Views[k];
            DbGui.LastWhy = 'W'; DbGui.LastIssued = 0;
            PerViewWithState(v.Scene, v.Camera, in v, in f.Hoist);
            if (!say || k >= RenderSnapshot.MaxViews) continue;
            _why[k] = DbGui.LastWhy; _issued[k] = DbGui.LastIssued;
            sig = (sig ^ v.Scene.Value) * 1099511628211;
            sig = (sig ^ DbGui.LastWhy) * 1099511628211;
        }
        if (say && sig != _orderSig) { _orderSig = sig; SayOrder(f); }
        SayWorldViews(f);
        return nui;
    }

    /// <summary>Issue the frame's captured GUI layers, panels then NUI, after the view list. GuiPass's own flush sites
    /// run before this boundary, so a panel captured here would be issued a frame late, under the next world pass.</summary>
    private static void FlushGui(int nui)
    {
        long tFlush = FrameZones.Now;
        GuiCapture.Lead(nui); GuiPass.FlushNow();
        FrameZones.Add(FrameZones.Zone.GuiFlush, tFlush);
    }

    /// <summary>The 2D layers on the game thread, in the engine's order with Alloy on as off: the panels, the
    /// below-NUI windows, NUI, then the above-NUI windows. NUI and the panels are one captured bank, so the panel
    /// layers are issued first and kept, the windows draw over them on the framebuffer, and NUI's layers close the
    /// flush. Hot; allocates nothing.</summary>
    private static void DrawGuiLayers(int nui)
    {
        long tFlush = FrameZones.Now;
        int panels = GuiCapture.LayerN - nui;
        GuiCapture.Lead(nui);
        GuiPass.FlushLayers(panels);
        PresentTarget.HandBackIfOwed();
        FrameZones.Add(FrameZones.Zone.GuiFlush, tFlush);
        CX.Ui.UiHostService.DrawWindowsForDbFrame(CX.Ui.UiLayer.BelowNui);
        tFlush = FrameZones.Now;
        GuiPass.FlushNow();
        FrameZones.Add(FrameZones.Zone.GuiFlush, tFlush);
        CX.Ui.UiHostService.DrawWindowsForDbFrame(CX.Ui.UiLayer.AboveNui);
    }

    /// <summary>One view, as <see cref="DbFeed.Draw"/>. Reached on the single-threaded path, where the
    /// snapshot's record for this view is the one just published.</summary>
    private static void PerView(DbId scene, DbId camera)
    {
        RenderSnapshot.Frame? f = RenderSnapshot.Drain();
        if (f != null)
            for (int k = 0; k < f.ViewCount; k++)
                if (f.Views[k].Scene == scene && f.Views[k].Camera == camera)
                {
                    PerViewWithState(scene, camera, in f.Views[k], in f.Hoist);
                    return;
                }
        // The feed asked for a view the snapshot does not carry, which can only mean the camera's own view
        // did not build (a degenerate rect -- the engine skips such a view too) or the two lists drifted.
        // Its own cause, not Pass's: a refusal that names the wrong reason is worse than none.
        _unmatched++;
        if (!_saidUnmatched)
        {
            _saidUnmatched = true;
            _log?.Always("SeamDraw: the feed asked to draw a view the frame's snapshot does not carry -- "
                       + "that view is NOT DRAWN. Either its camera's rect was degenerate (which the engine "
                       + "skips too) or the feed's view list and the snapshot's have drifted apart.");
        }
    }

    private static long _unmatched;
    private static bool _saidUnmatched;

    /// <summary>Fill the frame inputs this view's passes read, then run the pass chain. The inputs come from
    /// the SNAPSHOT's matrices, not from a fresh read of the camera record: the game thread may already have
    /// moved the camera for frame N+1, and a pass that re-derived its own view would then draw frame N's
    /// geometry through frame N+1's camera.</summary>
    private static void PerViewWithState(DbId scene, DbId camera, in RenderSnapshot.ViewRec v,
                                         in FrameHoist h)
    {
        _views++;
        SceneFeed.TickFromView(scene, camera, in v, in h);
        // THE view for this pass: the snapshot's, not AlloyCamera.WorldView (which is measured at an engine
        // hook that does not run in DB mode, and is one camera for the whole frame regardless). Set around
        // the pass and cleared in a finally, so a throw cannot leave a GUI panel's ortho camera latched for
        // the next frame's world.
        AlloyRenderer.DbView = new AlloyView
        {
            View = v.View, Proj = v.Proj, X = v.X, Y = v.Y, W = v.W, H = v.H,
            Aspect = v.Aspect, Near = v.Near, Far = v.Far, Fovy = v.Fovy,
            DepthNear = v.DepthNear, DepthFar = v.DepthFar,
            Perspective = v.Perspective, Valid = true,
        };
        AlloyRenderer.DbViewValid = true;
        try { DrawView(scene, camera, in v, in h); }
        finally { AlloyRenderer.DbViewValid = false; }
    }

    /// <summary>One view's draw, with <see cref="AlloyRenderer.DbView"/> already latched.</summary>
    private static void DrawView(DbId scene, DbId camera, in RenderSnapshot.ViewRec v, in FrameHoist h)
    {
        if (!v.World)
        {
            // A GUI panel view draws itself out of the database (seam-feed's DbGui): its controls are model
            // objects in its own scene and its text is CX-119's glyph runs, so there is no pass chain to run
            // and no world verdict to wait for. The view's own view*proj is what the quads sit on.
            _guiViews++;
            // CX-120 (gui): panel geometry AND text, both on the GuiPass/GuiCapture path, which already
            // owns the per-rect viewport and the GUI depth convention. Not the owned pass chain: that is not
            // re-entrant per view -- every BeginOwnedFrame clears the HDR target and ends by compositing the
            // WHOLE display -- so running it once per panel made the last panel's empty composite overwrite
            // the world (the black frames in .scratch/live/gui2 and gui3).
            long tGui = FrameZones.Now;
            _guiText += DbGui.Draw(scene, in v, (uint)Environment.TickCount);
            FrameZones.Add(FrameZones.Zone.DbGuiDraw, tGui);
            return;
        }
        if (Pass == null) { Refuse(); return; }
        Pass(scene, camera, in h);
    }

    private static long _guiViews, _guiText;

    /// <summary>The world-view tripwire's memory: the set last said, and the lines left to say.</summary>
    private sealed class WorldViewState { public ulong Sig; public int Left = 40; }
    private static WorldViewState _worldViews = new();

    /// <summary>A frame that draws the world through more than one view, or through a camera with a viewport of its
    /// own, said whenever that set changes: each such view clears its rect and paints the area from its camera.
    /// Silent, and allocation-free, on the ordinary frame of one world view through the world camera.</summary>
    private static void SayWorldViews(RenderSnapshot.Frame f)
    {
        WorldViewState st = _worldViews;
        int n = 0;
        bool odd = false;
        ulong sig = 14695981039346656037;   // not-an-offset: the FNV-1a 64 offset basis
        for (int k = 0; k < f.ViewCount; k++)
        {
            ref RenderSnapshot.ViewRec v = ref f.Views[k];
            if (!v.World) continue;
            n++;
            int c = v.Camera.Index;
            if ((uint)c < SceneDb.MaxCameras && SceneDb.CamViewW[c] > 0f) odd = true;
            sig = (sig ^ v.Camera.Value) * 1099511628211;
            sig = (sig ^ v.Scene.Value) * 1099511628211;
        }
        if (n > 1) odd = true;
        if (!odd) sig = 0;
        if (sig == st.Sig || st.Left <= 0) return;
        st.Sig = sig;
        st.Left--;
        if (!odd) { _log?.Info($"SeamDraw: [worldviews] frame {f.Index}: back to one world view through the world camera"); return; }
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < f.ViewCount; k++)
        {
            ref RenderSnapshot.ViewRec v = ref f.Views[k];
            if (!v.World) continue;
            int c = v.Camera.Index, s = v.Scene.Index;
            bool cam = (uint)c < SceneDb.MaxCameras;
            DbId camScene = cam ? SceneDb.CamScene[c] : default;
            sb.Append(" | view ").Append(k).Append(" scene=").Append(s).Append(" \"")
              .Append((uint)s < SceneDb.MaxScenes ? SceneDb.SceneName[s] ?? "?" : "?").Append("\" cam=").Append(c)
              .Append(v.Camera == SceneInputs.DbWorldCamera ? " (THE world camera)" : "")
              .Append(" camScene=").Append(camScene.Index)
              .Append(cam ? $" camRect={SceneDb.CamViewX[c]:F0},{SceneDb.CamViewY[c]:F0} {SceneDb.CamViewW[c]:F0}x{SceneDb.CamViewH[c]:F0}" : "")
              .Append($" drawRect={v.X},{v.Y} {v.W}x{v.H} camPos=({v.CamPos.X:F1},{v.CamPos.Y:F1},{v.CamPos.Z:F1}) persp={v.Perspective}");
        }
        _log?.Info($"SeamDraw: [worldviews] frame {f.Index}: {n} world view(s) of {f.ViewCount}{sb}");
    }

    private const int OrderBudget = 80;
    private static int _orderSaid;
    private static ulong _orderSig;
    private static readonly char[] _why = new char[RenderSnapshot.MaxViews];
    private static readonly int[] _issued = new int[RenderSnapshot.MaxViews];

    /// <summary><c>alloy.hitcheck=1</c>: the frame's views in paint order, each with its outcome
    /// (<see cref="DbGui.LastWhy"/>) and draw count, said whenever that list changes.</summary>
    private static void SayOrder(RenderSnapshot.Frame f)
    {
        _orderSaid++;
        var sb = new System.Text.StringBuilder();
        for (int k = 0; k < f.ViewCount && k < RenderSnapshot.MaxViews; k++)
        {
            ref RenderSnapshot.ViewRec v = ref f.Views[k];
            int s = v.Scene.Index;
            string name = (uint)s < SceneDb.MaxScenes ? SceneDb.SceneName[s] ?? "?" : "?";
            if (name.StartsWith("sceneGUI_", StringComparison.Ordinal)) name = name[9..];
            sb.Append(k == 0 ? "" : " > ").Append(name).Append('#').Append(s).Append('[').Append(_why[k])
              .Append(_issued[k]).Append(' ').Append(v.X).Append(',').Append(v.Y).Append(' ').Append(v.W)
              .Append('x').Append(v.H).Append(']');
        }
        _log?.Info($"[dbviews] frame {f.Index}: {sb}");
    }

    private static void Refuse()
    {
        if (_saidNoPass) return;
        _saidNoPass = true;
        _log?.Always("SeamDraw: no renderer pass is registered for a DB-mode view (SeamDraw.Pass is unset) -- "
                   + "the scene database is being fed and published, and NOTHING IS DRAWN this session. "
                   + "This is the draw side of CX-120, not a fault in the database.");
    }

    internal static string Stats(int _)
        // "gui draws" was a lie that cost an investigation: DbGui.Draw RETURNS its text-run count, not its
        // draw count, so this line read "0 gui draws" while DbGui had walked 366498 controls and refused no
        // layer. Two numbers now, named for what they actually are -- a counter you have to read the callee's
        // source to interpret is worse than no counter.
        => $"seamdraw: {_frames} frames, {_views} views, {_guiViews} gui views ({_guiText} text runs)"
         + (_unmatched > 0 ? $", {_unmatched} views UNMATCHED" : "")
         + (Pass == null ? ", NO WORLD PASS REGISTERED" : "");
}
