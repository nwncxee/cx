using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Ui;

/// <summary>
/// Hosts the settings panel: one registered <see cref="Window"/>, the <c>/settings</c> door, the panel's own
/// faces (design <see cref="KnobPanel.FacePx"/> × the game's GUI scale, requested cold at open), keyboard
/// ownership while open, and the per-frame draw -- <see cref="KnobPanel.Emit"/> into a reused batch, uploaded
/// through the shared text rig. Escape, a module unload, or losing the surface it opened on closes it.
/// </summary>
public sealed unsafe class KnobPanelService : IExtenderService
{
    private static ExtenderLog? _log;
    private static readonly KnobPanel _panel = new();
    /// <summary>The panel's registry entry. The window registry is immutable after the first frame, so this
    /// registers at Initialize and gates its content on <see cref="KnobPanel.Open"/>.</summary>
    private static Window? _window;
    private static VertexBatch? _batch;
    private static int _epoch = -1;
    private static float _facePx, _smallPx;
    private static GlyphAtlas? _body, _small;

    /// <summary>The live panel, for the gear and any other caller that drives it.</summary>
    public static KnobPanel Panel => _panel;
    public static bool IsOpen => _panel.Open;

    /// <summary>Where the panel draws and takes input: in game, at the main menu, or over the in-game Options
    /// panel (the gear's two surfaces) -- never the loading screen. The ESC menu with a sub-panel up hides it;
    /// Resume brings it back in game.</summary>
    private static bool Visible => GameGate.InGame || GameGate.AtMainMenu || GameGate.OptionsOpen;
    private static bool _wasInModule, _wasAtMenu;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _panel.Log = log;
        KnobPanel.SelfCheck(log);
        // Focusable = false like every CX window: the panel self-manages the keyboard (a framework focus change
        // clears WantsKeyboard silently, which would leave the engine gate up with nobody to drop it). Registered
        // after Chat and Dialog by discovery order, so it paints above them; SuppressChords while open puts it in
        // the modal press pass ahead of a dialog it overlaps.
        _window = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            DrawContent = Draw,
            OnMouseButton = OnMouseButton,
            OnMouseMotion = OnMouseMotion,
            OnMouseWheel = OnMouseWheel,
            OnKey = OnKey,
            OnText = OnText,
            HitTestOverride = (px, py) => _panel.Open && Visible && _panel.Contains(px, py),
        };
        log.Info("KnobPanelService: settings panel registered (/settings opens it)");
    }

    /// <summary>Open the panel: bake its faces, centre it in the last presented viewport, take the keyboard. Cold,
    /// input thread; a second call while open unfolds a collapsed panel and re-takes the keyboard.</summary>
    public static void Open()
    {
        if (_window == null) { _log?.Info("KnobPanelService: /settings -- no window (framework stood down)"); return; }
        RequestFaces();
        if (!_panel.Open)
        {
            float vw = TextRig.ViewportW > 0 ? TextRig.ViewportW : 1280f;
            float vh = TextRig.ViewportH > 0 ? TextRig.ViewportH : 720f;
            _panel.OpenAt(vw, vh, _facePx, _body, _small);
            SyncRect();
            _log?.Info($"KnobPanelService: opened {_panel.W}x{_panel.H} at ({_panel.X},{_panel.Y}) in {vw}x{vh}, face {_facePx}px");
        }
        if (_panel.Collapsed) { _panel.ToggleCollapse(); SyncRect(); }
        SyncKeyboard();
    }

    /// <summary>Close and release the keyboard (the panel may already have closed itself on Escape or its ×).</summary>
    public static void Close()
    {
        bool was = _panel.Open;
        _panel.Close();
        if (_window != null)
        {
            _window.SuppressChords = false;
            if (_window.WantsKeyboard) UiHost.RequestKeyboard(_window, false);
        }
        if (was) _log?.Info("KnobPanelService: closed");
    }

    /// <summary>The panel's faces at the current GUI scale. A miss bakes, so this runs cold only.</summary>
    private static void RequestFaces()
    {
        float scale = Metrics.GameUiScale < 1f ? 1f : Metrics.GameUiScale;
        _facePx = MathF.Round(KnobPanel.FacePx * scale);
        _smallPx = MathF.Round(KnobPanel.SmallPx * scale);
        _body = TextRig.Face(_facePx) ?? TextRig.Atlas;
        _small = TextRig.Face(_smallPx) ?? _body;
        _epoch = TextRig.AtlasEpoch;
    }

    /// <summary>Mirror the panel rect onto the window so focus hit-testing and the cursor pick see it.</summary>
    private static void SyncRect()
    {
        if (_window == null) return;
        _window.X = _panel.X; _window.Y = _panel.Y; _window.W = _panel.W; _window.H = _panel.ShownH;
    }

    /// <summary>Open, the panel is modal and owns the keyboard; collapsed it lets both go, so the game plays behind it.</summary>
    private static void SyncKeyboard()
    {
        if (_window == null || !_panel.Open) return;
        bool own = !_panel.Collapsed;
        _window.SuppressChords = own;
        if (_window.WantsKeyboard != own) UiHost.RequestKeyboard(_window, own);
    }

    /// <summary>A GUI-scale or viewport change while open: re-request the faces and re-lay out. Cold, on the
    /// next input event; the draw meanwhile shows the old layout with whatever face is baked.</summary>
    private static void Refit()
    {
        if (!_panel.Open) return;
        float scale = Metrics.GameUiScale < 1f ? 1f : Metrics.GameUiScale;
        bool face = MathF.Round(KnobPanel.FacePx * scale) != _facePx;
        int vpW = TextRig.ViewportW, vpH = TextRig.ViewportH;
        bool vp = vpW > 0 && (vpW != (int)_panel.VpW || vpH != (int)_panel.VpH);
        if (!face && !vp) return;
        if (face) RequestFaces();
        _panel.Rebase(vpW > 0 ? vpW : _panel.VpW, vpH > 0 ? vpH : _panel.VpH, _facePx, _body, _small);
        SyncRect();
    }

    private static bool OnMouseButton(bool down, int button, int x, int y)
    {
        if (!_panel.Open || !Visible) return false;
        if (button != 1) return _panel.Contains(x, y);
        if (!down) return _panel.MouseUp();
        Refit();
        bool wasEditing = _panel.Editing;
        if (!_panel.MouseDown(x, y)) return false;
        SyncKeyboard();
        if (_panel.Editing && !wasEditing && EngineFn.SdlStartTextInput != null) EngineFn.SdlStartTextInput();
        if (!_panel.Open) Close();
        SyncRect();
        return true;
    }

    private static bool OnMouseMotion(int x, int y)
    {
        bool r = _panel.MouseMove(x, y);
        if (_panel.Moving) SyncRect();
        return r && _panel.Open && Visible;
    }

    private static bool OnMouseWheel(int dy) => _panel.Open && Visible && _panel.MouseWheel(dy);

    /// <summary>While open the window owns the keyboard, so every key lands here. Escape closes; Enter and
    /// Backspace drive a field edit; typed characters arrive as text.</summary>
    private static bool OnKey(uint sdlKey, int mods)
    {
        if (!_panel.Open) return false;
        if (!_panel.Key(sdlKey)) Close();
        return true;
    }

    private static void OnText(IntPtr payload)
    {
        if (!_panel.Open || !_panel.Editing || payload == IntPtr.Zero) return;
        string? s = Marshal.PtrToStringUTF8(payload);
        if (!string.IsNullOrEmpty(s)) _panel.EditInsert(s);
    }

    /// <summary>Per frame (render thread, hot): track the viewport, re-fetch the faces by epoch, emit and upload.
    /// Allocates nothing while open and idle -- the batch is reused and the panel's text is cached. Leaving the
    /// surface the panel is open on -- a module unload, or the main menu replaced by a sub-screen or a load --
    /// closes it; an ESC-menu sub-panel or the hidden HUD only hides it.</summary>
    private static void Draw()
    {
        if (_log == null || !Gl.Init(_log)) return;
        bool inModule = GameGate.InModule, atMenu = GameGate.AtMainMenu;
        bool left = (_wasInModule && !inModule) || (_wasAtMenu && !atMenu);
        _wasInModule = inModule; _wasAtMenu = atMenu;
        if (!_panel.Open) return;
        if (left) { Close(); return; }
        if (!Visible) return;
        if (_epoch != TextRig.AtlasEpoch)
        {
            _body = TextRig.BakedFace(_facePx) ?? TextRig.Atlas;
            _small = TextRig.BakedFace(_smallPx) ?? _body;
            _panel.Body = _body; _panel.Small = _small;
            _epoch = TextRig.AtlasEpoch;
        }
        GlyphAtlas? atlas = _panel.Body;
        if (atlas == null) return;
        VertexBatch b = _batch ??= new VertexBatch(atlas.SolidU, atlas.SolidV);
        b.Reset(atlas.SolidU, atlas.SolidV);
        _panel.Emit(b);
        TextRig.DrawBatch(b.AsSpan(), b.VertexCount);
    }
}
