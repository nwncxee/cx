using System;
using CX.Framework;

namespace CX.Services.Camera;

/// <summary>
/// The tab-target mode's two pure decisions: which candidate the cycle key lands on, and what a target-asking
/// quickbar button does under each cast mode. No native reads; <see cref="TabTargetService"/> fills the candidate
/// list and acts on the verdicts, and nothing here allocates. The cycle order is a ring -- candidates sorted by
/// screen distance when it is built, kept across presses, rebuilt only when the lock is not on it.
/// </summary>
internal static class TabTargetCore
{
    /// <summary>Ring and candidate ceiling. ponytail: 64 is more than any fight puts in view; a bigger area
    /// simply drops the farthest candidates, which is what the range knob does anyway.</summary>
    internal const int MaxCandidates = 64;

    internal struct Candidate
    {
        public uint Id;
        /// <summary>Pixels from the screen centre.</summary>
        public float ScreenDist;
        /// <summary>Metres from the player.</summary>
        public float Range;
        public bool Hostile;
        /// <summary>The engine object type byte: 5 creature, 9 placeable, 0xa door.</summary>
        public byte Type;
    }

    internal const byte TypeCreature = 5, TypeItem = 6, TypeTrigger = 7, TypePlaceable = 9, TypeDoor = 0xa;  // not-an-offset: an object type enum, the trigger's from the CNWCTrigger ctor

    internal enum CastMode { Normal, Quick, Preview }
    internal enum CastEvent { Open, Release, Cancel }
    internal enum CastVerdict { LeaveUp, FireAtLock, FireAtCursor, Cancel }

    /// <summary>What a target mode does at <paramref name="ev"/>. Normal never fires for us; Quick fires on
    /// open; Preview leaves the mode up (the engine draws its own preview) and fires on release. A firing
    /// verdict goes to the lock when there is one and the mode takes an object, else to the cursor pick.</summary>
    internal static CastVerdict Decide(CastMode mode, bool hasLock, bool takesObject, CastEvent ev)
    {
        if (ev == CastEvent.Cancel) return CastVerdict.Cancel;
        bool fire = mode switch
        {
            CastMode.Quick => ev == CastEvent.Open,
            CastMode.Preview => ev == CastEvent.Release,
            _ => false,
        };
        if (!fire) return CastVerdict.LeaveUp;
        return hasLock && takesObject ? CastVerdict.FireAtLock : CastVerdict.FireAtCursor;
    }

    /// <summary>Does <paramref name="c"/> pass the knobs? Creatures always by type; placeables and doors only
    /// with <paramref name="objects"/>; the hostility filter applies to creatures alone.</summary>
    internal static bool Passes(in Candidate c, bool hostileOnly, bool objects, float maxRange)
    {
        if (c.Id == 0 || c.Range > maxRange) return false;
        if (c.Type == TypeCreature) return !hostileOnly || c.Hostile;
        return objects && (c.Type == TypePlaceable || c.Type == TypeDoor);
    }

    /// <summary>The next lock. <paramref name="ring"/> / <paramref name="ringCount"/> are the persistent cycle
    /// order (see the class summary). Returns <paramref name="held"/> unchanged when nothing passes the filter
    /// or nothing else on the ring is live.</summary>
    internal static uint Cycle(ReadOnlySpan<Candidate> live, bool hostileOnly, bool objects, float maxRange,
                               uint held, bool reverse, Span<uint> ring, ref int ringCount)
    {
        Span<Candidate> ok = stackalloc Candidate[MaxCandidates];
        int n = 0;
        for (int i = 0; i < live.Length && n < MaxCandidates; i++)
            if (Passes(live[i], hostileOnly, objects, maxRange)) ok[n++] = live[i];
        if (n == 0) return held;
        for (int i = 1; i < n; i++)
        {
            Candidate c = ok[i];
            int j = i - 1;
            while (j >= 0 && ok[j].ScreenDist > c.ScreenDist) { ok[j + 1] = ok[j]; j--; }
            ok[j + 1] = c;
        }

        int at = IndexOf(ring, ringCount, held);
        if (held == 0 || at < 0)
        {
            ringCount = 0;
            for (int i = 0; i < n && ringCount < ring.Length; i++) ring[ringCount++] = ok[i].Id;
            at = IndexOf(ring, ringCount, held);
            if (at < 0) return ring[0];
        }
        else
        {
            for (int i = 0; i < n && ringCount < ring.Length; i++)
                if (IndexOf(ring, ringCount, ok[i].Id) < 0) ring[ringCount++] = ok[i].Id;
        }

        for (int step = 1; step < ringCount; step++)
        {
            int idx = ((reverse ? at - step : at + step) % ringCount + ringCount) % ringCount;
            uint id = ring[idx];
            for (int i = 0; i < n; i++) if (ok[i].Id == id) return id;
        }
        return held;
    }

