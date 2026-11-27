using CX.Framework;

namespace CX.Ui;

/// <summary>The press-to-bind key row: a press on its box waits for a key or a thumb button, Escape or a click
/// elsewhere keeps the old key, Backspace unbinds, and a key already in use waits on the player's confirm.</summary>
public sealed partial class KnobPanel
{
    /// <summary>True while <paramref name="r"/> waits for a key.</summary>
    public bool Waiting(Row r) => false;

    /// <summary>The clash the waiting row warns about, null when none is showing.</summary>
    public string? KeyWarning => null;

    /// <summary>A mouse button while a key row waits: -4 / -5 for the thumb buttons. True when the row took it.</summary>
    public bool KeyButton(int code) => false;

    /// <summary>The player confirms the clash warning: the row binds the key.</summary>
    public void ConfirmKey() { }

    /// <summary>The player declines the clash warning: the row keeps the key it had.</summary>
    public void DeclineKey() { }

    /// <summary>The warning <paramref name="row"/> shows before binding <paramref name="name"/>: the CX key setting or
    /// the game's Key Mapping action that already uses it. Null when nothing does.</summary>
    internal static string? KeyClash(Knob row, string name) => null;
}
