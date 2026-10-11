using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat;
using CX.Services.Chat.Render;
using CX.Ui;

namespace CX.Services.Quickbar;

/// <summary>
/// Quickbar chrome: replaces <c>CPanelQuickBar::Reposition</c> to fix centring, suppress the buggy
/// <c>GUI_QBAR_ENDS</c> model and draw full-width background bars, emitting <see cref="RenderFrame"/>s that
/// <see cref="ChatService"/> draws alongside chat -- no second SwapBuffers hook. The replacement stands even with
/// <c>quickbar.enabled</c> off: the engine body attaches <c>GUI_QBAR_ENDS</c> past 800 px and never clears the flag.
/// </summary>
public sealed unsafe partial class QuickbarService : IExtenderService
{
    private static ExtenderLog? _log;

    private static readonly IntPtr _guiMan = Native.Sym(SymbolMap.GuiMan);
    private static readonly IntPtr _appMgr = Native.Sym(SymbolMap.AppManager);

    private const int QuickbarModelW = 800;
    private const int QuickbarBaseH = 36;

    private static volatile int _vpW, _vpH;
    private static float _guiScale = 1f;
    /// <summary>The snapping values the chrome was last built for (the grid overlay draws them).</summary>
    private static bool _gridOn;
    private static int _gridPx;

    private static volatile bool _dirty = true;

    // Compass hide/show engine calls.
    private static bool _compassApplied = true;

    private static volatile RenderFrame[] _extraFrames = Array.Empty<RenderFrame>();
    private static VertexBatch? _bandBatch;
    /// <summary>Extra frames this service wants drawn (the band under chat).</summary>
    public static RenderFrame[] ExtraFrames => _extraFrames;

    /// <summary>A layout knob moved (the master, align, bars, hidden): the next in-game tick repositions the
    /// quickbar and rebuilds the chrome. Cold, the setter's thread; the tick is the one place engine pointers are walked.</summary>
    internal static void LayoutChanged() => _dirty = true;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        UiCategory.Declare();   // the UI groups declare in panel order whether or not the hook arms
        QuickbarLayout.SelfCheck(log);
        // Stand down as a UNIT: every knob here ends in one of these three symbols, so a build
        // that resolves none of them would offer rows whose every control silently does nothing.
        // Not OS-gated -- gated on the symbols, so the Windows anchors landing is all it takes to switch on.
        QuickbarRepositionFn tramp = hooks.InstallQuickbarReposition(&OnReposition);
        if (tramp == null || _setPosScreen == null)
        {
            string missing = (tramp == null ? "CPanelQuickBar::Reposition " : "")
                           + (_setPosScreen == null ? "CGuiPanel::SetPositionScreen " : "");
            log.Always($"QuickbarService: standing down as a unit -- no anchor for {missing}" +
                       "(docs/windows-symbols.md); the quickbar rows answer instead of acting");
            QuickbarKnobs.G.Status = () => $"unavailable on this client: no anchor for {missing.Trim()}";
            QuickbarKnobs.G.PanelStatus = static () => "This version of the game gives CX no way to reach the quickbar, "
                                                     + "so these rows do nothing and the stock HUD is what you see.";
            return;
        }
        log.Info("QuickbarService: Reposition hook installed");
        InitCx(log, hooks);

