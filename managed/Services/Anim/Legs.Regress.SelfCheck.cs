using System;
using System.Numerics;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>The leg solve's regressions: approved examples kept green and found faults pinned, on the synthetic rig
/// (<see cref="LegRig"/>) and whole synthetic frames. Oracles: the analytic synthetic walk, hand-worked geometry, and the
/// frame with the corrections absent.</summary>
internal static class LegsRegress
{
    /// <summary>Runs every regression; each logs a FAIL line naming its example when it no longer holds.</summary>
    internal static void Run(ExtenderLog log)
    {
        var check = new Checker(log, "Legs.Regress.SelfCheck");
        E3(check);
        E5(check);
        E8(check);
        Gap(check);
        StopYaw(check);
        PhaseOrder(check);
        FreshFeet(check);
        RebaseAim(check);
        SnapSettle(check);
        Paused(check);
        StepSilent(check);
        AimWeighted(check);
        StopLifted(check);
        NoFloor(check);
        FootFloorCheck.Run(log);
        log.Info(check.Ok ? "Legs.Regress.SelfCheck: passed" : "Legs.Regress.SelfCheck: FAILED");
    }

    /// <summary>World units an ankle or the pelvis may stray from the animation and still be drawn as animated.</summary>
    private const float AsAnimated = 1e-4f;

    // Given a character running on flat ground at exactly the pace its run animation was made for
    // When it runs
    // Then its hips and knees are drawn as with Procedural animations off
    /// <summary>The synthetic run at its own pace for 4 s: every frame is the frame with the corrections absent, within a
    /// tenth of a millimetre. Oracle: an unwarped walk on flat ground needs no correction.</summary>
    private static void E3(Checker check)
    {
        var r = new LegRig();
        r.Knobs.Diagonal = r.Knobs.Pivot = false;
        int off = 0, frames = 0;
        while (r.Time < 4f)
        {
            r.Run(1f);
            frames++;
            if (!r.AsOff(AsAnimated)) off++;
        }
        check.Expect(frames > 200 && off == 0, $"E3 a run at its own pace is drawn as animated: {off} of {frames} frames corrected");
    }

    // Given a character standing with one foot on a step 20 cm higher than the other
    // When it stands
    // Then the higher foot rests on the step with its knee bent, and the other leg is straight
    /// <summary>A standing body, every sample under its right sole 0.20 up and its left on the floor: the pelvis is not
    /// lowered, the right ankle is drawn 0.20 up within a centimetre with its leg shorter by over 5 cm, and the left leg
    /// keeps its animated length. Oracle: the hand-placed step.</summary>
    private static void E5(Checker check)
    {
        var r = new LegRig();
        for (int n = 0; n < 60; n++) r.StandStill(LegRig.Flat(0.2f), LegRig.Flat(0f));
        float rise = r.Drawn(true).Z - r.AnimR.Z, bendR = r.Shortened(true), bendL = r.Shortened(false);
        check.Expect(r.P.Body > -LegRig.Still && MathF.Abs(rise - 0.2f) < 0.01f && bendR > 0.05f && MathF.Abs(bendL) < LegRig.Still,
                     $"E5 a foot on a 20 cm step rests on it with its knee bent and the other leg straight: pelvis {r.P.Body:0.0000}, right foot up {rise:0.0000}, legs shortened {bendR:0.0000} right and {bendL:0.0000} left");
    }

    // Given a character running straight ahead
    // When it runs
    // Then its hips face forward and its legs are drawn as with Procedural animations off
    /// <summary>The synthetic run straight ahead at its own pace with the diagonal turn and the pivot on: the hips never
    /// turn off the facing and every frame is the frame with the corrections absent. Oracle: a straight run has no
    /// diagonal and no change of heading to turn the hips by.</summary>
    private static void E8(Checker check)
    {
        var r = new LegRig();
        int off = 0, frames = 0;
        float turn = 0f;
        while (r.Time < 4f)
        {
            r.Run(1f);
            frames++;
            turn = MathF.Max(turn, MathF.Abs(r.P.Turn));
            if (!r.AsOff(AsAnimated)) off++;
        }
        check.Expect(frames > 200 && turn < AsAnimated && off == 0,
                     $"E8 a straight run faces its hips forward and is drawn as animated: hips turned {turn:0.0000}, {off} of {frames} frames corrected");
    }

