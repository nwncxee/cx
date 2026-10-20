using System;
using System.Collections.Generic;
using System.Text;

namespace CX.Framework;

/// <summary>
/// One feature's knobs (ADR 0025): feature = group = submenu = chat command = reset unit = key prefix.
/// <see cref="Declare"/> registers the group for enumeration and its generated <c>/&lt;group&gt;</c> chat handler;
/// the factories declare knobs in row order, binding each from the pre-main <see cref="Settings"/> dictionary as
/// it is constructed, so a static field is right on first touch. A group that stands down still declares.
/// </summary>
public sealed partial class KnobGroup
{
    /// <summary>The key prefix, submenu id and chat command: <c>grass</c>.</summary>
    public string Name { get; }
    public string Category { get; }
    public string Submenu { get; }
    /// <summary>Submenu header text; a group with no master says why here.</summary>
    public string Header { get; }
    /// <summary><c>&lt;group&gt;.enabled</c>, the first row, when the group has a master.</summary>
    public Knob<bool>? Master { get; }
    /// <summary>Another group's master key (<c>alloy.enabled</c>), resolved lazily by <see cref="Parent"/>:
    /// the parent group may declare later than this one.</summary>
    public string? ParentKey { get; }
    /// <summary>The parent's master knob, or null while its group has not declared.</summary>
    public Knob<bool>? Parent => ParentKey == null ? null : Find(ParentKey) as Knob<bool>;
    /// <summary>Every knob in declaration order, master first.</summary>
    public IReadOnlyList<Knob> Knobs => _knobs;
    /// <summary>The group's Hidden dynamic-key bags (<see cref="Bag"/>), persisted beside its knobs.</summary>
    public IReadOnlyList<KnobBag> Bags => _bags;
    /// <summary>The group's diagnostic line: bare <c>/&lt;group&gt;</c> and <c>/knobs</c> print it instead of every
    /// row's value when set. Never the panel's -- counters and engine state are developer text.</summary>
    public Func<string>? Status;
    /// <summary>The line the settings panel shows atop the group's rows, written for a player ("Approved on this
    /// server: …"). Null for almost every group; a group with no rows and no panel status has no submenu.</summary>
    public Func<string>? PanelStatus;
    /// <summary>Panel sort key across every group: submenus appear in the order of their lowest-ordered group,
    /// and a shared submenu's groups in this order (ties: declaration order).</summary>
    public int Order { get; }
    /// <summary>The heading over this group's rows when it shares its submenu with other groups.</summary>
    public string Title { get; }
    /// <summary>False for the SelfCheck's throwaway group: not enumerated, no command, sets arm no write.</summary>
    internal bool Registered { get; }

    private readonly List<Knob> _knobs = new();
    private readonly List<KnobBag> _bags = new();
    private string _section = "";

    private static readonly List<KnobGroup> _all = new();
    private static readonly Dictionary<string, Knob> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> _violations = new();
    /// <summary>Refusal log. Set by the host right after <see cref="Settings.Load"/>.</summary>
    public static ExtenderLog? Log;

    /// <summary>Every declared group in declaration order.</summary>
    public static IReadOnlyList<KnobGroup> All { get { lock (_all) return _all.ToArray(); } }

    /// <summary>The knob declared under <paramref name="key"/>, or null.</summary>
    public static Knob? Find(string key) { lock (_all) return _byKey.TryGetValue(key, out Knob? k) ? k : null; }

    /// <summary>Declare a group. <paramref name="master"/> labels <c>&lt;group&gt;.enabled</c>, the first row, User
    /// tier (so <paramref name="masterHelp"/> is required); a <paramref name="masterTier"/> below User keeps that
    /// switch in chat and <c>cx.cfg</c> only. <paramref name="parent"/> names another group's master by KEY, later
    /// if need be; groups sharing a <paramref name="submenu"/> sit under their <paramref name="title"/> in <paramref name="order"/>.</summary>
    public static KnobGroup Declare(string name, string category, string submenu, string? master = null,
                                    string? masterHelp = null, bool masterDefault = true,
                                    Effect masterEffect = Effect.Live, Action? masterChanged = null,
                                    string? parent = null, string header = "", Tier masterTier = Tier.User,
                                    int order = 0, string? title = null)
        => new(name, category, submenu, master, masterHelp, masterDefault, masterEffect, masterChanged, parent, header,
               register: true, masterTier, order, title);

