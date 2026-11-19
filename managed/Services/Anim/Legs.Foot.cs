using System;
using System.Numerics;

namespace CX.Services.Anim;

/// <summary>One foot through a walk: held on the ground at the world point and heading it landed with, stepping off a
/// hold it could not keep, or swinging, its offset and turn off the animation easing out to nothing by touchdown.</summary>
internal struct Footing
{
    /// <summary>Stages: not yet seen (taken up where it is drawn, on the ground or in the air), in the air, held on the
    /// ground, stepping off a hold it let go.</summary>
    public const byte Fresh = 0, Swing = 1, Held = 2, Stepping = 3;
    public byte Stage;
    /// <summary>In the air last frame, so a hold now is a landing.</summary>
    public bool Aired;
    /// <summary>The hold (world XY) and the world heading it keeps (radians); where a step sets the foot down once it starts
    /// to, and the warped point when it began; the offset off the warped animation the hold was taken at.</summary>
    public Vector2 At, To, From, Landed;
    public float Heading;
    /// <summary>Drawn this frame off the warped animation: the offset (world) and the turn about the vertical (radians,
    /// positive = left).</summary>
    public Vector3 Off;
    public float Yaw;
    /// <summary>The offset and turn a swing eases out from, and its progress then.</summary>
    public Vector3 Off0;
    public float Yaw0, U0;
    /// <summary>The turn a swing is aimed by onto the line of travel (radians) and the progress it was last aimed at; met
    /// past <see cref="Legs.SwingEnd"/>, too late in its swing to be aimed.</summary>
    public float Aimed, AimU;
    public bool Late;
    /// <summary>How far off the warped animation the hold was set down (aimed, or handed over by a settle); seconds into a
    /// step and at its first frame, the farthest it may draw the foot off, and whether where it sets down is fixed.</summary>
    public float Far, T, T0, Most;
    public bool Set;
}

/// <summary>What one foot's hold may cost before it is let go: the hip joint it hangs from (world), the length the leg
/// may reach, the pelvis drop's cap and the share of it a hold may add, the offset off the warped animation, and a
/// step's lift (world units).</summary>
internal struct HoldLimits
{
    public Vector3 Hip;
    public float Reach, Cap, Sink, Hold, Lift;
}

/// <summary>The foot state: a hold keeps a landed foot's world point and heading, let go for a quick lifted step when
/// keeping it would stretch the leg, twist the ankle or pull the foot too far off the animation.</summary>
internal static partial class Legs
{
    /// <summary>The share of a leg's length a hold may sit off the warped animation, or drift from where it landed, before it
    /// is let go: no foot is drawn further off than this, or than where it was set down (aimed, or handed over by
    /// a settle) until its next step or swing eases it onto the animation.</summary>
    internal const float HoldShare = 0.12f;
    /// <summary>Radians a held foot may be turned off the animation's direction before it is let go: about what an ankle
    /// and a hip turn a planted foot by.</summary>
    internal const float YawCap = 0.8f;
    /// <summary>The share of a leg's length the pelvis may drop past what the warped animation needs to keep a hold.</summary>
    internal const float HoldSink = 0.02f;
    /// <summary>Seconds a step off a let-go hold takes, and the share of it at each end where the foot only rises or
    /// sets down, so it never moves along the floor.</summary>
    internal const float StepTime = 0.2f, StepLead = 0.15f;
    /// <summary>World units (times scale) a step off a let-go hold lifts the foot at its top.</summary>
    internal const float StepLift = 0.04f;
    /// <summary>Radians at most a swinging foot is turned about the body onto its line of travel.</summary>
    internal const float AimCap = 1.6f;
    /// <summary>The swing's progress by which the offset a foot lifted off with is gone, so it lands on the animation.</summary>
    internal const float SwingEnd = 0.85f;
    /// <summary>Seconds a foot's turn off the animation eases out over once nothing holds it (a stop, a settle).</summary>
    internal const float YawEase = 0.3f;
    /// <summary>Seconds a foot's leftover offset takes to ease out: one handed over in the air, or left by a walk gone.</summary>
    internal const float LeftTime = 0.15f;

