using System;
using CX.Framework;
using CX.Services.Chat.Core;

namespace CX.Services.CombatText;

/// <summary>What a captured <c>ShowFloatyText</c> turned out to be. The discriminator is the FONT resref the
/// engine passes: damage numbers ask for <c>fnt_floaty_smal</c>, every combat-text banner passes "".</summary>
public enum FloatyKind : byte
{
    /// <summary>A damage number. Scrolls up and fades.</summary>
    Damage,
    /// <summary>A combat-text banner (<c>*Sneak Attack!*</c>). Punches in, drifts, fades.</summary>
    Banner,
}

/// <summary>The animation curves, as pure functions of age. Separated from the table so they can be
/// self-checked without a frame, a GL context or a live object.</summary>
public static class FloatyAnim
{
    /// <summary>How long the spawn punch takes, ms. Short enough to read as an impact rather than a grow.</summary>
    public const float PopMs = 120f;

    /// <summary>Size a fresh element starts at, as a multiple of its resting size.</summary>
    public const float PopFrom = 1.75f;

    /// <summary>Fraction of a life spent at full opacity before the fade starts.</summary>
    public const float HoldFraction = 0.6f;

    /// <summary>Normalised age, clamped to 0..1. A non-positive life is "already over".</summary>
    public static float T(long ageMs, float lifeMs)
    {
        if (!(lifeMs > 0f)) return 1f;
        float t = ageMs / lifeMs;
        return t < 0f ? 0f : t > 1f ? 1f : t;
    }

    /// <summary>Ease-out cubic: fast off the mark, settling at the top. What makes the scroll read as a
    /// release rather than a constant crawl.</summary>
    public static float EaseOut(float t) { float u = 1f - t; return 1f - u * u * u; }

    /// <summary>Opacity: full for <see cref="HoldFraction"/> of the life, then linear to zero.</summary>
    public static float Fade(float t)
    {
        if (t <= HoldFraction) return 1f;
        float u = (1f - t) / (1f - HoldFraction);
        return u < 0f ? 0f : u > 1f ? 1f : u;
    }

    /// <summary>Spawn punch as a multiplier on the resting size: <see cref="PopFrom"/> at age 0, easing to 1
    /// by <see cref="PopMs"/>. Never below 1, so it only ever overshoots outward.</summary>
    public static float Pop(long ageMs)
    {
        if (ageMs >= PopMs || !(PopMs > 0f)) return 1f;
        float u = ageMs < 0 ? 0f : ageMs / PopMs;
        return 1f + (PopFrom - 1f) * (1f - EaseOut(u));
    }


    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        const float Eps = 1e-4f;
        static bool Near(float a, float b) => MathF.Abs(a - b) < 1e-4f;

        ok &= T(0, 1000f) == 0f && Near(T(500, 1000f), 0.5f) && T(5000, 1000f) == 1f;
        ok &= T(10, 0f) == 1f;                       // a zero life is already over, never a divide by zero
        ok &= Near(EaseOut(0f), 0f) && Near(EaseOut(1f), 1f) && EaseOut(0.5f) > 0.5f;
        ok &= Fade(0f) == 1f && Fade(HoldFraction) == 1f && Near(Fade(1f), 0f);
        ok &= Fade((1f + HoldFraction) * 0.5f) is > 0.4f and < 0.6f;
        ok &= Near(Pop(0), PopFrom) && Pop((long)PopMs) == 1f && Pop(99999) == 1f && Pop(60) > 1f;

        log.Info(ok ? "FloatyAnim.SelfCheck: passed" : "FloatyAnim.SelfCheck: FAILED");
    }
}

/// <summary>
/// Every live combat-text element, plus the fixed ring the cold hook publishes through. The hook resolves
/// the string and parses its colour runs on its own thread and banks a record; the render thread drains the
/// ring, ages every slot and hands the survivors to the draw.
/// <para>Slots are a fixed array and the ring drops on overflow -- a hook never allocates a table.</para>
/// </summary>
public sealed class FloatyTable
{
    /// <summary>Hard ceiling on live elements across every target. The per-target cap is the config's
    /// <c>max</c>; this is the array behind it.</summary>
    public const int MaxSlots = 96;

    private const int Ring = 128;

