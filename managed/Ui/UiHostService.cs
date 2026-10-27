using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Ui;

/// <summary>The framework's frame owner (ADR 0004, amended by ADR 0014): it owns the single <c>SwapBuffers</c>
/// present, computes the in-game gate, syncs the game UI scale and keeps the shared glyph atlas at
/// <c>ui.font × GUI scale</c> once a frame, then drives two registries and nothing else -- the <see cref="UiHost"/>
/// window roll-call in z-order and the <see cref="FrameRegistry"/> phases. It names no service and renders none.</summary>
public sealed unsafe class UiHostService : IExtenderService
{
    private static ExtenderLog? _log;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        UiCategory.SelfCheck(log);   // declares the UI groups in panel order, then checks their rows
        FrameRegistry.Global.Register(FramePhase.PostPresent, 70, PaletteShare.Tick, "Palettes.scan", log);
        Capabilities.Register("palette", 1, static () => true);
        UiHost.SelfCheck(log);   // arbiter + draw roll-call proof (off-device)
        FrameZones.SelfCheck(log);   // frame-split instrument (off-device)
        ScrollColumn.SelfCheck(log);    // scroll column: offset, thumb, wheel, zero-alloc draw (off-device)
        Dropdown.SelfCheck(log);        // dropdown list: 27 entries, scroll, pick, outside press (off-device)
        Tooltip.SelfCheck(log);         // tooltip: dwell, tick, wrap, zero-alloc draw (off-device)
        CascadeMenu.SelfCheck(log);     // cascade menu: nesting, flip/clamp, keys, dwell, icons, zero-alloc draw (off-device)
        MenuGear.SelfCheck(log);        // main-menu gear: anchor, hover animation, zero-alloc emit (off-device)
        UiDrawList.SelfCheck(log);      // the recorded draw list: merging, ranges, rows, growth (off-device)
        TextRig.Init(log);   // the one glyph atlas every window draws from (null under run-demo: stbtt absent)
        ApplyFont();         // ...baked at ui.font x GUI scale; declares the ui group
        ApplyTypeface();     // the persisted ui.typeface, a no-op at default
        _getGuiScale = EngineFn.GetGuiScale;
        _resetStates = EngineFn.RiResetRenderStates;
        _bindBuffer = EngineFn.RiBindBuffer;
        _swapOrig = hooks.InstallSwapBuffers(&OnSwap);
        log.Info(_swapOrig != null
            ? "UiHostService: frame present owned (SwapBuffers) -- driving the window registry"
            : "UiHostService: frame present not installed (Install returned Zero: symbol unresolved or already hooked)");

        // With Alloy off, the below-NUI layer runs just before nuklear's GL flush and the above-NUI layer after it.
        _nkSlot = hooks.ChainNkSdlRender(&OnNkRender, 20);
        log.Info(_nkSlot != null
            ? "UiHostService: window roll-call straddles NUI (chained on nk_sdl_render)"
            : "UiHostService: nk_sdl_render unavailable -- both window layers stay at the present (above NUI)");

