using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>The leg solve on slopes and ledges: the synthetic run (<see cref="GaitClip.SyntheticFoot"/>) on a floor whose
/// height is known everywhere, the body's reference height riding the walkmesh as the engine moves it. Oracles: the
/// hand-built floor under each foot, the synthetic stance's ankle height, and hand-written bounds.</summary>
internal static class LegsSlope
{
    /// <summary>Runs the slope regressions into <paramref name="check"/>.</summary>
    internal static void Run(Checker check)
    {
        Bob(check);
        Planted(check);
        Stand(check);
        OffSlope(check);
        LedgeDown(check);
        Steep(check);
    }

    // Given a character running up or down a slope too steep for its stride, 45 degrees
    // When it runs
    // Then its feet are never set further apart in height than the highest knee lift
    /// <summary>The synthetic run on grades of 1 up and down, at 1 and 1.5 times its pace, 2 s to 5 s: the two ankles' height
    /// deltas, less their stride warps, differ by at most the rig's lift of 0.25. Oracle: the hand-set knob.</summary>
    private static void Steep(Checker check)
    {
        foreach (float k in stackalloc[] { 1f, 1.5f })
            foreach (float grade in stackalloc[] { 1f, -1f })
            {
                var r = new SlopeRig(Terrain.Slope(grade));
                float worst = 0f;
                while (r.Time < 5f)
                {
                    r.Run(k);
                    if (r.Time < 2f) continue;
                    worst = MathF.Max(worst, MathF.Abs(r.P.DeltaR.Z - r.P.WarpR.Z - (r.P.DeltaL.Z - r.P.WarpL.Z)));
                }
                check.Expect(worst < 0.251f,
                             $"slope: a run at {k:0.0}x pace on grade {grade:0.0} sets its feet no further apart in height than the lift: {worst:0.0000} (bound 0.25)");
            }
    }

    /// <summary>World units the pelvis may rise and fall over a steady run on a slope, peak to peak: at the run's own pace,
    /// where the flat needs none, a fifth of its stance sawtooth at grade 0.3; half again as fast, where the stride warp's
    /// drop already moves it about 4 cm on the flat, a third of that sawtooth.</summary>
    private const float BobAtPace = 0.025f, BobFast = 0.06f;

    // Given a character running up or down a steady slope, at its own pace or half again as fast
    // When it runs
    // Then its pelvis travels parallel to the slope, with no hop at each step
    /// <summary>The synthetic run on grades of 0.15 and 0.3 up and down, at 1 and 1.5 times its pace, for 5 s: from 2 s on the
    /// pelvis offset's peak to peak is under <see cref="BobAtPace"/> and <see cref="BobFast"/>. Oracle: a body on a plane needs no
    /// offset that changes with the step; the hopping fault rides the stance foot, 2 x grade x half the stance travel.</summary>
    private static void Bob(Checker check)
    {
        foreach (float k in stackalloc[] { 1f, 1.5f })
            foreach (float grade in stackalloc[] { 0.15f, -0.15f, 0.3f, -0.3f })
            {
                float bob = Bobbing(grade, k), bound = k > 1f ? BobFast : BobAtPace;
                check.Expect(bob < bound,
                             $"slope: a run at {k:0.0}x pace on grade {grade:0.00} keeps its pelvis parallel to the slope: it bobs {bob:0.0000} peak to peak (bound {bound})");
            }
    }

    /// <summary>The pelvis offset's peak to peak from 2 s to 5 s of the synthetic run at <paramref name="k"/> times its pace on
    /// <paramref name="grade"/>.</summary>
    private static float Bobbing(float grade, float k)
    {
        var r = new SlopeRig(Terrain.Slope(grade));
        float lo = float.MaxValue, hi = float.MinValue;
        while (r.Time < 5f)
        {
            r.Run(k);
            if (r.Time < 2f) continue;
            lo = MathF.Min(lo, r.P.Body);
            hi = MathF.Max(hi, r.P.Body);
        }
        return hi - lo;
    }

    /// <summary>World units a planted foot's ankle may sit off its stance height over the floor in mid-stance: the leg
    /// reaching down the slope behind the body comes up a little short of it as the stance goes on.</summary>
    private const float PlantBound = 0.035f;

