using System;
using System.Collections.Generic;
using System.Text;
using CX.Framework;

namespace CX.Ui;

/// <summary>The palette file a player shares or a server ships (<c>cxpal_&lt;id&gt;</c>): <c>key=value</c> lines,
/// <c>#</c> comments. <c>name</c> is the label, <c>base</c> a built-in palette the unnamed roles come from (slate when
/// absent), and every other key is a lowercase <see cref="Role"/> with a <c>#RRGGBB</c> colour.</summary>
public static class PaletteText
{
    /// <summary>A role's row label in the custom palette editor, index-aligned with <see cref="Role"/>.</summary>
    public static readonly string[] Labels =
    {
        "Window", "Window border", "Title bar", "Category column", "Submenu column", "Column divider", "Selected category",
        "Accent", "Accent, bright", "Input box", "Selection", "Text", "Text, secondary", "Text, faint", "Grip and icons",
        "Highlight text", "Switch off", "Control frame", "Control frame, dim", "Scroll track", "List", "Master row",
        "Notice strip", "Notice strip border", "Warning", "Disabled", "Badge frame", "Good",
    };

    /// <summary>A file's palette over its base; false (with <paramref name="error"/>) on the first bad line, so a
    /// broken file is refused whole rather than half-applied.</summary>
    public static bool TryParse(string text, Func<string, uint[]?> baseOf, out string label, out uint[] rgb, out string error)
    {
        label = ""; error = "";
        rgb = (uint[])(baseOf("slate") ?? throw new InvalidOperationException("slate is built in")).Clone();
        var set = new List<(Role, uint)>();
        int n = 0;
        foreach (string raw in text.Split('\n'))
        {
            n++;
            string line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line[0] == '#') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { error = $"line {n}: not key=value"; return false; }
            string key = line[..eq].Trim().ToLowerInvariant(), val = line[(eq + 1)..].Trim();
            if (key == "name") { label = val.Length > 32 ? val[..32] : val; continue; }
            if (key == "base")
            {
                uint[]? b = baseOf(val.ToLowerInvariant());
                if (b == null) { error = $"line {n}: no built-in palette '{val}'"; return false; }
                rgb = (uint[])b.Clone();
                continue;
            }
            if (!Theme.TryRoleName("@" + key, out Role role)) { error = $"line {n}: '{key}' is not a colour role"; return false; }
            if (!HexColour.TryParse(val, out uint c) || val.Length != 7) { error = $"line {n}: '{val}' is not #RRGGBB"; return false; }
            set.Add((role, c >> 8));
        }
        foreach (var (role, c) in set) rgb[(int)role] = c;
        return true;
    }

    /// <summary>The palette as a shareable file: every role written out, so it reads the same against any base.</summary>
    public static string Format(string label, uint[] rgb)
    {
        var sb = new StringBuilder("# CX palette: save as cxpal_<id>.txt in override/, or ship it in a hak or NWSync.\n");
        sb.Append("name=").Append(label).Append('\n');
        foreach (Role r in Enum.GetValues<Role>())
            sb.Append(r.ToString().ToLowerInvariant()).Append("=#").Append(rgb[(int)r].ToString("x6")).Append('\n');
        return sb.ToString();
    }

    /// <summary>Off-device. Oracle: hand-written palette files and the colours they name -- a role overrides the
    /// base, unnamed roles keep it, a bad line refuses the file, and a formatted palette parses back to itself.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        static uint[]? Base(string id) => id switch
        {
            "slate" => Fill(0x111111u), "ember" => Fill(0x222222u), _ => null,
        };
        static uint[] Fill(uint c) { var a = new uint[Enum.GetValues<Role>().Length]; Array.Fill(a, c); return a; }
        check.Expect(Labels.Length == Enum.GetValues<Role>().Length, "every role has a label");
        check.Expect(TryParse("# mine\nname=Dusk\naccent=#ff8000\n", Base, out string l, out uint[] p, out _)
                     && l == "Dusk" && p[(int)Role.Accent] == 0xff8000 && p[(int)Role.Tx] == 0x111111, "a role overrides slate; the rest stay");
        check.Expect(TryParse("base=ember\nTX = #010203\n", Base, out _, out p, out _)
                     && p[(int)Role.Tx] == 0x010203 && p[(int)Role.Accent] == 0x222222, "base picks the fallback; keys are case-blind");
        check.Expect(!TryParse("accent=#ff8000ff\n", Base, out _, out _, out string e1) && e1.StartsWith("line 1"), "#RRGGBBAA is refused");
        check.Expect(!TryParse("name=x\nsparkle=#ffffff\n", Base, out _, out _, out string e2) && e2.StartsWith("line 2"), "an unknown role is refused");
        check.Expect(!TryParse("base=neon\n", Base, out _, out _, out _), "an unknown base is refused");
        check.Expect(!TryParse("accent\n", Base, out _, out _, out _), "a line without = is refused");
        var round = Fill(0x0a0b0c); round[(int)Role.Warn] = 0xd9a35a;
        check.Expect(TryParse(Format("Round", round), Base, out l, out p, out _) && l == "Round" && p[(int)Role.Warn] == 0xd9a35a
                     && p[(int)Role.PanelBg] == 0x0a0b0c, "a formatted palette parses back to itself");
        log.Info(check.Ok ? "PaletteText.SelfCheck: passed" : "PaletteText.SelfCheck: FAILED");
    }
}
