using System;
using System.Collections.Generic;

namespace CX.Framework;

/// <summary>Where in the frame present a participant runs. The host walks the phases in this order, with the
/// window roll-call and the present itself between them (see <c>CX.Ui.UiHostService.OnSwap</c>).</summary>
public enum FramePhase
{
    /// <summary>Before the session gate is recomputed: the frame's mode latch and its edge work.</summary>
    Start,
    /// <summary>After the window roll-call, before the present.</summary>
    PrePresent,
    /// <summary>After the present: end-of-frame lifecycle ticks.</summary>
    PostPresent,
}

/// <summary>
/// The frame roll-call (ADR 0014): a service joins the frame at <c>Initialize</c> instead of the host hardcoding a
/// call, so the framework names no service. Zero-alloc per frame, because this runs inside the present where a GC
/// pause is a visible stutter: <see cref="Seal"/> flattens the lists to arrays once and nothing after it allocates.
/// Priority orders the walk (low first, ties by registration), and a registration after Seal is refused and logged.
/// </summary>
public sealed class FrameRegistry
{
    /// <summary>The live registry the frame host walks. Services register onto this one.</summary>
    public static readonly FrameRegistry Global = new();

    private const int PhaseCount = 3;
    private readonly List<(int Priority, int Seq, Action Run, string Name)>[] _pending =
        new List<(int, int, Action, string)>[PhaseCount];
    private readonly Action[][] _sealed = new Action[PhaseCount][];
    private int _seq;
    private bool _isSealed;

    public FrameRegistry()
    {
        for (int i = 0; i < PhaseCount; i++) _pending[i] = new List<(int, int, Action, string)>();
    }

    /// <summary>True once <see cref="Seal"/> has run and the walk is live.</summary>
    public bool Sealed => _isSealed;

    /// <summary>Join the frame. <paramref name="priority"/> orders the phase, low first; ties keep registration
    /// order. Returns false (and logs) if the registry is already sealed -- the frame is running by then and a
    /// late join would either race the walk or be silently ignored.</summary>
    public bool Register(FramePhase phase, int priority, Action run, string name, ExtenderLog? log = null)
    {
        if (_isSealed)
        {
            log?.Info($"FrameRegistry: '{name}' registered after Seal -- refused (the frame is already live)");
            return false;
        }
        _pending[(int)phase].Add((priority, _seq++, run, name));
        return true;
    }

    /// <summary>Flatten the registrations to the arrays <see cref="Run"/> walks. Called once, before the
    /// present hook is committed. Sorting allocates; that is init-time and deliberate.</summary>
    public void Seal()
    {
        for (int p = 0; p < PhaseCount; p++)
        {
            var list = _pending[p];
            list.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Seq.CompareTo(b.Seq));
            var arr = new Action[list.Count];
            for (int i = 0; i < arr.Length; i++) arr[i] = list[i].Run;
            _sealed[p] = arr;
        }
        _isSealed = true;
    }

    /// <summary>Run a phase. Hot: indexed walk over cached delegates, no enumerator, no closure, no allocation.
    /// A participant that throws would take the present down, so participants own their own guards -- the walk
    /// deliberately does not try/catch per call (that is a per-frame cost for an init-time bug).</summary>
    public void Run(FramePhase phase)
    {
        Action[]? a = _sealed[(int)phase];
        if (a == null) return;
        for (int i = 0; i < a.Length; i++) a[i]();
    }

    /// <summary>The registered participants of a phase, in walk order. For diagnostics and
    /// <see cref="SelfCheck"/>; allocates, so never call it from a frame.</summary>
    public IReadOnlyList<string> Names(FramePhase phase)
    {
        var list = _pending[(int)phase];
        var outp = new List<string>(list.Count);
        var sorted = new List<(int Priority, int Seq, Action Run, string Name)>(list);
        sorted.Sort((a, b) => a.Priority != b.Priority ? a.Priority.CompareTo(b.Priority) : a.Seq.CompareTo(b.Seq));
        foreach (var e in sorted) outp.Add(e.Name);
        return outp;
    }

    /// <summary>Walk order under priority and ties, the post-seal refusal, and the acceptance criterion the
    /// ticket states but nothing else can prove off-device: the sealed walk allocates nothing.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var r = new FrameRegistry();
        var order = new List<string>();
        // Registered out of order on purpose: alphabetical service discovery is exactly what priority fixes.
        r.Register(FramePhase.PostPresent, 20, () => order.Add("c"), "c");
        r.Register(FramePhase.PostPresent, 0, () => order.Add("a"), "a");
        r.Register(FramePhase.PostPresent, 10, () => order.Add("b"), "b");
        r.Register(FramePhase.PostPresent, 10, () => order.Add("b2"), "b2");   // tie -> registration order
        r.Register(FramePhase.Start, 0, () => order.Add("start"), "start");
        r.Register(FramePhase.PrePresent, 0, () => order.Add("pre"), "pre");

        check.Expect(!r.Sealed, "a fresh registry is not sealed");
        check.Expect(string.Join(",", r.Names(FramePhase.PostPresent)) == "a,b,b2,c", "priority orders the phase, ties keep registration order");

        r.Seal();
        check.Expect(r.Sealed, "Seal latches");
        r.Run(FramePhase.Start); r.Run(FramePhase.PrePresent); r.Run(FramePhase.PostPresent);
        check.Expect(string.Join(",", order) == "start,pre,a,b,b2,c", "the walk runs each phase in order");

        check.Expect(!r.Register(FramePhase.Start, 0, () => { }, "late"), "a post-Seal registration is refused");
        order.Clear();
        r.Run(FramePhase.Start);
        check.Expect(string.Join(",", order) == "start", "and the refused participant never runs");

        // An empty phase on an unsealed registry must be a no-op, not a null deref.
        var empty = new FrameRegistry();
        empty.Run(FramePhase.Start);

        // AC: "nothing allocates in the per-frame walk". Participants that themselves allocate would mask this,
        // so the measured walk uses do-nothing statics.
        var quiet = new FrameRegistry();
        for (int i = 0; i < 4; i++) quiet.Register(FramePhase.PrePresent, i, Nop, "nop");
        quiet.Seal();
        quiet.Run(FramePhase.PrePresent);   // JIT/first-call warmup outside the measurement
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) quiet.Run(FramePhase.PrePresent);
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;
        check.Expect(delta == 0, $"the sealed walk allocates nothing (100 walks allocated {delta} bytes)");

        log.Info(check.Ok ? "FrameRegistry.SelfCheck: passed" : "FrameRegistry.SelfCheck: FAILED");
    }

    private static void Nop() { }
}
