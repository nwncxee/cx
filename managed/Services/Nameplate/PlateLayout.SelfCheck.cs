using CX.Framework;
using CX.Services.Chat.Core;
using CX.Ui;

namespace CX.Services.Nameplate;

public static partial class PlateLayout
{
    /// <summary>Off-device proof of the placement clamp, the measured boxes, both fade curves, the decay clamp
    /// and the band/reaction tables.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        static bool Near(float a, float b) => System.Math.Abs(a - b) < 1e-4f;

        // ---- Place: centred, above, and clamped into the viewport at every corner ----
        Place(640f, 400f, 100f, 40f, 1280f, 800f, out float px, out float py);
        check.Expect(Near(px, 590f), "place centres on the anchor");
        check.Expect(Near(py, 360f), "place sits above the anchor");
        Place(0f, 0f, 100f, 40f, 1280f, 800f, out px, out py);
        check.Expect(px == 0f && py == 0f, "top-left anchor clamps to (0,0)");
        Place(1280f, 800f, 100f, 40f, 1280f, 800f, out px, out py);
        check.Expect(Near(px, 1180f) && Near(py, 760f), "bottom-right anchor clamps fully on-screen");
        Place(1280f, 0f, 100f, 40f, 1280f, 800f, out px, out py);
        check.Expect(Near(px, 1180f) && py == 0f, "top-right anchor clamps on both axes");
        Place(0f, 800f, 100f, 40f, 1280f, 800f, out px, out py);
        check.Expect(px == 0f && Near(py, 760f), "bottom-left anchor clamps on both axes");
        Place(640f, 400f, 2000f, 40f, 1280f, 800f, out px, out py);
        check.Expect(px == 0f, "a plate wider than the viewport pins to the left edge");
        Place(640f, 400f, 100f, 2000f, 1280f, 800f, out px, out py);
        check.Expect(py == 0f, "a plate TALLER than the viewport pins to the top edge (the multi-line name case)");
        Place(640f, 400f, 100f, 40f, 0f, 0f, out px, out py);
        check.Expect(Near(px, 590f) && Near(py, 360f), "an unknown viewport leaves the point unclamped");

        // ---- ToDrawSpace: the engine's bottom-left pixels into the window's top-left ones ----
        // Camera viewport == draw viewport, GUI scale 1: a pure vertical mirror.
        ToDrawSpace(100f, 200f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out float dx, out float dy);
        check.Expect(Near(dx, 100f), "x passes through when the viewports match");
        check.Expect(Near(dy, 600f), "y flips about the viewport height");
        ToDrawSpace(0f, 0f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out dx, out dy);
        check.Expect(dx == 0f && Near(dy, 800f), "the engine's origin is the window's BOTTOM-left");
        ToDrawSpace(1280f, 800f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out dx, out dy);
        check.Expect(Near(dx, 1280f) && dy == 0f, "the engine's top-right is the window's top-right");
        ToDrawSpace(640f, 400f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out dx, out dy);
        check.Expect(Near(dx, 640f) && Near(dy, 400f), "the centre is a fixed point of the mirror");
        // A world point ABOVE a creature's origin projects HIGHER in engine space, which must be a SMALLER y.
        ToDrawSpace(640f, 500f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out _, out float above);
        ToDrawSpace(640f, 400f, 0f, 0f, 1280f, 800f, 1f, 1280f, 800f, out _, out float below);
        check.Expect(above < below, "higher on screen in engine space is a smaller draw y");

        // An offset, upscaled render viewport: rebase then rescale, and the flip is about the camera rect.
        ToDrawSpace(100f, 50f, 100f, 50f, 640f, 400f, 1f, 1280f, 800f, out dx, out dy);
        check.Expect(dx == 0f && Near(dy, 800f), "the camera rect's origin maps to the draw viewport's bottom-left");
        ToDrawSpace(740f, 450f, 100f, 50f, 640f, 400f, 1f, 1280f, 800f, out dx, out dy);
        check.Expect(Near(dx, 1280f) && dy == 0f, "the camera rect's far corner maps to the draw viewport's top-right");

        // GUI scale: inside the engine's own 0.09 dead band nothing was shrunk, so nothing is un-shrunk.
        ToDrawSpace(340f, 200f, 0f, 0f, 1280f, 800f, 1.05f, 1280f, 800f, out dx, out dy);
        check.Expect(Near(dx, 340f), "a GUI scale inside the +-0.09 dead band is not undone");
        ToDrawSpace(340f, 200f, 0f, 0f, 1280f, 800f, 2f, 1280f, 800f, out dx, out dy);
        check.Expect(Near(dx, 640f + (340f - 640f) * 2f), "a GUI scale past the dead band scales out about the centre");
        ToDrawSpace(640f, 400f, 0f, 0f, 1280f, 800f, 2f, 1280f, 800f, out dx, out dy);
        check.Expect(Near(dx, 640f) && Near(dy, 400f), "the rect centre is fixed under any GUI scale");
        ToDrawSpace(340f, 200f, 0f, 0f, 1280f, 800f, 0f, 1280f, 800f, out dx, out dy);
        check.Expect(Near(dx, 340f), "a zero GUI scale is treated as unknown, not as a collapse to the centre");

        ToDrawSpace(100f, 200f, 0f, 0f, 0f, 0f, 1f, 1280f, 800f, out dx, out dy);
        check.Expect(Near(dx, 100f) && Near(dy, 600f), "an unknown camera viewport still flips about the draw height");

        // ---- FadeStep / MoveToward / SettleOffset ----
        check.Expect(FadeStep(0, 180f) == 0f, "no elapsed time is no step");
        check.Expect(FadeStep(-500, 180f) == 0f, "a clock that ran backwards is no step");
        check.Expect(Near(FadeStep(90, 180f), 0.5f), "half the interval is half the step");
        check.Expect(FadeStep(180, 180f) == 1f, "exactly the interval crosses the whole range");
        check.Expect(FadeStep(10000, 180f) == 1f, "far past the interval still only crosses the range once");
        check.Expect(FadeStep(16, 0f) == 1f, "a zero interval snaps");

