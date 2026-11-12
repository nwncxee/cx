using CX.Framework;

namespace CX.Services.Nameplate;

/// <summary>The nameplate options (approved-spec, E1 to E16): names kept apart or not, the health bar's width, a
/// title on its own line. Oracle: the box positions, bar widths and broken names each example states, written here
/// by hand as literals over two boxes that overlap and two names of 20 and 180 px.</summary>
internal static class PlateOptionsApprovedExamples
{
    private const float LineH = 16f;
    /// <summary>The widths the checks give the two names: Bo, and Clarity Solforge the Machinist.</summary>
    private const float BoW = 20f, LongW = 180f;

    /// <summary>Two boxes that overlap, the first at y 100 and its neighbour at y 105, stacked as a frame stacks
    /// them. <paramref name="count"/> 1 leaves the neighbour out.</summary>
    private static void Stack(bool apart, bool firstBubble, bool secondBubble, int count, out float firstY, out float secondY)
    {
        float[] x = { 100f, 120f }, y = { 100f, 105f }, w = { 80f, 80f }, h = { 20f, 20f };
        long[] posted = { 2, 1 };
        uint[] ids = { 1, 2 };
        bool[] include = { PlateLayout.TakesPart(false, firstBubble, apart), PlateLayout.TakesPart(false, secondBubble, apart) };
        int[] order = new int[2];
        int n = PlateLayout.OrderByRecency(order, posted, ids, include, count);
        PlateLayout.Separate(x, y, w, h, order, n, 2f, 0f);
        firstY = y[0]; secondY = y[1];
    }

    private static bool Clear(float aY, float bY) => aY + 20f <= bY || bY + 20f <= aY;

    private static PlateMetrics Plate(float nameW, bool bar, float barW)
        => PlateLayout.Measure(nameW, 1, 0f, false, false, bar, LineH, 1f, 0, 0, 0f, barW);