        // A framework Window so the frame host runs the per-frame tick whether or not the chatbox is up. The
        // chrome itself still draws through ChatService's frame list; the tick is the viewport/scale edges, the
        // snapping knobs and the compass.
        _window = new Window(0, 0, 0, 0)
        {
            Focusable = false,
            DrawContent = () => { if (GameGate.InGame) Tick(Metrics.GameUiScale); else Sleep(); },
        };
    }

    private static Window? _window;

    private static readonly SetPositionScreenFn _setPosScreen = EngineFn.SetPositionScreen;

    [UnmanagedCallersOnly]
    private static void OnReposition(IntPtr self)
    {
        int vpW = 0;
        if (_guiMan != IntPtr.Zero)
        {
            IntPtr gm = *(IntPtr*)_guiMan;
            if (gm != IntPtr.Zero) { var man = new CGuiMan(gm); vpW = man.VpWidth; _vpH = man.VpHeight; }
        }
        _vpW = vpW;

        if (_setPosScreen == null) return;
        Place(self, vpW);
        _dirty = true;
    }

    /// <summary>The one position rule: the stock centre when the master is off, off-screen when hidden or the CX bar
    /// stands in for it, else the aligned x at the bottom edge.</summary>
    private static void Place(IntPtr qbar, int vpW)
    {
        if (QuickbarKnobs.Enabled.V && (QuickbarKnobs.Hidden.V || CxActive)) { _setPosScreen(qbar, -9999, -9999); return; }
        _setPosScreen(qbar, AlignedX(vpW), 0);
    }

    private static int AlignedX(int vpW) => !QuickbarKnobs.Enabled.V ? (vpW - QuickbarModelW) / 2 : QuickbarKnobs.Alignment switch
    {
        QbarAlign.Left => 0,
        QbarAlign.Right => vpW - QuickbarModelW,
        _ => (vpW - QuickbarModelW) / 2,
    };

    /// <summary>The in-game frame tick (this window's content-draw): refresh viewport, apply the snapping knobs,
    /// the compass, and -- when a knob or the viewport moved -- reposition the quickbar and rebuild the chrome.</summary>
    private static void Tick(float guiScale)
    {
        int vpW = 0, vpH = 0;
        if (_guiMan != IntPtr.Zero)
        {
            IntPtr gm = *(IntPtr*)_guiMan;
            if (gm != IntPtr.Zero) { var man = new CGuiMan(gm); vpW = man.VpWidth; vpH = man.VpHeight; }
        }
        if (vpW != _vpW || vpH != _vpH) { _vpW = vpW; _vpH = vpH; _dirty = true; }

        if (System.Math.Abs(guiScale - _guiScale) > 0.001f) { _guiScale = guiScale; _dirty = true; }

        ApplyCompass();

        bool gridOn = UiKnobs.Snap.V; int gridPx = UiKnobs.GridPx.V;
        if (gridOn != _gridOn || gridPx != _gridPx) { _gridOn = gridOn; _gridPx = gridPx; _dirty = true; }
        if (gridOn && gridPx > 0)
        {
            Chat.Core.ChatWindow.SnapPx = gridPx;
            Chat.Core.ChatWindow.SnapCX = vpW / 2f;
            Chat.Core.ChatWindow.SnapCY = vpH / 2f;
        }
        else
        {
            Chat.Core.ChatWindow.SnapPx = 0;
        }

        if (UiKnobs.EdgeSnap.V && vpW > 0)
        {
            Chat.Core.ChatWindow.EdgeThresh = Chat.Core.ChatWindow.EdgeMagnet;
            Chat.Core.ChatWindow.EdgeVpW = vpW;
            Chat.Core.ChatWindow.EdgeVpH = vpH;
        }
        else
        {
            Chat.Core.ChatWindow.EdgeThresh = 0;
        }

        if (_dirty) { _dirty = false; RepositionQuickbar(); RebuildChrome(); ChatService.RequestRebuild(); }
    }

    /// <summary>Go dormant off-module.</summary>
    private static void Sleep()
    {
        _compassApplied = true;
        _extraFrames = Array.Empty<RenderFrame>();
        Chat.Core.ChatWindow.SnapPx = 0;
        Chat.Core.ChatWindow.EdgeThresh = 0;
    }

    /// <summary>The quickbar rect for window snapping: the CX bar's, or the stock button bar (bottom-aligned 800x36).
    /// False when hidden or the viewport isn't known yet.</summary>
    public static bool SnapRectOut(out (float X, float Y, float W, float H) r)
    {
        if (CxActive) return CxRect(out r);
        int vpW = _vpW, vpH = _vpH;
        if ((QuickbarKnobs.Enabled.V && QuickbarKnobs.Hidden.V) || vpW <= 0 || vpH <= 0) { r = default; return false; }
        r = (AlignedX(vpW), vpH - QuickbarBaseH, QuickbarModelW, QuickbarBaseH);
        return true;
    }

    /// <summary>Apply compass visibility to the engine. Edge-triggered: only calls Hide/Show when the
    /// state changes (not every frame). Under an off master the row is inert: the stock compass shows.</summary>
    private static void ApplyCompass()
    {
        bool want = QuickbarKnobs.Compass.V || !QuickbarKnobs.Enabled.V;
        if (want == _compassApplied) return;
        CGuiInGame gui = LiveGuiInGame();
        if (gui.IsNull) return;
        _compassApplied = want;
        if (!want && EngineFn.HideCompass != null) gui.HideCompass();
        else if (want && EngineFn.ShowCompass != null) gui.ShowCompass();
    }

    /// <summary>Rebuild the chrome vertex batch: full-width band (if bars on and quickbar visible) and the
    /// snapping grid overlay.</summary>
    private static void RebuildChrome()
    {
        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null || _vpW <= 0 || _vpH <= 0)
        {
            _extraFrames = Array.Empty<RenderFrame>();
            return;
        }

        int vpW = _vpW, vpH = _vpH;
        VertexBatch b = _bandBatch ??= new VertexBatch(0, 0);
        b.Reset(atlas.SolidU, atlas.SolidV);

        if (QuickbarKnobs.Enabled.V && QuickbarKnobs.Bars.V && !QuickbarKnobs.Hidden.V && !CxActive)
        {
            float qW = QuickbarModelW;
            float qH = QuickbarBaseH;
            float qX = AlignedX(vpW);
            float qY = vpH - qH;

            const float barR = 0f, barG = 0f, barB = 0f, barA = 0.55f;
            if (qX > 0f)
                b.Rect(0f, qY, qX, (float)vpH, barR, barG, barB, barA);
            if (qX + qW < vpW)
                b.Rect(qX + qW, qY, (float)vpW, (float)vpH, barR, barG, barB, barA);
        }

        if (_gridOn && _gridPx > 0)
        {
            int gp = _gridPx;
            float cx = vpW / 2f, cy = vpH / 2f;
            for (float lx = cx % gp; lx < vpW; lx += gp)
            {
                bool ctr = System.Math.Abs(lx - cx) < 1f;
                b.Rect(lx, 0, lx + 1, vpH, ctr ? 0.8f : 0f, 0f, 0f, ctr ? 0.4f : 0.3f);
            }
            for (float ly = cy % gp; ly < vpH; ly += gp)
            {
                bool ctr = System.Math.Abs(ly - cy) < 1f;
                b.Rect(0, ly, vpW, ly + 1, ctr ? 0.8f : 0f, 0f, 0f, ctr ? 0.4f : 0.3f);
            }
        }

        int vc = b.VertexCount;
        _extraFrames = vc > 0
            ? new[] { new RenderFrame { Verts = b.ToArray(), Count = vc, BodyStart = vc, BodyEnd = vc } }
            : Array.Empty<RenderFrame>();
    }

    private static void RepositionQuickbar()
    {
        IntPtr qbar = LiveQuickbar();
        if (qbar == IntPtr.Zero || _setPosScreen == null) return;
        Place(qbar, _vpW);
    }

    private static IntPtr LiveQuickbar()
    {
        CGuiInGame gui = LiveGuiInGame();
        if (gui.IsNull) return IntPtr.Zero;
        return gui.Quickbar;
    }

    /// <summary>Walk to CGuiInGame* (the in-game GUI container). Used for quickbar + compass.</summary>
    private static CGuiInGame LiveGuiInGame()
    {
        if (_appMgr == IntPtr.Zero) return default;
        IntPtr mgr = *(IntPtr*)_appMgr;
        if (mgr == IntPtr.Zero) return default;
        IntPtr app = *(IntPtr*)mgr;
        if (app == IntPtr.Zero) return default;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        if (intern.IsNull) return default;
        return intern.InGameGui;
    }
}