    /// <summary>A walk of the synthetic run at its own pace on flat ground, seen 0.6 s later 0.9 further on with no frame
    /// between (an untracked frame): no held foot or settle is carried over the gap, each foot drawn within a centimetre of
    /// the animation. Oracle: a foot seen anew starts on the animation; the analytic walk needs no correction.</summary>
    private static void Gap(Checker check)
    {
        var w = new Walker();
        while (w.T < 1f) w.Run();
        w.Skip(0.6f);
        w.Run(tracked: false);
        float off = MathF.Max(w.P.WarpR.Length(), w.P.WarpL.Length());
        check.Expect(off < 0.01f, $"regress: a body seen again after a gap starts its feet on the animation: drawn {off:0.0000} off it");
    }

    /// <summary>The synthetic run round a 1 rad/s curve stopped while a held foot is turned: no frame turns either foot by
    /// over 0.05 rad, and a second on both are back on the animation's direction. Oracle: a foot does not spin in a frame;
    /// a stood body's feet face its animation.</summary>
    private static void StopYaw(Checker check)
    {
        var r = new LegRig();
        while (r.Time < 2.5f) r.Run(1f, yawRate: 1f);
        float held = MathF.Max(MathF.Abs(r.P.YawR), MathF.Abs(r.P.YawL)), prevR = r.P.YawR, prevL = r.P.YawL, jump = 0f;
        Vector3 ar = r.AnimR, al = r.AnimL, tr = r.ThighR, tl = r.ThighL;
        float stop = r.Time;
        while (r.Time < stop + 1f)
        {
            r.Stand(ar, al, tr, tl, LegRig.Flat(0f), LegRig.Flat(0f), stance: true);
            jump = MathF.Max(jump, MathF.Max(MathF.Abs(r.P.YawR - prevR), MathF.Abs(r.P.YawL - prevL)));
            prevR = r.P.YawR; prevL = r.P.YawL;
        }
        float left = MathF.Max(MathF.Abs(r.P.YawR), MathF.Abs(r.P.YawL));
        check.Expect(held > 0.2f && jump < 0.05f && left < 0.01f,
                     $"regress: a stop eases a held foot's turn out: held {held:0.000} rad, largest step {jump:0.000}, {left:0.000} left a second on");
    }

    /// <summary>The synthetic run at 1.6 times its rate playing the forward-right diagonal, stepped the adapter's way (the turn
    /// on a frame with no walk yet, then the stride and the floors on the whole frame) and as <see cref="Legs.Step"/> on the
    /// same whole frames from the same start: the hip turn is the same every frame. Oracle: one solve, two call orders.</summary>
    private static void PhaseOrder(Checker check)
    {
        var a = new Walker { Rate = 1.6f };
        var b = new Walker { Rate = 1.6f };
        float worst = 0f;
        Vector2 diag = new(1.7f, 1.7f);
        for (int n = 0; n < 60; n++)
        {
            LegFrame f = a.Next(diag, anim: 0x57, swapped: true); // not-an-offset: the engine's diagonal run id
            b.Next(diag, anim: 0x57, swapped: true); // not-an-offset: the engine's diagonal run id
            LegFrame bare = f;
            bare.Walk = default;
            Legs.Turn(ref a.S, bare, out LegPose pa);
            Legs.Stride(ref a.S, f, ref pa);
            Legs.Place(ref a.S, f, ref pa);
            Legs.Step(ref b.S, f, out LegPose pb);
            worst = MathF.Max(worst, MathF.Abs(pa.Turn - pb.Turn));
        }
        check.Expect(worst < 1e-6f && MathF.Abs(b.S.Turn) > 0.1f,
                     $"regress: the adapter's phase order turns the hips as the whole step does: {worst:0.000000} rad apart, turned {b.S.Turn:0.000}");
    }

    /// <summary>The synthetic run met for the first time travelling sideways to its legs, one foot planted and one half
    /// way through its swing: neither foot is drawn off the animation that frame. Oracle: a foot met anew has no aim or
    /// hold to carry yet; the analytic walk at its own pace needs no correction.</summary>
    private static void FreshFeet(Checker check)
    {
        var w = new Walker { T = 0.3f };
        w.Run(new Vector2(1.5f, 0f));
        float off = MathF.Max(w.P.WarpR.Length(), w.P.WarpL.Length());
        check.Expect(off < 1e-3f, $"regress: feet met anew start on the animation: drawn {off:0.0000} off it");
    }

