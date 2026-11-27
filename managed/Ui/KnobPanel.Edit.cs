using CX.Framework;

namespace CX.Ui;

/// <summary>The panel's text entry and keys: a field or list item being typed, its commit, and the keys the panel
/// takes while it owns the keyboard.</summary>
public sealed partial class KnobPanel
{
    /// <summary>The panel wants typed text: a field edit, or an open searchable list.</summary>
    public bool Editing => _editRow != null || Drop.Searching;
    public string EditText => _editText;
    public bool EditingRow(Row r) => ReferenceEquals(_editRow, r);

    private void BeginEdit(Row r, string seed) { _editRow = r; _editText = seed; _grab = null; }

    /// <summary>Longest text a box takes: a field or a consumer's label, and a list item (a chat shortcut holds a
    /// whole emote).</summary>
    public const int EditMax = 64, ItemMax = 240;

    /// <summary>Typed text (finished UTF-8 from the host): appended, capped at <see cref="EditMax"/> chars
    /// (<see cref="ItemMax"/> in a list item).</summary>
    public void EditInsert(string s)
    {
        if (Drop.Searching) { Drop.Type(s); return; }
        if (_editRow == null) return;
        int max = _editRow.Kind == RowKind.Item ? ItemMax : EditMax;
        foreach (char c in s)
            if (_editText.Length < max && c >= ' ' && c != '\n' && c != '\r') _editText += c;
    }

    public void EditBackspace()
    {
        if (Drop.Searching) { Drop.Backspace(); return; }
        if (_editRow != null && _editText.Length > 0) _editText = _editText.Substring(0, _editText.Length - 1);
    }

    /// <summary>Commit: a searching list accepts its top match; a consumer text row hands the text to
    /// <see cref="CommitLabel"/> and a consumer slider parses it for <see cref="ApplyValue"/>; a knob's text goes
    /// through its own Parse (a slider clamps, a swatch wants rrggbbaa, a channel row takes 0–255); a list item
    /// replaces its entry, appends from the add box, or is removed by an empty commit. Else the old value stands.</summary>
    public void CommitEdit() => Commit();

    /// <summary><see cref="CommitEdit"/>; true when a list gained or lost an item, so every row below it moved.</summary>
    private bool Commit()
    {
        if (Drop.Searching)
        {
            Row? dr = Drop.Tag as Row;
            int i = Drop.Accept();
            if (i >= 0 && dr != null) PickChoice(dr, i);
            return false;
        }
        Row? r = _editRow; string t = _editText;
        _editRow = null; _editText = "";
        if (r == null) return false;
        if (r.Kind == RowKind.Item) return CommitItem(r, t.Trim());
        if (t.Length == 0) return false;
        if (r.K == null)
        {
            if (r.Ctl == KnobKind.Text) CommitLabel?.Invoke(r, t);
            else if (float.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) Apply(r, v);
            return false;
        }
        if (r.Kind == RowKind.Channel)
        {
            if (int.TryParse(t, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int v)) SetChannel(r, v);
            return false;
        }
        if (r.K.Parse(t)) r.Dirty = true;
        return false;
    }

    /// <summary>A list item's edit as a set of the whole list, then a relayout; true when the item count changed.</summary>
    private bool CommitItem(Row r, string t)
    {
        if (r.K is not Knob<TextList> kl) return false;
        TextList v = kl.V;
        TextList next = r.Item >= v.Count ? v.Add(t) : v.With(r.Item, t);
        if (!next.Equals(v)) kl.Set(next);
        LayoutRows();
        return next.Count != v.Count;
    }

    /// <summary>Enter in a list's add box after an append: open the new add box, scrolled into view, so entries can be
    /// typed one after another.</summary>
    private void KeepAdding(Knob k)
    {
        Row? add = null;
        foreach (Row r in _rows) if (r.Kind == RowKind.Item && r.Adds && ReferenceEquals(r.K, k)) add = r;
        if (add == null) return;
        if (add.Top + add.H > List.Offset + List.H) { List.Offset = add.Top + add.H - List.H; List.Clamp(); }
        BeginEdit(add, "");
    }

    /// <summary>Escape: backs out of an edit, else closes an open list.</summary>
    public void CancelEdit()
    {
        if (_editRow != null) { _editRow = null; _editText = ""; return; }
        if (Drop.Open) Drop.Close();
    }

    /// <summary>SDL keycodes the panel acts on.</summary>
    private const uint KeyEscape = 0x1B, KeyReturn = 0x0D, KeyKpEnter = 0x40000058, KeyBackspace = 0x08;  // not-an-offset: SDL key/event code

    /// <summary>A key while the panel owns the keyboard. Escape backs out of an edit, then the list, then the panel.
    /// Returns true when the panel is still open afterwards.</summary>
    public bool Key(uint sdlKey)
    {
        if (!Open) return false;
        if (_keyKnob != null) { WaitingKey(sdlKey); return Open; }
        switch (sdlKey)
        {
            case KeyEscape:
                if (_editRow != null || Drop.Open) CancelEdit(); else Close();
                break;
            case KeyReturn: case KeyKpEnter:
            {
                Knob? adding = _editRow is { Kind: RowKind.Item, Adds: true } ar && _editText.Trim().Length > 0 ? ar.K : null;
                Commit();
                if (adding != null) KeepAdding(adding);
                break;
            }
            case KeyBackspace: EditBackspace(); break;
        }
        return Open;
    }
}
