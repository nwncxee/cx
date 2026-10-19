using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Radial;

/// <summary>One persisted layout row: a <b>hoist</b> (<see cref="Fn"/> set: a leaf or submenu deep in the tree spliced
/// into the top list, validated by <c>(Fn, Param)</c> at <see cref="Path"/>), a <b>reorder</b> (<see cref="Fn"/> empty:
/// the slot row at <see cref="Path"/> fixed at <see cref="At"/> within its own list, no validator), a <b>hide</b>
/// (<see cref="Hidden"/>: dropped from its list, claiming no index), or a <b>folder</b> (<see cref="Folder"/> set).</summary>
internal sealed class RadialPin
{
    public RootKind Root;
    public int[] Path = Array.Empty<int>();
    public bool Hidden;
    /// <summary>A folder row: its name. Names are unique per root; a rename onto an existing name merges.</summary>
    public string Folder = "";
    /// <summary>The folder this hoist sits in ("" = the top list).</summary>
    public string In = "";
    /// <summary>The <see cref="PlayerIdentity.Key"/> this row belongs to; "" = a row written before the key
    /// existed, adopted by the first identity that loads.</summary>
    public string Who = "";
    /// <summary>The Itanium name of the leaf's handler, <c>vt:&lt;off&gt;:&lt;_ZTV&gt;+&lt;n&gt;</c> for a virtual one,
    /// <c>rva:&lt;hex&gt;</c> on Windows, <see cref="RadialPins.SubToken"/> for a hoisted submenu; "" = a reorder.</summary>
    public string Fn = "";
    public long Param;
    /// <summary>The row's index among the slot rows of its list, written back after every compose.</summary>
    public int At;
    /// <summary>The breadcrumb as seen at pin time (<c>Cast Spell &gt; Wizard &gt; 3 &gt; Fireball</c>): the greyed row's text.</summary>
    public string Label = "";
    /// <summary>The leaf's icon resref at pin time: a self pin has no live leaf to read one from.</summary>
    public string Icon = "";
    /// <summary>A self pin (<c>fn=self:&lt;key&gt;</c>): fired on a stand-in of the self menu wherever it is shown,
    /// never walked; its path is the source breadcrumb only.</summary>
    public bool IsSelf => Fn.StartsWith(SelfActions.Prefix, StringComparison.Ordinal);
    /// <summary>The leaf label alone.</summary>
    public string LeafLabel { get { int cut = Label.LastIndexOf(RadialPins.Sep, StringComparison.Ordinal); return cut < 0 ? Label : Label.Substring(cut + RadialPins.Sep.Length); } }
    public bool IsHoist => Fn.Length > 0;
    public bool IsFolder => Folder.Length > 0;
    /// <summary>The list this row sits in: the top list for a hoist or folder, the parent list for a reorder.</summary>
    public ReadOnlySpan<int> ListPath => IsHoist || IsFolder ? ReadOnlySpan<int>.Empty : Path.AsSpan(0, Path.Length - 1);
    public bool SameList(RadialPin o) => Root == o.Root && In == o.In && ListPath.SequenceEqual(o.ListPath);
    /// <summary>Shares <paramref name="o"/>'s list and claims an index there (a hide claims none).</summary>
    public bool Claims(RadialPin o) => !Hidden && SameList(o);
}

/// <summary>The tag of a hoisted row: the materialized parent menu at the path and the slot in it, the pin, and the
/// validation verdict <see cref="RadialService"/> refuses on regardless of the row's Disabled flag.</summary>
internal sealed class RadialRowTag
{
    public IntPtr Menu;
    public int Slot;
    public RadialPin Pin = null!;
    public bool Valid;
}

/// <summary>
/// The persisted radial layout: the <c>radial.p&lt;N&gt;.*</c> bag parsed into <see cref="RadialPin"/> rows at first
/// open, replaced whole on every change, one layout per (server, character) by <see cref="PlayerIdentity.Key"/>. A list
/// is its vanilla slot rows in slot order plus this root's hoists; a persisted row claims index
/// <see cref="RadialPin.At"/> (first free at or after it), unclaimed vanilla rows fill the gaps, and leading rows with a null tag (the item root's header) are anchored.
/// </summary>
internal static unsafe partial class RadialPins
{
    public const string SubToken = "sub";
    public const string Sep = " > ";
    private static readonly List<RadialPin> _rows = new();
    /// <summary>Other identities' rows, carried through every save untouched.</summary>
    private static readonly List<RadialPin> _other = new();
    private static string? _who;
    /// <summary>Set once a key with a character has loaded: the old file's key-less rows went to it, or to no one.</summary>
    private static bool _adopted;
    private static bool _nameWarned;
    private static readonly IntPtr Base = NativeLibrary.GetMainProgramHandle();

