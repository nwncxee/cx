using System;
using System.Collections.Generic;

namespace CX.Framework;

/// <summary>Area settings (<c>docs/area-settings.md</c>): an area's local variables named <c>cx.&lt;knob key&gt;</c>, kept in
/// its <c>.git</c>'s <c>VarTable</c>, hold those knobs while the player is in it, through <see cref="ServerPolicy"/>.</summary>
public static partial class AreaSettings
{
    /// <summary>The variable-name prefix that marks an area setting; matched case-insensitively.</summary>
    public const string Prefix = "cx.";

    /// <summary>Every <see cref="Prefix"/> variable of a GIT in file order: the knob key (lower-case, prefix dropped) and
    /// its value as knob text. Variables of other names, and ones with no scalar value, are skipped.</summary>
    public static List<(string Key, string Value)> Read(Gff git)
    {
        var named = new List<(string, string)>();
        if (git.Top.Get("VarTable") is not { } table) return named;
        foreach (GffStruct v in table.List)
            if (v.Get("Name")?.Text is string n && n.Length > Prefix.Length && n.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
                && v.Get("Value")?.AsValue is string value)
                named.Add((n[Prefix.Length..].ToLowerInvariant(), value));
        return named;
    }

    /// <summary>The holds <paramref name="named"/> makes: each key at its value, and every other knob of a holdable group
    /// it names at its default -- the master on -- so no dial of that feature is left to the player. A quality-preset
    /// rung stays the player's.</summary>
    public static List<(string Key, string Value)> Holds(List<(string Key, string Value)> named, Func<string, Knob?> find)
    {
        var holds = new List<(string, string)>(named);
        var groups = new List<KnobGroup>();
        foreach (var (key, _) in named)
            if (find(key) is { } k && ServerPolicy.Holdable(k.Key) && !groups.Contains(k.Group)) groups.Add(k.Group);
        foreach (KnobGroup g in groups)
            foreach (Knob k in g.Knobs)
                if (k.Persisted && !k.OnLadder && !named.Exists(n => n.Key == k.Key))
                    holds.Add((k.Key, k == g.Master ? "1" : k.FormatDefault()));
        return holds;
    }
}
