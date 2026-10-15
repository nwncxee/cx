using System;
using System.Collections.Generic;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>
/// Which node is which pose bone on a body. Content names the limbs in <c>cx_rig.2da</c>, one row per skeleton
/// model, matched up the body's supermodel chain; <c>appearance.2da</c>'s <c>HEAD_NAME</c> names the head. Where
/// content names none the stock biped names stand. A rig is lowercase NUL-terminated names in
/// <see cref="PoseService.BoneIndex"/> order, empty where it has no such bone; each resolves once per module.
/// </summary>
internal static unsafe class RigMap
{
    /// <summary>The rig map's resref, loaded as a script's <c>Get2DAString</c> loads a 2DA, so a hak or NWSync copy wins.</summary>
    private static ReadOnlySpan<byte> Resref => "cx_rig\0"u8;
    /// <summary>The column naming the skeleton model a row is for.</summary>
    private const string ModelColumn = "MODEL";
    /// <summary>The head's place in a rig: <c>appearance.2da</c> names it, so the map has no column for it.</summary>
    private const int HeadSlot = 8;
    /// <summary>Bytes of a model name a cache key holds: a resref's length.</summary>
    private const int KeyBytes = 16;
    /// <summary>Supermodel links followed before a chain counts as a loop.</summary>
    internal const int MaxChain = 16;

    /// <summary>The map's column per bone, in <see cref="PoseService.BoneIndex"/> order.</summary>
    private static readonly string?[] Columns = ["RTHIGH", "RSHIN", "RFOOT", "LTHIGH", "LSHIN", "LFOOT", "TORSO", "NECK", null,
                                                 "PELVIS", "RBICEP", "RFOREARM", "RHAND", "LBICEP", "LFOREARM", "LHAND"];
    /// <summary>The stock biped's names, carried by every stock humanoid (a_ba and its descendants), and
    /// <c>head_g</c>, the head the engine looks for when <c>HEAD_NAME</c> names none.</summary>
    private static readonly string[] StockNames = ["rthigh_g", "rshin_g", "rfoot_g", "lthigh_g", "lshin_g", "lfoot_g", "torso_g", "neck_g",
                                                   "head_g", "pelvis_g", "rbicep_g", "rforearm_g", "rhand_g", "lbicep_g", "lforearm_g", "lhand_g"];
    /// <summary>The stock biped's rig.</summary>
    internal static readonly byte[][] Stock = Terminated(StockNames);

    /// <summary>The engine reads, resolved once, and what one module's content resolved to (cleared at every
    /// module edge, when the haks change).</summary>
    private sealed class RigMapState
    {
        public ExtenderLog? Log;
        /// <summary>Every engine read resolved: without it every body keeps the stock names, said once.</summary>
        public bool Ready;
        /// <summary>The address of <c>g_pRules</c>.</summary>
        public IntPtr Rules;
        public bool InModule;
        /// <summary>The map's rows by model resref, case-insensitive; null until read this module.</summary>
        public Dictionary<string, string[]>? Rows;
        /// <summary>Resolved rigs by (model name key, appearance row).</summary>
        public readonly Dictionary<(ulong, ulong, int), byte[][]> Rigs = new();
        /// <summary>Resolved heads by appearance row: the name as a rig holds it, and as text.</summary>
        public readonly Dictionary<int, (byte[] Name, string Text)> Heads = new();
    }

    private static readonly RigMapState _s = new();

    /// <summary>Resolves the engine reads once; every service that asks for a rig or a head calls it.</summary>
    internal static void Init(ExtenderLog log)
    {
        if (_s.Log != null) return;
        _s.Log = log;
        _s.Ready = EngineLayout.Require(log, "RigMap", "CNWCCreature", "CPathfindInformation", "CNWRules", "CTwoDimArrays", "C2DA")
                   && SymbolMap.Require(log, "RigMap", SymbolMap.Rules, SymbolMap.TwoDimGetCached2DA, SymbolMap.C2DAGetCExoStringEntry,
                                        SymbolMap.ExoStringInit, SymbolMap.ExoStringDtor);
        _s.Rules = _s.Ready ? Native.Sym(SymbolMap.Rules) : IntPtr.Zero;
    }

    /// <summary>The rig of <paramref name="body"/>, worn by appearance row <paramref name="row"/> (-1: none known).
    /// Hot: one key read and one lookup once resolved; the stock rig while the engine's tables are not reachable.</summary>
    internal static byte[][] Of(IntPtr body, int row)
    {
        Edge();
        (ulong lo, ulong hi) = PoseRig.ModelKey(body);
        if (_s.Rigs.TryGetValue((lo, hi, row), out byte[][]? rig)) return rig;
        if (Rows() is not { } rows) return Stock;
        rig = Build(PoseRig.ModelChain(body), Head(row).Text, rows);
        _s.Rigs[(lo, hi, row)] = rig;
        return rig;
    }

    /// <summary>The head node at appearance row <paramref name="row"/>: <c>HEAD_NAME</c>, or <c>head_g</c> where
    /// <c>CNWCCreature::LookAt</c> takes it -- the engine refuses the cell, or no row is known.</summary>
    internal static (byte[] Name, string Text) Head(int row)
    {
        Edge();
        if (_s.Heads.TryGetValue(row, out var head)) return head;
        IntPtr table = row >= 0 && Arrays() is { } t ? t.Appearance : IntPtr.Zero;
        if (row >= 0 && table == IntPtr.Zero) return (Stock[HeadSlot], StockNames[HeadSlot]);
        string text = (table == IntPtr.Zero ? null : Cell(table, row, "HEAD_NAME")) ?? StockNames[HeadSlot];
        head = (Terminated(text), text.ToLowerInvariant());
        _s.Heads[row] = head;
        return head;
    }

