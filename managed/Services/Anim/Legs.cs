using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>
/// The leg solve with no engine in it: one body's <see cref="LegFrame"/> in, its <see cref="LegPose"/> out, its
/// <see cref="LegState"/> carried. The stages run in the pose layer's order -- the hip turn (<see cref="Diagonal"/>),
/// the stride warp and settle (<see cref="StrideWarp"/>, <see cref="Settle"/>), then the floors (<see cref="FootPlacement"/>)
/// and the pelvis (<see cref="Mass"/>). <b>Hot</b>, no allocation.
/// </summary>
internal static partial class Legs
{
    static partial void ApprovedExamples(ExtenderLog log);
    static partial void Regressions(ExtenderLog log);

    /// <summary>Runs the leg examples the build carries: the approved ones, then the regressions.</summary>
    internal static void Examples(ExtenderLog log)
    {
        ApprovedExamples(log);
        Regressions(log);
    }

    /// <summary>A whole frame at once: every phase in order, for a frame whose ankles are the turned ones and whose
    /// floors are already sampled.</summary>
    internal static void Step(ref LegState s, in LegFrame f, out LegPose p)
    {
        Turn(ref s, f, out p);
        Stride(ref s, f, ref p);
        Place(ref s, f, ref p);
    }

    /// <summary>The hip turn and the legs' frame it gives. The adapter writes the turn before it reads the ankles.</summary>
    internal static void Turn(ref LegState s, in LegFrame f, out LegPose p)
    {
        p = default;
        p.RateNext = 1f;
        p.TiltR = p.TiltL = Quaternion.Identity;
        p.Turned = Diagonal.Step(ref s, f);
        p.Turn = s.Turn;
        p.Legs = f.Facing;
        if (p.Turned) p.Legs *= Quaternion.CreateFromAxisAngle(Vector3.UnitZ, s.Turn);
        Axes(p.Legs, out p.LegFwd, out p.LegFwdH, out p.LegRight);
    }

    /// <summary>The stride warp, the plants and a stop's or a turn's settle: where each foot is drawn before its floor,
    /// and where a swinging one lands. The adapter casts the floors at those points.</summary>
    internal static void Stride(ref LegState s, in LegFrame f, ref LegPose p)
    {
        StrideWarp.Step(ref s, f, ref p, out Vector3 warpR, out Vector3 warpL, out float drop);
        p.Drop = drop;
        // the engine drops the walk on a stop and the warp lets go of it: the settle takes the feet over where they were drawn
        if (s.SettleLoco && !f.Loco) { warpR = s.WarpR; warpL = s.WarpL; }
        s.WarpR = warpR; s.WarpL = warpL;
        Vector3 rStance = f.HasStance ? f.StanceR : f.RAnkle + warpR, lStance = f.HasStance ? f.StanceL : f.LAnkle + warpL;
        Settle.Feet(ref s, f.Knobs.Stride && !f.Gated && !f.Snap && f.Dt > 0f, f.Loco, f.RAnkle + warpR, f.LAnkle + warpL,
                    rStance, lStance, f.Fade, f.Standing, f.YawRate, f.Scale, f.Dt, out Vector3 setR, out Vector3 setL);
        warpR += setR;
        warpL += setL;
        p.WarpR = warpR; p.WarpL = warpL;
        p.FootR = f.RAnkle + warpR; p.FootL = f.LAnkle + warpL;
    }

    /// <summary>The floors and the pelvis: each foot's eased floor offset and tilt, the pelvis offset under the lower
    /// foot with the dip and the drop, and each ankle's delta for the leg IK.</summary>
    internal static void Place(ref LegState s, in LegFrame f, ref LegPose p)
    {
        FootPlacement.Ease(ref s, f, p, out FootPlacement.Feet feet);
        float body = MathF.Min(feet.OffR, feet.OffL) + Mass.Step(ref s, f.Cue, f.Rise, f.Scale, f.Knobs.Mass, f.Gated, f.Snap, f.Dt) - p.Drop;
        p.Body = body;
        p.TiltR = feet.TiltR;
        p.TiltL = feet.TiltL;
        p.Ik = !f.Gated && (MathF.Abs(feet.OffR - body) + MathF.Abs(feet.OffL - body) > FootPlacement.MinLift
                            || !feet.TiltR.IsIdentity || !feet.TiltL.IsIdentity
                            || p.WarpR.LengthSquared() + p.WarpL.LengthSquared() > FootPlacement.MinLift * FootPlacement.MinLift);
        float cap = f.Knobs.Lift * f.Scale;
        p.DeltaR = p.WarpR + new Vector3(0f, 0f, MathF.Min(feet.OffR - body, cap));
        p.DeltaL = p.WarpL + new Vector3(0f, 0f, MathF.Min(feet.OffL - body, cap));
    }

    /// <summary>Foot placement runs this frame: <c>anim.footik</c> on and the animation not gated.</summary>
    internal static bool Placing(in LegFrame f) => f.Knobs.FootIk && !f.Gated;

    /// <summary>Feet are laid on their slopes this frame.</summary>
    internal static bool Tilting(in LegFrame f) => Placing(f) && f.Knobs.MaxSlope > 1e-4f;

