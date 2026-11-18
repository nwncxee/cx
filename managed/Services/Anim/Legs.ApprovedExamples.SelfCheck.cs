using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>Hands the pose self-check's approved-examples slot to <see cref="LegsApprovedExamples"/>.</summary>
internal static partial class Legs
{
    static partial void ApprovedExamples(ExtenderLog log) => LegsApprovedExamples.Run(log);
}

/// <summary>The leg solve's approved examples, each driven through <see cref="Legs"/> with synthetic frames built from
/// the analytic synthetic walk (<see cref="GaitClip.SyntheticFoot"/>). Oracles: that walk's construction, hand-worked
/// geometry, and the frame with the corrections absent (the animated pose).</summary>
internal static class LegsApprovedExamples
{
    /// <summary>Runs every example; each logs a FAIL line naming it when its Then does not hold.</summary>
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "Legs.ApprovedExamples.SelfCheck");
        E1(check);
        E2(check);
        E4(check);
        E6(check);
        E7(check);
        E9(check);
        E10(check);
        log.Info(check.Ok ? "Legs.ApprovedExamples.SelfCheck: passed" : "Legs.ApprovedExamples.SelfCheck: FAILED");
    }

    // Given a character running on flat ground faster than its run animation was made for
    // When it has kept that pace for two seconds
    // Then its hips come back up between the long steps instead of staying lowered for the whole run
    /// <summary>The synthetic run at 1.6 times its pace for 2 s and one more cycle: over that last cycle the pelvis rises
    /// to within a fifth of its deepest drop. Oracle: the analytic walk, whose single support needs no drop at all.</summary>
    private static void E1(Checker check)
    {
        var r = new LegRig();
        const float K = 1.6f;
        float deepest = 0f, highest = float.MinValue, cycle = 0f;
        while (r.Time < 2f) { r.Run(K); deepest = MathF.Max(deepest, -r.P.Body); }
        while (cycle < 1f)
        {
            float before = r.Phase;
            r.Run(K);
            cycle += r.Phase - before;
            deepest = MathF.Max(deepest, -r.P.Body);
            highest = MathF.Max(highest, r.P.Body);
        }
        check.Expect(deepest > 0.005f && highest >= -0.2f * deepest,
                     $"E1 the hips come back up between long steps: deepest drop {deepest:0.0000}, highest over the last cycle {highest:0.0000} (wants >= {-0.2f * deepest:0.0000})");
    }

    // Given a character that was running and has stopped on flat ground
    // When it has stood for half a second
    // Then its legs are as straight as with Procedural animations off
    /// <summary>The synthetic run at 1.6 times its pace, then a cycle at its own pace (no stride left for the settle to
    /// step from), then a stop with the idle's feet where the run's were animated: 0.5 s on, the pelvis is where it is
    /// animated and each leg as long as animated. Oracle: the frame with the corrections absent.</summary>
    private static void E2(Checker check)
    {
        var r = new LegRig();
        while (r.Time < 2f) r.Run(1.6f);
        while (r.Time < 3f) r.Run(1f);
        Vector3 animR = r.AnimR, animL = r.AnimL, thighR = r.ThighR, thighL = r.ThighL;
        float stop = r.Time;
        while (r.Time < stop + 0.5f) r.Stand(animR, animL, thighR, thighL, LegRig.Flat(0f), LegRig.Flat(0f), stance: true);
        float bendR = r.Shortened(true), bendL = r.Shortened(false);
        check.Expect(MathF.Abs(r.P.Body) < LegRig.Still && r.P.Shift.Length() < LegRig.Still
                     && MathF.Abs(bendR) < LegRig.Still && MathF.Abs(bendL) < LegRig.Still,
                     $"E2 half a second after a stop the legs are straight as animated: pelvis {r.P.Body:0.0000} up, {r.P.Shift.Length():0.0000} aside, legs shortened {bendR:0.0000} right and {bendL:0.0000} left");
    }

    // Given a character standing on a flat floor with one foot over a crack narrower than the foot
    // When it stands
    // Then it does not sink and neither knee bends
    /// <summary>A standing body whose right sole has one sample 0.4 down a crack among level ones, the crack sample first
    /// and then third: the pelvis stays at the floor and both legs keep their animated length. Oracle: the level floor
    /// the rest of the sole stands on, so the frame with the corrections absent.</summary>
    private static void E4(Checker check)
    {
        CrackStand(check, 0);
        CrackStand(check, 2);
    }

    private static void CrackStand(Checker check, int at)
    {
        var r = new LegRig();
        FootFloor crack = default;
        for (int i = 0; i < 4; i++) crack.Add(new FloorSample(true, i == at ? -0.4f : 0f));
        for (int n = 0; n < 60; n++) r.StandStill(crack, LegRig.Flat(0f));
        float bendR = r.Shortened(true), bendL = r.Shortened(false);
        check.Expect(r.P.Body > -LegRig.Still && MathF.Abs(bendR) < LegRig.Still && MathF.Abs(bendL) < LegRig.Still,
                     $"E4 a foot over a crack (sample {at} of 4 in it) neither sinks nor bends a knee: pelvis {r.P.Body:0.0000}, legs shortened {bendR:0.0000} right and {bendL:0.0000} left");
    }

    // Given a character running diagonally forward and left
    // When the player switches to diagonally forward and right
    // Then its hips and legs come round over about one step and do not swing past the new direction
    /// <summary>The synthetic run swapped from forward-left to forward-right at its own pace (one step = half its 1 s
    /// cycle): a quarter step after the switch the hips are under half way round, by one step they face the new
    /// diagonal within 3°, and they never pass it by a degree. Oracle: the engine's diagonals, 45° each side.</summary>
    private static void E6(Checker check)
    {
        var r = new LegRig();
        const int RunFl = 0x56, RunFr = 0x57; // not-an-offset: the engine's diagonal run ids
        Vector2 left = 1.5f * new Vector2(-MathF.Sqrt(0.5f), MathF.Sqrt(0.5f)), right = 1.5f * new Vector2(MathF.Sqrt(0.5f), MathF.Sqrt(0.5f));
        while (r.Time < 1f) r.Run(1f, anim: RunFl, swapped: true, vel: left);
        float switched = r.Time, quarter = float.NaN, step = float.NaN, past = 0f;
        float to = -MathF.PI / 4f, from = MathF.PI / 4f;
        while (r.Time < switched + 1f)
        {
            r.Run(1f, anim: RunFr, swapped: true, vel: right);
            float since = r.Time - switched;
            if (float.IsNaN(quarter) && since >= 0.125f) quarter = (from - r.P.Turn) / (from - to);
            if (float.IsNaN(step) && since >= 0.5f) step = r.P.Turn;
            past = MathF.Max(past, to - r.P.Turn);
        }
        float deg = MathF.PI / 180f;
        check.Expect(quarter < 0.5f && MathF.Abs(step - to) < 3f * deg && past < deg,
                     $"E6 a diagonal switch comes round over a step: {quarter:P0} round at a quarter step (wants under half), {(step - to) / deg:0.0}° off at one step, {past / deg:0.00}° past the new diagonal");
    }

    // Given a character running a weave, steered left and right about once a second
    // When it runs
    // Then each foot lands on the line the body is travelling, not out to the side of it
    /// <summary>Two weaves at the run's own pace: the facing swung 0.4 sin(2 pi t), and the diagonals forward-left and
    /// forward-right switched each second, a quarter cycle off a landing, with the hips turning: at every landing after 1 s the foot
    /// sits its own side's 0.1 off the line of travel, within 2 cm. Oracle: the clip's own lateral offset, by construction.</summary>
    private static void E7(Checker check)
    {
        (float facing, int a) = Weave(false);
        (float diagonal, int b) = Weave(true);
        check.Expect(a >= 4 && b >= 4 && facing < 0.02f && diagonal < 0.02f,
                     $"E7 a weaving run lands each foot on its travel line: worst {facing:0.0000} aside over {a} landings steering the facing, {diagonal:0.0000} over {b} switching diagonals");
    }

    /// <summary>The worst sideways miss of a landing from the line of travel, past the foot's own side offset, and the
    /// landings counted: steering the facing, or switching the diagonal run with the facing held.</summary>
    private static (float Worst, int Landings) Weave(bool diagonals)
    {
        const int RunFl = 0x56, RunFr = 0x57; // not-an-offset: the engine's diagonal run ids
        const float A = 0.4f;
        var r = new LegRig();
        float h = MathF.Sqrt(0.5f) * LegRig.Speed;
        Span<bool> was = stackalloc bool[2];
        float worst = 0f;
        int landings = 0;
        while (r.Time < 4f)
        {
            float t = r.Time + LegRig.Dt;
            Vector3 travel;
            if (diagonals)
            {
                bool left = ((int)(t + 0.75f) & 1) == 1;
                Vector2 v = new(left ? -h : h, h);
                r.Run(1f, anim: left ? RunFl : RunFr, swapped: true, vel: v);
                travel = Vector3.Normalize(Vector3.Transform(new Vector3(v, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, r.Heading)));
            }
            else
            {
                r.Run(1f, yawRate: (A * MathF.Sin(2f * MathF.PI * t) - r.Heading) / LegRig.Dt);
                travel = new Vector3(-MathF.Sin(r.Heading), MathF.Cos(r.Heading), 0f);
            }
            Vector3 rightAxis = new(travel.Y, -travel.X, 0f);
            for (int side = 0; side < 2; side++)
            {
                bool right = side == 0, on = r.Contact(right);
                if (on && !was[side] && r.Time > 1f)
                {
                    float aside = Vector3.Dot(r.Drawn(right) - r.Origin, rightAxis) - (right ? LegRig.Side : -LegRig.Side);
                    worst = MathF.Max(worst, MathF.Abs(aside));
                    landings++;
                }
                was[side] = on;
            }
        }
        return (worst, landings);
    }

    // Given a character running round a curve
    // When one of its feet is on the ground
    // Then that foot neither slides nor swivels until it lifts
    /// <summary>The synthetic run at its own pace round a 1 rad/s curve: through each stance after the first second
    /// the drawn foot stays within a centimetre of where it landed and turns by under 2°, its world direction being the
    /// legs' heading plus its own turn. Oracle: a foot on the ground does not move, the analytic contacts.</summary>
    private static void E9(Checker check)
    {
        var r = new LegRig();
        const float Rate = 1f;
        Span<bool> was = stackalloc bool[2];
        Span<Vector2> landed = stackalloc Vector2[2];
        Span<float> faced = stackalloc float[2];
        float slide = 0f, swivel = 0f;
        int stances = 0;
        while (r.Time < 4f)
        {
            r.Run(1f, yawRate: Rate);
            for (int side = 0; side < 2; side++)
            {
                bool right = side == 0, on = r.Contact(right);
                Vector3 drawn = r.Drawn(right);
                float dir = r.Heading + r.P.Turn + (right ? r.P.YawR : r.P.YawL);
                if (on && !was[side]) { landed[side] = new Vector2(drawn.X, drawn.Y); faced[side] = dir; if (r.Time > 1f) stances++; }
                else if (on && r.Time > 1f)
                {
                    slide = MathF.Max(slide, Vector2.Distance(new Vector2(drawn.X, drawn.Y), landed[side]));
                    swivel = MathF.Max(swivel, MathF.Abs(MathF.IEEERemainder(dir - faced[side], 2f * MathF.PI)));
                }
                was[side] = on;
            }
        }
        check.Expect(stances >= 4 && slide < 0.01f && swivel < 2f * MathF.PI / 180f,
                     $"E9 a planted foot on a curve neither slides nor swivels over {stances} stances: slid {slide:0.0000}, turned {swivel * 180f / MathF.PI:0.0}°");
    }

    // Given a character running a curve so tight that a planted foot is left far behind the body
    // When the leg would have to stretch to keep the foot there
    // Then the foot lets go and steps forward, and the leg is never drawn stretched or the knee locked
    /// <summary>The synthetic run at its own pace round a 4 rad/s curve, where holding a landed foot would need a leg
    /// longer than the clip's: no frame drags a grounded foot along the floor, and no leg is drawn longer than the clip's
    /// own most extended frame. Oracle: the synthetic walk's geometry, sampled by hand over its cycle.</summary>
    private static void E10(Checker check)
    {
        var r = new LegRig();
        float longest = LegRig.LongestAnimated(), worst = 0f, reach = 0f;
        Span<bool> was = stackalloc bool[2];
        Span<Vector3> landed = stackalloc Vector3[2], prev = stackalloc Vector3[2];
        int drags = 0;
        while (r.Time < 4f)
        {
            r.Run(1f, yawRate: 4f);
            for (int side = 0; side < 2; side++)
            {
                bool right = side == 0, on = r.Contact(right);
                Vector3 drawn = r.Drawn(right), hip = r.Hip(right);
                worst = MathF.Max(worst, Vector3.Distance(drawn, hip));
                if (on && !was[side]) landed[side] = (right ? r.AnimR : r.AnimL);
                if (on) reach = MathF.Max(reach, Vector3.Distance(landed[side], right ? r.ThighR : r.ThighL));
                bool grounded = drawn.Z < LegRig.AnkleHeight + 0.005f;
                if (on && was[side] && grounded && Vector2.Distance(new Vector2(drawn.X, drawn.Y), new Vector2(prev[side].X, prev[side].Y)) > 0.001f) drags++;
                was[side] = on;
                prev[side] = drawn;
            }
        }
        check.Expect(reach > LegRig.LegLength && drags == 0 && worst <= longest + 0.001f,
                     $"E10 a foot left behind on a tight curve lets go rather than stretch: holding it needs {reach:0.000} of a {LegRig.LegLength:0.00} leg, {drags} frames dragged a grounded foot, longest leg drawn {worst:0.000} against the clip's {longest:0.000}");
    }
}

