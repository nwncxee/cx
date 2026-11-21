using System;
using System.Collections.Generic;
using System.Threading;

namespace CX.Framework;

/// <summary>Where a wait reads the time: the system's monotonic milliseconds, or a self-check's own count that only
/// <see cref="Step"/> moves. A stepped clock's alarms fire inside <see cref="Step"/>, on the stepping thread.</summary>
public sealed class Clock
{
    /// <summary>The clock every wait the client ships reads.</summary>
    public static readonly Clock System = new(false);

    private readonly bool _stepped;
    private long _now;
    private readonly List<Alarm> _alarms = new();

    private Clock(bool stepped) => _stepped = stepped;

    /// <summary>A clock of the caller's own, at zero until stepped.</summary>
    public static Clock Stepped() => new(true);

    /// <summary>Milliseconds on this clock.</summary>
    public long NowMs => _stepped ? Interlocked.Read(ref _now) : Environment.TickCount64;

    /// <summary>What cx.log calls this clock.</summary>
    public string Name => _stepped ? "a stepped clock" : "the system clock";

    /// <summary>Moves a stepped clock on by <paramref name="ms"/> and fires each alarm that falls due, earliest first.</summary>
    public void Step(long ms)
    {
        if (!_stepped) throw new InvalidOperationException("the system clock is not stepped");
        long to = Interlocked.Add(ref _now, ms);
        while (true)
        {
            Alarm? next = null;
            lock (_alarms)
                foreach (Alarm a in _alarms)
                    if (a.DueAt <= to && (next == null || a.DueAt < next.DueAt)) next = a;
            if (next == null) return;
            next.DueAt = long.MaxValue;
            next.Fire();
        }
    }

    /// <summary>A one-shot alarm on this clock that runs <paramref name="fire"/> when it falls due.</summary>
    public Alarm NewAlarm(Action fire)
    {
        var a = new Alarm(this, fire);
        if (_stepped) lock (_alarms) _alarms.Add(a);
        return a;
    }

    /// <summary>A stepped clock's alarm fires once, at its due time and not a millisecond before, and a second set
    /// restarts it. Oracle: the hand-written step sizes and fire counts below.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Clock c = Stepped();
        int fired = 0;
        Alarm a = c.NewAlarm(() => fired++);
        a.Set(1000);
        c.Step(999);
        check.Expect(fired == 0 && c.NowMs == 999, $"an alarm set for 1000 ms fired after 999 ({fired} times, at {c.NowMs})");
        c.Step(1);
        check.Expect(fired == 1, $"an alarm set for 1000 ms fired {fired} times at 1000, not once");
        c.Step(5000);
        check.Expect(fired == 1, "a fired alarm fired again without a set");
        a.Set(100);
        c.Step(50);
        a.Set(100);
        c.Step(99);
        check.Expect(fired == 1, "a second set did not restart the alarm");
        c.Step(1);
        check.Expect(fired == 2, $"a restarted alarm fired {fired - 1} times at its new due time, not once");
        check.Expect(System.Name == "the system clock" && c.Name == "a stepped clock", "the clocks' names in cx.log");
        log.Info(check.Ok ? "Clock.SelfCheck: passed" : "Clock.SelfCheck: FAILED");
    }

    /// <summary>A one-shot alarm: <see cref="Set"/> restarts it, so a run of sets fires once, after the last.</summary>
    public sealed class Alarm
    {
        private readonly Clock _clock;
        private readonly Action _fire;
        private readonly Timer? _timer;
        internal long DueAt = long.MaxValue;

        internal Alarm(Clock clock, Action fire)
        {
            _clock = clock;
            _fire = fire;
            if (!clock._stepped) _timer = new Timer(_ => fire(), null, Timeout.Infinite, Timeout.Infinite);
        }

        internal void Fire() => _fire();

        /// <summary>Fire <paramref name="ms"/> from now on this alarm's clock, forgetting any earlier set.</summary>
        public void Set(int ms)
        {
            if (_timer != null) _timer.Change(ms, Timeout.Infinite);
            else DueAt = _clock.NowMs + ms;
        }
    }
}
