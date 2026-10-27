using System;
using System.Numerics;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>The glyph geometry of a text object, built ONCE per change rather than per frame: a pure function of
/// (bytes, visible length, font, atlas epoch, width, scale, colours), drawn as a span. This is
/// <c>CAurFont::TextOut</c> @0x496220 with <c>StringGob::Render</c> @0x4933a0's translate chain folded in: one list
/// for the whole document in local space, 6 vertices a glyph of (x, y, z, u, v) -- stream format 0x804.</summary>
public static class TextQuads
{
    /// <summary>Lists issued while still stale -- an ordering bug in the caller, never a silent hole.</summary>
    public static long Stale;
    private static int _saidStale;
    private static CX.Framework.ExtenderLog? _log;

    /// <summary>Inject the log so a refusal is loud.</summary>
    public static void Install(CX.Framework.ExtenderLog log)
    {
        _log = log;
    }

    /// <summary>Floats a vertex: x, y, z, u, v -- the engine's 20-byte stream vertex.</summary>
    public const int VertFloats = 5;
    /// <summary>Vertices a glyph: two triangles, not indexed, as the engine emits them.</summary>
    public const int VertsPerGlyph = 6;

    /// <summary>Local z the glyphs sit at: <c>Render</c>'s <c>Translate(0, 0, 0.001)</c> around each
    /// <c>TextOut</c>, which the caret's own bracket re-uses rather than stacks.</summary>
    private const float GlyphZ = 0.001f;
    /// <summary>Local z of a selection box: under the glyphs, above the text object's own plane.</summary>
    private const float SelZ = GlyphZ * 0.5f;
    /// <summary>The selection highlight's rgba, drawn untextured under the selected glyphs.</summary>
    private const float SelR = 0.25f, SelG = 0.45f, SelB = 0.85f, SelA = 0.55f;
    /// <summary>Selection boxes of the build in progress (x0, x1, y0, y1); game thread only.</summary>
    private static float[] _sel = new float[16];