/// <summary>One synthetic body for the leg solve's examples: the synthetic walk's legs (hips 0.85 up, feet 0.1 either
/// side, 0.9 long legs, 1 s cycle, 1.5 units/s at its own pace) posed on a path, stepped through <see cref="Legs"/> the
/// way the adapter steps it: the turn, the ankles posed through the turned legs, the stride, the floors.</summary>
internal sealed class LegRig
{
    internal const float Dt = 1f / 60f, Speed = 1.5f, Floor = 0.08f, SwingLift = 0.12f, Stance = 0.6f, HipZ = 0.85f, Side = 0.1f;
    /// <summary>The synthetic leg's length and its ankle's height over the floor it stands on.</summary>
    internal const float LegLength = 0.9f, AnkleHeight = Floor;
    /// <summary>World units a pelvis or a leg length may differ from the animation's and still be as animated.</summary>
    internal const float Still = 0.002f;
    private const int RunId = 4, StandId = 0;

    public LegState S;
    public LegPose P;
    public float Time, Phase, Heading, RateNext = 1f;
    public Vector3 Origin, AnimR, AnimL, ThighR, ThighL;
    private bool _first = true;
    private bool _contactR, _contactL;

    public LegKnobs Knobs = new()
    {
        Stride = true, FootIk = true, MaxSlope = MathF.Tan(30f * MathF.PI / 180f), Lift = 0.25f, Mass = true, Diagonal = true, Pivot = true,
    };