    /// <summary>Two walks of the synthetic run travelling sideways to its legs (so the swing is aimed), the second told its
    /// clip changed while its left foot is half way through a swing: that frame both draw the left foot within a centimetre
    /// of each other. Oracle: the same walk without the clip change.</summary>
    private static void RebaseAim(Checker check)
    {
        var a = new Walker();
        var b = new Walker();
        Vector2 side = new(1.5f, 0f);
        float apart = 0f, aimed = 0f;
        while (a.T < 1.3f)
        {
            bool change = a.T > 1.25f && a.T < 1.25f + Walker.Dt;
            a.Run(side);
            b.Run(side, changed: change);
            if (change) { apart = Vector3.Distance(a.P.WarpL, b.P.WarpL); aimed = a.P.WarpL.Length(); }
        }
        check.Expect(aimed > 0.05f && apart < 0.01f,
                     $"regress: a clip change mid-swing keeps the swing's aim: {apart:0.0000} apart of an aim of {aimed:0.0000}");
    }

    /// <summary>The synthetic run stopped into a stand, then seen 10 units away on a teleport mid-settle: that frame and the
    /// next each foot is drawn within a centimetre of the animation. Oracle: a teleport carries nothing over.</summary>
    private static void SnapSettle(Checker check)
    {
        var w = new Walker();
        while (w.T < 1f) w.Run();
        for (int n = 0; n < 6; n++) w.Stand();
        bool settling = w.S.SettleStage != Settle.None;
        w.Origin += new Vector3(10f, 0f, 0f);
        w.Stand(snap: true);
        float first = MathF.Max(w.P.WarpR.Length(), w.P.WarpL.Length());
        w.Stand();
        float second = MathF.Max(w.P.WarpR.Length(), w.P.WarpL.Length());
        check.Expect(settling && first < 0.01f && second < 0.01f,
                     $"regress: a teleport mid-settle carries no offset: {first:0.0000} then {second:0.0000}");
    }

    /// <summary>A pelvis dropped 0.05 with a long stride's need, and a settle running, each given a frame of no time (a
    /// pause): the drop and the settle are where they were. Oracle: a frame of no time changes nothing.</summary>
    private static void Paused(Checker check)
    {
        LegState s = default;
        s.Drop = 0.05f;
        LegFrame f = default;
        f.Tracked = true;
        f.Scale = 1f;
        f.Walk.Present = true;
        f.Walk.LegLength = 0.9f;
        f.RThigh = new Vector3(0.1f, 0f, 0.85f);
        f.LThigh = new Vector3(-0.1f, 0f, 0.85f);
        f.RAnkle = new Vector3(0.1f, 0f, 0.08f);
        f.LAnkle = new Vector3(-0.1f, 0f, 0.08f);
        LegPose p = default;
        p.Legs = p.TiltR = p.TiltL = Quaternion.Identity;
        p.WarpR = new Vector3(0f, 0.5f, 0f);
        Legs.Place(ref s, f, ref p);
        float drop = p.Drop;

        var w = new Walker();
        while (w.T < 1f) w.Run();
        for (int n = 0; n < 6; n++) w.Stand();
        byte stage = w.S.SettleStage;
        Vector3 warp = w.P.WarpR;
        w.Stand(dt: 0f);
        check.Expect(MathF.Abs(drop - 0.05f) < 1e-6f && stage != Settle.None && w.S.SettleStage == stage && Vector3.Distance(warp, w.P.WarpR) < 1e-5f,
                     $"regress: a paused frame holds the pelvis and the settle: drop {drop:0.0000}, settle stage {stage} then {w.S.SettleStage}");
    }

