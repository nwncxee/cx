using CX.Framework;
using CX.Services.Interact;
using static CX.Ui.PanelChrome;

namespace CX.Ui;

/// <summary>The press-to-bind key row: a press on its box waits for a key or a thumb button, Escape or a click
/// elsewhere keeps the old key, Backspace unbinds, and a key already in use waits on the player's confirm.</summary>
public sealed partial class KnobPanel
{
    /// <summary>The key row waiting for a key, null when none is. A knob, not a row: a re-layout keeps the wait.</summary>
    private Knob? _keyKnob;

    /// <summary>True while <paramref name="r"/> waits for a key.</summary>
    public bool Waiting(Row r) => _keyKnob != null && ReferenceEquals(r.K, _keyKnob);

    /// <summary>What a key row reads with no key bound, and what its box says while it waits.</summary>
    internal const string UnboundText = "Unbound", WaitText = "Press a key";

    /// <summary>The key row's box text: its key, or while it waits the key held behind a warning, else <see cref="WaitText"/>.</summary>
    internal string KeyBoxText(Row r) => Waiting(r) ? _keyPending ?? WaitText : r.Value;

    /// <summary>The SDL keycodes of the eight modifier keys: alone, they bind nothing.</summary>
    private const uint ModFirst = 0x400000E0, ModLast = 0x400000E7;  // not-an-offset: SDL key/event code

    /// <summary>A key while a key row waits: Escape keeps the old key, Backspace unbinds, any other key binds.</summary>
    private void WaitingKey(uint sdlKey)
    {
        if (sdlKey == KeyEscape) { StopWaiting(); return; }
        if (sdlKey is >= ModFirst and <= ModLast) return;
        if (_keyWarning != null && sdlKey is KeyReturn or KeyKpEnter) { ConfirmKey(); return; }
        if (sdlKey == KeyBackspace) { Choose(""); return; }
        string name = KeyNames.Name((int)sdlKey);
        if (name.Length > 0) Choose(name);
    }

    /// <summary>The waiting row takes <paramref name="name"/>: at once, or behind a warning when it clashes.</summary>
    private void Choose(string name)
    {
        if (_keyKnob is not Knob<string> ks) return;
        string? clash = KeyClash(ks, name);
        if (clash == null) { ks.Set(name); StopWaiting(); return; }
        _keyPending = name; _keyWarning = clash; LayoutRows();
    }

    /// <summary>Ends the wait and drops any warning; the row keeps the key it had.</summary>
    private void StopWaiting()
    {
        bool warned = _keyWarning != null;
        _keyKnob = null; _keyPending = null; _keyWarning = null;
        if (warned) LayoutRows();
    }

    /// <summary>A press anywhere ends the wait; true when it only dismissed a warning, so the press goes no further.</summary>
    private bool KeyDismissed()
    {
        bool warned = _keyWarning != null; StopWaiting(); return warned;
    }

    /// <summary>The help lines of key row <paramref name="r"/> while it warns: the clash and the two keys that answer it.</summary>
    private void KeyRowHelp(Row r, float w)
    {
        if (_keyWarning == null || !Waiting(r) || Small == null) return;
        r.Help = Wrap(Small, _keyWarning + " Enter binds it anyway; Esc keeps " + (r.K!.Format() is { Length: > 0 } old ? old : UnboundText) + ".", w);
        r.Warned = true;
    }

    /// <summary>A key row's box: its key, or while it waits a prompt in an accent frame.</summary>
    private void DrawKeyBox(VertexBatch b, Skin? rowArt, GlyphAtlas body, Row r, float top, float cy, float em, float alpha)
    {
        BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
        bool waiting = Waiting(r);
        bool art = Art(rowArt, NativeChrome.Edit, bx0, by0, bx1, by1, alpha);
        if (!art) Fill(b, bx0, by0, bx1, by1, Input, alpha);
        if (!art || waiting) Outline(b, bx0, by0, bx1, by1, waiting ? _keyWarning != null ? Warn : Accent2 : Frame2, alpha);
        UiText.Draw(b, body, KeyBoxText(r), bx0 + 0.4f * em, cy - body.LineHeight * 0.5f + body.Ascent, waiting ? Hi : Tx, bx1 - 0.3f * em, alpha);
    }

    /// <summary>A press on a key row: its box starts the wait.</summary>
    private void KeyPress(Row r, float top, float mx, float my)
    {
        BoxRect(r, top, out float bx0, out float bx1, out float by0, out float by1);
        if (In(mx, my, bx0, bx1, by0, by1)) _keyKnob = r.K;
    }

    /// <summary>The key the waiting row holds behind its clash warning, and the warning; null when none is showing.</summary>
    private string? _keyPending, _keyWarning;

    /// <summary>The clash the waiting row warns about, null when none is showing.</summary>
    public string? KeyWarning => _keyWarning;

    /// <summary>A mouse button while a key row waits: -4 / -5 for the thumb buttons. True when the row took it.</summary>
    public bool KeyButton(int code)
    {
        if (_keyKnob == null || code is not (-4 or -5)) return false;
        Choose(KeyNames.Name(code));
        return true;
    }

    /// <summary>The player confirms the clash warning: the row binds the key.</summary>
    public void ConfirmKey()
    {
        if (_keyKnob is Knob<string> ks && _keyPending != null) ks.Set(_keyPending);
        StopWaiting();
    }

    /// <summary>The player declines the clash warning: the row keeps the key it had.</summary>
    public void DeclineKey() => StopWaiting();

    /// <summary>The warning <paramref name="row"/> shows before binding <paramref name="name"/>: the CX key setting or
    /// the game's Key Mapping action that already uses it. Null when nothing does.</summary>
    internal static string? KeyClash(Knob row, string name)
    {
        int code = KeyNames.Code(name);
        if (code == 0) return null;
        if (KnobGroup.KeyHolder(code, row, KeyNames.Code) is { } k) return name + " is already bound to " + k.Label + " in " + k.Group.Title + ".";
        return InteractInput.GameKeyAction(code) is { } action ? name + " already does " + action + " in the game's Key Mapping." : null;
    }
}
