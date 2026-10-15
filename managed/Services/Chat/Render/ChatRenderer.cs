using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;
using CX.Graphics;

using CX.Services.Chat.Core.Model;

namespace CX.Services.Chat.Render;

/// <summary>
/// Chat-coupled owned-render pass. Owns the per-window <see cref="Frame"/> publish/animation buffering and
/// drives the shared <see cref="TextRig"/> primitives (program, atlas, VAO/VBO rig, portrait rig) to draw the
/// chat windows inside the SwapBuffers hook. Callers build a batch cold (ingest/input/resize thread) via
/// <see cref="VertexBatch"/>; the hot <see cref="Draw"/> uploads-if-changed, one draw per window, zero-alloc.
/// </summary>
public static unsafe class ChatRenderer
{
    private const int Floats = VertexBatch.Floats;   // x,y,u,v,r,g,b,a

    /// <summary>Injected log + baked atlas, both owned by <see cref="TextRig"/> (set in its Init).</summary>
    private static ExtenderLog? _log => TextRig.Log;
    private static GlyphAtlas? _atlas => TextRig.Atlas;

    private static bool _diagged;              // one-shot: log GL error + viewport on the first drawn frame

    /// <summary>A published frame: the built <see cref="RenderFrame"/> plus a per-frame <see cref="Scratch"/>
    /// buffer (same length, allocated cold at publish). The hot draw copies the base verts into scratch and
    /// fades them there, so it allocates nothing and a concurrent publish can never hand the animator a half-built
    /// array. <see cref="Settled"/>/<see cref="OpaqueCount"/>: stable alphas let a draw skip that walk.</summary>
    private sealed class Frame
    {
        public readonly RenderFrame Rf;
        public readonly float[] Scratch;
        public bool Settled;
        public int OpaqueCount;
        public Frame(RenderFrame rf, float[] scratch) { Rf = rf; Scratch = scratch; }
    }
    /// <summary>All published frames (one per chat window). Volatile-swapped atomically by
    /// <see cref="Publish(RenderFrame[])"/>; the hot <see cref="Draw"/> iterates the snapshot.</summary>
    private static volatile Frame[] _frames = System.Array.Empty<Frame>();

    private static int _drawCount;
    private static bool _portraitDrew;   // one-shot: log the first portrait that actually binds a live TID

    /// <summary>Publish frames for all chat windows. Reuses the <see cref="Frame"/> wrapper (and its Scratch
    /// buffer + Settled state) when the <see cref="RenderFrame"/> is reference-equal to a prior publish, so a
    /// focusedOnly/dirty rebuild avoids LOH scratch allocations. A changed frame recycles a large-enough old
    /// scratch buffer (claimed once) in a fresh <see cref="Frame"/>, so the hot draw never tears Rf+Scratch.</summary>
    public static void Publish(RenderFrame[] rfs)
    {
        Frame[] old = _frames;
        var arr = new Frame[rfs.Length];
        for (int i = 0; i < rfs.Length; i++)
        {
            Frame? reuse = null;
            for (int j = 0; j < old.Length; j++)
                if (ReferenceEquals(old[j].Rf, rfs[i])) { reuse = old[j]; break; }
            if (reuse != null) { arr[i] = reuse; continue; }
            // Recycle an unclaimed scratch buffer from the previous publish if large enough.
            float[]? scratch = null;
            int need = rfs[i].Verts.Length;
            for (int j = 0; j < old.Length; j++)
            {
                if (old[j].Scratch.Length >= need)
                {
                    bool claimed = false;
                    for (int k = 0; k < i; k++)
                        if (ReferenceEquals(arr[k].Scratch, old[j].Scratch)) { claimed = true; break; }
                    if (!claimed) { scratch = old[j].Scratch; break; }
                }
            }
            arr[i] = new Frame(rfs[i], scratch ?? new float[need]);
        }
        _frames = arr;
    }

    /// <summary>Single-frame convenience (probe demo, backward compat).</summary>
    public static void Publish(RenderFrame? rf)
        => _frames = rf == null ? System.Array.Empty<Frame>() : new[] { new Frame(rf, new float[rf.Verts.Length]) };

