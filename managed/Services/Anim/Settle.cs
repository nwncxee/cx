using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Engine;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Stops and turns on the spot through the feet, a <see cref="PoseService"/> module under <c>anim.stride</c>. The engine
/// crossfades a stop and turns a standing body whole, so the feet glide or spin: here they keep their world points, and
/// one at a time steps onto its stance spot (a stop's feet, a turn's standing ones). <b>Hot</b>, no allocation.
/// </summary>
internal static unsafe class Settle
{
    /// <summary>Seconds a shift's step takes from lift to touchdown: nearly twice a walk's own swing, a weight shift
    /// and not a stride. It starts this long before the crossfade lands, so the foot reaches its stance spot with it.</summary>
    private const float StepTime = 0.55f;
    /// <summary>World units (times scale) the farther foot may stop off its stance spot and still shift over late, its
    /// step landing with the fade; past it the feet step up at once, and past <see cref="CatchGap"/> -- a run caught
    /// mid-stride, the feet a stride apart -- they step quicker still.</summary>
    private const float StepUpGap = 0.2f, CatchGap = 0.45f;
    /// <summary>Seconds a step takes stepping up and catching: a stride brought to a halt, not a weight shift.</summary>
    private const float StepUpTime = 0.4f, CatchTime = 0.3f;
    /// <summary>World units (times scale) a step lifts at most, however long: a stride's swing clears the floor by this.</summary>
    private const float MaxLift = 0.08f;
    /// <summary>Seconds a settle waits for a crossfade it cannot see the end of before stepping anyway: longer
    /// than the longest stock idle transtime (1.00 s, female), so a readable fade always decides.</summary>
    private const float MaxWait = 1.5f;
    /// <summary>World units (times scale) the stepping foot rises at the top of its arc: the toe clears the floor
    /// and no more.</summary>
    private const float Lift = 0.025f;
    /// <summary>Step length (world units, times scale) at which the arc reaches its full <see cref="Lift"/>: a
    /// short adjustment slides on the ball of the foot, a stride's worth lifts clear.</summary>
    private const float LiftFull = 0.3f;
    /// <summary>World units (times scale) the weighted foot may end off its stance spot without a second step:
    /// a foot's width. Under it the remainder drifts out instead.</summary>
    private const float Adjust = 0.1f;
    /// <summary>Seconds the leftover under <see cref="Adjust"/> drifts out over -- slow enough to read as the
    /// weight settling, not a slide.</summary>
    private const float EaseTime = 0.6f;
    /// <summary>World units under which the easing leftover is done.</summary>
    private const float Done = 0.002f;
    /// <summary>Seconds a turn's step takes: quicker than a stop's, a pivot on the ball of the foot.</summary>
    private const float TurnStepTime = 0.35f;
    /// <summary>Radians per second of facing change past which a standing body's feet are held and stepped round.</summary>
    private const float TurnRate = 0.6f;
    /// <summary>World units (times scale) a held foot may be twisted off its stance spot before it steps.</summary>
    private const float TurnSlack = 0.08f;
    /// <summary>Seconds a turn holds its feet at most before the first step, however small the twist.</summary>
    private const float TurnWait = 0.3f;
    /// <summary>Settle stages: none running, the feet held until a step is due (a stop's crossfade, a turn's twist), the
    /// first and second steps, the leftover easing out.</summary>
    internal const byte None = 0, Waiting = 1, First = 2, Second = 3, Easing = 4;

    internal static int Steps;

    /// <summary>Seconds the run being blended in still has to go: the idle's own transtime at arrival, which is
    /// how long the animated ankles keep travelling toward the stance. 0 when there is no fade to read, and the
    /// settle then steps on <see cref="MaxWait"/> instead of on the fade.</summary>
    internal static float Fade(RunRef run) =>
        run.IsNull || !(run.BlendTime > 0f) ? 0f : MathF.Max(0f, (1f - run.Blend) * run.BlendTime);

    // ---- Stops: the held feet, then the settle steps ----

