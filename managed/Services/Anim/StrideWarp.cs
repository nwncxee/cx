using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Services.Anim;

/// <summary>
/// Stride warping, a <see cref="PoseService"/> module under <c>anim.stride</c>: a body covering ground faster or slower
/// than its walk cycle does takes quicker AND longer (or slower and shorter) steps instead of playing the cycle faster,
/// each foot held where it landed (<see cref="Legs.Hold"/>) and each swing aimed onto the line of travel. The contacts and the pace come from the clip's own keys
/// (<see cref="GaitClip"/>), so nothing here guesses from a measured height; a clip that is not a walk is left alone.
/// </summary>
internal static class StrideWarp
{
    /// <summary>The range the cycle's playback rate takes of a pace change before the stride length takes the rest.</summary>
    internal const float RateMin = 0.75f, RateMax = 1.35f;
    /// <summary>The range the stride length takes; past both, the rate takes what is left, so the feet never slide.</summary>
    internal const float StrideMin = 0.7f, StrideMax = 1.4f;
    /// <summary>The widest stride ratio drawn from a rate and speed that disagree for a frame (a pace still catching up).</summary>
    internal const float WarpMin = 0.6f, WarpMax = 1.6f;

    /// <summary>Walk weight from which the feet are planted: through a crossfade in from a stand the blended feet move only
    /// part of the stride, and a plant held against them builds an offset the next swing has to carry.</summary>
    private const float Blended = 0.99f;

    /// <summary>What the module did this period: bodies warped, feet planted, holds let go for a step, gait changes
    /// and starts put in step, the largest offset a foot lifted off with and the deepest pelvis drop, world units.</summary>
    private sealed class StrideWarpState
    {
        public int Warped, Planted, Released;
        public float Leftover, MaxDrop;
        public int Matched, Started;
    }

    private static readonly StrideWarpState _s = new();

    /// <summary>Bodies warped and feet planted since the last ask, per frame over <paramref name="frames"/>; resets.</summary>
    internal static string Period(int frames)
    {
        (int walks, int others) = GaitClips.Count;
        string line = $"{_s.Warped / (float)frames:F1} stride-warped + {_s.Planted / (float)frames:F1} feet planted/f, "
                      + $"{_s.Released} holds let go, {_s.Matched} gait changes and {_s.Started} starts put in step, largest lift-off offset {_s.Leftover:F3}, deepest pelvis drop {_s.MaxDrop:F3} "
                      + $"({walks} walk clips, {others} not walks)";
        _s.Warped = _s.Planted = _s.Released = _s.Matched = _s.Started = 0;
        _s.Leftover = _s.MaxDrop = 0f;
        return line;
    }

    /// <summary>One frame for a body, after the animation is posed: the world offset each ankle is drawn at (the stride
    /// warp and the plant) and the rate multiplier the next frame plays its runs at (<see cref="LegPose.RateNext"/>).
    /// Every walk run on the body counts by its weight (<see cref="Gather"/>), so a crossfade into or out of one carries
    /// the correction in and out with it.</summary>
    internal static void Step(ref LegState s, in LegFrame f, ref LegPose p, out Vector3 dR, out Vector3 dL)
        => Warps(ref s, f, ref p, f.Gated || f.Snap, out dR, out dL);

    /// <summary>Counts this frame's pelvis drop into the period's deepest.</summary>
    internal static void Dropped(float drop)
    {
        if (drop > _s.MaxDrop) _s.MaxDrop = drop;
    }