    /// <summary>Chrome-only convenience (the probe demo): draw a flat batch, no body/animation.</summary>
    public static void Publish(float[] verts, int vertCount)
        => Publish(new RenderFrame { Verts = verts, Count = vertCount, BodyStart = vertCount, BodyEnd = vertCount });

    /// <summary>Clear the drawn batch (e.g. probe demo off).</summary>
    public static void Clear() => _frames = System.Array.Empty<Frame>();

    // ---- demo (probe rig: `~cx text`) -------------------------------------------------------

    private static bool _demoOn;
    private const string Demo =
        "<cFFD200>CX</c> chat -- Ćma œuf ß: <c46C246>Привет</c>, " +
        "<c4AA3FF>мир</c>! now wrap this line across the box.";

    /// <summary>Toggle the hardcoded demo string over the shared primitives (the owned-render demo).</summary>
    public static bool ToggleDemo()
    {
        if (_atlas == null) { _log?.Info("TextRig: no atlas; can't draw text"); return false; }
        _demoOn = !_demoOn;
        if (_demoOn) { var b = BuildDemo(_atlas); Publish(b.Item1, b.Item2); }
        else Clear();
        _log?.Info($"TextRig: demo={_demoOn}");
        return _demoOn;
    }

    private static (float[], int) BuildDemo(GlyphAtlas atlas)
    {
        var runes = new List<Rune>();
        var cols = new List<uint>();
        ParseColored(Demo, runes, cols);
        var batch = new VertexBatch(atlas.SolidU, atlas.SolidV);

        const float maxWidth = 320f, startX = 60f, startY = 200f;
        List<TextWrapper.LineSpan> lines = TextWrapper.WrapRunes(runes, maxWidth, atlas.AdvanceOfCached);
        for (int li = 0; li < lines.Count; li++)
        {
            float baseline = startY + (li + 1) * atlas.LineHeight, penX = startX;
            TextWrapper.LineSpan span = lines[li];
            for (int k = span.Start; k < span.End; k++)
            {
                Rune r = runes[k];
                if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
                uint c = cols[k];
                batch.Glyph(g, penX, baseline, ((c >> 16) & 0xFF) / 255f, ((c >> 8) & 0xFF) / 255f, (c & 0xFF) / 255f, 1f);
                penX += g.Advance;
            }
        }
        return (batch.ToArray(), batch.VertexCount);
    }

    /// <summary>Throwaway &lt;cRRGGBB&gt;...&lt;/c&gt; parser for the demo (real ingest uses ColorRunParser).</summary>
    private static void ParseColored(string s, List<Rune> runes, List<uint> cols)
    {
        uint color = 0xFFFFFF;
        var seg = new StringBuilder();
        void Flush() { foreach (Rune r in seg.ToString().EnumerateRunes()) { runes.Add(r); cols.Add(color); } seg.Clear(); }
        int i = 0;
        while (i < s.Length)
        {
            if (i + 9 <= s.Length && s[i] == '<' && s[i + 1] == 'c'
                && uint.TryParse(s.AsSpan(i + 2, 6), System.Globalization.NumberStyles.HexNumber, null, out uint rgb)
                && s[i + 8] == '>')
            { Flush(); color = rgb; i += 9; }
            else if (i + 4 <= s.Length && s[i] == '<' && s[i + 1] == '/' && s[i + 2] == 'c' && s[i + 3] == '>')
            { Flush(); color = 0xFFFFFF; i += 4; }
            else { seg.Append(s[i]); i++; }
        }
        Flush();
    }

    // ---- draw ---------------------------------------------------------------------------------