    /// <summary>The key a new row is stamped with.</summary>
    private static string Who => _who ?? "";

    /// <summary>Parse the bag once, then on every open split the rows by <paramref name="who"/>: the current
    /// identity's rows are the live set, the rest wait in <see cref="_other"/>. Rows with no key are adopted by the
    /// first identity that loads and take its key at the next save.</summary>
    public static void Load(ExtenderLog? log, string who)
    {
        if (_who == who) return;
        if (_who == null)
        {
            var (rows, bad) = Parse(RadialKnobs.Layout.Pairs);
            _other.AddRange(rows);
            log?.Info($"[radial] pins loaded n={rows.Count} bad={bad}");
        }
        if (Split(who)) Save(log, "adopted");   // the first opener owns the old file for good, not just this session
        log?.Info($"[radial] pins for \"{who}\": n={_rows.Count} other={_other.Count}");
    }

    /// <summary>Make <paramref name="who"/>'s rows the live set. True when key-less rows were adopted.</summary>
    private static bool Split(string who)
    {
        _other.AddRange(_rows);
        _rows.Clear();
        bool adopt = !_adopted && PlayerIdentity.HasCharacter(who);   // a key with no character never adopts the old file
        bool adopted = false;
        _adopted |= adopt;
        _who = who;
        for (int i = _other.Count - 1; i >= 0; i--)
        {
            RadialPin p = _other[i];
            if (p.Who != who && !(adopt && p.Who.Length == 0)) continue;
            adopted |= p.Who.Length == 0;
            p.Who = who;
            _rows.Add(p);
            _other.RemoveAt(i);
        }
        _rows.Reverse();
        return adopted;
    }

    /// <summary>Rows of <paramref name="root"/> whose list is <paramref name="listPath"/> (empty = the top list) in
    /// folder <paramref name="in"/> ("" = not in one).</summary>
    public static List<RadialPin> ForList(RootKind root, ReadOnlySpan<int> listPath, string @in = "")
    {
        var r = new List<RadialPin>();
        foreach (RadialPin p in _rows) if (p.Root == root && p.In == @in && p.ListPath.SequenceEqual(listPath)) r.Add(p);
        return r;
    }

    /// <summary>The top-level reorders of <paramref name="root"/> filed in a folder: their slots leave the top list.</summary>
    public static List<RadialPin> Filed(RootKind root)
    {
        var r = new List<RadialPin>();
        foreach (RadialPin p in _rows) if (p.Root == root && p.In.Length > 0 && !p.IsHoist) r.Add(p);
        return r;
    }

    /// <summary>The folder rows of <paramref name="root"/>, in index order.</summary>
    public static List<RadialPin> Folders(RootKind root)
    {
        var r = new List<RadialPin>();
        foreach (RadialPin p in _rows) if (p.Root == root && p.IsFolder) r.Add(p);
        r.Sort((a, b) => a.At.CompareTo(b.At));
        return r;
    }

    public static RadialPin? FindFolder(RootKind root, string name)
    {
        foreach (RadialPin p in _rows) if (p.Root == root && p.IsFolder && p.Folder == name) return p;
        return null;
    }

    /// <summary>A folder row at index <paramref name="at"/> of the top list (the rows from there shift down one).</summary>
    public static RadialPin AddFolder(RootKind root, string name, int at)
    {
        var pin = new RadialPin { Root = root, Folder = name, At = at, Who = Who };
        foreach (RadialPin p in _rows) if (p.Claims(pin) && p.At >= at) p.At++;
        _rows.Add(pin);
        return pin;
    }

    /// <summary>Move a hoist or a top-level reorder to folder <paramref name="in"/> ("" = the top list) at index
    /// <paramref name="at"/>: it leaves its old list (the rows below shift up) and claims the index in the new one (the
    /// rows from there shift down). A folder row is never refiled (one level); a hide has no list to file in.</summary>
    public static void Refile(RadialPin pin, string @in, int at)
    {
        if (pin.IsFolder || pin.Hidden || pin.Path.Length > 1 && !pin.IsHoist) return;
        Remove(pin);
        pin.In = @in; pin.At = at;
        foreach (RadialPin p in _rows) if (p.Claims(pin) && p.At >= at) p.At++;
        _rows.Add(pin);
    }

