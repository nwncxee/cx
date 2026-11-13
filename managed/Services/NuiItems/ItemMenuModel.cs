using System;
using System.Collections.Generic;
using System.Globalization;

namespace CX.Services.NuiItems;

/// <summary>One row of the server's item menu: what to send back when it is picked, and how to draw it.</summary>
internal readonly struct ItemMenuRow
{
    public readonly int Order;
    public readonly string Id, Label, Icon;
    public readonly bool Enabled;
    /// <summary>The row starts a group: a rule is drawn above it.</summary>
    public readonly bool RuleAbove;

    public ItemMenuRow(int order, string id, string label, string icon, bool enabled, bool ruleAbove = false)
    {
        Order = order; Id = id; Label = label; Icon = icon; Enabled = enabled; RuleAbove = ruleAbove;
    }
}

/// <summary>The server's look for its menu, so the extender's draws as the server's own NUI menu does. Each colour is
/// <c>rrggbbaa</c>, null where the NUI skin's stands; a row height of 0 follows the text.</summary>
internal sealed class ItemMenuLook
{
    public uint? Fill, Edge, Stripe, Text, HoverFill, HoverEdge, Rule;
    /// <summary>The row height in NUI units at UI scale 1; 0 when the server named none.</summary>
    public float RowHeight;
    /// <summary>The row's geometry in NUI units at UI scale 1; null when the server named none.</summary>
    public CX.Ui.CascadeRowLayout? Layout;
}

/// <summary>
/// The server's item menu as policy lines, with no engine in it: <c>open=&lt;x&gt;;&lt;y&gt;</c> says where it opens and
/// <c>row.&lt;n&gt;=&lt;id&gt;;&lt;label&gt;;&lt;icon&gt;;&lt;enabled&gt;</c> gives one row, drawn in the order of its
/// <c>n</c>. The rows are the server's only: this holds no row of its own and offers none when the server declared none.
/// </summary>
internal sealed class ItemMenuModel
{
    /// <summary>The key that opens the menu; the rest are rows.</summary>
    internal const string OpenKey = "open";
    /// <summary>The open value that places the menu at the pointer rather than at a pixel.</summary>
    internal const string PointerValue = "pointer";
    /// <summary>The keys for the server's look: seven colours, and the row height in NUI units.</summary>
    internal const string LookKey = "look", RowHeightKey = "rowheight";
    /// <summary>The tallest row height taken, in NUI units: past it a typo, not a menu.</summary>
    internal const float MaxRowHeight = 128f;
    /// <summary>What a row key starts with: the rest is its order.</summary>
    internal const string RowPrefix = "row.";
    /// <summary>The most rows one menu may carry; a server past it is refused.</summary>
    internal const int MaxRows = 64;
    /// <summary>The longest label and icon name taken, in characters.</summary>
    internal const int MaxLabel = 64, MaxIcon = 16;

    private readonly Dictionary<int, ItemMenuRow> _rows = new();

    /// <summary>The server's look; it outlives a close, so the next menu draws the same.</summary>
    public ItemMenuLook Look { get; } = new();

    /// <summary>True while the server says the menu is open.</summary>
    public bool Open { get; private set; }
    /// <summary>Where it opens, in the client's own pixels.</summary>
    public int X { get; private set; }
    public int Y { get; private set; }
    /// <summary>True when the server said <c>open=pointer</c>: it knows the element pressed, not the pixel, so the menu
    /// opens where the pointer is.</summary>
    public bool AtPointer { get; private set; }

    /// <summary>Where the menu opens: the pointer's pixel when it opened at the pointer, otherwise the server's point.</summary>
    public (int X, int Y) At(int pointerX, int pointerY) => AtPointer ? (pointerX, pointerY) : (X, Y);
    /// <summary>Bumps on every change, so a draw that showed one menu can tell the next one apart.</summary>
    public int Gen { get; private set; }
    public int RowCount => _rows.Count;

    /// <summary>The rows in the order the server numbered them. Cold: it sorts a copy, once per open.</summary>
    public ItemMenuRow[] Rows()
    {
        var list = new List<ItemMenuRow>(_rows.Values);
        list.Sort(static (a, b) => a.Order.CompareTo(b.Order));
        return list.ToArray();
    }