    /// <summary>Hot: draw all published frames (one per chat window) through the owned GL3 program.
    /// Applies the fade/slide/scroll per frame; the slide residual only moves the focused window's
    /// body (<see cref="RenderFrame.ApplyResidual"/>). One GL state save/restore wraps all windows; the VBO
    /// is re-specified per window (N<=4, tiny buffers — negligible bus cost). Zero-alloc in every path.</summary>
    public static void Draw()
    {
        Frame[] frames = _frames;
        if (frames.Length == 0 || _atlas == null || _log == null) return;
        if (!Gl.Init(_log)) return;

        float focusTau = 70f;
        for (int i = 0; i < frames.Length; i++)
            if (frames[i].Rf.ApplyResidual) { focusTau = frames[i].Rf.TauMs; break; }
        float globalResidual = ChatAnim.Tick(System.Environment.TickCount64, focusTau);
        System.DateTime now = System.DateTime.UtcNow;

        // no state snapshot. UiHostService resyncs the engine on hand-back; everything below sets the
        // full preamble it needs, so nothing here is inherited from another window either.
        Gl.ActiveTexture(Gl.TEXTURE0);

        if (TextRig.EnsureProgram())
        {
            if (!TextRig._texUp) TextRig.UploadTexture();
            if (!TextRig._vaoUp) TextRig.SetupVao();

            Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);

            float* m = stackalloc float[16];
            TextRig.Ortho(m, TextRig.ViewportW, TextRig.ViewportH);

            for (int i = 0; i < frames.Length; i++)
            {
                Frame f = frames[i];
                RenderFrame rf = f.Rf;
                if (rf.Count == 0) continue;

                float residual = rf.ApplyResidual ? globalResidual : 0f;
                System.DateTime ls = rf.LastScroll;
                bool stable = IsStable(rf, residual, now, ls, out int opaque);
                int uploadLen = rf.Verts.Length;
                if (stable && f.Settled && opaque == f.OpaqueCount)
                {
                    // ponytail: settled — Scratch has correct data from the last ApplyAnim.
                    // Single-frame: VBO still holds it; multi-frame: re-upload (shared VBO).
                    if (frames.Length > 1) TextRig.UploadBuffer(f.Scratch, uploadLen);
                }
                else if (stable)
                {
                    ApplyAnim(f, residual, now, ls);
                    TextRig.UploadBuffer(f.Scratch, uploadLen);
                    f.Settled = true;
                    f.OpaqueCount = opaque;
                }
                else
                {
                    f.Settled = false;
                    ApplyAnim(f, residual, now, ls);
                    TextRig.UploadBuffer(f.Scratch, uploadLen);
                }
                _drawCount = rf.Count;

                if (!_diagged)
                {
                    Gl.DrainErrors();
                    _diagged = true;
                    DrawFrame(f, m, residual, now, ls);
                    _log?.Info($"TextRig: draw verts={_drawCount} viewport={TextRig.ViewportW}x{TextRig.ViewportH} glErr=0x{Gl.GetError():x}");
                }
                else DrawFrame(f, m, residual, now, ls);
            }

        }
    }

    /// <summary>True when the frame's animation output is stable: residual is 0, no blink, and every span's
    /// fade alpha is exactly 0 or 1 (not mid-transition). A stable frame's Scratch won't change across draws,
    /// so ApplyAnim can be skipped. <paramref name="opaqueCount"/> tracks how many spans are at alpha 1 — a
    /// scroll resets the per-window <c>ls</c> and flips faded spans back to opaque, changing this count.</summary>
    private static bool IsStable(RenderFrame rf, float residual, System.DateTime now, System.DateTime ls, out int opaqueCount)
    {
        opaqueCount = 0;
        if (residual != 0f) return false;
        if (rf.BlinkEnd > rf.BlinkStart) return false;
        if (rf.HoverEnd > rf.HoverStart && RenderFrame.HoverT(now, rf.HoverSince) < 1f) return false;
        if (rf.LeftEnd > rf.LeftStart && RenderFrame.HoverT(now, rf.LeftSince) < 1f) return false;
        if (rf.Scrolled || !rf.AnimEnabled) { opaqueCount = rf.Spans.Count; return true; }
        List<LineSpanVerts> spans = rf.Spans;
        for (int i = 0; i < spans.Count; i++)
        {
            float a = ChatAnim.FadeAlpha(now, spans[i].Ts, ls, true, rf.HoldSec, rf.FadeSec);
            if (a > 0f && a < 1f) return false;
            if (a >= 1f) opaqueCount++;
        }
        return true;
    }

    /// <summary>Copy the base verts into scratch, then per body line add the shared slide/scroll residual
    /// to y and multiply the fade alpha in. Zero-alloc: scratch is owned by the frame.</summary>
    private static void ApplyAnim(Frame f, float residual, System.DateTime now, System.DateTime ls)
    {
        float[] src = f.Rf.Verts, dst = f.Scratch;
        System.Array.Copy(src, dst, src.Length);
        List<LineSpanVerts> spans = f.Rf.Spans;
        for (int i = 0; i < spans.Count; i++)
        {
            LineSpanVerts s = spans[i];
            float a = f.Rf.Scrolled ? 1f : ChatAnim.FadeAlpha(now, s.Ts, ls, f.Rf.AnimEnabled, f.Rf.HoldSec, f.Rf.FadeSec);
            int st = s.Start * Floats, en = s.End * Floats;
            for (int v = st; v < en; v += Floats) { dst[v + 1] += residual; dst[v + 7] *= a; }
        }
        // Blink: modulate unread-tab border alpha (500ms period, on 300ms / dim 200ms).
        if (f.Rf.BlinkEnd > f.Rf.BlinkStart)
        {
            float ba = (System.Environment.TickCount64 % 500) < 300 ? 0.85f : 0.20f;
            int bs = f.Rf.BlinkStart * Floats, be = f.Rf.BlinkEnd * Floats;
            for (int v = bs; v < be; v += Floats) dst[v + 7] *= ba;
        }
        // Tab hover: the entered tab's plate eases in, the left tab's eases out.
        if (f.Rf.HoverEnd > f.Rf.HoverStart)
        {
            float ha = RenderFrame.HoverT(now, f.Rf.HoverSince);
            for (int v = f.Rf.HoverStart * Floats, en = f.Rf.HoverEnd * Floats; v < en; v += Floats) dst[v + 7] *= ha;
        }
        if (f.Rf.LeftEnd > f.Rf.LeftStart)
        {
            float la = 1f - RenderFrame.HoverT(now, f.Rf.LeftSince);
            for (int v = f.Rf.LeftStart * Floats, en = f.Rf.LeftEnd * Floats; v < en; v += Floats) dst[v + 7] *= la;
        }
    }

    /// <summary>Draw the frame in three slices: chrome above the body, the animated body under a body-rect
    /// scissor (with the portrait pass), then chrome below. The scissor flip reads the frame's published
    /// viewport height (<see cref="TextRig.ViewportH"/>).</summary>
    private static void DrawFrame(Frame f, float* proj, float residual, System.DateTime now, System.DateTime ls)
    {
        RenderFrame rf = f.Rf;
        DrawArt(rf, 0, rf.SkinOver);
        TextRig.BindText(proj);
        if (rf.BodyStart > 0) Gl.DrawArrays(Gl.TRIANGLES, 0, rf.BodyStart);

        int bodyVerts = rf.BodyEnd - rf.BodyStart;
        if (bodyVerts > 0 || rf.Portraits.Count > 0)
        {
            // Scissor to the body rect (GL is bottom-left origin, so flip y against the viewport height).
            int sx = (int)rf.Bx, sy = (int)(TextRig.ViewportH - (rf.By + rf.Bh)), sw = (int)rf.Bw, sh = (int)rf.Bh;
            if (sx < 0) { sw += sx; sx = 0; }
            if (sy < 0) { sh += sy; sy = 0; }
            if (sw < 0) sw = 0;
            if (sh < 0) sh = 0;
            Gl.Scissor(sx, sy, sw, sh);
            Gl.Enable(Gl.SCISSOR_TEST);
            if (bodyVerts > 0) Gl.DrawArrays(Gl.TRIANGLES, rf.BodyStart, bodyVerts);
            DrawPortraits(rf.Portraits, proj, residual, now, ls, rf.Scrolled, rf.AnimEnabled, rf.HoldSec, rf.FadeSec);
            Gl.Disable(Gl.SCISSOR_TEST);
        }

        DrawArt(rf, rf.SkinOver, rf.Skin.Length);
        int tail = _drawCount - rf.BodyEnd;
        if (tail > 0)
        {
            TextRig.BindText(proj);   // the portrait and art passes switched program/VAO/buffer
            Gl.DrawArrays(Gl.TRIANGLES, rf.BodyEnd, tail);
        }
    }

    /// <summary>Replay one slice of a frame's native art through the live skin: one batch per part, one draw
    /// per part. Nothing happens in the flat look, or while the art is refused. Hot, zero-alloc.</summary>
    private static void DrawArt(RenderFrame rf, int from, int to)
    {
        if (from >= to) return;
        Skin? skin = ChatSkin.Begin();
        if (skin == null) return;
        SkinCmd[] cmds = rf.Skin;
        for (int i = from; i < to; i++)
        {
            SkinCmd c = cmds[i];
            skin.Draw(c.Part, c.X0, c.Y0, c.X1, c.Y1, c.Rgba);
        }
        skin.Flush();
    }

    // ---- portrait pass ------------------------------------------------------------------

    /// <summary>At most this many portraits per frame (bounds the stack scratch below). Far above the
    /// rows a chat body can show; a silent cap, so log if we ever hit it.</summary>
    private const int MaxPortraitsPerFrame = 64;

    /// <summary>Second pass: bind each speaker's engine-resident portrait texture and draw it as a quad
    /// through the RGBA program. Cache-hit path is zero-alloc (dict lookup + one virtual TID read); only a
    /// first-seen resref pays an <c>AurTextureGet</c>. A texture whose TID is still 0 (not yet uploaded by
    /// the engine's RefreshTextures pass, or a bad resref) is simply skipped this frame.</summary>
    private static void DrawPortraits(IReadOnlyList<PortraitQuad> quads, float* proj,
                                      float residual, System.DateTime now, System.DateTime ls, bool scrolled,
                                      bool animEnabled, double holdSec, double fadeSec)
    {
        int k = quads.Count;
        if (k == 0 || TextRig._aurGet == null || !CX.Framework.AurTex.Ready) return;
        if (!TextRig.EnsurePortraitProgram()) return;
        if (!TextRig._pvaoUp) TextRig.SetupPortraitVao();
        if (k > MaxPortraitsPerFrame) { _log?.Info($"[chat] portrait cap hit ({k}>{MaxPortraitsPerFrame})"); k = MaxPortraitsPerFrame; }

        // Resolve every TID and build all geometry FIRST. AurTextureGet's create path (first load of a
        // resref) can touch GL and rebind ARRAY_BUFFER, so it must never run between our BindBuffer and
        // BufferData -- keep all resolves ahead of the single upload below. Steady state is a per-frame
        // resman lookup returning a borrowed pointer (verified: no AddRef, no GL on the hit path) + a TID
        // read; zero GC alloc on our side. We never cache the pointer -- see ResolveTid.
        int* tids = stackalloc int[k];
        QuadVerts* verts = stackalloc QuadVerts[k];
        int drawn = 0;
        for (int i = 0; i < k; i++)
        {
            float a = scrolled ? 1f : ChatAnim.FadeAlpha(now, quads[i].Ts, ls, animEnabled, holdSec, fadeSec);
            if (a <= 0f) continue;                                 // fully faded -> skip
            int tid = TextRig.ResolvePortraitTid(quads[i].Resref);
            if (tid == 0) continue;   // not uploaded yet, or unresolvable -> skip this frame
            tids[drawn] = tid;
            TextRig.FillQuad(ref verts[drawn], quads[i], residual, a);   // slide with the body + fade
            drawn++;
        }
        if (drawn == 0) return;

        Gl.UseProgram(TextRig._progRgb);
        Gl.UniformMatrix4fv(TextRig._uProjRgb, 1, (byte)Gl.FALSE, proj);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.Uniform1i(TextRig._uTexRgb, 0);
        Gl.BindVertexArray(TextRig._pvao);
        Gl.BindBuffer(Gl.ARRAY_BUFFER, TextRig._pvbo);
        Gl.BufferData(Gl.ARRAY_BUFFER, (System.IntPtr)(drawn * sizeof(QuadVerts)), verts, Gl.DYNAMIC_DRAW);
        bool samp = TextRig.EnsurePortraitSampler();
        if (samp) Gl.BindSampler(0, TextRig._psamp);
        for (int i = 0; i < drawn; i++)
        {
            Gl.BindTexture(Gl.TEXTURE_2D, (uint)tids[i]);
            Gl.DrawArrays(Gl.TRIANGLES, i * 6, 6);
        }
        // Unbind before the body-tail text draw below re-uses unit 0 for the glyph atlas (a sampler would override its filter state).
        if (samp) Gl.BindSampler(0, 0);
        if (!_portraitDrew) { _portraitDrew = true; _log?.Info($"[chat] portrait first-draw tid={tids[0]} ({drawn} shown)"); }
    }

    /// <summary>Cosmetic reset at a session (module) boundary: re-arm the one-shot first-portrait log for the
    /// next session. Nothing engine-owned survives a boundary to drop -- the portrait pointer is never cached
    /// (see ResolveTid). Does NOT touch GL objects that outlive a session (atlas, programs, VAOs/VBOs). Cold,
    /// zero-alloc.</summary>
    public static void ClearSessionState()
    {
        _portraitDrew = false;   // let the next session log its first live portrait again
    }

    // ---- attribution overlay (main menu only) -------------------------------------------------

    private static float[]? _attrVerts;
    private static int _attrCount;
    private static int _attrEpoch;   // atlas epoch the cached _attrVerts UVs were baked against

    /// <summary>Draw the attribution text in the top-left corner (main menu only). Lazily builds and caches
    /// the vertex data; shares the text shader program, VAO, VBO, and atlas texture with <see cref="Draw"/>.
    /// Rebuilds the cache when the atlas re-baked (its glyph UVs moved). Called from the SwapBuffers hook
    /// when not in-game.</summary>
    public static void DrawOverlay()
    {
        if (_atlas == null || _log == null) return;
        if (!Gl.Init(_log)) return;
        if (_attrVerts == null || _attrEpoch != TextRig.AtlasEpoch) { BuildAttribution(); _attrEpoch = TextRig.AtlasEpoch; }
        if (_attrCount == 0) return;

        // no state snapshot. UiHostService resyncs the engine on hand-back; everything below sets the
        // full preamble it needs, so nothing here is inherited from another window either.
        Gl.ActiveTexture(Gl.TEXTURE0);

        if (TextRig.EnsureProgram())
        {
            if (!TextRig._texUp) TextRig.UploadTexture();
            if (!TextRig._vaoUp) TextRig.SetupVao();

            Gl.Disable(Gl.DEPTH_TEST); Gl.Disable(Gl.CULL_FACE); Gl.Disable(Gl.SCISSOR_TEST);
            Gl.Enable(Gl.BLEND); Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);

            float* m = stackalloc float[16];
            TextRig.Ortho(m, TextRig.ViewportW, TextRig.ViewportH);

            TextRig.BindText(m);
            Gl.BindBuffer(Gl.ARRAY_BUFFER, TextRig._vbo);
            fixed (float* p = _attrVerts)
                Gl.BufferData(Gl.ARRAY_BUFFER, (System.IntPtr)(_attrVerts.Length * sizeof(float)), p, Gl.DYNAMIC_DRAW);
            Gl.DrawArrays(Gl.TRIANGLES, 0, _attrCount);

            // Overlay re-specifies the shared VBO; invalidate the settled-frame cache so Draw()
            // re-uploads on the next in-game frame instead of reading stale attribution data.
            Frame[] frames = _frames;
            for (int i = 0; i < frames.Length; i++) frames[i].Settled = false;

        }
    }

    private static void BuildAttribution()
    {
        GlyphAtlas atlas = _atlas!;
        string version = typeof(ChatRenderer).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev";
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version.Substring(0, plus);

        string prefix = $"NWN:EE Client Extender {version}. ";
        const string italic = "Wonderbringer, let it turn true.";

        var batch = new VertexBatch(atlas.SolidU, atlas.SolidV);
        float penX = 10f, baseline = atlas.LineHeight + 2f;
        const float a = 0.7f;

        foreach (Rune r in prefix.EnumerateRunes())
        {
            if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
            batch.Glyph(g, penX, baseline, 1f, 1f, 1f, a);
            penX += g.Advance;
        }
        // ponytail: gold = (1, 0.82, 0); synthetic italic via vertex shear
        foreach (Rune r in italic.EnumerateRunes())
        {
            if (!atlas.TryGet(r.Value, out Glyph g)) { penX += atlas.AdvanceOf(r); continue; }
            batch.GlyphItalic(g, penX, baseline, 1f, 0.82f, 0f, a);
            penX += g.Advance;
        }

        _attrVerts = batch.ToArray();
        _attrCount = batch.VertexCount;
    }
}
