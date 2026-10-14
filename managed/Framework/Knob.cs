using System;
using System.Collections.Generic;
using System.Globalization;

namespace CX.Framework;

/// <summary>How the panel renders a knob (ADR 0025). A Button has no value and is never persisted; a Link is
/// an address the row copies to the clipboard on press (spec §3), never persisted either. A List holds a
/// <see cref="TextList"/>: one text box per item and a trailing box that adds one.</summary>
public enum KnobKind { Toggle, Slider, Dropdown, Swatch, Button, Text, Link, List }

/// <summary>Who sees a knob: a User row (help mandatory), a collapsed Advanced row, a chat command and
/// <c>/knobs</c> line only, or persisted state with no surface at all.</summary>
public enum Tier { User, Advanced, ChatOnly, Hidden }

/// <summary>Live = a set applies now; Restart = readers take <c>Boot</c>, the panel shows the badge.</summary>
public enum Effect { Live, Restart }

/// <summary>
/// One declared knob: the metadata every view -- the panel row, the <c>cx.cfg</c> key, the
/// <c>/&lt;group&gt;</c> chat form and the <c>/knobs</c> line -- reads (spec §1). The typed half is
/// <see cref="Knob{T}"/>; a <see cref="KnobButton"/> is the valueless kind. Only <see cref="KnobGroup"/>
/// constructs these.
/// </summary>
public abstract class Knob
{
    /// <summary>The cfg key, <c>&lt;group&gt;.&lt;knob&gt;</c>; the chat form is <c>/&lt;group&gt; &lt;knob&gt;</c>.</summary>
    public string Key { get; }
    /// <summary>The knob segment of <see cref="Key"/>: what chat types after the group name.</summary>
    public string Name { get; }
    public KnobGroup Group { get; }
    public string Label { get; }
    public KnobKind Kind { get; }
    /// <summary>Demoted to Advanced at declaration when a User row carries no help.</summary>
    public Tier Tier { get; internal set; }
    public Effect Effect { get; }
    /// <summary>Optional header inside the submenu, "" when none.</summary>
    public string Section { get; }
    /// <summary>Row help / tooltip. "" when none.</summary>
    public string Help { get; }
    /// <summary>Placeholder an empty box shows: a List's add box. "" when none.</summary>
    public string Hint { get; init; } = "";
    /// <summary>Layout-affecting: only the Dropdown factory can set it, so the panel never has to re-lay
    /// out under a slider drag.</summary>
    public bool Layout { get; }
    /// <summary>Side effect after a set (on the setter's thread); a Button's press.</summary>
    public Action? Changed;
    /// <summary>A row that exists but cannot be edited yet: the panel
    /// greys it and ignores its presses; chat and the file still read it.</summary>
    public bool Disabled;
    /// <summary>A server policy (<see cref="ServerPolicy"/>) holds this knob: <c>V</c> is the server's value, the
    /// player's own lives on underneath and is what the file keeps. The panel greys the row.</summary>
    public bool Enforced { get; private protected set; }

    private protected Knob(KnobGroup group, string name, string label, KnobKind kind, Tier tier, Effect effect,
                           string section, string help, bool layout)
    {
        Group = group; Name = name.ToLowerInvariant(); Key = group.Name + "." + Name; Label = label; Kind = kind;
        Tier = tier; Effect = effect; Section = section; Help = help; Layout = layout;
    }

