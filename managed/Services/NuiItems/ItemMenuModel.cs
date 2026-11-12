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

    public ItemMenuRow(int order, string id, string label, string icon, bool enabled)
    {
        Order = order; Id = id; Label = label; Icon = icon; Enabled = enabled;
    }
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
    /// <summary>What a row key starts with: the rest is its order.</summary>
    internal const string RowPrefix = "row.";
    /// <summary>The most rows one menu may carry; a server past it is refused.</summary>
    internal const int MaxRows = 64;
    /// <summary>The longest label and icon name taken, in characters.</summary>
    internal const int MaxLabel = 64, MaxIcon = 16;

    private readonly Dictionary<int, ItemMenuRow> _rows = new();

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
        if (!key.StartsWith(RowPrefix, StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(key.AsSpan(RowPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int order))
            return $"the keys are {OpenKey} and {RowPrefix}<n>";
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
        _rows[order] = new ItemMenuRow(order, f[0], label, icon, enabled);
        Gen++;
        return null;
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