    /// <summary>The world offsets the settle draws each ankle at, from where they are drawn so far. Begins on the frame
    /// a locomoting body leaves locomotion, and hands what it holds to the stride warp's plants if movement resumes or it
    /// is turned off. <paramref name="rStance"/>/<paramref name="lStance"/>: where the idle sets each foot (world, only
    /// X and Y read); <paramref name="fade"/>: seconds the idle still has to blend in.</summary>
    internal static void Feet(ref PoseService.Slot s, bool on, Vector3 rAnkle, Vector3 lAnkle, Vector3 rStance, Vector3 lStance,
                              float fade, bool standing, float yawRate, float scale, float dt, out Vector3 dr, out Vector3 dl)
    {
        bool wasLoco = s.SettleLoco;
        s.SettleLoco = s.Loco;
        s.SettleSpin = standing && MathF.Abs(yawRate) > TurnRate;
        dr = dl = Vector3.Zero;
        if (s.SettleStage == None)
        {
            if (!on || s.Loco) return;
            if (wasLoco) Begin(ref s, rAnkle, lAnkle, rStance, lStance, scale);
            else if (s.SettleSpin) Turn(ref s, rAnkle, lAnkle, yawRate > 0f);
            else return;
        }
        else if (!on || s.Loco) { Hand(ref s, rAnkle, lAnkle, rStance, lStance, fade, scale, out dr, out dl); return; }
        Pose(ref s, rAnkle, lAnkle, rStance, lStance, fade, scale, dt, out dr, out dl);
    }

    /// <summary>Does <see cref="Feet"/> read stance spots this frame: a settle runs, or a stop or a turn may start one.</summary>
    internal static bool Wants(in PoseService.Slot s, bool standing, float yawRate)
        => s.SettleStage != None || s.SettleLoco && !s.Loco || standing && MathF.Abs(yawRate) > TurnRate;

    /// <summary>Where the run fading in sets each foot (world): its clip sampled at its own time, posed on the body's frame
    /// (<paramref name="legs"/> about <paramref name="origin"/>). Left as given with no clip to sample.</summary>
    internal static void Stance(IntPtr gob, in PoseService.Bones bones, Quaternion legs, Vector3 origin, float scale,
                                ref Vector3 r, ref Vector3 l)
    {
        RunRef run = Gait.Driven(gob);
        int rf = PoseRig.Index(bones.RFoot), lf = PoseRig.Index(bones.LFoot);
        if (!PoseRig.Db || run.IsNull || run.Clip is not { } clip || rf < 0 || lf < 0) return;
        r = origin + Vector3.Transform(DbAnim.SampledPos(rf, clip, run.Time, run.PosScale) * scale, legs);
        l = origin + Vector3.Transform(DbAnim.SampledPos(lf, clip, run.Time, run.PosScale) * scale, legs);
    }

    /// <summary>Takes both feet over at arrival, held where they are drawn: the engine drops the walk from the queue and
    /// fades the idle in from its last frame, frozen, so a foot that follows the fade slides. The foot farther from its
    /// stance spot steps first, and that distance picks the stop: a shift landing with the fade, a step up, or a catch.</summary>
    private static void Begin(ref PoseService.Slot s, Vector3 drawnR, Vector3 drawnL, Vector3 stanceR, Vector3 stanceL, float scale)
    {
        float offR = Flat(drawnR - stanceR).Length(), offL = Flat(drawnL - stanceL).Length();
        bool right = offR >= offL;
        float gap = MathF.Max(offR, offL) / scale;
        s.SettleRight = right;
        s.SettleFrom = right ? drawnR : drawnL;
        s.SettleHold = right ? drawnL : drawnR;
        s.SettleNow = gap > StepUpGap;
        s.SettleStep = gap > CatchGap ? CatchTime : s.SettleNow ? StepUpTime : StepTime;
        s.SettleTurn = false;
        Start(ref s);
    }

    /// <summary>Holds a standing body's feet where they are drawn as it turns; the foot on the side it turns to
    /// (<paramref name="left"/>) steps first, opening the stance the way the body goes.</summary>
    private static void Turn(ref PoseService.Slot s, Vector3 drawnR, Vector3 drawnL, bool left)
    {
        s.SettleRight = !left;
        s.SettleFrom = left ? drawnL : drawnR;
        s.SettleHold = left ? drawnR : drawnL;
        s.SettleNow = false;
        s.SettleStep = TurnStepTime;
        s.SettleTurn = true;
        Start(ref s);
    }

