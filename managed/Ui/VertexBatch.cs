using System;
using System.Collections.Generic;

namespace CX.Ui;

/// <summary>
/// Accumulates interleaved 8-float vertices (x,y,u,v,r,g,b,a) for the owned GL text/UI pass. One primitive set
/// feeds a whole window: <see cref="Rect"/> for chrome (it samples a solid white atlas texel, so alpha comes
/// straight from the vertex) and <see cref="Glyph"/> for text. Built cold on the ingest / input / resize thread;
/// the hot draw only uploads. <see cref="Reset"/> clears and re-points the solid UV, keeping the list's capacity.
/// </summary>
public sealed class VertexBatch
{
    public const int Floats = 8;
    private const int VertsPerQuad = 6;   // two triangles; GL_QUADS is gone in core profile

    private readonly List<float> _v = new(1024);
    /// <summary>The open run's halo and body quads while <see cref="Outline"/> is on, appended halos-first by <see cref="Flush"/>.</summary>
    private readonly List<float> _halo = new(), _body = new();
    private float _su, _sv;   // solid (white) texel centre -- rects sample this for flat colour
    private float _clipY0 = float.NegativeInfinity, _clipY1 = float.PositiveInfinity;

    public VertexBatch(float solidU, float solidV) { _su = solidU; _sv = solidV; }

    /// <summary>Halo radius in px (0 = none, <see cref="TextStyle.HaloPx"/>) for every following <see cref="Glyph"/> /
    /// <see cref="GlyphItalic"/>: black rings (<see cref="TextStyle.HaloOffsets"/>) at the glyph's alpha. A run -- the
    /// glyphs between two other primitives or reads -- lays all its halos before all its bodies, so a neighbour's halo
    /// never paints over a glyph of the same run. At 0 nothing is held back and the order is the emit order.</summary>
    public float Outline;

    /// <summary>Clear the batch, reconfigure the solid UV and drop the outline. The internal list keeps its capacity.</summary>
    public void Reset(float solidU, float solidV) { Clear(); Outline = 0f; _su = solidU; _sv = solidV; }
    public void Clear() { _v.Clear(); _halo.Clear(); _body.Clear(); }

