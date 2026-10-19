using System;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// NUI layout solve. kiwi's <see cref="SymbolMap.KiwiSubstitute"/> walks the whole tableau to replace one symbol, so a
/// group that adds its subtree's R constraints one at a time pays R² row visits: 144 ms of addConstraint for one
/// 267-node list. This hook keeps, per solver, an index from each symbol to the tableau rows that may hold it and runs
/// the engine's own substitute over just those, so the tableau stays bit-identical to vanilla's. Cold.
/// </summary>
internal static unsafe class NuiSolver
{
    /// <summary>Symbols at or past this id stop tracking that solver (index arrays are sized by id).</summary>
    private const ulong MaxSymbol = 1 << 22;
    private const int Slots = 8;
    /// <summary>A symbol pair's type when it is a solver-internal dummy (never enters a pivot). Dummies are not
    /// indexed: every required equality leaves one and they ride a column's whole chain, so indexing them measured
    /// costlier than the walk it saved. Only a redundant constraint or a removal substitutes one, which takes the
    /// stock walk.</summary>
    private const int DummyType = 4;

    private sealed class Index
    {
        public IntPtr Solver;
        public long Used;
        /// <summary>Stale: the tableau changed where the index could not see it. Compact: the node pool doubled since the
        /// last rebuild, most of it for rows that have since moved on.</summary>
        public bool Stale, Compact;
        public int[] Head = Filled(1024);
        public int[] Next = new int[4096];
        public ulong[] Key = new ulong[4096];
        public int Nodes, MaxSym, Live;
        /// <summary>Rows the subset walks skipped since the last rebuild, and what that rebuild cost (rows + cells read).</summary>
        public long Saved, Cost;
    }

    private static ExtenderLog? _log;
    private static Knob<bool>? _enabled, _solve, _check;
    private static KiwiSubstituteFn _substituteOrig;
    private static readonly Index[] _ix = new Index[Slots];
    private static long _clock;
    private static int[] _cand = new int[256];
    private static byte* _buf;
    private static int _bufBytes;
    private static bool _broken;
    private static long _calls, _visited, _total;