    private static void Start(ref PoseService.Slot s)
    {
        s.SettleStage = Waiting;
        s.SettleT = 0f;
        s.SettleLift = 0f;
        s.SettleOff = s.SettleOffV = s.SettleOffO = s.SettleOffOV = Vector2.Zero;
        s.SettleFolded = false;
    }

    /// <summary>One frame of the settle: the acting foot waiting, stepping onto its stance spot, or both easing their
    /// leftovers out; until its own turn comes the weighted foot is pinned to the point the body stopped on, and a foot
    /// that has stepped stays on its stance spot while the fade carries the animated ankle there.</summary>
    private static void Pose(ref PoseService.Slot s, Vector3 rAnkle, Vector3 lAnkle, Vector3 rStance, Vector3 lStance,
                             float fade, float scale, float dt, out Vector3 dr, out Vector3 dl)
    {
        bool right = s.SettleRight;
        Vector3 acting = right ? rAnkle : lAnkle, other = right ? lAnkle : rAnkle;
        Vector3 to = Flat((right ? rStance : lStance) - acting), toOther = Flat((right ? lStance : rStance) - other);
        Vector3 step = Vector3.Zero, hold = Vector3.Zero;
        switch (s.SettleStage)
        {
            case Waiting:
                s.SettleT += dt;
                step = Flat(s.SettleFrom - acting);
                hold = Flat(s.SettleHold - other);
                bool go = s.SettleTurn
                    ? s.SettleT >= TurnWait || step.Length() > TurnSlack * scale || hold.Length() > TurnSlack * scale
                    : s.SettleNow || fade <= s.SettleStep || s.SettleT >= MaxWait;
                if (dt > 0f && go)
                {
                    s.SettleFrom = acting + step;
                    s.SettleStage = First;
                    s.SettleT = 0f;
                    Steps++;
                }
                break;
            case First:
            case Second:
                s.SettleT += dt;
                float u = MathF.Min(1f, s.SettleT / s.SettleStep);
                Vector3 target = acting + to;
                s.SettleLift = MathF.Max(s.SettleLift, LiftOf(s.SettleFrom, target, scale));
                step = Arc(s.SettleFrom, target, u, s.SettleLift) + to;
                hold = s.SettleStage == First ? Flat(s.SettleHold - other) : toOther;
                if (u >= 1f && dt > 0f) Advance(ref s, target, other + toOther, scale);
                break;
            default:
                // once the fade has landed the stance spots are the animated ankles, bar the sampling's own error: from
                // then the offsets themselves ease out, so the settle ends on the animation without a jump
                if (fade <= 0f && !s.SettleFolded)
                {
                    s.SettleOff += new Vector2(to.X, to.Y);
                    s.SettleOffO = new Vector2(toOther.X, toOther.Y);
                    s.SettleFolded = true;
                }
                Smooth(ref s.SettleOff, ref s.SettleOffV, Vector2.Zero, dt, EaseTime);
                if (s.SettleFolded) Smooth(ref s.SettleOffO, ref s.SettleOffOV, Vector2.Zero, dt, EaseTime);
                Vector3 off = new(s.SettleOff.X, s.SettleOff.Y, 0f), offO = new(s.SettleOffO.X, s.SettleOffO.Y, 0f);
                step = s.SettleFolded ? off : to + off;
                hold = s.SettleFolded ? offO : toOther;
                float done = Done * Done * scale * scale;
                if (s.SettleFolded && s.SettleOff.LengthSquared() < done && s.SettleOffO.LengthSquared() < done)
                {
                    s.SettleOff = s.SettleOffO = Vector2.Zero;
                    s.SettleStage = None;
                }
                break;
        }
        if (right) { dr = step; dl = hold; } else { dl = step; dr = hold; }
    }