    /// <summary>The name as a table entry holds it for drawing, set the way a sighting sets it.</summary>
    private static string Name(string name, bool titles)
    {
        var e = new PlateTable.Entry();
        PlateTable.SetName(e, PlateLayout.BreakName(name), null, titles);
        return e.Name;
    }

    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "PlateOptionsApprovedExamples.SelfCheck");

        // Given two creatures standing so close that their nameplates overlap, and Keep names apart is off
        // When both nameplates show
        // Then each nameplate sits directly over its own creature, one drawn over the other
        check.Expect(!NameplateKnobs.Apart.Default && NameplateKnobs.BarWidth.Default == 0f && NameplateKnobs.Titles.Default,
                     "the settings ship as the examples name their defaults: apart off, bar width 0, titles on");
        Stack(false, false, false, 2, out float a, out float b);
        check.Expect(a == 100f && b == 105f, $"E1 with the setting off both nameplates stay over their creatures: {a}, {b}");

        // Given two creatures standing so close that their nameplates overlap, and Keep names apart is on
        // When both nameplates show
        // Then the nameplates stack one above the other and do not overlap
        Stack(true, false, false, 2, out a, out b);
        check.Expect(Clear(a, b), $"E2 with the setting on the nameplates do not overlap: {a}, {b}");

        // Given a nameplate showing over a creature, and Keep names apart is off
        // When a neighbour's nameplate appears overlapping it
        // Then the first nameplate does not move
        Stack(false, false, false, 1, out float alone, out _);
        Stack(false, false, false, 2, out a, out _);
        check.Expect(alone == 100f && a == 100f, $"E3 the first nameplate holds still when a neighbour's appears: {alone} then {a}");

        // Given two creatures standing close together, and Keep names apart is off
        // When both speak and their chat bubbles would overlap
        // Then the chat bubbles stack one above the other and do not overlap
        Stack(false, true, true, 2, out a, out b);
        check.Expect(Clear(a, b), $"E4 chat bubbles still move apart with the setting off: {a}, {b}");

        // Given a nameplate showing over a creature, and Keep names apart is off
        // When a neighbour speaks and its chat bubble overlaps the nameplate
        // Then the nameplate stays directly over its creature
        Stack(false, false, true, 2, out a, out _);
        check.Expect(a == 100f, $"E5 a neighbour's chat bubble does not push the nameplate: {a}");

        // Given Health bar is on and Health bar width is 100 px, a creature named Bo and a creature named Clarity Solforge the Machinist
        // When both nameplates show
        // Then both health bars are the same width
        PlateMetrics bo = Plate(BoW, true, 100f), lng = Plate(LongW, true, 100f);
        check.Expect(bo.BarW == 100f && lng.BarW == 100f, $"E6 both health bars are 100 px wide: {bo.BarW}, {lng.BarW}");

        // Given Health bar is on and Health bar width is 0, a creature named Bo and a creature named Clarity Solforge the Machinist
        // When both nameplates show
        // Then each health bar is as wide as its own name, so the longer name has the longer bar
        PlateMetrics bo0 = Plate(BoW, true, 0f), lng0 = Plate(LongW, true, 0f);
        check.Expect(bo0.BarW == 20f && lng0.BarW == 180f, $"E7 at 0 each bar is as wide as its name: {bo0.BarW}, {lng0.BarW}");

        // Given Health bar is on and Health bar width is 100 px, and a creature named Bo
        // When its nameplate shows
        // Then the health bar is wider than the name, and the name is centred over the bar
        float nameX = PlateLayout.LineX(bo.NameX, bo.TextW, BoW);
        check.Expect(bo.BarX == 4f && nameX == 44f, $"E8 the 20 px name is centred over the 100 px bar: bar at {bo.BarX}, name at {nameX}");

        // Given Health bar is on and Health bar width is 100 px, and a creature named Clarity Solforge the Machinist
        // When its nameplate shows
        // Then the health bar is narrower than the name, and the bar is centred under the name
        nameX = PlateLayout.LineX(lng.NameX, lng.TextW, LongW);
        check.Expect(lng.BarX == 44f && nameX == 4f, $"E9 the 100 px bar is centred under the 180 px name: bar at {lng.BarX}, name at {nameX}");

        // Given Health bar is off and Health bar width is 100 px, and a creature named Bo
        // When its nameplate shows
        // Then no health bar shows, and the nameplate is the size it is with Health bar width at 0
        PlateMetrics off = Plate(BoW, false, 100f), off0 = Plate(BoW, false, 0f);
        check.Expect(off.BarW == 0f && off.W == 28f && off.H == 24f && off0.W == off.W && off0.H == off.H,
                     $"E10 with Health bar off the width changes nothing: bar {off.BarW}, box {off.W} x {off.H} against {off0.W} x {off0.H}");

        // Given a creature named Clarity Solforge [Wide-Eyed Machinist]
        // When its nameplate shows
        // Then the name is two lines: Clarity Solforge, and under it [Wide-Eyed Machinist]
        string s = Name("Clarity Solforge [Wide-Eyed Machinist]", true);
        check.Expect(s == "Clarity Solforge\n[Wide-Eyed Machinist]", $"E11 the bracketed title takes its own line: {s}");

        // Given a creature named Clarity Solforge (Wide-Eyed Machinist)
        // When its nameplate shows
        // Then the name is two lines: Clarity Solforge, and under it (Wide-Eyed Machinist)
        s = Name("Clarity Solforge (Wide-Eyed Machinist)", true);
        check.Expect(s == "Clarity Solforge\n(Wide-Eyed Machinist)", $"E12 parentheses break the same way: {s}");

        // Given a creature named Bo Marsh [AFK]
        // When its nameplate shows
        // Then the name is one line: Bo Marsh [AFK]
        s = Name("Bo Marsh [AFK]", true);
        check.Expect(s == "Bo Marsh [AFK]", $"E13 a short name stays on one line: {s}");

        // Given a creature named Clarity Solforge the Wide-Eyed Machinist
        // When its nameplate shows
        // Then the name draws as it does today: one line, unless it is wider than Text width
        s = Name("Clarity Solforge the Wide-Eyed Machinist", true);
        check.Expect(s == "Clarity Solforge the Wide-Eyed Machinist", $"E14 a long name with no brackets is left alone: {s}");

        // Given a creature named Clarity Solforge [Wide-Eyed Machinist] (Away From Keys)
        // When its nameplate shows
        // Then the name is three lines: Clarity Solforge, then [Wide-Eyed Machinist], then (Away From Keys)
        s = Name("Clarity Solforge [Wide-Eyed Machinist] (Away From Keys)", true);
        check.Expect(s == "Clarity Solforge\n[Wide-Eyed Machinist]\n(Away From Keys)", $"E15 two bracketed parts each take a line: {s}");

        // Given Titles on their own line is off, and a creature named Clarity Solforge [Wide-Eyed Machinist]
        // When its nameplate shows
        // Then the name draws as it does today: one line, unless it is wider than Text width
        s = Name("Clarity Solforge [Wide-Eyed Machinist]", false);
        check.Expect(s == "Clarity Solforge [Wide-Eyed Machinist]", $"E16 with the setting off the name is left alone: {s}");

        if (check.Ok) log.Info("PlateOptionsApprovedExamples.SelfCheck: passed");
    }
}