    /// <summary>The thread of the first substitution -- NUI's, which builds the main menu's windows before any script can
    /// reach NWScript's cassowary. A solver used from any other thread is left to the engine (the index is not shared).</summary>
    private static int _thread;

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _enabled = NuiKnobs.Enabled;
        _solve = NuiKnobs.Solve;
        _check = NuiKnobs.SolveCheck;
        for (int i = 0; i < Slots; i++) _ix[i] = new Index();
        if (!EngineLayout.Require(log, "NuiSolver", "kiwi::impl::SolverImpl", "kiwi::impl::Row", "kiwi::impl::SymbolPair")
            || !SymbolMap.Require(log, "NuiSolver", SymbolMap.KiwiSubstitute, SymbolMap.KiwiOptimize))
        {
            Fail("the kiwi solver symbols or layout rows did not resolve");
            return;
        }
        _substituteOrig = hooks.InstallKiwiSubstitute(&OnSubstitute);
        if (_substituteOrig == null || (OperatingSystem.IsWindows() && !KiwiOptimizeHook.Add(hooks, &BeforeOptimize, null)))
            Fail("kiwi substitute/optimize did not hook");
    }

    private static void Fail(string why)
    {
        _broken = true;
        foreach (var ix in _ix) if (ix != null) ix.Solver = IntPtr.Zero;
        _log?.Always($"NuiSolver: {why} -- NUI layouts solve the stock way");
        Notice.Show(_log, "Faster NUI layout solving is off: " + why);
    }

    [UnmanagedCallersOnly]
    private static void OnSubstitute(IntPtr self, IntPtr symbol, IntPtr row)
    {
        var sv = new kiwi__impl__SolverImpl(self);
        IntPtr begin = sv.Rows, end = sv.RowsEnd;
        if (_thread == 0) _thread = Environment.CurrentManagedThreadId;
        bool on = _enabled!.V && _solve!.V && !_broken && Environment.CurrentManagedThreadId == _thread;
        Index? ix = on ? Live(self, begin == end) : Forget(self);
        var s = new kiwi__impl__SymbolPair(symbol);
        ulong sym = s.Id;
        int stride = EngineLayout.KiwiPairStride;
        int count = (int)((end - begin) / stride);
        if (ix != null && (sym >= MaxSymbol || (ix.Stale && ix.Saved < ix.Cost))) ix.Solver = IntPtr.Zero;
        if (ix == null || ix.Solver == IntPtr.Zero || s.Type == DummyType)
        {
            if (ix != null && ix.Solver != IntPtr.Zero && HasIndexed(row)) ix.Stale = true;
            _substituteOrig(self, symbol, row);
            _calls++;
            _visited += count;
            _total += count;
            return;
        }
        if (ix.Stale || ix.Compact) Rebuild(ix, begin, end);
        if (ix.Solver == IntPtr.Zero)
        {
            _substituteOrig(self, symbol, row);
            return;
        }

        ulong art = sv.Artificial != IntPtr.Zero ? sv.IdTick - 1 : 0;
        int k = Collect(ix, sym, begin, count, art);
        bool check = _check!.V;
        if (check) Covered(sym, begin, count, k);

        if (!check && sv.Infeasible == sv.InfeasibleEnd)
        {
            // m_infeasible_rows empty: no unvisited row is negative, so the pushes match vanilla's (remarks).
            if (_bufBytes < k * stride) Grow(k * stride);
            for (int i = 0; i < k; i++) Buffer.MemoryCopy((void*)new kiwi__impl__SymbolPair(begin)[_cand[i]].Ptr, (void*)new kiwi__impl__SymbolPair((IntPtr)_buf)[i].Ptr, stride, stride);
            sv.Rows = (IntPtr)_buf;
            sv.RowsEnd = new kiwi__impl__SymbolPair((IntPtr)_buf)[k].Ptr;
            _substituteOrig(self, symbol, row);
            sv.Rows = begin;
            sv.RowsEnd = end;
            _visited += k;
            ix.Saved += count - k;
        }
        else
        {
            _substituteOrig(self, symbol, row);
            _visited += count;
        }
        _calls++;
        _total += count;

        if (sym < (ulong)ix.Head.Length) ix.Head[(int)sym] = -1;
        var r = new kiwi__impl__Row(row);
        var cells = new kiwi__impl__SymbolPair(r.Cells);
        int n = (int)((r.CellsEnd - r.Cells) / stride);
        for (int c = 0; c < n; c++)
        {
            var cell = cells[c];
            if (cell.Type == DummyType) continue;
            for (int i = 0; i < k; i++) Add(ix, cell.Id, new kiwi__impl__SymbolPair(begin)[_cand[i]].Id);
            Add(ix, cell.Id, sym);
        }
        if (ix.Nodes > 1 << 16 && ix.Nodes > 2 * ix.Live) ix.Compact = true;
    }

    /// <summary>Windows only: <c>optimize</c> pivots through its own inlined substitute, which this class never sees. It
    /// pivots only when the objective holds a negative coefficient, so a solver whose objective does is rebuilt at its
    /// next substitute. Called by <see cref="KiwiOptimizeHook"/>'s C wrapper, never around the original.</summary>
    [UnmanagedCallersOnly]
    private static void BeforeOptimize(IntPtr self, IntPtr objective)
    {
        Index? ix = Find(self);
        if (ix != null && MayPivot(objective)) ix.Stale = true;
    }

    /// <summary>Whether a row holds any symbol the index tracks. A redundant constraint's row is all dummies, so the
    /// rows its dummy subject is substituted into gain only dummies and the index stays whole.</summary>
    private static bool HasIndexed(IntPtr row)
    {
        var r = new kiwi__impl__Row(row);
        var cells = new kiwi__impl__SymbolPair(r.Cells);
        int n = (int)((r.CellsEnd - r.Cells) / EngineLayout.KiwiPairStride);
        for (int c = 0; c < n; c++) if (cells[c].Type != DummyType) return true;
        return false;
    }

    private static bool MayPivot(IntPtr objective)
    {
        var r = new kiwi__impl__Row(objective);
        var cells = new kiwi__impl__SymbolPair(r.Cells);
        int n = (int)((r.CellsEnd - r.Cells) / EngineLayout.KiwiPairStride);
        for (int c = 0; c < n; c++)
        {
            var cell = cells[c];
            if (cell.Type != DummyType && BitConverter.Int64BitsToDouble((long)cell.Value) < 0.0) return true;
        }
        return false;
    }

    /// <summary>The index of this solver, claimed afresh when its tableau is empty (a new or reset solver).</summary>
    private static Index? Live(IntPtr solver, bool empty)
    {
        Index? ix = Find(solver);
        if (!empty) return ix;
        ulong tick = new kiwi__impl__SolverImpl(solver).IdTick;
        if (tick == 0 || tick >= MaxSymbol)
        {
            Fail($"an empty kiwi tableau reads symbol counter {tick} (layout rows wrong for this build?)");
            return null;
        }
        if (ix == null)
        {
            ix = _ix[0];
            foreach (var s in _ix) if (s.Used < ix.Used) ix = s;
        }
        Reset(ix);
        ix.Solver = solver;
        ix.Used = ++_clock;
        return ix;
    }

    private static Index? Find(IntPtr solver)
    {
        foreach (var ix in _ix)
            if (ix != null && ix.Solver == solver) { ix.Used = ++_clock; return ix; }
        return null;
    }

    private static Index? Forget(IntPtr solver)
    {
        foreach (var ix in _ix)
            if (ix != null && ix.Solver == solver) ix.Solver = IntPtr.Zero;
        return null;
    }

    private static void Reset(Index ix)
    {
        Array.Fill(ix.Head, -1, 0, Math.Min(ix.MaxSym + 1, ix.Head.Length));
        ix.Nodes = ix.MaxSym = ix.Live = 0;
        ix.Stale = ix.Compact = false;
        ix.Saved = ix.Cost = 0;
    }

    /// <summary>Re-derives the index from the tableau itself: every non-dummy cell of every row.</summary>
    private static void Rebuild(Index ix, IntPtr begin, IntPtr end)
    {
        Reset(ix);
        int stride = EngineLayout.KiwiPairStride;
        int count = (int)((end - begin) / stride);
        long cost = count;
        var entries = new kiwi__impl__SymbolPair(begin);
        for (int i = 0; i < count; i++)
        {
            var e = entries[i];
            var r = new kiwi__impl__Row(e.Value);
            var cells = new kiwi__impl__SymbolPair(r.Cells);
            int n = (int)((r.CellsEnd - r.Cells) / stride);
            cost += n;
            for (int c = 0; c < n; c++)
                if (cells[c].Type != DummyType) Add(ix, cells[c].Id, e.Id);
        }
        ix.Live = ix.Nodes;
        ix.Cost = cost;
    }

    private static void Add(Index ix, ulong sym, ulong key)
    {
        if (sym >= MaxSymbol) { ix.Solver = IntPtr.Zero; return; }
        int s = (int)sym;
        if (s >= ix.Head.Length)
        {
            int[] h = Filled(Math.Max(ix.Head.Length * 2, s + 1));
            Array.Copy(ix.Head, h, ix.Head.Length);
            ix.Head = h;
        }
        if (ix.Nodes == ix.Next.Length)
        {
            Array.Resize(ref ix.Next, ix.Nodes * 2);
            Array.Resize(ref ix.Key, ix.Nodes * 2);
        }
        int node = ix.Nodes++;
        ix.Key[node] = key;
        ix.Next[node] = ix.Head[s];
        ix.Head[s] = node;
        if (s > ix.MaxSym) ix.MaxSym = s;
    }

    /// <summary>Fills <see cref="_cand"/> with the tableau positions, ascending and distinct, of the rows that hold
    /// <paramref name="sym"/>: the indexed ones, plus the artificial row when <paramref name="art"/> names it.</summary>
    private static int Collect(Index ix, ulong sym, IntPtr begin, int count, ulong art)
    {
        int k = 0;
        for (int node = sym < (ulong)ix.Head.Length ? ix.Head[(int)sym] : -1; node >= 0; node = ix.Next[node])
            k = Consider(ix.Key[node], sym, begin, count, k);
        if (art != 0) k = Consider(art, sym, begin, count, k);
        if (k < 2) return k;
        Array.Sort(_cand, 0, k);
        int u = 1;
        for (int i = 1; i < k; i++) if (_cand[i] != _cand[u - 1]) _cand[u++] = _cand[i];
        return u;
    }

    private static int Consider(ulong key, ulong sym, IntPtr begin, int count, int k)
    {
        int at = Search(begin, count, key);
        if (at < 0) return k;
        var r = new kiwi__impl__Row(new kiwi__impl__SymbolPair(begin)[at].Value);
        if (Search(r.Cells, (int)((r.CellsEnd - r.Cells) / EngineLayout.KiwiPairStride), sym) < 0) return k;
        if (k == _cand.Length) Array.Resize(ref _cand, k * 2);
        _cand[k] = at;
        return k + 1;
    }

    /// <summary>Position of the pair with symbol id <paramref name="id"/> in a vector sorted by id, or -1.</summary>
    private static int Search(IntPtr begin, int count, ulong id)
    {
        var pairs = new kiwi__impl__SymbolPair(begin);
        int lo = 0, hi = count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (pairs[mid].Id < id) lo = mid + 1; else hi = mid;
        }
        return lo < count && pairs[lo].Id == id ? lo : -1;
    }

    /// <summary><see cref="NuiKnobs.SolveCheck"/>: every row that holds the symbol must be a candidate. On a miss the
    /// fast solve stands down; the substitute runs the stock way either way.</summary>
    private static bool Covered(ulong sym, IntPtr begin, int count, int k)
    {
        var entries = new kiwi__impl__SymbolPair(begin);
        for (int i = 0; i < count; i++)
        {
            var r = new kiwi__impl__Row(entries[i].Value);
            if (Search(r.Cells, (int)((r.CellsEnd - r.Cells) / EngineLayout.KiwiPairStride), sym) < 0) continue;
            if (Array.BinarySearch(_cand, 0, k, i) >= 0) continue;
            Fail($"the row index missed row {entries[i].Id} holding symbol {sym}");
            return false;
        }
        return true;
    }

    private static void Grow(int bytes)
    {
        _bufBytes = Math.Max(bytes, _bufBytes * 2);
        _buf = (byte*)NativeMemory.Realloc(_buf, (nuint)_bufBytes);
    }

    private static int[] Filled(int n)
    {
        var a = new int[n];
        Array.Fill(a, -1);
        return a;
    }

    /// <summary>Bare <c>/nui</c>: how many tableau rows the solves since the last ask visited, of how many.</summary>
    internal static string Status()
    {
        string s = _broken || _substituteOrig == null
            ? "Fast layout solve is off (see cx.log)."
            : _total == 0
                ? "No layout solved since the last /nui."
                : $"Layout solve: {_calls:N0} substitutions visited {_visited:N0} of {_total:N0} tableau rows ({100.0 * _visited / _total:F1}%).";
        _calls = _visited = _total = 0;
        return s;
    }

    /// <summary>Off-device: the index over a hand-built tableau finds exactly the rows holding a symbol, drops a row the
    /// tableau lost, and after a substitution's bookkeeping finds the rows that gained its cells. The engine
    /// substitute itself is not called here -- live-unverified.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        int stride = EngineLayout.KiwiPairStride;
        if (stride == 0) { check.Skip("no kiwi layout rows for this build"); return; }
        var ix = new Index();
        using var scratch = new NuiScratch();
        var rowBlocks = new IntPtr[4];
        IntPtr rows = scratch.Block(4096), cellArea = scratch.Block(1024);
        for (int b = 0; b < rowBlocks.Length; b++) rowBlocks[b] = scratch.Block(64);
        int cellAt = 0;
        IntPtr MakeRow(int slot, params ulong[] ids)
        {
            IntPtr row = rowBlocks[slot];
            IntPtr c0 = new kiwi__impl__SymbolPair(cellArea)[cellAt].Ptr;
            for (int i = 0; i < ids.Length; i++) new kiwi__impl__SymbolPair(c0)[i].Id = ids[i];
            cellAt += ids.Length;
            var r = new kiwi__impl__Row(row);
            r.Cells = c0;
            r.CellsEnd = new kiwi__impl__SymbolPair(c0)[ids.Length].Ptr;
            return row;
        }
        void Entry(int i, ulong key, IntPtr row)
        {
            var e = new kiwi__impl__SymbolPair(rows)[i];
            e.Id = key;
            e.Value = row;
        }
        Entry(0, 5, MakeRow(0, 1, 3));
        Entry(1, 7, MakeRow(1, 3, 4));
        Entry(2, 9, MakeRow(2, 2));
        Rebuild(ix, rows, new kiwi__impl__SymbolPair(rows)[3].Ptr);
        check.Expect(Collect(ix, 3, rows, 3, 0) == 2 && _cand[0] == 0 && _cand[1] == 1, "symbol 3 is in the rows keyed 5 and 7");
        check.Expect(Collect(ix, 2, rows, 3, 0) == 1 && _cand[0] == 2, "symbol 2 only in row 9");
        check.Expect(Collect(ix, 8, rows, 3, 0) == 0, "symbol 8 is nowhere");
        check.Expect(Collect(ix, 2, rows, 2, 0) == 0, "a row the tableau lost is not a candidate");

        // substitute(3, {4, 6}): rows 5 and 7 trade 3 for 4 and 6; the row joins under key 3.
        ix.Head[3] = -1;
        foreach (ulong key in new ulong[] { 5, 7 }) { Add(ix, 4, key); Add(ix, 6, key); }
        Add(ix, 4, 3);
        Add(ix, 6, 3);
        cellAt = 0;
        Entry(0, 3, MakeRow(3, 4, 6));
        Entry(1, 5, MakeRow(0, 1, 4, 6));
        Entry(2, 7, MakeRow(1, 4, 6));
        Entry(3, 9, MakeRow(2, 2));
        check.Expect(Collect(ix, 6, rows, 4, 0) == 3 && _cand[0] == 0 && _cand[2] == 2, "symbol 6 is now in rows 3, 5, 7");
        check.Expect(Collect(ix, 3, rows, 4, 0) == 0, "symbol 3 left every row");
        check.Expect(Collect(ix, 2, rows, 4, 9) == 1 && _cand[0] == 3, "the artificial key is checked directly, once");
        check.Expect(Covered(6, rows, 4, Collect(ix, 6, rows, 4, 0)) && !_broken, "the full walk agrees");
        log.Info(check.Ok ? "NuiSolver.SelfCheck: passed" : "NuiSolver.SelfCheck: FAILED");
    }
}
