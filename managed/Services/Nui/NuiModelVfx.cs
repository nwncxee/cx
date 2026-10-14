using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The visual effects a model view's doll wears: only the <c>visualeffects.2da</c> rows the server lists, each on the body part it
/// names, put on the doll through the engine's own add call. The client cannot tell a permanent effect from a temporary one, so the
/// server chooses. The add call takes its transform by value: Linux copies it, Win64 destroys the copy it is handed, so each call there gets a
/// fresh copy of the identity transform built by the engine's own tree constructor.
/// </summary>
internal static unsafe class NuiModelVfx
{
    /// <summary>The most effects one doll wears.</summary>
    internal const int MaxRows = 16;
    /// <summary>The bytes a Win64 <c>ObjectVisualTransformData</c> takes (an MSVC map: head pointer and size), and the same again for the
    /// copy constructor's unused allocator.</summary>
    private const int TransformBytes = 32;

    /// <summary>One effect to wear: its <c>visualeffects.2da</c> row and the body part it sits on. Part 0 is the hand node, 1 the
    /// impact node, 2-11 the creature's ground nodes <c>monster0</c>-<c>monster9</c>, and any other number the root.</summary>
    internal readonly record struct VfxRow(ushort Row, byte Part);

    private sealed class VfxState
    {
        public ExtenderLog? Log;
        public IntPtr Identity;
        public bool Ready, Said, SoundReady;
        /// <summary>The managed thread inside <see cref="Wear"/> with the sounds muted, or 0.</summary>
        public int MuteThread;
        public ExoSoundPlay3DOneShotFn OrigOneShot;
        public ExoSoundSourcePlayFn OrigPlay;
    }

    private static readonly VfxState _v = new();

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _v.Log = log;
        _v.Identity = SymbolMap.Resolve(SymbolMap.IdentityVisualTransform, log);
        _v.Ready = _v.Identity != IntPtr.Zero && SymbolMap.Resolve(SymbolMap.ObjAddVisualEffect, log) != IntPtr.Zero
                   && SymbolMap.Resolve(SymbolMap.ObjKillAllVisualEffects, log) != IntPtr.Zero
                   && (!OperatingSystem.IsWindows() || SymbolMap.Resolve(SymbolMap.TransformCopy, log) != IntPtr.Zero);
        if (!_v.Ready) { log.Always("NuiModelVfx: the engine's effect calls did not resolve -- dolls wear no effects"); return; }
        _v.OrigOneShot = hooks.InstallExoSoundPlay3DOneShot(&OnOneShot);
        _v.OrigPlay = hooks.InstallExoSoundSourcePlay(&OnPlay);
        _v.SoundReady = _v.OrigOneShot != null && _v.OrigPlay != null;
        if (!_v.SoundReady) log.Always("NuiModelVfx: the engine's sound calls did not hook -- vfxmute is ignored");
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnOneShot(IntPtr self, IntPtr resref, VectorArg at, float volume, byte group, uint a, byte b)
        => _v.MuteThread == Environment.CurrentManagedThreadId ? IntPtr.Zero : _v.OrigOneShot(self, resref, at, volume, group, a, b);

    [UnmanagedCallersOnly]
    private static IntPtr OnPlay(IntPtr self)
        => _v.MuteThread == Environment.CurrentManagedThreadId ? IntPtr.Zero : _v.OrigPlay(self);

    /// <summary>A <c>vfx</c> value, <c>row[:part],row[:part],...</c>, as its effects. Text that is not a row, a part past 255 and anything past
    /// <see cref="MaxRows"/> is skipped, and a row named twice on one part is worn once.</summary>
    internal static VfxRow[] Parse(string value) =>
        value.Split(',').Select(One).Where(e => e.Row >= 0).Distinct().Take(MaxRows).Select(e => new VfxRow((ushort)e.Row, (byte)e.Part)).ToArray();