    /// <summary>Drop a folder row; its members return to the top list at the folder's index, in their order.</summary>
    public static int RemoveFolder(RadialPin folder)
    {
        if (!folder.IsFolder) return 0;
        var members = ForList(folder.Root, ReadOnlySpan<int>.Empty, folder.Folder);
        members.Sort((a, b) => a.At.CompareTo(b.At));
        int at = folder.At;
        Remove(folder);
        foreach (RadialPin m in members) Refile(m, "", at++);
        return members.Count;
    }

    /// <summary>Rename a folder; onto an existing name of the same root the two merge (this row goes, its members
    /// follow the name).</summary>
    public static void RenameFolder(RadialPin folder, string name)
    {
        if (!folder.IsFolder || name.Length == 0 || name == folder.Folder) return;
        foreach (RadialPin p in _rows) if (p.Root == folder.Root && p.In == folder.Folder) p.In = name;
        if (FindFolder(folder.Root, name) != null) Remove(folder); else folder.Folder = name;
    }

    /// <summary>The hoist (or the reorder / hide) at <paramref name="path"/>.</summary>
    public static RadialPin? Find(RootKind root, ReadOnlySpan<int> path, bool hoist)
    {
        foreach (RadialPin p in _rows) if (p.Root == root && p.IsHoist == hoist && p.Path.AsSpan().SequenceEqual(path)) return p;
        return null;
    }

    /// <summary>Add a row at index <paramref name="at"/> of its list (the list's rows from there shift down one);
    /// <paramref name="in"/> files it in a folder (a hoist, or a top-level reorder: a filed one leaves the top list).</summary>
    public static RadialPin AddAt(RootKind root, int[] path, string fn, long param, string label, int at, string @in = "", string icon = "")
    {
        var pin = new RadialPin { Root = root, Path = path, Fn = fn, Param = param, At = at, Label = label, Who = Who, In = @in, Icon = icon };
        foreach (RadialPin p in _rows) if (p.Claims(pin) && p.At >= at) p.At++;
        _rows.Add(pin);
        return pin;
    }

    /// <summary><see cref="AddAt"/> from a template: the same path, handler, param and label as a row of
    /// <paramref name="root"/>.</summary>
    public static RadialPin AddLike(RadialPin like, RootKind root, int at, string @in = "")
        => AddAt(root, like.Path, like.Fn, like.Param, like.Label, at, @in, like.Icon);

    /// <summary>Drop a row; the rows below it in its list shift up one (a hide claimed no index: nothing shifts).</summary>
    public static void Remove(RadialPin pin)
    {
        if (!_rows.Remove(pin) || pin.Hidden) return;
        foreach (RadialPin p in _rows) if (p.Claims(pin) && p.At > pin.At) p.At--;
    }

    /// <summary>Move the row at <paramref name="from"/> (a slot-row index in its list) to <paramref name="to"/>: the
    /// moved row is fixed there (found or, for a vanilla row, added as a reorder), the persisted rows between the
    /// two indices shift one toward <paramref name="from"/>, flowing rows just flow around them.</summary>
    public static void Move(RootKind root, int[] path, RadialPin? moved, int from, int to)
    {
        moved ??= Find(root, path, hoist: false) ?? new RadialPin { Root = root, Path = path, Who = Who };
        if (!_rows.Contains(moved)) _rows.Add(moved);
        foreach (RadialPin p in _rows)
        {
            if (p == moved || !p.Claims(moved)) continue;
            if (from < to && p.At > from && p.At <= to) p.At--;
            else if (to < from && p.At >= to && p.At < from) p.At++;
        }
        moved.At = to;
    }

    /// <summary>Hide the slot row at <paramref name="path"/>: its reorder, if any, goes with it.</summary>
    public static RadialPin Hide(RootKind root, int[] path)
    {
        RadialPin? old = Find(root, path, hoist: false);
        if (old != null) Remove(old);
        var pin = new RadialPin { Root = root, Path = path, Hidden = true, Who = Who };
        _rows.Add(pin);
        return pin;
    }

    /// <summary>Hides of <paramref name="root"/>'s list at <paramref name="listPath"/> (the popup's Show hidden count).</summary>
    public static int HiddenCount(RootKind root, int[] listPath)
    {
        int n = 0;
        foreach (RadialPin p in _rows) if (p.Hidden && p.Root == root && p.ListPath.SequenceEqual(listPath)) n++;
        return n;
    }