    /// <summary>The walk runs on <paramref name="gob"/> digested into <paramref name="f"/>'s <see cref="LegFrame.Walk"/>,
    /// and a walk fading in put in step (<see cref="Seed"/>). <paramref name="mr"/>, <paramref name="ml"/>: the ankles
    /// now, model frame. Engine side: it reads the runs and writes a run's time.</summary>
    internal static void Gather(ref PoseService.Slot s, IntPtr gob, in PoseService.Bones bones, ref LegFrame f, Vector3 mr, Vector3 ml)
    {
        f.Walk = default;
        if (f.Gated || f.Snap || !f.Knobs.Stride || s.Leg.SettleStage != Settle.None) { Forget(ref s); return; }
        bool paced = f.Paced;
        float speed = f.Ground / f.Scale, weight = 0f, strides = 0f, top = -1f;
        Vector3 dir = Vector3.Zero;
        GaitClip? lead = null;
        AnimDef? leadAnim = null;
        RunRef leadRun = default, freshRun = default, otherRun = default;
        GaitClip? fresh = null, other = null;
        float otherW = -1f;
        int n = PoseRig.RunCount(gob);
        for (int i = 0; i < n; i++)
        {
            RunRef r = PoseRig.Run(gob, i);
            if (r.IsNull || r.Clip is not { } anim) continue;
            GaitClip? c = GaitClips.For(anim, bones, r.PosScale, paced);
            float w = Math.Clamp(r.Blend, 0f, 1f);
            if (c is not { Walk: true } || !(w > 0f)) continue;
            float rate = r.Rate;
            weight += w;
            strides += w * (paced && rate > 1e-4f ? Math.Clamp(speed / c.Speed / rate, WarpMin, WarpMax) : 1f);
            dir += w * c.Dir;
            if (w > top) { top = w; lead = c; leadAnim = anim; leadRun = r; }
            if (c.Cyclic && r.Blend < 1f && r.Id != s.Seeded && r.Time <= Fresh * c.Length) { fresh = c; freshRun = r; }
            else if (w > otherW) { otherW = w; other = c; otherRun = r; }
        }
        if (lead == null || weight < 1e-3f) { Forget(ref s); return; }
        float phase = leadRun.Time / lead.Length;
        ref LegWalk k = ref f.Walk;
        k.Present = true;
        k.Weight = weight;
        k.Strides = strides;
        k.Dir = dir;
        k.LeadDir = lead.Dir;
        k.Speed = lead.Speed;
        k.EngineRate = paced ? Stride.EngineRate(s, leadRun) : 0f;
        k.Rate = leadRun.Rate;
        k.Phase = phase;
        k.Length = lead.Length;
        k.Cyclic = lead.Cyclic;
        k.Authored = lead.Authored;
        k.FloorR = lead.FloorR;
        k.FloorL = lead.FloorL;
        k.LegLength = lead.LegLength;
        k.DownR = lead.DownR;
        k.DownL = lead.DownL;
        k.LandR = lead.LandR;
        k.LandL = lead.LandL;
        k.ContactR = lead.Contact(true, phase);
        k.ContactL = lead.Contact(false, phase);
        k.SwingR = lead.Swing(true, phase);
        k.SwingL = lead.Swing(false, phase);
        k.Changed = !ReferenceEquals(leadAnim, s.PlantClip);
        s.PlantClip = leadAnim;
        if (fresh != null) Seed(ref s.Seeded, fresh, freshRun, other, otherRun, mr, ml);
    }

    /// <summary>No walk to plant by: the next one is timed from scratch and seeded afresh.</summary>
    private static void Forget(ref PoseService.Slot s)
    {
        s.PlantClip = null;
        s.Seeded = 0L;
    }