        float v = 0f;
        MoveToward(ref v, 1f, FadeStep(90, 180f));
        check.Expect(Near(v, 0.5f), "a fade-in reaches half way at half the interval");
        MoveToward(ref v, 1f, FadeStep(90, 180f));
        check.Expect(v == 1f, "the second half lands exactly on the target");
        MoveToward(ref v, 1f, FadeStep(90, 180f));
        check.Expect(v == 1f, "stepping past the target never overshoots");
        MoveToward(ref v, 0f, FadeStep(360, 180f));
        check.Expect(v == 0f, "a long frame fades all the way out, not past it");
        v = 0.5f;
        MoveToward(ref v, 1f, FadeStep(-5, 180f));
        check.Expect(Near(v, 0.5f), "a backwards clock leaves the alpha alone");

        float off = 14f;
        SettleOffset(ref off, 0, 180f);
        check.Expect(off == 14f, "no elapsed time leaves the slide where it was");
        SettleOffset(ref off, -100, 180f);
        check.Expect(off == 14f, "a backwards clock leaves the slide where it was");
        SettleOffset(ref off, 16, 180f);
        check.Expect(off < 14f && off > 0f, "a frame's worth of ease closes part of the slide, toward zero");
        for (int i = 0; i < 200; i++) SettleOffset(ref off, 16, 180f);
        check.Expect(off == 0f, "the slide snaps to exactly zero instead of asymptoting");
        off = -14f;
        for (int i = 0; i < 200; i++) SettleOffset(ref off, 16, 180f);
        check.Expect(off == 0f, "a slide from above settles the same way");
        off = 14f;
        SettleOffset(ref off, 16, 0f);
        check.Expect(off == 0f, "a zero interval is 'no animation': the slide snaps rather than easing at the tau floor");
        off = 14f;
        SettleOffset(ref off, 16, 1f);
        check.Expect(off < 14f && off > 0f, "a sub-tau-floor interval still eases at the floor rather than dividing by zero");

        // ---- Measure ----
        PlateMetrics m = Measure(80f, 60f, portrait: true, band: true, bar: true, lineH: 18f, scale: 1f);
        check.Expect(Near(m.PortraitSize, 18f + 18f + 4f + 3f),
          "a ONE-line name spans the whole content column, exactly as it always did");
        check.Expect(Near(m.PortraitSize * 0.64f, m.NameX - 12f), "and keeps the 0.64 aspect at that height");
        check.Expect(Near(m.NameX, 4f + m.PortraitSize * 0.64f + 8f), "name column clears the portrait");
        check.Expect(Near(m.BandX, m.NameX) && Near(m.BandY, 4f + 18f), "band sits on the second line, same column");
        check.Expect(Near(m.BarW, 80f) && Near(m.BarH, 3f), "bar spans the text column at the bar thickness");
        check.Expect(Near(m.BarY, 4f + 18f + 18f + 4f), "bar sits under the band");
        check.Expect(Near(m.H, m.PortraitSize + 8f), "height = content + both pads");
        check.Expect(Near(m.PortraitY, 4f),
          "a portrait as tall as its content centres to exactly the pad -- unchanged from the top-aligned rule");
        check.Expect(Near(m.BarX, 4f + m.PortraitSize * 0.64f + 8f), "the bar starts at the text column, not at the plate edge");
        check.Expect(Near(m.W, m.NameX + 80f + 4f), "width = column + widest text + pad");

        PlateMetrics tex = Measure(80f, 60f, portrait: false, band: false, bar: false, lineH: 18f, scale: 1f);
        check.Expect(tex.PortraitSize == 0f && Near(tex.NameX, 4f), "no portrait: text starts at the pad");
        check.Expect(Near(tex.H, 26f), "text-only height is one line plus pads");
        check.Expect(tex.BarW == 0f && tex.BarH == 0f, "no bar: zero bar box");

        PlateMetrics sc = Measure(80f, 60f, portrait: false, band: false, bar: false, lineH: 36f, scale: 2f);
        check.Expect(Near(sc.NameX, 8f), "chrome pads scale, glyph metrics do not");
        check.Expect(Near(sc.H, 36f + 16f), "scaled height = the given line height plus scaled pads");
        check.Expect(Near(m.TextW, 80f) && Near(tex.TextW, 80f), "the text column is the widest of name and band");

        // ---- Measure with an N-line name ----
        PlateMetrics m3 = Measure(80f, 3, 60f, portrait: true, band: true, bar: true, lineH: 18f, scale: 1f);
        check.Expect(Near(m3.H, m.H + 18f * 2f), "two extra name lines add exactly two line heights to the box");
        check.Expect(Near(m3.PortraitSize, 18f * 2f + 18f + 4f + 3f),
          "a creature's portrait grows by ONE more name line and no further, band and bar still on top");
        check.Expect(Near(m3.NameX, 4f + m3.PortraitSize * 0.64f + 8f), "the text column clears the WIDER portrait");
        check.Expect(Near(m3.W - m.W, m3.NameX - m.NameX), "and the box widens by exactly that, nothing else moving");
        check.Expect(Near(m3.BandY, 4f + 18f * 3f), "the band follows the LAST name line");
        check.Expect(Near(m3.BarY, m3.BandY + 18f + 4f), "and the bar follows the band");
        check.Expect(Near(m3.PortraitY, 4f + (18f * 3f + 18f + 4f + 3f - m3.PortraitSize) * 0.5f) && Near(m3.PortraitY, 13f),
          "a three-line name outgrows the capped portrait, which centres in the taller column");
        check.Expect(Near(m3.PortraitY + m3.PortraitSize * 0.5f, m3.H * 0.5f),
          "and the portrait's centre is the box's centre, the pads being equal");
        check.Expect(Near(Measure(80f, 1, 60f, true, true, true, 18f, 1f).H, m.H), "one line is the one-line overload");
        check.Expect(Near(Measure(80f, 0, 60f, true, true, true, 18f, 1f).H, m.H), "a zero line count is treated as one");

