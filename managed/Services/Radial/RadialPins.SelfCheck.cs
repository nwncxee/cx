using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Radial;

internal static unsafe partial class RadialPins
{
    /// <summary>Off-device: the row parse/format round-trip with garbage rows counted, the position model
    /// (pin / unpin / move incl. a far move / hide / reset and the compose invariant <c>compose(rows)[row.At] == row</c>), the splice over
    /// fake rows with an anchored header, the greyed row's text, and validation on fake <c>(fn, param)</c> pairs
    /// (a real exported address on Linux, RVA arithmetic on Windows). Nothing engine-side.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var pairs = new[]
        {
            new KeyValuePair<string, string>("p0.root", "Creature"), new("p0.path", "1/4/3/0"), new("p0.fn", "_ZN16CClientSpellMenu14RadialMenuCastEl"),
            new("p0.param", "0x1300007a"), new("p0.at", "0"), new("p0.label", "Cast Spell > Wizard > 3 > Fireball"),
            new("p1.root", "Creature"), new("p1.path", "7"), new("p1.at", "1"),
            new("p2.root", "Bogus"), new("p2.path", "1"), new("p2.at", "0"),
            new("p3.root", "Door"), new("p3.path", "9"), new("p3.at", "0"),
            new("p4.root", "Door"), new("p4.path", "2"), new("p4.at", "x"),
            new("p5.root", "Door"), new("p5.path", "2"), new("p5.fn", "x"), new("p5.at", "0"),
            new("junk", "1"),
            new("p6.root", "Placeable"), new("p6.path", "3/1"), new("p6.fn", SubToken), new("p6.param", "0xffffffffffffffff"), new("p6.at", "2"),
        };
        var (rows, bad) = Parse(pairs);
        check.Expect(rows.Count == 3 && bad == 5, $"parse keeps 3 rows, counts 5 bad ({rows.Count}/{bad})");
        check.Expect(rows.Count == 3 && rows[0].IsHoist && rows[0].Path.Length == 4 && rows[0].Param == 0x1300007a && rows[0].Label.EndsWith("Fireball")
          && !rows[1].IsHoist && rows[1].Path[0] == 7 && rows[1].ListPath.Length == 0 && rows[2].Param == -1 && rows[2].Fn == SubToken,
          "parsed fields: hoist/reorder, path, hex param incl. negative, label");
        var again = Parse(Format(rows).ToArray());
        check.Expect(again.Bad == 0 && again.Rows.Count == 3 && Format(again.Rows).Count == Format(rows).Count
          && again.Rows[0].Fn == rows[0].Fn && again.Rows[2].Param == -1 && again.Rows[1].At == 1, "format/parse round-trip is lossless");
        check.Expect(rows[0].SameList(rows[1]) && !rows[0].SameList(rows[2]), "a hoist and a top-level reorder share the top list");

        CascadeItem[] V(params int[] slots) { var a = new CascadeItem[slots.Length]; for (int i = 0; i < slots.Length; i++) a[i] = new CascadeItem("s" + slots[i], tag: slots[i]); return a; }
        string Order(CascadeItem[] a) { var sb = new StringBuilder(); foreach (var it in a) sb.Append(it.Label).Append(' '); return sb.ToString().TrimEnd(); }
        var top = V(0, 1, 2, 3);
        check.Expect(Order(Compose(top, new())) == "s0 s1 s2 s3", "no rows = vanilla order");

