using System.Numerics;
using System;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The layout port, checked against hand arithmetic. Pure logic -- no engine, no GL -- so
/// <c>run-demo.sh</c> runs all of it. Two kinds of case: synthetic metrics chosen so every expected number is
/// exact, and a GOLDEN case on the real shipped metrics of <c>fnt_galahad14</c> whose expected positions are
/// derived by hand from its <c>.txi</c>.</summary>
public static class TextObjectsSelfCheck
{
    private const float Eps = 1e-5f;
    private static bool Near(float a, float b) => MathF.Abs(a - b) <= Eps;

    /// <summary>A monospace synthetic font: every glyph 10 design px wide, a 20 px line box with a 16 px ascent,
    /// and <c>texturewidth 1</c> so a u-span IS a GUI measurement.</summary>
    private static TextFont Synthetic(float advance = 0.10f, float spacingR = 0f)
    {
        var f = new TextFont
        {
            Name = "cx-selfcheck-mono", NumChars = 256,
            FontHeight = 0.20f, BaselineHeight = 0.16f, TextureWidth = 1f,
            SpacingR = spacingR, SpacingB = 0f, CaretIndent = 0f, Ttf = false, Epoch = 1,
        };
        for (int i = 0; i < 256; i++) { f.UlU[i] = 0f; f.LrU[i] = advance; f.UlV[i] = 1f; f.LrV[i] = 0f; }
        return f;
    }

    /// <summary>The real shipped <c>fnt_galahad14.txi</c> metrics, with glyph 32's real coordinates. Advance is
    /// <c>(0.03125 - 0) * 2.56 = 0.08</c> GUI units = 8 design px; the line box is
    /// <c>ascent = 0.14</c>, <c>height = 0.17</c>.</summary>
    private static TextFont Galahad14()
    {
        // The real shipped header, PARSED rather than hand-assigned, so the .txi reader is on the golden path
        // too. Only glyph 32's real coordinates are listed; the rest of the table repeats them, which is what
        // makes the expected arithmetic hand-checkable.
        var sb = new StringBuilder();
        sb.Append("numchars 256\nfontheight 0.17\nbaselineheight 0.14\ntexturewidth 2.56\n");
        sb.Append("spacingR 0.0\nspacingB 0.0\ncaretindent -0.01\n");
        sb.Append("upperleftcoords 256\n");
        for (int i = 0; i < 256; i++) sb.Append("0.0 0.925781 0.0\n");
        sb.Append("endlist\nlowerrightcoords 256\n");
        for (int i = 0; i < 256; i++) sb.Append("0.03125 0.859375 0.0\n");
        sb.Append("endlist\n");
        if (!TextFonts.ParseTxi("fnt_galahad14-golden", sb.ToString(), out TextFont f))
            return new TextFont { Name = "fnt_galahad14-golden" };
        f.Epoch = 1;
        return f;
    }

    private static TextLine Line(string s)
    {
        var l = new TextLine();
        byte[] b = Encoding.Latin1.GetBytes(s);
        l.Set(b, 0, b.Length);
        return l;
    }

    private static TextDoc Doc(TextFont f, float maxWidth = 0f, float maxHeight = -1e-5f, bool wrap = true)
        => new() { Font = f, MaxWidth = maxWidth, MaxHeight = maxHeight, Wrap = wrap };

    private static void Put(TextDoc d, string s)
    {
        byte[] b = Encoding.Latin1.GetBytes(s);
        TextObjects.AddLine(d, b);
    }

    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TextObjects.SelfCheck");
        float grid = TextObjects.GuiToPixel;
        TextObjects.SetGuiToPixel(0.01f);
        TextMarkup.ResetStack();

        Pixels(check);
        Markup(check);
        Measuring(check);
        Wrapping(check);
        Alignment(check);
        Publication(check);
        Caret(check);
        ListRowClick(check);
        Scrolling(check);
        ChangeDriven(check);
        Quads(check);
        Selection(check);
        Golden(check);
        FontLoading(check);
        SlotTable(check);
        SortArray(check);
        DisplayBlock(check);
        LineModel(check);
        Reviewed(check);