    /// <summary>The synthetic run round a 4 rad/s curve, where holds are let go and stepped: a foot planted is reported only
    /// on the frames the clip lands a foot. Oracle: the analytic contacts, one landing per foot per cycle.</summary>
    private static void StepSilent(Checker check)
    {
        var r = new LegRig();
        bool wasR = false, wasL = false, stepped = false;
        int landings = 0, touchdowns = 0;
        while (r.Time < 4f)
        {
            r.Run(1f, yawRate: 4f);
            bool onR = r.Contact(true), onL = r.Contact(false);
            if (r.Time > 0.5f)
            {
                if (onR && !wasR || onL && !wasL) landings++;
                if (r.P.Touchdown) touchdowns++;
            }
            stepped |= r.S.FootingR.Stage == Footing.Stepping || r.S.FootingL.Stage == Footing.Stepping;
            wasR = onR; wasL = onL;
        }
        check.Expect(stepped && touchdowns == landings,
                     $"regress: a let-go step makes no footfall: {touchdowns} touchdowns for {landings} landings");
    }

    /// <summary>One walk of the synthetic run travelling sideways to its legs, the left foot aimed mid-swing, given its
    /// next frame at full walk weight and at half: at half every offset is half. Oracle: a correction fades with the walk's weight.</summary>
    private static void AimWeighted(Checker check)
    {
        var w = new Walker();
        Vector2 side = new(1.5f, 0f);
        while (w.T < 1.2f) w.Run(side);
        LegState s = w.S;
        LegFrame full = w.Next(side), half = full;
        half.Walk.Weight = half.Walk.Strides = 0.5f;
        LegState sa = s, sb = s;
        Legs.Step(ref sa, full, out LegPose pa);
        Legs.Step(ref sb, half, out LegPose pb);
        float miss = Vector3.Distance(pb.WarpL, 0.5f * pa.WarpL);
        check.Expect(pa.WarpL.Length() > 0.01f && miss < 1e-4f,
                     $"regress: a swing's aim fades with the walk: at half weight {miss:0.00000} off half of {pa.WarpL.Length():0.0000}");
    }

    /// <summary>The synthetic run stopped while its right foot is drawn 3 cm up (a let-go hold's step) on its stance spot
    /// and its left 0.3 off its own, so the left steps and the right is held: the right's drawn height moves by under 3 mm
    /// a frame. Oracle: a foot in the air does not drop to the floor in a frame.</summary>
    private static void StopLifted(Checker check)
    {
        var w = new Walker();
        while (w.T < 1f) w.Run();
        w.S.WarpR = new Vector3(0f, 0f, 0.03f);
        w.S.WarpL = new Vector3(0f, 0.3f, 0f);
        float prev = 0.03f, jump = 0f;
        for (int n = 0; n < 30; n++)
        {
            w.Stand();
            jump = MathF.Max(jump, MathF.Abs(w.P.WarpR.Z - prev));
            prev = w.P.WarpR.Z;
        }
        check.Expect(jump < 0.003f && MathF.Abs(prev) < 1e-3f,
                     $"regress: a held foot in the air at a stop comes down over frames: largest drop {jump:0.0000} a frame, {prev:0.0000} up at the end");
    }

    /// <summary>Feet with no floor in range under either one, the samples 0.5 off: both target the reference height.
    /// Oracle: no floor is the reference height, not a floor out of range.</summary>
    private static void NoFloor(Checker check)
    {
        LegFrame f = default;
        f.Scale = 1f;
        f.Knobs.FootIk = true;
        f.FloorR.Add(new FloorSample(false, 0.5f));
        f.FloorL.Add(new FloorSample(false, 0.5f));
        Legs.Targets(f, default, out bool hr, out float tr, out bool hl, out float tl);
        check.Expect(!hr && !hl && tr == 0f && tl == 0f, $"regress: no floor under either foot is the reference height: {tr:0.000} and {tl:0.000}");
    }

    /// <summary>The synthetic run (<see cref="GaitClip.SyntheticFoot"/>, 1 s cycle at rate 1, 1.5 units/s, 0.9 legs) as whole
    /// frames: its legs on the facing, the walk digested by hand, floors level; or standing on its last ankles.</summary>
    private sealed class Walker
    {
        internal const float Dt = 1f / 60f;
        public LegState S;
        public LegPose P;
        public float T, Rate = 1f;
        public Vector3 Origin, StandR, StandL;
        private bool _first = true;

        /// <summary>Steps <paramref name="seconds"/> of the run with no frame drawn.</summary>
        public void Skip(float seconds)
        {
            T += seconds;
            Origin += new Vector3(0f, LegRig.Speed * Rate * seconds, 0f);
        }