        // ---- the portrait grows with the name, up to two lines ---- (a placeable: no band, no bar)
        PlateMetrics p1 = Measure(80f, 1, 0f, portrait: true, band: false, bar: false, lineH: 18f, scale: 1f);
        check.Expect(Near(p1.PortraitSize, 18f), "a one-line plate gets a ONE-line portrait, unchanged");
        PlateMetrics p2 = Measure(80f, 2, 0f, portrait: true, band: false, bar: false, lineH: 18f, scale: 1f);
        check.Expect(Near(p2.PortraitSize, 36f), "a second name line grows the portrait to two lines");
        PlateMetrics p5 = Measure(80f, 5, 0f, portrait: true, band: false, bar: false, lineH: 18f, scale: 1f);
        check.Expect(Near(p5.PortraitSize, p2.PortraitSize), "a fifth line does not grow it any further");
        check.Expect(Near(p5.H, 5f * 18f + 8f), "and the box still grows to the whole name");
        check.Expect(Near(Measure(80f, 1, 60f, true, true, true, 18f, 1f).PortraitSize, 18f + 18f + 4f + 3f),
          "a one-line CREATURE portrait is byte-identical to the pre-change rule (never shrunk by the cap)");
        check.Expect(Near(p1.NameX, 4f + 18f * 0.64f + 8f) && Near(p2.NameX, 4f + 36f * 0.64f + 8f)
          && Near(p5.NameX, p2.NameX), "the text column clears the portrait at every height, at the 0.64 aspect");
        check.Expect(Near(p1.PortraitY, 4f) && Near(p2.PortraitY, 4f),
          "up to the cap the portrait fills its content exactly and stays at the pad");
        check.Expect(Near(p5.PortraitY, 4f + (5f * 18f - p5.PortraitSize) * 0.5f) && Near(p5.PortraitY, 31f),
          "past the cap the shorter portrait centres against the full content height");
        check.Expect(Near(p5.PortraitY + p5.PortraitSize * 0.5f, p5.H * 0.5f), "which is the box's centre");

        // ---- PortraitTop: the rule itself, including the case Measure cannot reach ----
        check.Expect(Near(PortraitTop(4f, 90f, 36f), 31f), "a short portrait centres in the content");
        check.Expect(Near(PortraitTop(4f, 36f, 36f), 4f), "an exactly-fitting portrait sits at the pad");
        check.Expect(Near(PortraitTop(4f, 20f, 36f), 4f),
          "a portrait TALLER than its content pins to the pad -- no negative top, no empty space above it");

