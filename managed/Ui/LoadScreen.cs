using System;
using CX.Alloy;
using CX.Engine;
using CX.Framework;

namespace CX.Ui;

/// <summary>The ONE loading screen is the engine's own <c>CGuiAreaLoadscreen</c> -- whatever layout, art and bar
/// position the module or server gives it -- drawn by Alloy through the GUI capture, from the area click to the
/// first world frame. CX only shares the panel's bar: the engine's load fills the first <see cref="EngineShare"/>
/// of it (<c>CGuiStatusBar::SetStatus</c>, remapped), the bake hold the rest, with CX's caption and skip over it.</summary>
internal static unsafe class LoadScreen
{
    /// <summary>The share of the bar the engine's own load phase owns; the bake hold fills the rest.</summary>
    internal const float EngineShare = 0.65f;
    /// <summary>The screen's three colours by role: the caption, the skip line and the backdrop.</summary>
    private static uint CaptionRgb => Theme.Rgb(Role.Hi);
    private static uint SkipRgb => Theme.Rgb(Role.Tx2);
    private static uint BgRgb => Theme.Rgb(Role.PanelBg);

    /// <summary>The engine's <c>SetStatus</c>, for the bake phase's writes (which must not be remapped again).</summary>
    internal static StatusBarSetStatusFn Orig;

    private static readonly VertexBatch _fg = new(0f, 0f);
    /// <summary>The bar the current load drives, as an identity only -- never dereferenced (the panel deletes itself).</summary>
    private static IntPtr _bar;
    private static bool _remap, _bake, _skipOpen;
    private static float _engine01, _bake01, _shown;
    private static string _caption = "";
    private static float _skipX0, _skipX1, _skipY0, _skipY1;
    private static float _bakedW, _bakedH, _bakedShown, _bakedScale;
    private static int _bakedEpoch = -1, _bakedTheme = -1;
    private static string? _bakedCaption;
    private static bool _bakedSkip, _bakedPanel;

    /// <summary>The live load screen's status bar, or zero when no load screen is up.</summary>
    internal static IntPtr Bar()
    {
        IntPtr app = PlayerIdentity.App();
        if (app == IntPtr.Zero) return IntPtr.Zero;
        CClientExoAppInternal intern = new CClientExoApp(app).Internal;
        if (intern.IsNull) return IntPtr.Zero;
        CGuiAreaLoadscreen panel = intern.Loadscreen;
        return panel.IsNull ? IntPtr.Zero : panel.StatusBar.Ptr;
    }

    /// <summary>An engine load screen is on screen.</summary>
    internal static bool PanelUp => Bar() != IntPtr.Zero;

    /// <summary>The engine set <paramref name="bar"/> to <paramref name="percent"/>: the value it should store instead.
    /// Only the live load screen's bar is touched, and only when a bake hold can follow (<paramref name="holdCanFollow"/>)
    /// -- otherwise the engine's own load owns the whole bar, as in vanilla.</summary>
    internal static float EngineStatus(IntPtr bar, float percent, bool holdCanFollow)
    {
        if (bar == IntPtr.Zero || bar != Bar()) return percent;
        if (bar != _bar) { Reset(); _bar = bar; _remap = holdCanFollow; }
        if (!_remap || _bake) return percent;
        _engine01 = Math.Clamp(percent * 0.01f, 0f, 1f);
        _shown = Advance(_shown, Combine(_engine01, 0f, true, false));
        return _shown * 100f;
    }

    /// <summary>The bake hold's progress and caption, pushed into the load screen's bar when one is up. The engine
    /// screen stays up through the hold by itself: the hold swallows the AreaLoaded send, and
    /// <c>CGuiAreaLoadscreen::Update</c> @0x76ba00 waits in its end phase for the placement that send triggers
    /// before it fades.</summary>
    internal static void BakeStep(float fraction, string caption, bool skipOpen)
    {
        IntPtr bar = Bar();
        if (!_bake && bar != _bar) { Reset(); _bar = bar; }
        _bake = true;
        _bake01 = Math.Clamp(fraction, 0f, 1f);
        _caption = caption;
        _skipOpen = skipOpen;
        _shown = Advance(_shown, Combine(_engine01, _bake01, _bar != IntPtr.Zero, true));
        if (bar != IntPtr.Zero && Orig != null) Orig(bar, _shown * 100f);
    }