    /// <summary>Drop every hide of <paramref name="root"/>'s list at <paramref name="listPath"/>; the rows flow back.</summary>
    public static int Unhide(RootKind root, int[] listPath)
        => _rows.RemoveAll(p => p.Hidden && p.Root == root && p.ListPath.SequenceEqual(listPath));

    public static int Reset(RootKind root) => _rows.RemoveAll(p => p.Root == root);

    public static void Save(ExtenderLog? log, string what)
    {
        var all = new List<RadialPin>(_rows.Count + _other.Count);
        all.AddRange(_rows); all.AddRange(_other);
        RadialKnobs.Layout.Replace(Format(all));
        log?.Info($"[radial] pin {what} saved n={_rows.Count} other={_other.Count}");
    }

    /// <summary>Lay a list out: <paramref name="fixedRows"/> (this list's rows with their items -- a hoist's spliced
    /// item, a reorder's vanilla item) claim their indices, the rest of <paramref name="vanilla"/> flows in slot
    /// order; each row's <see cref="RadialPin.At"/> becomes the index it got. Leading null-tag rows are kept in place.</summary>
    public static CascadeItem[] Compose(CascadeItem[] vanilla, List<(RadialPin Pin, CascadeItem Item)> fixedRows)
    {
        int lead = 0;
        while (lead < vanilla.Length && vanilla[lead].Tag == null) lead++;
        var flow = new List<CascadeItem>();
        for (int i = lead; i < vanilla.Length; i++)
        {
            bool claimed = false;
            foreach (var f in fixedRows)
                if (!f.Pin.IsHoist && vanilla[i].Tag is int s && f.Pin.Path.Length > 0 && s == f.Pin.Path[^1]) { claimed = true; break; }
            if (!claimed) flow.Add(vanilla[i]);
        }
        int n = flow.Count + fixedRows.Count;
        var result = new CascadeItem[lead + n];
        Array.Copy(vanilla, result, lead);
        var taken = new bool[n];
        var order = new int[fixedRows.Count];
        for (int i = 0; i < order.Length; i++) order[i] = i;
        Array.Sort(order, (a, b) => fixedRows[a].Pin.At != fixedRows[b].Pin.At ? fixedRows[a].Pin.At.CompareTo(fixedRows[b].Pin.At) : a.CompareTo(b));
        foreach (int i in order)
        {
            var (pin, item) = fixedRows[i];
            int t = Math.Clamp(pin.At, 0, n - 1), k = t;
            while (k < n && taken[k]) k++;
            if (k >= n) { k = t; while (taken[k]) k--; }
            taken[k] = true;
            result[lead + k] = item;
            pin.At = k;
        }
        for (int k = 0, f = 0; k < n; k++) if (!taken[k]) result[lead + k] = flow[f++];
        return result;
    }

    /// <summary>Bit 37 of a cast-leaf param: set by <c>CreateSpellSubRadial</c> @0x6f0590 only while a spontaneous
    /// caster has no uses of that spell left, cleared by <c>SetButtonSpellMenuID</c> @0x6aac50 as transient -- so it
    /// is no part of a leaf's identity and is masked on both sides of a compare.</summary>
    public const long TransientBit = 1L << 37;

    /// <summary>A hoist matches the leaf at its path when the packed param is equal (less <see cref="TransientBit"/>)
    /// and the handler is the same function: both params are read raw from a slot, so equality is exact.</summary>
    public static bool Validate(RadialPin pin, in RadialSlot s)
    {
        if (!pin.IsHoist || (s.Param & ~TransientBit) != (pin.Param & ~TransientBit)) return false;
        string fn = pin.Fn;
        if (fn.StartsWith(SubToken, StringComparison.Ordinal))
        {
            if (!s.IsSubMenu) return false;
            if (fn.Length == SubToken.Length) return s.Fn == IntPtr.Zero;   // a submenu with no entry: nothing else to fire
            fn = fn.Substring(SubToken.Length + 1);
        }
        else if (s.IsSubMenu || s.Kind != SlotKind.Entry) return false;
        if (s.Fn == IntPtr.Zero) return false;
        if (OperatingSystem.IsWindows() || fn.StartsWith("vt:", StringComparison.Ordinal) || fn.StartsWith("rva:", StringComparison.Ordinal))
            return fn == EntryToken(s);
        return SymbolMap.Resolve(fn, null) == s.Fn;
    }