    internal KnobGroup(string name, string category, string submenu, string? master, string? masterHelp,
                       bool masterDefault, Effect masterEffect, Action? masterChanged, string? parent,
                       string header, bool register, Tier masterTier = Tier.User, int order = 0, string? title = null)
    {
        Name = name.ToLowerInvariant(); Category = category; Submenu = submenu; ParentKey = parent; Header = header;
        Registered = register; Order = order; Title = title ?? submenu;
        if (register)
            lock (_all) _all.Add(this);
        if (master != null)
            Master = Toggle("enabled", master, masterDefault, masterTier, masterEffect, masterHelp, masterChanged);
        if (register)
            CommandRegistry.Global.Register("/" + Name,
                $"report or set the {Submenu} knobs; /{Name} <knob> <value>, /{Name} reset", Command);
    }

    /// <summary>Knobs declared after this carry <paramref name="name"/> as their section header.</summary>
    public void Section(string name) => _section = name;

    public Knob<bool> Toggle(string key, string label, bool def, Tier tier = Tier.User, Effect effect = Effect.Live,
                             string? help = null, Action? changed = null)
        => Add(new Knob<bool>(this, key, label, KnobKind.Toggle, tier, effect, _section, help ?? "", false,
                              def, false, true, true, "", null, null, null, null), changed);

    public Knob<float> Slider(string key, string label, float min, float max, float step, float def, string unit = "",
                              Tier tier = Tier.User, Effect effect = Effect.Live, string? help = null, Action? changed = null)
        => Add(new Knob<float>(this, key, label, KnobKind.Slider, tier, effect, _section, help ?? "", false,
                               def, min, max, step, unit, null, null, null, null), changed);

    public Knob<int> Slider(string key, string label, int min, int max, int step, int def, string unit = "",
                            Tier tier = Tier.User, Effect effect = Effect.Live, string? help = null, Action? changed = null)
        => Add(new Knob<int>(this, key, label, KnobKind.Slider, tier, effect, _section, help ?? "", false,
                             def, min, max, step, unit, null, null, null, null), changed);

    /// <summary>The one factory with <paramref name="layout"/>: a layout-affecting knob is a dropdown by
    /// construction. <paramref name="choices"/> are the labels shown, index-aligned with
    /// <paramref name="values"/>; null shows each value's own text.</summary>
    public Knob<T> Dropdown<T>(string key, string label, T[] values, T def, string[]? choices = null, bool layout = false,
                               Tier tier = Tier.User, Effect effect = Effect.Live, string? help = null, Action? changed = null,
                               Func<T, string>? format = null, Func<string, (bool, T)>? parse = null, bool open = false)
        => Add(new Knob<T>(this, key, label, KnobKind.Dropdown, tier, effect, _section, help ?? "", layout,
                           def, def, def, def, "", choices, values, format, parse, open), changed);

    public Knob<uint> Swatch(string key, string label, uint def, Tier tier = Tier.User, Effect effect = Effect.Live,
                             string? help = null, Action? changed = null)
        => Add(new Knob<uint>(this, key, label, KnobKind.Swatch, tier, effect, _section, help ?? "", false,
                              def, 0, uint.MaxValue, 1, "", null, null, null, null), changed);

    public Knob<string> Text(string key, string label, string def, Tier tier = Tier.User, Effect effect = Effect.Live,
                             string? help = null, Action? changed = null)
        => Add(new Knob<string>(this, key, label, KnobKind.Text, tier, effect, _section, help ?? "", false,
                                def, "", "", "", "", null, null, null, null), changed);

    /// <summary>An editable list of text items (<see cref="TextList"/>): the panel shows one text box per item with a
    /// remove button and a trailing empty box, <paramref name="hint"/> its placeholder, that appends one. The cfg key
    /// and the chat form hold the items joined with <c>|</c>.</summary>
    public Knob<TextList> List(string key, string label, string[]? def = null, string hint = "", Tier tier = Tier.User,
                               Effect effect = Effect.Live, string? help = null, Action? changed = null)
    {
        TextList d = def == null ? TextList.Empty : new TextList(def);
        return Add(new Knob<TextList>(this, key, label, KnobKind.List, tier, effect, _section, help ?? "", false,
                                      d, d, d, d, "", null, null, null, null) { Hint = hint }, changed);
    }