    /// <summary>The warp and plant offsets of <see cref="Step"/>.</summary>
    private static void Warps(ref LegState s, in LegFrame f, ref LegPose p, bool off, out Vector3 dR, out Vector3 dL)
    {
        dR = dL = Vector3.Zero;
        p.RateNext = 1f;
        p.Touchdown = p.Unvoiced = p.Landing = false;
        ref readonly LegWalk k = ref f.Walk;
        if (off || !f.Knobs.Stride || s.SettleStage != Settle.None || !k.Present) { Reset(ref s, ref p); return; }
        float scale = f.Scale, speed = f.Ground / scale;
        float weight = k.Weight;
        float stride = k.Strides / weight;
        weight = MathF.Min(weight, 1f);
        Vector3 dir = k.Dir.LengthSquared() > 1e-8f ? Vector3.Normalize(k.Dir) : k.LeadDir;
        if (f.Paced && k.EngineRate > 1e-4f) p.RateNext = RateShare(speed / k.Speed, k.EngineRate);
        float phase = k.Phase;
        if (k.Changed)
        {
            Rebase(ref s.FootingR, k.SwingR);
            Rebase(ref s.FootingL, k.SwingL);
            // a contact the new clip starts in is no landing
            s.FootingR.Aired = s.FootingL.Aired = false;
        }

        Vector3 ra = f.RAnkle, la = f.LAnkle, rt = f.RThigh, lt = f.LThigh, origin = f.Origin;
        Quaternion legs = p.Legs, toModel = Quaternion.Conjugate(legs);
        Vector3 mr = Vector3.Transform(ra - origin, toModel) / scale, ml = Vector3.Transform(la - origin, toModel) / scale;
        Vector3 hip = 0.5f * (Vector3.Transform(rt - origin, toModel) + Vector3.Transform(lt - origin, toModel)) / scale;
        dR = Vector3.Transform(Warp(mr, hip, dir, k.FloorR, stride, weight) - mr, legs) * scale;
        dL = Vector3.Transform(Warp(ml, hip, dir, k.FloorL, stride, weight) - ml, legs) * scale;
        float leg = k.LegLength * scale;
        bool downR = false, downL = false;
        // plants start once the walk is blended in, and carry on through its blend out into a stop's settle
        s.Planting |= weight >= Blended;
        if (s.Planting)
        {
            // a one-shot past its end times nothing: its plants keep their offsets, which fade with the run's weight
            bool timed = k.Cyclic || phase < 1f;
            float heading = MathF.Atan2(p.LegFwdH.Y, p.LegFwdH.X);
            Vector3 line = Vector3.Transform(dir, legs);
            // a foot setting down this frame is aimed too: its hold is taken where it lands
            if (s.FootingR.Stage == Footing.Swing) dR += Legs.Aim(ref s.FootingR, ra + dR, origin, f.Vel, line, k.SwingR, scale);
            if (s.FootingL.Stage == Footing.Swing) dL += Legs.Aim(ref s.FootingL, la + dL, origin, f.Vel, line, k.SwingL, scale);
            HoldLimits r = Limits(rt, Legs.Stretch(f, p, true), leg, scale), l = Limits(lt, Legs.Stretch(f, p, false), leg, scale);
            dR += weight * Plant(ref s.FootingR, k.ContactR, k.SwingR, ra + dR, heading, r, timed, f.Dt, out float yawR, out downR);
            dL += weight * Plant(ref s.FootingL, k.ContactL, k.SwingL, la + dL, heading, l, timed, f.Dt, out float yawL, out downL);
            p.YawR = weight * yawR;
            p.YawL = weight * yawL;
        }

        p.Unvoiced = !k.Authored;
        p.Touchdown = downR || downL;
        p.Landing = f.Paced && k.Cyclic && k.Rate > 1e-4f;
        if (p.Landing)
        {
            Vector3 vel = Vector3.Transform(dir, legs) * f.Ground;
            p.AheadR = Predict(k.ContactR, k.SwingR, k.DownR, phase, k.Length, k.Rate, Vector3.Transform(Warp(k.LandR, hip, dir, k.FloorR, stride, weight), legs) * scale + origin, vel);
            p.AheadL = Predict(k.ContactL, k.SwingL, k.DownL, phase, k.Length, k.Rate, Vector3.Transform(Warp(k.LandL, hip, dir, k.FloorL, stride, weight), legs) * scale + origin, vel);
        }
        else p.AheadR = p.AheadL = default;
        _s.Warped++;
    }

    /// <summary>One foot's plant (<see cref="Legs.Hold"/>) at its warped world position <paramref name="p"/>, on the ground
    /// (<paramref name="on"/>) or <paramref name="u"/> through its swing (held as it is when not <paramref name="timed"/>),
    /// counted into the period; <paramref name="down"/>: it was set down this frame.</summary>
    private static Vector3 Plant(ref Footing f, bool on, float u, Vector3 p, float heading, in HoldLimits r, bool timed, float dt,
                                 out float yaw, out bool down)
    {
        down = false;
        yaw = f.Yaw;
        if (!timed) return f.Off;
        bool was = f.Stage != Footing.Swing;
        Vector3 off = Legs.Hold(ref f, on, u, p, heading, r, dt, out yaw, out down, out bool let);
        if (on) _s.Planted++;
        if (let) _s.Released++;
        if (was && !on) _s.Leftover = MathF.Max(_s.Leftover, f.Off0.Length());
        return off;
    }

    /// <summary>What a hold on the leg from hip joint <paramref name="hip"/> (world, <paramref name="leg"/> long, the clip
    /// drawing it <paramref name="stretch"/> long at most) may cost before it is let go.</summary>
    private static HoldLimits Limits(Vector3 hip, float stretch, float leg, float scale) => new()
    {
        Hip = hip,
        Reach = MathF.Max(Legs.Reach * leg, stretch),
        Cap = Legs.MaxDrop * leg,
        Sink = Legs.HoldSink * leg,
        Hold = Legs.HoldShare * leg,
        Lift = Legs.StepLift * scale,
    };