    /// <summary>Rebuild <paramref name="d"/>'s quad list if it is stale, and return whether the list is usable.
    /// Called from the game thread on a change, never from the frame.</summary>
    public static bool Build(TextDoc d)
    {
        TextFont? f = d.Font;
        // The cache-hit test reads the PUBLISHED list (through d), not the one about to be filled.
        if (!d.QuadsStale && f != null && d.QuadEpoch == f.Epoch && d.QuadGrid == TextObjects.GridGen)
        { TextObjects.CacheHits++; return d.QuadVerts > 0; }
        // CX-120: a build fills the slot that is NOT published and publishes it whole, so the issuer never
        // walks a list under rewrite. Every exit below publishes, including the ones that build nothing.
        QuadList q = d.NextList();
        if (f == null) { q.QuadVerts = 0; q.RunCount = 0; q.CaretRun = -1; q.SelRun = -1; q.AttachedCount = 0; d.PublishQuads(q); return false; }
        // A double-byte font draws nothing in the engine (TextOutDoubleByte is a stub); refuse the same way
        // rather than invent glyphs for it.
        if (f.DoubleByte)
        {
            q.QuadVerts = 0; q.RunCount = 0; q.CaretRun = -1; q.SelRun = -1; q.AttachedCount = 0;
            d.QuadsStale = false; d.QuadEpoch = f.Epoch; d.QuadGrid = TextObjects.GridGen;
            d.PublishQuads(q);
            return false;
        }

        int glyphBudget = 0;
        int last = Math.Min(d.TopLine + d.VisibleLines, d.LineCount);
        for (int i = Math.Max(d.TopLine, 0); i < last; i++) glyphBudget += d.Lines[i].VisibleLength;
        glyphBudget += 2;                                    // the caret, on a visible line or one past the last
        glyphBudget += Math.Max(last - Math.Max(d.TopLine, 0), 0);   // a selection box a visible line
        int need = glyphBudget * VertsPerGlyph * VertFloats;
        if (q.Quads.Length < need) q.Quads = new float[Math.Max(need, 1024)];
        if (q.Runs.Length < glyphBudget + 8) q.Runs = new (int, int, float, float, float, float)[glyphBudget + 8];

        float yTop = f.BaselineHeight + f.SpacingB;
        float yBottom = f.BaselineHeight - f.FontHeight + f.SpacingB;

        // Render's pre-loop chain: the vertical alignment within maxHeight, the sub-line scroll, then the
        // baseline shift. The 0.001 is the engine's own epsilon and is kept so a pixel-snapped diff matches.
        float oy = -(d.MaxHeight - d.VisibleHeight) * d.AlignV + d.SubLineScroll + 0.001f - f.SpacingB;

        var (r, g, b, a) = TextObjects.ColourOf(d, 0);
        int prevColour = 0;
        int v = 0, runs = 0, runFirst = 0;
        float accrued = f.SpacingB;
        q.CaretRun = -1;
        q.SelRun = -1;
        q.AttachedCount = 0;
        int selFrom = 0, selTo = 0, selN = 0, lineAt = 0;
        if (d.CaretVisible && d.SelAnchor >= 0)
        {
            int caret = EditSelect.CaretAt(d);
            selFrom = Math.Min(d.SelAnchor, caret);
            selTo = Math.Max(d.SelAnchor, caret);
            for (int i = 0; i < Math.Max(d.TopLine, 0) && i < d.LineCount; i++) lineAt += d.Lines[i].Length;
        }

        for (int li = Math.Max(d.TopLine, 0); li < last; li++)
        {
            TextLine l = d.Lines[li];
            float ox = TextObjects.AlignToPixel((d.MaxWidth - l.Width) * d.AlignH + 0.0001f);
            oy -= l.Ascent;
            if (!l.Model.IsNull)
            {
                if (q.AttachedCount == q.Attached.Length) Array.Resize(ref q.Attached, q.AttachedCount * 2 + 2);
                q.Attached[q.AttachedCount++] = (l.Model, ox, oy);
            }

            if (l.Colour != prevColour) (r, g, b, a) = TextObjects.ColourOf(d, l.Colour);

            ReadOnlySpan<byte> s = l.Span;
            int n = Math.Min(l.VisibleLength, s.Length);
            float pen = 0f;
            for (int i = 0; i < n; i++)
            {
                byte c = s[i];
                if (c == (byte)'<')
                {
                    float pr = r, pg = g, pb = b, pa = a;
                    int k = TextMarkup.Process(s, i, ref r, ref g, ref b, ref a);
                    if (k > 1)
                    {
                        // The engine commits the batch BEFORE SetColor4f: this run keeps the previous colour.
                        if (v > runFirst) { q.Runs[runs++] = (runFirst / VertFloats, (v - runFirst) / VertFloats, pr, pg, pb, pa); runFirst = v; }
                        i += k - 1;
                        continue;
                    }
                }
                float u0 = f.UlU[c], v0 = f.UlV[c], u1 = f.LrU[c], v1 = f.LrV[c];
                float tw = f.TextureWidth;
                float xL, xR, next;
                if (f.Ttf)
                {
                    xL = pen + f.UlZ[c] * tw;
                    xR = (u1 - u0) * tw + xL;
                    next = pen + f.LrZ[c] * tw;
                }
                else
                {
                    xL = pen;
                    xR = (u1 - u0) * tw + pen;
                    next = xR;
                }
                v = Quad(q.Quads, v, ox + xL, ox + xR, oy + yBottom + f.BotTrim[c], oy + yTop - f.TopTrim[c],
                         u0, v0, u1, v1);
                pen = TextObjects.AlignToPixel(next + f.SpacingR);
            }
            if (v > runFirst) { q.Runs[runs++] = (runFirst / VertFloats, (v - runFirst) / VertFloats, r, g, b, a); runFirst = v; }

            int c0 = Math.Clamp(selFrom - lineAt, 0, l.Length), c1 = Math.Clamp(selTo - lineAt, 0, l.Length);
            if (c1 > c0)
            {
                if (_sel.Length < (selN + 1) * 4) Array.Resize(ref _sel, _sel.Length * 2);
                _sel[selN * 4] = ox + TextObjects.PenAt(d, li, c0);
                _sel[selN * 4 + 1] = ox + TextObjects.PenAt(d, li, c1);
                _sel[selN * 4 + 2] = oy + yBottom;
                _sel[selN * 4 + 3] = oy + yTop;
                selN++;
            }
            lineAt += l.Length;

            if (d.CaretVisible && d.Caret.Line == li)
            {
                byte c = d.Caret.Glyph;
                float tw = f.TextureWidth;
                float cx = ox + f.CaretIndent + d.Caret.X;
                float xL = f.Ttf ? cx + f.UlZ[c] * tw : cx;
                float xR = (f.LrU[c] - f.UlU[c]) * tw + xL;
                v = Quad(q.Quads, v, xL, xR, oy + yBottom + f.BotTrim[c], oy + yTop - f.TopTrim[c],
                         f.UlU[c], f.UlV[c], f.LrU[c], f.LrV[c]);
                q.CaretRun = runs;
                q.Runs[runs++] = (runFirst / VertFloats, VertsPerGlyph, d.CaretR, d.CaretG, d.CaretB, 1f);
                runFirst = v;
                // Render re-asserts the line's table colour after the caret, which cancels any markup leak.
                (r, g, b, a) = TextObjects.ColourOf(d, l.Colour);
            }

            prevColour = l.Colour;
            oy -= l.Height - l.Ascent;
            accrued += l.Height;
        }

        // Render's post-loop block: a caret one line PAST the last is still drawn when another line box fits
        // inside maxHeight. This is the empty chat input's caret.
        if (d.CaretVisible && d.Caret.Line == last && accrued + f.FontHeight <= d.MaxHeight)
        {
            byte c = d.Caret.Glyph;
            float tw = f.TextureWidth;
            float cx = f.CaretIndent + d.Caret.X;
            float xL = f.Ttf ? cx + f.UlZ[c] * tw : cx;
            float xR = (f.LrU[c] - f.UlU[c]) * tw + xL;
            float y = oy - f.BaselineHeight;
            v = Quad(q.Quads, v, xL, xR, y + yBottom + f.BotTrim[c], y + yTop - f.TopTrim[c],
                     f.UlU[c], f.UlV[c], f.LrU[c], f.LrV[c]);
            q.CaretRun = runs;
            q.Runs[runs++] = (runFirst / VertFloats, VertsPerGlyph, d.CaretR, d.CaretG, d.CaretB, 1f);
            runFirst = v;
        }

        if (selN > 0)
        {
            for (int i = 0; i < selN; i++)
            {
                v = Quad(q.Quads, v, _sel[i * 4], _sel[i * 4 + 1], _sel[i * 4 + 2], _sel[i * 4 + 3], 0f, 0f, 0f, 0f, SelZ);
            }
            // Drawn first, under the glyphs: the boxes' run moves to the front and the caret's index follows.
            Array.Copy(q.Runs, 0, q.Runs, 1, runs);
            q.Runs[0] = (runFirst / VertFloats, selN * VertsPerGlyph, SelR, SelG, SelB, SelA);
            runs++;
            q.SelRun = 0;
            if (q.CaretRun >= 0) q.CaretRun++;
            runFirst = v;
        }

        q.QuadVerts = v / VertFloats;
        q.RunCount = runs;
        d.PublishQuads(q);
        d.QuadsStale = false;
        d.QuadEpoch = f.Epoch;
        d.QuadGrid = TextObjects.GridGen;
        d.Builds++;
        TextObjects.Builds++;
        return q.QuadVerts > 0;
    }