    /// <summary>One running frame at <paramref name="k"/> times the clip's pace, the engine pacing the run to the ground
    /// speed, turning at <paramref name="yawRate"/>; travelling along the facing unless <paramref name="vel"/> says (model frame).</summary>
    public void Run(float k, int anim = RunId, bool swapped = false, Vector2? vel = null, float yawRate = 0f)
    {
        float engine = k, rate = engine * RateNext;
        Phase += rate * Dt;
        Heading += yawRate * Dt;
        Quaternion facing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Heading);
        float ground = k * Speed;
        Vector2 local = vel ?? new Vector2(0f, ground);
        Vector3 v = Vector3.Transform(new Vector3(local, 0f), facing);
        Origin += v * Dt;
        Time += Dt;

        LegFrame f = Frame(facing, anim, swapped, new Vector2(v.X, v.Y), yawRate);
        f.Ground = ground;
        f.Loco = f.Paced = true;
        Legs.Turn(ref S, f, out P);
        float q = Phase - MathF.Floor(Phase);
        Pose(P.Legs, GaitClip.SyntheticFoot(q, Side, Floor, Speed, SwingLift), GaitClip.SyntheticFoot(q + 0.5f, -Side, Floor, Speed, SwingLift));
        f.RAnkle = AnimR; f.LAnkle = AnimL; f.RThigh = ThighR; f.LThigh = ThighL;
        _contactR = Grounded(q);
        _contactL = Grounded(q + 0.5f);
        f.Walk = new LegWalk
        {
            Present = true, Weight = 1f, Strides = Math.Clamp(k / rate, StrideWarp.WarpMin, StrideWarp.WarpMax), Dir = Vector3.UnitY,
            LeadDir = Vector3.UnitY, Speed = Speed, EngineRate = engine, Rate = rate, Phase = q, Length = 1f, Cyclic = true,
            Authored = true, Changed = _first, FloorR = Floor, FloorL = Floor, LegLength = LegLength, DownR = 0f, DownL = 0.5f,
            LandR = new Vector3(Side, Speed * Stance * 0.5f, Floor), LandL = new Vector3(-Side, Speed * Stance * 0.5f, Floor),
            ContactR = _contactR, ContactL = _contactL, SwingR = Swing(q), SwingL = Swing(q + 0.5f),
        };
        f.FloorR = Flat(0f);
        f.FloorL = Flat(0f);
        Finish(ref f);
    }

    /// <summary>One standing frame at the rig's origin with the stock stance (feet 0.1 either side under the hips).</summary>
    public void StandStill(FootFloor r, FootFloor l)
    {
        Quaternion facing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Heading);
        Pose(facing, new Vector3(Side, 0f, Floor), new Vector3(-Side, 0f, Floor));
        Stand(AnimR, AnimL, ThighR, ThighL, r, l, stance: false, facing);
    }

    /// <summary>One standing frame with the animated ankles and thighs given (world); <paramref name="stance"/>: the idle's
    /// stance spots are those ankles, as the adapter samples them for a settle.</summary>
    public void Stand(Vector3 animR, Vector3 animL, Vector3 thighR, Vector3 thighL, FootFloor r, FootFloor l, bool stance,
                      Quaternion? facing = null)
    {
        Time += Dt;
        LegFrame f = Frame(facing ?? Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Heading), StandId, false, Vector2.Zero, 0f);
        f.Standing = true;
        Legs.Turn(ref S, f, out P);
        AnimR = animR; AnimL = animL; ThighR = thighR; ThighL = thighL;
        f.RAnkle = animR; f.LAnkle = animL; f.RThigh = thighR; f.LThigh = thighL;
        f.HasStance = stance;
        f.StanceR = animR; f.StanceL = animL;
        f.FloorR = r;
        f.FloorL = l;
        _contactR = _contactL = true;
        Finish(ref f);
    }

    private LegFrame Frame(Quaternion facing, int anim, bool swapped, Vector2 vel, float yawRate)
    {
        LegFrame f = default;
        f.Dt = Dt;
        f.Tracked = !_first;
        f.Knobs = Knobs;
        f.Origin = Origin;
        f.Facing = facing;
        Legs.Axes(facing, out _, out f.FwdH, out f.Right);
        f.Scale = 1f;
        f.Weight = 1f;
        f.Vel = vel;
        f.YawRate = yawRate;
        f.Anim = anim;
        f.Swapped = swapped;
        f.Hips = true;
        return f;
    }

    private void Pose(Quaternion legs, Vector3 footR, Vector3 footL)
    {
        AnimR = Origin + Vector3.Transform(footR, legs);
        AnimL = Origin + Vector3.Transform(footL, legs);
        ThighR = Origin + Vector3.Transform(new Vector3(Side, 0f, HipZ), legs);
        ThighL = Origin + Vector3.Transform(new Vector3(-Side, 0f, HipZ), legs);
    }

    private void Finish(ref LegFrame f)
    {
        Legs.Stride(ref S, f, ref P);
        Legs.Place(ref S, f, ref P);
        RateNext = P.RateNext;
        _first = false;
    }

    /// <summary>The foot is on the ground this frame, by the synthetic walk's construction.</summary>
    public bool Contact(bool right) => right ? _contactR : _contactL;

    /// <summary>The pelvis offset this frame (world): the horizontal shift and the height.</summary>
    public Vector3 Offset => new(P.Shift.X, P.Shift.Y, P.Body);

    /// <summary>Where the ankle is drawn (world): the animated ankle carried by the pelvis, then moved by its delta --
    /// the adapter writes the root before the leg IK.</summary>
    public Vector3 Drawn(bool right) => (right ? AnimR + P.DeltaR : AnimL + P.DeltaL) + Offset;

    /// <summary>Where the hip joint is drawn (world): the animated thigh carried by the pelvis.</summary>
    public Vector3 Hip(bool right) => (right ? ThighR : ThighL) + Offset;

    /// <summary>How much shorter the drawn leg is than the animated one (hip to ankle, world units): over 0 is a knee bent
    /// further than animated, 0 a leg as animated.</summary>
    public float Shortened(bool right)
        => Vector3.Distance(right ? AnimR : AnimL, right ? ThighR : ThighL) - Vector3.Distance(Drawn(right), Hip(right));

    /// <summary>The frame with the corrections absent: the pelvis, every ankle and foot direction as animated, no turn.</summary>
    public bool AsOff(float eps)
        => MathF.Abs(P.Body) < eps && P.Shift.Length() < eps && P.DeltaR.Length() < eps && P.DeltaL.Length() < eps
           && MathF.Abs(P.YawR) < eps && MathF.Abs(P.YawL) < eps && MathF.Abs(P.Turn) < eps && !P.Ik;

    /// <summary>The longest hip-to-ankle reach the synthetic walk draws over its cycle, sampled finely.</summary>
    public static float LongestAnimated()
    {
        float most = 0f;
        for (int i = 0; i <= 1000; i++)
            most = MathF.Max(most, Vector3.Distance(GaitClip.SyntheticFoot(i / 1000f, Side, Floor, Speed, SwingLift), new Vector3(Side, 0f, HipZ)));
        return most;
    }

    /// <summary>A level floor <paramref name="h"/> off the reference height under all four sole samples, the landing and
    /// both slopes.</summary>
    public static FootFloor Flat(float h)
    {
        FootFloor floor = default;
        for (int i = 0; i < LegFrame.Samples; i++) floor.Add(new FloorSample(true, h));
        floor.Land = new FloorSample(true, h);
        floor.NewF = floor.NewS = true;
        return floor;
    }

    private static bool Grounded(float phase) => phase - MathF.Floor(phase) < Stance;

    private static float Swing(float phase)
    {
        float q = phase - MathF.Floor(phase);
        return q < Stance ? 1f : (q - Stance) / (1f - Stance);
    }
}