    /// <summary>Where a foot in the air will land: <paramref name="at"/> (world, as the clip lands it with the body where it
    /// is now) carried on by the body's velocity <paramref name="vel"/> for the time to its landing at <paramref name="down"/>;
    /// and how far through its swing <paramref name="u"/> it is. Nothing for a foot on the ground or one with no landing.</summary>
    private static Ahead Predict(bool on, float u, float down, float phase, float length, float rate, Vector3 at, Vector3 vel)
    {
        if (!(down >= 0f) || on) return default;
        Vector3 p = at + vel * SecondsTo(phase, down, length, rate);
        return new Ahead { Swing = true, At = new Vector2(p.X, p.Y), U = u };
    }

    /// <summary>Seconds from <paramref name="phase"/> to the next <paramref name="down"/> of a loop
    /// <paramref name="length"/> seconds long played at <paramref name="rate"/>.</summary>
    internal static float SecondsTo(float phase, float down, float length, float rate) => Wrap(down - phase) * length / rate;

    /// <summary>A foot in the air: where it will land (world XY) and how far through its swing it is, 0..1.</summary>
    internal struct Ahead
    {
        public bool Swing;
        public Vector2 At;
        public float U;
    }

    /// <summary>No walk on the body: the plants are let go and the next one starts fresh (<see cref="Gather"/> forgets
    /// the clip it was timed by).</summary>
    private static void Reset(ref LegState s, ref LegPose p)
    {
        p.AheadR = p.AheadL = default;
        s.FootingR = s.FootingL = default;
        s.Planting = false;
    }

    /// <summary>Cycle fraction under which a fading-in walk is fresh enough to seed: the engine starts a new run at its
    /// first frame, and one reused or carried past this is left on the phase it has.</summary>
    private const float Fresh = 0.1f;
    /// <summary>Cycle fraction past its foot's landing a walk started from standing begins: inside the shortest stock
    /// stance and clear of the other foot's lift-off.</summary>
    private const float JustAfter = 0.06f;

    /// <summary>Starts a walk fading in (<paramref name="run"/>) in step: out of another walk, at the same point of the
    /// stride (<see cref="Matched"/>); from standing, with the foot already in front planted (<see cref="Start"/>), so the
    /// first step falls to the back one. <paramref name="mr"/>, <paramref name="ml"/>: the ankles now, model frame.</summary>
    private static void Seed(ref long seeded, GaitClip clip, RunRef run, GaitClip? from, RunRef fromRun, Vector3 mr, Vector3 ml)
    {
        seeded = run.Id;
        float p;
        if (from != null)
        {
            if (!(from.DownR >= 0f) || !(clip.DownR >= 0f) || !(from.Length > 0f)) return;
            p = Matched(fromRun.Time / from.Length, from.DownR, clip.DownR);
            _s.Matched++;
        }
        else
        {
            if (!(clip.DownR >= 0f) || !(clip.DownL >= 0f)) return;
            p = Start((mr.X - ml.X) * clip.Dir.X + (mr.Y - ml.Y) * clip.Dir.Y >= 0f, clip.DownR, clip.DownL);
            _s.Started++;
        }
        run.Time = p * clip.Length;
    }

    /// <summary>The phase a walk takes up from one at <paramref name="phase"/>: as far past its own right-foot landing
    /// (<paramref name="newDown"/>) as the old is past its (<paramref name="oldDown"/>), so the same foot is down.</summary>
    internal static float Matched(float phase, float oldDown, float newDown) => Wrap(newDown + phase - oldDown);

    /// <summary>The phase a walk started from standing begins at: just past the landing of the foot in front.</summary>
    internal static float Start(bool rightInFront, float downR, float downL) => Wrap((rightInFront ? downR : downL) + JustAfter);

    private static float Wrap(float p) => p - MathF.Floor(p);

    /// <summary>The share of the engine's rate a walk plays at when its clip covers the ground at <paramref name="k"/>
    /// times the body's speed. Never under <see cref="HeldBack"/>: a body held near still by a wall keeps stepping.</summary>
    internal static float RateShare(float k, float engine) => MathF.Max(Pace(k).Rate / engine, HeldBack);

    /// <summary>The least share of the engine's rate a paced walk plays at.</summary>
    private const float HeldBack = 0.5f;

