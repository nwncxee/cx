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