    /// <summary>The persistable identity of a slot's handler: <see cref="SubToken"/> for a submenu, suffixed
    /// <c>:&lt;entry&gt;</c> when the slot also carries an entry (vanilla fires it on the descent, so it is part of what
    /// the pin may invoke); else the entry's <see cref="EntryToken"/>. "" = not persistable.</summary>
    public static string FnToken(in RadialSlot s)
    {
        if (s.IsSubMenu)
        {
            if (s.Fn == IntPtr.Zero) return SubToken;
            string e = EntryToken(s);
            return e.Length == 0 ? "" : SubToken + ":" + e;
        }
        return s.Kind == SlotKind.Entry ? EntryToken(s) : "";
    }

    /// <summary>The fn's exported name (Linux, exact <c>dladdr</c> hit only; a virtual thunk names the handler's
    /// <c>_ZTV</c> and the vptr's offset into it), or its RVA on Windows, where the handlers are not exported.
    /// "" = an entry-less slot or an unnamed fn.</summary>
    private static string EntryToken(in RadialSlot s)
    {
        if (s.Fn == IntPtr.Zero) return "";
        if (OperatingSystem.IsWindows()) return $"rva:{(long)s.Fn - (long)Base:x}";
        if (((long)s.Fn & 1) != 0)
        {
            if (s.Obj == IntPtr.Zero) return "";
            IntPtr vptr = *(IntPtr*)(s.Obj + s.Adj);
            if (Dladdr(vptr, out DlInfo vi) == 0 || vi.SName == IntPtr.Zero) return "";
            return $"vt:{(long)s.Fn - 1:x}:{Marshal.PtrToStringAnsi(vi.SName)}+{(long)vptr - (long)vi.SAddr:x}";
        }
        if (Dladdr(s.Fn, out DlInfo info) == 0 || info.SName == IntPtr.Zero || info.SAddr != s.Fn) return "";
        return Marshal.PtrToStringAnsi(info.SName) ?? "";
    }

    /// <summary>Log once that a handler could not be named (the pin is refused, never stored by address).</summary>
    public static void WarnUnnamed(ExtenderLog? log, in RadialSlot s)
    {
        if (_nameWarned) return;
        _nameWarned = true;
        log?.Info($"[radial] pin refused: handler fn=0x{(long)s.Fn:x} has no exported name (logged once)");
    }

