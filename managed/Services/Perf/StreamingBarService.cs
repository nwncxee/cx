using System;
using CX.Framework;
using CX.Ui;      // a genuine UI client: draws through the text rig and registers a UiHost window
using CX.Alloy;
using CX.Alloy.Db;

namespace CX.Services.Perf;

/// <summary>
/// The streaming bar (<c>ui.streaming</c>): a thin bar at the top centre while an area is still arriving after its load
/// screen. "Arriving" is three backlogs that drain to zero once an area has settled -- textures in flight
/// (<see cref="TexRegistry.InFlight"/>), parts waiting on geometry (<see cref="DbFeed.Retrying"/>) and light charts
/// being built (<see cref="MeshChart.InFlight"/>) -- and the bar fills as their sum falls from its peak. No allocation.
/// </summary>
public sealed class StreamingBarService : IExtenderService
{
    private const float Pad = 4f;
    /// <summary>The caption's colour by role; the pill and the bar follow the same look.</summary>
    private static uint CaptionRgb => Theme.Rgb(Role.Hi);

    private static readonly VertexBatch _b = new(0f, 0f);
    private static readonly char[] _text = new char[96];
    private static int _textLen;
    private static Bar _bar;
    private static long _last;
    private static int _bakedTex = -1, _bakedGeo = -1, _bakedCharts = -1, _bakedEpoch = -1;
    private static float _bakedFill = -1f, _bakedW, _bakedScale;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        UiCategory.Declare();
        SelfCheck(log);
        new Window(0, 0, 0, 0) { Focusable = false, DrawContent = Draw };
        log.Info($"StreamingBarService: registered ({(UiKnobs.Streaming.V ? "on" : "off")}; ui.streaming)");
    }

    /// <summary>When the bar shows and how full it is. Pure, so the self-check drives it headless.</summary>
    internal struct Bar
    {
        /// <summary>A backlog has to last this long before the bar appears (a texture or two landing is not streaming),
        /// and the bar lingers this long, full, after it drains.</summary>
        internal const float ShowAfter = 0.3f, HideAfter = 0.8f;

        internal bool Visible;
        internal int Peak;
        internal float Busy, Idle;

        /// <summary>Advance by <paramref name="dt"/> seconds with <paramref name="pending"/> items outstanding.</summary>
        internal void Step(float dt, int pending)
        {
            if (pending > 0)
            {
                Busy += dt; Idle = 0f;
                if (!Visible && Busy >= ShowAfter) { Visible = true; Peak = 0; }
                if (pending > Peak) Peak = pending;
            }
            else
            {
                Idle += dt; Busy = 0f;
                if (Visible && Idle >= HideAfter) Visible = false;
            }
        }

        /// <summary>0..1: how much of the peak backlog has landed.</summary>
        internal readonly float Fill(int pending) => Peak <= 0 ? 1f : Math.Clamp(1f - pending / (float)Peak, 0f, 1f);
    }

    /// <summary>Per-frame draw (render thread, hot).</summary>
    private static void Draw()
    {
        if (!UiKnobs.Streaming.V || !Renderer.On || !GameGate.InGame || LightmapBake.Held) { _last = 0; _bar = default; return; }
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        float dt = _last == 0 ? 0f : MathF.Min((now - _last) / (float)System.Diagnostics.Stopwatch.Frequency, 0.25f);
        _last = now;
        int tex = TexRegistry.InFlight, geo = DbFeed.Retrying, charts = Math.Max(MeshChart.InFlight, 0);
        int pending = tex + geo + charts;
        _bar.Step(dt, pending);
        if (!_bar.Visible) return;
        GlyphAtlas? atlas = TextRig.Atlas;
        float w = TextRig.ViewportW;
        if (atlas == null || w <= 0f) return;
        float fill = _bar.Fill(pending);
        if (tex != _bakedTex || geo != _bakedGeo || charts != _bakedCharts || fill != _bakedFill || w != _bakedW
            || _bakedScale != Metrics.Scale || _bakedEpoch != TextRig.AtlasEpoch)
            Bake(atlas, w, tex, geo, charts, fill);
        TextRig.DrawBatch(_b.AsSpan(), _b.VertexCount);
    }

    /// <summary>A dark pill at the top centre: the caption, and under it the bar filled to <paramref name="fill"/>.</summary>
    private static void Bake(GlyphAtlas atlas, float w, int tex, int geo, int charts, float fill)
    {
        _bakedTex = tex; _bakedGeo = geo; _bakedCharts = charts; _bakedFill = fill; _bakedW = w;
        _bakedScale = Metrics.Scale; _bakedEpoch = TextRig.AtlasEpoch;

        _textLen = 0;
        if (tex + geo + charts == 0) Append("Area streamed in");
        else
        {
            Append("Streaming area");
            if (tex > 0) { Append("  textures "); AppendInt(tex); }
            if (geo > 0) { Append("  meshes "); AppendInt(geo); }
            if (charts > 0) { Append("  light charts "); AppendInt(charts); }
        }
        float textW = 0f;
        for (int i = 0; i < _textLen; i++) textW += atlas.TryGet(_text[i], out Glyph g) ? g.Advance : atlas.AdvanceOf(new System.Text.Rune(_text[i]));

        float s = Metrics.Scale, pad = Pad * s, barH = MathF.Max(2f, 3f * s);
        float pillW = MathF.Max(textW, 220f * s) + pad * 2f, pillH = atlas.LineHeight + barH + pad * 3f;
        float x0 = (w - pillW) * 0.5f, y0 = 6f * s;

        _b.Reset(atlas.SolidU, atlas.SolidV);
        PanelChrome.Fill(_b, x0, y0, x0 + pillW, y0 + pillH, Theme.Rgb(Role.PanelBg), 0.55f);
        float tx = x0 + (pillW - textW) * 0.5f, baseline = y0 + pad + atlas.Ascent;
        uint caption = CaptionRgb;
        float r = ((caption >> 16) & 0xFF) / 255f, gg = ((caption >> 8) & 0xFF) / 255f, bb = (caption & 0xFF) / 255f;
        for (int i = 0; i < _textLen; i++)
        {
            if (atlas.TryGet(_text[i], out Glyph gl)) { _b.Glyph(gl, tx, baseline, r, gg, bb, 1f); tx += gl.Advance; }
            else tx += atlas.AdvanceOf(new System.Text.Rune(_text[i]));
        }
        float bx0 = x0 + pad, bx1 = x0 + pillW - pad, by0 = y0 + pad * 2f + atlas.LineHeight;
        PanelChrome.Fill(_b, bx0, by0, bx1, by0 + barH, Theme.Rgb(Role.Track));
        PanelChrome.Fill(_b, bx0, by0, bx0 + (bx1 - bx0) * fill, by0 + barH, Theme.Rgb(Role.Accent));
    }

    private static void Append(string s)
    {
        for (int i = 0; i < s.Length && _textLen < _text.Length; i++) _text[_textLen++] = s[i];
    }

    private static void AppendInt(int v)
    {
        Span<char> d = stackalloc char[10];
        int n = 0;
        do { d[n++] = (char)('0' + v % 10); v /= 10; } while (v > 0 && n < d.Length);
        while (n > 0 && _textLen < _text.Length) _text[_textLen++] = d[--n];
    }

    /// <summary>Off-device: a blip never shows, a backlog shows after its delay and fills as it drains, the bar
    /// lingers full and then hides, and a new backlog starts a fresh peak.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "StreamingBar.SelfCheck");
        var b = new Bar();
        b.Step(0.1f, 5); b.Step(0.1f, 0);
        check.Expect(!b.Visible, "a backlog shorter than ShowAfter never shows");
        for (int i = 0; i < 4; i++) b.Step(0.1f, 40);
        check.Expect(b.Visible && b.Peak == 40 && b.Fill(40) == 0f, "a lasting backlog shows, empty, at its peak");
        b.Step(0.1f, 10);
        check.Expect(MathF.Abs(b.Fill(10) - 0.75f) < 1e-5f, "three quarters landed is three quarters full");
        b.Step(0.1f, 0);
        check.Expect(b.Visible && b.Fill(0) == 1f, "drained: full, still up");
        for (int i = 0; i < 9; i++) b.Step(0.1f, 0);
        check.Expect(!b.Visible, "...and hidden after HideAfter");
        for (int i = 0; i < 4; i++) b.Step(0.1f, 8);
        check.Expect(b.Visible && b.Peak == 8, "the next backlog starts a fresh peak");
        log.Info(check.Ok ? "StreamingBar.SelfCheck: passed" : "StreamingBar.SelfCheck: FAILED");
    }
}
