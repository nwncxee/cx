using System;
using System.Collections.Generic;
using System.Text;

namespace CX.Framework;

public sealed partial class KnobGroup
{
    /// <summary>User rows one submenu may hold, every group in it counted: past this it is a wall of dials. A
    /// <see cref="Knob.ShowIf"/> row is its switch's detail, shown only while that asks for it, and is not counted.</summary>
    internal const int MaxUserRows = 14;
    /// <summary>Words that mark a row as a developer's (an engine-path comparison, a debug aid): such a knob is ChatOnly.</summary>
    internal static readonly string[] DevWords = { "A/B", "debug", "diagnostic", "bisect" };

    /// <summary>Off-device: the three constraints on a throwaway group (each must be caught), the
    /// bind/set/reset/format contract, then a walk of every LIVE declaration -- a real knob that broke one
    /// fails the suite, which is what makes <c>run-demo.sh</c> the gate.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var g = new KnobGroup("cx-selfcheck", "Test", "Test", "Enable", "help", true, Effect.Live, null, "nosuch.enabled", "", register: false);
        check.Expect(g.Master != null && g.Master.Key == "cx-selfcheck.enabled" && g.Master.V, "master declares <group>.enabled, first row, on");
        check.Expect(g.Parent == null, "an undeclared parent resolves to null rather than throwing");
        check.Expect(g.Knobs.Count == 1 && g.Knobs[0] == g.Master, "the master is the first row");

        var nohelp = g.Toggle("nohelp", "No help", false);
        check.Expect(nohelp.Tier == Tier.Advanced, "a User-tier knob without help is refused: demoted to Advanced");
        var withHelp = g.Toggle("help", "Help", false, help: "h");
        check.Expect(withHelp.Tier == Tier.User, "a User-tier knob with help keeps its tier");

        var bad = new Knob<int>(g, "badlayout", "Bad", KnobKind.Slider, Tier.Advanced, Effect.Live, "", "", true,
                                1, 0, 4, 1, "", null, null, null, null);
        check.Expect(bad.Layout && bad.Kind != KnobKind.Dropdown, "a layout knob that is not a Dropdown is only reachable past the factories");
        check.Expect(!Constraints(bad), "...and the constraint walk catches it");

        int changed = 0;
        var dens = g.Slider("density", "Density", 0f, 4f, 0.25f, 2f, help: "h", changed: () => changed++);
        check.Expect(dens.V == 2f && dens.Boot == 2f && dens.Default == 2f, "an absent key binds V and Boot to the default");
        check.Expect(dens.Parse("3") && dens.V == 3f && changed == 1, "a chat set parses, assigns and runs Changed");
        check.Expect(dens.Boot == 2f && dens.BootMoved && !dens.IsDefault, "a set moves V, not Boot");
        check.Expect(dens.Parse("9") && dens.V == 4f, "a slider set clamps to the range");
        check.Expect(!dens.Parse("abc") && dens.V == 4f, "unparseable text is refused and changes nothing");
        check.Expect(!dens.Parse("0,5"), "a comma-decimal float is refused under any locale");
        check.Expect(dens.Format() == "4" && dens.FormatDefault() == "2", "invariant float format");
        dens.Reset();
        check.Expect(dens.IsDefault && changed == 3, "reset is a set: back to default, Changed ran");

        var restart = g.Slider("cubes", "Cubes", 0, 64, 4, 32, effect: Effect.Restart, tier: Tier.Advanced);
        restart.Set(16);
        check.Expect(restart.V == 16 && restart.Boot == 32 && restart.BootIntact, "a Restart knob's Boot survives a set");
        check.Expect(restart.Describe() == "cx-selfcheck.cubes=16 (default 32) [advanced] [restart] (boot 32)", $"the /knobs line: {restart.Describe()}");
        restart.Boot = 16;
        check.Expect(!Constraints(restart), "a Restart knob whose Boot moved fails the constraint walk");
        restart.Boot = 32;