    /// <summary>Issue a built list into the open GUI layer: one draw a colour run, the transform composed from
    /// the text object's own place in the database rather than read off <c>m_aurMtxStack</c>. The caret run's
    /// alpha is overridden here because its blink is the one thing in a cached list that moves between frames.
    /// Hot: no allocation, no layout, no markup parsing -- a span and a loop.</summary>
    public static unsafe int Issue(TextDoc d, in Matrix4x4 mvp, uint atlas, float caretAlpha,
                                   bool depthTest, bool depthWrite)
    {
        if (d.QuadsStale)
        {
            Stale++;
            if (_saidStale++ == 0)
                _log?.Always("TextQuads: a text object was issued with a STALE quad list and is NOT DRAWN (totals on /alloy)");
            return 0;
        }
        // CX-120: ONE reference read, and every term below comes off it. Reading d.Quads, d.RunCount and
        // d.Runs separately let a rebuild land between them -- a run index from the new list against the old
        // array, through unchecked pointer arithmetic.
        QuadList? list = d.PublishedQuads;
        if (list == null || list.QuadVerts == 0 || list.RunCount == 0) return 0;
        int issued = 0;
        for (int i = 0; i < list.RunCount; i++)
            {
                var run = list.Runs[i];
                if (run.Count <= 0) continue;
                var rgba = new Vector4(run.R, run.G, run.B, i == list.CaretRun ? caretAlpha : run.A);
                fixed (float* q = list.Quads.AsSpan(checked((int)((long)run.First * VertFloats))))
                    GuiCapture.TextRun(q, run.Count, Gl.TRIANGLES, mvp, i == list.SelRun ? 0 : (int)atlas,
                                       rgba, Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA, depthTest, depthWrite);
                issued++;
            }
        return issued;
    }

    /// <summary>One glyph: the engine's six vertices in its order and winding
    /// (bottom-left, bottom-right, top-left, top-left, bottom-right, top-right), with v flipped as the engine
    /// pairs <c>ul.v</c> with the quad's TOP.</summary>
    private static int Quad(float[] q, int at, float xL, float xR, float yB, float yT,
                            float u0, float v0, float u1, float v1, float z = GlyphZ)
    {
        Put(q, ref at, xL, yB, u0, v1, z);
        Put(q, ref at, xR, yB, u1, v1, z);
        Put(q, ref at, xL, yT, u0, v0, z);
        Put(q, ref at, xL, yT, u0, v0, z);
        Put(q, ref at, xR, yB, u1, v1, z);
        Put(q, ref at, xR, yT, u1, v0, z);
        return at;
    }

    private static void Put(float[] q, ref int at, float x, float y, float u, float v, float z)
    {
        q[at] = x; q[at + 1] = y; q[at + 2] = z; q[at + 3] = u; q[at + 4] = v;
        at += VertFloats;
    }
}