        // ---- Measure with the ObjectTextBubbleOverride bands ----
        PlateMetrics ovNone = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 0, 0, 0f);
        check.Expect(Near(ovNone.H, m.H) && Near(ovNone.W, m.W) && Near(ovNone.NameY, m.NameY)
          && Near(ovNone.BandY, m.BandY) && Near(ovNone.PortraitY, m.PortraitY),
          "no override lines is byte-identical to the plate that never had the feature");
        PlateMetrics post = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 0, 2, 0f);
        check.Expect(Near(post.H, m.H + 36f), "two appended lines add exactly two line heights");
        check.Expect(Near(post.NameY, m.NameY) && Near(post.BandY, m.BandY),
          "and nothing above them moves -- append grows the box downward only");
        check.Expect(Near(post.PostY, 4f + 18f + 18f + 4f + 3f), "the appended lines start below the band AND the bar");
        PlateMetrics pre = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 2, 0, 0f);
        check.Expect(Near(pre.H, m.H + 36f) && Near(pre.PreY, 4f), "prepended lines start at the pad and grow the box");
        check.Expect(Near(pre.NameY, m.NameY + 36f) && Near(pre.BandY, m.BandY + 36f) && Near(pre.BarY, m.BarY + 36f),
          "and push the name, band and bar down by exactly their height");
        check.Expect(Near(pre.PortraitY, 4f + (79f - pre.PortraitSize) * 0.5f) && Near(pre.PortraitY, 22f),
          "the portrait centres against the WHOLE composed column, pre-lines included");
        check.Expect(Near(pre.PortraitSize, m.PortraitSize), "and is not stretched by the extra lines");
        check.Expect(Near(post.PortraitY, pre.PortraitY),
          "an append of the same line count centres the portrait identically -- it is the height that counts");
        PlateMetrics wide = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 0, 1, 140f);
        check.Expect(Near(wide.TextW, 140f) && Near(wide.W, wide.NameX + 140f + 4f),
          "an override line wider than name and band widens the text column");
        PlateMetrics repl = Measure(0f, 0, 0f, true, false, false, 18f, 1f, 2, 0, 90f);
        check.Expect(Near(repl.H, 36f + 8f), "replace mode's lines ARE the plate: no name line and no band survive");
        check.Expect(Near(repl.PortraitSize, 36f), "and the portrait spans them exactly as it would name lines");

        // ---- LineX: every line of every plate centres in the text column, as vanilla does ----
        check.Expect(Near(LineX(20f, 100f, 40f), 50f), "a line is inset by half the slack, so it centres in the column");
        check.Expect(Near(LineX(20f, 100f, 60f), 40f), "...and a wider one by half its smaller slack");
        check.Expect(Near(LineX(20f, 100f, 100f), 20f), "the widest line still starts at the column");
        check.Expect(Near(LineX(20f, 100f, 140f), 20f), "an over-wide line pins to the column, never left of it");
        float shortX = LineX(20f, 100f, 30f), longX = LineX(20f, 100f, 90f);
        check.Expect(shortX > longX, "a short line sits further right than a long one");
        check.Expect(shortX >= 20f && shortX + 30f <= 120f && longX + 90f <= 120f, "both stay inside the text column");

        // Every line type, on both plate shapes: a creature (name + band + bar, band the narrower line) and a
        // placeable (two name lines, no band). The layout takes no object type at all -- that is the point.
        PlateMetrics cre = Measure(80f, 1, 60f, true, true, true, 18f, 1f, 0, 1, 50f);
        check.Expect(Near(cre.TextW, 80f), "the creature's text column is its widest line, the 80-wide name");
        check.Expect(Near(LineX(cre.NameX, cre.TextW, 80f), cre.NameX), "a creature's widest name line starts at the column");
        check.Expect(Near(LineX(cre.BandX, cre.TextW, 60f), cre.NameX + 10f),
          "its 60-wide health band is inset by half its 20 of slack, in the same column");
        check.Expect(Near(LineX(cre.NameX, cre.TextW, 50f), cre.NameX + 15f),
          "and its 50-wide override line by half its 30");
        float hitL = LineX(cre.NameX, cre.TextW, 30f);
        check.Expect(hitL >= cre.NameX && hitL + 30f <= cre.NameX + cre.TextW,
          "the click-to-tell rect is the whole text column, so a centred short name line still lands inside it");
        PlateMetrics plc = Measure(80f, 2, 0f, true, false, false, 18f, 1f, 1, 0, 40f);
        check.Expect(Near(plc.TextW, 80f) && Near(LineX(plc.NameX, plc.TextW, 30f), plc.NameX + 25f)
          && Near(LineX(plc.NameX, plc.TextW, 40f), plc.NameX + 20f),
          "a placeable insets its name and override lines by half their own slack too -- no per-type rule survives");
        check.Expect(Near(plc.PortraitY, 4f + (54f - plc.PortraitSize) * 0.5f) && Near(plc.PortraitY, 13f),
          "and its capped portrait centres against name plus override, not against the name alone");

        // ---- PlaceOverride: which segment joins which plate line, per mode ----
        PlateSplice Placement(int mode, int paras, int nameLines, bool band, out bool sn, out bool sb,
                          out int si, out int pf, out int pt, out int qf, out int qt)
            => PlaceOverride(mode, paras, nameLines, band, out sn, out sb, out si, out pf, out pt, out qf, out qt);

        PlateSplice t = Placement(3, 2, 1, true, out bool sn, out bool sb, out int si,
                              out int pf, out int pt, out int qf, out int qt);
        check.Expect(t == PlateSplice.BandLast && si == 0 && qf == 1 && qt == 2 && pt == 0 && sn && sb,
          "append glues its FIRST segment onto the band and drops the rest below it");
        t = Placement(2, 2, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        check.Expect(t == PlateSplice.NameFirst && si == 1 && pf == 0 && pt == 1 && qt == 0 && sn && sb,
          "prepend runs its LAST segment into the name and puts the rest above it");
        t = Placement(1, 2, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        check.Expect(t == PlateSplice.None && si == -1 && pf == 0 && pt == 2 && !sn && !sb,
          "replace makes the segments the WHOLE plate -- no name line and no band survive");
        t = Placement(0, 2, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        check.Expect(t == PlateSplice.None && si == -1 && pt == 0 && qt == 0 && sn && sb,
          "bubble mode 0 leaves the plate untouched, override segments or not");
        t = Placement(3, 0, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        check.Expect(t == PlateSplice.None && si == -1 && qt == 0 && sn && sb,
          "an override with no segments is no override");
        t = Placement(3, 2, 1, false, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        check.Expect(t == PlateSplice.NameLast && si == 0 && qf == 1 && qt == 2,
          "with the band off, append glues onto the LAST name line instead");
        t = Placement(2, 2, 0, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        check.Expect(t == PlateSplice.BandFirst && si == 1 && pt == 1,
          "with no name line, prepend runs into the FRONT of the band -- never off its end");
        t = Placement(3, 2, 0, false, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        check.Expect(t == PlateSplice.None && si == -1 && qf == 0 && qt == 2,
          "with nothing to glue to, every append segment stands as its own line");
        t = Placement(2, 1, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        check.Expect(t == PlateSplice.NameFirst && si == 0 && pt == 0, "a single-segment prepend is all splice");
        t = Placement(3, 1, 1, true, out sn, out sb, out si, out pf, out pt, out qf, out qt);
        check.Expect(t == PlateSplice.BandLast && si == 0 && qf == 1 && qt == 1, "a single-segment append likewise");

        // ---- SplitOverride: the wire format, NOT a builder-typed name ----
        int[] os = new int[8], ol = new int[8];
        string Seg(string s, int i) => s.Substring(os[i], ol[i]);
        const string vec1 = " Poisoned Diseased\nSneaking Invisible";
        check.Expect(SplitOverride(vec1, os, ol, 8) == 2 && Seg(vec1, 0) == " Poisoned Diseased"
          && Seg(vec1, 1) == "Sneaking Invisible",
          "the server's composed override splits into its splice and its own line, space intact");
        const string vec2 = "\nSneaking";
        check.Expect(SplitOverride(vec2, os, ol, 8) == 2 && ol[0] == 0 && Seg(vec2, 1) == "Sneaking",
          "a leading break leaves an EMPTY first segment -- nothing joins the existing line");
        const string vec3 = "a\r\nb\rc\nd";
        check.Expect(SplitOverride(vec3, os, ol, 8) == 4 && Seg(vec3, 1) == "b" && Seg(vec3, 2) == "c",
          "LF, CR and CRLF are each ONE break");
        const string vec4 = "abc\n";
        check.Expect(SplitOverride(vec4, os, ol, 8) == 1 && Seg(vec4, 0) == "abc",
          "a trailing break is not a blank line");
        const string vec5 = " a   b\\nc";
        check.Expect(SplitOverride(vec5, os, ol, 8) == 1 && Seg(vec5, 0) == vec5,
          "BreakName's conveniences do NOT apply: no trim, no 3-space rule, no literal backslash-n");
        check.Expect(SplitOverride("", os, ol, 8) == 0, "an empty override is no segments at all");
        check.Expect(SplitOverride("a\nb\nc", os, ol, 2) == 2, "the segment ceiling truncates rather than overrunning");

        // ---- BreakName ----
        const string plain = "Events Board";
        check.Expect(ReferenceEquals(BreakName(plain), plain), "a name with nothing to rewrite is returned as-is");
        check.Expect(BreakName("") == "", "an empty name stays empty");
        check.Expect(BreakName("   ") == "", "a name that is only spaces collapses to nothing");
        check.Expect(BreakName("a\nb") == "a\nb", "a real LF is one break");
        check.Expect(BreakName("a\r\nb") == "a\nb", "CRLF collapses to one break");
        check.Expect(BreakName("a\rb") == "a\nb", "a bare CR is one break");
        check.Expect(BreakName("a\\nb") == "a\nb", "the literal two-character backslash-n is one break");
        check.Expect(BreakName("a  b") == "a  b", "exactly two spaces stay literal");
        check.Expect(BreakName("a b") == "a b", "one space stays literal");
        check.Expect(BreakName("a   b") == "a\nb", "exactly three spaces are one break");
        check.Expect(BreakName("a          b") == "a\nb", "a long run of spaces is ONE break, not one per space");
        check.Expect(BreakName("a  \n  b") == "a\nb", "spaces either side of a break are trimmed away");
        check.Expect(BreakName("a\n\n\nb") == "a\nb", "consecutive breaks collapse to one -- no blank lines");
        check.Expect(BreakName("\na") == "a" && BreakName("a\n") == "a", "a break at either end leaves no empty line");
        check.Expect(BreakName("   a   ") == "a", "leading and trailing whitespace is trimmed outright");
        check.Expect(BreakName("- {Ministry of Culture} -          Events Board!")
          == "- {Ministry of Culture} -\nEvents Board!", "the reported padded name splits into header + line");

        // ---- StripColours + the colour-carrying BreakName ----
        string tagged = "<c" + (char)0x10 + (char)0x20 + (char)0x30 + ">Bob</c> the <c"
                      + (char)0xff + (char)0x00 + (char)0x00 + ">Red</c>";
        string bare = StripColours(tagged, out uint[]? bc);
        check.Expect(bare == "Bob the Red", "the colour tags leave the drawable string");
        check.Expect(bc != null && bc.Length == bare.Length, "one colour per char of the stripped string");
        check.Expect(bc![0] == 0x102030u && bc[3] == Plain && bc[8] == 0xff0000u,
          "each char carries the colour whose tag covered it, and untagged chars stay plain");
        check.Expect(ReferenceEquals(StripColours(plain, out uint[]? nc2), plain) && nc2 == null,
          "a name with no '<' is returned as-is and allocates no colour array");
        check.Expect(StripColours("<c" + (char)1 + (char)1 + (char)1 + "></c>", out _) == "",
          "a name that is nothing but tags strips to nothing");

        string cbSrc = StripColours("<c" + (char)0x10 + (char)0x20 + (char)0x30 + ">hdr</c>   aaa bbb",
                                    out uint[]? cbCols);
        string cbOut = BreakName(cbSrc, cbCols, out uint[]? cbOutCols);
        check.Expect(cbOut == "hdr\naaa bbb", "the stripped name still breaks where its author asked");
        check.Expect(cbOutCols != null && cbOutCols.Length == cbOut.Length,
          "the colour array survives the break, index for index");
        check.Expect(cbOutCols![0] == 0x102030u && cbOutCols[2] == 0x102030u && cbOutCols[4] == Plain,
          "and every surviving char kept the colour it had before the rewrite");
        check.Expect(BreakName(cbSrc, null, out uint[]? cbNone) == cbOut && cbNone == null,
          "a null colour array stays null through the same rewrite");

        // ---- SplitLines ----
        var ls = new int[4]; var ll = new int[4];
        check.Expect(SplitLines("", ls, ll, 4) == 0, "an empty name has no lines");
        check.Expect(SplitLines("abc", ls, ll, 4) == 1 && ls[0] == 0 && ll[0] == 3, "a break-free name is one whole line");
        int ln3 = SplitLines("ab\ncd\ne", ls, ll, 4);
        check.Expect(ln3 == 3 && ls[0] == 0 && ll[0] == 2 && ls[1] == 3 && ll[1] == 2 && ls[2] == 6 && ll[2] == 1,
          "each span points at its own line, the LF in neither");
        check.Expect(SplitLines("a\nb\nc\nd\ne", ls, ll, 4) == 4, "the cap is a hard ceiling; the remainder is dropped");
        check.Expect(ll[3] == 1, "and the last kept line ends at its own break, not mid-word");
        check.Expect(SplitLines("abc", ls, ll, 0) == 0, "a zero cap asks for no lines at all");

        // ---- DistanceFade ----
        check.Expect(DistanceFade(500f, 0f, 10f) == 1f, "maxDist 0 is unlimited");
        check.Expect(DistanceFade(500f, -1f, 10f) == 1f, "negative maxDist is unlimited");
        check.Expect(DistanceFade(10f, 45f, 10f) == 1f, "well inside is fully opaque");
        check.Expect(DistanceFade(35f, 45f, 10f) == 1f, "exactly at maxDist-fadeDist is still 1");
        check.Expect(Near(DistanceFade(40f, 45f, 10f), 0.5f), "mid-band is half");
        check.Expect(DistanceFade(45f, 45f, 10f) == 0f, "exactly at maxDist is 0");
        check.Expect(DistanceFade(60f, 45f, 10f) == 0f, "beyond maxDist is 0");
        check.Expect(DistanceFade(44.9f, 45f, 0f) == 1f, "zero fade band is a hard step");
        check.Expect(DistanceFade(0f, 5f, 10f) == 1f, "a fade band wider than maxDist clamps to it");
        check.Expect(Near(DistanceFade(2.5f, 5f, 10f), 0.5f), "a clamped fade band ramps over the whole range");

        // ---- BubbleAlpha ----
        check.Expect(BubbleAlpha(1000, 1000, 4f, 2f) == 1f, "fresh bubble fully opaque");
        check.Expect(BubbleAlpha(3000, 1000, 4f, 2f) == 1f, "mid-hold still opaque");
        check.Expect(BubbleAlpha(5000, 1000, 4f, 2f) == 1f, "exactly at the hold end is still 1");
        check.Expect(Near(BubbleAlpha(6000, 1000, 4f, 2f), 0.5f), "mid-fade is half");
        check.Expect(BubbleAlpha(7000, 1000, 4f, 2f) == 0f, "exactly at hold+fade is 0");
        check.Expect(BubbleAlpha(60000, 1000, 4f, 2f) == 0f, "past the end is 0");
        check.Expect(BubbleAlpha(6000, 1000, 4f, 0f) == 0f, "zero fade drops straight to 0 after the hold");
        check.Expect(BubbleAlpha(500, 1000, 4f, 2f) == 1f, "a clock that runs backwards never exceeds 1");

        // ---- TailPoints ---- (box spans x 100..200, tail 12 wide, inset 5)
        TailPoints(150f, 100f, 100f, 12f, 5f, out float tl, out float tr, out float tp);
        check.Expect(Near(tp, 150f), "the tail tip sits on the anchor when the box is centred under it");
        check.Expect(Near(tl, 144f) && Near(tr, 156f), "and its base straddles the anchor");
        check.Expect(tl >= 105f && tr <= 195f, "the base stays inside the box, inset from the corners");
        TailPoints(90f, 100f, 100f, 12f, 5f, out tl, out tr, out tp);
        check.Expect(Near(tl, 105f) && Near(tr, 117f), "an anchor off the left end pins the base at that end");
        check.Expect(Near(tp, 100f), "and the tip leans to the box edge rather than detaching from it");
        TailPoints(300f, 100f, 100f, 12f, 5f, out tl, out tr, out tp);
        check.Expect(Near(tr, 195f) && Near(tp, 200f), "an anchor off the right end pins the base and tip at that end");
        TailPoints(104f, 100f, 8f, 12f, 5f, out tl, out tr, out tp);
        check.Expect(Near(tl, 100f) && Near(tr, 108f), "a box narrower than the tail gets a tail its own width, no wider");
        TailPoints(100f, 100f, 0f, 12f, 5f, out tl, out tr, out tp);
        check.Expect(Near(tl, tr), "a zero-width box produces no tail at all");

        // ---- Separate ----
        // Two boxes at the same anchor: the second is pushed clear above the first, gap included.
        var sx = new float[3]; var sy = new float[3]; var sw = new float[3]; var sh = new float[3];
        sx[0] = 100f; sy[0] = 400f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 100f; sy[1] = 400f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 800f);
        check.Expect(Near(sy[0], 400f), "the first box never moves");
        check.Expect(Near(sy[1], 400f - 30f - 2f), "an overlapping box is pushed clear above, gap included");

        sx[0] = 0f; sy[0] = 400f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 300f; sy[1] = 400f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 800f);
        check.Expect(Near(sy[1], 400f), "a box that does not overlap is left alone");

        // The pushed box would leave the top of the screen: it re-clamps into the viewport instead.
        sx[0] = 0f; sy[0] = 0f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 0f; sy[1] = 0f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 800f);
        check.Expect(sy[1] == 0f, "a push past the top edge clamps back on-screen instead of vanishing");
        sx[0] = 0f; sy[0] = 780f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 0f; sy[1] = 780f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 20f);
        check.Expect(sy[1] == 0f, "a box taller than the viewport pins to the top edge");
        sx[0] = 0f; sy[0] = 0f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 0f; sy[1] = 0f; sw[1] = 80f; sh[1] = 30f;
        Separate(sx, sy, sw, sh, 2, 2f, 0f);
        check.Expect(Near(sy[1], -32f), "an unknown viewport leaves the push unclamped");

        // ---- Separate: the order list picks the participants AND the stacking sequence ----
        var so = new int[3];
        sx[0] = 0f; sy[0] = 400f; sw[0] = 80f; sh[0] = 30f;
        sx[1] = 0f; sy[1] = 400f; sw[1] = 80f; sh[1] = 30f;
        sx[2] = 0f; sy[2] = 400f; sw[2] = 80f; sh[2] = 30f;
        // Box 1 sits where box 0 lands once pushed: if it took part, box 0 would be shoved to 328 instead.
        sy[1] = 360f;
        so[0] = 2; so[1] = 0;                                   // box 1 is not in the list at all
        Separate(sx, sy, sw, sh, so, 2, 2f, 800f);
        check.Expect(Near(sy[2], 400f), "the first index in the order list keeps its placed position");
        check.Expect(Near(sy[0], 368f), "a later index is pushed above the earlier one, not above its array neighbour");
        check.Expect(Near(sy[1], 360f), "a box left out of the order list is never pushed");
        check.Expect(Near(sy[0], 368f), "and it never pushes either -- an excluded box is simply not there");

        // ---- OrderByRecency ----
        var okey = new long[4]; var oid = new uint[4]; var oin = new bool[4]; var ord = new int[4];
        okey[0] = 100; okey[1] = 300; okey[2] = 200; okey[3] = 50;
        oid[0] = 1; oid[1] = 2; oid[2] = 3; oid[3] = 4;
        for (int i = 0; i < 4; i++) oin[i] = true;
        int on = OrderByRecency(ord, okey, oid, oin, 4);
        check.Expect(on == 4 && ord[0] == 1 && ord[1] == 2 && ord[2] == 0 && ord[3] == 3,
          "interleaved timestamps land newest-first");
        oin[2] = false;
        on = OrderByRecency(ord, okey, oid, oin, 4);
        check.Expect(on == 3 && ord[0] == 1 && ord[1] == 0 && ord[2] == 3, "an excluded box is absent and off the count");
        oin[2] = true;

        okey[0] = okey[1] = okey[2] = okey[3] = 777;
        on = OrderByRecency(ord, okey, oid, oin, 4);
        check.Expect(ord[0] == 0 && ord[1] == 1 && ord[2] == 2 && ord[3] == 3, "equal timestamps tie-break on ascending id");
        int a0 = ord[0], a1 = ord[1], a2 = ord[2], a3 = ord[3];
        OrderByRecency(ord, okey, oid, oin, 4);
        check.Expect(ord[0] == a0 && ord[1] == a1 && ord[2] == a2 && ord[3] == a3,
          "the same input orders identically twice -- an equal-timestamp stack cannot flicker");

        okey[2] = long.MaxValue;
        on = OrderByRecency(ord, okey, oid, oin, 4);
        check.Expect(ord[0] == 2, "a plate's 'now' sentinel sorts ahead of every real timestamp");
        check.Expect(OrderByRecency(ord, okey, oid, oin, 0) == 0, "no boxes is an empty order list, not a fault");

        // ---- AbsorbPush ----
        float yo = 0f, lp = 0f;
        AbsorbPush(ref yo, ref lp, 0f);
        check.Expect(yo == 0f && lp == 0f, "a box that was never pushed absorbs nothing");
        AbsorbPush(ref yo, ref lp, -32f);
        check.Expect(Near(yo, 32f) && Near(lp, -32f), "a new push is cancelled by the residual, so the box does not jump");
        AbsorbPush(ref yo, ref lp, -32f);
        check.Expect(Near(yo, 32f), "an unchanged push leaves the residual for SettleOffset to close");
        AbsorbPush(ref yo, ref lp, 0f);
        check.Expect(yo == 0f && lp == 0f, "losing the push absorbs back the other way");

        // ---- Bands + reaction ----
        string[] stock = StockBandTexts;
        check.Expect(BandText(stock, 0) == "Uninjured" && BandText(stock, 5) == "Dead", "band table ends are the game's own words");
        check.Expect(BandText(stock, 2) == "Injured" && BandText(stock, 3) == "Heavily Wounded", "the stock fourth band is the game's Heavily Wounded");
        check.Expect(BandText(stock, -1) == "Uninjured" && BandText(stock, 99) == "Dead", "out-of-range band index clamps");
        check.Expect(ReferenceEquals(BandText(stock, 1), BandText(stock, 1)), "band text is a cached string, not built per call");

        // ---- BandCaptions: what the client's table resolved to ----
        string[] got = BandCaptions(new string?[] { "Fine", "", "   ", null, " Hurt ", "Gone" });
        check.Expect(got.Length == BandCount, "one caption per band");
        check.Expect(got[0] == "Fine" && got[5] == "Gone", "a resolved caption is shown as the table gave it");
        check.Expect(got[1] == "" && got[2] == "", "a blank or whitespace-only caption stays empty, the server chose no word");
        check.Expect(got[3] == "Heavily Wounded", "a failed lookup falls back to the stock word");
        check.Expect(got[4] == "Hurt", "a caption loses its stray edge spaces");
        string[] shortGot = BandCaptions(new string?[] { "Only" });
        check.Expect(shortGot[0] == "Only" && shortGot[1] == "Barely Injured" && shortGot[5] == "Dead", "bands past a short answer take the stock words");
        check.Expect(BandCaptions(new string?[] { null, null, null, null, null, null })[3] == "Heavily Wounded", "an all-failed lookup is the stock set");
        check.Expect(BandText(got, 1) == "" && BandText(got, -4) == "Fine" && BandText(got, 12) == "Gone", "a live caption set clamps like the stock one");

        // ---- A blank caption measures no width ----
        PlateMetrics withBlank = Measure(40f, 1, 0f, false, true, false, 10f, 1f);
        PlateMetrics noBand = Measure(40f, 1, 0f, false, false, false, 10f, 1f);
        check.Expect(Near(withBlank.W, noBand.W), "a blank band caption does not widen the plate");
        check.Expect(Near(withBlank.H - noBand.H, 10f), "a blank band caption keeps its line, where a spliced override can sit");
        PlateMetrics wideBand = Measure(40f, 1, 90f, false, true, false, 10f, 1f);
        check.Expect(Near(wideBand.W - withBlank.W, 50f), "a caption wider than the name widens the plate by the difference");

        var b0 = BandColor(0);
        check.Expect(Near(b0.r, 128f / 255f) && Near(b0.g, 1f) && Near(b0.b, 128f / 255f), "band 0 colour");
        var b4 = BandColor(4);
        check.Expect(Near(b4.r, 1f) && Near(b4.g, 80f / 255f) && Near(b4.b, 64f / 255f), "band 4 colour");
        var b5 = BandColor(5);
        check.Expect(Near(b5.r, 128f / 255f) && Near(b5.g, 128f / 255f) && Near(b5.b, 128f / 255f), "band 5 colour");
        check.Expect(BandColor(-3) == BandColor(0) && BandColor(42) == BandColor(5), "out-of-range band colour clamps");

        var friend = ReactionColor(1);
        check.Expect(Near(friend.r, 128f / 255f) && Near(friend.g, 1f) && Near(friend.b, 128f / 255f), "reaction 1 is green");
        var enemy = ReactionColor(2);
        check.Expect(Near(enemy.r, 1f) && Near(enemy.g, 80f / 255f) && Near(enemy.b, 64f / 255f), "reaction 2 is red");
        check.Expect(ReactionColor(0) == ReactionColor(200), "every other reaction is the same neutral");
        var neutral = ReactionColor(0);
        check.Expect(Near(neutral.b, 1f) && Near(neutral.r, 128f / 255f), "neutral is blue");

        // ---- BandOf: CNWSObject::GetDamageLevel's float thresholds, replayed ----
        check.Expect(BandOf(20, 20) == 0 && BandOf(19, 20) == 0, "exactly 0.95 is still uninjured (ucomiss 0.95f)");
        check.Expect(BandOf(189, 200) == 1 && BandOf(15, 20) == 1, "under 0.95 down to 0.75 is barely injured");
        check.Expect(BandOf(149, 200) == 2 && BandOf(10, 20) == 2, "under 0.75 down to 0.5 is injured");
        check.Expect(BandOf(99, 200) == 3 && BandOf(5, 20) == 3, "under 0.5 down to 0.25 is badly wounded");
        check.Expect(BandOf(1, 20) == 4, "alive under 0.25 is near death");
        check.Expect(BandOf(0, 20) == 5 && BandOf(-7, 20) == 5, "zero or below is dead");
        check.Expect(BandOf(0, 0) == 0 && BandOf(5, 0) == 0 && BandOf(-5, 0) == 5,
                     "a zero max replays the engine's NaN (uninjured), +inf (uninjured) and -inf (dead)");

        // ---- HealthFraction: exact HP when the server sent it and it agrees with the band, else the band ----
        check.Expect(Near(HealthFraction(37, 0, 50, BandOf(37, 50)), 0.74f), "exact HP drives the bar when it agrees with the band");
        check.Expect(Near(HealthFraction(30, 10, 50, BandOf(40, 50)), 0.8f), "temporary HP counts, as the band and the party bar count it");
        check.Expect(Near(HealthFraction(0, 0, 0, 2), 0.625f), "no HP from the server: the band's midpoint");
        check.Expect(Near(HealthFraction(50, 0, 50, 3), 0.375f), "stale HP that contradicts the band loses to the band");
        check.Expect(HealthFraction(60, 0, 50, 0) == 1f, "an overfull pool never draws past the track");
        check.Expect(HealthFraction(0, 0, 0, 0) == 1f && HealthFraction(0, 0, 0, 5) == 0f, "band 0 is a full bar, dead an empty one");
        check.Expect(HealthFraction(0, 0, 0, 42) == 0f && HealthFraction(0, 0, 0, -1) == 1f, "an out-of-range band clamps");

        // ---- SpeakerPrefixLen: the engine's "[name]: " on a placeable/door line ----
        static System.Collections.Generic.List<System.Text.Rune> R(string s)
        {
            var l = new System.Collections.Generic.List<System.Text.Rune>();
            foreach (System.Text.Rune r in s.EnumerateRunes()) l.Add(r);
            return l;
        }
        check.Expect(SpeakerPrefixLen(R("[Old Door]: Locked.")) == 12, "the bracketed speaker and its colon-space are the prefix");
        check.Expect(SpeakerPrefixLen(R("Locked.")) == 0, "no leading bracket, no prefix");
        check.Expect(SpeakerPrefixLen(R("[Old Door] Locked.")) == 0, "a bracket without the colon-space is the speaker's own text");
        check.Expect(SpeakerPrefixLen(R("[Sign]: ")) == 0, "a prefix with nothing after it is kept, never skipped to an empty line");
        check.Expect(SpeakerPrefixLen(R("")) == 0, "empty text has no prefix");

        // ---- CardParts: nameplate.speaker, and a plate stacked under the bubble showing each part once ----
        check.Expect(CardParts(3, false, true, true, true, out bool cn, out bool cp) && cn && cp, "name and portrait: both on the card");
        check.Expect(CardParts(1, false, true, true, true, out cn, out cp) && cn && !cp, "name only: no portrait");
        check.Expect(CardParts(2, false, true, true, true, out cn, out cp) && !cn && cp, "portrait only: no name");
        check.Expect(!CardParts(0, false, true, true, true, out _, out _), "neither: the stock plain boxes");
        check.Expect(!CardParts(3, true, true, true, true, out _, out _), "a stacked plate with a portrait takes over both");
        check.Expect(CardParts(3, true, false, true, true, out cn, out cp) && !cn && cp, "a stacked plate without a portrait leaves it to the card");
        check.Expect(!CardParts(3, false, true, false, false, out _, out _), "no name and no resref: no card");

        // ---- MeasureCard: portrait left, name header, body; top-aligned portrait sets the floor ----
        var card = MeasureCard(100f, 20f, 1, 3, 20f, 5f, 1f);
        check.Expect(Near(card.PortraitH, 60f) && Near(card.PortraitW, 38.4f), "the card portrait is its lines tall at the NWN aspect");
        check.Expect(Near(card.ColX, 5f + 38.4f + 8f) && Near(card.W, 5f + 38.4f + 8f + 100f + 5f), "the text column sits right of the portrait");
        check.Expect(Near(card.BodyY, 5f + 20f + 2f), "the body starts under the name header");
        check.Expect(Near(card.H, 70f), "a one-line card is as tall as its portrait plus the pad");
        var tall = MeasureCard(100f, 200f, 1, 3, 20f, 5f, 1f);
        check.Expect(Near(tall.H, 5f + 22f + 200f + 5f), "a long message grows the card past its portrait");
        var named = MeasureCard(100f, 20f, 1, 0, 20f, 5f, 1f);
        check.Expect(Near(named.H, 5f + 22f + 20f + 5f) && Near(named.ColX, 5f) && named.PortraitW == 0f, "a name header without a portrait");
        var faced = MeasureCard(100f, 20f, 0, 3, 20f, 5f, 1f);
        check.Expect(Near(faced.BodyY, 5f) && Near(faced.H, 70f), "a portrait without a name header");
        var titled = MeasureCard(100f, 20f, 2, 3, 20f, 5f, 1f);
        check.Expect(Near(titled.BodyY, 5f + 40f + 2f) && Near(titled.H, 5f + 42f + 20f + 5f),
                     "a two-line name (name + title) keeps both lines in the header");
        var twice = MeasureCard(100f, 20f, 1, 3, 20f, 10f, 2f);
        check.Expect(Near(twice.ColX, 10f + 38.4f + 16f) && Near(twice.BodyY, 10f + 20f + 4f), "scale widens only the chrome");
        var big = MeasureCard(100f, 20f, 1, 5, 20f, 5f, 1f);
        check.Expect(Near(big.PortraitH, 100f) && Near(big.ColX, 5f + 64f + 8f) && Near(big.H, 110f),
                     "a bigger portrait widens the column and sets the card's height");

        log.Info(check.Ok ? "PlateLayout.SelfCheck: passed" : "PlateLayout.SelfCheck: FAILED");
    }
}