    // Given a character running up or down a steady slope
    // When a foot is planted
    // Then the foot rests on the slope, neither floating above it nor sunk into it
    /// <summary>The synthetic run at 1.5 times its pace on grades of 0.3 up and down, 2 s to 5 s: through the middle half of
    /// each stance each drawn ankle is within <see cref="PlantBound"/> of the floor under it plus the synthetic stance's ankle
    /// height. Oracle: the hand-built plane and the construction's ankle height.</summary>
    private static void Planted(Checker check)
    {
        foreach (float grade in stackalloc[] { 0.3f, -0.3f })
        {
            var r = new SlopeRig(Terrain.Slope(grade));
            float worst = 0f;
            while (r.Time < 5f)
            {
                r.Run(1.5f);
                if (r.Time < 2f) continue;
                for (int side = 0; side < 2; side++)
                {
                    bool right = side == 0;
                    float q = r.Through(right);
                    if (q < 0.15f || q > 0.45f) continue;
                    worst = MathF.Max(worst, MathF.Abs(r.Rest(right)));
                }
            }
            check.Expect(worst < PlantBound,
                         $"slope: a planted foot on grade {grade:0.00} rests on the slope mid-stance: {worst:0.0000} off it at worst (bound {PlantBound})");
        }
    }

    // Given a character standing still across a slope, one foot 10 cm higher than the other
    // When it stands
    // Then its pelvis settles on the lower foot and the higher foot's knee bends
    /// <summary>A standing body with every sample under its right sole 0.1 up and its left 0.1 down, 1 s: the pelvis offset is
    /// within 5 mm of -0.1. Oracle: the hand-placed floors; standing, the pelvis rides the lower foot.</summary>
    private static void Stand(Checker check)
    {
        var r = new LegRig();
        for (int n = 0; n < 60; n++) r.StandStill(LegRig.Flat(0.1f), LegRig.Flat(-0.1f));
        check.Expect(MathF.Abs(r.P.Body + 0.1f) < 0.005f,
                     $"slope: a body standing across a slope settles on its lower foot: pelvis {r.P.Body:0.0000}, the lower floor -0.1");
    }

    /// <summary>Seconds after the body's reference point leaves a slope for the flat by which its pelvis is level again.</summary>
    private const float LevelTime = 0.6f;

    // Given a character running down a slope onto flat ground
    // When it reaches the flat
    // Then its pelvis is level within a step or two
    /// <summary>The synthetic run at its pace down a 0.3 grade that ends 4 m on, then on the flat: from <see cref="LevelTime"/>
    /// after the body passes the edge its pelvis offset stays within a centimetre of 0. Oracle: on the flat the synthetic
    /// run needs no pelvis offset.</summary>
    private static void OffSlope(Checker check)
    {
        var r = new SlopeRig(Terrain.Slope(-0.3f, edge: 4f));
        float passed = -1f, worst = 0f;
        while (r.Time < 6f)
        {
            r.Run(1f);
            if (passed < 0f && r.Origin.Y >= 4f) passed = r.Time;
            if (passed >= 0f && r.Time >= passed + LevelTime) worst = MathF.Max(worst, MathF.Abs(r.P.Body));
        }
        check.Expect(passed > 0f && worst < 0.01f,
                     $"slope: a run off a slope onto the flat is level again {LevelTime} s on: pelvis {worst:0.0000} off level at worst");
    }

    /// <summary>Seconds a foot that lands on a lower floor may be drawn over a centimetre off it.</summary>
    private const float HangTime = 0.1f;

