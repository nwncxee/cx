using CX.Framework;

namespace CX.Services.Quickbar;

internal sealed partial class QuickbarLayout
{
    /// <summary>Off-device: the bar's size and slot rects stacked and side by side, the slot and grip hit-test, which set
    /// each strip shows (the plain one taking an unshown held set), the button gaps, and the keys' parse, bind, read-back, match, labels and refusals.
    /// Oracle: hand-written literals for the geometry (worked by hand from the design px) and the vanilla F1..F12 sets.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var l = new QuickbarLayout();
        l.Layout(1f, 1, false);
        check.Expect(l.W == 587.5f && l.H == 52.5f, $"one set at Size 1 is 587.5x52.5, 1.25x the design px ({l.W}x{l.H})");
        l.Layout(2f, 3, false);
        check.Expect(l.W == 1175f && l.H == 295f, $"three stacked at Size 2 is 1175x295 ({l.W}x{l.H})");
        l.Layout(1f, 3, true);
        check.Expect(l.W == 1742.5f && l.H == 52.5f, $"three side by side at Size 1 is 1742.5x52.5 ({l.W}x{l.H})");
        l.Layout(1f, 2, false, grip: false);
        check.Expect(l.W == 575f && !l.GripAt(1f, 1f), $"locked: no grip, 12.5 px narrower ({l.W})");

        l.Layout(1f, 3, false);
        l.X = 100f; l.Y = 200f;
        l.SlotRect(0, out float x0, out float y0, out float x1, out float y1);
        check.Expect(x0 == 116.25f && y0 == 203.75f && x1 == 161.25f && y1 == 248.75f, $"stacked slot 0 sits past the grip ({x0},{y0})-({x1},{y1})");
        l.SlotRect(35, out x0, out y0, out _, out _);
        check.Expect(x0 == 638.75f && y0 == 298.75f, $"stacked slot 35 ({x0},{y0})");
        check.Expect(l.ButtonOf(0, 0) == 24 && l.ButtonOf(35, 0) == 11 && l.ButtonOf(17, 1) == 17, "stacked: top strip Ctrl, bottom plain, held ignored");

        check.Expect(l.SlotAt(118f, 206f, 0) == 24, "top row of three is Ctrl's set: button 24");
        check.Expect(l.SlotAt(118f, 301f, 0) == 0, "bottom row is the plain set: button 0");
        check.Expect(l.SlotAt(355.5f, 253.5f, 0) == 17, "middle row col 5 is Shift's button 17");
        check.Expect(l.SlotAt(162f, 210f, 0) == 24, "the gap after a slot still lands on it");
        check.Expect(l.SlotAt(105f, 210f, 0) == 24 && l.GripAt(105f, 210f), "the grip lands on the row's first button, and is the grip");
        check.Expect(!l.GripAt(99f, 210f) && l.SlotAt(99f, 210f, 0) == -1, "left of the bar hits nothing");

        l.Layout(1f, 3, true);
        l.X = 100f; l.Y = 200f;
        l.SlotRect(12, out x0, out y0, out _, out _);
        check.Expect(x0 == 693.75f && y0 == 203.75f, $"side by side: slot 12 opens the second strip ({x0},{y0})");
        l.SlotRect(35, out x0, out _, out _, out _);
        check.Expect(x0 == 1793.75f, $"side by side slot 35 ({x0})");
        check.Expect(l.SlotAt(700f, 210f, 0) == 12 && l.SlotAt(688f, 210f, 0) == 11, "side by side: Shift's first button, and the strip gap lands on plain's last");