        var dd = g.Dropdown("shadow", "Shadow", new[] { 0, 1, 2, 4 }, 1, new[] { "none", "1", "2", "4" }, tier: Tier.Advanced);
        check.Expect(dd.Parse("4") && dd.V == 4, "a dropdown takes a stored value");
        check.Expect(dd.Parse("none") && dd.V == 0, "...or a label");
        check.Expect(dd.Parse("3") && dd.V == 3, "...or, on a numeric ladder, any value inside its span (chat is not held to the steps)");
        check.Expect(!dd.Parse("7") && dd.V == 3, "a value past the ladder's span is refused");
        check.Expect(dd.Format() == "3" && dd.RangeText() == "none, 1, 2, 4", "dropdown formats the stored value");
        var tm = g.Dropdown("tm", "Tm", new[] { "off", "shoulder", "aces" }, "aces", tier: Tier.Advanced);
        check.Expect(tm.Parse("1") && tm.V == "shoulder", "a non-numeric dropdown takes an index when nothing else matches");
        check.Expect(!tm.Parse("7") && tm.V == "shoulder", "an out-of-range index is refused");
        var tg = g.Toggle("tg", "Tg", true, tier: Tier.ChatOnly);
        check.Expect(tg.Parse("off") && !tg.V && tg.Parse("1") && tg.V && !tg.Parse("maybe"), "toggle accepts on/off/0/1 only");
        check.Expect(tg.Format() == "1", "toggle formats 0/1");
        var sw = g.Swatch("col", "Col", 0x11223344u, tier: Tier.Hidden);
        check.Expect(sw.Format() == "11223344" && sw.Parse("ffeeddcc") && sw.V == 0xffeeddccu && !sw.Parse("fff"), "swatch is rrggbbaa");
        check.Expect(g.Command("col ffffffff")!.StartsWith("[cx-selfcheck] unknown knob") && sw.V == 0xffeeddccu, "a Hidden knob has no command");
        var bag = g.Bag("w");
        bag.Replace(new[] { new KeyValuePair<string, string>("w0.x", "60"), new KeyValuePair<string, string>("w0.t0.label", "All") });
        check.Expect(bag.Pairs.Length == 2 && bag.Owns("cx-selfcheck.w0.x") && !bag.Owns("cx-selfcheck.col"), "a bag holds its pairs and owns its key family");
        check.Expect(g.Bags.Count == 1 && g.Knobs.Count == 9, "a bag is not a knob");
        int pressed = 0;
        var btn = g.Button("dump", "Dump", () => pressed++, Tier.ChatOnly);
        check.Expect(!btn.Persisted && btn.IsDefault && !btn.Parse("1"), "a button has no value and is never persisted");
        btn.Press();
        check.Expect(pressed == 1 && btn.Describe() == "cx-selfcheck.dump [button] [chatonly]", "a press runs its action");

        var lnk = g.Link("site", "Releases", "https://example.invalid/r");
        check.Expect(lnk.Tier == Tier.User && !lnk.Persisted && lnk.IsDefault && lnk.Format() == "https://example.invalid/r" && Constraints(lnk), "a link is a User row without help, never persisted");
        check.Expect(g.Command("site") == "[cx-selfcheck] Releases: https://example.invalid/r" && !g.Report().Contains("site"), "a link prints its address from chat and is on no value report");
        check.Expect(g.Command("") == $"[cx-selfcheck] {g.Report()}\nReleases: https://example.invalid/r" && g.Report().StartsWith("enabled 1 nohelp 0"), $"bare command reports every row, then the links: {g.Report()}");
        int confirmed = 0;
        var cbtn = g.Button("wipe", "Wipe", () => confirmed++, Tier.Advanced, confirm: true);
        check.Expect(cbtn.Confirm && g.Command("wipe") == "[cx-selfcheck] wipe" && confirmed == 1, "a confirming button fires straight away from chat; the two-click rule is the panel's");
        dens.Set(1f); tg.Set(false); nohelp.Set(true);
        g.ResetRows();
        check.Expect(dens.IsDefault && nohelp.IsDefault && !tg.V && sw.V == 0xffeeddccu, "ResetRows restores the User and Advanced rows and leaves ChatOnly and Hidden keys alone");
        tg.Set(true);
        check.Expect(g.Command("density 1.5") == "[cx-selfcheck] density 1.5 (was 2)", "the set form echoes new and old");
        check.Expect(g.Command("nosuch 1")!.StartsWith("[cx-selfcheck] unknown knob"), "an unknown knob is named");
        check.Expect(g.Command("dump") == "[cx-selfcheck] dump" && pressed == 2, "a button presses from chat");
        check.Expect(g.Command("reset")!.StartsWith("[cx-selfcheck] reset ->") && dens.IsDefault, "reset restores every default");
        check.Expect(sw.V == 0xffeeddccu && bag.Pairs.Length == 2, "...and leaves Hidden state alone");
        check.Expect(!g.Report().Contains("col ") && !g.Command("nosuch 1")!.Contains("col"), "Hidden state is on no report");
        g.Status = () => "status";
        check.Expect(g.Command("") == "[cx-selfcheck] status\nReleases: https://example.invalid/r", "a status line replaces the row report; the links follow it");
        var z = new KnobGroup("cx-selfcheck-zero", "Test", "Zero", null, null, true, Effect.Live, null, null, "", register: false);
        z.Status = () => "four\nlines";
        check.Expect(z.Knobs.Count == 0 && z.Command("") == "[cx-selfcheck-zero] four\nlines" && z.Command("reset") == z.Command("")
          && z.Command("nosuch 1") == z.Command(""), "a zero-knob group answers every form with its status line");

