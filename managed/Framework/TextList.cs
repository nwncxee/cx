using System.Collections;
using System.Text;

namespace CX.Framework;

/// <summary>
/// An immutable list of text items with value equality: what a <see cref="KnobKind.List"/> knob holds, so an edit
/// is a set of a new list and "is it the default" compares items, not references. Empty items are dropped. The
/// cfg / chat form is one line: the items joined with <c>|</c>, a <c>|</c> or <c>\</c> inside an item escaped with
/// <c>\</c>.
/// </summary>
public sealed class TextList : IReadOnlyList<string>, IEquatable<TextList>
{
    public static readonly TextList Empty = new(Array.Empty<string>());

    private readonly string[] _items;

    public TextList(IEnumerable<string> items)
    {
        var kept = new List<string>();
        foreach (string s in items) if (s.Length > 0) kept.Add(s);
        _items = kept.ToArray();
    }

    public int Count => _items.Length;
    public string this[int i] => _items[i];
    public IEnumerator<string> GetEnumerator() => ((IEnumerable<string>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>A copy with <paramref name="item"/> appended; an empty item leaves the list as it is.</summary>
    public TextList Add(string item) => item.Length == 0 ? this : new TextList(_items.Append(item));

    /// <summary>A copy with item <paramref name="i"/> replaced; an empty replacement removes it.</summary>
    public TextList With(int i, string item)
    {
        if ((uint)i >= (uint)_items.Length) return this;
        var next = (string[])_items.Clone();
        next[i] = item;
        return new TextList(next);
    }

    /// <summary>A copy without item <paramref name="i"/>.</summary>
    public TextList RemoveAt(int i) => With(i, "");

    public bool Equals(TextList? o) => o != null && _items.AsSpan().SequenceEqual(o._items);
    public override bool Equals(object? o) => Equals(o as TextList);
    public override int GetHashCode()
    {
        var h = new HashCode();
        foreach (string s in _items) h.Add(s);
        return h.ToHashCode();
    }

    /// <summary>The one-line cfg / chat form.</summary>
    public string Format()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _items.Length; i++)
        {
            if (i > 0) sb.Append('|');
            foreach (char c in _items[i])
            {
                if (c is '|' or '\\') sb.Append('\\');
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>The list <see cref="Format"/> wrote. Never fails: a trailing lone <c>\</c> is kept as itself.</summary>
    public static TextList Parse(string s)
    {
        var items = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length) { sb.Append(s[++i]); continue; }
            if (c == '|') { items.Add(sb.ToString()); sb.Clear(); continue; }
            sb.Append(c);
        }
        items.Add(sb.ToString());
        return new TextList(items);
    }

    /// <summary>Off-device proof of the value semantics and the one-line form.</summary>
    [SelfCheckSuite(SuitePhase.AfterServices)]
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var a = new TextList(new[] { ";bow *bows*", "", "a|b", "back\\slash", "x\\" });
        check.Expect(a.Count == 4 && a[1] == "a|b", "empty items are dropped, the rest kept in order");
        check.Expect(TextList.Parse(a.Format()).Equals(a), $"the one-line form round-trips separators and escapes ({a.Format()})");
        check.Expect(a.Format().Split('\n').Length == 1 && TextList.Parse("").Count == 0 && TextList.Parse("|||").Count == 0, "one line; blank and bare separators are an empty list");
        check.Expect(new TextList(new[] { "p", "q" }).Equals(TextList.Parse("p|q")) && EqualityComparer<TextList>.Default.Equals(TextList.Parse(""), Empty),
                     "equality compares items, so a fresh empty list is the default");
        var b = a.Add("new").With(0, ";nod *nods*").RemoveAt(1);
        check.Expect(b.Count == 4 && b[0] == ";nod *nods*" && b[1] == "back\\slash" && b[3] == "new" && a.Count == 4 && a[0] == ";bow *bows*",
                     "Add / With / RemoveAt return copies and leave the original alone");
        check.Expect(ReferenceEquals(a.Add(""), a) && ReferenceEquals(a.With(9, "z"), a) && a.With(0, "").Count == 3, "an empty add and an out-of-range edit change nothing; an empty edit removes");
        log.Info(check.Ok ? "TextList.SelfCheck PASS" : "TextList.SelfCheck had FAILURES");
    }
}
