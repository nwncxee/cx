using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using CX.Ui;

namespace CX.Services.Panels;

/// <summary>An icon or image to draw over a panel's background and under its text. <see cref="Item"/> is a client object id for an
/// <c>@item:</c> source, <see cref="Look"/> names an <c>@look:</c> one, <see cref="Model"/> an <c>@model:</c> one, and 0 with neither is a resref in <see cref="Src"/>.</summary>
internal struct IconDraw
{
    public string Src;
    /// <summary>The <c>@look:</c> image name, or null.</summary>
    public string? Look;
    /// <summary>The key of an <c>@model:</c> image (what follows the prefix), or null.</summary>
    public string? Model;
    public uint Item;
    public float X, Y, W, H;
    /// <summary>The opacity as a share of the card's own.</summary>
    public float Alpha;
}

/// <summary>One laid-out panel in panel-local px: the panel is (0,0)..(<see cref="W"/>, <see cref="H"/>) and its tab sits
/// <see cref="TabH"/> above it. <see cref="Back"/> is every rectangle, <see cref="Text"/> every glyph, drawn with the icons between them.</summary>
internal sealed class PanelView
{
    public float W, H, TabH;
    /// <summary>The pulse blocks' floor and period, from the card's style.</summary>
    public float PulseMin, PulsePeriodMs;
    public readonly VertexBatch Back, Text;
    /// <summary>The glyphs of every <c>pulse</c> block, drawn pulsing; null when the panel has none.</summary>
    public VertexBatch? Pulse;
    public readonly List<IconDraw> Icons = [];

    public PanelView(GlyphAtlas atlas)
    {
        Back = new VertexBatch(atlas.SolidU, atlas.SolidV);
        Text = new VertexBatch(atlas.SolidU, atlas.SolidV);
    }
}

/// <summary>What the client knows of the item a panel is about, for <c>{item.name}</c> and <c>{item.stack}</c> in its text.</summary>
internal readonly struct PanelSubject(string name, string stack)
{
    public readonly string Name = name, Stack = stack;
}

/// <summary>
/// Lays one <see cref="PanelDecl"/> out exactly as declared, top to bottom: text wraps to the panel's width, a block's
/// size and colour come from the declaration, and CX adds no design of its own. A face of each wanted size comes from
/// <c>face</c>, as the atlas and the stretch it still needs (1 when that size is baked). Cold: it allocates.
/// </summary>
internal sealed class PanelLayout
{
    /// <summary>The prefix of an image name that describes an item by its appearance.</summary>
    public const string LookPrefix = "@look:";

    /// <summary>Is <paramref name="src"/> an <c>@look:</c> image name?</summary>
    public static bool IsLook(string src) => src.StartsWith(LookPrefix, StringComparison.Ordinal);

    /// <summary>The prefix of an image name that is a live model view.</summary>
    public const string ModelPrefix = "@model:";

    /// <summary>The key of an <c>@model:</c> image name, or null when <paramref name="src"/> is not one or has no key.</summary>
    public static string? ModelKey(string src)
        => src.StartsWith(ModelPrefix, StringComparison.Ordinal) && src.Length > ModelPrefix.Length ? src[ModelPrefix.Length..] : null;

    private struct Atom
    {
        public string Text, Icon;
        public int From, To;
        public float X, W, K, Asc, Desc, Px;
        public GlyphAtlas A;
        public uint Rgba;
        public bool Italic;
    }

    private readonly Func<float, (GlyphAtlas A, float K)> _face;
    private readonly float _s;
    private readonly PanelSubject _subject;
    private readonly List<Atom> _line = [];
    private PanelView _v = null!;
    private PanelDecl _c = null!;
    private bool _pulse;
    private PanelStyle St => _c.St;

    private PanelLayout(Func<float, (GlyphAtlas, float)> face, float scale, PanelSubject subject)
    {
        _face = face; _s = scale; _subject = subject;
    }

    /// <summary>The panel laid out at UI scale <paramref name="scale"/>.</summary>
    public static PanelView Build(PanelDecl card, float scale, Func<float, (GlyphAtlas A, float K)> face, PanelSubject subject)
        => new PanelLayout(face, scale, subject).Run(card);