        // the gear's window. Its rect is the sprite's; it hits only while the main menu or the in-game Options
        // panel is up, so in play and on the loading screen it is inert for the cursor pick, the click routing and
        // the yield exemption.
        _gearWindow = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            DrawContent = DrawGear,
            OnMouseButton = OnGearButton,
            OnMouseMotion = OnGearMotion,
            HitTestOverride = (px, py) => GearSurface && _gear.Contains(px, py),
        };
        log.Info("UiHostService: settings gear registered (main menu + in-game Options; opens the settings panel)");
    }

    // ---- general.resizeat: the engine's re-layout path, pulled on a schedule ----

    private static int _resizeAt = -1, _resizeStep, _resizeWide;
    private static long _resizeFrom;

    /// <summary>Pull the engine's own resize path twice, at the two times <see cref="General.ResizeAt"/> names:
    /// halve the window's width, then put it back. Hot and allocation-free; with the knob at its 0 default it is
    /// one compare a frame and the window is never touched.</summary>
    private static void ResizeTick(IntPtr window)
    {
        if (_resizeAt < 0) _resizeAt = General.ResizeAt.Boot;
        if (_resizeAt == 0 || window == IntPtr.Zero || _resizeStep > 1) return;
        if (_resizeFrom == 0) { _resizeFrom = System.Diagnostics.Stopwatch.GetTimestamp(); return; }
        long due = (long)(_resizeAt * (_resizeStep + 1)) * System.Diagnostics.Stopwatch.Frequency;
        if (System.Diagnostics.Stopwatch.GetTimestamp() - _resizeFrom < due) return;
        bool half = _resizeStep++ == 0;
        if (EngineFn.SdlSetWindowSize == null || EngineFn.SdlGetWindowSize == null)
        {
            _resizeAt = 0;
            _log?.Always("[resizeat] SDL_SetWindowSize/SDL_GetWindowSize did not resolve -- no resize this run");
            return;
        }
        int w, h;
        EngineFn.SdlGetWindowSize(window, &w, &h);
        if (half) { _resizeWide = w; w /= 2; }
        else if (_resizeWide > 0) w = _resizeWide;
        _log?.Always($"[resizeat] {(half ? "halving" : "restoring")}: window -> {w}x{h} (SDL_SetWindowSize)");
        EngineFn.SdlSetWindowSize(window, w, h);
    }

    // ---- the main-menu gear ----
    private static readonly MenuGear _gear = new();
    private static Window? _gearWindow;
    private static VertexBatch? _gearBatch;
    private static float _gearFacePx;
    private static int _gearEpoch = -1;
    private static GlyphAtlas? _gearFace;
    private static long _gearLast;
    private static (int w, int h, float px) _gearAnchor;

    /// <summary>Where the gear lives: the main menu or the in-game Options panel (its follow-up) -- the two
    /// surfaces the settings panel has a door on.</summary>
    private static bool GearSurface => GameGate.AtMainMenu || GameGate.OptionsOpen;

    /// <summary>A press on the gear opens the panel (cold, input thread). Over the in-game Options panel its release then
    /// resumes the game, since some settings (the quickbar's hotkey editor) only work unpaused -- on the release, so the
    /// engine never sees half a click. Anything else over the gear is swallowed.</summary>
    private static bool OnGearButton(bool down, int button, int x, int y)
    {
        if (!GearSurface || !_gear.Contains(x, y)) return false;
        if (down && button == 1) { _log?.Info($"UiHostService: gear pressed ({(GameGate.AtMainMenu ? "main menu" : "Options panel")}) -- opening the settings panel"); KnobPanelService.Open(); }
        else if (!down && button == 1 && GameGate.OptionsOpen && KnobPanelService.IsOpen) ResumeFromOptions();
        return true;
    }

    /// <summary>The <c>Deactivate</c> argument Resume passes: a plain close, none of <c>COptionsPanel::OnDeactivate</c>'s
    /// special codes.</summary>
    private const int PlainClose = 0;

    /// <summary>Close the in-game Options panel the way its Resume button does: the modal deactivate, whose close
    /// callback nulls <c>CGuiInGame</c>'s slot and clears the app's options-open flag. Re-walks the panel at the release;
    /// a no-op with no module loaded (the main menu's Options is a different panel, never this slot).</summary>
    private static void ResumeFromOptions()
    {
        CClientExoAppInternal intern = LiveInternal();
        CGuiInGame gui = intern.IsNull ? default : intern.InGameGui;
        IntPtr pnl = !gui.IsNull && gui.ChatWindow != IntPtr.Zero ? ShownOptionsPanel(gui) : IntPtr.Zero;
        if (pnl == IntPtr.Zero) return;
        if (EngineFn.GuiModalPanelDeactivate == null)
        {
            _log?.Always("UiHostService: !! CGuiModalPanel::Deactivate unresolved -- the gear cannot close the game menu");
            return;
        }
        _log?.Info("UiHostService: gear released over the Options panel -- resuming the game");
        EngineFn.GuiModalPanelDeactivate(pnl, PlainClose);
    }

    /// <summary>Hover tracking only; motion is never consumed, so the engine's menu keeps its own pointer state.</summary>
    private static bool OnGearMotion(int x, int y)
    {
        _gear.Hovered = GearSurface && _gear.Contains(x, y);
        return false;
    }

    /// <summary>The gear's draw (render thread, on the menu and under the in-game Options panel only) -- the one piece
    /// of content the frame host owns itself. The sprite face is <see cref="MenuGear.FaceFor"/> the live GUI scale; a
    /// miss bakes it right here, the same edge <see cref="ApplyFont"/> re-bakes the text atlas on. The anchor comes
    /// from the live viewport, logged on every change of (viewport, face). Zero-alloc once the face is baked.</summary>
    private static void DrawGear()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _gearLast == 0 ? 0f : (now - _gearLast) / (float)System.Diagnostics.Stopwatch.Frequency;
        _gearLast = now;
        if (!GearSurface) { _gear.Hovered = false; _gear.Bright = 0f; _gear.Spin = 0f; return; }
        if (_log == null || !(NullGl.Active || Gl.Init(_log)) || !TextRig.Ready) return;
        float px = MenuGear.FaceFor(Metrics.GameUiScale);
        if (px != _gearFacePx || _gearEpoch != TextRig.AtlasEpoch)
        {
            _gearFace = TextRig.BakedIconFace(px) ?? TextRig.IconFace(px);
            _gearFacePx = px; _gearEpoch = TextRig.AtlasEpoch;
        }
        if (_gearFace == null || !_gearFace.TryGet(MenuGear.Codepoint, out Glyph g) || g.W <= 0) return;
        int vw = TextRig.ViewportW, vh = TextRig.ViewportH;
        _gear.Layout(vw, vh, Metrics.GameUiScale, g);
        if (_gearWindow != null) { _gearWindow.X = _gear.X; _gearWindow.Y = _gear.Y; _gearWindow.W = _gear.W; _gearWindow.H = _gear.H; }
        if (_gearAnchor != (vw, vh, px))
        {
            _gearAnchor = (vw, vh, px);
            _log.Info($"[menugear] anchor: viewport {vw}x{vh}, GUI scale {Metrics.GameUiScale:F2}, face {px}px -> "
                    + $"gear {_gear.W}x{_gear.H}px at ({_gear.X},{_gear.Y}), inset {MenuGear.InsetPx * Metrics.GameUiScale}px from the lower-left; "
                    + "the PNL_MAINMENU column's pixel origin (MapGuiSceneToScreen) is the owed live check");
        }
        _gear.Tick(dt);
        VertexBatch b = _gearBatch ??= new VertexBatch(_gearFace.SolidU, _gearFace.SolidV);
        b.Reset(_gearFace.SolidU, _gearFace.SolidV);
        _gear.Emit(b, g);
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
    }

    private static SwapBuffersFn _swapOrig;
    private static NkSdlRenderFn* _nkSlot;   // chain slot: the next nk_sdl_render subscriber (null = not chained)
    // Resolved once at init, not per frame: every one of these runs on the present.
    private static GetGuiScaleFn _getGuiScale;   // CAurora::GetGUIScale -> live GUI-scale multiplier
    private static RiResetRenderStatesFn _resetStates;    // RenderInterface::ResetRenderStates -- the hand-back resync
    private static RiBindBufferFn _bindBuffer;   // ...and the one binding it does not cover
    private static bool _announced;
    private const uint GlArrayBuffer = 0x8892;  // not-an-offset: a GL enum

    /// <summary>presents left to sample. Costs a pipeline stall each, so it is a knob that runs out.</summary>
    private static int _presentProbe;

    /// <summary>The frame present: announce once, run the frame phases, then present. The window roll-call
    /// itself happens earlier, at <see cref="OnNkRender"/>, either side of NUI; it falls back to here, both
    /// layers over NUI, only when that chain never installed. Hot (every frame) and zero-alloc.</summary>
    [UnmanagedCallersOnly]
    private static void OnSwap(IntPtr window)
    {
        if (!_announced)
        {
            _announced = true;
            _log?.Info("UiHostService: first frame presented -- hook is LIVE");
            CX.Services.CrashReport.Expose();
            _presentProbe = General.PresentProbe.Boot;
            int pin = General.Pin.Boot;
            if (pin >= 0) _log?.Info($"UiHostService: main thread pin={pin} -> {(Affinity.PinThis(pin) ? "pinned" : "REFUSED")} (was on cpu {Affinity.CurrentCpu})");
        }
        ResizeTick(window);
        CX.Graphics.SdlWindow.Note(window);
        FrameRegistry.Global.Run(FramePhase.Start);
        // When Alloy renders, the 2D UI is drawn BY its frame, after the view list: that frame runs at PrePresent --
        // after this point -- and would otherwise overwrite everything drawn here, which is what made CX's chat
        // vanish the moment the world started drawing.
        if (_nkSlot == null && !CX.Alloy.Renderer.On) DrawWindows();   // both layers, above NUI
        FrameRegistry.Global.Run(FramePhase.PrePresent);
        // Must be the LAST thing before the present -- after the window roll-call and after every PrePresent
        // participant. With Alloy rendering, only when something drew in this context this frame.
        if (!CX.Alloy.Renderer.On || _handBack.Owed) HandBack();
        // the present is its own zone. It is the discriminator the other brackets cannot supply --
        // a frame that spends its time HERE is waiting on the GPU or on vsync, which is a different problem
        // from every zone that measures our own CPU work.
        // the black-frame discriminator, and the only one that is free of the two-context question --
        // this read is in the ENGINE's context, at the last instruction before the present, so what it sees
        // IS what the swap is about to show. A bright value on a frame that displays black means the swap
        // presented a buffer nobody drew; alternating values mean the frame was lost before it got here.
        // Only in a module: the main menu presents for a minute before a save is loaded, and a budget spent
        // there is 60 samples of a static background -- which is what the first run of this probe measured.
        if (_presentProbe > 0 && GameGate.InModule)
        {
            _presentProbe--;
            _log?.Always($"[presentprobe] fb0 centre before swap: 0x{GlProbe.ScreenCentre():x8}"
                       + $"  into={CX.Alloy.PostChain.LastInto} resolves={CX.Alloy.PostChain.Resolves}");
        }
        long tSwap = FrameZones.Now;
        // Alloy issues the present when the screen is Alloy's; otherwise the engine's GLRender::SwapBuffers.
        if (!CX.Alloy.ScreenOwner.Present(window) && _swapOrig != null) _swapOrig(window);
        FrameZones.Add(FrameZones.Zone.Present, tSwap);
        FrameStats();
        FrameRegistry.Global.Run(FramePhase.PostPresent);
    }

    /// <summary>The window roll-call's draw site with Alloy off: the below-NUI layer, resync, the rest of the chain
    /// and nuklear's own flush, then the above-NUI layer. Hot, and on the same thread as the present.</summary>
    [UnmanagedCallersOnly]
    private static void OnNkRender(float aa)
    {
        if (CX.Alloy.Renderer.On) { (*_nkSlot)(aa); return; }   // Alloy's frame draws both layers around its own NUI flush
        DrawWindows(UiLayer.BelowNui);
        (*_nkSlot)(aa);
        DrawWindows(UiLayer.AboveNui);
    }

    /// <summary>Each layer's recorded windows, by <see cref="UiLayer"/>: what Alloy's GUI pass replays on either
    /// side of NUI's layers, or the engine-context draw below replays at once.</summary>
    private static readonly UiDrawList[] _layers = { new(), new() };

    /// <summary>Layer <paramref name="layer"/>'s windows as last recorded.</summary>
    internal static UiDrawList Windows(UiLayer layer) => _layers[(int)layer];

    /// <summary>Record CX's own 2D windows for the frame Alloy draws, both layers, issuing no GL: Alloy's GUI pass
    /// replays them inside its bracket. Game thread.</summary>
    internal static void RecordWindowsForDbFrame()
    {
        long tEdge = FrameZones.Now, ui0 = FrameZones.Ticks(FrameZones.Zone.Ui);
        Record(UiLayer.BelowNui, true);
        Record(UiLayer.AboveNui, false);
        FrameZones.Add(FrameZones.Zone.UiEdge, tEdge, FrameZones.Ticks(FrameZones.Zone.Ui) - ui0);
    }

    /// <summary>Replay both recorded layers into the ENGINE's context and hand its state back: the windows of a frame
    /// whose GUI the 4.6 bracket could not draw.</summary>
    internal static void ReplayWindowsHere()
    {
        TextRig.Replay(_layers[(int)UiLayer.BelowNui]);
        TextRig.Replay(_layers[(int)UiLayer.AboveNui]);
        HandBack();
    }

    /// <summary>The engine-context draw with Alloy off: record one layer (null = both), replay it at once, then hand
    /// the GL state back -- our raw GL desyncs GLRender's shadow cache and what renders next is NUI.</summary>
    private static void DrawWindows(UiLayer? layer = null)
    {
        long tEdge = FrameZones.Now, ui0 = FrameZones.Ticks(FrameZones.Zone.Ui);
        UiLayer first = layer ?? UiLayer.BelowNui;
        Record(first, true);
        TextRig.Replay(_layers[(int)first]);
        if (layer == null) { Record(UiLayer.AboveNui, false); TextRig.Replay(_layers[(int)UiLayer.AboveNui]); }
        HandBack();
        FrameZones.Add(FrameZones.Zone.UiEdge, tEdge, FrameZones.Ticks(FrameZones.Zone.Ui) - ui0);
    }

    /// <summary>Record one layer's z-order roll-call into its list. <paramref name="head"/> first refreshes the session
    /// gate, the UI scale and the viewport, once a frame. Only the roll-call stands aside while a movie or the engine's
    /// loadscreen is on screen; the session gate still runs.</summary>
    private static void Record(UiLayer layer, bool head)
    {
        if (head)
        {
            RefreshGate();
            SyncUiScale(); ApplyFont();   // every frame, not only in game -- the menu gear and panel honour the UI scale
            TextRig.SampleViewport();     // every draw below reads the pair
        }
        long tUi = FrameZones.Now;
        UiDrawList l = _layers[(int)layer];
        l.Reset(TextRig.ViewportW, TextRig.ViewportH);
        // A movie frame never reaches the chain: CGuiMan::UpdateAndRender skips nk_sdl_render outright while one plays.
        if (!CX.Alloy.MovieService.OnScreen && !LoadScreen.PanelUp)
        {
            TextRig.Record(l);
            try { UiHost.DrawFrame(layer); }
            finally { TextRig.Record(null); }
        }
        FrameZones.Add(FrameZones.Zone.Ui, tUi);
    }

    /// <summary>The hand-back resync after raw GL in the ENGINE's context, which went past GLRender's
    /// shadow cache -- that cache early-outs on a stale match and skips real GL calls for the rest of the frame.
    /// One engine-side full resync instead of each drawer mirroring the state it touched. It rebinds the engine's
    /// default framebuffer too (Ghidra: <c>GLRender::ResetRenderStates</c>).</summary>
    internal static void HandBack()
    {
        _handBack.Owed = false;
        if (_resetStates != null) _resetStates();
        // ResetRenderStates rewrites every shadow-cache global EXCEPT the two buffer bindings, and
        // GLRender::BindBuffer early-outs on a cache match -- so a VBO left bound in real GL would take the
        // engine's next streamed vertex upload. Through the engine wrapper so the cache learns about it.
        if (_bindBuffer != null) _bindBuffer(GlArrayBuffer, 0);
    }

    /// <summary>Whether raw GL has run in the ENGINE context since the last <see cref="HandBack"/>.</summary>
    private sealed class HandBackState { public bool Owed; }

    private static readonly HandBackState _handBack = new();

    /// <summary>Raw GL just ran in the ENGINE context: the present's <see cref="HandBack"/> is owed, even with Alloy on.</summary>
    internal static void Owe() => _handBack.Owed = true;

    private static long _fsLast, _fsSum, _fsMax;
    private static int _fsN, _fsOver7, _fsOver17;
    /// <summary>GC counts and bytes at the last period line: a gen0 count near the hitch count is a per-frame allocator.</summary>
    private static int _gc0, _gc1, _gc2;
    private static long _alloc;

    /// <summary>Present-to-present frame stats, logged once per ~900 frames or 10 s: avg/max ms plus counts over the
    /// 144Hz (6.9ms) and 60Hz (16.7ms) budgets. The one number the perf work kept lacking -- cube/depth pass
    /// times exist but nothing tied them to what the player actually experiences. Hot path: one timestamp,
    /// four adds; the log line is the only cold part.</summary>
    private static void FrameStats()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_fsLast != 0 && GameGate.InModule)
        {
            long dt = now - _fsLast;
            FrameZones.EndFrame(dt);   // fold this frame's zone split in, against the same dt
            CX.Services.Perf.FrameProbe.Frame(dt);
            _fsSum += dt; _fsN++;
            if (dt > _fsMax) _fsMax = dt;
            float ms = dt * 1000f / System.Diagnostics.Stopwatch.Frequency;
            if (ms > 6.9f) _fsOver7++;
            if (ms > 16.7f) _fsOver17++;
            // or ten seconds: at 2 fps 900 frames is seven minutes, and a collapse went unlogged
            bool period = _fsN >= 900 || _fsSum >= 10 * System.Diagnostics.Stopwatch.Frequency;
            if (period && !ExtenderLog.Enabled)
            {
                FrameZones.ResetPeriod();
                _fsSum = 0; _fsN = 0; _fsMax = 0; _fsOver7 = 0; _fsOver17 = 0;
            }
            else if (period)
            {
                float avg = _fsSum * 1000f / System.Diagnostics.Stopwatch.Frequency / _fsN;
                float max = _fsMax * 1000f / System.Diagnostics.Stopwatch.Frequency;
                int gc0 = GC.CollectionCount(0), gc1 = GC.CollectionCount(1), gc2 = GC.CollectionCount(2);
                long alloc = GC.GetTotalAllocatedBytes();
                _log?.Info($"UiHostService: [frame] avg {avg:F2}ms ({1000f / avg:F0}fps) max {max:F1}ms, >6.9ms {_fsOver7}/{_fsN}, >16.7ms {_fsOver17}/{_fsN}"
                         + $", gc {gc0 - _gc0}/{gc1 - _gc1}/{gc2 - _gc2} on {(alloc - _alloc) >> 10}KiB allocated" + FrameZones.BoxLine() + (CX.Alloy.AlloyRenderer.VramFreeMib > 0 ? $", vram free {CX.Alloy.AlloyRenderer.VramFreeMib}MiB" : "") + (CX.Alloy.AlloyRenderer.VramEvictedMib > 0 ? $", vram EVICTED {CX.Alloy.AlloyRenderer.VramEvictedMib}MiB" : "") + FrameZones.ThreadsLine());
                _gc0 = gc0; _gc1 = gc1; _gc2 = gc2; _alloc = alloc;
                // NOT inside the null-conditional -- Period() carries the period reset, and `?.` skips
                // argument evaluation, so a null log would desync it from the _fs* counters reset below
                // permanently (every later line would then average since boot).
                string zones = FrameZones.Period() + CX.Graphics.GlContextService.GpuSpanLine();
                _log?.Info(zones);                 // ...and where that frame went
                _fsSum = 0; _fsN = 0; _fsMax = 0; _fsOver7 = 0; _fsOver17 = 0;
            }
        }
        else FrameZones.Discard();   // menu/loading frames are not the period's
        _fsLast = now;
    }

    // In-game gate: draw when a module is loaded and no full-screen options menu / hidden-HUD is up. The walk is
    // verified against the disassembly of GetInGameGui and IsVisible on both builds -- every hop is an EngineLayout row:
    //   mgr  = *g_pAppManager           (CAppManager*)
    //   app  = *mgr                     (CClientExoApp* at CAppManager+0)
    //   gui  = *(*(app+0x8)+0x90)       (CClientExoApp::GetInGameGui -- persists, non-null even on the menu)
    //   win  = *(gui+chatWindow)        (CGuiInGame's chat window -- NULL until a module loads = the in-area signal)
    //   opt  = *(int*)(intern+0x604)    (CClientExoApp::SetOptionsPanelOpen flag -- 1 while the ESC menu is up)
    //   hud  = *(int*)(gui+toggledOff)  (CGuiInGame::ToggleOff sets this; H key hides all HUD)
    //   menu = *(intern+0x98)           (the live CMainMenuPanel* -- non-null from DisplayMainMenu to DestroyMainMenuPanel)
    //   pnl  = *(gui+optionsPanel)      (the live in-game COptionsPanel* -- HandleOptionsButton to HandleOptionsPanelClose)
    //   show = win != null && opt == 0 && hud == 0
    //   options = win != null && pnl != null && *(uint*)(pnl+state) == 1   (active and not hidden by a sub-panel)
    // Chat suppresses CGuiInGameChatWindow::Show, so the vanilla window never activates and its IsVisible
    // (win+0xc68) stays 0 -- module presence + no-ESC-menu is the in-area signal now. A null `win` is the
    // main-menu case (no module) => hidden. The [uigate] log fires only on a state transition (rare).
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);
    private static (IntPtr win, bool opt, bool hud, bool menu, bool options) _gateSig;

    /// <summary>Recompute the gate and publish it onto <see cref="GameGate.InGame"/> / <see cref="GameGate.InModule"/> /
    /// <see cref="GameGate.AtMainMenu"/> / <see cref="GameGate.OptionsOpen"/>.
    /// Hot, zero-alloc: guarded pointer reads at each hop; log only on transition. The module-unload edge and
    /// scale reaction are the content's to observe (chat resets its session on the InModule true->false edge).</summary>
    private static void RefreshGate()
    {
        CClientExoAppInternal intern = LiveInternal();
        CGuiInGame gui = intern.IsNull ? default : intern.InGameGui;
        IntPtr win = gui.IsNull ? IntPtr.Zero : gui.ChatWindow;
        bool opt = !intern.IsNull && intern.OptionsPanelOpen != 0;   // ESC/game menu up
        bool hud = !gui.IsNull && gui.ToggledOff != 0;               // H: HUD toggled off
        bool show = win != IntPtr.Zero && !opt && !hud;
        bool menu = win == IntPtr.Zero && !intern.IsNull && intern.MainMenuPanel != IntPtr.Zero;
        bool options = win != IntPtr.Zero && ShownOptionsPanel(gui) != IntPtr.Zero;

        GameGate.InGameGui = gui.Ptr;
        GameGate.InModule = win != IntPtr.Zero;
        GameGate.InGame = show;
        GameGate.AtMainMenu = menu;
        GameGate.OptionsOpen = options;

        var sig = (win, opt, hud, menu, options);
        if (sig != _gateSig)
        {
            _gateSig = sig;
            bool reached = !intern.IsNull && intern.ReachedMainMenu != 0;
            _log?.Info($"[uigate] win=0x{win:x} opt={opt} hud={hud} menu={menu} options={options} reached={reached} -> inGame={show} inModule={win != IntPtr.Zero}");
        }
    }

    /// <summary>The live <c>CClientExoAppInternal</c>, two guarded hops off <c>g_pAppManager</c>, or null.</summary>
    private static CClientExoAppInternal LiveInternal()
    {
        IntPtr mgr = _appMgr != IntPtr.Zero ? *(IntPtr*)_appMgr : IntPtr.Zero;
        IntPtr app = mgr != IntPtr.Zero ? *(IntPtr*)mgr : IntPtr.Zero;
        return app != IntPtr.Zero ? new CClientExoApp(app).Internal : default;
    }

    /// <summary>The in-game <c>COptionsPanel*</c> when it is up and showing (not hidden by a sub-panel), else Zero.</summary>
    private static IntPtr ShownOptionsPanel(CGuiInGame gui)
    {
        IntPtr pnl = gui.IsNull ? IntPtr.Zero : gui.OptionsPanel;
        return pnl != IntPtr.Zero && new CGuiPanel(pnl).State == GuiPanelShown ? pnl : IntPtr.Zero;
    }

    /// <summary>The <c>CGuiPanel</c> state word of a panel that is active and not hidden: the engine's own test in
    /// <c>Hide</c>, <c>UnHide</c> and <c>Activate</c> (bit 1 active, bits 2/4 hidden).</summary>
    private const uint GuiPanelShown = 1;

    /// <summary>Effective glyph px the atlas is baked at: <c>ui.font × GUI scale</c>, or 0 before the first apply.</summary>
    private static float _appliedPx;

    /// <summary>Bake the shared atlas at <c>ui.font × GUI scale</c> and publish <see cref="Metrics.Scale"/> from it.
    /// Runs on a <c>ui.font</c> set (the setter's thread, so a window rebuilding right after sees the new atlas)
    /// and once per in-game frame for the GUI-scale edge; a no-op when nothing moved, so the frame path is two
    /// float reads. Windows react to the <see cref="Metrics.Scale"/> change on their own draw.</summary>
    internal static void ApplyFont()
    {
        float px = UiKnobs.Font.V * Metrics.GameUiScale;
        px = px < TextRig.MinFontPx ? TextRig.MinFontPx : px > TextRig.AtlasMaxPx ? TextRig.AtlasMaxPx : px;
        if (px == _appliedPx) return;
        _appliedPx = px;
        Metrics.Scale = px / Metrics.DesignFontPx;
        TextRig.SetFont(px);
    }

    /// <summary><c>ui.typeface</c>'s set: rebind <c>default</c> and re-bake (cold).</summary>
    internal static void ApplyTypeface() => TextRig.SetTypeface(UiKnobs.Typeface.V);

    /// <summary>Track the engine's live GUI scale (<c>CAurora::GetGUIScale</c>) into <see cref="Metrics.GameUiScale"/>
    /// so windowed content scales with the game's UI-scaling option. A cheap float read every frame -- the static it
    /// reads is set from the resolution and valid before any module; the per-window re-bake and relayout is the
    /// content's rare reaction (a resolution change, the options slider), keeping this hot path zero-alloc.</summary>
    private static void SyncUiScale()
    {
        if (_getGuiScale == null) return;
        float s = _getGuiScale();
        if (s < 1f) s = 1f;   // engine floors at 1.0; guards an early 0 read before CAurora computes it
        if (System.Math.Abs(s - Metrics.GameUiScale) < 0.001f) return;
        Metrics.GameUiScale = s;
    }
}
