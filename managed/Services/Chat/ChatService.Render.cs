using System;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Chat;

/// <summary>The frame side of the chatbox: the framework Window registration, the per-frame content draw and its edge
/// gates, the per-window frame build (Rebuild / BuildOneWindow) and the session reset at the module edge.</summary>
public sealed unsafe partial class ChatService
{
    /// <summary>Always-closed stub passed to non-focused window builds so EmitSettings doesn't duplicate.</summary>
    private static readonly SettingsWindow _closedSettings = new();

    /// <summary>Builds the windows from the persisted state, observes ingest, and registers the framework Window
    /// whose content-draw is the whole chatbox. The atlas is the frame host's (<c>UiHostService</c> bakes it at
    /// <c>ui.font</c>), so the first content draw is the first real build.</summary>
    private static void InitRender(ExtenderLog log, HookService hooks)
    {
        // Tabs are pure receive-filters; the send channel is the dropdown (window-level).
        // Default is a single All tab; the player adds per-channel tabs via the settings window.
        _layout = new ChatLayout(new ChatWindow(60, 40, 520, 300,
        [
            new Tab("All",  0xffffffff),
        ]));
        LoadConfig();   // the bag bound pre-main: rects, toggles, tabs are there at Initialize
        _store.AddObserver(OnIngest);  // new line -> slide-in/hold + rebuild (cold network thread)
        // The chatbox is the extender's one text surface, so it is where any service's on-screen notice
        // lands -- an Alloy dropout above all. Set here rather than published as a chat API: Notice is the
        // seam, and nothing outside this file needs to know a chatbox drew the line.
        Notice.Sink = EchoLocal;

        // Register the framework Window whose content IS the whole chatbox. UiHostService drives
        // DrawContent once per frame in z-order; the framework input layer (UiInputService) routes captured
        // input through the window's delegates. The chatbox owns many sub-windows internally (ChatLayout), so it
        // stays one framework Window with a hit-test override (its UI extends outside any single rect -- popped-out
        // strips, dropdowns) and self-manages focus; WantsKeyboard is driven via KeyboardMode -> RequestKeyboard.
        _uiWindow = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            DrawContent = OnFrame,
            OnMouseButton = OnMouseButton,
            OnMouseMotion = OnMouseMotion,
            OnMouseWheel = OnMouseWheel,
            OnKey = OnChatKey,
            OnText = InsertUtf8,
            HitTestOverride = (px, py) => MouseOverUi((int)px, (int)py),
        };
        UiHost.AddChordConsumer(new SlashChord());   // "/" opens a command line
        log.Info("ChatService: registered as a framework Window (content-draw + routed input), \"/\" opens a command line");
    }

    private static Window? _uiWindow;
    private static bool _booted;                  // first-frame viewport relayout done
    private static int _swapTid;                  // managed thread id of the frame-present thread (probe)
    private static bool _wasInModule;             // last frame's GameGate.InModule, for the module-unload edge
    private static float _appliedScale = float.NaN;   // Metrics.Scale we last relaid at (font / GUI-scale edge); NaN = never
    private static int _appliedEpoch = -1;        // TextRig.AtlasEpoch we last relaid at (re-bake edge: typeface, a new face)
    private static float _panelPx, _panelSmallPx; // the settings panel's face heights at the last open

    /// <summary>The chord-consumer face of chat: an unmodified "/" that has fallen past every window opens the
    /// command line. Priority 1, so a hotkey bound to the same key (Priority 2) wins it.</summary>
    private sealed class SlashChord : IChordConsumer
    {
        /// <summary>SDL keycode for <c>/</c> (SDLK_SLASH), and the raw SDL modifier masks
        /// (KMOD_SHIFT / KMOD_CTRL) that mean the player meant something else.</summary>
        private const int KeySlash = 47, KmodShift = 0x0003, KmodCtrl = 0x00C0;  // not-an-offset: SDL key/event code

        public int Priority => 1;

        public bool WantsKey(int key) => key == KeySlash;

        public void Fire(uint key, int sdlMods)
        {
            // Any modifier means the player meant something else (shift+/ = "?", ctrl+/ = a bind).
            if (key != KeySlash || (sdlMods & (KmodShift | KmodCtrl)) != 0) return;
            OpenWith('/');
        }

        public void FireMouse(int button, int sdlMods) { }
    }

    /// <summary>Pull every chat window back inside the live viewport. Rects are restored from <c>cx.cfg</c> in
    /// absolute pixels, so a client that comes up smaller than the one that wrote them (windowed after
    /// fullscreen, another monitor, a Proton prefix) lands the box past the edge, undraggable. Viewport is
    /// whatever the last draw measured, so this is a no-op on the very first frame.</summary>
    private static void KeepOnScreen()
    {
        float vpW = TextRig.ViewportW, vpH = TextRig.ViewportH;
        if (_layout == null || vpW <= 0f || vpH <= 0f) return;
        ChatWindow[] wins = _layout.Windows;
        for (int i = 0; i < wins.Length; i++)
        {
            ChatWindow w = wins[i];
            float x = Window.ClampAxis(w.X, w.W, vpW), y = Window.ClampAxis(w.Y, w.H, vpH);
            if (x == w.X && y == w.Y) continue;
            _log?.Info($"[chat] window {i} was off-screen at ({w.X},{w.Y}) in {vpW}x{vpH} -- pulled to ({x},{y})");
            w.X = x; w.Y = y;
            DirtyAll();
        }
    }

    /// <summary>The chat window's per-frame content draw (UiHostService, via <see cref="UiHost.DrawFrame"/>). Lays
    /// out for the live viewport once, reacts to the host-published gate + scale edges, then draws the box in-game
    /// (or the attribution overlay on the main menu). Hot and zero-alloc in the steady state: bootstrap, session
    /// reset and scale relayout are edge-gated. Draws through ChatRenderer, which owns the cold->hot hand-off.</summary>
    private static void OnFrame()
    {
        if (!_booted)
        {
            _booted = true;
            _swapTid = Environment.CurrentManagedThreadId;
            _log?.Info($"ChatService: first content draw [chatthread] swap tid={_swapTid}");
            _log?.Info($"ChatService: cfg dir = {Native.UserDirectory() ?? "(engine dir unresolved -> ~/.config fallback)"}");
            Rebuild();   // re-layout for the live viewport width
        }
        // Module-unload edge (was inside the old InGame() gate): drop cached engine ptrs + stale state.
        // It now fires once per frame on the render thread instead of possibly on the input thread; ResetSession
        // is documented idempotent and zero-alloc, so the move is safe.
        bool inModule = GameGate.InModule;
        if (_wasInModule && !inModule) ResetSession();
        _wasInModule = inModule;

        if (IsTyping && _layout != null && Hidden(_layout.Focused))
        {
            _log?.Info("[chat] a NUI window covers the typing line -- closing it");
            CloseChat(false);
        }

        if (GameGate.InGame)
        {
            // Look edge: ui.look / ui.palette moved, or the native art's verdict changed -> re-lay every window's
            // chrome. Probed first, so the build and this frame's draw agree on which look they are drawing.
            ChatSkin.Probe(_log);
            if (ChatSkin.LookChanged()) { DirtyAll(); Rebuild(); }
            // Scale edge: the frame host re-baked the atlas (ui.font or the game's UI scale moved) -> relay the chrome.
            if (Metrics.Scale != _appliedScale)
            {
                _appliedScale = Metrics.Scale;
                if (_layout != null)
                {
                    ChatWindow[] wins = _layout.Windows;
                    for (int i = 0; i < wins.Length; i++) { wins[i].ClampToMin(); EnsureFace(wins[i]); }   // window faces follow the GUI scale; the first in-game frame bakes the loaded ones
                }
                DirtyAll();
                Rebuild();   // re-lay the chrome at the new scale
            }
            // Re-bake edge: the sheet was re-baked (ui.typeface, a per-window face) -> every cached face is stale.
            if (TextRig.AtlasEpoch != _appliedEpoch)
            {
                _appliedEpoch = TextRig.AtlasEpoch;
                if (_settings.Open)
                {
                    GlyphAtlas? body = TextRig.BakedFace(_panelPx) ?? TextRig.Atlas;
                    _settings.SetFaces(body, TextRig.BakedFace(_panelSmallPx) ?? body);
                }
                DirtyAll();
                Rebuild();
            }
            PollGameClock();
            KeepOnScreen();
            ChatRenderer.Draw();
            DrawSettings();
            // Dialog and the quickbar draw themselves: each registers its own framework Window and UiHostService's
            // z-order roll-call invokes their content-draw after ours. No cross-call from here.
        }
        else if (!inModule) ChatRenderer.DrawOverlay();   // main menu (no module): the attribution overlay
    }

    /// <summary>New line ingested: auto-open Tell routing, then compensate every window whose active
    /// tab matches (scroll hold / slide-in), then publish ONCE. Cold (network thread).</summary>
    private static void OnIngest(ChatLine line)
    {
        GlyphAtlas? atlas = TextRig.Atlas;
        if (_layout == null || atlas == null) { Rebuild(); return; }

        // route incoming Tells before the scroll-compensation loop so a new tab is visible on rebuild.
        // ponytail: races the input thread's AddTab/AddWindow; atomic array swap keeps it memory-safe.
        if ((line.Channel & 0x002) != 0)
            RouteIncomingTell(line);

        ChatWindow[] wins = _layout.Windows;   // snapshot: concurrent add/close swaps the array
        int fi = _layout.FocusedIndex;
        for (int i = 0; i < wins.Length; i++)
        {
            Tab active = wins[i].Active;
            if (!active.Matches(line)) continue;
            wins[i].Dirty = true;
            float grow = WindowRenderer.MeasureBlock(wins[i], line, WindowAtlas(wins[i], atlas)) + WindowRenderer.MsgGap;
            if (active.ScrollPx == 0f && i == fi && wins[i].AnimEnabled) ChatAnim.AddResidual(grow);
            else if (active.ScrollPx != 0f) active.ScrollPx += grow;

            // Mark non-active ephemeral tabs that match this line as unread.
            var tabs = wins[i].Tabs;
            int at = wins[i].ActiveTab;
            for (int ti = 0; ti < tabs.Count; ti++)
                if (ti != at && tabs[ti].Ephemeral && tabs[ti].Matches(line))
                    tabs[ti].Unread = true;
        }
        Rebuild();
    }

    /// <summary>Trigger a rebuild from outside ChatService (e.g. QuickbarService after a chrome change).
    /// Safe to call from the swap thread (same threading model as <see cref="OnFrame"/>).</summary>
    internal static void RequestRebuild() => Rebuild();

    /// <summary>Per-window last-built frame cache. Keyed on the window reference so an add/close can't
    /// serve a stale frame for a different window.</summary>
    // ponytail: torn two-ref tuple read is a one-frame cosmetic (documented tolerance); volatile struct is the ceiling
    private static (ChatWindow? win, RenderFrame? frame)[] _lastBuilt =
        new (ChatWindow?, RenderFrame?)[ChatLayout.MaxWindows];

    private static VertexBatch? _panelBatch;

    /// <summary>Per frame (render thread, hot): the settings panel over every window, from its live state --
    /// a drag or a hover shows next frame with no rebuild, as the main panel draws. Allocates nothing
    /// while open and idle: the batch is reused and <see cref="KnobPanel.Emit"/> is zero-alloc.</summary>
    private static void DrawSettings()
    {
        GlyphAtlas? atlas = TextRig.Atlas;
        if (!_settings.Open || atlas == null) return;
        VertexBatch b = _panelBatch ??= new VertexBatch(atlas.SolidU, atlas.SolidV);
        b.Reset(atlas.SolidU, atlas.SolidV);
        _settings.Panel.Emit(b);
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
    }

    /// <summary>Mark every window dirty (config change, UI-scale change, session reset).</summary>
    private static void DirtyAll()
    {
        if (_layout == null) return;
        ChatWindow[] wins = _layout.Windows;
        for (int i = 0; i < wins.Length; i++) wins[i].Dirty = true;
    }

    /// <summary>Rebuild vertex batches for all windows and publish them. Only the focused window gets the
    /// input strip, settings panel, and slide residual. Non-focused windows whose
    /// <see cref="ChatWindow.Dirty"/> flag is false reuse their last-built frame, skipping the build +
    /// LOH scratch alloc. The focused window is always rebuilt (it owns drag chrome, grip, strip).</summary>
    private static void Rebuild()
    {
        GlyphAtlas? atlas = TextRig.Atlas;
        if (_layout == null || atlas == null) return;
        ChatWindow[] wins = _layout.Windows;   // snapshot
        int n = wins.Length;
        // Ghost tab: find the source window (if any) to append a floating ghost frame.
        int dragSrc = -1;
        for (int i = 0; i < n; i++) if (wins[i].TabDragActive) { dragSrc = i; break; }
        var frames = new RenderFrame[n + (dragSrc >= 0 ? 1 : 0)];
        int fi = _layout.FocusedIndex;
        if ((uint)fi >= (uint)n) fi = 0;
        // Build non-focused windows first, focused last, so the focused window draws on top.
        int slot = 0;
        for (int i = 0; i < n; i++)
        {
            if (i == fi) continue;
            // Clean + cached: reuse the last-built frame.
            if (!wins[i].Dirty && i < _lastBuilt.Length
                && _lastBuilt[i].win == wins[i] && _lastBuilt[i].frame != null)
            {
                frames[slot++] = _lastBuilt[i].frame!;
            }
            else
            {
                wins[i].Dirty = false;
                var rf = BuildOneWindow(wins[i], false, atlas, i);
                if (i < _lastBuilt.Length) _lastBuilt[i] = (wins[i], rf);
                frames[slot++] = rf;
            }
        }
        {
            wins[fi].Dirty = false;
            var rf = BuildOneWindow(wins[fi], true, atlas, fi);
            if (fi < _lastBuilt.Length) _lastBuilt[fi] = (wins[fi], rf);
            frames[slot++] = rf;
        }
        if (dragSrc >= 0)
        {
            ChatWindow src = wins[dragSrc];
            float gw = src.TabW;
            frames[slot] = WindowRenderer.BuildGhostTab(atlas,
                _lastMx - gw * 0.5f, _lastMy - ChatWindow.TabBarH * 0.5f, gw, src.Active.Label);
        }
        // Quickbar chrome: band/borders/cog drawn first (under chat), settings panel drawn last (on top).
        RenderFrame[] qbExtra = Quickbar.QuickbarService.ExtraFrames;
        int chatCount = slot + (dragSrc >= 0 ? 1 : 0);
        if (qbExtra.Length > 0)
        {
            var all = new RenderFrame[1 + chatCount + (qbExtra.Length > 1 ? 1 : 0)];
            int ai = 0;
            all[ai++] = qbExtra[0];
            for (int i = 0; i < chatCount; i++) all[ai++] = frames[i];
            if (qbExtra.Length > 1) all[ai++] = qbExtra[1];
            ChatRenderer.Publish(all);
        }
        else
        {
            ChatRenderer.Publish(frames);
        }
    }

    /// <summary>The window's own face: its typeface / text size at the GUI scale, else the UI atlas.
    /// Hot-safe (a baked-face lookup, never a bake); <see cref="EnsureFace"/> baked it cold when it was picked or
    /// loaded, so a miss here is the bake having refused -- the UI face stands in.</summary>
    private static GlyphAtlas WindowAtlas(ChatWindow win, GlyphAtlas atlas)
    {
        if (win.FontPx <= 0 && win.Typeface.Length == 0) return atlas;
        return TextRig.BakedFace(win.Typeface.Length > 0 ? win.Typeface : FontRegistry.DefaultName, WindowFacePx(win)) ?? atlas;
    }

    private static float WindowFacePx(ChatWindow win) => TextRig.FacePx(win.FontPx);

    /// <summary>Bake a window's own face if it has one. COLD (a miss re-bakes the sheet): a gear pick, or the
    /// scale edge where the frame host re-bakes the UI atlas anyway. A refused bake logs and the window draws with
    /// the UI face. ponytail: every (typeface, px) picked stays in the sheet for the session and later picks fall
    /// back to the UI face once it fills; upgrade = release a window's previous face when nothing shares it.</summary>
    private static void EnsureFace(ChatWindow win)
    {
        if (!TextRig.Ready || (win.FontPx <= 0 && win.Typeface.Length == 0)) return;
        string face = win.Typeface.Length > 0 ? win.Typeface : FontRegistry.DefaultName;
        if (TextRig.Face(face, WindowFacePx(win)) == null)
            _log?.Info($"[chat] window face {face} @ {WindowFacePx(win)}px refused; drawing with the UI face");
    }

    private static RenderFrame BuildOneWindow(ChatWindow win, bool focused, GlyphAtlas atlas, int winIndex)
    {
        bool footer = focused && IsTyping && win.HasSendable;
        win.FooterVisible = footer;
        bool grip = (winIndex == _hoverIndex) || win.Dragging || _anyTabDrag;
        var frame = WindowRenderer.Build(win, focused ? _settings : _closedSettings, _store, WindowAtlas(win, atlas),
            footer, footer ? Edit.Text : "", footer ? Edit.Cursor : 0, grip,
            footer ? Edit.Selection : null, chrome: atlas);
        frame.ApplyResidual = focused && win.AnimEnabled;
        frame.AnimEnabled = win.AnimEnabled;
        frame.HoldSec = win.HoldSec;
        frame.FadeSec = win.FadeSec;
        frame.TauMs = win.TauMs;
        return frame;
    }

    /// <summary>Reset all per-session state at a module boundary (the gate's window-drop-to-zero edge, which fires
    /// on clean exit and disconnect alike). Order matters: the engine frees its portrait textures on module unload,
    /// so a cached <c>CAurTexture*</c> must be dropped first or it dangles into the next session and crashes when
    /// bound. Idempotent and zero-alloc -- safe to fire from the hot gate read on the transition frame.</summary>
    private static void ResetSession()
    {
        ChatRenderer.ClearSessionState();   // crash-critical: freed engine textures must not carry over
        ChatSkin.Rearm();                   // the next module's haks decide the native art again
        Dialog.DialogService.Close();       // drop any dialog left open by a mid-conversation disconnect
        _gmHour = -1;                        // drop the game clock: the next module re-anchors it
        // ponytail: racing a late-arriving ingest (disconnect teardown) can leave <=1 stale line; indices
        // stay in-bounds so it's memory-safe, just cosmetic. Add a lock only if the [chatthread] log proves
        // ingest really runs on a separate thread.
        _store.Clear();                     // cosmetic: no prior session's lines leak in
        if (IsTyping) ParkDraft();          // a disconnect mid-post keeps the post (chat.drafts)
        _typingTab = null;
        _completer.Reset();
        Edit.Clear();
        IsTyping = false;
        TypingSignal.Reset();               // session edge: drop the latch, never send (the connection is gone)
        KeyboardMode(false);                // drop any keyboard capture left from the old session
        _settings.Reset();                  // close + drop any half-owned press so no stray up gets eaten next session
        if (_layout != null)
        {
            ChatWindow[] wins = _layout.Windows;
            for (int wi = 0; wi < wins.Length; wi++)
            {
                ChatWindow w = wins[wi];
                var wt = w.Tabs; for (int ti = 0; ti < wt.Count; ti++) { wt[ti].ScrollPx = 0f; wt[ti].SendChannel = null; }
                w.SendChannel = Channel.Talk;
                w.ClampSendChannel();
                w.ChannelListOpen = false;
                w.Dirty = true;
                w.LastScroll = System.DateTime.MinValue;
            }
        }
    }
}
