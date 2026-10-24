using System.Numerics;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

using CX.Services.Chat.Core.Model;

namespace CX.Services.Chat.Render;

/// <summary>
/// Chat-coupled owned-render pass. Owns the per-window <see cref="Frame"/> publish/animation buffering and
/// records the chat windows through the shared <see cref="TextRig"/> primitives. Callers build a batch cold
/// (ingest/input/resize thread) via <see cref="VertexBatch"/>; the hot <see cref="Draw"/> animates and records,
/// zero-alloc.
/// </summary>
public static unsafe class ChatRenderer
{
    private const int Floats = VertexBatch.Floats;   // x,y,u,v,r,g,b,a

    /// <summary>Injected log + baked atlas, both owned by <see cref="TextRig"/> (set in its Init).</summary>
    private static ExtenderLog? _log => TextRig.Log;
    private static GlyphAtlas? _atlas => TextRig.Atlas;

    private static bool _diagged;              // one-shot: log the viewport on the first drawn frame

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

    // ---- draw ---------------------------------------------------------------------------------

    /// <summary>Hot: record all published frames (one per chat window). Applies the fade/slide/scroll per frame;
    /// the slide residual only moves the focused window's body (<see cref="RenderFrame.ApplyResidual"/>), and a
    /// settled frame records last frame's animated vertices as they are. Zero-alloc in every path.</summary>
    public static void Draw()
    {
        Frame[] frames = _frames;
        if (frames.Length == 0 || _atlas == null || _log == null) return;

        float focusTau = 70f;
        for (int i = 0; i < frames.Length; i++)
            if (frames[i].Rf.ApplyResidual) { focusTau = frames[i].Rf.TauMs; break; }
        float globalResidual = ChatAnim.Tick(System.Environment.TickCount64, focusTau);
        System.DateTime now = System.DateTime.UtcNow;

        for (int i = 0; i < frames.Length; i++)
        {
            Frame f = frames[i];
            RenderFrame rf = f.Rf;
            if (rf.Count == 0) continue;

            float residual = rf.ApplyResidual ? globalResidual : 0f;
            System.DateTime ls = rf.LastScroll;
            bool stable = IsStable(rf, residual, now, ls, out int opaque);
            if (!stable || !f.Settled || opaque != f.OpaqueCount) ApplyAnim(f, residual, now, ls);
            f.Settled = stable;
            f.OpaqueCount = opaque;
            DrawFrame(f, residual, now, ls);
            if (!_diagged)
            {
                _diagged = true;
                _log?.Info($"TextRig: chat draw verts={rf.Count} viewport={TextRig.ViewportW}x{TextRig.ViewportH}");
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

    /// <summary>Record the frame in three slices: chrome above the body, the animated body clipped to the body rect
    /// (with the portrait pass), then chrome below.</summary>
    private static void DrawFrame(Frame f, float residual, System.DateTime now, System.DateTime ls)
    {
        RenderFrame rf = f.Rf;
        float[] s = f.Scratch;
        DrawArt(rf, 0, rf.SkinOver);
        if (rf.BodyStart > 0) TextRig.DrawBatch(s, rf.BodyStart);

        int bodyVerts = rf.BodyEnd - rf.BodyStart;
        if (bodyVerts > 0 || rf.Portraits.Count > 0)
        {
            int bx = (int)rf.Bx, by = (int)rf.By, bw = (int)rf.Bw, bh = (int)rf.Bh;
            if (bodyVerts > 0)
                TextRig.DrawBatch(s.AsSpan(rf.BodyStart * Floats), bodyVerts, clip: true, cx: bx, cy: by, cw: bw, ch: bh);
            DrawPortraits(rf.Portraits, residual, now, ls, rf.Scrolled, rf.AnimEnabled, rf.HoldSec, rf.FadeSec, bx, by, bw, bh);
        }

        DrawArt(rf, rf.SkinOver, rf.Skin.Length);
        int tail = rf.Count - rf.BodyEnd;
        if (tail > 0) TextRig.DrawBatch(s.AsSpan(rf.BodyEnd * Floats), tail);
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

    /// <summary>Second pass: each speaker's portrait as a quad over its texture, clipped to the body rect. Only a
    /// first-seen resref pays an <c>AurTextureGet</c>; a texture whose TID is still 0 (not yet uploaded, or a bad
    /// resref) is simply skipped this frame.</summary>
    private static void DrawPortraits(IReadOnlyList<PortraitQuad> quads, float residual, System.DateTime now,
                                      System.DateTime ls, bool scrolled, bool animEnabled, double holdSec, double fadeSec,
                                      int bx, int by, int bw, int bh)
    {
        int k = quads.Count;
        if (k == 0 || TextRig._aurGet == null || !CX.Framework.AurTex.Ready) return;
        QuadVerts v = default;
        for (int i = 0; i < k; i++)
        {
            float a = scrolled ? 1f : ChatAnim.FadeAlpha(now, quads[i].Ts, ls, animEnabled, holdSec, fadeSec);
            if (a <= 0f) continue;
            int tid = TextRig.ResolvePortraitTid(quads[i].Resref);
            if (tid == 0) continue;
            TextRig.FillQuad(ref v, quads[i], residual, a);   // slide with the body + fade
            TextRig.DrawPortraitQuad(tid, in v, true, true, bx, by, bw, bh);
            if (!_portraitDrew) { _portraitDrew = true; _log?.Info($"[chat] portrait first-draw tid={tid}"); }
        }
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

    /// <summary>Record the attribution text in the top-left corner (main menu only). Lazily builds and caches the
    /// vertex data, rebuilt when the atlas re-baked (its glyph UVs moved).</summary>
    public static void DrawOverlay()
    {
        if (_atlas == null || _log == null) return;
        if (_attrVerts == null || _attrEpoch != TextRig.AtlasEpoch) { BuildAttribution(); _attrEpoch = TextRig.AtlasEpoch; }
        if (_attrCount > 0) TextRig.DrawBatch(_attrVerts, _attrCount);
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