        /// <summary>The next running frame, travelling <paramref name="vel"/> (world; along the legs by default).</summary>
        public LegFrame Next(Vector2? vel = null, int anim = 4, bool swapped = false, bool tracked = true, bool changed = false)
        {
            T += Dt;
            Vector2 v = vel ?? new Vector2(0f, LegRig.Speed * Rate);
            Origin += new Vector3(0f, LegRig.Speed * Rate * Dt, 0f);
            float q = T * Rate - MathF.Floor(T * Rate);
            LegFrame f = Base(tracked && !_first, false, Dt);
            f.Vel = v;
            f.Ground = v.Length();
            f.Anim = anim;
            f.Swapped = swapped;
            f.Loco = f.Paced = true;
            f.RAnkle = Origin + GaitClip.SyntheticFoot(q, LegRig.Side, LegRig.Floor, LegRig.Speed, LegRig.SwingLift);
            f.LAnkle = Origin + GaitClip.SyntheticFoot(q + 0.5f, -LegRig.Side, LegRig.Floor, LegRig.Speed, LegRig.SwingLift);
            StandR = f.RAnkle;
            StandL = f.LAnkle;
            _standAt = Origin;
            f.Walk = new LegWalk
            {
                Present = true, Weight = 1f, Strides = 1f, Dir = Vector3.UnitY, LeadDir = Vector3.UnitY, Speed = LegRig.Speed,
                EngineRate = Rate, Rate = Rate, Phase = q, Length = 1f, Cyclic = true, Authored = true, Changed = _first || changed,
                FloorR = LegRig.Floor, FloorL = LegRig.Floor, LegLength = LegRig.LegLength, DownR = 0f, DownL = 0.5f,
                LandR = new Vector3(LegRig.Side, 0.45f, LegRig.Floor), LandL = new Vector3(-LegRig.Side, 0.45f, LegRig.Floor),
                ContactR = Stance(q), ContactL = Stance(q + 0.5f), SwingR = Swing(q), SwingL = Swing(q + 0.5f),
            };
            _first = false;
            return f;
        }

        /// <summary>One running frame through <see cref="Legs.Step"/>.</summary>
        public void Run(Vector2? vel = null, bool tracked = true, bool changed = false)
            => Legs.Step(ref S, Next(vel, tracked: tracked, changed: changed), out P);

        /// <summary>One standing frame on the run's last ankles, its idle's stance spots those ankles.</summary>
        public void Stand(bool snap = false, float dt = Dt)
        {
            T += dt;
            LegFrame f = Base(!snap, snap, dt);
            f.Standing = true;
            f.RAnkle = StandR + (Origin - _standAt);
            f.LAnkle = StandL + (Origin - _standAt);
            f.HasStance = true;
            f.StanceR = f.RAnkle;
            f.StanceL = f.LAnkle;
            Legs.Step(ref S, f, out P);
        }

        private Vector3 _standAt;

        private LegFrame Base(bool tracked, bool snap, float dt)
        {
            LegFrame f = default;
            f.Dt = dt;
            f.Tracked = tracked;
            f.Snap = snap;
            f.Knobs = new LegKnobs { Stride = true, FootIk = true, MaxSlope = 0.5f, Lift = 0.25f, Mass = true, Diagonal = true, Pivot = true };
            f.Origin = Origin;
            f.Facing = Quaternion.Identity;
            f.FwdH = Vector3.UnitY;
            f.Right = Vector3.UnitX;
            f.Scale = 1f;
            f.Weight = 1f;
            f.Hips = true;
            f.RThigh = Origin + new Vector3(LegRig.Side, 0f, LegRig.HipZ);
            f.LThigh = Origin + new Vector3(-LegRig.Side, 0f, LegRig.HipZ);
            f.FloorR = LegRig.Flat(0f);
            f.FloorL = LegRig.Flat(0f);
            return f;
        }

        private static bool Stance(float phase) => phase - MathF.Floor(phase) < LegRig.Stance;

        private static float Swing(float phase)
        {
            float q = phase - MathF.Floor(phase);
            return q < LegRig.Stance ? 1f : (q - LegRig.Stance) / (1f - LegRig.Stance);
        }
    }
}