    public static string PathText(ReadOnlySpan<int> path)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < path.Length; i++) { if (i > 0) sb.Append('/'); sb.Append(path[i]); }
        return sb.ToString();
    }

    public static string Breadcrumb(IReadOnlyList<string> labels) => string.Join(Sep, labels).Replace("\n", "").Replace("\r", "");

    /// <summary>The greyed row for an unresolved hoist: the leaf label, the parents' breadcrumb in the count column
    /// (the disabled alpha mutes both), disabled, tag invalid.</summary>
    public static CascadeItem Greyed(RadialPin pin)
    {
        int cut = pin.Label.LastIndexOf(Sep, StringComparison.Ordinal);
        string leaf = pin.LeafLabel;
        string parents = cut < 0 ? "" : "(" + pin.Label.Substring(0, cut) + ")";
        return new CascadeItem(leaf.Length > 0 ? leaf : "?", disabled: true, hasChildren: pin.Fn.StartsWith(SubToken, StringComparison.Ordinal), count: parents,
            tag: new RadialRowTag { Pin = pin, Slot = -1, Valid = false });
    }

    // ---- rows <-> pairs -------------------------------------------------------------------------

    /// <summary>Parse the bag: rows grouped by <c>p&lt;N&gt;</c>; a row missing root/at, with an unparsable field, a
    /// slot outside 0..7, a reorder or hide with an empty path, a hoist with a one-step path, a hidden hoist, a folder
    /// row with a path, fn or hide, or an <c>in</c> on a hide, a folder or a deeper reorder is skipped and counted; a
    /// member of a folder row that is not in the file is un-filed.</summary>
    public static (List<RadialPin> Rows, int Bad) Parse(KeyValuePair<string, string>[] pairs)
    {
        var byN = new SortedDictionary<int, Dictionary<string, string>>();
        int bad = 0;
        foreach (var kv in pairs)
        {
            string k = kv.Key;
            int dot = k.IndexOf('.');
            if (k.Length < 3 || k[0] != 'p' || dot < 2 || !int.TryParse(k.AsSpan(1, dot - 1), out int n)) { bad++; continue; }
            if (!byN.TryGetValue(n, out var f)) byN[n] = f = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            f[k.Substring(dot + 1)] = kv.Value;
        }
        var rows = new List<RadialPin>();
        foreach (var f in byN.Values)
        {
            if (!f.TryGetValue("root", out string? root) || !Enum.TryParse(root, true, out RootKind kind) || kind == RootKind.Unknown
                || !f.TryGetValue("at", out string? ats) || !int.TryParse(ats, out int at) || at < 0)
            { bad++; continue; }
            string folder = f.TryGetValue("folder", out string? fo) ? fo.Trim() : "";
            string @in = f.TryGetValue("in", out string? ins) ? ins.Trim() : "";
            string ps = f.TryGetValue("path", out string? pv) ? pv : "";
            string[] parts = ps.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var path = new int[parts.Length];
            bool okPath = parts.Length > 0 || folder.Length > 0;
            for (int i = 0; i < parts.Length && okPath; i++) okPath = int.TryParse(parts[i], out path[i]) && (uint)path[i] < RadialTree.SlotCount;
            string fn = f.TryGetValue("fn", out string? fs) ? fs.Trim() : "";
            long param = 0;
            bool okParam = !f.TryGetValue("param", out string? prm) || TryHex(prm, out param);
            bool hidden = f.TryGetValue("hide", out string? hs) && hs.Trim() == "1";
            bool self = fn.StartsWith(SelfActions.Prefix, StringComparison.Ordinal);   // a self pin is never walked, so a one-step source path is fine
            if (!okPath || !okParam || (fn.Length > 0 && !self && path.Length < 2) || (hidden && fn.Length > 0)
                || (folder.Length > 0 && (path.Length > 0 || fn.Length > 0 || hidden)) || (@in.Length > 0 && (hidden || folder.Length > 0 || fn.Length == 0 && path.Length > 1))) { bad++; continue; }
            rows.Add(new RadialPin { Root = kind, Path = path, Fn = fn, Param = param, At = at, Hidden = hidden, Label = f.TryGetValue("label", out string? l) ? l : "",
                                     Who = f.TryGetValue("who", out string? w) ? w : "", Folder = folder, In = @in, Icon = f.TryGetValue("icon", out string? ic) ? ic.Trim() : "" });
        }
        foreach (RadialPin p in rows)   // a member whose folder row is gone (a hand edit) returns to the top list rather than vanish
            if (p.In.Length > 0 && !rows.Exists(f => f.IsFolder && f.Root == p.Root && f.Who == p.Who && f.Folder == p.In)) p.In = "";
        return (rows, bad);
    }

    public static List<KeyValuePair<string, string>> Format(List<RadialPin> rows)
    {
        var o = new List<KeyValuePair<string, string>>(rows.Count * 7);
        for (int i = 0; i < rows.Count; i++)
        {
            RadialPin p = rows[i];
            string k = "p" + i + ".";
            o.Add(new(k + "root", p.Root.ToString()));
            if (p.Path.Length > 0) o.Add(new(k + "path", PathText(p.Path)));
            o.Add(new(k + "at", p.At.ToString()));
            if (p.IsFolder) o.Add(new(k + "folder", p.Folder));
            if (p.In.Length > 0) o.Add(new(k + "in", p.In));
            if (p.IsHoist)
            {
                o.Add(new(k + "fn", p.Fn));
                o.Add(new(k + "param", "0x" + ((ulong)p.Param).ToString("x")));
            }
            if (p.Hidden) o.Add(new(k + "hide", "1"));
            if (p.Label.Length > 0) o.Add(new(k + "label", p.Label));
            if (p.Icon.Length > 0) o.Add(new(k + "icon", p.Icon));
            if (p.Who.Length > 0) o.Add(new(k + "who", p.Who));
        }
        return o;
    }

    private static bool TryHex(string s, out long v)
    {
        v = 0;
        ReadOnlySpan<char> t = s.AsSpan().Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Slice(2);
        bool ok = ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong u);
        v = (long)u;
        return ok;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo { public IntPtr FName, FBase, SName, SAddr; }

    /// <summary>Linux only (never bound on Windows, where <see cref="FnToken"/> takes the RVA branch first).</summary>
    [DllImport("libdl.so.2", EntryPoint = "dladdr")]
    private static extern int Dladdr(IntPtr addr, out DlInfo info);

    // ---- self-check ----------------------------------------------------------------------------
}
