using CX.Framework;

namespace CX.Services.Nameplate;

/// <summary>The nameplate options (approved-spec): names kept apart or not (E1 to E5) and a title on its own line
/// (E11 to E16); E6 to E10, E17 and E18 went with the health bar. Oracle: the box positions and broken
/// names each example states, written here by hand as literals over two boxes that overlap.</summary>
internal static class PlateOptionsApprovedExamples
{
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
        check.Expect(!NameplateKnobs.Apart.Default && NameplateKnobs.Titles.Default,
                     "the settings ship as the examples name their defaults: apart off, titles on");
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