        TextObjects.SetGuiToPixel(grid);
        TextMarkup.ResetStack();
        // The suite is not a workload: leave the session counters where it found them.
        TextObjects.Builds = 0; TextObjects.CacheHits = 0; TextQuads.Stale = 0;
        log.Info(check.Ok ? "TextObjects.SelfCheck: passed" : "TextObjects.SelfCheck: FAILED");
    }

    private static void Pixels(Checker c)
    {
        c.Expect(Near(TextObjects.AlignToPixel(0.123f), 0.12f), "the pixel snap rounds 12.3 px down to 12");
        c.Expect(Near(TextObjects.AlignToPixel(0.125f), 0.13f), "the pixel snap rounds a half up, as floor(x+0.5) does");
        c.Expect(Near(TextObjects.AlignToPixel(-0.123f), -0.12f), "a negative snaps toward zero the same way");
        TextObjects.SetGuiToPixel(0.005f);
        c.Expect(Near(TextObjects.AlignToPixel(0.123f), 0.125f), "at GUI scale 2 the grid is half a design pixel");
        TextObjects.SetGuiToPixel(0.01f);
    }

    private static void Markup(Checker c)
    {
        TextMarkup.ResetStack();
        float r = 1f, g = 1f, b = 1f, a = 1f;
        var s = Encoding.Latin1.GetBytes("<cÿ\u0080\u0001>x</c>");
        c.Expect(TextMarkup.Process(s, 0, ref r, ref g, ref b, ref a) == 6, "<cRGB> consumes 6 bytes");
        c.Expect(Near(r, 1f) && Near(g, 128f / 255f) && Near(b, 1f / 255f) && Near(a, 1f),
          "<cRGB> takes the three bytes raw over 255 and forces alpha to 1");
        c.Expect(TextMarkup.Process(s, 7, ref r, ref g, ref b, ref a) == 4, "</c> consumes 4 bytes");
        c.Expect(Near(r, 1f) && Near(g, 1f) && Near(b, 1f), "</c> pops the pushed colour back");

        var bad = Encoding.Latin1.GetBytes("<cAB>x<x><");
        c.Expect(TextMarkup.Skip(bad, 0) == 0,
          "a <c with only two bytes before > is not markup");
        c.Expect(TextMarkup.Skip(bad, 6) == 0, "<x> is not markup");
        c.Expect(TextMarkup.Skip(bad, 9) == 0, "a bare trailing < is not markup");
        var noGt = Encoding.Latin1.GetBytes("<cABCD");
        c.Expect(TextMarkup.Skip(noGt, 0) == 0,
          "<cRGB without the closing > is not markup");
        var slashOnly = Encoding.Latin1.GetBytes("</c");
        c.Expect(TextMarkup.Skip(slashOnly, 0) == 4,
          "</c needs no closing > -- the engine does not check for one");

        // The ring is 16 deep and wraps: a 17th push overwrites the oldest, so popping 17 times does not
        // return the first colour.
        TextMarkup.ResetStack();
        r = g = b = 0.5f; a = 1f;
        var push = Encoding.Latin1.GetBytes("<c\u0001\u0001\u0001>");
        for (int i = 0; i < 17; i++) TextMarkup.Process(push, 0, ref r, ref g, ref b, ref a);
        var pop = Encoding.Latin1.GetBytes("</c>");
        for (int i = 0; i < 17; i++) TextMarkup.Process(pop, 0, ref r, ref g, ref b, ref a);
        c.Expect(!Near(r, 0.5f), "a 17th nested colour overwrites the oldest in the 16-entry ring");
        TextMarkup.ResetStack();
    }

    private static void Measuring(Checker c)
    {
        TextFont f = Synthetic();
        TextLine l = Line("abcde");
        TextObjects.Measure(f, l, 0f, false);
        c.Expect(l.VisibleLength == 5, "an unbounded line is visible to its end");
        c.Expect(Near(l.Width, 0.50f), "five 10 px glyphs measure 50 px");
        c.Expect(Near(l.Ascent, 0.16f) && Near(l.Height, 0.20f),
          "the line box is the font's ascent and full height");

        TextLine m = Line("ab<cÿ\u0001\u0001>cd</c>e");
        TextObjects.Measure(f, m, 0f, false);
        c.Expect(Near(m.Width, 0.50f), "markup consumes no width");
        c.Expect(m.VisibleLength == m.Length, "markup bytes stay inside the visible length");

        TextLine n = Line("ab\ncd");
        TextObjects.Measure(f, n, 0f, false);
        c.Expect(n.VisibleLength == 2 && Near(n.Width, 0.20f), "a newline ends the line at its index");

        TextFont sp = Synthetic(0.10f, 0.02f);
        TextLine q = Line("abc");
        TextObjects.Measure(sp, q, 0f, false);
        c.Expect(Near(q.Width, 0.34f),
          "spacingR falls between glyphs but not after the last: 10 + 2 + 10 + 2 + 10 = 34 px");
    }

    private static void Wrapping(Checker c)
    {
        TextFont f = Synthetic();
        // "aa bb cc" is 8 glyphs = 80 px; at 55 px the second space (index 5) is the break.
        var d = Doc(f, 0.55f);
        Put(d, "aa bb cc");
        c.Expect(d.LineCount == 2, "an over-wide line wraps onto a second line");
        c.Expect(Encoding.Latin1.GetString(d.Lines[0].Span) == "aa bb ", "the break keeps the trailing space");
        c.Expect(Encoding.Latin1.GetString(d.Lines[1].Span) == "cc", "the remainder starts after the space");
        c.Expect(Near(d.Lines[0].Width, 0.50f), "the wrapped width excludes the space it broke on");

        var longWord = Doc(f, 0.35f);
        Put(longWord, "aaaaaa");
        c.Expect(longWord.LineCount == 2 && longWord.Lines[0].Length == 3,
          "a word with no space hard-breaks at the glyph that overflowed");

        // The engine's sentinel: lastSpaceIdx == 0 means BOTH "no space" and "a space at index 0", so a line
        // beginning with a space hard-breaks instead of wrapping on it.
        var leading = Doc(f, 0.25f);
        Put(leading, " abcd");
        c.Expect(leading.Lines[0].Length == 2,
          "a space at index 0 is the engine's no-space sentinel, so the line hard-breaks");

        var off = Doc(f, 0.25f, wrap: false);
        Put(off, "abcdef");
        c.Expect(off.LineCount == 1, "wrapping off leaves one long line");

        var capped = Doc(f, 0.15f);
        capped.MaxLines = 1;
        Put(capped, "aaaa");
        c.Expect(capped.LineCount == 1, "the line ceiling stops the wrap");

        // A continuation line gets colour 0, not its parent's: the engine copies the state short but not the
        // colour short when it mints the new CStringItem.
        var col2 = Doc(f, 0.25f);
        TextObjects.AddLine(col2, Encoding.Latin1.GetBytes("xx"));
        col2.Lines[0].Colour = 3;
        col2.Lines[0].State = 7;
        TextObjects.AddText(col2, Encoding.Latin1.GetBytes(" yy zz"));
        c.Expect(col2.LineCount > 1 && col2.Lines[1].Colour == 0 && col2.Lines[1].State == 7,
          "a wrapped continuation line inherits the state but NOT the colour index");
    }

    /// <summary>A build never writes the list an issuer may be walking. The issuer takes
    /// <see cref="TextDoc.PublishedQuads"/> in one reference read, so what this asserts is that the reference
    /// it holds stays whole across the next build -- and that a SECOND build inside one frame takes a fresh
    /// list rather than recycling the one this frame already published.</summary>
    private static void Publication(Checker c)
    {
        TextFont f = Synthetic();
        var d = Doc(f, 1.00f, 1.00f);
        Put(d, "abcd");
        TextObjects.Relayout(d);
        TextQuads.Build(d);
        QuadList? held = d.PublishedQuads;
        c.Expect(held != null, "a built document publishes a list");
        int verts = held!.QuadVerts;
        float x0 = held.Quads[0];
        int runs = held.RunCount;

        Put(d, "abcdefgh");
        TextObjects.Relayout(d);
        d.QuadsStale = true;
        TextQuads.Build(d);
        c.Expect(!ReferenceEquals(d.PublishedQuads, held), "the next build publishes a DIFFERENT list");
        c.Expect(held.QuadVerts == verts && held.RunCount == runs && Near(held.Quads[0], x0),
                 "the list an issuer was holding is untouched by that build -- no rewrite under the reader");
        c.Expect(d.QuadVerts > verts, "the document now reads the longer, newly published list");

        // Two builds in ONE frame: the second cannot recycle the slot this frame already published.
        QuadList? first = d.PublishedQuads;
        d.QuadsStale = true;
        TextQuads.Build(d);
        c.Expect(!ReferenceEquals(d.PublishedQuads, first) && !ReferenceEquals(d.PublishedQuads, held),
                 "a second build in the same frame takes a list neither issuer can be holding");

        TextDoc empty = Doc(f, 1.00f, 1.00f);
        c.Expect(empty.PublishedQuads == null && empty.QuadVerts == 0 && empty.RunCount == 0,
                 "a document that has never been built publishes nothing and reads as empty");
    }

    private static void Alignment(Checker c)
    {
        TextFont f = Synthetic();
        var d = Doc(f, 1.00f, 1.00f);
        Put(d, "abcd");                       // 40 px wide in a 100 px box
        TextObjects.Relayout(d);
        d.AlignH = 0f;
        TextQuads.Build(d);
        c.Expect(Near(d.Quads[0], 0f), "left alignment starts the first glyph at x = 0");
        d.AlignH = 0.5f; d.QuadsStale = true;
        TextQuads.Build(d);
        c.Expect(Near(d.Quads[0], 0.30f), "centre alignment insets by (100 - 40) / 2 = 30 px");
        d.AlignH = 1f; d.QuadsStale = true;
        TextQuads.Build(d);
        c.Expect(Near(d.Quads[0], 0.60f), "right alignment insets by 100 - 40 = 60 px");
    }

    private static void Caret(Checker c)
    {
        TextFont f = Synthetic();
        var d = Doc(f);
        Put(d, "abcde");
        d.Caret.Column = 3;
        TextObjects.UpdateCaret(d, 0, 1e6f, false);
        c.Expect(Near(d.Caret.X, 0.30f), "the caret at column 3 sits after three 10 px glyphs");
        d.Caret.Column = 99;
        TextObjects.UpdateCaret(d, 0, 1e6f, true);
        c.Expect(d.Caret.Column == 5, "a clamping update pulls the column back to the line's length");

        // Markup-blind, exactly like CAurFont::UpdateCaret: the six <cRGB> bytes each advance the pen.
        var m = Doc(f);
        Put(m, "<c\u0001\u0001\u0001>ab");
        m.Caret.Column = 8;
        TextObjects.UpdateCaret(m, 0, 1e6f, false);
        c.Expect(Near(m.Caret.X, 0.80f),
          "the caret walk counts markup bytes as glyphs -- an engine behaviour, reproduced");

        c.Expect(Near(TextObjects.CaretAlpha(0), 1f), "the caret blink peaks at 1");
        c.Expect(Near(TextObjects.CaretAlpha(500), 0.5f), "and troughs at 0.5 half a second later");
        c.Expect(Near(TextObjects.CaretAlpha(1000), 1f), "with a one-second period");
    }

    /// <summary>The click that picks a list ROW. A GUI list is ONE text gob whose rows are its LINES, and the
    /// game answers a click on the list control by handing <c>StringGob::MoveCaretTo</c> @0x4926d0 the hit
    /// point IN WORLD SPACE -- the slot's first statement is the transform into the string's frame. Taking the
    /// argument as already-local clamps every click to the first line.</summary>
    private static void ListRowClick(Checker c)
    {
        TextFont f = Synthetic();
        DbId id = TextObjects.Create(default, -1);
        TextDoc? d = TextObjects.Of(id);
        if (d == null) { c.Expect(false, "the list-row text record could not be made"); return; }
        d.Font = f; d.Wrap = false; d.MaxWidth = 2f; d.MaxHeight = 0.60f;
        for (int k = 0; k < 3; k++) Put(d, "row");
        TextObjects.Relayout(d);

        // Somewhere no local coordinate could be mistaken for: the gob's own frame starts at its origin, so a
        // world point read as a local one lands tens of line-heights below the box.
        var at = new Vector3(5f, 7f, 41f);
        SceneDb.ObjPos[id.Index] = at;
        SceneDb.ObjRot[id.Index] = Quaternion.Identity;

        // Lines are 20 px tall on the synthetic font and the box hangs DOWN from the origin, so row k spans
        // y in -(k+1)*0.20 .. -k*0.20 of the string's own space.
        for (int k = 0; k < 3; k++)
        {
            var world = at + new Vector3(0.10f, -(k * 0.20f + 0.10f), 0f);
            c.Expect(TextSlots.MoveCaretTo(id, world) == 1 && TextSlots.GetCaretLinePos(id) == k,
                     $"a world-space click on list row {k} selects row {k}");
        }
        // The engine's own two edges, on the same world frame: above the box is the line BEFORE the top
        // (clamped to it), and past the last line is the last line.
        TextSlots.MoveCaretTo(id, at + new Vector3(0.10f, 0.10f, 0f));
        c.Expect(TextSlots.GetCaretLinePos(id) == 0, "a click above the box selects the top line");
        TextSlots.MoveCaretTo(id, at + new Vector3(0.10f, -5f, 0f));
        c.Expect(TextSlots.GetCaretLinePos(id) == 2, "a click below the last line selects the last line");

        TextObjects.Destroy(id);
    }

    private static void Scrolling(Checker c)
    {
        TextFont f = Synthetic();
        var d = Doc(f, 0f, 0.65f);            // 65 px of height, 20 px lines -> 3 fit
        for (int i = 0; i < 10; i++) Put(d, "line");
        TextObjects.Relayout(d);
        c.Expect(d.VisibleLines == 3, "three 20 px lines fit in a 65 px box");
        c.Expect(Near(d.TotalHeight, 2.00f), "ten lines total 200 px");
        c.Expect(Near(d.VisibleHeight, 0.60f), "the visible run is 60 px");

        TextObjects.ScrollToLine(d, 4);
        c.Expect(d.TopLine == 4, "scrolling to a line moves the top");
        TextObjects.ScrollToLine(d, 99);
        c.Expect(d.TopLine == 10, "a line past the end clamps to the line count");

        var p = Doc(f, 0f, 0.65f);
        for (int i = 0; i < 10; i++) Put(p, "line");
        TextObjects.Relayout(p);
        TextObjects.ScrollToPixel(p, 50);
        c.Expect(p.TopLine == 2 && Near(p.SubLineScroll, 0.10f),
          "50 px of scroll lands on line 2 with 10 px left over inside it");
        c.Expect(p.PixelScroll, "pixel scrolling latches its own mode");
    }

    private static void ChangeDriven(Checker c)
    {
        TextFont f = Synthetic();
        var d = Doc(f, 0.55f);
        Put(d, "aa bb cc");
        int before = d.Layouts;

        d.MaxWidth = 0.35f;
        c.Expect(d.Layouts == before,
          "SetMaxWidth lays nothing out -- it is a pure setter in the engine and here");
        TextObjects.Relayout(d);
        c.Expect(d.Layouts == before + 1, "RefreshDisplay is the one explicit relayout");

        before = d.Layouts;
        int builds = d.Builds;
        TextQuads.Build(d);
        c.Expect(d.Builds == builds + 1, "a stale document builds its quads once");
        TextQuads.Build(d);
        TextQuads.Build(d);
        c.Expect(d.Builds == builds + 1 && d.Layouts == before,
          "two more frames reuse the cached quads: no rebuild, no relayout");

        TextObjects.SetText(d, Encoding.Latin1.GetBytes("new"));
        c.Expect(d.QuadsStale, "new text makes the quads stale");
        TextQuads.Build(d);
        c.Expect(d.Builds == builds + 2, "and exactly one rebuild follows");

        // An atlas re-bake moves every UV, so the epoch alone must force a rebuild.
        builds = d.Builds;
        f.Epoch++;
        TextQuads.Build(d);
        c.Expect(d.Builds == builds + 1, "a new atlas epoch rebuilds even when nothing else changed");
    }

    private static void Quads(Checker c)
    {
        TextFont f = Synthetic();
        var d = Doc(f, 0f, 1.00f);
        Put(d, "ab");
        TextObjects.Relayout(d);
        c.Expect(TextQuads.Build(d), "a two-glyph line builds");
        c.Expect(d.QuadVerts == 2 * TextQuads.VertsPerGlyph, "six vertices a glyph, non-indexed");
        c.Expect(d.RunCount == 1, "one colour run when there is no markup");
        // Vertex 0 is the bottom-left: x = 0, u = ul.u, v = lr.v.
        c.Expect(Near(d.Quads[0], 0f) && Near(d.Quads[3], 0f) && Near(d.Quads[4], 0f),
          "the first vertex is the glyph's bottom-left corner with ul.u and lr.v");
        c.Expect(Near(d.Quads[1 * TextQuads.VertFloats], 0.10f),
          "the second vertex is the same glyph's bottom-right");
        c.Expect(Near(d.Quads[6 * TextQuads.VertFloats], 0.10f),
          "the next glyph starts at the snapped pen, 10 px along");

        var runs = Doc(f, 0f, 1.00f);
        Put(runs, "a<cÿ\u0001\u0001>b</c>c");
        TextObjects.Relayout(runs);
        TextQuads.Build(runs);
        c.Expect(runs.RunCount == 3, "each markup token cuts a new draw: three runs");
        c.Expect(Near(runs.Runs[1].R, 1f) && Near(runs.Runs[1].G, 1f / 255f),
          "the middle run carries the markup colour");
        c.Expect(runs.QuadVerts == 3 * TextQuads.VertsPerGlyph, "and the markup bytes emit no geometry");

        var caret = Doc(f, 0f, 1.00f);
        Put(caret, "ab");
        caret.CaretVisible = true;
        caret.Caret.Line = 0; caret.Caret.Column = 1;
        TextObjects.UpdateCaret(caret, 0, 1e6f, false);
        TextObjects.Relayout(caret);
        TextQuads.Build(caret);
        c.Expect(caret.QuadVerts == 3 * TextQuads.VertsPerGlyph, "the caret adds one glyph's worth of geometry");
        c.Expect(caret.CaretRun == caret.RunCount - 1 && Near(caret.Runs[caret.CaretRun].R, 0.5f),
          "the caret is the last run, in the caret colour, so the frame can override its alpha alone");

        var dbcs = Doc(new TextFont { Name = "db", DoubleByte = true, NumChars = 256, Epoch = 1 }, 0f, 1.00f);
        Put(dbcs, "ab");
        c.Expect(!TextQuads.Build(dbcs) && dbcs.QuadVerts == 0,
          "a double-byte font draws nothing, as the engine's TextOutDoubleByte stub does");
    }

    /// <summary><c>ui.textedit</c>: offsets into the joined lines, word steps, and the highlight boxes drawn first and
    /// untextured under the selected glyphs, with the caret still the last run.</summary>
    private static void Selection(Checker c)
    {
        TextFont f = Synthetic();
        var d = Doc(f, 0f, 1.00f);
        Put(d, "ab cd");
        Put(d, "ef\n");
        c.Expect(EditSelect.Locate(d, 5) == (1, 0), "a line's end is the next line's start");
        c.Expect(EditSelect.Locate(d, 8) == (2, 0), "the end after a final newline is the line past the last");
        d.Caret.Line = 1; d.Caret.Column = 1;
        c.Expect(EditSelect.CaretAt(d) == 6, "the caret's offset counts every earlier line");
        ReadOnlySpan<byte> s = EditSelect.Joined(d);
        c.Expect(EditSelect.WordLeft(s, 3) == 0 && EditSelect.WordLeft(s, 6) == 3, "word-left skips spaces, then a word");
        c.Expect(EditSelect.WordRight(s, 0) == 2 && EditSelect.WordRight(s, 2) == 7, "word-right skips spaces, then a word up to the newline");
        c.Expect(EditSelect.WordAt(s, 1) == (0, 2) && EditSelect.WordAt(s, 2) == (2, 3), "a double-click takes a word or a space run");

        var h = Doc(f, 0f, 1.00f);
        Put(h, "abcd");
        h.CaretVisible = true;
        h.Caret.Line = 0; h.Caret.Column = 3;
        TextObjects.UpdateCaret(h, 0, 1e6f, false);
        h.SelAnchor = 1;
        TextObjects.Relayout(h);
        TextQuads.Build(h);
        c.Expect(h.SelRun == 0 && h.Runs[0].Count == TextQuads.VertsPerGlyph, "one selected line is one box, drawn first");
        int box = h.Runs[0].First * TextQuads.VertFloats;
        c.Expect(Near(h.Quads[box], 0.10f) && Near(h.Quads[box + TextQuads.VertFloats], 0.30f),
          "the box spans the selected columns' pen positions");
        c.Expect(h.CaretRun == h.RunCount - 1, "the caret stays the last run");
        h.CaretVisible = false;
        h.QuadsStale = true;
        TextQuads.Build(h);
        c.Expect(h.SelRun == -1, "an unfocused box draws no selection");
    }

    private static void Golden(Checker c)
    {
        TextFont f = Galahad14();
        // Hand arithmetic from fnt_galahad14.txi: advance = (0.03125 - 0) * 2.56 = 0.08 = 8 px;
        // ascent = baselineheight + spacingB = 0.14; height = fontheight + ascent - baselineheight = 0.17.
        TextLine l = Line("HELLO");
        TextObjects.Measure(f, l, 0f, false);
        c.Expect(Near(l.Width, 0.40f), "golden: five 8 px glyphs of fnt_galahad14 measure 40 px");
        c.Expect(Near(l.Ascent, 0.14f), "golden: its ascent is 14 px");
        c.Expect(Near(l.Height, 0.17f), "golden: its line box is 17 px");

        // 40 px of text in a 33 px box with no space breaks at the glyph that crossed: 5 * 8 = 40 > 33,
        // and 4 * 8 = 32 <= 33, so the break index is 4.
        var d = Doc(f, 0.33f);
        Put(d, "HELLO");
        c.Expect(d.Lines[0].Length == 4 && Near(d.Lines[0].Width, 0.32f),
          "golden: a 33 px box keeps four glyphs (32 px) and pushes the fifth");

        // Two lines of 17 px each stack to 34 px; the second line's glyph baseline sits 17 px below the first.
        var two = Doc(f, 0f, 0.40f);
        Put(two, "AB"); Put(two, "CD");
        TextObjects.Relayout(two);
        TextQuads.Build(two);
        float y0 = two.Quads[2 * TextQuads.VertFloats + 1];                 // first line, top-left y
        float y1 = two.Quads[(2 * 6 + 2) * TextQuads.VertFloats + 1];       // second line, top-left y
        c.Expect(Near(y0 - y1, 0.17f), "golden: the second line's box sits exactly one 17 px line lower");

        c.Expect(Near(f.CaretIndent, -0.01f), "golden: fnt_galahad14's caret indents by -1 px");
        c.Expect(f.NumChars == 256 && !f.Ttf && Near(f.TextureWidth, 2.56f),
          "golden: the whole font came out of the .txi reader, not out of this test");
    }

    private static void FontLoading(Checker c)
    {
        // --- the font-name token parse (CAurTextureBasic::ParseTTFTokens @0x485930) ---
        var n = TextFontLoad.ParseName("ttf fnt_default 14");
        c.Expect(n.Ttf && n.Resref == "fnt_default" && n.PointSize == 14, "a ttf token names a resref and a point size");
        c.Expect(!TextFontLoad.ParseName("fnt_galahad14").Ttf, "a bare name is a bitmap font");
        c.Expect(!TextFontLoad.ParseName("ttf fnt_default").Ttf, "a ttf token with no size is not a ttf font");
        c.Expect(!TextFontLoad.ParseName("TTF fnt_default 14").Ttf, "the ttf token is matched case-sensitively, as strcmp does");

        // --- RemapFontName (CTlkTable::RemapFontName @0x53fb60) ---
        c.Expect(TextFonts.Remap("fnt_galahad14") == "ttf fnt_maintext 17", "fnt_galahad14 redirects to the main-text TTF");
        c.Expect(TextFonts.Remap("fnt_console") == TextFonts.Default, "fnt_console redirects to the default TTF");
        c.Expect(TextFonts.Remap("anything else") == TextFonts.Default, "an unknown name becomes the default TTF");
        c.Expect(TextFonts.Remap("ttf fnt_maintext 12") == "ttf fnt_maintext 12", "a ttf name is used as given");
        TextFonts.LargeDialogFont = true;
        c.Expect(TextFonts.Remap("fnt_floaty_smal") == TextFonts.DialogLarge, "the large-dialog option swaps the dialog font");
        TextFonts.LargeDialogFont = false;

        // --- atlas geometry (CAuroraTTFTexture::Load @0x11329d0) ---
        c.Expect(TextFontLoad.NextPow2(1) == 1 && TextFontLoad.NextPow2(0) == 1 && TextFontLoad.NextPow2(2) == 2
                 && TextFontLoad.NextPow2(1000) == 1024, "the engine's doubling loop rounds up to a power of two");
        // fnt_default at 14 px, scale 1: H = pow2(14 + 6) = 32; W = pow2(256*3 + 3 + 256*14 = 4355) = 8192.
        TextFontLoad.AtlasSize(14f, 256, out int w, out int h);
        c.Expect(h == 32 && w == 8192, "golden: fnt_default at 14 px packs into an 8192x32 strip before cropping");
        TextFontLoad.AtlasSize(28f, 256, out w, out h);
        c.Expect(h == 64 && w == 8192, "golden: GUI scale 2 (28 px) packs into an 8192x64 strip, room for every glyph");
        c.Expect(TextFontLoad.AtlasPadding == 3 && TextFontLoad.Oversample == 1,
          "the bake uses the engine's 3-texel padding and NO oversampling");
        c.Expect(TextFontLoad.CropWidth(1024, 600) == 1024, "a glyph past the half mark blocks every crop step");
        c.Expect(TextFontLoad.CropWidth(1024, 500) == 512, "one crop step when every glyph fits the left half");
        c.Expect(TextFontLoad.CropWidth(1024, 200) == 256, "two steps when they fit a quarter");
        c.Expect(TextFontLoad.PixelSize(2f, 14) == 28f, "GUI scale 2 bakes the 14 pt font at 28 px");

        // --- the UV derivation, with hand arithmetic over a 256x32 atlas, baseline 11, size 14 ---
        var p = new TextFontLoad.Packed(10, 2, 18, 14, 1.5f, -9f, 9.25f, 9.5f, 3f);
        TextFontLoad.GlyphCoords(p, 256, 32, 11, 14f, out float u0, out float v0, out float z0,
                                 out float u1, out float v1, out float z1);
        c.Expect(Near(u0, 9.5f / 256f) && Near(u1, 19.5f / 256f),
          "the u range is (x0 - 0.5) .. (x1 + 1.5), one padding texel each side");
        // (2 - (-9 + 11 - 0.5)) / 32 and (14 - 11 - 3 + 0.5 + 14) / 32, worked out by hand.
        c.Expect(Near(v0, 0.015625f), "ul.v lifts the box by the glyph's y offset and the baseline");
        c.Expect(Near(v1, 0.453125f), "lr.v drops it to the full line box");
        c.Expect(Near(z0, 1f / 256f) && Near(z1, 9.25f / 256f), "ul.z is the left bearing and lr.z the advance, in u");

        // --- the outline filter (CAuroraTTFTexture::EnhanceCharacter @0x1132810) ---
        Span<byte> cov = stackalloc byte[9];
        cov.Clear();
        TextFontLoad.Enhance(cov, 9, 3, 4, 1f, 0.1f, 0.35f, out byte lum, out byte a);
        c.Expect(lum == 0 && a == 0, "an empty neighbourhood is fully transparent");
        cov.Fill(255);
        TextFontLoad.Enhance(cov, 9, 3, 4, 1f, 0.1f, 0.35f, out lum, out a);
        c.Expect(a == 255 && lum == 255, "a saturated core is opaque white with no outline left to add");
        cov.Clear(); cov[3] = 255;                      // one lit neighbour to the left of a dark centre
        TextFontLoad.Enhance(cov, 9, 3, 4, 1f, 0.1f, 0.35f, out lum, out a);
        c.Expect(a > 0 && lum < 128, "a dark pixel beside a lit one becomes the dark outline, not the glyph");
        // One pixel at half coverage, pinned by hand right through the formula: blur = core = 128/255,
        // inv = 1 - core, outline = blur * 0.35 * inv = 0.08750, a = 0.58946, (int)(a*255 + 0.49) = 150.
        // The 128 does NOT survive: the blur counts the centre pixel itself and the outline adds to alpha.
        cov.Clear(); cov[4] = 128;
        TextFontLoad.Enhance(cov, 9, 3, 4, 1f, 0.1f, 0.35f, out _, out byte a2);
        c.Expect(a2 == 150, "golden: a half-covered pixel comes out at alpha 150 through the engine's filter");

        // --- metrics write-back (CAurTextureBasic::LoadFromTTF @0x4875b0) ---
        var f = new TextFont { Name = "m", Ttf = true };
        f.UlU[0] = 0f; f.LrU[0] = 0.05f;
        TextFontLoad.Metrics(f, 256, 11, 1f);
        c.Expect(Near(f.TextureWidth, 2.56f), "texturewidth is the atlas width in GUI units");
        c.Expect(Near(f.BaselineHeight, 0.11f), "baselineheight is the baked baseline in GUI units");
        c.Expect(Near(f.CaretIndent, -0.05f * 0.499f * 2.56f + 0.01f), "caretindent is derived from glyph 0's width");
        TextFontLoad.Metrics(f, 256, 11, 2f);
        c.Expect(Near(f.TextureWidth, 1.28f) && Near(f.BaselineHeight, 0.055f),
          "GUI scale divides back out, so GUI units stay scale-independent");

        // --- the .txi parse, on the real shipped fnt_default.txi values ---
        var g = new TextFont { Name = "t" };
        float ct = 1f, ob = 0.1f, oo = 0.35f;
        TextFonts.ParseTxiInto(g, "ttfContrast 1.15\nttfOutlineOpacity 0.35\nttfOutlineBrightness 0.1\nspacingR 0.0\n",
                               ref ct, ref ob, ref oo);
        c.Expect(Near(ct, 1.15f) && Near(ob, 0.1f) && Near(oo, 0.35f),
          "golden: fnt_default.txi's three outline terms parse");
        c.Expect(TextFonts.ParseTxi("b", "numchars 2\nfontheight 0.17\nbaselineheight 0.14\ntexturewidth 2.56\n"
                                       + "upperleftcoords 2\n0.0 0.925781 0.0\n0.03125 0.925781 0.0\nendlist\n"
                                       + "lowerrightcoords 2\n0.03125 0.859375 0.0\n0.0625 0.859375 0.0\nendlist\n",
                                    out TextFont bm)
                 && bm.NumChars == 2 && Near(bm.Advance(0) , 0.08f),
          "golden: a bitmap .txi gives fnt_galahad14's 8 px advance verbatim");
        c.Expect(!TextFonts.ParseTxi("n", "fontheight 0.17\n", out _), "a .txi with no glyphs is not a font");
    }

    /// <summary>The behaviours review round 1 found missing, each re-read off the decompile first.</summary>
    private static void Reviewed(Checker c)
    {
        TextFont f = Synthetic();

        // --- ShowAsManyLines' pixel-scroll body KEEPS the overshooting line (the chat log's partial top line) ---
        var px = Doc(f, 0f, 0.65f);            // 65 px of box, 20 px lines
        for (int i = 0; i < 10; i++) Put(px, "line");
        TextObjects.Relayout(px);
        int lineMode = px.VisibleLines;
        TextObjects.ScrollToPixel(px, 50);
        c.Expect(px.PixelScroll && px.VisibleLines == lineMode + 1,
          "pixel scrolling keeps one more line than line scrolling: the partially visible one");
        c.Expect(Near(px.VisibleHeight, (lineMode + 1) * 0.20f),
          "and its height counts the overshooting line in full");
        c.Expect(px.ScrollBudget >= px.VisibleLines, "the scrollback budget covers at least the visible run");

        // --- UpdateCaret: a target x picks the NEARER gap, and a column walks to an x ---
        var cr = Doc(f);
        Put(cr, "abcde");
        cr.Caret.Column = -1;
        TextObjects.UpdateCaret(cr, 0, 0.24f, false);     // 24 px: past 'b', less than half way through 'c'
        c.Expect(cr.Caret.Column == 2 && Near(cr.Caret.X, 0.20f),
          "a click 4 px into the third glyph lands before it, on the half-advance midpoint");
        cr.Caret.Column = -1;
        TextObjects.UpdateCaret(cr, 0, 0.26f, false);
        c.Expect(cr.Caret.Column == 3, "a click 6 px in lands after it");
        cr.Caret.Column = 3;
        TextObjects.UpdateCaret(cr, 0, 1e6f, false);
        c.Expect(cr.Caret.Column == 3 && Near(cr.Caret.X, 0.30f), "a column plus a huge limit walks to that column's x");
        cr.Caret.Column = -1;
        TextObjects.UpdateCaret(cr, 0, 1e6f, false);
        c.Expect(cr.Caret.Column == 5, "column -1 and a huge limit runs to the line's end");
        // Wrapping off, or the newline would have split this into two lines before the caret ever walked it.
        var nl = Doc(f, 0f, -1e-5f, false);
        Put(nl, "ab\ncd");
        nl.Caret.Column = -1;
        TextObjects.UpdateCaret(nl, 0, 1e6f, true);
        c.Expect(nl.Caret.Column == 2, "the newline stops the walk only when the caller asks it to");
        nl.Caret.Column = -1;
        TextObjects.UpdateCaret(nl, 0, 1e6f, false);
        c.Expect(nl.Caret.Column == 5, "and does not when it does not");

        // --- the slots over a real record ---
        DbId id = TextObjects.Create(default, 8);
        TextDoc d = TextObjects.Of(id)!;
        d.Font = f;
        c.Expect(!id.IsNull && d.Owner.Value == id.Value, "a text record knows its own handle");

        // Lifetimes: a text object joins its scene's member list, so a scene destroy reaches it, and leaves
        // again on its own destroy. Without the join every GUI label outlives the scene that made it.
        DbId sc = SceneDb.Scenes.Alloc();
        DbId joined = TextObjects.Create(sc, 4);
        c.Expect(SceneDb.ObjScene[joined.Index].Value == sc.Value, "a new text object records its scene");
        c.Expect(DbScene.MemberCount(sc) == 1, "and is in that scene's member list, not merely pointing at it");
        TextObjects.Destroy(joined);
        c.Expect(DbScene.MemberCount(sc) == 0, "and leaves the list when it dies");
        SceneDb.Scenes.Free(sc);

        // The position offset is a column of its own, NOT ObjPos -- the attach placement overwrites that one
        // every compose. And vanilla writes nothing at all unless the behaviour is an AttachTranslate.
        DbId off = TextObjects.Create(default, 4);
        SceneDb.ObjPos[off.Index] = new Vector3(7f, 8f, 9f);
        c.Expect(TextSlots.SetPositionOffset(off, new Vector3(1f, 2f, 3f)) == 0,
          "SetPositionOffset answers 0 with no AttachTranslate behaviour, exactly as the engine does");
        c.Expect(SceneDb.ObjPos[off.Index] == new Vector3(7f, 8f, 9f),
          "and writes NOTHING -- least of all the placement column the attach owns");
        c.Expect(SceneDb.TextPosOffset[off.Index] == default,
          "the offset column is untouched by a refused write");
        TextObjects.Destroy(off);
        DbId reuse = TextObjects.Create(default, 4);
        c.Expect(SceneDb.TextOffsetBehavior[reuse.Index] == 0 && SceneDb.TextPosOffset[reuse.Index] == default,
          "a recycled record does not inherit the previous one's offset");
        TextObjects.Destroy(reuse);

        TextSlots.SetText(id, Encoding.Latin1.GetBytes("abcdef"));
        d.Caret.Line = 0; d.Caret.Column = 2;
        TextSlots.DeleteTextAtCaret(id, 2);
        c.Expect(Encoding.Latin1.GetString(d.Lines[0].Span) == "abef",
          "DeleteTextAtCaret deletes FORWARD from the column, as CStringItem::DeleteChars does");
        c.Expect(d.Caret.Column == 2, "and leaves the column where it was");

        TextSlots.SetText(id, Encoding.Latin1.GetBytes("ab"));
        d.Caret.Line = 0; d.Caret.Column = 1;
        TextSlots.InsertTextAtCaret(id, Encoding.Latin1.GetBytes("XY"));
        c.Expect(Encoding.Latin1.GetString(d.Lines[0].Span) == "aXYb", "InsertTextAtCaret inserts at the column");
        c.Expect(d.Caret.Column == 3, "and moves the caret past what it inserted");

        TextSlots.SetText(id, Encoding.Latin1.GetBytes("ab\n"));
        c.Expect(TextSlots.SetCaretLinePos(id, d.LineCount),
          "the caret may sit one line PAST the last when the last ends in a newline");
        int before = d.LineCount;
        d.Caret.Line = d.LineCount;
        TextSlots.InsertTextAtCaret(id, Encoding.Latin1.GetBytes("zz"));
        c.Expect(d.LineCount == before + 1 && Encoding.Latin1.GetString(d.Lines[before].Span) == "zz",
          "inserting there APPENDS a line rather than refusing");

        var noNl = TextObjects.Of(TextObjects.Create(default, 8))!;
        noNl.Font = f;
        TextObjects.AddLine(noNl, Encoding.Latin1.GetBytes("ab"));
        c.Expect(noNl.LineCount == 1, "a line with no newline is one line");

        // --- a caret one line past the last still draws (the empty chat input) ---
        var cd = Doc(f, 1f, 1f);
        Put(cd, "a");
        TextObjects.Relayout(cd);
        cd.CaretVisible = true;
        cd.Caret.Line = cd.TopLine + cd.VisibleLines;     // one past the visible run
        cd.Caret.X = 0f;
        TextQuads.Build(cd);
        c.Expect(cd.CaretRun >= 0, "a caret one line past the last is drawn while another line box still fits");

        // --- the pixel grid is part of the quad-cache key ---
        var gk = Doc(f, 1f, 1f);
        Put(gk, "ab");
        TextObjects.Relayout(gk);
        TextQuads.Build(gk);
        int builds = gk.Builds;
        TextQuads.Build(gk);
        c.Expect(gk.Builds == builds, "a second frame reuses the list");
        TextObjects.SetGuiToPixel(0.005f);
        TextQuads.Build(gk);
        c.Expect(gk.Builds == builds + 1, "a new pixel grid rebuilds it, even though the atlas did not move");
        TextObjects.SetGuiToPixel(0.01f);

        // --- a wild index from the game layer must not throw out of a vtable slot ---
        var wild = Doc(f);
        Put(wild, "a");
        TextObjects.InsertLine(wild, Encoding.Latin1.GetBytes("x"), 99);
        TextObjects.InsertLine(wild, Encoding.Latin1.GetBytes("x"), -1);
        TextObjects.DeleteLine(wild, 99);
        c.Expect(wild.LineCount == 1, "an out-of-range line index is refused, not thrown");

        // --- bare setters stay bare ---
        var bare = Doc(f, 0f, 0.65f);
        for (int i = 0; i < 10; i++) Put(bare, "line");
        TextObjects.Relayout(bare);
        int vis = bare.VisibleLines;
        TextSlots.SetMaxHeight(id, 0.05f);
        c.Expect(bare.VisibleLines == vis, "SetMaxHeight on another document changes nothing here");
        bare.MaxHeight = 0.05f;
        c.Expect(bare.VisibleLines == vis, "and setting it directly recomputes nothing: it is a bare store");
    }

    private static void SlotTable(Checker c)
    {
        var s = TextSlots.Slots;
        c.Expect(s.Length == 52, "the CAurString interface is 52 slots on Linux");
        bool order = true, win = true;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i].Linux != i) order = false;
            // MSVC collapses GCC's D1/D0 pair into one slot, so every Windows index past the destructor is
            // one lower -- measured, not assumed (tools/seam/aurora-seam-vtables.tsv).
            int want = i <= 1 ? 0 : i - 1;
            if (s[i].Windows != want) win = false;
        }
        c.Expect(order, "the table is in vtable order with no gaps");
        c.Expect(win, "the Windows index is one lower past the single MSVC destructor slot");
        c.Expect(s[6].Abi == SlotAbi.SretReturn, "SetPosition is the one by-value return in the interface");
        c.Expect(s[5].Abi == SlotAbi.VectorByValue && s[47].Abi == SlotAbi.VectorByValue,
          "SetPositionOffset and MoveCaretTo take a Vector by value, which the two OSes pass differently");
        int floats = 0;
        foreach (var r in s) if (r.Abi == SlotAbi.FloatArgs) floats++;
        c.Expect(floats == 8, "eight slots carry float arguments, where Win64 assigns XMM by argument index");
    }

    /// <summary>Slot 50 under its real caller: <c>CServerPage::SortServerList</c> takes the item array, copies
    /// it aside and writes it back permuted. The facade used to answer NULL there and the engine dereferenced
    /// it four instructions later, which is the online server list crashing the client.</summary>
    private static unsafe void SortArray(Checker c)
    {
        TextFont f = Synthetic();
        var d = Doc(f);
        Put(d, "charlie"); Put(d, "alpha"); Put(d, "bravo");

        nint arr = TextSlots.ItemArray(d);
        c.Expect(arr != 0, "slot 50 hands out a non-null item array -- a NULL is the server-list SIGSEGV");
        nint* a = (nint*)arr;
        c.Expect(a[0] == 1 && a[1] == 2 && a[2] == 3, "the array starts as the identity, one sentinel per line");

        // Exactly what the engine does: a scratch copy, then a permuted write-back (sorted = 1, 2, 0).
        nint* scratch = stackalloc nint[3];
        for (int i = 0; i < 3; i++) scratch[i] = a[i];
        int[] order = [1, 2, 0];
        for (int i = 0; i < 3; i++) a[i] = scratch[order[i]];

        c.Expect(TextSlots.ApplyItemOrder(d), "the permutation is read back as a line move");
        c.Expect(Line(d, 0) == "alpha" && Line(d, 1) == "bravo" && Line(d, 2) == "charlie",
          "the lines follow the order the engine sorted the array into");
        c.Expect(!TextSlots.ApplyItemOrder(d), "the array stands down after one apply");

        // A sentinel the document never wrote means the array is not ours; the lines must not scramble.
        var e = Doc(f);
        Put(e, "one"); Put(e, "two");
        nint* b = (nint*)TextSlots.ItemArray(e);
        b[0] = 99;
        c.Expect(!TextSlots.ApplyItemOrder(e), "an out-of-range sentinel is refused");
        c.Expect(Line(e, 0) == "one" && Line(e, 1) == "two", "a refused permutation leaves the lines alone");
    }

    /// <summary>Slot 24 under <c>CGuiLabel::LoadGffData</c>: the game writes a label's box through the handed-out
    /// block and calls nothing, so a dropped write leaves the label unbounded -- centred on its own origin and
    /// never wrapped (the quit prompt spilling out of its panel).</summary>
    private static unsafe void DisplayBlock(Checker c)
    {
        var d = Doc(Synthetic());
        byte* p = (byte*)TextSlots.DisplayInfo(d);
        ((TextDisplayInfo*)p)->Width = 4.5f; ((TextDisplayInfo*)p)->Height = 1.25f;
        c.Expect(TextSlots.ApplyDisplayInfo(d) && d.MaxWidth == 4.5f && d.MaxHeight == 1.25f,
          "a box written through the display block is adopted on the next slot call");
        d.MaxWidth = 2f;
        c.Expect(!TextSlots.ApplyDisplayInfo(d) && d.MaxWidth == 2f, "a later SetMaxWidth is not undone by the stale block");
        c.Expect(TextSlots.DisplayInfo(d) == (nint)p, "the block is the document's own, not one shared scratch");
        // CGuiEdit caches the pointer at construction and reads the budget through it after ScrollToLine.
        for (int i = 0; i < 30; i++) Put(d, "row");
        d.MaxHeight = 10f * d.Lines[0].Height - 0.0005f;
        TextObjects.Relayout(d);
        TextObjects.ScrollToLine(d, 29);
        TextObjects.Stale(d);
        c.Expect(((TextDisplayInfo*)p)->ScrollBudget == d.ScrollBudget && d.ScrollBudget > 0 && ((TextDisplayInfo*)p)->TopLine == d.TopLine,
          $"the cached block follows the scroll (budget {((TextDisplayInfo*)p)->ScrollBudget} vs {d.ScrollBudget}), so the thumb is not the minimum");
    }

    /// <summary>Slot 7's model rides its line into the published quad list at that line's pen -- the list
    /// box's selection highlight, which drew nowhere while the slot was a stub.</summary>
    private static void LineModel(Checker c)
    {
        var d = Doc(Synthetic());
        Put(d, "one"); Put(d, "two");
        d.Lines[1].Model = new DbId(77);
        TextObjects.Relayout(d);
        TextQuads.Build(d);
        c.Expect(d.AttachedCount == 1 && d.Attached[0].Model.Value == 77,
          "a model attached to a visible line is published with the quads");
        c.Expect(d.AttachedCount == 1 && MathF.Abs(d.Attached[0].Y + d.Lines[0].Height + d.Lines[1].Ascent - 0.001f) < 1e-4f,
          "...at the second line's pen, one line box down");
    }

    private static string Line(TextDoc d, int i)
        => Encoding.Latin1.GetString(d.Lines[i].Bytes, 0, d.Lines[i].Length);
}