    /// <summary>The live value in cfg / chat / <c>/knobs</c> form; "" for a Button.</summary>
    public abstract string Format();
    /// <summary>The default in the same form.</summary>
    public abstract string FormatDefault();
    /// <summary>The boot value in the same form.</summary>
    public abstract string FormatBoot();
    /// <summary>Parse <paramref name="s"/> and set the live value (a set: runs <see cref="Changed"/>, arms the
    /// writer). False, and nothing changes, when the text is not a value of this knob.</summary>
    public abstract bool Parse(string s);
    /// <summary>True when the live value equals the default -- the key is then absent from the file.</summary>
    public abstract bool IsDefault { get; }
    /// <summary>True when the live value differs from what booted: the amber restart badge.</summary>
    public abstract bool BootMoved { get; }
    /// <summary>Back to the default, as a set.</summary>
    public abstract void Reset();
    /// <summary>Hold the live value at <paramref name="raw"/> for the session's server: the player's value is kept
    /// underneath, <see cref="Changed"/> runs, the writer is not armed. False when the text is no value of this knob.</summary>
    public abstract bool Enforce(string raw);
    /// <summary>Drop the server's hold: the player's value is live again.</summary>
    public abstract void Release();
    /// <summary>The player's value in cfg form -- what <see cref="KnobFile"/> writes, never the server's.</summary>
    public abstract string FormatStored();
    /// <summary>The player's value equals the default: its key is absent from the file.</summary>
    public abstract bool StoredIsDefault { get; }
    /// <summary>Persisted to <c>cx.cfg</c>: everything but a Button or a Link.</summary>
    public bool Persisted => Kind != KnobKind.Button && Kind != KnobKind.Link;
    /// <summary>Human-readable range / choices for a usage line, "" when none.</summary>
    public abstract string RangeText();
    /// <summary>Index of the live value in a Dropdown's <c>Values</c>, -1 when it is none of them (or the knob has no
    /// choices). Alloc-free, so the panel may read it every frame.</summary>
    public abstract int ChoiceIndex { get; }
    /// <summary>How many choices a Dropdown has; 0 otherwise.</summary>
    public virtual int ChoiceCount => 0;
    /// <summary>The knobs whose default is a rung of this one's ladder (<see cref="Knob{T}.Ladder"/>): a set
    /// moves each to the rung of the new choice. Null when nothing ladders on it.</summary>
    internal List<Knob>? Members;
    /// <summary>The driver picked choice <paramref name="i"/>: take that rung as the default, and set it.</summary>
    internal virtual void Rung(int i) { }

    /// <summary>Move every ladder member to the rung of this knob's current choice (a set, an enforce, a release).</summary>
    private protected void Cascade()
    {
        if (Members == null) return;
        int ci = ChoiceIndex;
        for (int i = 0; i < Members.Count; i++) Members[i].Rung(ci);
    }
    /// <summary>The SelfCheck's witness that <c>Boot</c> never moved: it is a public field by the spec's
    /// shape, so "immutable after a set" is checked rather than enforced.</summary>
    internal abstract bool BootIntact { get; }

    /// <summary>The <c>/knobs</c> line: <c>key=value (default d) [tier] [restart]</c>, <c>(boot b)</c> when
    /// the boot value differs from the live one.</summary>
    public string Describe()
    {
        string tier = Tier.ToString().ToLowerInvariant();
        if (Kind == KnobKind.Button) return $"{Key} [button] [{tier}]";
        if (Kind == KnobKind.Link) return $"{Key} [link] {Format()} [{tier}]";
        string s = $"{Key}={Format()} (default {FormatDefault()}) [{tier}]";
        if (Effect == Effect.Restart) s += " [restart]";
        if (BootMoved) s += $" (boot {FormatBoot()})";
        if (Enforced) s += $" [server; yours {FormatStored()}]";
        return s;
    }

    /// <summary>Set from a set: Changed on the setter's thread, then the writer is armed. Never on a bind.</summary>
    private protected void AfterSet()
    {
        Changed?.Invoke();
        if (!Enforced) Cascade();
        if (Persisted && Group.Registered) KnobFile.Arm();
    }
}

/// <summary>
/// The typed knob, and the storage: a feature reads <c>Grass.Density.V</c> as a plain field read, so it is
/// hot-path safe. <c>Boot</c> is bound at construction and never moves, which is what a Restart-effect reader
/// reads. Built-in <typeparamref name="T"/>: <c>bool</c>, <c>int</c>, <c>float</c>, <c>string</c>, <c>uint</c>
/// (swatch, <c>rrggbbaa</c>) and <see cref="TextList"/>; any other T supplies a format/parse pair.
/// </summary>
public sealed class Knob<T> : Knob
{
    /// <summary>The live value: what the panel edits and the cfg persists. Plain field by decision (spec
    /// §1 Threading): a scalar write is atomic and no two knobs change together.</summary>
    public T V;
    /// <summary>Bound at construction from the loaded file (re-bound once by <see cref="Ladder"/> when the key is unset),
    /// never changes after; Restart readers read this.</summary>
    public T Boot;
    /// <summary>The default: fixed, or the rung of a <see cref="Ladder"/>'s driver (a quality preset). An unset
    /// key follows it, so a preset change moves every knob the player has not touched.</summary>
    public T Default { get; private set; }
    /// <summary>Slider range; the panel snaps to <see cref="Step"/>, chat accepts any in-range value.</summary>
    public readonly T Min, Max, Step;
    public readonly string Unit;
    /// <summary>Dropdown: labels shown / values stored, index-aligned; an <see cref="Open"/> one may be re-offered.</summary>
    public string[] Choices { get; private set; }
    public T[] Values { get; private set; }
    /// <summary>A Dropdown whose values are found at runtime: any text its parser accepts is a value, listed or not
    /// (the file may name one before it is found), and <see cref="Offer"/> replaces the list.</summary>
    public bool Open { get; }

