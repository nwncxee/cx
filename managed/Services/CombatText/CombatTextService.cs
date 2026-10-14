using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Services.Nameplate;
using CX.Ui;

namespace CX.Services.CombatText;

/// <summary>
/// Damage numbers and floating combat text on our own 2D UI: one cold hook on <c>CNWCObject::ShowFloatyText</c>,
/// the funnel they all pass, captures the engine-composed string (vanilla's consolidation and damage-type
/// colours arrive free, as colour tags) and swallows the call. The anchor is the object's chat bubble
/// (<c>bubble+0x154</c>), fresh only while <see cref="NameplateService"/> suppresses bubbles; else vanilla keeps it.
/// </summary>
public sealed unsafe class CombatTextService : IExtenderService
{
    private static ExtenderLog? _log;
    private static bool _armed, _wasInModule;

    private static readonly FloatyTable _table = new();
    private static Window? _window;

    private static ShowFloatyTextFn _origShowFloaty;

    /// <summary>The ShowFloatyText unit: the funnel itself. Nameplates are a runtime prerequisite
    /// (checked per call, not here).</summary>
    private static readonly string[] RequiredSymbols = { SymbolMap.ShowFloatyText, SymbolMap.ExoStringDtor };

    /// <summary>Glyph height of the face the animation samples: the largest an element ever gets --
    /// the resting px (<c>combattext.font</c>, else the UI's) × the spawn pop × the banner multiplier -- so every
    /// frame is a minification. Capped at <see cref="TextRig.AtlasMaxPx"/>: past it (UI 18px needs textscale &gt; 3.05)
    /// the top of the pop magnifies again. Written cold; read on the frame, with <see cref="_peakFace"/> its typeface.</summary>
    private static volatile float _peakPx;
    private static volatile string _peakFace = FontRegistry.DefaultName;

    /// <summary>The peak height for the current UI size and multipliers.</summary>
    private static float PeakPx()
    {
        float px = PeakPxRaw();
        return px > TextRig.AtlasMaxPx ? TextRig.AtlasMaxPx : px;
    }

    /// <summary>The uncapped peak, so the status line can say when the cap is in force.</summary>
    private static float PeakPxRaw()
        => MathF.Round(CombatTextKnobs.Text.Px * FloatyAnim.PopFrom * CombatTextKnobs.BannerSize.V);

    /// <summary>COLD: (re)bake the peak face, dropping the previous one so the sheet does not accumulate every
    /// ceiling ever tried. Runs at arm time, on a <c>combattext.bannersize</c> or text-row set, and after a UI font change.</summary>
    /// <summary>The owner name the peak face is held under.</summary>
    private const string FaceOwner = "combattext";

    internal static void RequestPeakFace()
    {
        if (!_armed) return;
        float px = PeakPx();
        string face = CombatTextKnobs.Text.Typeface.V;
        if (px == _peakPx && face == _peakFace && TextRig.BakedFace(face, px) != null) return;
        if (_peakPx != 0f && (_peakPx != px || face != _peakFace)) TextRig.ReleaseFace(_peakFace, _peakPx, FaceOwner);
        _peakFace = face;
        _peakPx = TextRig.Face(face, px, FaceOwner) != null ? px : 0f;
        _log?.Info($"CombatTextService: peak face {(_peakPx > 0f ? $"{face} @ {_peakPx}px" : "unavailable, drawing from the UI face")}");
    }

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _log = log;
        // Every pure piece self-checks BEFORE any stand-down return -- run-demo's only gate.
        FloatyAnim.SelfCheck(log);
        FloatyTable.SelfCheck(log);
        CombatTextNative.SelfCheck(log);
        UiCategory.Declare();   // the UI groups declare in panel order whether or not the hook arms

        if (!SymbolMap.Require(log, "CombatTextService", RequiredSymbols)) return;   // whole or nothing

        _origShowFloaty = hooks.InstallShowFloatyText(&OnShowFloatyText);
        if (_origShowFloaty == null)
        {
            log.Info("CombatTextService: standing down (ShowFloatyText unresolved) -- vanilla damage numbers "
                   + "and combat text draw as they always did");
            return;
        }