    /// <summary>One frame of a foot at warped world point <paramref name="p"/> (legs heading <paramref name="heading"/>), on the
    /// ground (<paramref name="on"/>) or <paramref name="u"/> through its swing and aimed by <paramref name="aim"/>, which a
    /// landing keeps: the offset it is drawn at; <paramref name="yaw"/> its turn; <paramref name="down"/> it landed out of the
    /// clip's swing (a step is no footfall); <paramref name="let"/> its hold was let go.</summary>
    internal static Vector3 Hold(ref Footing f, bool on, float u, Vector3 p, float heading, in HoldLimits r, float dt,
                                 out float yaw, out bool down, out bool let, Vector3 aim = default)
    {
        down = let = false;
        Vector2 q = new(p.X, p.Y);
        bool aired = f.Aired;
        f.Aired = !on;
        if (!on && f.Stage != Footing.Swing) Lift(ref f, u);
        if (on && f.Stage == Footing.Fresh)
        {
            f.Stage = Footing.Held;
            f.Landed = new Vector2(f.Off.X, f.Off.Y);
            f.Far = f.Landed.Length();
            f.At = q + f.Landed;
            f.Heading = heading + f.Yaw;
        }
        if (f.Stage == Footing.Swing)
        {
            if (on)
            {
                f.Stage = Footing.Held;
                f.Landed = new Vector2(f.Off.X, f.Off.Y);
                f.At = q + f.Landed + new Vector2(aim.X, aim.Y);
                f.Far = (f.At - q).Length();
                f.Heading = heading + f.Yaw;
                down = aired;
            }
            else
            {
                float rest = Remaining(u, f.U0);
                f.Off = f.Off0 * rest;
                f.Yaw = f.Yaw0 * rest;
                yaw = f.Yaw;
                return f.Off + aim;
            }
        }
        if (f.Stage == Footing.Held)
        {
            f.Off = new Vector3(f.At - q, 0f);
            f.Yaw = Wrap(f.Heading - heading);
            if (!Breaks(f, p, r))
            {
                yaw = f.Yaw;
                return f.Off;
            }
            let = true;
            f.Stage = Footing.Stepping;
            f.Most = MathF.Max(r.Hold, f.Far);
            f.Set = false;
            f.From = q;
            f.T = 0f;
            f.T0 = dt;
        }
        f.T += dt;
        float s = f.T / StepTime, h = Ease(Math.Clamp((s - StepLead) / (1f - 2f * StepLead), 0f, 1f));
        // it sets down where the animation will be when it lands, at its pace over the step so far, so it neither trails the
        // body nor slides on the floor
        if (h >= 1f && !f.Set)
        {
            float seen = f.T - f.T0;
            f.To = q + (seen > 0f ? (q - f.From) * (MathF.Max(1f - s, 0f) * StepTime / seen) : Vector2.Zero);
            f.Set = true;
        }
        Vector2 off = Bound(Vector2.Lerp(f.At, f.Set ? f.To : q, h) - q, f.Most);
        f.Off = new Vector3(off, r.Lift * MathF.Sin(MathF.PI * MathF.Min(s, 1f)));
        f.Yaw = Wrap(f.Heading - heading) * (1f - h);
        if (s >= 1f)
        {
            f.Stage = Footing.Held;
            f.At = q + off;
            f.Landed = Vector2.Zero;
            f.Far = off.Length();
            f.Heading = heading;
            f.Off = new Vector3(off, 0f);
            f.Yaw = 0f;
        }
        yaw = f.Yaw;
        return f.Off;
    }

    /// <summary>The offset turning a swinging foot at <paramref name="p"/> about the body's <paramref name="origin"/> from
    /// the legs' line <paramref name="legs"/> (world) onto the line of travel <paramref name="vel"/> (at most
    /// <see cref="AimCap"/>): the turn closes over what is left of the swing, so it is whole from <see cref="SwingEnd"/>
    /// and a change of travel mid-swing is taken up over the rest of it, not at once; one met past it is not aimed.</summary>
    internal static Vector3 Aim(ref Footing f, Vector3 p, Vector3 origin, Vector2 vel, Vector3 legs, float u, float scale)
    {
        float turn = 0f;
        if (vel.LengthSquared() >= MinTravel * MinTravel * scale * scale && legs.X * legs.X + legs.Y * legs.Y >= 1e-8f)
            turn = Math.Clamp(Wrap(MathF.Atan2(vel.Y, vel.X) - MathF.Atan2(legs.Y, legs.X)), -AimCap, AimCap);
        float left = SwingEnd - f.AimU;
        f.Aimed += (turn - f.Aimed) * (left > 1e-4f ? Math.Clamp((u - f.AimU) / left, 0f, 1f) : f.Late ? 0f : 1f);
        f.AimU = MathF.Max(f.AimU, u);
        float c = MathF.Cos(f.Aimed), sn = MathF.Sin(f.Aimed);
        Vector2 r = new(p.X - origin.X, p.Y - origin.Y);
        return new Vector3(r.X * c - r.Y * sn - r.X, r.X * sn + r.Y * c - r.Y, 0f);
    }