    private T _bound;
    private T[]? _rungs;
    /// <summary>The player's value while <see cref="Knob.Enforced"/>; meaningless otherwise.</summary>
    private T _user = default!;
    private readonly Func<T, string> _fmt;
    private readonly Func<string, (bool Ok, T V)> _parse;

    internal Knob(KnobGroup group, string name, string label, KnobKind kind, Tier tier, Effect effect,
                  string section, string help, bool layout, T def, T min, T max, T step, string unit,
                  string[]? choices, T[]? values, Func<T, string>? fmt, Func<string, (bool, T)>? parse, bool open = false)
        : base(group, name, label, kind, tier, effect, section, help, layout)
    {
        Open = open;
        Default = def; Min = min; Max = max; Step = step; Unit = unit;
        Values = values ?? Array.Empty<T>();
        _fmt = fmt ?? KnobCodec<T>.Format ?? throw new ArgumentException($"{Key}: no Format for {typeof(T).Name}");
        _parse = parse ?? KnobCodec<T>.Parse ?? throw new ArgumentException($"{Key}: no Parse for {typeof(T).Name}");
        Choices = choices ?? Array.ConvertAll(Values, v => _fmt(v));
        T v = def;
        string? raw = Settings.Raw(Key);
        if (raw != null && TryParse(raw, out T p)) v = p;
        V = Boot = _bound = v;
    }

    /// <summary>A set: clamp to the range, assign, run <see cref="Changed"/>, arm the writer. Under a server hold
    /// the player's value moves underneath and <c>V</c> stays the server's.</summary>
    public void Set(T v)
    {
        if (Enforced) { _user = Clamp(v); AfterSet(); return; }
        V = Clamp(v);
        AfterSet();
    }

    public override bool Enforce(string raw)
    {
        if (!Persisted || !TryParse(raw, out T v)) return false;
        if (!Enforced) _user = V;
        Enforced = true;
        V = v;
        Changed?.Invoke();
        Cascade();
        return true;
    }

    public override void Release()
    {
        if (!Enforced) return;
        Enforced = false;
        V = _user;
        Changed?.Invoke();
        Cascade();
    }

    public override string FormatStored() => _fmt(Enforced ? _user : V);
    public override bool StoredIsDefault => EqualityComparer<T>.Default.Equals(Enforced ? _user : V, Default);

    public override bool Parse(string s)
    {
        if (!TryParse(s, out T v)) return false;
        Set(v);
        return true;
    }