    // Given a character walking off a ledge 20 cm high
    // When its leading foot lands on the lower floor
    // Then that foot reaches the floor at once, neither hanging in the air nor sinking into it
    /// <summary>The synthetic run at its pace over a 0.2 step down 3 m on (the walkmesh ramping over half a metre): the first
    /// foot to land below is drawn over a centimetre off its stance height for under <see cref="HangTime"/> of its stance.
    /// Oracle: the hand-built ledge and the construction's ankle height.</summary>
    private static void LedgeDown(Checker check)
    {
        var r = new SlopeRig(Terrain.Ledge(-0.2f, 3f));
        float hang = 0f;
        bool landed = false, done = false;
        while (r.Time < 5f && !done)
        {
            r.Run(1f);
            for (int side = 0; side < 2 && !done; side++)
            {
                bool right = side == 0;
                if (!r.Contact(right) || r.Drawn(right).Y < 3.05f) continue;
                landed = true;
                if (r.Through(right) > 0.5f) { done = true; break; }
                if (MathF.Abs(r.Rest(right)) > 0.01f) hang += LegRig.Dt;
            }
        }
        check.Expect(landed && hang < HangTime,
                     $"slope: a foot landing past a 20 cm ledge reaches the lower floor: drawn over a centimetre off it for {hang:0.000} s (bound {HangTime})");
    }

    /// <summary>A floor known everywhere along +Y: a plane of <see cref="Grade"/> up to <see cref="Edge"/> and flat past it,
    /// plus a step of <see cref="Step"/> at <see cref="At"/> whose walkmesh ramps over <see cref="Ramp"/> either side.</summary>
    internal readonly struct Terrain
    {
        public readonly float Grade, Edge, Step, At;
        /// <summary>Half the run of the walkmesh's ramp over a step.</summary>
        public const float Ramp = 0.25f;

        private Terrain(float grade, float edge, float step, float at) { Grade = grade; Edge = edge; Step = step; At = at; }

        public static Terrain Slope(float grade, float edge = float.MaxValue) => new(grade, edge, 0f, float.MaxValue);
        public static Terrain Ledge(float step, float at) => new(0f, float.MaxValue, step, at);

        /// <summary>The floor's height at <paramref name="y"/>.</summary>
        public float Floor(float y) => Grade * MathF.Min(y, Edge) + (y >= At ? Step : 0f);

        /// <summary>The walkmesh's height at <paramref name="y"/>: where the engine sets the body.</summary>
        public float Mesh(float y)
        {
            float w = Math.Clamp((y - At + Ramp) / (2f * Ramp), 0f, 1f);
            return Grade * MathF.Min(y, Edge) + Step * w;
        }
    }

    /// <summary>The synthetic run of <see cref="LegRig"/> along +Y over a <see cref="Terrain"/>: each frame's floors are cast
    /// from the terrain under each drawn foot and its landing, and the reference height rides the walkmesh.</summary>
    internal sealed class SlopeRig
    {
        public LegState S;
        public LegPose P;
        public float Time, Phase, RateNext = 1f;
        public Vector3 Origin, AnimR, AnimL, ThighR, ThighL;
        private readonly Terrain _t;
        private bool _first = true;

        public SlopeRig(Terrain t) { _t = t; Origin = new Vector3(0f, 0f, t.Mesh(0f)); }