    /// <summary>A step has landed on <paramref name="landed"/>. Through a turn still going, the feet trade: the landed one
    /// is held and the other waits its turn. After the first step the foot that carried the body steps onto its stance
    /// spot <paramref name="stance"/> only if it is more than <see cref="Adjust"/> off it; under that the remainder eases
    /// out, a centimetres-wide drift nobody reads as a slide. Either way both feet then ease onto the animation.</summary>
    private static void Advance(ref PoseService.Slot s, Vector3 landed, Vector3 stance, float scale)
    {
        if (s.SettleTurn && s.SettleSpin)
        {
            // still turning: the foot that landed is held there and the one that waited steps next
            s.SettleFrom = s.SettleHold;
            s.SettleHold = landed;
            s.SettleRight = !s.SettleRight;
            s.SettleStage = Waiting;
            s.SettleT = 0f;
            s.SettleLift = 0f;
            return;
        }
        if (s.SettleStage == Second) { s.SettleStage = Easing; return; }
        Vector2 flat = new(s.SettleHold.X - stance.X, s.SettleHold.Y - stance.Y);
        s.SettleRight = !s.SettleRight;
        if (flat.Length() > Adjust * scale)
        {
            s.SettleFrom = new Vector3(s.SettleHold.X, s.SettleHold.Y, stance.Z);
            s.SettleLift = 0f;
            s.SettleStage = Second;
            s.SettleT = 0f;
            Steps++;
        }
        else
        {
            s.SettleStage = Easing;
            s.SettleOff = flat;
            s.SettleOffV = Vector2.Zero;
        }
    }

    /// <summary>Hands this frame's offsets to the stride warp's plants and stands the settle down: a plant holds where
    /// its foot is drawn and a swing eases the offset out, so the drawn foot does not jump.</summary>
    private static void Hand(ref PoseService.Slot s, Vector3 rAnkle, Vector3 lAnkle, Vector3 rStance, Vector3 lStance,
                             float fade, float scale, out Vector3 dr, out Vector3 dl)
    {
        Pose(ref s, rAnkle, lAnkle, rStance, lStance, fade, scale, 0f, out dr, out dl);
        s.SettleTurn = false;
        s.AnchorR.Off = new Vector2(dr.X, dr.Y);
        s.AnchorL.Off = new Vector2(dl.X, dl.Y);
        s.Planting = true;
        s.SettleStage = None;
        s.SettleT = 0f;
        s.SettleOff = s.SettleOffV = s.SettleOffO = s.SettleOffOV = Vector2.Zero;
    }

    /// <summary>The offset from the animated ankle at which a stepping foot is drawn, <paramref name="u"/> of the
    /// way from <paramref name="from"/> onto it: the horizontal hand-over eases in and out, and the arc rises
    /// <paramref name="lift"/> at its top. At both ends the offset leaves the foot exactly where an end wants it,
    /// so the step starts and finishes on the ground.</summary>
    internal static Vector3 Arc(Vector3 from, Vector3 ankle, float u, float lift)
    {
        Vector3 d = (from - ankle) * (1f - Ease(u));
        return new Vector3(d.X, d.Y, d.Z + lift * MathF.Sin(MathF.PI * Math.Clamp(u, 0f, 1f)));
    }

    /// <summary>How high a step of this length lifts: <see cref="Lift"/> per <see cref="LiftFull"/>, capped at <see cref="MaxLift"/>.</summary>
    private static float LiftOf(Vector3 from, Vector3 to, float scale)
    {
        float d = new Vector2(from.X - to.X, from.Y - to.Y).Length();
        return scale * MathF.Min(MaxLift, Lift * d / (LiftFull * scale));
    }

    private static float Ease(float u) => u <= 0f ? 0f : u >= 1f ? 1f : u * u * (3f - 2f * u);

    private static Vector3 Flat(Vector3 v) => new(v.X, v.Y, 0f);

    // ---- Self-check ----

    /// <summary>The arc, the two-step stop and the turn on the spot, against hand-placed feet, the measured male idle
    /// crossfade and a hand-driven half turn.</summary>
    internal static bool SelfCheck() => ArcCheck() && StopCheck() && CatchCheck() && TurnCheck();