    private static int IndexOf(Span<uint> ring, int count, uint id)
    {
        if (id == 0) return -1;
        for (int i = 0; i < count; i++) if (ring[i] == id) return i;
        return -1;
    }

    /// <summary>The cycle and cast-verdict cases off-device, run from the service's Initialize.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        Candidate C(uint id, float px, float m = 5f, bool hostile = true, byte type = TypeCreature)
            => new() { Id = id, ScreenDist = px, Range = m, Hostile = hostile, Type = type };

        uint[] ring = new uint[MaxCandidates];
        int rc = 0;
        Candidate[] live = { C(30, 300f), C(10, 100f), C(20, 200f) };
        uint Next(uint held, bool rev = false, bool hostileOnly = true, bool objects = false, float range = 100f)
            => Cycle(live, hostileOnly, objects, range, held, rev, ring, ref rc);

        check.Expect(Next(0) == 10, "no lock: the candidate nearest the screen centre wins");
        check.Expect(Next(10) == 20 && Next(20) == 30, "lock present: the next in ring order");
        check.Expect(Next(30) == 10, "the ring wraps at the end");
        check.Expect(Next(10, rev: true) == 30 && Next(30, rev: true) == 20, "reverse steps back and wraps at the start");

        live = new[] { C(20, 50f), C(30, 300f), C(10, 100f) };
        check.Expect(Next(20) == 30, "the ring is stable: the order does not follow a candidate that moved nearer");
        live = new[] { C(20, 50f), C(30, 300f), C(10, 100f), C(40, 20f) };
        check.Expect(Next(30) == 40 && Next(40) == 10, "a newcomer joins the end of the ring and the ring still wraps");
        live = new[] { C(30, 300f), C(10, 100f) };
        check.Expect(Next(10) == 30, "an id that left the candidate list is skipped");
        check.Expect(Next(99) == 10, "a lock no longer in the list behaves as no lock");
        live = Array.Empty<Candidate>();
        check.Expect(Next(10) == 10 && Next(0) == 0, "an empty list leaves the lock unchanged");

        rc = 0;
        live = new[] { C(1, 10f, hostile: false), C(2, 20f) };
        check.Expect(Next(0) == 2, "hostile-only drops the non-hostile creature");
        rc = 0;
        check.Expect(Next(0, hostileOnly: false) == 1, "the wide filter keeps it");
        rc = 0;
        live = new[] { C(1, 10f, m: 50f), C(2, 20f, m: 5f) };
        check.Expect(Next(0, range: 30f) == 2, "an out-of-range candidate is dropped");
        rc = 0;
        live = new[] { C(1, 10f, type: TypePlaceable), C(2, 20f, type: TypeDoor), C(3, 30f) };
        check.Expect(Next(0) == 3, "placeables and doors are out by default");
        rc = 0;
        check.Expect(Next(0, objects: true) == 1, "the objects knob lets them in");
        rc = 0;
        live = new[] { C(1, 10f, hostile: false, type: TypePlaceable) };
        check.Expect(Next(0, objects: true) == 1, "the hostility filter does not apply to a placeable");

        check.Expect(Decide(CastMode.Normal, true, true, CastEvent.Open) == CastVerdict.LeaveUp
          && Decide(CastMode.Normal, false, false, CastEvent.Open) == CastVerdict.LeaveUp, "Normal leaves every mode up");
        check.Expect(Decide(CastMode.Quick, true, true, CastEvent.Open) == CastVerdict.FireAtLock, "Quick + lock + object mode fires at the lock");
        check.Expect(Decide(CastMode.Quick, false, true, CastEvent.Open) == CastVerdict.FireAtCursor, "Quick without a lock fires at the cursor");
        check.Expect(Decide(CastMode.Quick, true, false, CastEvent.Open) == CastVerdict.FireAtCursor, "Quick + ground-only mode lands at the cursor even with a lock");
        check.Expect(Decide(CastMode.Preview, true, true, CastEvent.Open) == CastVerdict.LeaveUp, "Preview leaves the mode up on open");
        check.Expect(Decide(CastMode.Preview, true, true, CastEvent.Release) == CastVerdict.FireAtLock, "Preview fires at the lock on release");
        check.Expect(Decide(CastMode.Preview, false, false, CastEvent.Release) == CastVerdict.FireAtCursor, "Preview + ground mode fires at the cursor on release");
        check.Expect(Decide(CastMode.Preview, true, true, CastEvent.Cancel) == CastVerdict.Cancel, "a cancel before release cancels");
        check.Expect(Decide(CastMode.Quick, true, true, CastEvent.Release) == CastVerdict.LeaveUp, "a stray release under Quick does nothing");

        log.Info(check.Ok ? "TabTargetCore.SelfCheck: passed" : "TabTargetCore.SelfCheck: FAILED");
    }
}
