using System;
using System.Numerics;

namespace CX.Services.Anim;

/// <summary>One foot through a walk: held on the ground at the world point and heading it landed with, stepping off a
/// hold it could not keep, or swinging, its offset and turn off the animation easing out to nothing by touchdown.</summary>
internal struct Footing
{
    /// <summary>Stages: in the air, held on the ground, stepping off a hold it let go.</summary>
    public const byte Swing = 0, Held = 1, Stepping = 2;
    public byte Stage;
    /// <summary>In the air last frame, so a hold now is a landing.</summary>
    public bool Aired;
    /// <summary>The hold (world XY) and the world heading it keeps (radians); where a step sets the foot down.</summary>
    public Vector2 At, To;
    public float Heading;
    /// <summary>Drawn this frame off the warped animation: the offset (world) and the turn about the vertical (radians,
    /// positive = left).</summary>
    public Vector3 Off;
    public float Yaw;
    /// <summary>The offset and turn a swing eases out from, and its progress then (NaN: the next frame's).</summary>
    public Vector3 Off0;
    public float Yaw0, U0;
    /// <summary>The turn a swing is aimed by onto the line of travel (radians) and the progress it was last aimed at.</summary>
    public float Aimed, AimU;
    /// <summary>Seconds into a step.</summary>
    public float T;
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
    /// <summary>The share of a leg's length a hold may sit off the warped animation before it is let go.</summary>
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

    /// <summary>One frame of a foot at warped world point <paramref name="p"/> whose legs head <paramref name="heading"/>
    /// (world yaw), on the ground (<paramref name="on"/>) or <paramref name="u"/> through its swing: the offset it is drawn
    /// at; <paramref name="yaw"/> its turn; <paramref name="down"/> it was set down this frame; <paramref name="let"/> its
    /// hold was let go.</summary>
    internal static Vector3 Hold(ref Footing f, bool on, float u, Vector3 p, float heading, in HoldLimits r, float dt,
                                 out float yaw, out bool down, out bool let)
    {
        down = let = false;
        Vector2 q = new(p.X, p.Y);
        bool aired = f.Aired;
        f.Aired = !on;
        if (!on && f.Stage != Footing.Swing) Lift(ref f, u);
        if (f.Stage == Footing.Swing)
        {
            if (on)
            {
                f.Stage = Footing.Held;
                f.At = q + new Vector2(f.Off.X, f.Off.Y);
                f.Heading = heading + f.Yaw;
                down = aired;
            }
            else
            {
                if (float.IsNaN(f.U0)) f.U0 = u;
                float rest = Remaining(u, f.U0);
                f.Off = f.Off0 * rest;
                f.Yaw = f.Yaw0 * rest;
                yaw = f.Yaw;
                return f.Off;
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
            f.To = q;
            f.T = 0f;
        }
        f.T += dt;
        float s = f.T / StepTime, h = Ease(Math.Clamp((s - StepLead) / (1f - 2f * StepLead), 0f, 1f));
        Vector2 at = Vector2.Lerp(f.At, f.To, h);
        f.Off = new Vector3(at - q, r.Lift * MathF.Sin(MathF.PI * MathF.Min(s, 1f)));
        f.Yaw = Wrap(f.Heading - heading) * (1f - h);
        if (s >= 1f)
        {
            f.Stage = Footing.Held;
            f.At = f.To;
            f.Heading = heading;
            f.Off = new Vector3(f.To - q, 0f);
            f.Yaw = 0f;
            down = true;
        }
        yaw = f.Yaw;
        return f.Off;
    }

    /// <summary>The offset turning a swinging foot at <paramref name="p"/> about the body's <paramref name="origin"/> from
    /// the legs' line <paramref name="legs"/> (world) onto the line of travel <paramref name="vel"/> (at most
    /// <see cref="AimCap"/>): the turn closes over what is left of the swing, so it is whole from <see cref="SwingEnd"/>
    /// and a change of travel mid-swing is taken up over the rest of it, not at once.</summary>
    internal static Vector3 Aim(ref Footing f, Vector3 p, Vector3 origin, Vector2 vel, Vector3 legs, float u, float scale)
    {
        float turn = 0f;
        if (vel.LengthSquared() >= MinTravel * MinTravel * scale * scale && legs.X * legs.X + legs.Y * legs.Y >= 1e-8f)
            turn = Math.Clamp(Wrap(MathF.Atan2(vel.Y, vel.X) - MathF.Atan2(legs.Y, legs.X)), -AimCap, AimCap);
        float left = SwingEnd - f.AimU;
        f.Aimed += (turn - f.Aimed) * (left > 1e-4f ? Math.Clamp((u - f.AimU) / left, 0f, 1f) : 1f);
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
    }

    /// <summary>A hold that costs more than <paramref name="r"/> allows: too far off the warped animation at
    /// <paramref name="p"/>, turned past <see cref="YawCap"/>, or needing the pelvis lower than the animation does by
    /// more than its sink.</summary>
    private static bool Breaks(in Footing f, Vector3 p, in HoldLimits r)
    {
        if (new Vector2(f.Off.X, f.Off.Y).LengthSquared() > r.Hold * r.Hold || MathF.Abs(f.Yaw) > YawCap) return true;
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

    private static float Wrap(float a) => MathF.IEEERemainder(a, 2f * MathF.PI);

    /// <summary>A foot on hand-worked 0.1 s frames: lands where drawn, turns back by the legs' 0.3 rad, past a 0.1 hold steps
    /// half way and 0.04 up at mid-step onto the animation, is let go past 0.8 rad and 0.5 off a hip 0.77 up (0.0544 drop),
    /// not 0.43 (0.0102); a swing keeps half its offset and is aimed half of a quarter turn half way to <see cref="SwingEnd"/>,
    /// all from there; a turn alone is written. Oracle: the hand-worked steps, the ease and the leg triangle.</summary>
    internal static bool FootCheck()
    {
        var r = new HoldLimits { Hip = new Vector3(0f, 0f, 0.85f), Reach = 0.873f, Cap = 0.108f, Sink = 0.018f, Hold = 0.1f, Lift = 0.04f };
        Footing f = default;
        f.Aired = true;
        Vector3 o = Hold(ref f, true, 1f, new Vector3(0f, 0f, 0.08f), 0f, r, 0.1f, out float yaw, out bool down, out bool let);
        bool lands = down && !let && o == Vector3.Zero && yaw == 0f && f.Stage == Footing.Held;
        o = Hold(ref f, true, 1f, new Vector3(0.05f, 0f, 0.08f), 0.3f, r, 0.1f, out yaw, out down, out let);
        bool holds = !let && !down && MathF.Abs(o.X + 0.05f) < 1e-6f && MathF.Abs(yaw + 0.3f) < 1e-6f;
        o = Hold(ref f, true, 1f, new Vector3(0.12f, 0f, 0.08f), 0.3f, r, 0.1f, out yaw, out _, out let);
        bool steps = let && MathF.Abs(o.X + 0.06f) < 1e-5f && MathF.Abs(o.Z - 0.04f) < 1e-5f && MathF.Abs(yaw + 0.15f) < 1e-5f;
        o = Hold(ref f, true, 1f, new Vector3(0.12f, 0f, 0.08f), 0.3f, r, 0.1f, out yaw, out down, out let);
        bool stepped = down && !let && o.Length() < 1e-6f && yaw == 0f && f.At == new Vector2(0.12f, 0f);
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
        return lands && holds && steps && stepped && twists && stretch && swings && aims && lp.Ik;
    }
}