    /// <summary>The load is over for CX (the hold released, or the AreaLoaded send passed straight through): fill the
    /// bar before the engine fades its screen out, and forget the load.</summary>
    internal static void End()
    {
        IntPtr bar = Bar();
        if (bar != IntPtr.Zero && bar == _bar && _remap && Orig != null) Orig(bar, 100f);
        Reset();
    }

    private static void Reset()
    {
        _bar = IntPtr.Zero; _remap = false; _bake = false; _skipOpen = false;
        _engine01 = 0f; _bake01 = 0f; _shown = 0f; _caption = ""; _bakedCaption = null;
    }

    /// <summary>The skip line's hit box (the click target beside Escape); false whenever no skip is offered.</summary>
    internal static bool HitSkip(float x, float y)
        => _skipOpen && x >= _skipX0 && x < _skipX1 && y >= _skipY0 && y < _skipY1;

    /// <summary>The bake hold's share of the screen, at the present: the caption and skip line over the engine's load
    /// screen, or -- with no engine screen up -- CX's own backdrop and bar under them. Allocates nothing once baked.</summary>
    internal static void Draw()
    {
        float w = TextRig.ViewportW, h = TextRig.ViewportH;
        GlyphAtlas? atlas = TextRig.Atlas;
        if (w <= 0f || h <= 0f || atlas == null || !_bake) return;
        bool panel = PanelUp;
        Rebake(atlas, w, h, panel);
        TextRig.DrawBatch(_fg.AsSpan(), _fg.VertexCount);
    }

    /// <summary>CX's backdrop with two centred lines: what a blocking shader compile puts up when no engine load screen is
    /// on screen (under one, the frozen screen is the notice). The caller owns the context, the viewport and the present.</summary>
    internal static void Notice(string caption, string sub)
    {
        float w = TextRig.ViewportW, h = TextRig.ViewportH;
        GlyphAtlas? atlas = TextRig.Atlas;
        if (w <= 0f || h <= 0f || atlas == null) return;
        _fg.Reset(atlas.SolidU, atlas.SolidV);
        PanelChrome.Fill(_fg, 0f, 0f, w, h, BgRgb);
        float y0 = h * 0.72f;
        UiText.Draw(_fg, atlas, caption, (w - UiText.Measure(atlas, caption)) * 0.5f, y0 - atlas.LineHeight * 0.6f, CaptionRgb, w);
        UiText.Draw(_fg, atlas, sub, (w - UiText.Measure(atlas, sub)) * 0.5f, y0 + atlas.LineHeight * 1.4f, SkipRgb, w);
        TextRig.DrawBatch(_fg.AsSpan(), _fg.VertexCount);
        _bakedCaption = null;   // the notice reused the batch: the hold's lines rebake at their next draw
    }