    /// <summary>Vertical clip band for every following <see cref="Rect"/> / <see cref="Glyph"/>: quads are cut
    /// at the band edges with their texcoords re-interpolated, so a scroll column's partially visible top and
    /// bottom rows end exactly at its box. <see cref="GradientRect"/> and <see cref="GlyphItalic"/> ignore it.</summary>
    public void ClipY(float y0, float y1) { _clipY0 = y0; _clipY1 = y1; }
    public void ClearClip() { _clipY0 = float.NegativeInfinity; _clipY1 = float.PositiveInfinity; }
    /// <summary>Vertices so far. Like every read, it closes an open outlined run first.</summary>
    public int VertexCount { get { Flush(); return _v.Count / Floats; } }
    public float[] ToArray() { Flush(); return _v.ToArray(); }
    /// <summary>The accumulated floats without a copy (an open outlined run is closed first), for a per-frame owner that
    /// uploads straight from the batch (the settings panel). Valid until the next emit.</summary>
    public ReadOnlySpan<float> AsSpan() { Flush(); return System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_v); }

    /// <summary>Close the open outlined run: its halos, then its bodies.</summary>
    private void Flush()
    {
        if (_body.Count == 0 && _halo.Count == 0) return;
        _v.AddRange(_halo); _v.AddRange(_body);
        _halo.Clear(); _body.Clear();
    }

    /// <summary>Solid-colour rect (chrome): both texcoords pin to the white texel so <c>tex.r==1</c>.</summary>
    public void Rect(float x0, float y0, float x1, float y1, float r, float g, float b, float a)
    {
        Flush();
        Quad(_v, x0, y0, x1, y1, _su, _sv, _su, _sv, r, g, b, a);
    }

    /// <summary>Per-corner coloured rect: the GPU interpolates between the four corners. Corners are
    /// top-left, top-right, bottom-right, bottom-left (matching the winding order of <see cref="Rect"/>).</summary>
    public void GradientRect(float x0, float y0, float x1, float y1,
        (float r, float g, float b, float a) tl, (float r, float g, float b, float a) tr,
        (float r, float g, float b, float a) br, (float r, float g, float b, float a) bl)
    {
        Flush();
        V(x0,y0, _su,_sv, tl.r,tl.g,tl.b,tl.a); V(x1,y0, _su,_sv, tr.r,tr.g,tr.b,tr.a); V(x1,y1, _su,_sv, br.r,br.g,br.b,br.a);
        V(x0,y0, _su,_sv, tl.r,tl.g,tl.b,tl.a); V(x1,y1, _su,_sv, br.r,br.g,br.b,br.a); V(x0,y1, _su,_sv, bl.r,bl.g,bl.b,bl.a);
    }

    /// <summary>One baked glyph at pen <paramref name="penX"/>/<paramref name="baseline"/> (empty glyphs skipped), its box
    /// stretched by <paramref name="k"/> when a face of the wanted size is not baked.</summary>
    public void Glyph(in Glyph gl, float penX, float baseline, float r, float g, float b, float a, float k = 1f)
    {
        if (gl.W <= 0) return;
        float x0 = penX + gl.XOff * k, y0 = baseline + gl.YOff * k, x1 = x0 + gl.W * k, y1 = y0 + gl.H * k;
        if (Outline <= 0f) { Flush(); Quad(_v, x0, y0, x1, y1, gl.U0, gl.V0, gl.U1, gl.V1, r, g, b, a); return; }
        Span<float> o = TextStyle.HaloOffsets(Outline, stackalloc float[TextStyle.MaxHaloFloats]);
        for (int i = 0; i < o.Length; i += 2)
            Quad(_halo, x0 + o[i], y0 + o[i + 1], x1 + o[i], y1 + o[i + 1], gl.U0, gl.V0, gl.U1, gl.V1, 0f, 0f, 0f, a);
        Quad(_body, x0, y0, x1, y1, gl.U0, gl.V0, gl.U1, gl.V1, r, g, b, a);
    }

    /// <summary>Synthetic italic: shear the top edge of the glyph quad rightward.</summary>
    public void GlyphItalic(in Glyph gl, float penX, float baseline, float r, float g, float b, float a,
                            float shear = 0.2f, float k = 1f)
    {
        if (gl.W <= 0) return;
        float x0 = penX + gl.XOff * k, y0 = baseline + gl.YOff * k, x1 = x0 + gl.W * k, y1 = y0 + gl.H * k;
        float dx = gl.H * k * shear;
        if (Outline <= 0f) { Flush(); Sheared(_v, gl, x0, y0, x1, y1, dx, r, g, b, a); return; }
        Span<float> o = TextStyle.HaloOffsets(Outline, stackalloc float[TextStyle.MaxHaloFloats]);
        for (int i = 0; i < o.Length; i += 2)
            Sheared(_halo, gl, x0 + o[i], y0 + o[i + 1], x1 + o[i], y1 + o[i + 1], dx, 0f, 0f, 0f, a);
        Sheared(_body, gl, x0, y0, x1, y1, dx, r, g, b, a);
    }

    private static void Sheared(List<float> to, in Glyph gl, float x0, float y0, float x1, float y1, float dx,
                                float r, float g, float b, float a)
    {
        V(to, x0+dx,y0, gl.U0,gl.V0, r,g,b,a); V(to, x1+dx,y0, gl.U1,gl.V0, r,g,b,a); V(to, x1,y1, gl.U1,gl.V1, r,g,b,a);
        V(to, x0+dx,y0, gl.U0,gl.V0, r,g,b,a); V(to, x1,y1, gl.U1,gl.V1, r,g,b,a);    V(to, x0,y1, gl.U0,gl.V1, r,g,b,a);
    }

    /// <summary>One baked glyph as a sprite: its box centred on (<paramref name="cx"/>, <paramref name="cy"/>)
    /// and rotated by <paramref name="angle"/> radians (clockwise on the y-down screen). Ignores the clip band.</summary>
    public void GlyphSprite(in Glyph gl, float cx, float cy, float angle, float r, float g, float b, float a)
    {
        if (gl.W <= 0) return;
        Flush();
        float hw = gl.W * 0.5f, hh = gl.H * 0.5f;
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        float tlx = cx - hw * c + hh * s, tly = cy - hw * s - hh * c;
        float trx = cx + hw * c + hh * s, try_ = cy + hw * s - hh * c;
        float brx = cx + hw * c - hh * s, bry = cy + hw * s + hh * c;
        float blx = cx - hw * c - hh * s, bly = cy - hw * s + hh * c;
        V(tlx,tly, gl.U0,gl.V0, r,g,b,a); V(trx,try_, gl.U1,gl.V0, r,g,b,a); V(brx,bry, gl.U1,gl.V1, r,g,b,a);
        V(tlx,tly, gl.U0,gl.V0, r,g,b,a); V(brx,bry, gl.U1,gl.V1, r,g,b,a);  V(blx,bly, gl.U0,gl.V1, r,g,b,a);
    }

    /// <summary>A textured quad with explicit UVs (a skin batch; honours the clip band).</summary>
    public void Tex(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1,
                    float r, float g, float b, float a)
    {
        Flush();
        Quad(_v, x0, y0, x1, y1, u0, v0, u1, v1, r, g, b, a);
    }

    /// <summary>Nine-slice: the texture's <paramref name="l"/>/<paramref name="t"/>/<paramref name="r"/>/
    /// <paramref name="bt"/> texel border keeps its size × <paramref name="scale"/> on screen, the edges stretch
    /// along one axis, the centre along both. Engine textures are bottom-up (v = 1 is the top row). A rect narrower
    /// than its borders shrinks them proportionally; <paramref name="tw"/>/<paramref name="th"/> are its texel size.</summary>
    public void NineSlice(float x0, float y0, float x1, float y1, int tw, int th, int l, int t, int r, int bt,
                          float scale, float cr, float cg, float cb, float ca)
    {
        if (tw <= 0 || th <= 0 || x1 <= x0 || y1 <= y0) return;
        Flush();
        float sl = l * scale, sr = r * scale, st = t * scale, sb = bt * scale;
        float kx = sl + sr > x1 - x0 ? (x1 - x0) / (sl + sr) : 1f, ky = st + sb > y1 - y0 ? (y1 - y0) / (st + sb) : 1f;
        sl *= kx; sr *= kx; st *= ky; sb *= ky;
        float xa = x0 + sl, xb = x1 - sr, ya = y0 + st, yb = y1 - sb;
        float ua = (float)l / tw, ub = 1f - (float)r / tw;
        float vTop = 1f, va = 1f - (float)t / th, vb = (float)bt / th, vBot = 0f;
        Span<float> xs = stackalloc float[] { x0, xa, xb, x1 };
        Span<float> us = stackalloc float[] { 0f, ua, ub, 1f };
        Span<float> ys = stackalloc float[] { y0, ya, yb, y1 };
        Span<float> vs = stackalloc float[] { vTop, va, vb, vBot };
        for (int j = 0; j < 3; j++)
            for (int i = 0; i < 3; i++)
                if (xs[i + 1] > xs[i] && ys[j + 1] > ys[j])
                    Quad(_v, xs[i], ys[j], xs[i + 1], ys[j + 1], us[i], vs[j], us[i + 1], vs[j + 1], cr, cg, cb, ca);
    }

    private void Quad(List<float> to, float x0, float y0, float x1, float y1,
                      float u0, float v0, float u1, float v1,
                      float r, float g, float b, float a)
    {
        if (y1 > y0)
        {
            if (y1 <= _clipY0 || y0 >= _clipY1) return;
            if (y0 < _clipY0) { v0 += (v1 - v0) * (_clipY0 - y0) / (y1 - y0); y0 = _clipY0; }
            if (y1 > _clipY1) { v1 -= (v1 - v0) * (y1 - _clipY1) / (y1 - y0); y1 = _clipY1; }
        }
        V(to, x0, y0, u0, v0, r, g, b, a); V(to, x1, y0, u1, v0, r, g, b, a); V(to, x1, y1, u1, v1, r, g, b, a);
        V(to, x0, y0, u0, v0, r, g, b, a); V(to, x1, y1, u1, v1, r, g, b, a); V(to, x0, y1, u0, v1, r, g, b, a);
    }

    private void V(float x, float y, float u, float v, float r, float g, float b, float a) => V(_v, x, y, u, v, r, g, b, a);

    private static void V(List<float> to, float x, float y, float u, float v, float r, float g, float b, float a)
    {
        to.Add(x); to.Add(y); to.Add(u); to.Add(v); to.Add(r); to.Add(g); to.Add(b); to.Add(a);
    }
}
