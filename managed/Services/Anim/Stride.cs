using System;
using System.Numerics;
using CX.Engine;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Stride matching, a <see cref="PoseService"/> module. The engine paces a cycle by
/// <c>appearance.2da</c>'s WALKDIST/RUNDIST (<c>CNWCCreature::WalkUpdateLocation</c> writes the run's rate,
/// <c>AnimRun+0x08</c>); here the stance foot's own model-space speed is measured and every run on the gob scaled so
/// the feet match the measured ground speed.
/// </summary>
internal static unsafe class Stride
{
    /// <summary>Model units the stance foot must sit below the other before its speed is sampled: in double
    /// support neither foot is the one carrying the stride.</summary>
    private const float StanceGap = 0.03f;
    /// <summary>Seconds of stance samples the measured stride speed averages over.</summary>
    private const float StanceTime = 0.5f;
    /// <summary>Stride speeds below this (model units per unit rate per second) are no measurement: a shuffle.</summary>
    private const float MinStance = 0.2f;
    /// <summary>The correction's range: past it the cycle is not a walk at all and is left to the engine.</summary>
    private const float MinFactor = 0.6f, MaxFactor = 1.6f;
    /// <summary>Corrections inside this are no correction: the engine's pacing is already right. The next band's
    /// worth past it eases the correction in, so a measurement hovering at the edge cannot flip the rate between
    /// the engine's and a few percent off it; from twice this out it is the whole measured ratio.</summary>
    private const float Deadband = 0.04f;
    /// <summary>Seconds the applied factor takes to follow the measurement.</summary>
    private const float FactorTime = 0.3f;
    /// <summary>Gait acceleration (world units/s²) past which the body is winding up or down: the engine scales the
    /// cycle's rate with the speed factor itself then, and the measurement -- a ground speed averaged over 0.15 s
    /// against a stance speed averaged over half a second -- lags the ramp both ways, so pacing holds still.</summary>
    private const float Transient = 0.3f;
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

    /// <summary>The multiplier a body with slot <paramref name="s"/> plays its runs at this frame: last frame's
    /// eased factor while it is being paced -- walking, running, or shuffling a position correction closed
    /// (<see cref="Skating"/>) -- else 1. Read by the body itself and by its attachments, which animate first in
    /// the same frame and so see the same value.</summary>
    internal static float Of(in PoseService.Slot s) => AnimKnobs.Stride.V && (s.Loco || s.Shuffling) ? 1f + s.Factor : 1f;

    /// <summary>Sets every run on <paramref name="gob"/> to the engine's own rate times <paramref name="f"/>,
    /// telling the engine's value from this module's last write (<paramref name="s"/> keeps both).</summary>
    internal static void Scale(ref PoseService.Slot s, IntPtr gob, float f)
    {
        s.RateF = f;
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

    /// <summary>After the engine animates, before any correction: samples the stance foot's model-space speed
    /// (<paramref name="mr"/>/<paramref name="ml"/>, the ankles in model units) against the body's travel direction
    /// <paramref name="travel"/> and eases the factor toward <paramref name="groundSpeed"/>. The sample is the lower
    /// ankle's travel against that direction, only while it bears weight (<see cref="Skating.Bears"/>); a <see cref="Transient"/> gait holds the factor and the stance speed.</summary>
    internal static void Measure(ref PoseService.Slot s, Vector3 mr, Vector3 ml, Vector3 travel, float groundSpeed, bool fading, float dt)
    {
        bool paced = s.Loco || s.Shuffling;
        // a different clip is a different stride: the shuffle's is nothing like the walk that preceded it
        if (s.StanceAnim != s.GaitAnim) { s.StanceAnim = s.GaitAnim; s.Stance = 0f; }
        bool ramping = s.Gait != GaitMode.None && MathF.Abs(s.GaitAccel) > Transient;
        bool judged = paced && !fading && s.HasPrev && dt > 0f;
        bool bears = false;
        if (judged && MathF.Abs(mr.Z - ml.Z) > StanceGap)
        {
            Vector3 d = mr.Z < ml.Z ? mr - s.PrevR : ml - s.PrevL;
            float v = -(d.X * travel.X + d.Y * travel.Y) / dt / s.RateF;
            bears = Skating.Bears(v, groundSpeed);
            if (bears && !ramping) s.Stance = s.Stance <= 0f ? v : s.Stance + (v - s.Stance) * MathF.Min(1f, dt / StanceTime);
        }
        if (!paced) s.Airborne = 0f;
        else if (judged) s.Airborne = bears ? 0f : s.Airborne + dt;
        s.Grounded = s.Airborne < Skating.Liftoff;
        s.PrevR = mr; s.PrevL = ml; s.HasPrev = true;
        float target = paced && s.Grounded ? Factor(groundSpeed, s.Stance) - 1f : 0f;
        if (!ramping || !paced) Smooth(ref s.Factor, ref s.VFactor, target, dt, FactorTime);
    }

    /// <summary>The rate multiplier making a stance foot moving <paramref name="stance"/> per unit rate keep
    /// pace with the ground at <paramref name="ground"/>, eased in past the <see cref="Deadband"/>; 1 out of range
    /// or unmeasured.</summary>
    internal static float Factor(float ground, float stance)
    {
        if (!(stance > MinStance) || !(ground > 0f)) return 1f;
        float f = ground / stance;
        if (f < MinFactor || f > MaxFactor) return 1f;
        float u = Math.Clamp((MathF.Abs(f - 1f) - Deadband) / Deadband, 0f, 1f);
        return 1f + (f - 1f) * u * u * (3f - 2f * u);
    }
}