    /// <summary>The playback rate and stride length a clip whose feet cover the ground at <paramref name="k"/> times
    /// the body's speed plays at: the square root of it each, as a walker speeds up by both cadence and stride.</summary>
    internal static (float Rate, float Stride) Pace(float k)
    {
        if (!(k > 0f)) return (1f, 1f);
        float rate = Math.Clamp(MathF.Sqrt(k), RateMin, RateMax);
        float stride = Math.Clamp(k / rate, StrideMin, StrideMax);
        return (k / stride, stride);
    }

    /// <summary>Ankle <paramref name="a"/> (model frame) with its stride stretched by <paramref name="stride"/> about the
    /// hip midpoint <paramref name="hip"/> along <paramref name="dir"/>, its lift off <paramref name="floor"/> by the
    /// stride's square root, all at weight <paramref name="w"/>.</summary>
    internal static Vector3 Warp(Vector3 a, Vector3 hip, Vector3 dir, float floor, float stride, float w)
    {
        float along = (a.X - hip.X) * dir.X + (a.Y - hip.Y) * dir.Y;
        Vector3 reach = new Vector3(dir.X, dir.Y, 0f) * (along * (stride - 1f));
        float lift = (a.Z - floor) * (MathF.Sqrt(stride) - 1f);
        return a + w * new Vector3(reach.X, reach.Y, lift);
    }

    /// <summary>A swing's progress measured against a different clip from here on: the offset left eases out from now.</summary>
    internal static void Rebase(ref Footing f, float u)
    {
        if (f.Stage == Footing.Swing) Legs.Lift(ref f, u);
    }

    /// <summary>The pace split, the phase seeds and the landing time against hand-worked values, and a walk run through speed
    /// ramps between half and twice its clip's pace. Oracles: the square-root split and its 0.7 stride floor worked by
    /// hand, and the physical rule that a stance foot does not move on the ground and no foot jumps between frames.</summary>
    internal static bool SelfCheck()
    {
        (float r1, float s1) = Pace(1f);
        (float r15, float s15) = Pace(1.5f);
        (float r3, float s3) = Pace(3f);
        (float rs, float ss) = Pace(0.36f);
        bool paces = MathF.Abs(r1 - 1f) < 1e-5f && MathF.Abs(s1 - 1f) < 1e-5f
                     && MathF.Abs(r15 - 1.224745f) < 1e-4f && MathF.Abs(s15 - 1.224745f) < 1e-4f
                     && MathF.Abs(r3 - 2.142857f) < 1e-4f && MathF.Abs(s3 - 1.4f) < 1e-5f
                     && MathF.Abs(rs - 0.514286f) < 1e-4f && MathF.Abs(ss - 0.7f) < 1e-5f;

        // a walk 0.3 past a landing at 0.8 into a run landing at 0.5 is 0.3 past it too; a start lands 0.06 past its front foot
        bool seeds = MathF.Abs(Matched(0.1f, 0.8f, 0.5f) - 0.8f) < 1e-5f && MathF.Abs(Matched(0.9f, 0.8f, 0.5f) - 0.6f) < 1e-5f
                     && MathF.Abs(Start(true, 0.5f, 0.999f) - 0.56f) < 1e-5f && MathF.Abs(Start(false, 0.5f, 0.999f) - 0.059f) < 1e-4f;
        // 0.2 of a 1.2 s loop at rate 1.5 is 0.16 s, wrapping past the loop's end
        bool ahead = MathF.Abs(SecondsTo(0.9f, 0.1f, 1.2f, 1.5f) - 0.16f) < 1e-5f && MathF.Abs(SecondsTo(0.2f, 0.5f, 1f, 1f) - 0.3f) < 1e-5f;
        // a crawl of 0.02 splits to 0.02 / 0.7 = 0.029 of the clip's rate, under the 0.5 floor; 0.36 / 0.7 = 0.514 is over it
        bool held = RateShare(0.02f, 1f) == 0.5f && MathF.Abs(RateShare(0.02f, 1.4f) - 0.5f) < 1e-6f
                    && MathF.Abs(RateShare(0.36f, 1f) - 0.514286f) < 1e-4f && MathF.Abs(RateShare(1f, 1f) - 1f) < 1e-5f;
        return paces && held && seeds && ahead && Walks();
    }