        // Position model on a private row set.
        _rows.Clear();
        var fire = new CascadeItem("Fireball", tag: new RadialRowTag { Valid = true });
        var meta = new CascadeItem("Meta", hasChildren: true, tag: new RadialRowTag { Valid = true });
        RadialPin? pm = null;
        RadialPin pf = AddAt(RootKind.Creature, new[] { 1, 4, 3, 0 }, "fn", 7, "Cast Spell > Fireball", 0);
        List<(RadialPin, CascadeItem)> Fixed(CascadeItem[] vanilla)
        {
            var f = new List<(RadialPin, CascadeItem)>();
            foreach (RadialPin p in ForList(RootKind.Creature, ReadOnlySpan<int>.Empty))
            {
                if (p.IsHoist) { f.Add((p, p == pm ? meta : fire)); continue; }
                foreach (var it in vanilla) if (it.Tag is int s && s == p.Path[^1]) { f.Add((p, it)); break; }
            }
            return f;
        }
        List<(RadialPin, CascadeItem)> last = new();
        CascadeItem[] Run(CascadeItem[] vanilla) => Compose(vanilla, last = Fixed(vanilla));
        bool Invariant(CascadeItem[] composed, int lead)
        {
            foreach (var (p, item) in last) if (!ReferenceEquals(composed[lead + p.At].Tag, item.Tag)) return false;
            return true;
        }
        var c = Run(top);
        check.Expect(Order(c) == "Fireball s0 s1 s2 s3" && pf.At == 0 && Invariant(c, 0), $"pin to top splices at 0 ({Order(c)})");
        // Move s2 (index 3) up: becomes a reorder fixed at 2, s1 flows to 3.
        Move(RootKind.Creature, new[] { 2 }, null, 3, 2);
        c = Run(top);
        check.Expect(Order(c) == "Fireball s0 s2 s1 s3" && Invariant(c, 0), $"move up fixes the row, the neighbour flows back ({Order(c)})");
        // Move s0 (index 1) up past the pin (index 0): the pin is persisted, so it takes index 1.
        Move(RootKind.Creature, new[] { 0 }, null, 1, 0);
        c = Run(top);
        check.Expect(Order(c) == "s0 Fireball s2 s1 s3" && pf.At == 1 && Invariant(c, 0), $"move past a persisted row swaps their indices ({Order(c)})");
        // A second pin to top shifts every row of the list down one.
        pm = AddAt(RootKind.Creature, new[] { 6, 2 }, SubToken, 9, "Abilities > Meta", 0);
        c = Run(top);
        check.Expect(Order(c) == "Meta s0 Fireball s2 s1 s3" && pf.At == 2 && Invariant(c, 0), $"second pin lands at 0, the rest shift ({Order(c)})");
        // Unpin the first: rows below shift up.
        Remove(pf);
        c = Run(top);
        check.Expect(Order(c) == "Meta s0 s2 s1 s3" && pm.At == 0 && Invariant(c, 0), $"unpin shifts the rows below up ({Order(c)})");
        // A target where slot 2 is absent: its reorder row is skipped, the rest keep their places.
        var fewer = V(0, 1, 3);
        c = Run(fewer);
        check.Expect(Order(c) == "Meta s0 s1 s3", $"a reorder whose slot is absent is skipped ({Order(c)})");
        // Anchored header at 0, never counted; a garbage At past the end clamps to the last index (s0's reorder holds 1).
        var headed = new CascadeItem[] { new("Header"), new("s0", tag: 0), new("s1", tag: 1) };
        pm.At = 99;
        c = Run(headed);
        check.Expect(Order(c) == "Header s1 s0 Meta" && pm.At == 2 && Invariant(c, 1), $"header anchored, over-range At clamps ({Order(c)})");
        // Two rows claiming one index: the earlier row (lower N) wins, the later takes the next free.
        pm.At = 0;
        var pr = Find(RootKind.Creature, new[] { 2 }, hoist: false)!;
        pr.At = 0;
        c = Run(top);
        check.Expect(Order(c) == "s2 Meta s0 s1 s3" && pr.At == 0 && pm.At == 1, $"a tie pushes the later row forward ({Order(c)})");
        // A far move (a drop): s3 (index 4) to index 1; the persisted rows in (1..4] shift toward the source, flows fill.
        Move(RootKind.Creature, new[] { 3 }, null, 4, 1);
        c = Run(top);
        check.Expect(Order(c) == "s2 s3 Meta s0 s1" && pm.At == 2 && Invariant(c, 0), $"a far move shifts the persisted rows it crosses ({Order(c)})");
        Move(RootKind.Creature, new[] { 3 }, null, 1, 4);
        c = Run(top);
        check.Expect(Order(c) == "s2 Meta s0 s1 s3" && pm.At == 1 && Invariant(c, 0), $"and back ({Order(c)})");
        // A hoist dropped mid-list lands there; the rows from that index shift down.
        RadialPin pd = AddAt(RootKind.Creature, new[] { 1, 4, 3, 0 }, "fn", 7, "Cast Spell > Fireball", 2);
        c = Run(top);
        check.Expect(Order(c) == "s2 Meta Fireball s0 s1 s3" && pd.At == 2 && pm.At == 1 && Invariant(c, 0), $"a hoist at 2 splices at 2 ({Order(c)})");
        Remove(pd);
        check.Expect(Reset(RootKind.Creature) == 4 && _rows.Count == 0 && Order(Run(top)) == "s0 s1 s2 s3", "reset drops the root's rows, vanilla order returns");
        check.Expect(Find(RootKind.Creature, new[] { 2 }, hoist: false) == null, "find misses after reset");