    /// <summary>Each foot's floor target off the reference height, and whether it has a floor of its own: a foot with
    /// none in range borrows the other's, and a swinging one aims toward the floor it lands on.</summary>
    internal static void Targets(in LegFrame f, in LegPose p, out bool hr, out float tr, out bool hl, out float tl)
    {
        hr = hl = false;
        tr = tl = 0f;
        if (!Placing(f)) return;
        FloorSample r = f.FloorR.First, l = f.FloorL.First;
        hr = r.Hit; tr = r.Off;
        hl = l.Hit; tl = l.Off;
        // a foot with no floor in range borrows the other foot's: "none" is not "at the walkmesh"
        if (!hr && hl) tr = tl; else if (hr && !hl) tl = tr;
        if (p.AheadR.Swing && f.FloorR.Land.Hit) tr = FootPlacement.SwingTarget(tr, f.FloorR.Land.Off, p.AheadR.U);
        if (p.AheadL.Swing && f.FloorL.Land.Hit) tl = FootPlacement.SwingTarget(tl, f.FloorL.Land.Off, p.AheadL.U);
    }

    /// <summary>The model's forward in the world, its horizontal unit, and the horizontal right, for the gob-frame
    /// rotation <paramref name="adj"/>.</summary>
    internal static void Axes(Quaternion adj, out Vector3 fwd, out Vector3 fwdH, out Vector3 right)
    {
        fwd = Vector3.Transform(Vector3.UnitY, adj);
        fwdH = new Vector3(fwd.X, fwd.Y, 0f);
        fwdH = fwdH.LengthSquared() > 1e-8f ? Vector3.Normalize(fwdH) : Vector3.UnitY;
        right = new Vector3(fwdH.Y, -fwdH.X, 0f);
    }

    /// <summary>The synthetic walk's own stance speed, floor, swing height and stance share, known by construction
    /// (<see cref="GaitClip.SyntheticFoot"/>).</summary>
    private const float WalkSpeed = 1.5f, WalkFloor = 0.08f, WalkLift = 0.12f, WalkStance = 0.6f;

    /// <summary>The synthetic walk (<see cref="GaitClip.SyntheticFoot"/>) stepped 4 s at 60 fps at its own pace on flat
    /// floors: every ankle delta and the pelvis offset stay zero. Oracle: the analytic rule that an unwarped walk on flat
    /// ground needs no correction; the contacts and the digest are the construction's hand-written values, not the analysis.</summary>
    internal static bool SelfCheck()
    {
        const float Dt = 1f / 60f;
        LegState s = default;
        float worst = 0f, body = 0f;
        int touchdowns = 0, iks = 0;
        for (int n = 0; n < 240; n++)
        {
            float t = n * Dt, phase = t - MathF.Floor(t);
            Vector3 origin = new(0f, WalkSpeed * t, 0f);
            LegFrame f = default;
            f.Dt = Dt;
            f.Knobs = new LegKnobs { Stride = true, FootIk = true, MaxSlope = 0.5f, Lift = 0.25f, Mass = true };
            f.Origin = origin;
            f.Facing = Quaternion.Identity;
            f.FwdH = Vector3.UnitY;
            f.Right = Vector3.UnitX;
            f.Scale = 1f;
            f.Ground = WalkSpeed;
            f.Weight = 1f;
            f.Vel = new Vector2(0f, WalkSpeed);
            f.Loco = f.Paced = f.Hips = true;
            f.RAnkle = origin + GaitClip.SyntheticFoot(phase, 0.1f, WalkFloor, WalkSpeed, WalkLift);
            f.LAnkle = origin + GaitClip.SyntheticFoot(phase + 0.5f, -0.1f, WalkFloor, WalkSpeed, WalkLift);
            f.RThigh = origin + new Vector3(0.1f, 0f, 0.85f);
            f.LThigh = origin + new Vector3(-0.1f, 0f, 0.85f);
            f.Walk = new LegWalk
            {
                Present = true, Weight = 1f, Strides = 1f, Dir = Vector3.UnitY, LeadDir = Vector3.UnitY, Speed = WalkSpeed,
                EngineRate = 1f, Rate = 1f, Phase = phase, Length = 1f, Cyclic = true, Authored = true, Changed = n == 0,
                FloorR = WalkFloor, FloorL = WalkFloor, LegLength = 0.9f, DownR = 0f, DownL = 0.5f,
                LandR = new Vector3(0.1f, 0.45f, WalkFloor), LandL = new Vector3(-0.1f, 0.45f, WalkFloor),
                ContactR = Stance(phase), ContactL = Stance(phase + 0.5f), SwingR = Swing(phase), SwingL = Swing(phase + 0.5f),
            };
            Flat(ref f.FloorR);
            Flat(ref f.FloorL);
            Step(ref s, f, out LegPose p);
            worst = MathF.Max(worst, MathF.Max(p.DeltaR.Length(), p.DeltaL.Length()));
            body = MathF.Max(body, MathF.Abs(p.Body));
            if (p.Touchdown) touchdowns++;
            if (p.Ik) iks++;
        }
        return s.Planting && touchdowns >= 6 && worst < 1e-4f && body < 1e-6f && iks == 0;
    }

    /// <summary>The synthetic foot is on the ground for the first <see cref="WalkStance"/> of its cycle.</summary>
    private static bool Stance(float phase) => phase - MathF.Floor(phase) < WalkStance;

    /// <summary>The synthetic foot's progress through its swing: time through it, 1 on the ground.</summary>
    private static float Swing(float phase)
    {
        float q = phase - MathF.Floor(phase);
        return q < WalkStance ? 1f : (q - WalkStance) / (1f - WalkStance);
    }

    /// <summary>A level floor at the reference height under the foot, under its landing and on both slopes.</summary>
    private static void Flat(ref FootFloor floor)
    {
        floor.Add(new FloorSample(true, 0f));
        floor.Land = new FloorSample(true, 0f);
        floor.NewF = floor.NewS = true;
        floor.SlopeF = floor.SlopeS = 0f;
    }
}