        // Ladders: a preset driver moves every member to its rung; a deviation survives until the next pick.
        var lq = new KnobGroup("cx-selfcheck-ladder", "Test", "Ladder", null, null, true, Effect.Live, null, null, "", register: false);
        string[] tiers = { "low", "high" };
        var preset = lq.Dropdown("preset", "Preset", tiers, "high", help: "h");
        var area = lq.Dropdown("area", "Area", tiers, "high", help: "h").Ladder(preset, "low", "high");
        var rung = lq.Slider("rung", "Rung", 0, 8, 1, 4, help: "h").Ladder(area, 1, 4);
        check.Expect(rung.V == 4 && rung.Default == 4 && rung.IsDefault, "a ladder member binds to the driver's rung");
        rung.Set(6);
        preset.Set("low");
        check.Expect(area.V == "low" && rung.V == 1 && rung.Default == 1 && rung.IsDefault, "a preset pick cascades: area, then its member, deviation dropped");
        rung.Set(3); rung.Reset();
        check.Expect(rung.V == 1, "a reset returns to the current rung, not the declared default");
        int fired = 0; rung.Changed = () => fired++;
        preset.Set("low");
        check.Expect(fired == 0, "re-picking the preset already in effect fires no member's Changed");
        rung.Set(3); preset.Set("low");
        check.Expect(fired == 2 && rung.V == 1, "...but still drops a nested deviation under an unchanged area");
        check.Expect(preset.Enforce("high") && area.V == "high" && rung.V == 4, "a server hold on the driver cascades");
        preset.Release();
        check.Expect(area.V == "low" && rung.V == 1, "...and so does its release");
        lq.Slider("wide", "Wide", 0, 8, 1, 4, help: "h").Ladder(preset, 1, 99);
        lq.Slider("short", "Short", 0, 8, 1, 4, help: "h").Ladder(preset, 1);
        check.Expect(preset.Members!.Count == 1, "a ladder with an out-of-range rung, or the wrong rung count, is refused");

        // The live walk: every real declaration so far, against the same three constraints.
        string[] v; lock (_all) v = _violations.ToArray();
        foreach (string w in v) check.Expect(false, $"declaration refused: {w}");
        foreach (KnobGroup lg in All)
            for (int i = 0; i < lg._knobs.Count; i++)
                check.Expect(Constraints(lg._knobs[i]), $"{lg._knobs[i].Key} breaks a registry constraint");
        // The panel is the player's: a submenu stays short, and no panel row is developer text.
        var userRows = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (KnobGroup lg in All)
            foreach (Knob k in lg._knobs)
            {
                if (k.Tier != Tier.User && k.Tier != Tier.Advanced) continue;
                if (k.Tier == Tier.User && k.Kind != KnobKind.Link && k.ShowIf == null)
                    userRows[lg.Category + " » " + lg.Submenu] = userRows.GetValueOrDefault(lg.Category + " » " + lg.Submenu) + 1;
                foreach (string word in DevWords)
                    check.Expect(k.Label.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0 && k.Help.IndexOf(word, StringComparison.OrdinalIgnoreCase) < 0,
                                 $"{k.Key} is a panel row that reads as developer text ('{word}'): make it ChatOnly");
            }
        foreach (var kv in userRows)
            check.Expect(kv.Value <= MaxUserRows, $"{kv.Key} has {kv.Value} User rows (cap {MaxUserRows}): merge, or move tuning to Advanced");

        // The gate on the LIVE registry (spec §4.1): /settings always, every generated command only when general.commands is on.
        var live = CommandRegistry.Global;
        check.Expect(live.Knows("/settings"), "/settings dispatches regardless of general.commands");
        foreach (KnobGroup lg in All)
            check.Expect(live.Knows("/" + lg.Name) == General.Commands.V, $"/{lg.Name} is gated by general.commands");

        log.Info(check.Ok ? "KnobGroup.SelfCheck: passed" : "KnobGroup.SelfCheck: FAILED");
    }
}