    /// <summary>The first part of a <c>vfx</c> value the client will not wear, with why: text that is not <c>row[:part]</c>, a row past the
    /// <paramref name="table"/> rows of <c>visualeffects.2da</c> (a table below zero is unread and refuses nothing), or the effect past <see cref="MaxRows"/>.
    /// False when every part is worn. <c>all</c> and an empty value have none.</summary>
    internal static bool Problem(string value, int table, out string part, out string why)
    {
        part = why = "";
        if (IsAll(value)) return false;
        var seen = new HashSet<(int, int)>();
        foreach (string text in value.Split(','))
        {
            if (text.Trim().Length == 0) continue;
            (int row, int on) = One(text);
            if (row < 0) { part = text.Trim(); why = "not an effect row: a row 0-65535, then ':' and a part 0-255 if wanted"; return true; }
            if (!NuiModelGuard.RowInTable(row, table)) { part = text.Trim(); why = $"past the {table} rows of the client's visualeffects.2da"; return true; }
            if (seen.Add((row, on)) && seen.Count > MaxRows) { part = text.Trim(); why = $"more than {MaxRows} effects; the rest are not worn"; return true; }
        }
        return false;
    }

    private static (int Row, int Part) One(string text)
    {
        string[] bits = text.Split(':');
        if (bits.Length > 2 || !ushort.TryParse(bits[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ushort row)) return (-1, 0);
        if (bits.Length == 1) return (row, 0);
        return byte.TryParse(bits[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte part) ? (row, part) : (-1, 0);
    }

    /// <summary>The doll wears exactly <paramref name="rows"/>: every effect it had is ended first. With <paramref name="mute"/> the engine's
    /// sounds for the new effects (their impact one-shot and looping source) are not started.</summary>
    internal static void Wear(IntPtr doll, VfxRow[] rows, bool mute = false) => Wear(doll, rows, rows.Length, 0, mute);

    /// <summary>The first <paramref name="count"/> of <paramref name="rows"/> on the doll. From 0 the doll's own effects are ended first; from a
    /// later index only the rows from there on are added to what the doll wears.</summary>
    internal static void Wear(IntPtr doll, VfxRow[] rows, int count, int from, bool mute)
    {
        if (doll == IntPtr.Zero) return;
        if (!_v.Ready)
        {
            if (count > 0 && !_v.Said) { _v.Said = true; _v.Log?.Always("NuiModelVfx: this client puts no effects on a doll -- vfx ignored"); }
            return;
        }
        if (from == 0) EngineFn.ObjKillAllVisualEffects(doll);
        Vec3 zero = default;
        byte* scratch = stackalloc byte[TransformBytes];
        if (mute && _v.SoundReady) _v.MuteThread = Environment.CurrentManagedThreadId;
        try
        {
            int table = NuiModelTarget.VisualEffectRows();
            for (int i = from; i < count; i++)
            {
                VfxRow e = rows[i];
                if (!NuiModelGuard.RowInTable(e.Row, table))
                {
                    if (NuiModelGuard.Once($"vfx {e.Row}")) _v.Log?.Always($"NuiModelVfx: effect row {e.Row} is past the {table} the client's visualeffects.2da has -- not worn");
                    continue;
                }
                if (EngineFn.ObjAddVisualEffect(doll, e.Row, 0, NuiModelDoll.InvalidId, e.Part, 0, VectorArg.Of(&zero), TransformFor(scratch)) == 0)
                    _v.Log?.Always($"NuiModelVfx: the engine would not load effect row {e.Row}");
            }
        }
        finally { _v.MuteThread = 0; }
        if (count > 0) _v.Log?.Always($"NuiModelVfx: the doll wears effect rows {string.Join(',', rows.Take(count).Select(e => $"{e.Row}:{e.Part}"))}{(mute ? ", sounds muted" : "")}");
    }

    /// <summary>The <c>vfx</c> value that mirrors the target's own effects instead of listing rows.</summary>
    internal const string AllValue = "all";
    /// <summary>The part number a node name the parts do not name stands for: the root.</summary>
    private const byte RootPart = 255;
    /// <summary>The most nodes one walk of a creature's effect list visits, so a damaged list ends the walk.</summary>
    private const int MaxWalk = 256;

    /// <summary>The effects a mirroring view last read from its target, and the ones its doll wears. Both are filled in place, so a frame
    /// allocates nothing.</summary>
    internal sealed class Mirror
    {
        public readonly VfxRow[] Seen = new VfxRow[MaxRows];
        public readonly VfxRow[] Worn = new VfxRow[MaxRows];
        public int SeenCount, WornCount;
    }

    /// <summary>What the doll needs to match what the target shows: nothing, the new rows added after those it wears, or all of them again.</summary>
    internal enum Step { None, Append, Rewear }

    /// <summary>Does this <c>vfx</c> value ask for the target's own effects? The word <c>all</c> alone, whatever its case or spacing.</summary>
    internal static bool IsAll(string value) => value.AsSpan().Trim().Equals(AllValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>The body part a node name stands for: <c>handconjure</c> 0, <c>impact</c> 1, <c>monster0</c>-<c>monster9</c> 2-11, anything else the root.</summary>
    internal static byte PartOfNode(ReadOnlySpan<byte> name)
    {
        if (name.SequenceEqual("handconjure"u8)) return 0;
        if (name.SequenceEqual("impact"u8)) return 1;
        if (name.Length == 8 && name[..7].SequenceEqual("monster"u8) && name[7] is >= (byte)'0' and <= (byte)'9') return (byte)(2 + name[7] - '0');
        return RootPart;
    }

    /// <summary>How the doll's worn effects become the target's. The engine's list only grows at its end, so rows that start with the worn
    /// ones are added to them; any other change puts the whole set on again.</summary>
    internal static Step Plan(VfxRow[] worn, int wornCount, VfxRow[] seen, int seenCount)
    {
        if (!seen.AsSpan(0, seenCount).StartsWith(worn.AsSpan(0, wornCount))) return Step.Rewear;
        return seenCount == wornCount ? Step.None : Step.Append;
    }

    /// <summary>The effects <paramref name="creature"/> shows now, in the order the engine added them, into <paramref name="into"/>: those the client has
    /// shown, that last (a one-shot impact is gone in a moment) and that have no source object (a beam from another creature has no end on the doll).
    /// The same row on the same part counts once, and at most <see cref="MaxRows"/> are read. Returns how many.</summary>
    internal static int Read(IntPtr creature, VfxRow[] into)
    {
        int n = 0, walked = 0;
        CExoLinkedListNode node = new CNWCObject(creature).Effects.Head;
        while (!node.IsNull && walked++ < MaxWalk && n < MaxRows)
        {
            CNWCVisualEffectOnObject e = node.Data;
            if (!e.IsNull && e.Shown != 0 && e.Lasting != 0 && e.Source == NuiModelDoll.InvalidId)
            {
                IntPtr name = e.Node;
                byte part = name == IntPtr.Zero ? RootPart : PartOfNode(MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)name));
                var row = new VfxRow(e.Row, part);
                if (!into.AsSpan(0, n).Contains(row)) into[n++] = row;
            }
            node = node.Next;
        }
        return n;
    }

    /// <summary>Make the doll wear what <paramref name="who"/> shows now. With <paramref name="rebuilt"/> the doll is new, so everything goes on again;
    /// otherwise only a change touches it, and a frame with none reads the list and compares, nothing more.</summary>
    internal static void Follow(IntPtr doll, IntPtr who, Mirror m, bool rebuilt, bool mute)
    {
        if (doll == IntPtr.Zero || who == IntPtr.Zero) return;
        m.SeenCount = Read(who, m.Seen);
        Step step = rebuilt ? Step.Rewear : Plan(m.Worn, m.WornCount, m.Seen, m.SeenCount);
        if (step == Step.None) return;
        if (m.SeenCount > 0 || m.WornCount > 0) _v.Log?.Always($"NuiModelVfx: mirroring {m.SeenCount} effects from the target ({(step == Step.Append ? "added" : "all again")})");
        Wear(doll, m.Seen, m.SeenCount, step == Step.Append ? m.WornCount : 0, mute);
        Array.Copy(m.Seen, m.Worn, m.SeenCount);
        m.WornCount = m.SeenCount;
    }

    /// <summary>The transform argument for one add call: the engine's identity as it is on Linux, and on Windows a fresh copy in <paramref name="scratch"/> that the call
    /// destroys itself.</summary>
    private static IntPtr TransformFor(byte* scratch)
    {
        if (!OperatingSystem.IsWindows()) return _v.Identity;
        EngineFn.TransformCopy((IntPtr)scratch, _v.Identity, (IntPtr)(scratch + TransformBytes / 2));
        return (IntPtr)scratch;
    }

    /// <summary>The doll is going: end its effects while the creature is still whole.</summary>
    internal static void Clear(IntPtr doll)
    {
        if (_v.Ready && doll != IntPtr.Zero) EngineFn.ObjKillAllVisualEffects(doll);
    }

    /// <summary>Oracle: hand-written <c>vfx</c> values and the effects they name.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(Parse("").Length == 0, "an empty value names no effect");
        VfxRow[] rows = Parse("147, 408,147");
        check.Expect(rows.Length == 2 && rows[0] == new VfxRow(147, 0) && rows[1] == new VfxRow(408, 0), "rows keep their order, sit on the hand and a repeat is dropped");
        check.Expect(Parse("x,-1,70000,13").Length == 1 && Parse("x,-1,70000,13")[0].Row == 13, "text, a negative and a row past 65535 are skipped");
        VfxRow[] hostile = Parse("65000,65535,612,611");
        check.Expect(hostile.Length == 4 && !NuiModelGuard.RowInTable(hostile[0].Row, 612) && !NuiModelGuard.RowInTable(hostile[1].Row, 612)
                     && !NuiModelGuard.RowInTable(hostile[2].Row, 612) && NuiModelGuard.RowInTable(hostile[3].Row, 612),
                     "rows the parse accepts but a 612-row table has none of are refused by the guard, its last row is not");
        check.Expect(Parse("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17").Length == 16, "seventeen rows keep the first sixteen");
        check.Expect(IsAll("all") && IsAll("  All ") && IsAll("ALL") && !IsAll("all,147") && !IsAll("") && !IsAll("al"), "only the word all asks for the target's effects");
        check.Expect(PartOfNode("handconjure"u8) == 0 && PartOfNode("impact"u8) == 1 && PartOfNode("monster0"u8) == 2 && PartOfNode("monster9"u8) == 11,
                     "the engine's node names give back the parts that made them");
        check.Expect(PartOfNode("monster"u8) == 255 && PartOfNode("monster10"u8) == 255 && PartOfNode("impc_x"u8) == 255 && PartOfNode(""u8) == 255 && PartOfNode("root"u8) == 255,
                     "any other node name is the root");
        VfxRow a = new(147, 0), b = new(408, 1), c = new(9, 255);
        check.Expect(Plan([a, b], 2, [a, b], 2) == Step.None && Plan([], 0, [], 0) == Step.None, "the same rows need nothing");
        check.Expect(Plan([a], 1, [a, b, c], 3) == Step.Append && Plan([], 0, [a], 1) == Step.Append, "rows added at the end are added");
        check.Expect(Plan([a, b], 2, [b, a], 2) == Step.Rewear && Plan([a, b], 2, [a], 1) == Step.Rewear && Plan([a, b], 2, [], 0) == Step.Rewear
                     && Plan([a, b], 2, [a, c], 2) == Step.Rewear && Plan([a], 1, [b, a], 2) == Step.Rewear,
                     "a changed order, a removed row or a replaced one starts over");
        check.Expect(Plan([a, b, c], 1, [a, c], 2) == Step.Append, "rows past the worn count are not compared");
        VfxRow[] parts = Parse("147:1, 147:5,147:1, 9:300, 9:x, 12:2:3, 7:255");
        check.Expect(Problem("147,abc", -1, out string bad, out string why) && bad == "abc" && why.Length > 0, "a part that is not a row is the problem");
        check.Expect(Problem("147, 700", 612, out bad, out _) && bad == "700" && !Problem("147,611", 612, out _, out _) && !Problem("65000", -1, out _, out _),
                     "a row past a 612-row table is the problem, the last row is not, and an unread table refuses no row");
        check.Expect(!Problem("", 612, out _, out _) && !Problem("all", 612, out _, out _) && !Problem("147, ,408:2", 612, out _, out _), "an empty value, all and a gap name no problem");
        check.Expect(Problem("9:300", -1, out bad, out _) && bad == "9:300" && Problem("1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17", 612, out bad, out _) && bad == "17",
                     "a part past 255 is the problem, and so is the seventeenth effect");
        check.Expect(parts.Length == 3 && parts[0] == new VfxRow(147, 1) && parts[1] == new VfxRow(147, 5) && parts[2] == new VfxRow(7, 255),
                     "a row names its part, the same row on two parts is worn twice, and a bad part skips the entry");
        log.Info(check.Ok ? "NuiModelVfx.SelfCheck: passed" : "NuiModelVfx.SelfCheck: FAILED");
    }
}
