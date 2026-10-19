using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// Says why a NUI layout could not be solved, where vanilla says only "The constraint can not be satisfied." kiwi
/// gives up at two points, each with the row in hand as its proof: <see cref="SymbolMap.KiwiCreateRow"/> returning
/// dummies with a nonzero constant is a contradiction among equalities, an artificial objective still above zero
/// after optimize needs an inequality. Markers lead through m_cns to rules, elements and the window's JSON ids.
/// </summary>
internal static unsafe class NuiLayoutErrors
{
    /// <summary>kiwi's nearZero.</summary>
    private const double NearZero = 1e-8;
    private const int DummyType = 4;
    /// <summary>kiwi::strength::required, create(1000, 1000, 1000).</summary>
    private const double Required = 1001001000.0;
    /// <summary>What UnsatisfiableConstraint::what() says; HandleError's message carries it for a solver failure.</summary>
    private const string Unsatisfiable = "The constraint can not be satisfied.";
    private const int MaxElements = 100_000, MaxDepth = 64, MaxShown = 40;
    /// <summary>Field slots past an element's twelve variables: a group's own width and height edit variables.</summary>
    private const int GivenWidth = 12, GivenHeight = 13;

    private static readonly string[] FieldNames =
    [
        "x", "y", "width", "height",
        "padding.left", "padding.top", "padding.right", "padding.bottom",
        "margin.left", "margin.top", "margin.right", "margin.bottom",
        "width", "height",
    ];

    private sealed class Elem
    {
        public IntPtr Ptr;
        public int Parent, Index;
        public string Kind = "";
    }

    /// <summary>A rule's term: an element's field, the UI scale (<see cref="Elem"/> -1), or an unknown variable (-2, <c>Field</c> its number).</summary>
    private readonly record struct Term(int Elem, int Field, double Coef);

    private sealed class Rule
    {
        public readonly List<Term> Terms = [];
        public double Constant, Strength;
        public int Op;
        public bool Added;
    }

    private sealed class Capture
    {
        public readonly List<Elem> Elems = [];
        public readonly List<Rule> Rules = [];
    }

    private static ExtenderLog? _log;
    private static Knob<bool>? _enabled, _explain;
    private static IntPtr _createRowOrig;
    private static NuiHandleErrorFn _handleErrorOrig;
    private static IntPtr _groupVptr, _singleVptr, _spanVptr;
    private static bool _broken;
    /// <summary>The constraint createRow last saw and its solver: the rule being added when kiwi gives up.</summary>
    private static IntPtr _adding, _addingSolver;
    private static Capture? _pending;
    private static long _explained, _unexplained;

    private static bool On => !_broken && _enabled!.V && _explain!.V;

    internal static void Init(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _enabled = NuiKnobs.Enabled;
        _explain = NuiKnobs.Explain;
        if (!EngineLayout.Require(log, "NuiLayoutErrors", "kiwi::impl::SolverImpl", "kiwi::impl::Row", "kiwi::impl::SymbolPair",
                "kiwi::impl::CnPair", "kiwi::ConstraintData", "kiwi::Term", "Nui::Layout::Layoutable", "Nui::Layout::Span",
                "Nui::Layout::LayoutGroup", "Nui::JSON::JsonWindow", "Nui::JSON::IdNode")
            || !SymbolMap.Require(log, "NuiLayoutErrors", SymbolMap.KiwiCreateRow, SymbolMap.KiwiOptimize, SymbolMap.NuiHandleError,
                SymbolMap.NuiLayoutGroupVtbl, SymbolMap.NuiSingleGroupVtbl, SymbolMap.NuiSpanVtbl))
        {
            Fail("the kiwi or NUI symbols or layout rows did not resolve");
            return;
        }
        _groupVptr = EngineAbi.Vtbl(Native.Sym(SymbolMap.NuiLayoutGroupVtbl));
        _singleVptr = EngineAbi.Vtbl(Native.Sym(SymbolMap.NuiSingleGroupVtbl));
        _spanVptr = EngineAbi.Vtbl(Native.Sym(SymbolMap.NuiSpanVtbl));
        _createRowOrig = OperatingSystem.IsWindows()
            ? hooks.Install(SymbolMap.KiwiCreateRow, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&OnCreateRowWin)
            : hooks.Install(SymbolMap.KiwiCreateRow, (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)&OnCreateRowSysV);
        _handleErrorOrig = hooks.InstallNuiHandleError(&OnHandleError);
        bool optimize = KiwiOptimizeHook.Add(hooks, null, &AfterOptimize);
        if (_createRowOrig == IntPtr.Zero || _handleErrorOrig == null || !optimize)
            Fail("createRow, optimize or HandleError did not hook");
    }