        // Hide: the pin claims no index, replaces the row's reorder, survives the round-trip, and is dropped
        // per list by Unhide. The row filter itself is the service's (ItemsFor); here the bookkeeping.
        Move(RootKind.Creature, new[] { 1 }, null, 1, 0);
        RadialPin ph = Hide(RootKind.Creature, new[] { 1 });
        check.Expect(ph.Hidden && !ph.IsHoist && Find(RootKind.Creature, new[] { 1 }, hoist: false) == ph && _rows.Count == 1, "hide replaces the row's reorder");
        RadialPin pt = AddAt(RootKind.Creature, new[] { 3 }, "", 0, "", 0);
        check.Expect(ph.At == 0 && pt.At == 0, "a hide is not shifted by an insert");
        Remove(pt);
        check.Expect(_rows.Count == 1 && ph.At == 0, "and a hide's removal shifts nothing");
        var hp = Parse(Format(_rows).ToArray());
        check.Expect(hp.Bad == 0 && hp.Rows.Count == 1 && hp.Rows[0].Hidden && hp.Rows[0].Path[0] == 1, "hide round-trips as hide=1");
        check.Expect(Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "1/2"), new("p0.fn", "x"), new("p0.at", "0"), new("p0.hide", "1") }).Bad == 1, "a hidden hoist is garbage");
        Hide(RootKind.Creature, new[] { 6, 2 });
        check.Expect(Unhide(RootKind.Creature, Array.Empty<int>()) == 1 && _rows.Count == 1 && _rows[0].Path.Length == 2, "unhide drops the top list's hides only");
        Reset(RootKind.Creature);

        // Folders: a folder row and its members are lists of their own; refile / remove / rename / merge;
        // the rows round-trip; a cross-root copy is an ordinary hoist of the other kind.
        RadialPin fa = AddFolder(RootKind.Creature, "Buffs", 0);
        RadialPin m1 = AddAt(RootKind.Creature, new[] { 1, 2 }, "fn", 1, "Cast > Bless", 0, "Buffs");
        RadialPin m2 = AddAt(RootKind.Creature, new[] { 1, 3 }, "fn", 2, "Cast > Aid", 0, "Buffs");
        RadialPin t1 = AddAt(RootKind.Creature, new[] { 6, 1 }, "fn", 3, "Abilities > Rage", 0);
        check.Expect(fa.IsFolder && !fa.IsHoist && fa.ListPath.Length == 0 && m1.SameList(m2) && !m1.SameList(fa) && !m1.SameList(t1) && fa.SameList(t1),
            "a folder sits in the top list; its members share a list of their own");
        check.Expect(m2.At == 0 && m1.At == 1 && t1.At == 0 && fa.At == 1 && ForList(RootKind.Creature, ReadOnlySpan<int>.Empty).Count == 2
          && ForList(RootKind.Creature, ReadOnlySpan<int>.Empty, "Buffs").Count == 2 && Folders(RootKind.Creature).Count == 1, "members claim indices in the folder, not the top list");
        Refile(m1, "", 0);
        check.Expect(m1.In == "" && m1.At == 0 && t1.At == 1 && fa.At == 2 && m2.At == 0, "refile to the top list shifts the top rows down and the folder's up");
        Refile(m1, "Buffs", 1);
        check.Expect(m1.In == "Buffs" && m1.At == 1 && m2.At == 0 && t1.At == 0 && fa.At == 1, "and back");
        RadialPin r4 = AddAt(RootKind.Creature, new[] { 4 }, "", 0, "Attack", 0, "Buffs");
        check.Expect(r4.In == "Buffs" && !r4.IsHoist && r4.At == 0 && m2.At == 1 && Filed(RootKind.Creature).Count == 1 && Filed(RootKind.Creature)[0] == r4
          && ForList(RootKind.Creature, ReadOnlySpan<int>.Empty).Count == 2, "a top-level reorder files in a folder: a member there, filed off the top list");
        var fp = Parse(Format(_rows).ToArray());
        check.Expect(fp.Bad == 0 && fp.Rows.Count == 5 && fp.Rows[0].IsFolder && fp.Rows[0].Folder == "Buffs" && fp.Rows[0].Path.Length == 0 && fp.Rows[1].In == "Buffs"
          && fp.Rows[4].In == "Buffs" && !fp.Rows[4].IsHoist && fp.Rows[4].Label == "Attack" && Format(fp.Rows).Count == Format(_rows).Count, "folder and in round-trip, on a filed reorder too");
        Refile(r4, "", 0);
        check.Expect(r4.In == "" && r4.At == 0 && t1.At == 1 && Filed(RootKind.Creature).Count == 0, "a filed reorder refiles to the top list");
        RadialPin h4 = Hide(RootKind.Creature, new[] { 4 });
        Refile(h4, "Buffs", 0);
        check.Expect(!_rows.Contains(r4) && h4.In == "" && Filed(RootKind.Creature).Count == 0, "hiding the slot drops its reorder; a hide never files");
        Unhide(RootKind.Creature, Array.Empty<int>());
        check.Expect(Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "1/2"), new("p0.fn", "x"), new("p0.in", "gone"), new("p0.at", "0") }).Rows[0].In == "",
            "a member of a missing folder is un-filed");
        var sp = Parse(Format(new List<RadialPin> { new() { Root = RootKind.Door, Path = new[] { 0 }, Fn = SelfActions.Prefix + "sheet", Label = "Character sheet", Icon = "ir_charsheet", At = 0 } }).ToArray());
        check.Expect(sp.Bad == 0 && sp.Rows.Count == 1 && sp.Rows[0].IsSelf && sp.Rows[0].Icon == "ir_charsheet" && sp.Rows[0].LeafLabel == "Character sheet" && sp.Rows[0].IsHoist,
            "a self pin with a one-step path and an icon round-trips as a hoist");
        check.Expect(Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "0"), new("p0.fn", "x"), new("p0.at", "0") }).Bad == 1, "a one-step path is still garbage for a walked hoist");
        check.Expect(Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.folder", "x"), new("p0.path", "1"), new("p0.at", "0") }).Bad == 1
          && Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "1/2"), new("p0.in", "x"), new("p0.at", "0") }).Bad == 1
          && Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.path", "1"), new("p0.in", "x"), new("p0.hide", "1"), new("p0.at", "0") }).Bad == 1
          && Parse(new[] { new KeyValuePair<string, string>("p0.root", "Door"), new("p0.at", "0") }).Bad == 1, "a folder with a path, an in on a deeper reorder or a hide, a row with neither: garbage");
        RadialPin fb = AddFolder(RootKind.Creature, "Debuffs", 0);
        Refile(t1, "Debuffs", 0);
        RenameFolder(fa, "Debuffs");
        check.Expect(FindFolder(RootKind.Creature, "Buffs") == null && Folders(RootKind.Creature).Count == 1 && m1.In == "Debuffs" && m2.In == "Debuffs" && t1.In == "Debuffs",
            "rename onto an existing name merges: one folder, every member follows");
        RenameFolder(fb, "All");
        check.Expect(fb.Folder == "All" && m1.In == "All" && FindFolder(RootKind.Creature, "All") == fb, "rename moves the members with the name");
        RenameFolder(fb, "");
        check.Expect(fb.Folder == "All", "an empty rename is nothing");
        check.Expect(RemoveFolder(fb) == 3 && Folders(RootKind.Creature).Count == 0 && m1.In == "" && t1.In == "" && ForList(RootKind.Creature, ReadOnlySpan<int>.Empty).Count == 3,
            "remove folder returns its members to the top list");
        check.Expect(RemoveFolder(m1) == 0, "removing a non-folder is nothing");
        RadialPin cp = AddAt(RootKind.Placeable, m1.Path, m1.Fn, m1.Param, m1.Label, 0);
        check.Expect(cp.Root == RootKind.Placeable && Find(RootKind.Placeable, m1.Path, hoist: true) == cp && Find(RootKind.Creature, m1.Path, hoist: true) == m1 && !cp.SameList(m1),
            "a copy on another root is that root's hoist at the same path");
        Reset(RootKind.Creature); Reset(RootKind.Placeable);
        check.Expect(_rows.Count == 0, "reset drops folders and members too");

        // Greyed row text.
        var g = Greyed(new RadialPin { Root = RootKind.Creature, Fn = "x", Label = "Cast Spell > Wizard > 3 > Fireball" });
        check.Expect(g.Label == "Fireball" && g.Count == "(Cast Spell > Wizard > 3)" && g.Disabled && g.Tag is RadialRowTag { Valid: false },
            $"greyed row: leaf label, parents as the count, disabled, invalid tag (\"{g.Label}\" \"{g.Count}\")");
        check.Expect(Greyed(new RadialPin { Fn = "x", Label = "" }).Label == "?", "a label-less greyed row still has text");

        // Validation on fake (fn, param) pairs.
        var sub = new RadialSlot(0, SlotKind.Creator, "", "", "", false, 9, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        check.Expect(Validate(new RadialPin { Fn = SubToken, Param = 9 }, sub) && !Validate(new RadialPin { Fn = SubToken, Param = 8 }, sub)
          && !Validate(new RadialPin { Fn = "", Param = 9 }, sub), "a submenu hoist validates on kind + param; a reorder never validates");
        check.Expect(FnToken(sub) == SubToken && FnToken(default) == "", "token: sub for a submenu, empty for an empty slot");
        IntPtr fn = OperatingSystem.IsWindows() ? (IntPtr)((long)Base + 0x1000) : SymbolMap.Resolve("malloc", null);
        if (fn != IntPtr.Zero)
        {
            var leaf = new RadialSlot(0, SlotKind.Entry, "", "", "", false, 0x1300007a, IntPtr.Zero, fn, IntPtr.Zero, IntPtr.Zero);
            string tok = FnToken(leaf);
            check.Expect(OperatingSystem.IsWindows() ? tok == "rva:1000" : tok.Length > 0 && SymbolMap.Resolve(tok, null) == fn,
                $"token names the exported fn and the name resolves back to it (\"{tok}\")");
            var pin = new RadialPin { Fn = tok, Param = 0x1300007a };
            check.Expect(Validate(pin, leaf), "a matching (fn, param) validates");
            check.Expect(!Validate(new RadialPin { Fn = tok, Param = 0x1300007b }, leaf), "a different param (another class slot) does not");
            check.Expect(Validate(pin, leaf with { Param = 0x1300007a | TransientBit }) && Validate(new RadialPin { Fn = tok, Param = 0x1300007a | TransientBit }, leaf),
                "the transient bit (spontaneous caster out of uses) is no part of the identity");
            var off = leaf with { Fn = (IntPtr)((long)fn + 2) };
            check.Expect(!Validate(pin, off) && (OperatingSystem.IsWindows() || FnToken(off) == ""), "a different fn does not; an inexact address has no name");
            check.Expect(OperatingSystem.IsWindows() || FnToken(leaf with { Fn = (IntPtr)((long)fn + 1) }) == "", "an odd fn with no handler object is not persistable");
            var subEntry = sub with { Fn = fn };
            string st = FnToken(subEntry);
            check.Expect(st == SubToken + ":" + tok && Validate(new RadialPin { Fn = st, Param = 9 }, subEntry)
              && !Validate(new RadialPin { Fn = SubToken, Param = 9 }, subEntry) && !Validate(new RadialPin { Fn = st, Param = 9 }, sub)
              && !Validate(new RadialPin { Fn = st, Param = 9 }, subEntry with { Fn = (IntPtr)((long)fn + 2) }),
              $"a submenu with an entry carries the entry in its token and validates on both (\"{st}\")");
            check.Expect(!Validate(new RadialPin { Fn = "_ZNoSuchSymbolEv", Param = 0x1300007a }, leaf), "a name that no longer resolves does not");
        }

        // Identity: the file's rows split by key on load; a save writes every identity back; key-less
        // rows are adopted by the first identity to load.
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        var file = Format(new List<RadialPin>
        {
            new() { Root = RootKind.Creature, Path = new[] { 1 }, At = 0, Who = "s|a" },
            new() { Root = RootKind.Creature, Path = new[] { 2 }, At = 0, Who = "s|b" },
            new() { Root = RootKind.Creature, Path = new[] { 3 }, At = 1 },
        });
        check.Expect(file.Count == 3 * 3 + 2 && Parse(file.ToArray()).Rows[0].Who == "s|a", "who round-trips, absent when empty");
        var (rl, _) = Parse(file.ToArray());
        check.Expect(!Split("s|z") && _rows.Count == 0, "no rows, nothing adopted");
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        _other.AddRange(rl);
        check.Expect(Split("s|a") && _rows.Count == 2 && _other.Count == 1 && _rows[0].Path[0] == 1 && _rows[1].Path[0] == 3 && _rows[1].Who == "s|a" && _other[0].Who == "s|b",
            "the first identity takes its own rows and adopts the key-less one, in file order");
        Hide(RootKind.Creature, new[] { 5 });
        check.Expect(_rows[2].Who == "s|a", "a new row carries the current key");
        check.Expect(!Split("s|b") && _rows.Count == 1 && _other.Count == 3 && _rows[0].Path[0] == 2, "switching identity swaps the live set, drops nothing");
        Split("s|c");
        check.Expect(_rows.Count == 0 && _other.Count == 4, "an unknown identity starts empty; nothing is adopted twice");
        List<RadialPin> all;
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        _other.AddRange(Parse(file.ToArray()).Rows);
        check.Expect(!Split("|") && _rows.Count == 0 && _other.Count == 3, "a key with no character adopts nothing");
        check.Expect(Split("s|a") && _rows.Count == 2, "and the next key with a character still adopts");
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        _other.AddRange(Parse(file.ToArray()).Rows); Split("s|a");
        Hide(RootKind.Creature, new[] { 5 });
        all = new List<RadialPin>(_other); all.AddRange(_rows);
        check.Expect(Format(all).Count == 4 * 3 + 4 + 1, "a save writes every identity's rows");
        _rows.Clear(); _other.Clear(); _who = null; _adopted = false;
        log.Info(check.Ok ? "RadialPins.SelfCheck: passed" : "RadialPins.SelfCheck: FAILED");
    }
}