    /// <summary>Rebuild the batch when anything it was built from moved. Over an engine screen: the two lines only, low
    /// and centred with a halo, so the module's own layout stays readable. Without one: backdrop, bar, lines.</summary>
    private static void Rebake(GlyphAtlas atlas, float w, float h, bool panel)
    {
        if (_bakedW == w && _bakedH == h && _bakedShown == _shown && _bakedScale == Metrics.Scale && _bakedPanel == panel
            && ReferenceEquals(_bakedCaption, _caption) && _bakedSkip == _skipOpen && _bakedEpoch == TextRig.AtlasEpoch
            && _bakedTheme == Theme.Epoch)
            return;
        _bakedW = w; _bakedH = h; _bakedShown = _shown; _bakedScale = Metrics.Scale; _bakedPanel = panel;
        _bakedCaption = _caption; _bakedSkip = _skipOpen; _bakedEpoch = TextRig.AtlasEpoch; _bakedTheme = Theme.Epoch;

        _fg.Reset(atlas.SolidU, atlas.SolidV);
        float y0;
        if (panel) y0 = h - atlas.LineHeight * (_skipOpen ? 3.6f : 2.2f);
        else
        {
            PanelChrome.Fill(_fg, 0f, 0f, w, h, BgRgb);
            float barW = MathF.Min(w * 0.5f, 640f * Metrics.Scale), barH = 10f * Metrics.Scale;
            float x0 = (w - barW) * 0.5f;
            y0 = h * 0.72f;
            PanelChrome.Fill(_fg, x0, y0, x0 + barW, y0 + barH, Theme.Rgb(Role.Track));
            PanelChrome.Fill(_fg, x0, y0, x0 + barW * _shown, y0 + barH, Theme.Rgb(Role.Accent));
            y0 -= atlas.LineHeight * 0.6f;
        }
        _fg.Outline = panel ? MathF.Max(1f, 1.5f * Metrics.Scale) : 0f;
        UiText.Draw(_fg, atlas, _caption, (w - UiText.Measure(atlas, _caption)) * 0.5f, y0, CaptionRgb, w);
        if (_skipOpen)
        {
            const string skip = "Press Escape to skip";
            float sw = UiText.Measure(atlas, skip), sx = (w - sw) * 0.5f, sy = y0 + atlas.LineHeight * 1.4f;
            UiText.Draw(_fg, atlas, skip, sx, sy, SkipRgb, w);
            _skipX0 = sx - 8f; _skipX1 = sx + sw + 8f; _skipY0 = sy - atlas.Ascent - 4f; _skipY1 = sy + atlas.LineHeight * 0.5f + 4f;
        }
    }

    /// <summary>The two sources as one bar. The engine phase owns [0, <see cref="EngineShare"/>] and the bake
    /// phase the rest, so the handover is continuous; a bake with no engine phase behind it owns the whole bar.</summary>
    internal static float Combine(float engine01, float bake01, bool engineRan, bool bakePhase)
    {
        engine01 = Math.Clamp(engine01, 0f, 1f);
        bake01 = Math.Clamp(bake01, 0f, 1f);
        if (!bakePhase) return engine01 * EngineShare;
        return engineRan ? EngineShare + bake01 * (1f - EngineShare) : bake01;
    }

    /// <summary>The bar never walks backwards within one load: both sources are estimates and the engine's own
    /// status bar is re-based per block (<c>LoadBorder</c> restarts it at a quarter of the way).</summary>
    internal static float Advance(float shown, float target) => target > shown ? target : shown;

    /// <summary>Headless: the combined mapping and the monotonic latch.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "LoadScreen.SelfCheck");
        check.Expect(EngineShare is > 0f and < 1f, "the engine's share of the bar must leave room for the bake phase");
        check.Expect(Combine(0f, 0f, false, false) == 0f, "an untouched load starts the bar at zero");
        check.Expect(Combine(1f, 0f, true, false) == EngineShare, "the engine phase ends exactly at its share");
        check.Expect(Combine(1f, 0f, true, true) == EngineShare, "the handover into the bake phase does not move the bar");
        check.Expect(Combine(0.5f, 1f, true, true) == 1f, "a finished bake fills the bar whatever the engine term says");
        check.Expect(Combine(0f, 0.5f, false, true) == 0.5f, "a bake with no engine phase behind it owns the whole bar");
        check.Expect(Combine(2f, -1f, true, true) == EngineShare, "out-of-range sources are clamped, not wrapped");
        check.Expect(Advance(0.4f, 0.2f) == 0.4f, "the bar never walks backwards");
        check.Expect(Advance(0.4f, 0.6f) == 0.6f, "...but it does follow forwards");
        float prev = 0f;
        for (int i = 0; i <= 10; i++) prev = Advance(prev, Combine(i / 10f, 0f, true, false));
        for (int i = 0; i <= 10; i++)
        {
            float next = Advance(prev, Combine(1f, i / 10f, true, true));
            check.Expect(next >= prev, "the engine -> bake walk is monotonic at every step");
            prev = next;
        }
        check.Expect(prev == 1f, "the walk ends full");
        check.Expect(EngineStatus(IntPtr.Zero, 42f, true) == 42f, "a bar that is not the load screen's is never touched");
        log.Info(check.Ok ? "LoadScreen.SelfCheck: passed" : "LoadScreen.SelfCheck: FAILED");
    }
}