    /// <summary>World units per second (times scale) under which a body has no line of travel to aim a landing at.</summary>
    private const float MinTravel = 0.1f;

    /// <summary>The foot leaves the ground <paramref name="u"/> through its swing: what it is drawn off the animation by
    /// now eases out from here.</summary>
    internal static void Lift(ref Footing f, float u)
    {
        f.Stage = Footing.Swing;
        f.Off0 = f.Off;
        f.Yaw0 = f.Yaw;
        f.U0 = u;
        f.Aimed = 0f;
        f.AimU = u;
        f.Late = u >= SwingEnd;
    }

    /// <summary>A hold that costs more than <paramref name="r"/> allows: further off the warped animation at <paramref name="p"/>
    /// than its limit (or than where it was set down, if further), drifted past the limit since, turned
    /// past <see cref="YawCap"/>, or needing the pelvis lower than the animation does by more than its sink.</summary>
    private static bool Breaks(in Footing f, Vector3 p, in HoldLimits r)
    {
        Vector2 off = new(f.Off.X, f.Off.Y);
        float most = MathF.Max(r.Hold, f.Far);
        if (off.LengthSquared() > most * most || (off - f.Landed).LengthSquared() > r.Hold * r.Hold || MathF.Abs(f.Yaw) > YawCap)
            return true;
        float held = Need(new Vector3(f.At.X, f.At.Y, p.Z) - r.Hip, r.Reach, r.Cap), anim = Need(p - r.Hip, r.Reach, r.Cap);
        return held > anim + r.Sink;
    }

    /// <summary>What is left of a swing's lift-off offset <paramref name="u"/> through it, from <paramref name="u0"/>:
    /// all of it there, none from <see cref="SwingEnd"/>, eased between. A swing with no progress left keeps it.</summary>
    private static float Remaining(float u, float u0)
    {
        float span = SwingEnd - u0;
        if (span <= 1e-4f) return 1f;
        return 1f - Ease(Math.Clamp((u - u0) / span, 0f, 1f));
    }

    private static float Ease(float w) => w * w * (3f - 2f * w);

    /// <summary><paramref name="v"/> no longer than <paramref name="most"/>.</summary>
    private static Vector2 Bound(Vector2 v, float most)
    {
        float l = v.Length();
        return l > most && l > 0f ? v * (most / l) : v;
    }

    private static float Wrap(float a) => MathF.IEEERemainder(a, 2f * MathF.PI);

