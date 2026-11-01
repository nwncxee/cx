using CX.Framework;

namespace CX.Ui;

/// <summary>Rows that show only while another knob allows: <see cref="Knob.ShowIf"/>.</summary>
public sealed partial class KnobPanel
{
    /// <summary>The knob's <see cref="Knob.ShowIf"/> says no: the layout skips its row.</summary>
    private static bool Hides(Knob k) => k.ShowIf != null && !k.ShowIf();

    /// <summary>Which of the group's conditional rows show, one character each: part of the status signature, so a
    /// press that shows or hides a row lays the panel out again.</summary>
    private static string ShowSig(KnobGroup g)
    {
        string sig = "";
        foreach (Knob k in g.Knobs) if (k.ShowIf != null) sig += Hides(k) ? '0' : '1';
        return sig;
    }
}
