using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>The feet's regressions: how far a foot is ever drawn off its warped animation, and a walk the engine plays
/// backwards. Oracles: hand-worked bounds and paces, and the synthetic walk's own construction.</summary>
internal static class LegsFeetRegress
{
    /// <summary>Runs every foot regression into <paramref name="check"/>.</summary>
    internal static void Run(Checker check)
    {
        LetGoFollows(check);
        BoundedRuns(check);
        Backward(check);
        BackwardParts(check);
    }

    /// <summary>The farthest a foot of the synthetic 0.9 leg may be drawn off its warped animation, world units: 0.12 of
    /// the leg, the share a hold may sit off it before it is let go, worked by hand.</summary>
    private const float Bound = 0.108f;
    /// <summary>World units of float rounding allowed past a bound.</summary>
    private const float Eps = 1e-4f;

    /// <summary>A held foot whose animation point runs on at 4 units/s for 1 s at 120 fps: every hold it cannot keep is
    /// let go, each step ends with the foot on its animation point as it is then, and no frame draws it further off than
    /// <see cref="Bound"/>. Oracle: the hand-worked bound, and a step's end on the animation.</summary>
    private static void LetGoFollows(Checker check)
    {
        const float Dt = 1f / 120f, Pace = 4f;
        var r = new HoldLimits { Reach = 5f, Cap = 0.108f, Sink = 0.018f, Hold = Bound, Lift = 0.04f };
        Footing f = default;
        f.Stage = Footing.Swing;
        f.Aired = true;
        int lets = 0, steps = 0;
        float worst = 0f, ended = 0f;
        for (int n = 0; n < 120; n++)
        {
            float y = Pace * n * Dt;
            r.Hip = new Vector3(0f, y, 0.85f);
            byte was = f.Stage;
            Vector3 off = Legs.Hold(ref f, true, 1f, new Vector3(0f, y, 0.08f), MathF.PI / 2f, r, Dt, out _, out _, out bool let);
            float flat = new Vector2(off.X, off.Y).Length();
            worst = MathF.Max(worst, flat);
            if (let) lets++;
            if (was == Footing.Stepping && f.Stage == Footing.Held) { steps++; ended = MathF.Max(ended, flat); }
        }
        check.Expect(lets >= 2 && steps >= 2 && ended < Eps && worst <= Bound + Eps,
                     $"feet: a hold let go at run pace steps back onto the animation: {lets} let go, {steps} steps ended, worst {ended:0.0000} off at a step's end, worst {worst:0.0000} off any frame (bound {Bound})");
    }

    /// <summary>The synthetic run 6 s on the rig, the pace ramping from the clip's own to 1.6 times it over 2 s and held,
    /// travelling straight ahead, along a hip-turned forward diagonal and straight sideways to its legs: no foot is drawn
    /// further off its warped animation than <see cref="Bound"/>. Oracle: the hand-worked bound.</summary>
    private static void BoundedRuns(Checker check)
    {
        float d = MathF.Sqrt(0.5f);
        float ahead = Worst(4, false, new Vector2(0f, 1f)), diagonal = Worst(0x56, true, new Vector2(-d, d)), // not-an-offset: anim id
              side = Worst(0x4f, false, new Vector2(1f, 0f)); // not-an-offset: anim id
        check.Expect(ahead <= Bound + Eps && diagonal <= Bound + Eps && side <= Bound + Eps,
                     $"feet: a ramping run keeps its feet within {Bound} of the animation: {ahead:0.0000} ahead, {diagonal:0.0000} diagonal, {side:0.0000} sideways");
    }

    /// <summary>The farthest either foot is drawn off its warped animation over a ramped run along <paramref name="way"/>
    /// (model frame, unit).</summary>
    private static float Worst(int anim, bool swapped, Vector2 way)
    {
        var r = new LegRig();
        float worst = 0f;
        while (r.Time < 6f)
        {
            float k = 1f + 0.6f * MathF.Min(r.Time / 2f, 1f);
            r.Run(k, anim, swapped, way * (k * LegRig.Speed));
            worst = MathF.Max(worst, MathF.Max(Flat(r.S.FootingR.Off), Flat(r.S.FootingL.Off)));
        }
        return worst;
    }

    private static float Flat(Vector3 v) => new Vector2(v.X, v.Y).Length();