    /// <summary>A foot on hand-worked 0.1 s frames: met on the ground it is held with no footfall; out of a swing it lands where
    /// drawn, turns back by the legs' 0.3 rad, past a 0.1 hold steps half way, 0.04 up, onto the animation (no footfall), is let
    /// go past 0.8 rad and 0.5 off a hip 0.77 up but not 0.43; a swing keeps half its offset and aim half way to
    /// <see cref="SwingEnd"/>, none and all from there; a turn alone is written. Oracle: hand-worked steps and leg triangle.</summary>
    internal static bool FootCheck()
    {
        var r = new HoldLimits { Hip = new Vector3(0f, 0f, 0.85f), Reach = 0.873f, Cap = 0.108f, Sink = 0.018f, Hold = 0.1f, Lift = 0.04f };
        Footing f = default;
        Vector3 met = Hold(ref f, true, 1f, Vector3.Zero, 0f, r, 0.1f, out _, out bool seen, out _);
        bool fresh = !seen && met == Vector3.Zero && f.Stage == Footing.Held;
        f = default;
        f.Stage = Footing.Swing;
        f.Aired = true;
        Vector3 o = Hold(ref f, true, 1f, new Vector3(0f, 0f, 0.08f), 0f, r, 0.1f, out float yaw, out bool down, out bool let);
        bool lands = down && !let && o == Vector3.Zero && yaw == 0f && f.Stage == Footing.Held;
        o = Hold(ref f, true, 1f, new Vector3(0.05f, 0f, 0.08f), 0.3f, r, 0.1f, out yaw, out down, out let);
        bool holds = !let && !down && MathF.Abs(o.X + 0.05f) < 1e-6f && MathF.Abs(yaw + 0.3f) < 1e-6f;
        o = Hold(ref f, true, 1f, new Vector3(0.12f, 0f, 0.08f), 0.3f, r, 0.1f, out yaw, out _, out let);
        bool steps = let && MathF.Abs(o.X + 0.06f) < 1e-5f && MathF.Abs(o.Z - 0.04f) < 1e-5f && MathF.Abs(yaw + 0.15f) < 1e-5f;
        o = Hold(ref f, true, 1f, new Vector3(0.12f, 0f, 0.08f), 0.3f, r, 0.1f, out yaw, out down, out let);
        bool stepped = !down && !let && o.Length() < 1e-6f && yaw == 0f && f.At == new Vector2(0.12f, 0f);
        Hold(ref f, true, 1f, new Vector3(0.12f, 0f, 0.08f), -0.51f, r, 0f, out yaw, out _, out let);
        bool twists = let && MathF.Abs(yaw - 0.81f) < 1e-5f;

        bool Reaches(float hipY)
        {
            var w = r;
            w.Hold = 1f;
            w.Hip = new Vector3(0f, hipY, 0.85f);
            Footing g = default;
            Hold(ref g, true, 1f, new Vector3(0f, -0.4f, 0.08f), 0f, w, 0.1f, out _, out _, out bool first);
            Hold(ref g, true, 1f, new Vector3(0f, hipY, 0.08f), 0f, w, 0.1f, out _, out _, out bool second);
            return !first && second;
        }
        bool stretch = Reaches(0.1f) && !Reaches(0.03f);

        Footing h = default;
        h.Stage = Footing.Swing;
        h.Aired = true;
        Hold(ref h, true, 1f, Vector3.Zero, 0f, r, 0.1f, out _, out _, out _);
        Hold(ref h, true, 1f, new Vector3(0.05f, 0f, 0f), 0f, r, 0.1f, out _, out _, out _);
        Vector3 lift = Hold(ref h, false, 0f, new Vector3(0.1f, 0f, 0f), 0f, r, 0.1f, out _, out _, out _);
        Vector3 mid = Hold(ref h, false, 0.425f, new Vector3(0.3f, 0f, 0f), 0f, r, 0.1f, out _, out _, out _);
        Vector3 end = Hold(ref h, false, 0.85f, new Vector3(0.5f, 0f, 0f), 0f, r, 0.1f, out _, out _, out _);
        Vector3 land = Hold(ref h, true, 1f, new Vector3(0.6f, 0f, 0f), 0f, r, 0.1f, out _, out down, out _);
        bool swings = MathF.Abs(lift.X + 0.05f) < 1e-6f && MathF.Abs(mid.X + 0.025f) < 1e-6f && end == Vector3.Zero
                      && land == Vector3.Zero && down && h.At == new Vector2(0.6f, 0f);
        Footing a = default;
        Lift(ref a, 0f);
        Vector3 at = new(0f, 1f, 0f);
        Vector2 across = new(-1f, 0f);
        float half = Aim(ref a, at, Vector3.Zero, across, Vector3.UnitY, 0.425f, 1f).X;
        float whole = Aim(ref a, at, Vector3.Zero, across, Vector3.UnitY, 0.85f, 1f).X;
        bool aims = MathF.Abs(half + MathF.Sin(0.25f * MathF.PI)) < 1e-4f && MathF.Abs(whole + 1f) < 1e-4f;

        LegState ls = default;
        LegFrame lf = default;
        lf.Dt = 0.1f;
        lf.Scale = 1f;
        lf.RThigh = lf.LThigh = new Vector3(0f, 0f, 0.85f);
        lf.RAnkle = lf.LAnkle = new Vector3(0f, 0f, 0.08f);
        LegPose lp = default;
        lp.Legs = Quaternion.Identity;
        lp.TiltR = lp.TiltL = Quaternion.Identity;
        lp.YawR = 0.3f;
        Place(ref ls, lf, ref lp);
        return fresh && lands && holds && steps && stepped && twists && stretch && swings && aims && lp.Ik;
    }
}