    /// <summary>World units a lift-off may bend a drawn path past the animation's own: the drift a ramping pace leaves
    /// on a planted foot, picked up in one frame; a foot let go and snapped back moves ten times this.</summary>
    private const float LiftBend = 0.02f;
    /// <summary>The synthetic walk's own stance speed and floor, known by construction: the pace and the warp read them,
    /// not the analysis, which times only the plants.</summary>
    private const float SyntheticSpeed = 1.5f, SyntheticFloor = 0.08f;

    /// <summary>The synthetic walk (<see cref="GaitClip.Synthetic"/>) driven 4 s at 60 fps from half its pace to twice it
    /// and back, the rate a frame late as live, each foot held by <see cref="Legs.Hold"/>: held steady, a planted foot's
    /// warped spot does not move; no frame bends a drawn path past the warped animation's own by more than
    /// <see cref="LiftBend"/>; and a foot on the ground moves only lifted.</summary>
    private static bool Walks()
    {
        GaitClip c = GaitClip.Synthetic(0.85f, SyntheticFloor, SyntheticSpeed, 0.12f);
        if (!c.Walk) return false;
        const float Dt = 1f / 60f, Leg = 0.9f;
        Vector3 hip = new(0f, 0f, 0.85f);
        float t = 0f, body = 0f, rateNext = Pace(0.75f / SyntheticSpeed).Rate, worstSlide = 0f, worstBend = 0f;
        int drags = 0;
        Span<Footing> feet = stackalloc Footing[2];
        Span<Vector2> p1 = stackalloc Vector2[2], p2 = stackalloc Vector2[2], d1 = stackalloc Vector2[2], d2 = stackalloc Vector2[2];
        Span<bool> was = stackalloc bool[2], held = stackalloc bool[2];
        for (int n = 0; n < 240; n++)
        {
            float time = n * Dt;
            float g = time < 1f ? 0.75f + 2.25f * time : time < 2f ? 3f : time < 3f ? 3f - 2.25f * (time - 2f) : 0.75f;
            float rate = rateNext;
            t += rate * Dt;
            body += g * Dt;
            float k = g / SyntheticSpeed;
            rateNext = Pace(k).Rate;
            float phase = t, stride = Math.Clamp(k / rate, WarpMin, WarpMax);
            bool steady = time is > 1.2f and < 2f or > 3.2f;
            for (int side = 0; side < 2; side++)
            {
                bool right = side == 0;
                float own = right ? phase : phase + 0.5f, x = right ? 0.1f : -0.1f;
                bool stance = own - MathF.Floor(own) < 0.6f;
                Vector3 a = GaitClip.SyntheticFoot(own, x, SyntheticFloor, SyntheticSpeed, 0.12f);
                Vector3 w = Warp(a, hip, Vector3.UnitY, SyntheticFloor, stride, 1f);
                Vector2 p = new(w.X, w.Y + body);
                Vector3 thigh = new(x, body, hip.Z), ankle = new(a.X, a.Y + body, a.Z);
                bool contact = c.Contact(right, phase);
                var limits = new HoldLimits
                {
                    Hip = thigh, Reach = MathF.Max(Legs.Reach * Leg, Vector3.Distance(thigh, ankle)), Cap = Legs.MaxDrop * Leg,
                    Sink = Legs.HoldSink * Leg, Hold = Legs.HoldShare * Leg, Lift = Legs.StepLift,
                };
                held[side] = feet[side].Stage == Footing.Held;
                Vector3 off = Legs.Hold(ref feet[side], contact, c.Swing(right, phase), new Vector3(p, w.Z), MathF.PI / 2f, limits, Dt,
                                        out _, out _, out _);
                Vector2 drawn = p + new Vector2(off.X, off.Y);
                if (steady && stance && was[side]) worstSlide = MathF.Max(worstSlide, Vector2.Distance(p, p1[side]) / Dt / g);
                if (contact && n > 0 && off.Z < 1e-4f && Vector2.Distance(drawn, d1[side]) > 1e-4f && feet[side].Stage == Footing.Held
                    && held[side]) drags++;
                if (n >= 2)
                {
                    float ex = (drawn - 2f * d1[side] + d2[side]).Length() - (p - 2f * p1[side] + p2[side]).Length();
                    worstBend = MathF.Max(worstBend, ex);
                }
                was[side] = stance;
                p2[side] = p1[side];
                p1[side] = p;
                d2[side] = d1[side];
                d1[side] = drawn;
            }
        }
        return worstSlide < 0.02f && worstBend < LiftBend && drags == 0;
    }
}