    private static void Fail(string why)
    {
        _broken = true;
        _log?.Always($"NuiLayoutErrors: {why} -- a broken NUI layout shows only the stock message");
        Notice.Show(_log, "Explaining broken NUI layouts is off: " + why);
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnCreateRowSysV(IntPtr ret, IntPtr self, IntPtr constraint, IntPtr tag)
    {
        IntPtr r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)_createRowOrig)(ret, self, constraint, tag);
        Created(self, constraint, ret);
        return r;
    }

    [UnmanagedCallersOnly]
    private static IntPtr OnCreateRowWin(IntPtr self, IntPtr ret, IntPtr constraint, IntPtr tag)
    {
        IntPtr r = ((delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)_createRowOrig)(self, ret, constraint, tag);
        Created(self, constraint, ret);
        return r;
    }

    /// <summary><c>addConstraint</c> throws straight after createRow when the new row is only dummies with a nonzero
    /// constant: every variable in the rule is already fixed by required equalities, to other values.</summary>
    private static void Created(IntPtr solver, IntPtr constraint, IntPtr ret)
    {
        _addingSolver = solver;
        _adding = *(IntPtr*)constraint;
        if (!On) return;
        var row = new kiwi__impl__Row(*(IntPtr*)ret);
        if (Math.Abs(row.Constant) < NearZero || !AllDummies(row)) return;
        Catch(solver, row.Ptr);
    }

    /// <summary><c>addWithArtificialVariable</c> fails, and <c>addConstraint</c> throws, when optimizing its artificial
    /// objective leaves that objective above zero: no point satisfies the required rules with the new one.</summary>
    [UnmanagedCallersOnly]
    private static void AfterOptimize(IntPtr solver, IntPtr objective)
    {
        if (!On || objective == IntPtr.Zero || objective != new kiwi__impl__SolverImpl(solver).Artificial) return;
        if (Math.Abs(new kiwi__impl__Row(objective).Constant) < NearZero) return;
        Catch(solver, objective);
    }

    private static void Catch(IntPtr solver, IntPtr row)
    {
        try
        {
            IntPtr group = FindGroup(solver);
            if (group == IntPtr.Zero) return;
            var cap = new Capture();
            var vars = Walk(group, cap);
            Prove(solver, row, _addingSolver == solver ? _adding : IntPtr.Zero, vars, cap);
            _pending = cap;
        }
        catch (Exception e)
        {
            Fail($"reading the failed layout threw {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Hands <see cref="SymbolMap.NuiHandleError"/> the explanation as its message (the same text goes to
    /// cx.log). The capture it reads was taken while the failing group was alive: an NuiCreate failure frees the
    /// half-built tree before this runs, so a captured element pointer is only a key into the id map.</summary>
    [UnmanagedCallersOnly]
    private static void OnHandleError(IntPtr self, IntPtr tag, IntPtr message)
    {
        Capture? cap = _pending;
        _pending = null;
        string msg = Marshal.PtrToStringUTF8(message) ?? "";
        if (!On || !msg.Contains(Unsatisfiable))
        {
            _handleErrorOrig(self, tag, message);
            return;
        }
        if (cap == null)
        {
            _unexplained++;
            _log?.Always($"NuiLayoutErrors: \"{msg}\" but no failing rule was caught (not an addConstraint failure?)");
            _handleErrorOrig(self, tag, message);
            return;
        }
        string text;
        try
        {
            text = Explain(msg, cap, Ids(self));
        }
        catch (Exception e)
        {
            Fail($"explaining the failed layout threw {e.GetType().Name}: {e.Message}");
            _handleErrorOrig(self, tag, message);
            return;
        }
        _explained++;
        _log?.Always("NuiLayoutErrors: a server window could not lay out --\n" + text);
        Notice.Show(_log, $"A server window could not lay out: {cap.Rules.Count} of its rules contradict each other. The window lists them; so does cx.log.");
        byte[] bytes = Encoding.UTF8.GetBytes(text + "\0");
        fixed (byte* p = bytes) _handleErrorOrig(self, tag, (IntPtr)p);
    }

    /// <summary>The LayoutGroup embedding this solver, or zero for any other kiwi user (NWScript's cassowary). The group
    /// starts before the solver, where nothing says memory is readable, so its vptr is read by a probe.</summary>
    private static IntPtr FindGroup(IntPtr solver)
    {
        IntPtr g = Nui__Layout__LayoutGroup.FromSolver(new kiwi__impl__SolverImpl(solver)).Ptr;
        return TryRead(g, out IntPtr vptr) && (vptr == _groupVptr || vptr == _singleVptr) ? g : IntPtr.Zero;
    }

    /// <summary>The group's subtree as far as its solver reaches -- through spans, not into nested groups, whose children
    /// another solver lays out -- and every variable in it keyed by its VariableData*.</summary>
    private static Dictionary<IntPtr, (int Elem, int Field)> Walk(IntPtr group, Capture cap)
    {
        var vars = new Dictionary<IntPtr, (int, int)>();
        Visit(group, -1, 0, 0, cap, vars);
        var g = new Nui__Layout__LayoutGroup(group);
        vars.TryAdd(g.EditWidth, (0, GivenWidth));
        vars.TryAdd(g.EditHeight, (0, GivenHeight));
        if (g.ScaleConstraints != IntPtr.Zero && *(IntPtr*)g.ScaleConstraints is var scale and not 0)
        {
            var cd = new kiwi__ConstraintData(scale);
            if (cd.TermsEnd != cd.Terms.Ptr) vars.TryAdd(cd.Terms.Variable, (-1, 0));
        }
        return vars;
    }

    private static void Visit(IntPtr el, int parent, int index, int depth, Capture cap, Dictionary<IntPtr, (int, int)> vars)
    {
        if (el == IntPtr.Zero || cap.Elems.Count >= MaxElements) return;
        IntPtr vptr = *(IntPtr*)el;
        bool group = vptr == _groupVptr || vptr == _singleVptr;
        int me = cap.Elems.Count;
        cap.Elems.Add(new Elem
        {
            Ptr = el, Parent = parent, Index = index,
            Kind = group ? "group" : vptr == _spanVptr ? (new Nui__Layout__Span(el).Direction == 1 ? "col" : "row") : "widget",
        });
        var view = new Nui__Layout__Layoutable(el);
        Span<IntPtr> v = view.Vars;
        for (int f = 0; f < v.Length; f++) vars.TryAdd(v[f], (me, f));
        if ((group && parent >= 0) || depth >= MaxDepth) return;
        int i = 0;
        for (int c = 0, n = view.ChildCount; c < n; c++)
            Visit(view.Child(c), me, i++, depth + 1, cap, vars);
    }

    /// <summary>The rules the failing row was built from: the one being added, then every rule whose marker or other
    /// symbol is a cell of the row. A cell with no rule in m_cns is the new rule's own symbol or the artificial variable.</summary>
    private static void Prove(IntPtr solver, IntPtr row, IntPtr adding, Dictionary<IntPtr, (int Elem, int Field)> vars, Capture cap)
    {
        var sv = new kiwi__impl__SolverImpl(solver);
        var cns = sv.Cns;
        int n = (int)((sv.CnsEnd - cns.Ptr) / EngineLayout.KiwiCnStride);
        var seen = new HashSet<IntPtr>();
        var unknown = new Dictionary<IntPtr, int>();
        if (adding != IntPtr.Zero && seen.Add(adding)) cap.Rules.Add(Decode(adding, vars, unknown, added: true));
        var r = new kiwi__impl__Row(row);
        var cells = new kiwi__impl__SymbolPair(r.Cells);
        int m = (int)((r.CellsEnd - r.Cells) / EngineLayout.KiwiPairStride);
        for (int c = 0; c < m; c++)
        {
            ulong id = cells[c].Id;
            for (int k = 0; k < n; k++)
            {
                var e = cns[k];
                if (e.Marker != id && e.Other != id) continue;
                if (seen.Add(e.Data.Ptr)) cap.Rules.Add(Decode(e.Data.Ptr, vars, unknown, added: false));
                break;
            }
        }
    }

    private static Rule Decode(IntPtr data, Dictionary<IntPtr, (int Elem, int Field)> vars, Dictionary<IntPtr, int> unknown, bool added)
    {
        var cd = new kiwi__ConstraintData(data);
        var rule = new Rule { Constant = cd.Constant, Strength = cd.Strength, Op = cd.Op, Added = added };
        var t = cd.Terms;
        int n = (int)((cd.TermsEnd - t.Ptr) / EngineLayout.KiwiTermStride);
        for (int i = 0; i < n; i++)
        {
            IntPtr v = t[i].Variable;
            if (vars.TryGetValue(v, out var at)) rule.Terms.Add(new Term(at.Elem, at.Field, t[i].Coefficient));
            else
            {
                if (!unknown.TryGetValue(v, out int u)) unknown[v] = u = unknown.Count + 1;
                rule.Terms.Add(new Term(-2, u, t[i].Coefficient));
            }
        }
        return rule;
    }

    private static bool AllDummies(kiwi__impl__Row row)
    {
        var cells = new kiwi__impl__SymbolPair(row.Cells);
        int n = (int)((row.CellsEnd - row.Cells) / EngineLayout.KiwiPairStride);
        for (int c = 0; c < n; c++) if (cells[c].Type != DummyType) return false;
        return true;
    }

    /// <summary>The window's id map inverted: element pointer to its JSON id.</summary>
    private static Dictionary<IntPtr, string> Ids(IntPtr window)
    {
        var ids = new Dictionary<IntPtr, string>();
        IntPtr head = new Nui__JSON__JsonWindow(window).IdsHead;
        bool win = OperatingSystem.IsWindows();
        IntPtr node = win ? (head == IntPtr.Zero ? IntPtr.Zero : *(IntPtr*)head) : head, end = win ? head : IntPtr.Zero;
        for (int i = 0; node != IntPtr.Zero && node != end && i < MaxElements; i++, node = *(IntPtr*)node)
        {
            var (buf, len, _) = Native.StdString((byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref new Nui__JSON__IdNode(node).Key));
            ids.TryAdd(new Nui__JSON__IdNode(node).Element, Encoding.UTF8.GetString((byte*)buf, (int)len));
        }
        return ids;
    }

    private static string Explain(string vanilla, Capture cap, Dictionary<IntPtr, string> ids)
    {
        var names = new string?[cap.Elems.Count];
        string Name(int e)
        {
            if (names[e] != null) return names[e]!;
            var el = cap.Elems[e];
            return names[e] = ids.TryGetValue(el.Ptr, out string? id) ? $"\"{id}\""
                : el.Parent < 0 ? $"({el.Kind})"
                : $"{Name(el.Parent)} > {el.Kind}[{el.Index}]";
        }
        var zero = ZeroPins(cap);
        var sb = new StringBuilder(vanilla).Append("\n\nThese rules cannot all hold at once:\n");
        int shown = 0, hidden = 0, pins = 0;
        foreach (var rule in cap.Rules)
        {
            string eq = Render(rule, Name, zero);
            if (eq.Length == 0) { pins++; continue; }
            if (shown == MaxShown) { hidden++; continue; }
            sb.Append("  ").Append(++shown).Append(". ").Append(eq);
            string why = Why(rule);
            if (why.Length > 0) sb.Append("   (").Append(why).Append(')');
            if (rule.Added) sb.Append("   <- the rule being added when the layout gave up");
            sb.Append('\n');
        }
        if (hidden > 0) sb.Append("  ... and ").Append(hidden).Append(" more.\n");
        if (pins > 0) sb.Append("  (and ").Append(pins).Append(" padding/margin = 0 rules, left out of the lines above)\n");
        sb.Append("\nChange or remove one of the rules that names a JSON key, or make them agree. Names are the elements' ")
          .Append("JSON ids; an element without one is given by its path from the nearest one (children counted from 0). ")
          .Append("UI scale is the player's UI scale setting, which every \"width\" and \"height\" is multiplied by.");
        return sb.ToString();
    }

    /// <summary>Padding and margin fields a rule of the proof pins to 0: they add nothing, so they are left out.</summary>
    private static HashSet<(int, int)> ZeroPins(Capture cap)
    {
        var zero = new HashSet<(int, int)>();
        foreach (var rule in cap.Rules)
        {
            if (rule.Op != 2 || Math.Abs(rule.Constant) >= NearZero) continue;
            var live = rule.Terms.FindAll(t => Math.Abs(t.Coef) >= NearZero);
            if (live.Count == 1 && live[0] is { Elem: >= 0, Field: >= 4 and < 12 } pin) zero.Add((pin.Elem, pin.Field));
        }
        return zero;
    }

    /// <summary>The rule as <c>left OP right</c>, each side a sum with positive coefficients, without the fields in
    /// <paramref name="zero"/>. Empty when nothing else is left.</summary>
    private static string Render(Rule rule, Func<int, string> name, HashSet<(int, int)> zero)
    {
        var left = new List<string>();
        var right = new List<string>();
        foreach (var t in rule.Terms)
        {
            if (Math.Abs(t.Coef) < NearZero || zero.Contains((t.Elem, t.Field))) continue;
            string v = t.Elem == -1 ? "UI scale" : t.Elem == -2 ? $"engine variable #{t.Field}" : $"{name(t.Elem)}.{FieldNames[t.Field]}";
            double a = Math.Abs(t.Coef);
            string s = Math.Abs(a - 1.0) < NearZero ? v : $"{Num(a)} * {v}";
            (t.Coef > 0 ? left : right).Add(s);
        }
        if (left.Count == 0 && right.Count == 0) return "";
        if (rule.Constant > NearZero) left.Add(Num(rule.Constant));
        else if (rule.Constant < -NearZero) right.Add(Num(-rule.Constant));
        string op = rule.Op switch { 0 => "<=", 1 => ">=", _ => "==" };
        return $"{(left.Count == 0 ? "0" : string.Join(" + ", left))} {op} {(right.Count == 0 ? "0" : string.Join(" + ", right))}";
    }

    /// <summary>Where a rule comes from, when its shape says so for certain: <c>LayoutableJ::Construct</c> builds
    /// <c>"width": W</c> as <c>W * scale == width</c>, <c>"height"</c> likewise, and <c>"aspect": A</c> as
    /// <c>width / A == height</c>, all at required unless a weight is given.</summary>
    private static string Why(Rule rule)
    {
        string strength = Math.Abs(rule.Strength - Required) < 1.0 ? "" : $"strength {Num(rule.Strength)}";
        var live = rule.Terms.FindAll(t => Math.Abs(t.Coef) >= NearZero);
        bool flat = Math.Abs(rule.Constant) < NearZero;
        if (live.Count == 1 && live[0] is { Elem: >= 0, Field: < 4 } && rule.Op == 1 && flat)
            return Join("NUI's own: an element's position and size are never negative", strength);
        if (live.Count == 1 && live[0] is { Elem: 0, Field: GivenWidth or GivenHeight })
            return Join("the space the group is given", strength);
        if (live.Count == 1 && live[0].Elem == -1)
            return Join("the UI scale", strength);
        if (live.Count == 2 && rule.Op == 2 && flat)
        {
            int si = live.FindIndex(t => t.Elem == -1);
            if (si >= 0 && live[1 - si] is { Elem: >= 0, Field: 2 or 3 } size && Math.Sign(size.Coef) != Math.Sign(live[si].Coef))
                return Join($"\"{(size.Field == 2 ? "width" : "height")}\": {Num(Math.Abs(live[si].Coef / size.Coef))} in the JSON", strength);
            if (live[0].Elem >= 0 && live[0].Elem == live[1].Elem && live.Exists(t => t.Field == 2) && live.Exists(t => t.Field == 3)
                && Math.Sign(live[0].Coef) != Math.Sign(live[1].Coef))
            {
                var w = live.Find(t => t.Field == 2);
                var h = live.Find(t => t.Field == 3);
                return Join($"\"aspect\": {Num(Math.Abs(h.Coef / w.Coef))} in the JSON", strength);
            }
        }
        return strength;
    }

    private static string Join(string a, string b) => b.Length == 0 ? a : $"{a}; {b}";

    private static string Num(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Reads the pointer at <paramref name="p"/>, or false when it is not readable. Linux: process_vm_readv on this
    /// process, which fails with EFAULT rather than faulting (a PROT_NONE arena reserve is mapped but unreadable). Windows:
    /// VirtualQuery; an aligned pointer never straddles a page.</summary>
    private static bool TryRead(IntPtr p, out IntPtr value)
    {
        value = IntPtr.Zero;
        if (OperatingSystem.IsWindows())
        {
            MemoryInfo m;
            if (VirtualQuery(p, &m, (nuint)sizeof(MemoryInfo)) == 0 || m.State != MemCommit || (m.Protect & (PageNoAccess | PageGuard)) != 0)
                return false;
            value = *(IntPtr*)p;
            return true;
        }
        IntPtr v;
        IoVec local = new() { Base = &v, Length = (nuint)sizeof(IntPtr) }, remote = new() { Base = (void*)p, Length = (nuint)sizeof(IntPtr) };
        if (process_vm_readv(Environment.ProcessId, &local, 1, &remote, 1, 0) != sizeof(IntPtr)) return false;
        value = v;
        return true;
    }

    private const uint MemCommit = 0x1000, PageNoAccess = 0x01, PageGuard = 0x100;   // not-an-offset: Win32 MEM_/PAGE_ flags

    /// <summary>Win32 MEMORY_BASIC_INFORMATION (x64).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }

    /// <summary>POSIX struct iovec.</summary>
    private struct IoVec
    {
        public void* Base;
        public nuint Length;
    }

    [DllImport("kernel32")] private static extern nuint VirtualQuery(IntPtr address, MemoryInfo* info, nuint length);
    [DllImport("libc")] private static extern nint process_vm_readv(int pid, IoVec* local, nuint localCount, IoVec* remote, nuint remoteCount, nuint flags);

    /// <summary>The proof walk and the text over a hand-built solver: an added "a".width == 32 against an accepted
    /// "a".width == 64 whose dummy marker is in the failing row, plus the rendering of a chain and a bound.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        if (EngineLayout.KiwiCnStride == 0 || EngineLayout.KiwiTermStride == 0)
        {
            check.Skip("no kiwi layout rows for this build");
            return;
        }
        using var scratch = new NuiScratch();
        IntPtr width = scratch.Block(512);
        IntPtr Data(int at, double constant)
        {
            var cd = new kiwi__ConstraintData(scratch.Block(256));
            IntPtr terms = scratch.Block(256);
            new kiwi__Term(terms).Variable = width;
            new kiwi__Term(terms).Coefficient = 1.0;
            cd.Terms = new kiwi__Term(terms);
            cd.TermsEnd = new kiwi__Term(terms)[1].Ptr;
            cd.Constant = constant;
            cd.Strength = Required;
            cd.Op = 2;
            return cd.Ptr;
        }
        IntPtr accepted = Data(0, -64), adding = Data(512, -32);
        IntPtr solver = scratch.Block(512), cns = scratch.Block(512), row = scratch.Block(256), cells = scratch.Block(1024);
        var pair = new kiwi__impl__CnPair(cns);
        pair.Data = new kiwi__ConstraintData(accepted);
        pair.Marker = 5;
        var impl = new kiwi__impl__SolverImpl(solver);
        impl.Cns = pair;
        impl.CnsEnd = pair[1].Ptr;
        var c = new kiwi__impl__SymbolPair(cells);
        c[0].Id = 5; c[0].Type = DummyType;
        c[1].Id = 9; c[1].Type = DummyType;
        var r = new kiwi__impl__Row(row);
        r.Cells = cells;
        r.CellsEnd = c[2].Ptr;
        r.Constant = 32;
        check.Expect(AllDummies(r), "a row of dummies is all dummies");

        var cap = new Capture();
        cap.Elems.Add(new Elem { Ptr = 1, Parent = -1, Kind = "group" });
        cap.Elems.Add(new Elem { Ptr = 2, Parent = 0, Index = 0, Kind = "row" });
        cap.Elems.Add(new Elem { Ptr = 3, Parent = 1, Index = 1, Kind = "widget" });
        var vars = new Dictionary<IntPtr, (int Elem, int Field)> { [width] = (2, 2) };
        Prove(solver, row, adding, vars, cap);
        check.Expect(cap.Rules.Count == 2 && cap.Rules[0].Added && !cap.Rules[1].Added, "the added rule, then the one its marker names");
        string text = Explain(Unsatisfiable, cap, new Dictionary<IntPtr, string> { [3] = "a" });
        check.Expect(text.Contains("1. \"a\".width == 32   <- the rule being added"), "the added rule is first and marked");
        check.Expect(text.Contains("2. \"a\".width == 64\n"), "the accepted rule it contradicts");
        text = Explain(Unsatisfiable, cap, []);
        check.Expect(text.Contains("(group) > row[0] > widget[1].width == 32"), "an element without an id is named by its path");

        var chain = new Rule { Op = 2, Constant = 4, Strength = Required };
        chain.Terms.Add(new Term(1, 1, 1));
        chain.Terms.Add(new Term(1, 3, 1));
        chain.Terms.Add(new Term(1, 11, 1));
        chain.Terms.Add(new Term(2, 9, 1));
        chain.Terms.Add(new Term(2, 1, -1));
        var chainZero = new HashSet<(int, int)> { (1, 11), (2, 9) };
        check.Expect(Render(chain, e => e == 1 ? "A" : "B", chainZero) == "A.y + A.height + 4 == B.y", "a chain drops zero margins and moves the negative side right");
        var bound = new Rule { Op = 1, Strength = Required };
        bound.Terms.Add(new Term(2, 2, 1));
        check.Expect(Render(bound, _ => "B", []) == "B.width >= 0" && Why(bound).StartsWith("NUI's own"), "a bound is NUI's own");
        var pin = new Rule { Op = 2, Strength = Required };
        pin.Terms.Add(new Term(2, 9, 1));
        var pinned = new Capture();
        pinned.Rules.Add(pin);
        var zero = ZeroPins(pinned);
        check.Expect(zero.Contains((2, 9)) && Render(pin, _ => "B", zero) == "", "a margin pinned to 0 renders to nothing");
        var margin = new Rule { Op = 2, Strength = Required };
        margin.Terms.Add(new Term(2, 1, 1));
        margin.Terms.Add(new Term(2, 9, 1));
        check.Expect(Render(margin, _ => "B", zero) == "B.y == 0" && Render(margin, _ => "B", []) == "B.y + B.margin.top == 0",
            "only a margin the proof pins to 0 is left out");
        var sized = new Rule { Op = 2, Strength = Required };
        sized.Terms.Add(new Term(-1, 0, 100));
        sized.Terms.Add(new Term(2, 2, -1));
        check.Expect(Render(sized, _ => "B", []) == "100 * UI scale == B.width" && Why(sized) == "\"width\": 100 in the JSON",
            "a width rule names its JSON key");
        var aspect = new Rule { Op = 2, Strength = Required };
        aspect.Terms.Add(new Term(2, 2, 0.5));
        aspect.Terms.Add(new Term(2, 3, -1));
        check.Expect(Why(aspect) == "\"aspect\": 2 in the JSON", "an aspect rule names its JSON key");
        IntPtr probe = 0x1234;
        check.Expect(TryRead((IntPtr)(&probe), out IntPtr read) && read == 0x1234 && !TryRead(IntPtr.Zero, out _),
            "a probe reads the stack and refuses page 0");
        if (check.Ok) log.Info("NuiLayoutErrors.SelfCheck: passed");
    }

    internal static string Status()
    {
        string s = _broken ? "Explaining broken layouts is off (see cx.log)."
            : $"Broken layouts explained: {_explained}, not explained: {_unexplained}.";
        return s;
    }
}
