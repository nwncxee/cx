using System;
using System.Numerics;
using CX.Engine;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Stops through the feet, a <see cref="PoseService"/> module under <c>anim.stride</c>. The engine crossfades the whole
/// body from a walk to the idle stance, so both feet glide: at arrival the stride warp's planted feet are taken over here
/// (one keeps its world point, the other takes a settle step onto its stance spot). <b>Hot</b>, no allocation.
/// </summary>
internal static unsafe class Settle
{
    /// <summary>Seconds a settle step takes from lift to touchdown: nearly twice a walk's own swing, a weight shift
    /// and not a stride. The step is started this long before the crossfade lands, so its target -- the animated
    /// ankle, which the crossfade is carrying to the stance -- is the stance spot exactly as the foot reaches it.</summary>
    private const float StepTime = 0.55f;
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
    /// <summary>Settle stages: none running, waiting on the crossfade, the first and second steps, the leftover easing out.</summary>
    internal const byte None = 0, Waiting = 1, First = 2, Second = 3, Easing = 4;

    internal static int Steps;

    /// <summary>Seconds the run being blended in still has to go: the idle's own transtime at arrival, which is
    /// how long the animated ankles keep travelling toward the stance. 0 when there is no fade to read, and the
    /// settle then steps on <see cref="MaxWait"/> instead of on the fade.</summary>
    internal static float Fade(RunRef run) =>
        run.IsNull || !(run.BlendTime > 0f) ? 0f : MathF.Max(0f, (1f - run.Blend) * run.BlendTime);

    // ---- Stops: the held feet, then the settle step ----

    /// <summary>The world offsets the settle draws each ankle at, from where they are drawn so far. Begins on the frame
    /// a locomoting body with a planted foot leaves locomotion, and hands what it holds to the stride warp's plants if
    /// movement resumes or it is turned off. <paramref name="fade"/>: seconds the idle still has to blend in.</summary>
    internal static void Feet(ref PoseService.Slot s, bool on, Vector3 rAnkle, Vector3 lAnkle, float fade,
                              float scale, float dt, out Vector3 dr, out Vector3 dl)
    {
        bool wasLoco = s.SettleLoco;
        s.SettleLoco = s.Loco;
        dr = dl = Vector3.Zero;
        if (s.SettleStage == None)
        {
            if (!on || s.Loco || !wasLoco || !(s.AnchorR.Planted || s.AnchorL.Planted)) return;
            Begin(ref s, rAnkle, lAnkle);
        }
        else if (!on || s.Loco) { Hand(ref s, rAnkle, lAnkle, fade, scale, out dr, out dl); return; }
        Pose(ref s, rAnkle, lAnkle, fade, scale, dt, out dr, out dl);
    }

    /// <summary>Takes the planted feet over at arrival, from where they are drawn. The weighted foot is the planted one
    /// drawn lower; the other is the one that will step.</summary>
    private static void Begin(ref PoseService.Slot s, Vector3 drawnR, Vector3 drawnL)
    {
        bool heldR = s.AnchorR.Planted, heldL = s.AnchorL.Planted;
        bool rWeighted = heldR && (!heldL || drawnR.Z <= drawnL.Z);
        s.SettleRight = !rWeighted;
        s.SettleHold = rWeighted ? drawnR : drawnL;
        s.SettleFrom = rWeighted ? drawnL : drawnR;
        s.SettlePin = rWeighted ? heldL : heldR;
        s.SettleStage = Waiting;
        s.SettleT = 0f;
        s.SettleLift = 0f;
        s.SettleOff = s.SettleOffV = Vector2.Zero;
    }