    /// <summary>The step's arc: it leaves the foot where it was, sets it on the animated ankle, and rises between.</summary>
    private static bool ArcCheck()
    {
        Vector3 from = new(0.4f, 0.1f, 0f), ankle = Vector3.Zero;
        Vector3 a0 = Arc(from, ankle, 0f, Lift), a1 = Arc(from, ankle, 1f, Lift), mid = Arc(from, ankle, 0.5f, Lift);
        float rise = 0f;
        for (int i = 1; i < 10; i++) rise = MathF.Min(rise < 1e-9f ? 1f : rise, Arc(from, ankle, i / 10f, Lift).Z);
        return Vector3.Distance(a0, from) < 1e-5f && a1.Length() < 1e-5f
               && MathF.Abs(mid.Z - Lift) < 1e-5f && rise > 0f
               && Arc(from, ankle, 0.25f, Lift).X > Arc(from, ankle, 0.75f, Lift).X;
    }

    /// <summary>A shift: a stop through a 0.70 s male idle crossfade, the farther foot 0.198 off its spot: the weighted foot does not move while the fade
    /// carries the animated ankles to the stance, the stepping foot lands
    /// on the stance spot as the fade lands, a weighted foot left more than <see cref="Adjust"/> off its own spot
    /// takes a second step and one left nearer does not, and the settle ends on the animated ankles.</summary>
    private static bool StopCheck()
    {
        // the right foot stopped 0.134 m from its stance spot, so it must step; then 0.067 m and 0.020 m, so it must not
        return Stop(new Vector3(0.22f, -0.26f, 0f), 2) && Stop(new Vector3(0.16f, -0.23f, 0f), 1)
               && Stop(new Vector3(0.112f, -0.216f, 0f), 1);
    }

    private static bool Stop(Vector3 rStance, int want)
    {
        const float dt = 1f / 60f, fade = 0.7f;
        Vector3 r0 = new(0.1f, -0.2f, 0f), l0 = new(-0.05f, 0.25f, 0f), lStance = new(-0.18f, 0.10f, 0f);
        PoseService.Slot s = default;
        // no plant held: the engine's stop leaves the stride warp no walk to plant by, and the settle starts anyway
        s.Loco = s.SettleLoco = true;
        int steps = Steps, moved = 0;
        bool ended = false, up = false, landed = false;
        Vector3 pinned = r0;
        for (int i = 0; i < 240; i++)
        {
            float t = i * dt, w = MathF.Min(1f, t / fade);
            Vector3 ra = Vector3.Lerp(r0, rStance, w), la = Vector3.Lerp(l0, lStance, w);
            byte was = s.SettleStage;
            s.Loco = false;
            Feet(ref s, true, ra, la, rStance, lStance, MathF.Max(0f, fade - t), false, 0f, 1f, dt, out Vector3 dr, out Vector3 dl);
            Vector3 drawnR = ra + dr, drawnL = la + dl;
            if (dl.Z > 1e-3f) up = true;
            // the left foot steps first, so the right is pinned right through the wait and that step
            if (was is Waiting or First && Vector3.Distance(drawnR, pinned) > 1e-3f) moved++;
            if (was == First && s.SettleStage != First) landed = Vector3.Distance(drawnL, lStance) < 5e-3f;
            if (s.SettleStage == None)
            {
                ended = Vector3.Distance(drawnR, ra) < 2e-3f && Vector3.Distance(drawnL, la) < 2e-3f;
                break;
            }
        }
        return ended && up && landed && moved == 0 && Steps - steps == want;
    }