    /// <summary>One <c>menu.&lt;key&gt;</c> line, the prefix already off; an empty value releases it. The reason, when it refused.</summary>
    public string? Set(string key, string value)
    {
        key = key.Trim();
        if (key.Equals(OpenKey, StringComparison.OrdinalIgnoreCase))
        {
            if (value.Length == 0) { Close(); return null; }
            if (value.Trim().Equals(PointerValue, StringComparison.OrdinalIgnoreCase))
            {
                AtPointer = true; Open = true; Gen++;
                return null;
            }
            string[] parts = value.Split(';');
            if (parts.Length < 2
                || !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int x)
                || !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int y))
                return $"open takes <x>;<y> in pixels, or {PointerValue}";
            X = x; Y = y; AtPointer = false; Open = true; Gen++;
            return null;
        }
        if (key.Equals(LookKey, StringComparison.OrdinalIgnoreCase)) return SetLook(value);
        if (key.Equals(RowHeightKey, StringComparison.OrdinalIgnoreCase)) return SetRowHeight(value);
        if (!key.StartsWith(RowPrefix, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(key.AsSpan(RowPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int order))
            return $"the keys are {OpenKey}, {LookKey}, {RowHeightKey} and {RowPrefix}<n>";
        if (value.Length == 0)
        {
            if (_rows.Remove(order)) Gen++;
            return null;
        }
        if (_rows.Count >= MaxRows && !_rows.ContainsKey(order)) return $"a menu carries at most {MaxRows} rows";
        string[] f = value.Split(';');
        if (f.Length < 2 || f[0].Length == 0) return "a row takes <id>;<label>;<icon>;<enabled>";
        string label = f[1].Length > MaxLabel ? f[1][..MaxLabel] : f[1];
        string icon = f.Length > 2 && f[2].Length <= MaxIcon ? f[2] : "";
        bool enabled = f.Length <= 3 || f[3] != "0";
        bool ruleAbove = f.Length > 4 && f[4] == "1";
        _rows[order] = new ItemMenuRow(order, f[0], label, icon, enabled, ruleAbove);
        Gen++;
        return null;
    }

    /// <summary><c>look=&lt;fill&gt;;&lt;edge&gt;;&lt;stripe&gt;;&lt;text&gt;;&lt;hover fill&gt;;&lt;hover edge&gt;;&lt;rule&gt;</c>, each
    /// <c>#RRGGBB</c> or <c>#RRGGBBAA</c> or empty for the NUI skin's. Taken whole or refused whole; empty gives it all back.</summary>
    private string? SetLook(string value)
    {
        if (value.Length == 0)
        {
            Look.Fill = Look.Edge = Look.Stripe = Look.Text = Look.HoverFill = Look.HoverEdge = Look.Rule = null;
            Gen++;
            return null;
        }
        string[] f = value.Split(';');
        if (f.Length != 7) return "look takes seven colours: fill;edge;stripe;text;hover fill;hover edge;rule";
        var parsed = new uint?[7];
        for (int i = 0; i < 7; i++)
        {
            if (f[i].Length == 0) continue;
            if (!TryColour(f[i], out uint c)) return $"'{f[i]}' is not #RRGGBB or #RRGGBBAA";
            parsed[i] = c;
        }
        Look.Fill = parsed[0]; Look.Edge = parsed[1]; Look.Stripe = parsed[2]; Look.Text = parsed[3];
        Look.HoverFill = parsed[4]; Look.HoverEdge = parsed[5]; Look.Rule = parsed[6];
        Gen++;
        return null;
    }

    /// <summary><c>rowheight=&lt;units&gt;</c>, the row height in NUI units at UI scale 1; empty follows the text again.</summary>
    private string? SetRowHeight(string value)
    {
        if (value.Length == 0) { Look.RowHeight = 0f; Gen++; return null; }
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float h) || !(h > 0f) || h > MaxRowHeight)
            return $"rowheight takes a number of NUI units above 0, at most {MaxRowHeight}";
        Look.RowHeight = h;
        Gen++;
        return null;
    }

    /// <summary><c>#RRGGBB</c> (opaque) or <c>#RRGGBBAA</c> as <c>rrggbbaa</c>.</summary>
    internal static bool TryColour(string s, out uint rgba)
    {
        rgba = 0;
        if (s.Length is not (7 or 9) || s[0] != '#'
            || !uint.TryParse(s.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v))
            return false;
        rgba = s.Length == 7 ? (v << 8) | 0xFF : v;
        return true;
    }

    /// <summary>Everything the server declared is gone: the menu is closed and holds no row.</summary>
    public void Close()
    {
        bool had = Open || _rows.Count != 0;
        Open = false;
        AtPointer = false;
        _rows.Clear();
        if (had) Gen++;
    }

    /// <summary>The <c>/policy</c> line.</summary>
    public string Report() =>
        !Open ? $"menu: closed, {RowCount} row(s) held"
        : AtPointer ? $"menu: open at the pointer with {RowCount} row(s)"
        : $"menu: open at {X},{Y} with {RowCount} row(s)";
}