    public sealed class Slot
    {
        public bool Used;
        public uint Id;
        public FloatyKind Kind;
        public long SpawnMs;
        /// <summary>Admission order. Distinct from <see cref="SpawnMs"/> on purpose: a whole attack's worth of
        /// damage types lands inside one millisecond, and a millisecond clock cannot say which of them is the
        /// oldest -- which is exactly what both the eviction and the paint order need to know.</summary>
        public long Seq;
        public float LifeMs;
        /// <summary>Row within this target and class at spawn time. Everything on one head sits in a
        /// column, one <c>stackpx</c> step apart -- several arriving at once is the case this feature has to
        /// survive, and drawing them all on the same anchor is illegible.</summary>
        public int Stack;
        /// <summary>The parsed text. Empty runs = nothing to draw (the slot is retired next age).</summary>
        public ColorRun[] Runs = Array.Empty<ColorRun>();
        /// <summary>The engine's <c>Vector</c> colour argument, used for runes the parser left plain white --
        /// the same fallback rule the chat renderer applies to a channel tint.</summary>
        public float BaseR = 1f, BaseG = 1f, BaseB = 1f;
        /// <summary>Cached text width in the measured face's px at scale 1, invalidated by an atlas re-bake or by
        /// the peak face changing height (a lowered ceiling can hand back an already-baked face, no epoch bump).</summary>
        public float MeasuredW = -1f;
        public int MeasureEpoch = -1;
        public float MeasurePx;
    }

    private struct Pending
    {
        public uint Id;
        public FloatyKind Kind;
        public ColorRun[] Runs;
        public float R, G, B;
    }

    private readonly Slot[] _slots = new Slot[MaxSlots];
    private readonly Slot[] _live = new Slot[MaxSlots];
    private int _liveCount;

    private readonly Pending[] _ring = new Pending[Ring];
    private int _head, _tail;
    private long _seq;
    private readonly object _lock = new();

    /// <summary>Records the ring refused because it was full, and slots refused because the array was.
    /// Non-zero says a ceiling is real; reported by <c>/combattext</c>.</summary>
    public long Overflow => System.Threading.Interlocked.Read(ref _overflow);
    /// <summary>Bumped from BOTH threads -- the ring's own overflow on the message thread and the slot-array
    /// one on the render thread -- so it is interlocked, not a plain increment.</summary>
    private long _overflow;

    public FloatyTable() { for (int i = 0; i < MaxSlots; i++) _slots[i] = new Slot(); }

    /// <summary>The slots this frame should draw, compacted by the last <see cref="Advance"/>, oldest first
    /// so a newer number paints over an older one.</summary>
    public Slot[] Live => _live;
    public int LiveCount => _liveCount;

    /// <summary>COLD, on the message-handling thread: bank one captured element. Never allocates a slot and
    /// never touches the live table -- the render thread owns both.</summary>
    public void Note(uint id, FloatyKind kind, ColorRun[] runs, float r, float g, float b)
    {
        if (runs.Length == 0) return;
        var p = new Pending { Id = id, Kind = kind, Runs = runs, R = r, G = g, B = b };
        lock (_lock)
        {
            int next = (_head + 1) % Ring;
            if (next == _tail) { System.Threading.Interlocked.Increment(ref _overflow); return; }   // full: drop the newest, never grow on a hook
            _ring[_head] = p;
            _head = next;
        }
    }

    /// <summary>HOT. Drain the ring into slots, retire the expired, and compact what is left into
    /// <see cref="Live"/>. Returns the live count.</summary>
    public int Advance(long nowMs, int max, float lifeMs, float bannerMs)
    {
        // Retire BEFORE draining. A slot that dies this frame must not still be counted when the newcomer
        // arriving on the same frame picks its row, or the new number opens three rows above a head with
        // nothing under it.
        for (int i = 0; i < MaxSlots; i++)
        {
            Slot s = _slots[i];
            if (s.Used && nowMs - s.SpawnMs >= s.LifeMs) Retire(s);
        }
        Drain(nowMs, max, lifeMs, bannerMs);

        _liveCount = 0;
        for (int i = 0; i < MaxSlots; i++)
        {
            Slot s = _slots[i];
            if (!s.Used) continue;
            _live[_liveCount++] = s;
        }
        // Oldest first (by admission, not by clock): a burst paints newest on top.
        Array.Sort(_live, 0, _liveCount, SpawnOrder.Instance);
        return _liveCount;
    }

    /// <summary>Drop every slot -- the module-unload / area-transition reset. Object ids are only unique
    /// within a session, so a carried-over number would land on whatever inherits the id.</summary>
    public void Clear()
    {
        for (int i = 0; i < MaxSlots; i++) Retire(_slots[i]);
        _liveCount = 0;
        lock (_lock) { _head = _tail = 0; }
    }

    private static void Retire(Slot s)
    {
        s.Used = false;
        s.Runs = Array.Empty<ColorRun>();   // the only thing a slot holds a reference to
        s.MeasuredW = -1f;
        s.MeasureEpoch = -1;
    }