    /// <summary>Text to value with the knob's rules: a Dropdown takes a stored value, a label, any number
    /// inside a numeric ladder's span (spec §1: chat is not held to the panel's steps), or an index; a Slider
    /// is clamped to its range. No side effect.</summary>
    public bool TryParse(string s, out T v)
    {
        s = s.Trim();
        if (Kind == KnobKind.Dropdown)
        {
            for (int i = 0; i < Values.Length; i++)
                if (string.Equals(_fmt(Values[i]), s, StringComparison.OrdinalIgnoreCase)) { v = Values[i]; return true; }
            for (int i = 0; i < Choices.Length; i++)
                if (string.Equals(Choices[i], s, StringComparison.OrdinalIgnoreCase)) { v = Values[i]; return true; }
            if (NumericSteps && _parse(s) is (true, T raw) && InSpan(raw)) { v = raw; return true; }
            if (Open && _parse(s) is (true, T any)) { v = any; return true; }
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int ix)
                && ix >= 0 && ix < Values.Length) { v = Values[ix]; return true; }
            v = Default;
            return false;
        }
        var (ok, p) = _parse(s);
        v = ok ? Clamp(p) : Default;
        return ok;
    }

    /// <summary>A numeric dropdown: the panel offers its steps, chat may name any value between them.</summary>
    private static bool NumericSteps => typeof(T) == typeof(int) || typeof(T) == typeof(float);

    private bool InSpan(T v)
    {
        var c = Comparer<T>.Default;
        bool inside = false;
        for (int i = 0; i < Values.Length; i++)
            for (int j = 0; j < Values.Length; j++)
                if (c.Compare(v, Values[i]) >= 0 && c.Compare(v, Values[j]) <= 0) inside = true;
        return inside;
    }

    private T Clamp(T v)
    {
        if (Kind != KnobKind.Slider) return v;
        var c = Comparer<T>.Default;
        if (c.Compare(v, Min) < 0) return Min;
        if (c.Compare(v, Max) > 0) return Max;
        return v;
    }

    public override string Format() => _fmt(V);
    public override string FormatDefault() => _fmt(Default);
    public override string FormatBoot() => _fmt(Boot);
    public override bool IsDefault => EqualityComparer<T>.Default.Equals(V, Default);
    public override bool BootMoved => !EqualityComparer<T>.Default.Equals(V, Boot);
    internal override bool BootIntact => EqualityComparer<T>.Default.Equals(Boot, _bound);
    public override void Reset() => Set(Default);
    public override int ChoiceIndex => Array.IndexOf(Values, V);

    /// <summary>An <see cref="Open"/> dropdown's new list, index-aligned; the live value is left alone. Cold.</summary>
    public void Offer(T[] values, string[] choices)
    {
        if (!Open || values.Length != choices.Length) { Group.Refuse($"{Key}: Offer needs an open dropdown and aligned lists"); return; }
        Values = values; Choices = choices;
    }
    public override int ChoiceCount => Values.Length;

    /// <summary>Make the default a rung of <paramref name="driver"/>'s ladder: <c>rungs[driver.ChoiceIndex]</c>.
    /// Declared right after the knob, before any reader: an unset key re-binds to the rung, and every later set of
    /// the driver moves this knob to the new rung (a preset pick drops the player's deviation, as a reset does).</summary>
    public Knob<T> Ladder(Knob driver, params T[] rungs)
    {
        if (driver.Kind != KnobKind.Dropdown || rungs.Length != driver.ChoiceCount)
        { Group.Refuse($"{Key}: ladder of {rungs.Length} rungs on {driver.Key} with {driver.ChoiceCount} choices"); return this; }
        for (int i = 0; i < rungs.Length; i++)
            if (!RungFits(rungs[i])) { Group.Refuse($"{Key}: rung {i} ({_fmt(rungs[i])}) is not a value of this knob"); return this; }
        _rungs = rungs;
        (driver.Members ??= new()).Add(this);
        int ix = driver.ChoiceIndex;
        if ((uint)ix < (uint)rungs.Length)
        {
            Default = rungs[ix];
            if (Settings.Raw(Key) is not string raw || !TryParse(raw, out _)) V = Boot = _bound = Default;
        }
        return this;
    }

    private bool RungFits(T v)
    {
        if (Kind == KnobKind.Dropdown) return Array.IndexOf(Values, v) >= 0;
        if (Kind != KnobKind.Slider) return true;
        var c = Comparer<T>.Default;
        return c.Compare(v, Min) >= 0 && c.Compare(v, Max) <= 0;
    }

    /// <summary>The default under the driver's choice <paramref name="i"/>, read without moving anything: the
    /// declared default when this knob is no ladder member.</summary>
    internal T RungValue(int i) => _rungs != null && (uint)i < (uint)_rungs.Length ? _rungs[i] : Default;

    internal override void Rung(int i)
    {
        if (_rungs == null || (uint)i >= (uint)_rungs.Length) return;
        Default = _rungs[i];
        // An unchanged value skips the set (and its Changed: a rebake per member), but its own members still re-rung.
        if (!EqualityComparer<T>.Default.Equals(V, Default)) Set(Default);
        else if (!Enforced) Cascade();
    }

    public override string RangeText() => Kind switch
    {
        KnobKind.Slider => $"{_fmt(Min)}-{_fmt(Max)}{(Unit.Length > 0 ? " " + Unit : "")}",
        KnobKind.Dropdown => string.Join(", ", Choices),
        KnobKind.Toggle => "on/off",
        KnobKind.Swatch => "rrggbbaa",
        KnobKind.List => "items separated by |",
        _ => "",
    };
}