    /// <summary>The creature's <c>appearance.2da</c> row, read where <c>CNWCCreature::LookAt</c> reads it; -1 when it has no pathfind block.</summary>
    internal static int Appearance(IntPtr creature)
    {
        if (!_s.Ready) return -1;
        CPathfindInformation pf = new CNWCCreature(creature).Pathfind;
        return pf.IsNull ? -1 : pf.Appearance;
    }

    /// <summary>The rig for a model chain, nearest first: the first model with a map row names the limbs, else
    /// the stock names do; <paramref name="head"/> names the head.</summary>
    internal static byte[][] Build(List<string> chain, string head, Dictionary<string, string[]> rows)
    {
        string[]? row = null;
        foreach (string model in chain)
            if (rows.TryGetValue(model, out row)) break;
        string[] names = (string[])(row ?? StockNames).Clone();
        names[HeadSlot] = head;
        return Terminated(names);
    }

    /// <summary>A model name, ASCII-lowercased, as a cache key: its first <see cref="KeyBytes"/> bytes.</summary>
    internal static (ulong, ulong) Key(byte* s)
    {
        ulong lo = 0, hi = 0;
        for (int i = 0; i < KeyBytes && s[i] != 0; i++)
            if (i < 8) lo |= (ulong)PoseService.Lower(s[i]) << (i * 8);
            else hi |= (ulong)PoseService.Lower(s[i]) << ((i - 8) * 8);
        return (lo, hi);
    }

    /// <inheritdoc cref="Key(byte*)"/>
    internal static (ulong, ulong) Key(string s)
    {
        ulong lo = 0, hi = 0;
        for (int i = 0; i < KeyBytes && i < s.Length; i++)
            if (i < 8) lo |= (ulong)PoseService.Lower((byte)s[i]) << (i * 8);
            else hi |= (ulong)PoseService.Lower((byte)s[i]) << ((i - 8) * 8);
        return (lo, hi);
    }

    /// <summary>Names as a rig holds them: lowercase, NUL-terminated, empty for none.</summary>
    private static byte[][] Terminated(string[] names)
    {
        var rig = new byte[names.Length][];
        for (int i = 0; i < names.Length; i++) rig[i] = Terminated(names[i]);
        return rig;
    }

    private static byte[] Terminated(string name)
        => name.Length == 0 ? [] : Encoding.Latin1.GetBytes(name.ToLowerInvariant() + "\0");

    /// <summary>Forgets every resolved rig and head, and the map, when a module is entered or left.</summary>
    private static void Edge()
    {
        bool now = GameGate.InModule;
        if (now == _s.InModule) return;
        _s.InModule = now;
        _s.Rows = null;
        _s.Rigs.Clear();
        _s.Heads.Clear();
    }

    /// <summary>The engine's 2DA table, or null while it is not reachable.</summary>
    private static CTwoDimArrays? Arrays()
    {
        IntPtr rules = _s.Rules == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)_s.Rules;
        IntPtr arrays = rules == IntPtr.Zero ? IntPtr.Zero : new CNWRules(rules).TwoDimArrays;
        return arrays == IntPtr.Zero ? null : new CTwoDimArrays(arrays);
    }

    /// <summary>The map, read once per module through the engine's own 2DA cache: no file is no rows, and so is
    /// missing an engine read. Null while the engine's tables are not reachable.</summary>
    private static Dictionary<string, string[]>? Rows()
    {
        if (_s.Rows != null) return _s.Rows;
        if (!_s.Ready) return _s.Rows = new(StringComparer.OrdinalIgnoreCase);
        if (Arrays() is not { } arrays) return null;
        ExoStringRec name = default;
        fixed (byte* n = Resref) EngineFn.ExoStringInit((IntPtr)(&name), n, uint.MaxValue);
        IntPtr table = EngineFn.TwoDimGetCached2DA(arrays.Ptr, (IntPtr)(&name), 0);
#if WINDOWS
        name = default;   // the Win64 callee destroyed its by-value CExoString
#endif
        EngineFn.ExoStringDtor((IntPtr)(&name));
        var rows = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        int count = table == IntPtr.Zero ? 0 : new C2DA(table).Rows;
        for (int r = 0; r < count; r++)
        {
            if (Cell(table, r, ModelColumn) is not { } model) continue;
            var names = new string[Columns.Length];
            for (int i = 0; i < names.Length; i++) names[i] = Columns[i] is { } c ? Cell(table, r, c) ?? "" : "";
            rows.TryAdd(model, names);
        }
        if (table != IntPtr.Zero) _s.Log?.Info($"RigMap: cx_rig.2da names {rows.Count} skeleton(s)");
        return _s.Rows = rows;
    }

    /// <summary>A 2DA cell as text, or null when the engine refuses it: a blank (<c>****</c>) cell, or no such row or column.</summary>
    private static string? Cell(IntPtr table, int row, string column)
    {
        ExoStringRec col = default, cell = default;
        byte* name = stackalloc byte[column.Length + 1];
        for (int i = 0; i < column.Length; i++) name[i] = (byte)column[i];
        name[column.Length] = 0;
        EngineFn.ExoStringInit((IntPtr)(&col), name, uint.MaxValue);
        string? text = EngineFn.C2DAGetCExoStringEntry(table, row, (IntPtr)(&col), (IntPtr)(&cell)) != 0 ? cell.Text : null;
        EngineFn.ExoStringDtor((IntPtr)(&cell));
        EngineFn.ExoStringDtor((IntPtr)(&col));
        return text is "" ? null : text;
    }
}