        // Never hit-tests: this is world-anchored text over the 3D scene, and a UI rect here would take the
        // contextual cursor and swallow world clicks (see the nameplate feature's HitTestOverride trap).
        _window = new Window(0, 0, 0, 0) { Focusable = false, DrawContent = Draw };
        UiHost.SendToBack(_window);

        CombatTextKnobs.G.Status = Status;
        TextRig.FontChanged += RequestPeakFace;
        _armed = true;
        RequestPeakFace();
        log.Info("CombatTextService: armed (vanilla floaty text swallowed at ShowFloatyText)");
    }

    // ---- the hook ----------------------------------------------------------------------------

    /// <summary>The one seam. COLD -- the message-handling thread, once per damage number or banner -- so
    /// resolving strings and parsing colour runs here is fine; the render thread sees only the parsed result.
    /// Anything we cannot draw better is handed straight back to the original, which is both the opt-out and
    /// what keeps an element we could not anchor from being lost between the two renderers.</summary>
    [UnmanagedCallersOnly]
    private static void OnShowFloatyText(IntPtr self, IntPtr text, VectorArg colourArg, IntPtr font, int background)
    {
        Vec3 colour = colourArg.Value;   // Win64: read before the original, which owns the caller-made copy
        // Hand back to vanilla unless we can actually draw this one BETTER. Swallowing an element we then
        // fail to place would lose it outright, so every reason it might not reach the screen is tested here,
        // on the cold path, and not in the draw:
        //   - the master is off
        //   - the nameplate feature is stood down, so nothing keeps the object's screen anchor fresh
        //   - the object owns no chat bubble, so it has no anchor at all (a placeable or door taking damage:
        //     only creatures get one, via CNWCCreature::SetName)
        if (!_armed || !CombatTextKnobs.Enabled.V || self == IntPtr.Zero
            || !NameplateService.Suppressing || NameplateNative.Bubble(self).IsNull)
        {
            if (_origShowFloaty != null) _origShowFloaty(self, text, colourArg, font, background);
            return;
        }

        try
        {
            NameplateNative.UnfreezeAnchor(self);   // a bubble that idled under vanilla has a frozen anchor
            string body = Native.ReadExo(text);
            string fontName = Native.ReadExo(font);
            if (OperatingSystem.IsWindows()) { Native.DestroyExoString(text); Native.DestroyExoString(font); }   // Win64: the swallowed callee owned both copies
            if (body.Length == 0) return;                 // an empty floaty draws nothing in either renderer

            long now = Environment.TickCount64;
            FloatyKind kind = CombatTextNative.IsDamageFont(fontName) ? FloatyKind.Damage : FloatyKind.Banner;
            _table.Note(NameplateNative.IdOf(self), kind, ColorRunParser.Parse(body),
                        colour.X, colour.Y, colour.Z, now);
        }
        catch (Exception e)
        {
            // A managed throw must never unwind into the engine's C++ frame. Losing one number is nothing;
            // losing the client is not.
            _log?.Info($"[combattext] capture failed: {e.GetType().Name}");
        }
    }

    // ---- the per-frame draw ------------------------------------------------------------------

    /// <summary>Element geometry, reused. Doubling is a guard, not a path.</summary>
    private static float[] _verts = new float[4096];

    /// <summary>Bumped when the face the draw samples changes object (a pick, any re-bake): the key a slot's cached
    /// measure is valid under. Render thread only.</summary>
    private static int _faceKey;
    private static GlyphAtlas? _lastFace;

    private static void Draw()
    {
        if (!_armed) return;

        // Session reset: object ids are only unique within a module, so a number carried across an unload
        // would land on whatever inherits the id.
        if (_wasInModule && !GameGate.InModule) _table.Clear();
        _wasInModule = GameGate.InModule;

        long now = Environment.TickCount64;
        int count = _table.Advance(now, CombatTextKnobs.Max.V, CombatTextKnobs.Life.V, CombatTextKnobs.BannerLife.V);   // BEFORE every early return: the hook ring is fixed-size
        if (count == 0 || !GameGate.InGame || !CombatTextKnobs.Enabled.V) return;

        GlyphAtlas? atlas = TextRig.Atlas;
        if (atlas == null || !NameplateNative.Ready) return;

        float vpW = TextRig.ViewportW, vpH = TextRig.ViewportH;
        if (vpW <= 0f || vpH <= 0f) return;
        if (!NameplateNative.ViewportRect(out float rx, out float ry, out float rw, out float rh))
        { rx = 0f; ry = 0f; rw = vpW; rh = vpH; }
        bool hasCam = NameplateNative.CameraPosition(out float cx, out float cy, out float cz);
        bool hasFwd = NameplateNative.CameraForward(out float fx, out float fy, out float fz);
        float gui = Metrics.GameUiScale;
        float ui = Metrics.Scale;
        float uiPx = Metrics.DesignFontPx * ui;
        float restPx = CombatTextKnobs.Text.Px;
        float peakPx = _peakPx;
        GlyphAtlas face = TextRig.BakedFace(_peakFace, peakPx) ?? atlas;
        if (!ReferenceEquals(face, _lastFace)) { _lastFace = face; _faceKey++; }
        float facePx = face == atlas ? uiPx : peakPx;
        float down = restPx / facePx;
        float lineH = atlas.LineHeight * restPx / uiPx;
        float halo = CombatTextKnobs.Text.HaloPx;

        int n = 0;
        for (int i = 0; i < count; i++)
        {
            FloatyTable.Slot s = _table.Live[i];
            IntPtr obj = NameplateNative.GameObject(s.Id);
            if (obj == IntPtr.Zero) continue;                       // despawned: it just stops drawing
            if (!NameplateNative.AnchorRaw(obj, out float ax, out float ay)) continue;
            PlateLayout.ToDrawSpace(ax, ay, rx, ry, rw, rh, gui, vpW, vpH, out float x, out float y);
            if (x < 0f || y < 0f || x > vpW || y > vpH) continue;
            // The engine's projection never checks the sign of its denominator, so an object BEHIND the camera
            // lands on a MIRRORED anchor that passes the rect test above.
            if (hasCam && hasFwd)
            {
                NameplateNative.Position(obj, out float ox, out float oy, out float oz);
                if ((ox - cx) * fx + (oy - cy) * fy + (oz - cz) * fz <= 0f) continue;
            }

            Place(s, now, ui, lineH, ref x, ref y, out float scale, out float alpha);
            if (alpha <= 0f) continue;

            float k = scale * down;
            float w = Measure(s, face, facePx) * k;
            // The halo grows with the element: a 1px outline behind a 1.3x banner is a grey fringe, not an outline.
            EmitSlot(ref n, face, s, x - w * 0.5f, y, k, halo * (scale < 1f ? 1f : scale), alpha);
        }

        if (n > 0) TextRig.DrawBatch(_verts, n / VertexBatch.Floats);
    }

    /// <summary>Where and how big one element is this frame, per kind. The one place the behaviours the
    /// feature promises are chosen: a damage number scrolls and fades, a banner punches in and drifts.</summary>
    private static void Place(FloatyTable.Slot s, long now, float ui, float lineH,
                              ref float x, ref float y, out float scale, out float alpha)
    {
        long age = now - s.SpawnMs;
        float t = FloatyAnim.T(age, s.LifeMs);
        alpha = FloatyAnim.Fade(t);
        scale = FloatyAnim.Pop(age);

        // Everything on one head is a column. The step is the larger of the knob and a line of text, so a
        // bigger font never lets two rows grow into each other.
        float step = CombatTextKnobs.Stack.V * ui;
        if (step < lineH) step = lineH;
        y -= s.Stack * step;

        switch (s.Kind)
        {
            case FloatyKind.Damage:
                y -= CombatTextKnobs.Rise.V * ui * FloatyAnim.EaseOut(t);
                break;

            case FloatyKind.Banner:
                // Banners have their own column, lifted clear of the damage one.
                scale *= CombatTextKnobs.BannerSize.V;
                y -= CombatTextKnobs.Lift.V * ui + (CombatTextKnobs.Rise.V * 0.35f) * ui * FloatyAnim.EaseOut(t);
                break;
        }
    }

    /// <summary>Text width in <paramref name="atlas"/>'s px at scale 1, cached per slot and invalidated by an atlas
    /// re-bake or a change of measured face height.</summary>
    private static float Measure(FloatyTable.Slot s, GlyphAtlas atlas, float facePx)
    {
        if (s.MeasuredW >= 0f && s.MeasureEpoch == _faceKey && s.MeasurePx == facePx) return s.MeasuredW;
        float w = 0f;
        for (int r = 0; r < s.Runs.Length; r++)
            foreach (Rune ru in s.Runs[r].Text.EnumerateRunes()) w += atlas.AdvanceOf(ru);
        s.MeasuredW = w;
        s.MeasureEpoch = _faceKey;
        s.MeasurePx = facePx;
        return w;
    }

    /// <summary><paramref name="scale"/> is the quad factor on the face's glyphs; <paramref name="halo"/> the outline
    /// radius in screen px (0 = none). Halos in a first pass, bodies in a second, so no halo paints over a glyph.</summary>
    private static void EmitSlot(ref int n, GlyphAtlas atlas, FloatyTable.Slot s,
                                 float penX, float baseline, float scale, float halo, float alpha)
    {
        Span<float> o = TextStyle.HaloOffsets(halo, stackalloc float[TextStyle.MaxHaloFloats]);
        for (int pass = o.Length > 0 ? 0 : 1; pass < 2; pass++)
        {
            float pen = penX;
            for (int r = 0; r < s.Runs.Length; r++)
            {
                ColorRun run = s.Runs[r];
                // A rune the parser left at plain white takes the engine's own Vector colour -- the same
                // fallback rule the plate's override lines use for an untagged run.
                bool plain = run.R == 1f && run.G == 1f && run.B == 1f;
                float cr = plain ? s.BaseR : run.R, cg = plain ? s.BaseG : run.G, cb = plain ? s.BaseB : run.B;
                foreach (Rune ru in run.Text.EnumerateRunes())
                {
                    if (!atlas.TryGet(ru.Value, out Glyph gl)) { pen += atlas.AdvanceOf(ru) * scale; continue; }
                    if (pass == 1) EmitGlyph(ref n, gl, pen, baseline, scale, cr, cg, cb, alpha);
                    else
                        for (int i = 0; i < o.Length; i += 2)
                            EmitGlyph(ref n, gl, pen + o[i], baseline + o[i + 1], scale, 0f, 0f, 0f, alpha);
                    pen += gl.Advance * scale;
                }
            }
        }
    }

    private static void EmitGlyph(ref int n, in Glyph gl, float penX, float baseline, float scale,
                                  float r, float g, float b, float a)
    {
        if (gl.W <= 0) return;
        Grow(n + 6 * VertexBatch.Floats);
        float x0 = penX + gl.XOff * scale, y0 = baseline + gl.YOff * scale;
        float x1 = x0 + gl.W * scale, y1 = y0 + gl.H * scale;
        Vtx(ref n, x0, y0, gl.U0, gl.V0, r, g, b, a); Vtx(ref n, x1, y0, gl.U1, gl.V0, r, g, b, a);
        Vtx(ref n, x1, y1, gl.U1, gl.V1, r, g, b, a);
        Vtx(ref n, x0, y0, gl.U0, gl.V0, r, g, b, a); Vtx(ref n, x1, y1, gl.U1, gl.V1, r, g, b, a);
        Vtx(ref n, x0, y1, gl.U0, gl.V1, r, g, b, a);
    }

    private static void Grow(int need)
    {
        while (need > _verts.Length) Array.Resize(ref _verts, _verts.Length * 2);
    }

    private static void Vtx(ref int n, float x, float y, float u, float v, float r, float g, float b, float a)
    { float[] q = _verts; q[n++] = x; q[n++] = y; q[n++] = u; q[n++] = v; q[n++] = r; q[n++] = g; q[n++] = b; q[n++] = a; }

    /// <summary>The bare <c>/combattext</c> line: the rows, then the table's live counters and the peak face.</summary>
    private static string Status()
        => $"{CombatTextKnobs.G.Report()} | live={_table.LiveCount} overflow={_table.Overflow} "
         + $"peakface={_peakFace} @ {F(_peakPx)}px{(PeakPxRaw() > TextRig.AtlasMaxPx ? " (capped)" : "")}";

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