        /// <summary>One running frame at <paramref name="k"/> times the clip's pace, the engine pacing the run to the ground speed.</summary>
        public void Run(float k)
        {
            float rate = k * RateNext;
            Phase += rate * LegRig.Dt;
            Time += LegRig.Dt;
            float y = Origin.Y + k * LegRig.Speed * LegRig.Dt, z = _t.Mesh(y);
            float rise = z - Origin.Z;
            Origin = new Vector3(0f, y, z);
            float q = Phase - MathF.Floor(Phase);
            AnimR = Origin + GaitClip.SyntheticFoot(q, LegRig.Side, LegRig.Floor, LegRig.Speed, LegRig.SwingLift);
            AnimL = Origin + GaitClip.SyntheticFoot(q + 0.5f, -LegRig.Side, LegRig.Floor, LegRig.Speed, LegRig.SwingLift);
            LegFrame f = default;
            f.Dt = LegRig.Dt;
            f.Tracked = !_first;
            f.Knobs = new LegKnobs
            {
                Stride = true, FootIk = true, MaxSlope = MathF.Tan(30f * MathF.PI / 180f), Lift = 0.25f, Mass = true, Diagonal = true, Pivot = true,
            };
            f.Origin = Origin;
            f.Facing = Quaternion.Identity;
            f.FwdH = Vector3.UnitY;
            f.Right = Vector3.UnitX;
            f.Scale = 1f;
            f.Weight = 1f;
            f.Vel = new Vector2(0f, k * LegRig.Speed);
            f.Ground = k * LegRig.Speed;
            f.Loco = f.Paced = f.Hips = true;
            f.Anim = 4;
            f.Rise = rise;
            f.RAnkle = AnimR; f.LAnkle = AnimL;
            f.RThigh = ThighR = Origin + new Vector3(LegRig.Side, 0f, LegRig.HipZ);
            f.LThigh = ThighL = Origin + new Vector3(-LegRig.Side, 0f, LegRig.HipZ);
            f.Walk = new LegWalk
            {
                Present = true, Weight = 1f, Strides = Math.Clamp(k / rate, StrideWarp.WarpMin, StrideWarp.WarpMax), Dir = Vector3.UnitY,
                LeadDir = Vector3.UnitY, Speed = LegRig.Speed, EngineRate = k, Rate = rate, Phase = q, Length = 1f, Cyclic = true,
                Authored = true, Changed = _first, FloorR = LegRig.Floor, FloorL = LegRig.Floor, LegLength = LegRig.LegLength,
                DownR = 0f, DownL = 0.5f,
                LandR = new Vector3(LegRig.Side, LegRig.Speed * LegRig.Stance * 0.5f, LegRig.Floor),
                LandL = new Vector3(-LegRig.Side, LegRig.Speed * LegRig.Stance * 0.5f, LegRig.Floor),
                ContactR = Down(q), ContactL = Down(q + 0.5f), SwingR = Swing(q), SwingL = Swing(q + 0.5f),
            };
            Legs.Turn(ref S, f, out P);
            Legs.Stride(ref S, f, ref P);
            Cast(ref f.FloorR, P.FootR, P.AheadR);
            Cast(ref f.FloorL, P.FootL, P.AheadL);
            Legs.Place(ref S, f, ref P);
            RateNext = P.RateNext;
            _first = false;
        }

        private void Cast(ref FootFloor floor, Vector3 foot, StrideWarp.Ahead ahead)
        {
            floor = default;
            floor.Add(new FloorSample(true, _t.Floor(foot.Y) - Origin.Z));
            if (!ahead.Swing) floor.Add(new FloorSample(true, _t.Floor(foot.Y + 0.1f) - Origin.Z));
            floor.Land = ahead.Swing ? new FloorSample(true, _t.Floor(ahead.At.Y) - Origin.Z) : default;
            floor.NewF = floor.NewS = true;
            floor.SlopeF = (_t.Floor(foot.Y + 0.1f) - _t.Floor(foot.Y)) / 0.1f;
        }

        /// <summary>The foot is on the ground this frame, by the synthetic walk's construction.</summary>
        public bool Contact(bool right) => Down(right ? Phase : Phase + 0.5f);

        /// <summary>How far through its cycle the foot is (0..1, under <see cref="LegRig.Stance"/> on the ground).</summary>
        public float Through(bool right)
        {
            float q = right ? Phase : Phase + 0.5f;
            return q - MathF.Floor(q);
        }

        /// <summary>Where the ankle is drawn (world): the animated ankle carried by the pelvis, then moved by its delta, no
        /// further from the hip joint than the leg's length or the clip's own reach this frame (the IK cannot stretch a leg).</summary>
        public Vector3 Drawn(bool right)
        {
            Vector3 up = new(0f, 0f, P.Body), anim = right ? AnimR : AnimL, thigh = right ? ThighR : ThighL;
            Vector3 hip = thigh + up, d = anim + (right ? P.DeltaR : P.DeltaL) + up - hip;
            float leg = MathF.Max(LegRig.LegLength, Vector3.Distance(anim, thigh));
            return hip + (d.Length() > leg ? Vector3.Normalize(d) * leg : d);
        }

        /// <summary>How far the drawn ankle sits above its stance height over the floor under it (below is negative).</summary>
        public float Rest(bool right)
        {
            Vector3 a = Drawn(right);
            return a.Z - _t.Floor(a.Y) - LegRig.Floor;
        }

        private static bool Down(float phase) => phase - MathF.Floor(phase) < LegRig.Stance;

        private static float Swing(float phase)
        {
            float q = phase - MathF.Floor(phase);
            return q < LegRig.Stance ? 1f : (q - LegRig.Stance) / (1f - LegRig.Stance);
        }
    }
}