        l.Layout(1f, 1, false);
        l.X = 100f; l.Y = 200f;
        check.Expect(l.SlotAt(118f, 206f, 1) == 12 && l.SlotAt(118f, 206f, 2) == 24 && l.SlotAt(118f, 206f, 0) == 0, "one set shows the held set, as vanilla");
        l.Layout(1f, 2, false);
        check.Expect(l.ButtonOf(0, 2) == 12 && l.ButtonOf(12, 1) == 0, "two stacked: Shift above plain, Shift held changes nothing");
        check.Expect(l.ButtonOf(12, 2) == 24 && l.ButtonOf(0, 2) == 12, "two stacked: Ctrl held turns the plain row into Ctrl's, as vanilla's one row");
        l.Layout(1f, 2, false, gapX: 6f, gapY: 10f);
        l.X = 100f; l.Y = 200f;
        l.SlotRect(13, out x0, out y0, out _, out _);
        check.Expect(l.W == 642.5f && l.H == 110f && x0 == 168.75f && y0 == 261.25f, $"6 px across, 10 down: 642.5x110, slot 13 at ({x0},{y0})");
        check.Expect(l.SlotAt(118f, 252f, 0) == 12 && l.SlotAt(118f, 258f, 0) == 0, "a wide row gap splits at its middle");

        var keys = new QuickbarKeys();
        static int Fake(string n) => n switch { "F1" => 1001, "F2" => 1002, "F12" => 1012, "1" => 49, "q" => 113, "Keypad +" => 1100, "-" => 45, _ => 0 };
        int bad = keys.Parse(0, "F1 F2 - - - - - - - - - F12", Fake);
        bad += keys.Parse(1, "Shift+F1 1 q", Fake);
        bad += keys.Parse(2, "Ctrl+F1 Ctrl+Shift+F2 Bogus Alt+", Fake);
        check.Expect(bad == 2, $"Bogus and a bare 'Alt+' are refused ({bad})");
        check.Expect(keys.Match(1001, 0) == 0 && keys.Match(1012, 0) == 11, "plain F1 / F12 are buttons 0 / 11");
        check.Expect(keys.Match(1001, QuickbarKeys.Shift) == 12 && keys.Match(1001, QuickbarKeys.Ctrl) == 24, "Shift+F1 is 12, Ctrl+F1 is 24");
        check.Expect(keys.Match(1002, QuickbarKeys.Ctrl | QuickbarKeys.Shift) == 25, "Ctrl+Shift+F2 is 25");
        check.Expect(keys.Match(1002, QuickbarKeys.Ctrl) == -1 && keys.Match(1002, QuickbarKeys.Alt) == -1, "a modifier is matched exactly");
        check.Expect(keys.Match(49, 0) == 13 && keys.Match(113, 0) == 14, "row 2 binds 1 and q to 13 and 14");
        check.Expect(keys.Wants(1012) && keys.Wants(49) && !keys.Wants(1003), "Wants answers the base key of any binding");
        check.Expect(keys.Label(12) == "S-F1" && keys.Label(25) == "CS-F2" && keys.Label(13) == "1" && keys.Label(14) == "Q" && keys.Label(2) == "",
                     $"labels ({keys.Label(12)} {keys.Label(25)} {keys.Label(13)} {keys.Label(14)} '{keys.Label(2)}')");
        keys.Parse(0, "", Fake);
        check.Expect(keys.Match(1001, 0) == -1 && keys.Match(1001, QuickbarKeys.Shift) == 12, "an empty row unbinds only that row");
        keys.Bind(30, 1001, QuickbarKeys.Shift, "Keypad 1");
        check.Expect(keys.Match(1001, QuickbarKeys.Shift) == 30 && keys.Label(12) == "" && keys.Label(30) == "S-KP1", $"a bind takes the chord off its old button ({keys.Label(30)})");
        check.Expect(keys.Row(2) == "Ctrl+F1 Ctrl+Shift+F2 - - - - Shift+Keypad_1 - - - - -", $"the Ctrl row reads back ({keys.Row(2)})");

        check.Expect(keys.Parse(0, "Keypad_+ Minus", Fake) == 0 && keys.Match(1100, 0) == 0 && keys.Match(45, 0) == 1, "Keypad_+ keeps its plus, Minus is the - key");
        check.Expect(keys.Row(0) == "Keypad_+ Minus - - - - - - - - - -", $"and both read back ({keys.Row(0)})");

        log.Info(check.Ok ? "QuickbarLayout.SelfCheck: passed" : "QuickbarLayout.SelfCheck: FAILED");
    }
}