    private void Drain(long nowMs, int max, float lifeMs, float bannerMs)
    {
        while (true)
        {
            Pending p;
            lock (_lock)
            {
                if (_tail == _head) return;
                p = _ring[_tail];
                _ring[_tail] = default;
                _tail = (_tail + 1) % Ring;
            }
            Admit(p, nowMs, max, lifeMs, bannerMs);
        }
    }

    /// <summary><paramref name="max"/> is the per-target, per-class cap; <paramref name="lifeMs"/> a number's life,
    /// <paramref name="bannerMs"/> a banner's.</summary>
    private void Admit(in Pending p, long nowMs, int max, float lifeMs, float bannerMs)
    {
        bool number = p.Kind is FloatyKind.Damage;
        int onTarget = 0;
        Slot? oldest = null;
        Slot? free = null;

        for (int i = 0; i < MaxSlots; i++)
        {
            Slot s = _slots[i];
            if (!s.Used) { free ??= s; continue; }
            if (s.Id != p.Id) continue;
            if (number != (s.Kind is FloatyKind.Damage)) continue;
            onTarget++;
            if (oldest == null || s.Seq < oldest.Seq) oldest = s;
        }

        // The cap is per target and per class: a burst of damage types never buries the banner that explains it.
        Slot? slot = free;
        if (onTarget >= max) slot = oldest;       // at the cap the oldest gives way, newest always wins
        if (slot == null) { System.Threading.Interlocked.Increment(ref _overflow); return; }     // the whole array is full of OTHER targets' text

        slot.Used = true;
        slot.Id = p.Id;
        slot.Kind = p.Kind;
        slot.SpawnMs = nowMs;
        slot.Seq = _seq++;
        slot.LifeMs = p.Kind == FloatyKind.Banner ? bannerMs : lifeMs;
        // At the cap the evicted slot's row is INHERITED, not pinned to the last one: pinning it puts every
        // survivor of a long fight on the same line and they pile back up.
        slot.Stack = onTarget >= max ? slot.Stack : onTarget;
        slot.Runs = p.Runs;
        slot.BaseR = p.R; slot.BaseG = p.G; slot.BaseB = p.B;
        slot.MeasuredW = -1f;
        slot.MeasureEpoch = -1;
    }

    private sealed class SpawnOrder : System.Collections.Generic.IComparer<Slot>
    {
        public static readonly SpawnOrder Instance = new();
        public int Compare(Slot? a, Slot? b) => a!.Seq.CompareTo(b!.Seq);
    }

    /// <summary>Ring, per-target cap and expiry, driven headlessly.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        bool ok = true;
        var t = new FloatyTable();
        ColorRun[] R(string s) => new[] { new ColorRun(s, 1f, 1f, 1f) };

        for (int i = 0; i < 5; i++) t.Note(7u, FloatyKind.Damage, R("" + i), 1f, 1f, 1f);
        ok &= t.Advance(0, 3, 1000f, 1000f) == 3;                       // per-target cap holds
        ok &= t.Live[0].Runs[0].Text == "2" && t.Live[2].Runs[0].Text == "4";   // oldest evicted, newest kept
        // and the survivors still occupy DISTINCT rows after two evictions
        ok &= t.Live[0].Stack != t.Live[1].Stack && t.Live[1].Stack != t.Live[2].Stack
              && t.Live[0].Stack != t.Live[2].Stack;

        t.Note(8u, FloatyKind.Damage, R("x"), 1f, 1f, 1f);
        ok &= t.Advance(0, 3, 1000f, 1000f) == 4;                       // a different target has its own cap

        t.Note(7u, FloatyKind.Banner, R("*Sneak Attack!*"), 1f, 1f, 1f);
        ok &= t.Advance(0, 3, 1000f, 1000f) == 5;                       // banners do not compete with numbers for the cap
        ok &= t.Live[4].Stack == 0;                         // and open their own column, not the numbers' one

        ok &= t.Advance(1000, 3, 1000f, 1000f) == 0;                    // everything expires at its life
        ok &= t.Advance(2000, 3, 1000f, 1000f) == 0;                    // and a retired slot stays retired

        t.Note(9u, FloatyKind.Damage, R("42"), 1f, 1f, 1f);
        t.Advance(0, 3, 1000f, 1000f);
        ok &= t.LiveCount == 1 && t.Live[0].LifeMs == 1000f;
        t.Clear();
        ok &= t.Advance(0, 3, 1000f, 1000f) == 0;

        // an empty parse is refused at the door rather than becoming an invisible slot
        t.Note(1u, FloatyKind.Damage, Array.Empty<ColorRun>(), 1f, 1f, 1f);
        ok &= t.Advance(0, 3, 1000f, 1000f) == 0;

        log.Info(ok ? "FloatyTable.SelfCheck: passed" : "FloatyTable.SelfCheck: FAILED");
    }
}