    /// <summary>The pieces of a walk played backwards against hand-worked values: the stride ratio from the rate's size, a
    /// reversed run fresh near its clip's end, a backwards start just short of the front foot's lift-off, swing progress
    /// counted the other way, and the synthetic walk's lift-offs (right at 0.6, left at 0.1). Oracle: hand arithmetic and
    /// the synthetic walk's construction.</summary>
    private static void BackwardParts(Checker check)
    {
        bool ratio = MathF.Abs(StrideWarp.Ratio(true, 2f, 1.1f, -1.4f) - 1.298701f) < 1e-4f
                     && MathF.Abs(StrideWarp.Ratio(true, 2f, 1.1f, 1.4f) - 1.298701f) < 1e-4f
                     && StrideWarp.Ratio(false, 2f, 1.1f, -1.4f) == 1f && StrideWarp.Ratio(true, 2f, 1.1f, 0f) == 1f;
        bool begun = StrideWarp.Begun(0.99f, -1.4f, 1f) && !StrideWarp.Begun(0.5f, -1f, 1f) && StrideWarp.Begun(0.05f, 1f, 1f)
                     && !StrideWarp.Begun(0.05f, -1f, 1f);
        bool start = MathF.Abs(StrideWarp.StartBack(true, 0.6f, 0.1f) - 0.54f) < 1e-5f
                     && MathF.Abs(StrideWarp.StartBack(false, 0.6f, 0.1f) - 0.04f) < 1e-5f;
        bool played = StrideWarp.Played(true, 1f, true) == 1f && StrideWarp.Played(false, 0.25f, true) == 0.75f
                      && StrideWarp.Played(false, 0.25f, false) == 0.25f;
        GaitClip walk = GaitClip.Synthetic(LegRig.HipZ, LegRig.Floor, LegRig.Speed, LegRig.SwingLift);
        const float Edge = 3f / GaitClip.Samples;
        bool ups = MathF.Abs(walk.UpR - 0.6f) < Edge && MathF.Abs(walk.UpL - 0.1f) < Edge;
        check.Expect(ratio && begun && start && played && ups,
                     $"feet: the pieces of a backwards walk: stride ratio {ratio}, fresh at the end {begun}, start {start}, swing progress {played}, lift-offs {walk.UpR:0.000} / {walk.UpL:0.000}");
    }

    /// <summary>The synthetic walk played at the engine's rate -1.4, backing off at 1.4 times its pace: the next frame plays
    /// at 1/sqrt(1.4) of the engine's rate, a stance foot stays put, a swing keeps its side (no aim), a foot lifted 0.05 off
    /// lands on the animation, and a step takes half a cycle at its speed. Oracle: the square-root split and the synthetic
    /// walk's construction, worked by hand.</summary>
    private static void Backward(Checker check)
    {
        var w = new Backer();
        float slide = 0f, wide = 0f, carried = 0f, landedOff = -1f, rateNext = 0f;
        bool nudged = false, wasOn = true;
        Vector2 landedAt = default;
        while (w.T < 4f)
        {
            w.Step();
            bool on = Backer.On(w.Q);
            Vector2 drawn = new(w.P.FootR.X, w.P.FootR.Y);
            if (w.T > 0.5f && w.T < 1.5f) rateNext = MathF.Max(rateNext, MathF.Abs(w.P.RateNext - 0.845154f));
            if (on && !wasOn) landedAt = drawn;
            else if (on && w.T > 0.5f && !nudged) slide = MathF.Max(slide, Vector2.Distance(drawn, landedAt));
            if (!on && !nudged && w.T > 0.5f) wide = MathF.Max(wide, MathF.Abs(w.P.FootR.X - w.Origin.X - LegRig.Side));
            if (!nudged && w.T > 1.5f && on && w.S.FootingR.Stage == Footing.Held && w.Q is > 0.02f and < 0.05f)
            {
                w.S.FootingR.At += new Vector2(0.05f, 0f);
                nudged = true;
            }
            if (nudged && !on && wasOn && landedOff < 0f) carried = Flat(w.S.FootingR.Off0);
            if (nudged && on && !wasOn && landedOff < 0f) landedOff = Flat(w.S.FootingR.Off);
            wasOn = on;
        }
        bool stepped = Diagonal.StepTime(new LegWalk { Present = true, Length = 1f, Rate = -2f }) == 0.25f;
        check.Expect(rateNext < 1e-3f && slide < 0.01f && wide < 0.005f && carried > 0.04f && landedOff is >= 0f and < 0.002f && stepped,
                     $"feet: a walk played backwards is paced, planted and eased like a forward one: rate share off by {rateNext:0.0000}, stance slid {slide:0.0000}, swing off its side {wide:0.0000}, lifted {carried:0.0000} off and landed {landedOff:0.0000} off, step time {(stepped ? "half a cycle" : "the fallback")}");
    }

