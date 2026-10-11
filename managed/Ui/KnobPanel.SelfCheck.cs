using System;
using System.Collections.Generic;
using System.Diagnostics;
using CX.Framework;
using static CX.Ui.PanelChrome;

namespace CX.Ui;

public sealed partial class KnobPanel
{
    /// <summary>Off-device proof: the tree (categories, submenus, rows without ChatOnly/Hidden, section headers),
    /// each control's set through the registry, the per-motion slider apply, the text edit, Escape, the three
    /// independent scrolls, geometry that fits 1280×720 and 1920×1080 with no clipped label; then the semantics --
    /// boxed master, greying, Advanced, Reset, badges, link copy, confirm, swatch channels, lists -- and a zero-alloc draw.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var body = new GlyphAtlas { LineHeight = 24, Ascent = 19, SpaceAdvance = 6f };
        var small = new GlyphAtlas { LineHeight = 19, Ascent = 15, SpaceAdvance = 5f };
        for (int cp = 0x20; cp < 0x180; cp++) { body.SetTestGlyph(cp, cp == ' ' ? 6f : 11f); small.SetTestGlyph(cp, cp == ' ' ? 5f : 8f); }
        foreach (int cp in new[] { 0x2014, 0x21BB, 0x25B8, 0x25BE }) { body.SetTestGlyph(cp, 11f); small.SetTestGlyph(cp, 8f); }

