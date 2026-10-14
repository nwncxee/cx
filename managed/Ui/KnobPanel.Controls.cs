using static CX.Ui.PanelChrome;

namespace CX.Ui;

/// <summary>The panel's individual controls: the off-default dot, a list item row, a slider and one nav item. Each
/// draws the game's own art when the panel handed it an armed <see cref="Skin"/>, else the themed flat look.</summary>
public sealed partial class KnobPanel
{
    /// <summary>The off-default dot: a small filled square centred on (<paramref name="cx"/>, <paramref name="cy"/>),
    /// drawn on a row whose value left its default and on a submenu holding one.</summary>
    private void Dot(VertexBatch b, float cx, float cy, float a = 1f)
        => Fill(b, cx - DotR, cy - DotR, cx + DotR, cy + DotR, Accent2, a);

    /// <summary>A list item row: its text box (the edit text, tail in view, while editing; the placeholder on an empty
    /// add box) and the remove ×; the add box closes the list with the row separator.</summary>
    private void Item(VertexBatch b, Skin? skin, GlyphAtlas body, Row r, float top, float colRight)
    {
        float em = Em, a = Greyed(r) ? OffAlpha : 1f;
        ItemRect(r, top, out float x0, out float x1, out float y0, out float y1);
        bool editing = EditingRow(r);
        if (!Art(skin, NativeChrome.Edit, x0, y0, x1, y1, a))
        {
            Fill(b, x0, y0, x1, y1, Input, a);
            Outline(b, x0, y0, x1, y1, editing ? Accent2 : Frame2, a);
        }
        float tx0 = x0 + 0.4f * em, clip = x1 - 0.3f * em, baseY = (y0 + y1) * 0.5f - body.LineHeight * 0.5f + body.Ascent;
        if (editing)
        {
            float end = UiText.Draw(b, body, _editText, UiText.TailFrom(body, _editText, clip - tx0 - 2f), tx0, baseY, Tx, clip, a);
            Fill(b, end, y0 + 0.2f * em, end + 1.5f, y1 - 0.2f * em, Hi);
        }
        else if (r.Value.Length > 0) UiText.Draw(b, body, r.Value, tx0, baseY, Tx, clip, a);
        else UiText.Draw(b, body, r.Text, tx0, baseY, Tx3, clip, a);
        if (r.Adds)
        {
            Fill(b, List.X + Pad, top + r.H - 1f, colRight, top + r.H, 0xFFFFFF, 0.03f);
            return;
        }
        RemoveRect(r, top, out float rx0, out float rx1, out float ry0, out float ry1);
        bool hover = ReferenceEquals(r, _hoverRow);
        if (!Art(skin, hover ? NativeChrome.ButtonHot : NativeChrome.Button, rx0, ry0, rx1, ry1, a))
            Outline(b, rx0, ry0, rx1, ry1, hover ? Accent2 : Frame2, a);
        UiText.Draw(b, body, "×", rx0 + (rx1 - rx0 - UiText.Measure(body, "×")) * 0.5f, baseY, hover ? Hi : Tx2, rx1, a);
    }

    /// <summary>A slider control (a knob row or a channel row): track, fill, grip, the value box (the edit text
    /// while editing) and the unit.</summary>
    private void Slider(VertexBatch b, Skin? skin, GlyphAtlas body, GlyphAtlas small, Row r, float top, float cy, string unit, float alpha)
    {
        float em = Em;
        TrackRect(r, top, out float sx0, out float sx1, out _);
        float th = 0.35f * em, knob = sx0 + r.Norm * (sx1 - sx0), kh = 0.55f * em;
        // NUI's slider is a flat rail filled in its border gold (the native Accent) under the nui_slider cursor. The
        // art flushes before this batch, so the rail stops at the cursor's edges instead of covering it.
        float y0 = cy - th * 0.5f, y1 = cy + th * 0.5f;
        if (skin != null)
        {
            float kw = kh * NativeChrome.SliderAspect;
            if (knob - kw > sx0) Fill(b, sx0, y0, knob - kw, y1, Accent, alpha);
            if (knob + kw < sx1) { Fill(b, knob + kw, y0, sx1, y1, Track, alpha); Outline(b, knob + kw, y0, sx1, y1, Frame2, alpha); }
            Art(skin, NativeChrome.Slider, knob - kw, cy - kh, knob + kw, cy + kh, alpha);
        }
        else
        {
            Fill(b, sx0, y0, sx1, y1, Track, alpha);
            Outline(b, sx0, y0, sx1, y1, Frame2, alpha);
            Fill(b, sx0, y0, knob, y1, Accent, alpha);
            Fill(b, knob - 0.375f * em, cy - kh, knob + 0.375f * em, cy + kh, Grip, alpha);
        }
        ValueRect(r, top, out float vx0, out float vx1, out float vy0, out float vy1);
        bool editing = EditingRow(r);
        if (!Art(skin, NativeChrome.Edit, vx0, vy0, vx1, vy1, alpha))
        {
            Fill(b, vx0, vy0, vx1, vy1, Input, alpha);
            Outline(b, vx0, vy0, vx1, vy1, editing ? Accent2 : Frame2, alpha);
        }
        string vtext = editing ? _editText : r.Value;
        float vbase = cy - body.LineHeight * 0.5f + body.Ascent;
        float vw = UiText.Measure(body, vtext);
        float vx = editing ? vx0 + 0.3f * em : vx1 - 0.3f * em - vw;
        if (vx < vx0 + 0.2f * em) vx = vx0 + 0.2f * em;
        float vend = UiText.Draw(b, body, vtext, vx, vbase, Tx, vx1 - 0.2f * em, alpha);
        if (editing) Fill(b, vend, vy0 + 0.2f * em, vend + 1.5f, vy1 - 0.2f * em, Hi);
        if (unit.Length > 0) UiText.Draw(b, small, unit, vx1 + 0.3f * em, cy - small.LineHeight * 0.5f + small.Ascent, Tx3, vx1 + 0.3f * em + UnitW, alpha);
    }

    /// <summary>One nav item: the selected one gets the filled band and the accent bar, a hovered one a faint band,
    /// a submenu with an off-default row its dot after the name.</summary>
    private void Nav(VertexBatch b, GlyphAtlas body, ScrollColumn col, int i, string text, bool on, bool hover, bool dot)
    {
        float top = NavTop(col, i), x0 = col.X, x1 = col.X + col.W - (col.Overflows ? col.Width : 0f);
        if (top + NavH <= col.Y || top >= col.Y + col.H) return;
        // The band stays a fill: the column clips the batch, and the native palette carries nui_header's own blue.
        if (on) { Fill(b, x0, top, x1, top + NavH, NavOn); Fill(b, x0, top, x0 + 3f, top + NavH, Accent2); }
        else if (hover) Fill(b, x0, top, x1, top + NavH, 0xFFFFFF, 0.03f);
        float clip = x1 - 0.3f * Em - (dot ? 0.5f * Em + 2f * DotR : 0f);
        float pen = UiText.Draw(b, body, text, x0 + NavPadX, top + (NavH - body.LineHeight) * 0.5f + body.Ascent, on ? Hi : Tx2, clip);
        if (dot) Dot(b, pen + 0.5f * Em + DotR, top + NavH * 0.5f);
    }
}