    private PanelView Run(PanelDecl c)
    {
        _c = c;
        PanelStyle st = c.St;
        GlyphAtlas baseAtlas = _face(1f).A;
        _v = new PanelView(baseAtlas) { PulseMin = st.PulseMin, PulsePeriodMs = st.PulsePeriodMs };
        float w = st.Width * _s, pad = st.Pad * _s;
        float h = Stack(c.Blocks, pad, pad, w - 2f * pad, w, false) + pad;
        _v.W = w; _v.H = Math.Max(h, 2f * pad);
        _v.TabH = c.Tab.Length > 0 ? baseAtlas.LineHeight + st.TabExtra * _s : 0f;
        if (st.Glow != 0) Glow(w, _v.H);
        uint from = st.BgFrom, to = st.BgTo ?? from;
        switch (st.Dir)
        {
            case BgDir.Horizontal: Gradient(0, 0, w, _v.H, from, to, to, from); break;
            case BgDir.Diagonal: uint mid = Mix(from, to); Gradient(0, 0, w, _v.H, from, mid, to, mid); break;
            default: Gradient(0, 0, w, _v.H, from, from, to, to); break;
        }
        Frame(0, 0, w, _v.H, st.Border, st.BorderW, st.Sides);
        if (st.Border2 != 0)
        {
            float i2 = st.Border2Inset * _s;
            if (_v.W > 2f * i2 && _v.H > 2f * i2) Frame(i2, i2, w - i2, _v.H - i2, st.Border2, st.Border2W, st.Sides);
        }
        if (st.Corners != 0) Corners(w, _v.H);
        if (_v.TabH > 0f) Tab(baseAtlas);
        foreach (PanelImage i in c.Images) Image(i);
        Stack(c.Blocks, pad, pad, w - 2f * pad, w, true);
        return _v;
    }

    private static uint Mix(uint a, uint b)
    {
        uint Ch(int sh) => (uint)((((a >> sh) & 0xFF) + ((b >> sh) & 0xFF)) / 2) << sh;
        return Ch(24) | Ch(16) | Ch(8) | Ch(0);
    }