    /// <summary>A catch: a run stopped mid-stride, feet 0.9 apart and the front one 0.48 off its stance spot, through a 1 s
    /// fade. By hand: the front foot lifts at once and lands on its spot at 0.30 s, the rear one held until then; the rear
    /// lands on its own at 0.60 s; both stay on their spots while the fade runs to 1 s, then end on the animated ankles.</summary>
    private static bool CatchCheck()
    {
        const float dt = 1f / 60f, fade = 1f;
        Vector3 r0 = new(0.5f, -0.12f, 0f), l0 = new(-0.4f, 0.12f, 0f), rStance = new(0.02f, -0.12f, 0f), lStance = new(-0.02f, 0.12f, 0f);
        PoseService.Slot s = default;
        s.Loco = s.SettleLoco = true;
        int steps = Steps, slid = 0;
        float liftR = -1f, landR = -1f, landL = -1f;
        bool ended = false;
        for (int i = 0; i < 240; i++)
        {
            float t = i * dt, w = MathF.Min(1f, t / fade);
            Vector3 ra = Vector3.Lerp(r0, rStance, w), la = Vector3.Lerp(l0, lStance, w);
            s.Loco = false;
            Feet(ref s, true, ra, la, rStance, lStance, MathF.Max(0f, fade - t), false, 0f, 1f, dt, out Vector3 dr, out Vector3 dl);
            Vector3 drawnR = ra + dr, drawnL = la + dl;
            if (liftR < 0f && dr.Z > 1e-3f) liftR = t;
            if (landR < 0f && liftR >= 0f && dr.Z < 1e-6f && Vector3.Distance(drawnR, rStance) < 5e-3f) landR = t;
            if (landL < 0f && landR >= 0f && dl.Z > 1e-3f) landL = 0f;
            if (landL == 0f && dl.Z < 1e-6f && Vector3.Distance(drawnL, lStance) < 5e-3f) landL = t;
            if (landR < 0f && Vector3.Distance(drawnL, l0) > 1e-3f) slid++;
            if (landL > 0f && t < fade && (Vector3.Distance(drawnR, rStance) > 5e-3f || Vector3.Distance(drawnL, lStance) > 5e-3f)) slid++;
            if (s.SettleStage == None)
            {
                ended = Vector3.Distance(drawnR, ra) < 2e-3f && Vector3.Distance(drawnL, la) < 2e-3f;
                break;
            }
        }
        return ended && slid == 0 && Steps - steps == 2 && liftR >= 0f && liftR <= 2f * dt
               && landR > 0.28f && landR < 0.34f && landL > 0.58f && landL < 0.66f;
    }

    /// <summary>A standing body turning 3 rad/s for 1 s each way, its feet 0.15 either side of its centre. By hand: the
    /// feet twist past <see cref="TurnSlack"/> at 0.18 s, the steps land at 0.53, 0.88 and 1.23 s, the turn has stopped
    /// by the last and the waiting foot is under <see cref="Adjust"/> off: three steps, the turning side's foot lifting
    /// first, a grounded foot never moving while the other is up, and both on their stance spots two seconds on.</summary>
    private static bool TurnCheck() => Turns(3f) && Turns(-3f);

    private static bool Turns(float rate)
    {
        const float Dt = 1f / 60f;
        PoseService.Slot s = default;
        int steps = Steps, slid = 0, first = 0;
        bool ended = false;
        Vector3 prevR = default, prevL = default;
        for (int i = 0; i < 180; i++)
        {
            float yaw = rate * Dt * Math.Min(i, 60);
            Vector3 x = new(MathF.Cos(yaw), MathF.Sin(yaw), 0f);
            Vector3 ra = 0.15f * x, la = -0.15f * x;
            Feet(ref s, true, ra, la, ra, la, 0f, true, i < 60 ? rate : 0f, 1f, Dt, out Vector3 dr, out Vector3 dl);
            Vector3 drawnR = ra + dr, drawnL = la + dl;
            bool upR = dr.Z > 1e-4f, upL = dl.Z > 1e-4f;
            if (first == 0 && (upR || upL)) first = upR ? 1 : -1;
            if (i > 0 && (upR && Vector3.Distance(drawnL, prevL) > 1e-4f || upL && Vector3.Distance(drawnR, prevR) > 1e-4f)) slid++;
            prevR = drawnR; prevL = drawnL;
            if (i == 179) ended = Vector3.Distance(drawnR, ra) < 2e-3f && Vector3.Distance(drawnL, la) < 2e-3f;
        }
        // a left turn (counter-clockwise from above) opens with the left foot
        return first == (rate > 0f ? -1 : 1) && slid == 0 && Steps - steps == 3 && ended;
    }
}
