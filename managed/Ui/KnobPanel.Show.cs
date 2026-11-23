using CX.Framework;

namespace CX.Ui;

/// <summary>Opening the panel at one group: the gear's press while its badge stands.</summary>
public sealed partial class KnobPanel
{
    /// <summary>Show <paramref name="g"/>'s category and submenu.</summary>
    public void Show(KnobGroup g)
    {
        int c = Array.FindIndex(Categories, n => string.Equals(n, g.Category, StringComparison.OrdinalIgnoreCase));
        if (c < 0) return;
        Category = c;
        SelectSub(0); SubCol.Reset(); Relayout();
        int s = Array.FindIndex(_subs, m => Array.IndexOf(m.Groups, g) >= 0);
        if (s > 0) { SelectSub(s); LayoutRows(); }
    }
}