    /// <summary>Soft light round the panel: frames spread outward, each fainter than the one inside it.</summary>
    private void Glow(float w, float h)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(St.GlowSpread * _s));
        uint rgb = St.Glow & 0xFFFFFF00, a0 = St.Glow & 0xFF;
        for (int i = 1; i <= steps; i++)
        {
            uint a = (uint)(a0 * (1f - (i - 1f) / steps) * 0.5f);
            Rect(-i, -i, w + i, -i + 1f, rgb | a); Rect(-i, h + i - 1f, w + i, h + i, rgb | a);
            Rect(-i, -i + 1f, -i + 1f, h + i - 1f, rgb | a); Rect(w + i - 1f, -i + 1f, w + i, h + i - 1f, rgb | a);
        }
    }

    /// <summary>Bracket marks in the four corners, drawn over the frame.</summary>
    private void Corners(float w, float h)
    {
        float len = St.CornerLen * _s, t = Math.Max(1f, St.CornerW * _s);
        uint c = St.Corners;
        Rect(0, 0, len, t, c); Rect(0, 0, t, len, c);
        Rect(w - len, 0, w, t, c); Rect(w - t, 0, w, len, c);
        Rect(0, h - t, len, h, c); Rect(0, h - len, t, h, c);
        Rect(w - len, h - t, w, h, c); Rect(w - t, h - len, w, h, c);
    }

    private void Tab(GlyphAtlas a)
    {
        string text = Expand(_c.Tab);
        float tw = 0f;
        foreach (Rune r in text.EnumerateRunes()) tw += a.AdvanceOf(r);
        float x = St.TabX * _s, pad = St.TabPad * _s;
        Rect(x, -_v.TabH, x + tw + 2f * pad, 0f, St.TabBg);
        Words(text, x + pad, -_v.TabH + St.TabDrop * _s + a.Ascent, a, 1f, St.TabText != 0 ? St.TabText : St.Text, false);
    }

    private void Image(PanelImage i)
    {
        float w = i.W * _s, h = i.H * _s;
        float x = i.At switch { Anchor.TopRight or Anchor.BottomRight or Anchor.Right => _v.W - w, Anchor.Top or Anchor.Bottom or Anchor.Center => (_v.W - w) / 2f, _ => 0f };
        float y = i.At switch { Anchor.BottomLeft or Anchor.BottomRight or Anchor.Bottom => _v.H - h, Anchor.Left or Anchor.Right or Anchor.Center => (_v.H - h) / 2f, _ => 0f };
        AddIcon(Expand(i.Src), x + i.Dx * _s, y + i.Dy * _s, w, h, i.Alpha);
    }

    /// <summary>Lays <paramref name="blocks"/> top to bottom from <paramref name="y"/>; returns the y after the last.</summary>
    private float Stack(Block[] blocks, float x, float y, float w, float fullW, bool emit)
    {
        foreach (Block b in blocks) y = LayoutBlock(b, x, y, w, fullW, emit);
        return y;
    }

    private float LayoutBlock(Block b, float x, float y, float w, float fullW, bool emit)
    {
        float gap = (b.After ?? St.Gap) * _s;
        uint text = b.Color != 0 ? b.Color : St.Text;
        switch (b.Kind)
        {
            case BlockKind.Divider:
                float m = (b.Margin ?? St.DividerMargin) * _s, th = Math.Max(1f, (b.Thick ?? St.DividerThick) * _s);
                if (emit) Rect(0, y + m, fullW, y + m + th, b.Color != 0 ? b.Color : St.Divider);
                return y + 2f * m + th + (b.After is { } da ? da * _s : 0f);
            case BlockKind.Icons:
                return Icons(b, x, y, w, emit) + gap;
            case BlockKind.Section:
                float sp = (b.Pad ?? St.SectionPad) * _s;
                float end = Stack(b.Kids, x, y + sp, w, fullW, false) + sp;
                if (emit)
                {
                    if (b.Tint != 0) Rect(0, y, fullW, end, b.Tint);
                    Stack(b.Kids, x, y + sp, w, fullW, true);
                }
                return end + (b.After is { } sa ? sa * _s : 0f);
            case BlockKind.Footer:
                return Footer(b, x, y, w, fullW, emit) + (b.After is { } fa ? fa * _s : 0f);
            case BlockKind.Status:
                return Status(b, x, y, w, emit) + gap;
            default:
                _pulse = b.Pulse;
                float lh = Flow(b.Runs, x, y, w, b.Size, text, b.Align, emit);
                _pulse = false;
                return y + lh + (b.After ?? St.TextGap) * _s;
        }
    }

    private float Status(Block b, float x, float y, float w, bool emit)
    {
        float box = b.Icon.Length > 0 ? b.IconPx * _s : 0f, gap = box > 0f ? (b.Spacing ?? St.StatusGap) * _s : 0f;
        uint text = b.Color != 0 ? b.Color : St.Text;
        float h = Flow(b.Runs, x + box + gap, y, w - box - gap, b.Size, text, b.Align, false);
        float rowH = Math.Max(h, box);
        if (!emit) return y + rowH;
        if (box > 0f)
        {
            float iy = y + (rowH - box) / 2f, inset = b.Frame ? (b.Inset ?? St.FrameInset) * _s : 0f;
            if (b.Frame) FrameBox(b, x, iy, box);
            AddIcon(Expand(b.Icon), x + inset, iy + inset, box - 2f * inset, box - 2f * inset);
        }
        Flow(b.Runs, x + box + gap, y + (rowH - h) / 2f, w - box - gap, b.Size, text, b.Align, true);
        return y + rowH;
    }

    private void FrameBox(Block b, float x, float y, float box)
    {
        Rect(x, y, x + box, y + box, b.FrameFill != 0 ? b.FrameFill : St.FrameFill);
        Frame(x, y, x + box, y + box, b.FrameLine != 0 ? b.FrameLine : St.FrameLine, St.FrameW, "trbl");
    }

    private float Icons(Block b, float x, float y, float w, bool emit)
    {
        float box = b.IconPx * _s, gap = (b.Spacing ?? St.IconGap) * _s, cx = 0f, cy = 0f;
        foreach (string name in b.Icons)
        {
            if (cx > 0f && cx + box > w) { cx = 0f; cy += box + gap; }
            if (emit)
            {
                if (b.Frame) FrameBox(b, x + cx, y + cy, box);
                float in_ = b.Frame ? (b.Inset ?? St.FrameInset) * _s : 0f;
                AddIcon(Expand(name), x + cx + in_, y + cy + in_, box - 2f * in_, box - 2f * in_);
            }
            cx += box + gap;
        }
        return b.Icons.Length == 0 ? y : y + cy + box;
    }

    private float Footer(Block b, float x, float y, float w, float fullW, bool emit)
    {
        float pad = (b.Pad ?? St.FooterPad) * _s;
        uint rgba = b.Color != 0 ? b.Color : St.Text;
        float lw = b.Right.Length == 0 ? w : w * St.FooterSplit, rw = b.Runs.Length == 0 ? w : w * (1f - St.FooterSplit);
        float h = Math.Max(Flow(b.Runs, x, 0, lw, b.Size, rgba, 0, false), Flow(b.Right, x, 0, rw, b.Size, rgba, 1, false));
        if (emit)
        {
            Rect(0, y, fullW, y + h + 2f * pad, b.Tint != 0 ? b.Tint : St.FooterBand);
            Flow(b.Runs, x, y + pad, lw, b.Size, rgba, 0, true);
            Flow(b.Right, x + w - rw, y + pad, rw, b.Size, rgba, 1, true);
        }
        return y + h + 2f * pad;
    }

    /// <summary>Wraps <paramref name="runs"/> into <paramref name="w"/> from (<paramref name="x"/>, <paramref name="y"/>); returns the height.
    /// <paramref name="align"/> 0 is left, 1 right, 2 centre. A word wider than the width runs past it.</summary>
    private float Flow(Run[] runs, float x, float y, float w, float size, uint rgba, int align, bool emit)
    {
        if (runs.Length == 0) return 0f;
        _line.Clear();
        float cursor = 0f, top = y;
        (GlyphAtlas A, float K) b0 = _face(size);
        float blankAsc = b0.A.Ascent * b0.K, blankDesc = (b0.A.LineHeight - b0.A.Ascent) * b0.K;
        bool any = false;
        foreach (Run r in runs)
        {
            if (r.Break)
            {
                top += EndLine(x, top, w, align, emit, blankAsc, blankDesc);
                cursor = 0f; any = true;
                continue;
            }
            float mult = r.Size > 0f ? r.Size : size;
            var (a, k) = _face(mult);
            uint col = r.Rgba != 0 ? r.Rgba : rgba;
            float asc = a.Ascent * k, desc = (a.LineHeight - a.Ascent) * k;
            if (r.Icon.Length > 0)
            {
                float px = a.LineHeight * k;
                if (cursor > 0f && cursor + px > w) { top += EndLine(x, top, w, align, emit, blankAsc, blankDesc); cursor = 0f; }
                _line.Add(new Atom { Icon = Expand(r.Icon), X = cursor, W = px, Px = px, K = k, A = a, Asc = asc, Desc = desc, Rgba = col });
                cursor += px + a.SpaceAdvance * k;
                any = true;
                continue;
            }
            string text = Expand(r.Text);
            int i = 0;
            while (i < text.Length)
            {
                if (text[i] == ' ') { cursor += a.SpaceAdvance * k; i++; continue; }
                int j = text.IndexOf(' ', i);
                if (j < 0) j = text.Length;
                float ww = Measure(text, i, j, a) * k;
                if (cursor > 0f && cursor + ww > w) { top += EndLine(x, top, w, align, emit, blankAsc, blankDesc); cursor = 0f; }
                _line.Add(new Atom { Text = text, From = i, To = j, X = cursor, W = ww, K = k, A = a, Asc = asc, Desc = desc, Rgba = col, Italic = r.Italic });
                cursor += ww;
                i = j;
                any = true;
            }
        }
        if (_line.Count > 0 || !any) top += EndLine(x, top, w, align, emit, blankAsc, blankDesc);
        return top - y;
    }

    /// <summary>Finishes the current line at <paramref name="top"/>, drawing it when <paramref name="emit"/>; returns its height. A line with nothing on it is as tall as the block's own text.</summary>
    private float EndLine(float x, float top, float w, int align, bool emit, float blankAsc, float blankDesc)
    {
        float asc = blankAsc, desc = blankDesc, width = 0f;
        if (_line.Count > 0) { asc = desc = 0f; }
        foreach (Atom a in _line) { asc = Math.Max(asc, a.Asc); desc = Math.Max(desc, a.Desc); width = Math.Max(width, a.X + a.W); }
        if (emit)
        {
            float shift = align == 1 ? w - width : align == 2 ? (w - width) / 2f : 0f;
            float baseline = top + asc;
            foreach (Atom a in _line)
            {
                float px = x + Math.Max(0f, shift) + a.X;
                if (a.Icon != null) AddIcon(a.Icon, px, baseline - a.Asc, a.Px, a.Px);
                else Words(a.Text, px, baseline, a.A, a.K, a.Rgba, a.Italic, a.From, a.To);
            }
        }
        _line.Clear();
        return (asc + desc) * St.Leading;
    }

    private static float Measure(string s, int from, int to, GlyphAtlas a)
    {
        float w = 0f;
        foreach (Rune r in s.AsSpan(from, to - from).EnumerateRunes()) w += a.AdvanceOf(r);
        return w;
    }

    private void Words(string s, float x, float baseline, GlyphAtlas a, float k, uint rgba, bool italic, int from = 0, int to = -1)
    {
        if (to < 0) to = s.Length;
        float r = (rgba >> 24) / 255f, g = ((rgba >> 16) & 0xFF) / 255f, b = ((rgba >> 8) & 0xFF) / 255f, al = (rgba & 0xFF) / 255f;
        float pen = x;
        VertexBatch into = _v.Text;
        if (_pulse) into = _v.Pulse ??= new VertexBatch(a.SolidU, a.SolidV);
        uint sh = St.Shadow;
        float sr = (sh >> 24) / 255f, sg = ((sh >> 16) & 0xFF) / 255f, sb = ((sh >> 8) & 0xFF) / 255f, sa = (sh & 0xFF) / 255f * al;
        if (sh != 0)
        {
            float sp = x;
            foreach (Rune ru in s.AsSpan(from, to - from).EnumerateRunes())
            {
                if (a.TryGet(ru.Value, out Glyph gl))
                {
                    if (italic) into.GlyphItalic(gl, sp + St.ShadowDx * _s, baseline + St.ShadowDy * _s, sr, sg, sb, sa, St.Slant, k);
                    else into.Glyph(gl, sp + St.ShadowDx * _s, baseline + St.ShadowDy * _s, sr, sg, sb, sa, k);
                    sp += gl.Advance * k;
                }
                else sp += a.AdvanceOf(ru) * k;
            }
        }
        foreach (Rune ru in s.AsSpan(from, to - from).EnumerateRunes())
        {
            if (a.TryGet(ru.Value, out Glyph gl))
            {
                if (italic) into.GlyphItalic(gl, pen, baseline, r, g, b, al, St.Slant, k);
                else into.Glyph(gl, pen, baseline, r, g, b, al, k);
                pen += gl.Advance * k;
            }
            else pen += a.AdvanceOf(ru) * k;
        }
    }

    private void AddIcon(string src, float x, float y, float w, float h, float alpha = 1f)
    {
        uint item = 0;
        if (src.StartsWith("@item:", StringComparison.Ordinal))
        {
            int n = 0;
            while (6 + n < src.Length && n < 8 && Uri.IsHexDigit(src[6 + n])) n++;
            if (n == 0 || !uint.TryParse(src.AsSpan(6, n), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out item)) return;
        }
        else if (PanelLayout.IsLook(src))
        {
            _v.Icons.Add(new IconDraw { Src = "", Look = src, X = x, Y = y, W = w, H = h, Alpha = alpha });
            return;
        }
        else if (PanelLayout.ModelKey(src) is { } model)
        {
            _v.Icons.Add(new IconDraw { Src = "", Model = model, X = x, Y = y, W = w, H = h, Alpha = alpha });
            return;
        }
        else if (src.StartsWith('@')) return;
        _v.Icons.Add(new IconDraw { Src = item == 0 ? src : "", Item = item, X = x, Y = y, W = w, H = h, Alpha = alpha });
    }

    private void Rect(float x0, float y0, float x1, float y1, uint rgba)
        => _v.Back.Rect(x0, y0, x1, y1, (rgba >> 24) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    private void Frame(float x0, float y0, float x1, float y1, uint rgba, float width, string sides)
    {
        float t = Math.Max(1f, width * _s);
        bool top = sides.Contains('t'), bot = sides.Contains('b'), left = sides.Contains('l'), right = sides.Contains('r');
        if (top) Rect(x0, y0, x1, y0 + t, rgba);
        if (bot) Rect(x0, y1 - t, x1, y1, rgba);
        float ya = top ? y0 + t : y0, yb = bot ? y1 - t : y1;
        if (left) Rect(x0, ya, x0 + t, yb, rgba);
        if (right) Rect(x1 - t, ya, x1, yb, rgba);
    }

    private static (float, float, float, float) C(uint rgba) => ((rgba >> 24) / 255f, ((rgba >> 16) & 0xFF) / 255f, ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);

    private void Gradient(float x0, float y0, float x1, float y1, uint tl, uint tr, uint br, uint bl)
        => _v.Back.GradientRect(x0, y0, x1, y1, C(tl), C(tr), C(br), C(bl));

    private string Expand(string s)
    {
        if (s.IndexOf('{') < 0) return s;
        return s.Replace("{item.name}", _subject.Name, StringComparison.Ordinal).Replace("{item.stack}", _subject.Stack, StringComparison.Ordinal);
    }
}