    /// <summary>One frame of the settle: the acting foot waiting, arcing, or easing its leftover out, and -- until
    /// its own turn comes -- the weighted foot pinned to the point the body stopped on.</summary>
    private static void Pose(ref PoseService.Slot s, Vector3 rAnkle, Vector3 lAnkle, float fade, float scale,
                             float dt, out Vector3 dr, out Vector3 dl)
    {
        bool right = s.SettleRight;
        Vector3 acting = right ? rAnkle : lAnkle, other = right ? lAnkle : rAnkle;
        Vector3 step = Vector3.Zero, hold = Vector3.Zero;
        switch (s.SettleStage)
        {
            case Waiting:
                s.SettleT += dt;
                // a planted foot stays where it landed; one caught mid-swing keeps following the animation
                if (s.SettlePin) step = Flat(s.SettleFrom - acting);
                hold = Flat(s.SettleHold - other);
                if (dt > 0f && (fade <= StepTime || s.SettleT >= MaxWait))
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
                float u = MathF.Min(1f, s.SettleT / StepTime);
                s.SettleLift = MathF.Max(s.SettleLift, LiftOf(s.SettleFrom, acting, scale));
                step = Arc(s.SettleFrom, acting, u, s.SettleLift);
                if (s.SettleStage == First) hold = Flat(s.SettleHold - other);
                if (u >= 1f && dt > 0f) Advance(ref s, other, scale);
                break;
            default:
                Smooth(ref s.SettleOff, ref s.SettleOffV, Vector2.Zero, dt, EaseTime);
                if (s.SettleOff.LengthSquared() < Done * Done * scale * scale) { s.SettleOff = Vector2.Zero; s.SettleStage = None; }
                step = new Vector3(s.SettleOff.X, s.SettleOff.Y, 0f);
                break;
        }
        if (right) { dr = step; dl = hold; } else { dl = step; dr = hold; }
    }

    /// <summary>A step has landed. After the first, the foot that carried the body takes its own step only if it
    /// is more than <see cref="Adjust"/> off its stance spot; under that the remainder eases out, which is a
    /// centimetres-wide drift nobody reads as a slide. After the second the settle is over.</summary>
    private static void Advance(ref PoseService.Slot s, Vector3 planted, float scale)
    {
        if (s.SettleStage == Second) { s.SettleStage = None; return; }
        Vector3 off = s.SettleHold - planted;
        Vector2 flat = new(off.X, off.Y);
        s.SettleRight = !s.SettleRight;
        if (flat.Length() > Adjust * scale)
        {
            s.SettleFrom = new Vector3(s.SettleHold.X, s.SettleHold.Y, planted.Z);
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
    private static void Hand(ref PoseService.Slot s, Vector3 rAnkle, Vector3 lAnkle, float fade, float scale,
                             out Vector3 dr, out Vector3 dl)
    {
        Pose(ref s, rAnkle, lAnkle, fade, scale, 0f, out dr, out dl);
        s.AnchorR.Off = new Vector2(dr.X, dr.Y);
        s.AnchorL.Off = new Vector2(dl.X, dl.Y);
        s.SettleStage = None;
        s.SettleT = 0f;
        s.SettleOff = s.SettleOffV = Vector2.Zero;
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

    /// <summary>How high a step of this length lifts: proportional up to <see cref="LiftFull"/>, capped there.</summary>
    private static float LiftOf(Vector3 from, Vector3 to, float scale)
    {
        float d = new Vector2(from.X - to.X, from.Y - to.Y).Length();
        return Lift * scale * MathF.Min(1f, d / (LiftFull * scale));
    }

    private static float Ease(float u) => u <= 0f ? 0f : u >= 1f ? 1f : u * u * (3f - 2f * u);

    private static Vector3 Flat(Vector3 v) => new(v.X, v.Y, 0f);

    // ---- Self-check ----

    /// <summary>The arc and the two-step state machine, against hand-placed feet and the measured male idle crossfade.</summary>
    internal static bool SelfCheck() => ArcCheck() && StopCheck();

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

    /// <summary>A stop through a 0.70 s male idle crossfade: the weighted foot does not move while the fade
    /// carries the animated ankles to the stance, only one foot is ever off the ground, the stepping foot lands
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
        s.Loco = s.SettleLoco = true;
        s.AnchorR.Planted = s.AnchorL.Planted = true;
        int steps = Steps, both = 0, moved = 0;
        bool ended = false, up = false, landed = false;
        Vector3 pinned = r0;
        for (int i = 0; i < 240; i++)
        {
            float t = i * dt, w = MathF.Min(1f, t / fade);
            Vector3 ra = Vector3.Lerp(r0, rStance, w), la = Vector3.Lerp(l0, lStance, w);
            byte was = s.SettleStage;
            s.Loco = false;
            Feet(ref s, true, ra, la, MathF.Max(0f, fade - t), 1f, dt, out Vector3 dr, out Vector3 dl);
            Vector3 drawnR = ra + dr, drawnL = la + dl;
            if (dr.Z > 1e-4f && dl.Z > 1e-4f) both++;
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
        return ended && up && landed && both == 0 && moved == 0 && Steps - steps == want;
    }
}