    /// <summary>The synthetic walk on flat ground played backwards at the engine's rate -1.4 times the solve's own
    /// rate share, the body backing off at 1.4 times the clip's pace; the run starts at the clip's end, as the engine
    /// starts a reversed run.</summary>
    private sealed class Backer
    {
        private const float Dt = 1f / 60f, Engine = -1.4f, Pace = 1.4f;
        public LegState S;
        public LegPose P;
        public float T, Q = 0.99f, RateNext = 1f;
        public Vector3 Origin;
        private bool _first = true;

        /// <summary>One frame through <see cref="Legs.Step"/>.</summary>
        public void Step()
        {
            float rate = Engine * RateNext, ground = Pace * LegRig.Speed;
            Q += rate * Dt;
            Q -= MathF.Floor(Q);
            T += Dt;
            Origin -= new Vector3(0f, ground * Dt, 0f);
            LegFrame f = default;
            f.Dt = Dt;
            f.Since = Dt;
            f.Tracked = !_first;
            f.Knobs = new LegKnobs { Stride = true, FootIk = true, MaxSlope = 0.5f, Lift = 0.25f, Mass = true, Diagonal = true, Pivot = true };
            f.Origin = Origin;
            f.Facing = Quaternion.Identity;
            f.FwdH = Vector3.UnitY;
            f.Right = Vector3.UnitX;
            f.Scale = 1f;
            f.Weight = 1f;
            f.Hips = true;
            f.Anim = 3;
            f.Vel = new Vector2(0f, -ground);
            f.Ground = ground;
            f.Loco = f.Paced = true;
            f.RThigh = Origin + new Vector3(LegRig.Side, 0f, LegRig.HipZ);
            f.LThigh = Origin + new Vector3(-LegRig.Side, 0f, LegRig.HipZ);
            f.RAnkle = Origin + GaitClip.SyntheticFoot(Q, LegRig.Side, LegRig.Floor, LegRig.Speed, LegRig.SwingLift);
            f.LAnkle = Origin + GaitClip.SyntheticFoot(Q + 0.5f, -LegRig.Side, LegRig.Floor, LegRig.Speed, LegRig.SwingLift);
            f.FloorR = LegRig.Flat(0f);
            f.FloorL = LegRig.Flat(0f);
            f.Walk = new LegWalk
            {
                Present = true, Weight = 1f, Strides = Math.Clamp(Pace / MathF.Abs(rate), StrideWarp.WarpMin, StrideWarp.WarpMax),
                Dir = Vector3.UnitY, LeadDir = Vector3.UnitY, Speed = LegRig.Speed, EngineRate = Engine, Rate = rate, Phase = Q,
                Length = 1f, Cyclic = true, Authored = true, Changed = _first, FloorR = LegRig.Floor, FloorL = LegRig.Floor,
                LegLength = LegRig.LegLength, DownR = 0f, DownL = 0.5f,
                LandR = new Vector3(LegRig.Side, LegRig.Speed * LegRig.Stance * 0.5f, LegRig.Floor),
                LandL = new Vector3(-LegRig.Side, LegRig.Speed * LegRig.Stance * 0.5f, LegRig.Floor),
                ContactR = On(Q), ContactL = On(Q + 0.5f), SwingR = Swing(Q), SwingL = Swing(Q + 0.5f),
            };
            Legs.Step(ref S, f, out P);
            RateNext = P.RateNext;
            _first = false;
        }

        /// <summary>The synthetic foot at clip phase <paramref name="phase"/> is on the ground.</summary>
        public static bool On(float phase) => phase - MathF.Floor(phase) < LegRig.Stance;

        /// <summary>The clip's own swing progress at <paramref name="phase"/>, as read forwards: time through the swing.</summary>
        private static float Swing(float phase)
        {
            float q = phase - MathF.Floor(phase);
            return q < LegRig.Stance ? 1f : (q - LegRig.Stance) / (1f - LegRig.Stance);
        }
    }
}