        long now = 0;
        Func<long> realClock = Clock, realTipClock = Tooltip.Clock;
        Func<string, bool> realCopy = CopyText;
        var realTiming = Tooltip.Timing;
        Tooltip.Timing = () => (Tooltip.DelaySec, Tooltip.FadeInSec, Tooltip.FadeOutSec);
        string copied = "";
        Clock = () => now; Tooltip.Clock = () => now;
        CopyText = s => { copied = s; return true; };
        try
        {
            // A throwaway parent group and a child group under it, in a category that has nothing else: every row
            // kind, every tier, a Restart row, a link, a confirming button, an expanded swatch.
            int pressed = 0, changed = 0, wiped = 0, statusN = 0;
            var pg = new KnobGroup("cx-panelparent", "Controls", "Parent", "Enable the parent", "The parent master.", true,
                                   Effect.Restart, null, null, "", register: false);
            var g = new KnobGroup("cx-panelcheck", "Controls", "Panel check", "Enable the check", "The master row.", true,
                                  Effect.Live, null, "cx-panelparent.enabled", "A header line for the note row.", register: false);
            g.PanelStatus = () => $"Running: check — {statusN}";
            g.Section("Field");
            var dens = g.Slider("density", "Density", 0f, 4f, 0.25f, 2f, help: "Blades per patch relative to the stock game, a help line long enough to wrap once at least.", changed: () => changed++);
            var far = g.Slider("far", "Distance", 0, 400, 10, 0, "m", help: "How far grass draws.");
            g.Section("Shadows");
            var shadow = g.Dropdown("shadow", "Grass casts shadows", new[] { 0, 1, 2, 4 }, 1, new[] { "none", "1", "2", "4" }, help: "Cascades.");
            var tex = g.Text("bordertex", "Border grass textures", "grass", help: "Comma-separated texture names.");
            var col = g.Swatch("col", "Colour", 0x11223344u, help: "A swatch.");
            var size = g.Dropdown("size", "Sun shadow resolution", new[] { 512, 1024, 2048 }, 2048, effect: Effect.Restart, help: "Per-cascade map size.");
            var adv = g.Slider("lod", "Full-density radius", 5f, 200f, 1f, 40f, "m", Tier.Advanced, help: "Every blade draws inside this.");
            var advNoHelp = g.Slider("lodmin", "Far density floor", 0.001f, 1f, 0.001f, 0.02f, tier: Tier.Advanced);
            var btn = g.Button("dump", "Dump next frame", () => { pressed++; statusN++; }, help: "Writes the frame's pass images.");
            var wipe = g.Button("wipe", "Forget every server", () => wiped++, help: "Click again to confirm.", confirm: true);
            bool inSession = true; int bound = 0;
            var bind = g.Button("bind", "Bind keys on this server…", () => bound++, help: "Greyed outside a server session.");
            bind.Enabled = () => inSession;
            var link = g.Link("site", "Releases", "https://example.invalid/releases");
            var face = g.Dropdown("typeface", "Typeface", new[] { "default" }, "default", help: "One choice until multi-font.");
            face.Disabled = true;
            var many = g.Dropdown("many", "Many", new[] { "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet" }, "alpha", help: "Ten choices: the list searches.");
            var chat = g.Toggle("prepass", "prepass", true, Tier.ChatOnly);
            g.Swatch("hidden", "hidden", 0u, Tier.Hidden);

            var p = new KnobPanel();
            p.ResolveParent = grp => ReferenceEquals(grp, g) ? pg.Master : null;
            // The registry is live: point the panel at the throwaway group directly (it is not enumerated).
            p.Category = Array.IndexOf(KnobCategories, "Controls");
            p.OpenAt(1280f, 720f, FacePx, body, small);
            p._subs = new[] { new SubMenu(g) }; p.Sub = 0; p.LayoutRows();
            check.Expect(p.Open && MathF.Abs(p.W - 896f) < 0.5f && MathF.Abs(p.H - 540f) < 0.5f, $"70%×75% of 1280×720 ({p.W}×{p.H})");
            check.Expect(MathF.Abs(p.X - 192f) < 0.5f && MathF.Abs(p.Y - 90f) < 0.5f, "centred");
            check.Expect(p.Cats.W < p.SubCol.W && p.SubCol.W < p.List.W && MathF.Abs(p.Cats.W + p.SubCol.W + p.List.W - p.W) < 0.5f, "columns split 18/22/60 and fill the width");

            Row R(Knob k) { foreach (Row r in p.Rows) if (r.Kind == RowKind.Knob && ReferenceEquals(r.K, k)) return r; throw new InvalidOperationException(k.Key + " has no row"); }
            int Count(RowKind kind) { int n = 0; foreach (Row r in p.Rows) if (r.Kind == kind) n++; return n; }
            bool Has(Knob k) { foreach (Row r in p.Rows) if (ReferenceEquals(r.K, k)) return true; return false; }
            Row? Find(RowKind kind) { foreach (Row r in p.Rows) if (r.Kind == kind) return r; return null; }
            float right = p.List.X + p.List.W - p.Pad - ScrollColumn.BarW;
            void Show(Row r) { p.List.Offset = r.Top - 0.4f * p.Em; p.List.Clamp(); }
            void Press(float mx, float my) { p.MouseDown(mx, my); p.MouseUp(); }
            void PressBox(Row r) { Show(r); p.BoxRect(r, p.List.ToScreen(r.Top), out float x0, out float x1, out float y0, out float y1); Press((x0 + x1) * 0.5f, (y0 + y1) * 0.5f); }
            void PressToggle(Row r) { Show(r); p.ControlAnchor(r, p.List.ToScreen(r.Top), out float rr, out float cy); Press(rr - 5f, cy); }

            // The tree: header note, status, the boxed master first, sections, User rows in order, the closed disclosure; no Advanced/ChatOnly/Hidden row.
            check.Expect(p.Rows[0].Kind == RowKind.Note && p.Rows[1].Kind == RowKind.Status && p.Rows[1].Help.Length == 1 && p.Rows[1].Help[0] == "Running: check — 0", "the header note, then the status line");
            check.Expect(p.Rows[2].Kind == RowKind.Knob && p.Rows[2].K == g.Master && p.Rows[2].Master && p.Rows[2].H > 3.3f * FacePx + 0.8f * FacePx, "the master is the first knob row, boxed and taller by its margins");
            check.Expect(p.Rows[3].Kind == RowKind.Section && p.Rows[3].Text == "FIELD" && p.Rows[6].Text == "SHADOWS", "section headers appear where the section changes, upper-cased");
            check.Expect(Count(RowKind.Knob) == 13 && Count(RowKind.Advanced) == 1 && !Has(adv) && !Has(advNoHelp) && !Has(chat), $"13 User rows, one closed disclosure, no Advanced / ChatOnly / Hidden row ({Count(RowKind.Knob)})");
            check.Expect(p.Rows[p.Rows.Count - 1].Kind == RowKind.Advanced && p.Rows[p.Rows.Count - 1].Text == AdvancedClosed, "the disclosure is the last row, closed");
            check.Expect(Find(RowKind.Strip) == null && !p.MasterOff, "parent and master on: no strip, nothing greyed");
            Row dr = R(dens);
            check.Expect(p.Greyed(R(face)) && !p.Greyed(dr), "a Disabled knob's row is greyed while everything else is live");
            PressBox(R(face));
            check.Expect(!p.Drop.Open, "a press on the disabled dropdown opens nothing");
            check.Expect(dr.Help.Length >= 2 && dr.H > 3.3f * FacePx, "a long help wraps under the label and the row grows");
            check.Expect(dr.Value == "2" && MathF.Abs(dr.Norm - 0.5f) < 0.001f && !dr.OffDefault, "the slider row caches its value text, knob position and default state");
            Row sr = R(size);
            check.Expect(sr.Restart && !sr.Moved && sr.ExtraW > 0f && !R(btn).Restart, "a Restart row reserves the badge; a button has none");
            Row lr = R(link);
            check.Expect(lr.Help.Length >= 1 && lr.Help[0].StartsWith("https://") && lr.Label.Length == 1 && lr.Label[0] == "Releases", "a link row shows its label with the address beneath");

            // No clipped text: every label fits left of its control, every help line fits, at both sizes.
            void FitCheck(string at)
            {
                foreach (Row r in p.Rows)
                {
                    if (r.Kind != RowKind.Knob) continue;
                    float lw = p.LabelW(r);
                    foreach (string l in r.Label) check.Expect(UiText.Measure(body, l) <= lw + 0.5f, $"{at}: label line '{l}' fits left of its control");
                    foreach (string l in r.Help) check.Expect(UiText.Measure(small, l) <= lw + r.ExtraW + 0.5f, $"{at}: help line fits left of the control");
                    check.Expect(r.H >= 0.8f * p.Em + r.Label.Length * body.LineHeight + (r.Help.Length > 0 ? r.Help.Length * small.LineHeight : 0f), $"{at}: the row is tall enough for its lines");
                }
            }
            FitCheck("720p");

            // Toggle: press the master's pill -> the master goes off, every value row greys, the action and link rows do not.
            Row mr = R(g.Master!);
            PressToggle(mr);
            check.Expect(!g.Master!.V && p.MasterOff && p.Greyed(dr) && p.Greyed(sr) && !p.Greyed(mr) && !p.Greyed(R(btn)) && !p.Greyed(lr), "pressing the master toggle flips it; value rows grey, the master, the button and the link stay live");
            mr.Refresh();
            check.Expect(mr.OffDefault && SubDirty(g), "the master left its default: the row and the submenu carry the dot");
            // A greyed value row is inert; the greyed submenu's button still fires.
            Show(dr); p.TrackRect(dr, p.List.ToScreen(dr.Top), out float tx0, out float tx1, out float tcy);
            Press(tx1 - 1f, tcy);
            check.Expect(dens.V == 2f, "a press on a greyed slider changes nothing");
            PressBox(R(btn));
            check.Expect(pressed == 1, "the action row fires under an off master");
            check.Expect(p.Rows[1].Help[0] == "Running: check — 1", "a press that changed the status line re-laid it out");
            PressToggle(R(g.Master!));
            check.Expect(g.Master.V && !p.MasterOff && !p.Greyed(dr), "the master back on: rows live again");
            check.Expect(Find(RowKind.Strip) == null, "the group's own master never lays the strip");

            // Parent off: the strip above the rows, every value row greyed, the master too is a value row of ITS group but stays live here.
            pg.Master!.Set(false);
            p.LayoutRows();
            Row? strip = Find(RowKind.Strip);
            check.Expect(strip != null && p.Rows[0] == strip && strip.Help.Length >= 1 && strip.Text.StartsWith("Enable the parent is off") && strip.Text.Contains("Controls » Parent (restart)"), $"an off ancestor lays the strip first, naming it and where to turn it on ({strip?.Text})");
            check.Expect(p.MasterOff && p.Greyed(R(dens)) && !p.Greyed(R(g.Master!)) && !p.Greyed(R(btn)) && !p.Greyed(R(link)), "under an off ancestor the value rows grey; master, button and link stay live");
            PressBox(R(btn));
            check.Expect(pressed == 2, "the button fires under an off ancestor");
            pg.Master.Set(true);
            // The parent flipped without the panel: the stale strip goes on the next press.
            check.Expect(Find(RowKind.Strip) != null, "the strip is laid until the next input");
            p.List.Reset(); Press(p.List.X + 5f, p.List.Y + 2f);
            check.Expect(Find(RowKind.Strip) == null && !p.MasterOff, "a press re-lays the rows once the ancestor is back on");

            // Slider: press the track at its right end, then drag to the middle: two sets, applied per motion.
            dr = R(dens); Show(dr);
            p.TrackRect(dr, p.List.ToScreen(dr.Top), out tx0, out tx1, out tcy);
            int before = changed;
            check.Expect(p.MouseDown(tx1 - 1f, tcy) && dens.V == 4f && changed == before + 1, $"a track press sets the value under the pointer ({dens.V})");
            p.MouseMove((tx0 + tx1) * 0.5f, tcy);
            check.Expect(MathF.Abs(dens.V - 2f) < 0.01f && changed == before + 2, $"dragging applies per motion, snapped to the step ({dens.V})");
            p.MouseMove(tx0 - 50f, tcy);
            check.Expect(dens.V == 0f && p.MouseUp(), "the drag clamps at the range and release ends it");
            dr.Refresh();
            check.Expect(dr.OffDefault && SubDirty(g), "an off-default slider carries the dot and marks the submenu");
            // Int slider snaps to whole steps.
            Row fr = R(far); Show(fr); p.TrackRect(fr, p.List.ToScreen(fr.Top), out float fx0, out float fx1, out float fcy);
            Press(fx0 + (fx1 - fx0) * 0.5f, fcy);
            check.Expect(far.V == 200 && fr.K!.Format() == "200", $"an int slider snaps to its step ({far.V})");
            // Value box: type an exact value.
            Show(dr); p.ValueRect(dr, p.List.ToScreen(dr.Top), out float vx0, out float vx1, out float vy0, out float vy1);
            Press((vx0 + vx1) * 0.5f, (vy0 + vy1) * 0.5f);
            check.Expect(p.Editing && p.EditingRow(dr), "a value-box press starts an edit");
            p.EditInsert("3.5"); p.Key(0x0D);
            check.Expect(!p.Editing && dens.V == 3.5f, $"Enter commits the typed value through Parse ({dens.V})");
            Press((vx0 + vx1) * 0.5f, (vy0 + vy1) * 0.5f); p.EditInsert("9"); p.Key(0x1B);
            check.Expect(!p.Editing && dens.V == 3.5f && p.Open, "Escape cancels the edit and leaves the panel open");
            // Dropdown: open, pick the last.
            Row shr = R(shadow); PressBox(shr);
            check.Expect(p.Drop.Open && ReferenceEquals(p.Drop.Tag, shr) && p.Drop.Selected == 1, "the dropdown press opens the list on the current choice");
            Press(p.Drop.X + 5f, p.Drop.ItemTop(3) + 2f);
            check.Expect(!p.Drop.Open && shadow.V == 4, $"picking the last choice sets its value ({shadow.V})");
            check.Expect(!p.Drop.Searching, "four choices: no search row");
            // a long list opens searchable; typed text filters it and Enter picks the one match.
            Row manyRow = R(many); PressBox(manyRow);
            check.Expect(p.Drop.Open && p.Drop.Searching && p.Editing, "ten choices open with the search row, and the panel wants text");
            p.EditInsert("JU");
            check.Expect(p.Drop.Shown == 1 && p.Drop.Filter == "JU", $"typing filters the list, case-folded ({p.Drop.Shown})");
            p.Key(0x08);
            check.Expect(p.Drop.Filter == "J" && p.Drop.Shown == 1, "backspace edits the filter, not a field");
            p.Key(0x0D);
            check.Expect(!p.Drop.Open && !p.Editing && many.V == "juliet", $"Enter picks the one match ({many.V})");
            PressBox(manyRow); p.EditInsert("l");
            check.Expect(p.Drop.Shown == 6 && p.Drop.ChoiceAt(0) == 0 && p.Drop.ChoiceAt(1) == 2, "'l' keeps the six choices with an l, alpha first");
            Press(p.Drop.X + 5f, p.Drop.List.Y + p.Drop.ItemH + 2f);
            check.Expect(!p.Drop.Open && many.V == "charlie", $"the second visible row is the second choice with an l ({many.V})");
            // Text: seeded with the value, edited, committed.
            Row tr = R(tex); PressBox(tr);
            check.Expect(p.Editing && p.EditText == "grass", "a text press seeds the edit with the value");
            p.EditBackspace(); p.EditInsert("y,moss"); p.Key(0x40000058);
            check.Expect(tex.V == "grasy,moss", $"KP_Enter commits the text ({tex.V})");
            // Swatch: hex edit, then the colour box expands four channel rows in place; a channel drag sets its byte.
            Row cr = R(col); PressBox(cr);
            p.EditBackspace(); p.EditBackspace(); p.EditInsert("ff"); p.Key(0x0D);
            check.Expect(col.V == 0x112233ffu, $"the swatch hex box edits the colour ({col.V:x8})");
            Show(cr); p.SwatchRect(cr, p.List.ToScreen(cr.Top), out float swx0, out float swx1, out float swy0, out float swy1);
            Press((swx0 + swx1) * 0.5f, (swy0 + swy1) * 0.5f);
            cr = R(col);
            int ci = p._rows.IndexOf(cr);
            check.Expect(Count(RowKind.Channel) == 4 && p.Rows[ci + 1].Kind == RowKind.Channel && p.Rows[ci + 1].Channel == 0 && p.Rows[ci + 4].Channel == 3 && p.Rows[ci + 4].Text == "A", "the colour box expands R/G/B/A channel rows right under the swatch");
            Row gch = p.Rows[ci + 2];
            check.Expect(gch.Byte == 0x22 && gch.Value == "34" && MathF.Abs(gch.Norm - 0x22 / 255f) < 0.001f, $"a channel row caches its byte ({gch.Value})");
            Show(gch); p.TrackRect(gch, p.List.ToScreen(gch.Top), out float gx0, out float gx1, out float gcy);
            Press(gx1 + 2f, gcy);
            check.Expect(col.V == 0x11ff33ffu, $"dragging the G channel to its end sets that byte only ({col.V:x8})");
            Show(gch); p.ValueRect(gch, p.List.ToScreen(gch.Top), out float gv0, out float gv1, out float gy0, out float gy1);
            Press((gv0 + gv1) * 0.5f, (gy0 + gy1) * 0.5f); p.EditInsert("16"); p.Key(0x0D);
            check.Expect(col.V == 0x111033ffu, $"a channel value box takes 0-255 ({col.V:x8})");
            Show(cr); p.SwatchRect(cr, p.List.ToScreen(cr.Top), out swx0, out swx1, out swy0, out swy1);
            Press((swx0 + swx1) * 0.5f, (swy0 + swy1) * 0.5f);
            check.Expect(Count(RowKind.Channel) == 0, "a second press on the colour box folds the channels");

            // Restart badge: a Restart dropdown moved off its boot value shows amber; back on it, plain.
            sr = R(size); PressBox(sr);
            Press(p.Drop.X + 5f, p.Drop.ItemTop(0) + 2f);
            sr.Refresh();
            check.Expect(size.V == 512 && sr.Moved && sr.OffDefault && size.Boot == 2048, "a Restart row whose value left Boot is amber-badged");
            size.Set(2048); sr.Refresh();
            check.Expect(!sr.Moved && !sr.OffDefault, "back at the boot value the badge is plain again");

            // Link: a press copies the address and flashes "copied" for a while.
            lr = R(link); Show(lr);
            Press(p.List.X + p.Pad + 5f, p.List.ToScreen(lr.Top) + 5f);
            check.Expect(copied == "https://example.invalid/releases" && p.CopiedShown(lr), "a link row press copies its address and flashes");
            now += (long)((CopiedSec + 0.1f) * Stopwatch.Frequency);
            check.Expect(!p.CopiedShown(lr), "the flash ends after CopiedSec");
            check.Expect(g.Master.V && !p.MasterOff, "the link press set nothing");

            // Two-click confirm: the first press arms, the second within 3 s fires, a late second press re-arms only.
            Row wr = R(wipe);
            PressBox(wr);
            check.Expect(wiped == 0 && p.ConfirmArmed(wr), "the first press on a confirming button arms it and fires nothing");
            now += (long)(1f * Stopwatch.Frequency);
            PressBox(wr);
            check.Expect(wiped == 1 && !p.ConfirmArmed(wr), "the second press within 3 s fires");
            PressBox(wr);
            now += (long)((KnobButton.ConfirmSec + 0.1f) * Stopwatch.Frequency);
            check.Expect(!p.ConfirmArmed(wr), "the arm expires after 3 s");
            PressBox(wr);
            check.Expect(wiped == 1 && p.ConfirmArmed(wr), "a press after the window re-arms rather than fires");
            PressBox(R(btn));
            check.Expect(!p.ConfirmArmed(wr) && pressed == 3, "a press elsewhere disarms");

            // A button's own Enabled predicate: false greys the row and swallows the press, true fires it.
            inSession = false;
            check.Expect(p.Greyed(R(bind)) && !p.Greyed(R(btn)), "a button whose Enabled says no is greyed; one without stays live");
            PressBox(R(bind));
            check.Expect(bound == 0, "a press on a greyed button fires nothing");
            inSession = true;
            PressBox(R(bind));
            check.Expect(!p.Greyed(R(bind)) && bound == 1, "Enabled back on: the row is live and the press fires");

            // Advanced: hidden until disclosed; disclosed rows are compact; the marker only where there is help; hover shows the tooltip after the dwell.
            Row advRow = p.Rows[p.Rows.Count - 1]; Show(advRow);
            Press(p.List.X + p.Pad + 5f, p.List.ToScreen(advRow.Top) + advRow.H - 5f);
            check.Expect(p.AdvOpen && Has(adv) && Has(advNoHelp) && !Has(chat), "pressing the disclosure reveals the Advanced rows and nothing else");
            Row ar = R(adv), anr = R(advNoHelp);
            check.Expect(ar.Adv && ar.HasMark && ar.Help.Length == 0 && ar.H < 3.3f * FacePx, "an Advanced row with help is compact, its help behind the marker");
            check.Expect(anr.Adv && !anr.HasMark && anr.ExtraW == 0f, "an Advanced row without help has no marker");
            check.Expect(ar.Label.Length == 2, $"a long Advanced label still wraps at 720p ({ar.Label.Length})");
            Show(ar);
            float hx = p.MarkX(ar) + 2f, hy = p.List.ToScreen(ar.Top) + 0.6f * p.Em;
            p.MouseMove(hx, hy);
            check.Expect(p.Tip.Hovering && ReferenceEquals(p.Tip.Key, ar) && !p.Tip.Visible, "hovering the marker starts the tooltip dwell");
            now += (long)((Tooltip.DelaySec + 0.05f) * Stopwatch.Frequency);
            check.Expect(p.Tip.Visible && p.Tip.Text == "Every blade draws inside this.", "after the dwell the row's help shows");
            p.MouseMove(hx - 5f * p.Em, hy);
            check.Expect(!p.Tip.Hovering, "leaving the marker hides it");
            foreach (Row r in p.Rows) if (r.Adv) { Show(r); p.MouseMove(p.MarkX(r) + 2f, p.List.ToScreen(r.Top) + 0.6f * p.Em); check.Expect(p.Tip.Hovering == r.HasMark, "only a marked row hovers"); }
            p.MouseMove(1f, 1f);
            FitCheck("720p advanced");
            // Reset to defaults: User + Advanced rows back, ChatOnly and Hidden untouched, the dots gone.
            adv.Set(90f); chat.Set(false);
            Knob hidden = g.Get("hidden")!; hidden.Parse("deadbeef");
            check.Expect(SubDirty(g) && R(dens).OffDefault, "off-default rows before the reset");
            p.ResetRect(out float rx0, out float rx1, out float ry0, out float ry1);
            Press((rx0 + rx1) * 0.5f, (ry0 + ry1) * 0.5f);
            check.Expect(dens.IsDefault && far.IsDefault && shadow.IsDefault && tex.IsDefault && col.IsDefault && adv.IsDefault && size.IsDefault, "Reset restores every User and Advanced row");
            check.Expect(!chat.V && hidden.Format() == "deadbeef", "Reset leaves ChatOnly and Hidden keys alone");
            foreach (Row r in p.Rows) if (r.Kind == RowKind.Knob) { r.Refresh(); check.Expect(!r.OffDefault, $"{r.K!.Key}: no dot after the reset"); }
            check.Expect(!SubDirty(g), "the submenu dot is gone after the reset");
            chat.Set(true); hidden.Parse("00000000");
            check.Expect(p.Rows[1].Help[0] == "Running: check — 3", "the status line follows the presses");

            // A submenu change closes the disclosure; a status-only group is its status.
            var z = new KnobGroup("cx-panelzero", "Controls", "Zero", null, null, true, Effect.Live, null, null, "", register: false);
            z.PanelStatus = () => "line one\nline two that is long enough to wrap in the column at seven twenty by twelve eighty for sure";
            p._subs = new[] { new SubMenu(g), new SubMenu(z) }; p.SelectSub(1); p.LayoutRows();
            check.Expect(p.Sub == 1 && !p.AdvOpen && p.Rows.Count == 1 && p.Rows[0].Kind == RowKind.Status && p.Rows[0].Help.Length >= 3 && !p._resettable, "a zero-knob group renders its status as the whole body, no reset box");
            // A shared submenu: each group under its heading, greyed by its own gate only; a chat-only master that is
            // off lays a strip that turns it back on; a diagnostics-only group earns no submenu.
            var sa = new KnobGroup("cx-sharea", "Controls", "Shared", "Enable A", "A's master.", true, Effect.Live, null, null, "", register: false, title: "Alpha");
            var aRow = sa.Toggle("x", "A row", true, help: "A help.");
            var sb = new KnobGroup("cx-shareb", "Controls", "Shared", "Enable B", null, false, Effect.Live, null, null, "", register: false,
                                   masterTier: Tier.ChatOnly, title: "Bravo");
            var bRow = sb.Toggle("y", "B row", true, help: "B help.");
            var diag = new KnobGroup("cx-sharediag", "Controls", "Diag", null, null, true, Effect.Live, null, null, "", register: false);
            diag.Toggle("probe", "probe", false, Tier.ChatOnly); diag.Status = () => "counters";
            check.Expect(Shown(sa) && Shown(sb) && !Shown(diag), "a group with rows is shown; a ChatOnly-and-Status group is not");
            p._subs = new[] { new SubMenu(sa, sb) }; p.SelectSub(0); p.LayoutRows();
            check.Expect(Count(RowKind.Heading) == 2 && p.Rows[0].Kind == RowKind.Heading && p.Rows[0].Text == "Alpha", "a shared submenu heads each group with its title");
            check.Expect(!p.Rows.Exists(r => r.Kind == RowKind.Knob && ReferenceEquals(r.K, sb.Master)) && !p.Greyed(R(aRow)) && p.Greyed(R(bRow)), "B's chat-only master is no row; off, it greys B's rows only");
            Row? bStrip = Find(RowKind.Strip);
            check.Expect(bStrip != null && bStrip.K == sb.Master && bStrip.Text.Contains("cx-shareb.enabled=0"), "an off chat-only master lays a strip naming its cfg key");
            Show(bStrip!); Press(p.List.X + p.Pad + 2f * p.Em, p.List.ToScreen(bStrip!.Top) + bStrip.H - 0.5f * p.Em);
            check.Expect(sb.Master!.V && Find(RowKind.Strip) == null && !p.Greyed(R(bRow)), "a press on that strip turns the master back on");
            sa.Master!.Set(false);
            check.Expect(p.Greyed(R(aRow)) && !p.Greyed(R(bRow)), "A's master off greys A's rows, not B's");
            p._subs = new[] { new SubMenu(g), new SubMenu(z) };
            p.SelectSub(0); p.LayoutRows();
            check.Expect(p.Sub == 0 && Count(RowKind.Knob) == 13 && !p.AdvOpen, "back to the group: the disclosure is closed again");

            // Independent scrolls: the knob list overflows at 720p, the nav columns do not; each wheel hits its own column.
            check.Expect(p.List.Overflows && !p.Cats.Overflows, "the row column scrolls; five categories fit");
            p.List.Reset();
            p.MouseMove(p.List.X + 10f, p.List.Y + 10f); p.MouseWheel(-1);
            check.Expect(p.List.Offset > 0f && p.Cats.Offset == 0f && p.SubCol.Offset == 0f, "a wheel over the rows scrolls the rows only");
            p.MouseMove(p.Cats.X + 10f, p.Cats.Y + 10f); p.MouseWheel(-1);
            check.Expect(p.Cats.Offset == 0f && p.List.Offset > 0f, "a wheel over a column that fits moves nothing else");
            // Header drag moves the panel and its columns; a category press relayouts.
            float ox = p.X, oy = p.Y;
            p.MouseDown(p.X + p.W * 0.5f, p.Y + 5f); p.MouseMove(p.X + p.W * 0.5f + 30f, p.Y + 5f + 20f); p.MouseUp();
            check.Expect(MathF.Abs(p.X - ox - 30f) < 0.5f && MathF.Abs(p.Y - oy - 20f) < 0.5f && MathF.Abs(p.List.X - (p.X + p.Cats.W + p.SubCol.W)) < 0.5f, "the header drags the panel and the columns follow");
            p.MouseDown(p.Cats.X + 10f, p.NavTop(p.Cats, 2) + 5f); p.MouseUp();
            check.Expect(p.Category == 2 && p.Sub == (p.Subs.Count > 0 ? 0 : -1), "a category press selects it and its first submenu");
            check.Expect(!p.MouseDown(1f, 1f), "a press off the panel is not consumed");
            // 1080p: the same tree fits with nothing clipped, Advanced open.
            p.Category = Array.IndexOf(KnobCategories, "Controls");
            p.OpenAt(1920f, 1080f, FacePx, body, small); p._subs = new[] { new SubMenu(g) }; p.Sub = 0; p.AdvOpen = true; p.LayoutRows();
            check.Expect(MathF.Abs(p.W - 1344f) < 0.5f && MathF.Abs(p.H - 810f) < 0.5f, "70%×75% of 1920×1080");
            FitCheck("1080p");
            // Escape closes; the face is the panel's own (a different Em re-lays out, nothing reads Metrics.Scale).
            check.Expect(!p.Key(0x1B) && !p.Open, "Escape closes the panel");
            p.OpenAt(1920f, 1080f, 30f, body, small); p._subs = new[] { new SubMenu(g) }; p.Sub = 0; p.LayoutRows();
            check.Expect(MathF.Abs(p.W - 1344f) < 0.5f && R(g.Master!).H >= 3.3f * 30f, "the window size is the viewport's; the rows scale with the panel face only");

            // Draw allocates nothing, with every semantic on: strip, status, master off, dots, amber badge, Advanced open with
            // the tooltip visible, the confirm armed, the copied flash, the channels expanded, the open list.
            pg.Master.Set(false); dens.Set(1f); size.Set(1024);
            p.AdvOpen = true; p._expanded = col; p.LayoutRows();
            lr = R(link); Show(lr); Press(p.List.X + p.Pad + 5f, p.List.ToScreen(lr.Top) + 5f);
            PressBox(R(wipe));
            ar = R(adv); Show(ar); p.MouseMove(p.MarkX(ar) + 2f, p.List.ToScreen(ar.Top) + 0.6f * p.Em);
            now += (long)((Tooltip.DelaySec + 0.05f) * Stopwatch.Frequency);
            check.Expect(p.Tip.Visible && p.ConfirmArmed(R(wipe)) && p.CopiedShown(lr) && p.MasterOff && Count(RowKind.Channel) == 4 && Find(RowKind.Strip) != null, "everything is on for the draw");
            var vb = new VertexBatch(body.SolidU, body.SolidV);
            p.Emit(vb);
            check.Expect(vb.VertexCount > 100, "the draw emitted the panel");
            long before2 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); p.Emit(vb); }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before2;
            check.Expect(delta == 0, $"100 draws allocate nothing ({delta} bytes)");
            g.Master.Set(false);
            before2 = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); p.Emit(vb); }
            delta = GC.GetAllocatedBytesForCurrentThread() - before2;
            check.Expect(delta == 0, $"100 greyed draws allocate nothing ({delta} bytes)");
            dens.Set(3f); p.List.Reset();
            vb.Clear(); p.Emit(vb);
            check.Expect(R(dens).Value == "3", "a value moved outside the panel re-formats its text on the next draw");
            g.Master.Set(true); pg.Master.Set(true);
            // Collapse: at Em 30 on 1344 px the box is 1212..1263 px in (the right pad 36 less 6, the × 51, then its own 51).
            Press(p.X + 1237f, p.Y + 40f);
            check.Expect(p.Collapsed && p.ShownH == 81f && !p.Contains(p.X + 600f, p.Y + 400f) && p.Contains(p.X + 600f, p.Y + 40f),
                         $"the box left of the × folds the panel to its 81 px title bar ({p.ShownH})");
            vb.Clear(); p.Emit(vb);
            int folded = vb.VertexCount;
            check.Expect(folded > 0 && folded < 200 && !p.MouseWheel(1), $"collapsed: only the title bar draws ({folded} vertices), the wheel passes");
            Press(p.X + 1237f, p.Y + 40f);
            check.Expect(!p.Collapsed && p.Contains(p.X + 600f, p.Y + 400f), "a second press unfolds it");
            p.Close();

            // List: an item row per entry and a trailing add box; the add box appends (Enter keeps adding), an item box
            // edits in place, the × or an emptied entry removes, the cfg / chat form is one | line, a press aimed at a row
            // that a commit just moved is not applied, an off master greys the items, and the draw allocates nothing.
            var lg = new KnobGroup("cx-panellist", "Controls", "List check", "Enable the list", "The list master.", true,
                                   Effect.Live, null, null, "", register: false);
            var lst = lg.List("items", "Shortcuts", new[] { ";a alpha" }, hint: "Add one", help: "One entry each.");
            p.OpenAt(1280f, 720f, FacePx, body, small); p._subs = new[] { new SubMenu(lg) }; p.Sub = 0; p.LayoutRows();
            Row I(int i) { foreach (Row r in p.Rows) if (r.Kind == RowKind.Item && r.Item == i) return r; throw new InvalidOperationException($"no item row {i}"); }
            void ItemBox(Row r, out float cx, out float cy) { Show(r); p.ItemRect(r, p.List.ToScreen(r.Top), out float x0, out float x1, out float y0, out float y1); cx = (x0 + x1) * 0.5f; cy = (y0 + y1) * 0.5f; }
            void PressItem(Row r) { ItemBox(r, out float cx, out float cy); Press(cx, cy); }
            check.Expect(Count(RowKind.Item) == 2 && I(0).Value == ";a alpha" && !I(0).Adds && I(1).Adds && I(1).Text == "Add one" && R(lst).CtlW == 0f,
                         "a list lays out its entry, then an add box carrying the hint; the label row has no control of its own");
            PressItem(I(1)); p.EditInsert(";b bravo"); p.Key(0x0D);
            check.Expect(lst.V.Count == 2 && lst.V[1] == ";b bravo" && p.Editing && p.EditingRow(I(2)), "Enter in the add box appends and opens the next add box");
            p.EditInsert("  "); p.Key(0x0D);
            check.Expect(lst.V.Count == 2 && !p.Editing, "Enter on a blank add box appends nothing and stops");
            PressItem(I(0));
            check.Expect(p.EditText == ";a alpha", "an item box seeds the edit with its entry");
            p.EditBackspace(); p.EditInsert("Z"); p.Key(0x0D);
            check.Expect(lst.V.Count == 2 && lst.V[0] == ";a alphZ", "a commit replaces the entry in place");
            Row r1 = I(1); Show(r1); p.RemoveRect(r1, p.List.ToScreen(r1.Top), out float lx0, out float lx1, out float ly0, out float ly1);
            Press((lx0 + lx1) * 0.5f, (ly0 + ly1) * 0.5f);
            check.Expect(lst.V.Count == 1 && lst.V[0] == ";a alphZ" && Count(RowKind.Item) == 2, "the × removes its entry and the rows follow");
            PressItem(I(0)); for (int i = 0; i < 8; i++) p.EditBackspace(); p.Key(0x0D);
            check.Expect(lst.V.Count == 0 && Count(RowKind.Item) == 1 && I(0).Adds && !lst.IsDefault, "an emptied entry is removed; the add box stays");
            PressItem(I(0)); p.EditInsert(new string('x', ItemMax + 10));
            check.Expect(p.EditText.Length == ItemMax, "an item takes ItemMax characters");
            p.CancelEdit();
            check.Expect(lst.Parse("p|q\\|r") && lst.V.Count == 2 && lst.V[1] == "q|r" && lst.Format() == "p|q\\|r", $"the cfg / chat form is one | line with escapes ({lst.Format()})");
            p.LayoutRows();
            check.Expect(Count(RowKind.Item) == 3, "a set from outside the panel shows on the next layout");
            ItemBox(I(2), out float ax, out float ay);
            Press(ax, ay); p.EditInsert("s");
            Press(ax, ay);
            check.Expect(lst.V.Count == 3 && lst.V[2] == "s" && !p.Editing, "a press on the add box that just appended is not applied to the entry that slid under it");
            lg.Master!.Set(false);
            check.Expect(p.Greyed(I(0)) && p.Greyed(I(3)), "an off master greys the items and the add box");
            Row r0 = I(0); Show(r0); p.RemoveRect(r0, p.List.ToScreen(r0.Top), out lx0, out lx1, out ly0, out ly1);
            Press((lx0 + lx1) * 0.5f, (ly0 + ly1) * 0.5f);
            check.Expect(lst.V.Count == 3, "a greyed × removes nothing");
            lg.Master.Set(true);
            PressItem(I(0)); p.EditInsert(" and a long enough tail to scroll the box while it is typed in");
            p.MouseMove(ax, ay);
            var lvb = new VertexBatch(body.SolidU, body.SolidV);
            p.Emit(lvb);
            long lb = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { lvb.Clear(); p.Emit(lvb); }
            long ld = GC.GetAllocatedBytesForCurrentThread() - lb;
            check.Expect(ld == 0, $"100 draws of a list with an item being typed allocate nothing ({ld} bytes)");
            p.CancelEdit(); p.Close();
        }
        finally { Clock = realClock; Tooltip.Clock = realTipClock; Tooltip.Timing = realTiming; CopyText = realCopy; }

        // Consumer mode: two columns, a modal panel with a header button, rows from a Source; every
        // edit reaches the consumer through its callbacks and never touches the registry.
        {
            var pressed = new List<Row>();
            var picked = new List<int>();
            float applied = float.NaN; string? committed = null; int layouts = 0;
            var toggle = new Row { Ctl = KnobKind.Toggle, Act = SetAct.ToggleBit, Text = "T", HelpText = "A help line long enough to wrap onto a second line at least once." };
            var slider = new Row { Ctl = KnobKind.Slider, Act = SetAct.SliderHold, Text = "S", Min = 0f, Max = 100f };
            var text = new Row { Ctl = KnobKind.Text, Act = SetAct.TabLabel, Text = "C", Value = "old" };
            var dd = new Row { Ctl = KnobKind.Dropdown, Act = SetAct.CycleAlign, Text = "Align", Choices = new[] { "Left", "Right", "Center" }, Selected = 1 };
            var btn = new Row { Ctl = KnobKind.Button, Act = SetAct.CopyFrom, Text = "Copy" };
            var p = new KnobPanel
            {
                Categories = new[] { "Window settings", "All", "Add new tab..." },
                WFrac = 0.52f, HFrac = 0.66f, CatFrac = 0.30f, SubFrac = 0f, Modal = true,
                HeaderAction = "Copy this window",
            };
            int headerFires = 0;
            p.HeaderActionPressed = () => headerFires++;
            p.Source = () =>
            {
                layouts++;
                p.Publish(p.Category == 0
                    ? new List<Row> { new Row { Kind = RowKind.Section, Text = "LOOK" }, toggle, slider, text, dd, btn }
                    : new List<Row> { text });
            };
            p.RowPressed = r => pressed.Add(r);
            p.CategoryPicked = c => { picked.Add(c); p.Category = c == 2 ? 1 : c; };
            p.ApplyValue = (r, v) => { applied = v; r.Norm = (v < 0f ? 0f : v > 100f ? 100f : v) / 100f; };
            p.CommitLabel = (r, t) => committed = t;
            void Press(float mx, float my) { p.MouseDown(mx, my); p.MouseUp(); }

            p.OpenAt(1280f, 800f, 20f, body, small);
            check.Expect(p.Open && p.Rows.Count == 6 && layouts == 1 && p.SubCol.W == 0f && p.Cats.W + p.List.W == p.W, "OpenAt lays out through the Source into two columns");
            // 0.52 of 1280 and 0.66 of 800, rounded to whole pixels: 666 x 528, written out rather than
            // recomputed through the rounding the layout under test applies.
            check.Expect(p.W == 666f && p.H == 528f, $"the consumer's size fractions size the panel ({p.W}x{p.H})");
            check.Expect(toggle.Help.Length >= 2 && toggle.H > slider.H && slider.H == p.RowPlainH && slider.Value == "0", "help wraps and grows its row; a plain row is compact; the slider's value text is laid out");
            check.Expect(p.MouseUp(), "a modal panel owns the press that opened it");
            Press(p.List.X + 5f, p.RowTop(toggle) + 2f);
            check.Expect(pressed.Count == 1 && pressed[0] == toggle && layouts == 2, "a toggle row press reaches the consumer and relays out");
            Press(p.List.X + 5f, p.RowTop(p.Rows[0]) + 2f);
            check.Expect(pressed.Count == 1, "a section header is inert");

            // Categories go through the consumer, which may redirect; a header button fires, the header around it drags.
            Press(p.Cats.X + 5f, p.NavTop(p.Cats, 2) + 2f);
            check.Expect(picked.Count == 1 && picked[0] == 2 && p.Category == 1 && p.Rows.Count == 1, "a category press is the consumer's pick (Add new tab... -> the new tab)");
            Press(p.Cats.X + 5f, p.NavTop(p.Cats, 0) + 2f);
            check.Expect(p.Category == 0 && p.Rows.Count == 6, "back to the first category");
            p.HeaderButtonRect(out float hbx0, out float hbx1, out float hby0, out float hby1);
            Press((hbx0 + hbx1) / 2f, (hby0 + hby1) / 2f);
            check.Expect(headerFires == 1 && p.Open && !p.Moving, "the header button fires its action, no drag, stays open");
            float px = p.X;
            p.MouseDown(hbx0 - 2f, (hby0 + hby1) / 2f); p.MouseMove(hbx0 - 2f + 40f, (hby0 + hby1) / 2f);
            check.Expect(headerFires == 1 && p.Moving && MathF.Abs(p.X - (px + 40f)) < 0.5f, "left of the button is the drag handle");
            p.MouseUp();

            // Slider: the track and the value box go through ApplyValue; a press elsewhere commits the edit.
            p.TrackRect(slider, p.RowTop(slider), out float sx0, out float sx1, out float scy);
            p.MouseDown(sx1 + 2f, scy);
            check.Expect(MathF.Abs(applied - 100f) < 0.5f && slider.Value == "100" && p.SliderDragging, "the track applies through the consumer and re-formats the value text");
            p.MouseMove((sx0 + sx1) / 2f, scy); p.MouseUp();
            check.Expect(MathF.Abs(applied - 50f) < 1f, "a drag applies per motion");
            p.ValueRect(slider, p.RowTop(slider), out float vx0, out float vx1, out float vy0, out float vy1);
            Press((vx0 + vx1) / 2f, (vy0 + vy1) / 2f); p.EditInsert("42");
            Press(p.List.X + 5f, p.RowTop(toggle) + 2f);
            check.Expect(!p.Editing && MathF.Abs(applied - 42f) < 0.01f && slider.Value == "42", "a press elsewhere commits the typed value through the consumer");
            applied = float.NaN;
            Press((vx0 + vx1) / 2f, (vy0 + vy1) / 2f); p.EditInsert("9"); p.CancelEdit();
            check.Expect(!p.Editing && float.IsNaN(applied), "cancel applies nothing");

            // Text: the box edits in place, the commit is the consumer's; a dropdown pick sets Selected first.
            p.BoxRect(text, p.RowTop(text), out float tx0, out float tx1, out float ty0, out float ty1);
            Press((tx0 + tx1) / 2f, (ty0 + ty1) / 2f);
            check.Expect(p.Editing && p.EditText == "old", "a text box press starts the edit seeded with the value");
            p.EditInsert("!"); p.Key(0x0D);
            check.Expect(committed == "old!" && !p.Editing, "Enter hands the text to CommitLabel");
            p.BoxRect(dd, p.RowTop(dd), out float dx0, out float dx1, out float dy0, out float dy1);
            Press((dx0 + dx1) / 2f, (dy0 + dy1) / 2f);
            check.Expect(p.Drop.Open && p.Drop.Selected == 1, "the dropdown box opens the list on the row's Selected");
            Press(p.Drop.X + 5f, p.Drop.ItemTop(2) + 2f);
            check.Expect(!p.Drop.Open && dd.Selected == 2 && pressed[^1] == dd, "a pick sets Selected and reaches the consumer");
            int n = pressed.Count;
            Press(p.List.X + 5f, p.RowTop(btn) + 2f);
            check.Expect(pressed.Count == n, "a press beside a button's box is nothing");
            p.BoxRect(btn, p.RowTop(btn), out float bx0, out float bx1, out float by0, out float by1);
            Press((bx0 + bx1) / 2f, (by0 + by1) / 2f);
            check.Expect(pressed[^1] == btn, "a press in the box fires the button");

            // A modal panel closes on an off-panel press and owns it; the draw allocates nothing.
            var vb = new VertexBatch(body.SolidU, body.SolidV);
            p.Emit(vb);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { vb.Clear(); p.Emit(vb); }
            long delta = GC.GetAllocatedBytesForCurrentThread() - before;
            check.Expect(delta == 0, $"100 consumer-mode draws allocate nothing ({delta} bytes)");
            check.Expect(p.MouseDown(p.X + p.W + 5f, p.Y + 5f) && !p.Open && p.MouseUp(), "an off-panel press closes a modal panel and is owned");
        }

        log.Info(check.Ok ? "KnobPanel.SelfCheck: passed" : "KnobPanel.SelfCheck: FAILED");
    }
}