/// <summary>A valueless knob: pressing it runs <see cref="Knob.Changed"/>. Never persisted; tier decides row
/// vs command.</summary>
public sealed class KnobButton : Knob
{
    /// <summary>The panel asks for a second press within <see cref="ConfirmSec"/> before it fires (the revoke
    /// rows, spec §4.5); chat presses straight away.</summary>
    public readonly bool Confirm;
    /// <summary>The window a confirming button's first press stays armed for.</summary>
    public const float ConfirmSec = 3f;
    /// <summary>When set and false, the panel greys the row and ignores its press (spec §4.5's Bind row outside a
    /// session); chat still presses, so the action guards itself. Read per frame: must not allocate.</summary>
    public Func<bool>? Enabled;

    internal KnobButton(KnobGroup group, string name, string label, Tier tier, string section, string help, Action press, bool confirm)
        : base(group, name, label, KnobKind.Button, tier, Effect.Live, section, help, layout: false)
    { Changed = press; Confirm = confirm; }

    public void Press() => Changed?.Invoke();

    public override string Format() => "";
    public override string FormatDefault() => "";
    public override string FormatBoot() => "";
    public override bool Parse(string s) => false;
    public override bool IsDefault => true;
    public override bool BootMoved => false;
    public override void Reset() { }
    public override string RangeText() => "";
    public override int ChoiceIndex => -1;
    internal override bool BootIntact => true;
    public override bool Enforce(string raw) => false;
    public override void Release() { }
    public override string FormatStored() => Format();
    public override bool StoredIsDefault => true;
}

/// <summary>A knob-less element with a knob's place in the row order (spec §3 link row): an address the panel
/// shows under its label and copies to the clipboard on press; bare <c>/&lt;group&gt;</c> prints it after the
/// status line. No value, no default, never persisted; stays live under an off master like a Button.</summary>
public sealed class KnobLink : Knob
{
    public readonly string Url;

    internal KnobLink(KnobGroup group, string name, string label, string url, Tier tier, string section)
        : base(group, name, label, KnobKind.Link, tier, Effect.Live, section, "", layout: false)
        => Url = url;

    public override string Format() => Url;
    public override string FormatDefault() => Url;
    public override string FormatBoot() => Url;
    public override bool Parse(string s) => false;
    public override bool IsDefault => true;
    public override bool BootMoved => false;
    public override void Reset() { }
    public override string RangeText() => "";
    public override int ChoiceIndex => -1;
    internal override bool BootIntact => true;
    public override bool Enforce(string raw) => false;
    public override void Release() { }
    public override string FormatStored() => Format();
    public override bool StoredIsDefault => true;
}

/// <summary>The built-in Format/Parse per <typeparamref name="T"/> (spec §1 cx.cfg): Toggle <c>0/1</c>,
/// invariant float / int, Swatch <c>rrggbbaa</c>, Text raw, List <see cref="TextList.Format"/>. Null pair = the
/// T needs its own.</summary>
internal static class KnobCodec<T>
{
    public static readonly Func<T, string>? Format;
    public static readonly Func<string, (bool, T)>? Parse;

    static KnobCodec()
    {
        if (typeof(T) == typeof(bool))
        {
            Format = v => (bool)(object)v! ? "1" : "0";
            Parse = s => s.ToLowerInvariant() switch
            {
                "1" or "true" or "yes" or "on" => (true, (T)(object)true),
                "0" or "false" or "no" or "off" => (true, (T)(object)false),
                _ => (false, default!),
            };
        }
        else if (typeof(T) == typeof(int))
        {
            Format = v => ((int)(object)v!).ToString(CultureInfo.InvariantCulture);
            Parse = s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)
                ? (true, (T)(object)i) : (false, default!);
        }
        else if (typeof(T) == typeof(float))
        {
            Format = v => ((float)(object)v!).ToString("R", CultureInfo.InvariantCulture);
            Parse = s => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)
                ? (true, (T)(object)f) : (false, default!);
        }
        else if (typeof(T) == typeof(string))
        {
            Format = v => (string)(object)v!;
            Parse = s => (true, (T)(object)s);
        }
        else if (typeof(T) == typeof(uint))
        {
            Format = v => ((uint)(object)v!).ToString("x8", CultureInfo.InvariantCulture);
            Parse = s => s.Length == 8 && uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint u)
                ? (true, (T)(object)u) : (false, default!);
        }
        else if (typeof(T) == typeof(TextList))
        {
            Format = v => ((TextList)(object)v!).Format();
            Parse = s => (true, (T)(object)TextList.Parse(s));
        }
    }
}
