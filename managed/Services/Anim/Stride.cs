using System;
using System.Numerics;
using CX.Engine;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// The playback-rate half of stride matching, a <see cref="PoseService"/> module. The engine paces a cycle by
/// <c>appearance.2da</c>'s WALKDIST/RUNDIST (<c>CNWCCreature::WalkUpdateLocation</c> writes the run's rate,
/// <c>AnimRun+0x08</c>); here every run on the gob plays at the rate <see cref="StrideWarp"/> paced from the clip's own
/// keys, and an attachment's runs are kept in step with its body's.
/// </summary>
internal static unsafe class Stride
{
    /// <summary>Runs whose rate is tracked: the walk, its overlay, and the ones fading out.</summary>
    internal const int TrackedRuns = 4;

    internal static int Strided, Synced;

    /// <summary>Before the engine animates: every run gets the engine's rate times the gob's stride factor
    /// (1 when not locomoting or the knob is off, which puts the engine's own value back). The rate is NOT restored
    /// like the pose -- the engine rewrites it on every AI update, and a frame without one leaves this module's own
    /// value, which <see cref="Scale"/> tells from the engine's and multiplies from rather than compounding.</summary>
    internal static void Pre(ref PoseService.Slot s, IntPtr gob)
    {
        float f = Of(s);
        Scale(ref s, gob, f);
        if (f != 1f) Strided++;
    }

    /// <summary>The multiplier a body with slot <paramref name="s"/> plays its runs at this frame: the one
    /// <see cref="StrideWarp"/> set last frame while it is being paced -- walking, running, or shuffling a position correction closed
    /// (<see cref="Skating"/>) -- else 1. Read by the body itself and by its attachments, which animate first in
    /// the same frame and so see the same value.</summary>
    internal static float Of(in PoseService.Slot s) => AnimKnobs.Stride.V && (s.Loco || s.Shuffling) ? s.RateNext : 1f;

    /// <summary>Sets every run on <paramref name="gob"/> to the engine's own rate times <paramref name="f"/>,
    /// telling the engine's value from this module's last write (<paramref name="s"/> keeps both).</summary>
    internal static void Scale(ref PoseService.Slot s, IntPtr gob, float f)
    {
        int n = Math.Min(PoseRig.RunCount(gob), TrackedRuns);
        {
            Span<PoseService.RunRate> rates = s.Rates;
            Span<PoseService.RunRate> keep = stackalloc PoseService.RunRate[TrackedRuns];
            int kept = 0;
            for (int i = 0; i < n; i++)
            {
                RunRef r = PoseRig.Run(gob, i);
                if (r.IsNull) continue;
                float cur = r.Rate, baseRate = cur;
                long id = r.Id;
                for (int k = 0; k < TrackedRuns; k++)
                    if (rates[k].Id == id) { if (cur == rates[k].Written) baseRate = rates[k].Engine; break; }
                float want = baseRate * f;
                if (want != cur) r.Rate = want;
                keep[kept++] = new PoseService.RunRate { Id = id, Engine = baseRate, Written = want };
            }
            for (int k = 0; k < TrackedRuns; k++) rates[k] = k < kept ? keep[k] : default;
        }
    }

    /// <summary>Before an attachment animates (a cloak, robe, tail or wings is a gob of its own, attached to the
    /// body's root by <c>CNWCAnimBaseTW::LoadModel</c>): each of its runs takes the rate of the body's run playing the
    /// same animation (the engine's value times <paramref name="f"/>), and with <c>anim.syncattach</c> its time and
    /// blend. <paramref name="lead"/> is the seconds the body ALREADY advanced this frame, taken back off that time.</summary>
    internal static void Sync(IntPtr gob, IntPtr body, ref PoseService.Slot b, bool tracked, float f, float lead = 0f)
    {
        int n = PoseRig.RunCount(gob), bn = PoseRig.RunCount(body);
        if (n == 0 || bn == 0) return;
        bool phase = AnimKnobs.SyncAttach.V;
        if (!phase && f == 1f) return;
        for (int i = 0; i < n; i++)
        {
            RunRef r = PoseRig.Run(gob, i);
            if (r.IsNull) continue;
            RunRef br = Match(r, body, bn, i);
            if (br.IsNull) continue;
            float rate = br.Rate;
            long id = br.Id;
            if (tracked)
            {
                Span<PoseService.RunRate> rates = b.Rates;
                for (int k = 0; k < TrackedRuns; k++)
                    if (rates[k].Id == id) { if (rate == rates[k].Written) rate = rates[k].Engine; break; }
            }
            r.Rate = rate * f;
            if (!phase) continue;
            float t = br.Time - lead * r.Rate, len = br.Length;
            // only a LOOPING run wraps: a one-shot the body started this frame reads time ~0, the lead takes it
            // below 0, and wrapping showed the cloak the END of a turn while the body began it
            r.Time = t < 0f && len > 0f && (r.Flags & Looping) != 0 ? t + len : MathF.Max(t, 0f);
            if (((r.Flags ^ br.Flags) & FadingOut) == 0)
            {
                r.Blend = br.Blend;
                r.BlendTime = br.BlendTime;
            }
            Synced++;
        }
    }

    /// <summary>AnimRun flag bit: the run is fading out.</summary>
    private const uint FadingOut = 0x1;  // not-an-offset: a flag bit
    /// <summary>AnimRun flag bit: the run loops.</summary>
    private const uint Looping = 0x4;  // not-an-offset: a flag bit

    /// <summary>The body's run playing the animation <paramref name="run"/> plays, by name (the two gobs load
    /// their own <c>Animation</c> objects): the one at the same list position first, else the first by name.</summary>
    private static RunRef Match(in RunRef run, IntPtr body, int bn, int at)
    {
        RunRef same = at < bn ? PoseRig.Run(body, at) : default;
        if (run.SameName(same)) return same;
        for (int j = 0; j < bn; j++)
        {
            if (j == at) continue;
            RunRef br = PoseRig.Run(body, j);
            if (run.SameName(br)) return br;
        }
        return default;
    }

    /// <summary>The rate the engine itself set on run <paramref name="r"/>: what it holds, unless that is this module's
    /// own last write, which <paramref name="s"/> remembers with the engine's value under it.</summary>
    internal static float EngineRate(in PoseService.Slot s, RunRef r)
    {
        float cur = r.Rate;
        long id = r.Id;
        ReadOnlySpan<PoseService.RunRate> rates = s.Rates;
        for (int k = 0; k < TrackedRuns; k++)
            if (rates[k].Id == id) return cur == rates[k].Written ? rates[k].Engine : cur;
        return cur;
    }
}