    /// <summary><paramref name="confirm"/>: the panel fires it on a second press within
    /// <see cref="KnobButton.ConfirmSec"/> only (spec §4.5's revoke rows).</summary>
    public KnobButton Button(string key, string label, Action press, Tier tier = Tier.User, string? help = null, bool confirm = false)
        => Add(new KnobButton(this, key, label, tier, _section, help ?? "", press, confirm), null);

    /// <summary>A link row (spec §3): <paramref name="url"/> under <paramref name="label"/>, copied to the clipboard
    /// on press. Knob-less by nature -- no value, no key in the file -- but a row in declaration order.</summary>
    public KnobLink Link(string key, string label, string url, Tier tier = Tier.User)
        => Add(new KnobLink(this, key, label, url, tier, _section), null);

    /// <summary>Hidden dynamic state under <paramref name="prefix"/> (spec §4.2 <c>w&lt;N&gt;.*</c>): one
    /// <see cref="KnobBag"/> owning every <c>&lt;group&gt;.&lt;prefix&gt;*</c> key. Outside the rows, the
    /// command, <c>/knobs</c> and <see cref="Reset"/>; the prefix must not head any knob name in the group.</summary>
    public KnobBag Bag(string prefix)
    {
        var b = new KnobBag(this, prefix);
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i].Name.StartsWith(b.Prefix, StringComparison.Ordinal)) Violation($"{_knobs[i].Key} collides with bag prefix {b.KeyPrefix}");
        _bags.Add(b);
        return b;
    }

    private K Add<K>(K k, Action? changed) where K : Knob
    {
        k.Changed = changed ?? k.Changed;
        if (k.Tier == Tier.User && k.Help.Length == 0 && k.Kind != KnobKind.Link)
        {
            Violation($"{k.Key} is User tier without help -- demoted to Advanced");
            k.Tier = Tier.Advanced;
        }
        if (k.Layout && k.Kind != KnobKind.Dropdown)
            Violation($"{k.Key} is layout-affecting but not a Dropdown");
        for (int i = 0; i < _bags.Count; i++)
            if (k.Name.StartsWith(_bags[i].Prefix, StringComparison.Ordinal)) Violation($"{k.Key} collides with bag prefix {_bags[i].KeyPrefix}");
        _knobs.Add(k);
        if (Registered)
            lock (_all)
            {
                if (_byKey.ContainsKey(k.Key)) Violation($"{k.Key} declared twice");
                _byKey[k.Key] = k;
            }
        return k;
    }

    /// <summary>A declaration this registry refuses (a ladder that does not fit): logged, and the SelfCheck fails.</summary>
    internal void Refuse(string what) => Violation(what);

    private void Violation(string what)
    {
        if (!Registered) return;
        Log?.Info("KnobGroup: " + what);
        lock (_all) _violations.Add(what);
    }

    /// <summary>The knob named <paramref name="name"/> in this group, or null.</summary>
    public Knob? Get(string name)
    {
        for (int i = 0; i < _knobs.Count; i++)
            if (string.Equals(_knobs[i].Name, name, StringComparison.OrdinalIgnoreCase)) return _knobs[i];
        return null;
    }

    /// <summary>Every knob back to its default (a set each): the group's keys leave the file. Hidden state
    /// (a Hidden knob, a <see cref="KnobBag"/>) is not a row and stays as it is.</summary>
    public void Reset()
    {
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i].Tier != Tier.Hidden) _knobs[i].Reset();
    }

    /// <summary>The panel's Reset to defaults (spec §3): the User and Advanced rows only; a ChatOnly key keeps
    /// its value as Hidden state does.</summary>
    public void ResetRows()
    {
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i].Tier == Tier.User || _knobs[i].Tier == Tier.Advanced) _knobs[i].Reset();
    }

    /// <summary>Every row's value on one line, for the bare command when there is no status line.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _knobs.Count; i++)
        {
            if (_knobs[i].Kind == KnobKind.Button || _knobs[i].Kind == KnobKind.Link || _knobs[i].Tier == Tier.Hidden) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(_knobs[i].Name).Append(' ').Append(_knobs[i].Format());
        }
        return sb.ToString();
    }

    /// <summary>The generated handler: <c>/g</c> reports, <c>/g &lt;knob&gt; &lt;value&gt;</c> sets,
    /// <c>/g &lt;button&gt;</c> presses, <c>/g reset</c> restores every default. Arguments arrive lowercased
    /// from <see cref="CommandRegistry"/>, so a Text knob's case is lost on the chat path (the panel keeps it).
    /// A zero-knob group (<c>about</c>, <c>knobs</c>) has nothing to set or reset: every form is the bare one.</summary>
    private string? Command(string args)
    {
        string[] a = args.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (a.Length == 0 || Settable() == 0) return $"[{Name}] {Bare()}";
        if (a[0] == "reset" && a.Length == 1)
        {
            Reset();
            return $"[{Name}] reset -> {Bare()}";
        }
        Knob? k = Get(a[0]);
        if (k == null || k.Tier == Tier.Hidden) return $"[{Name}] unknown knob \"{a[0]}\" -- try {Names()}";
        if (k is KnobButton b)
        {
            b.Press();
            return $"[{Name}] {k.Name}";
        }
        if (k is KnobLink l) return $"[{Name}] {l.Label}: {l.Url}";
        if (a.Length < 2) return $"[{Name}] {k.Name} {k.Format()} (default {k.FormatDefault()}) -- /{Name} {k.Name} <{k.RangeText()}>";
        string was = k.Format();
        if (!k.Parse(a[1])) return $"[{Name}] {k.Name}: \"{a[1]}\" is not {k.RangeText()}";
        return $"[{Name}] {k.Name} {k.Format()} (was {was})" + (k.Effect == Effect.Restart ? " -- takes effect at the next launch" : "");
    }

    /// <summary>The bare report: the status line (else every row's value), then each link row's address on its
    /// own line, so <c>/about</c> prints the same four lines the About submenu shows.</summary>
    private string Bare()
    {
        var sb = new StringBuilder(Status?.Invoke() ?? Report());
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i] is KnobLink l && l.Tier != Tier.Hidden) sb.Append('\n').Append(l.Label).Append(": ").Append(l.Url);
        return sb.ToString();
    }

    /// <summary>Knobs the command can set or press: a group of links only answers every form with the bare one.</summary>
    private int Settable()
    {
        int n = 0;
        for (int i = 0; i < _knobs.Count; i++) if (_knobs[i].Kind != KnobKind.Link) n++;
        return n;
    }

    private string Names()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < _knobs.Count; i++)
            if (_knobs[i].Tier != Tier.Hidden) sb.Append(sb.Length == 0 ? "" : ", ").Append(_knobs[i].Name);
        return sb.ToString();
    }

    /// <summary>The <c>/knobs</c> walk: groups in declaration order, one <see cref="Knob.Describe"/> line each.
    /// Hidden state has no surface, so it is not here either.</summary>
    public static void Walk(StringBuilder sb)
    {
        var all = All;
        int n = 0;
        foreach (KnobGroup g in all)
            for (int i = 0; i < g._knobs.Count; i++) if (g._knobs[i].Tier != Tier.Hidden) n++;
        sb.Append($"[knobs] {n} knob(s) in {all.Count} group(s) (/<group> <knob> <value>, /<group> reset):");
        foreach (KnobGroup g in all)
            for (int i = 0; i < g._knobs.Count; i++)
                if (g._knobs[i].Tier != Tier.Hidden) sb.Append("\n  ").Append(g._knobs[i].Describe());
    }

    /// <summary>The three constraints the registry enforces, as one predicate over a knob.</summary>
    private static bool Constraints(Knob k)
    {
        if (k.Tier == Tier.User && k.Help.Length == 0 && k.Kind != KnobKind.Link) return false;
        if (k.Layout && k.Kind != KnobKind.Dropdown) return false;
        if (k.Effect == Effect.Restart && !k.BootIntact) return false;
        return true;
    }
}
