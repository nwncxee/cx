using System;
using System.Numerics;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Mass, a <see cref="PoseService"/> module (<c>anim.mass</c>). A start, a stop or the bottom of a drop loads the
/// body: the pelvis dips by the ground's acceleration and comes back up through a slight overshoot, the legs taking
/// the dip so the feet stay where they are. The chest's lean settles the same way instead of critically damped, so
/// a stop or a turn ends in a small swing past upright. Driven by change, not speed: steady motion carries no load.
/// </summary>
internal static class Mass
{
    /// <summary>Metres the pelvis dips per m/s² of load, and the deepest it goes (both at scale 1): a walk's start or
    /// stop dips it about 2 cm, a run's about 5.</summary>
    private const float DipPerAccel = 0.007f, MaxDip = 0.06f;
    /// <summary>Upward vertical acceleration (m/s²) under which a change of climb is uneven ground, not a landing.</summary>
    private const float LandFloor = 2f;
    /// <summary>Seconds the vertical speed and its change are eased over: a walkmesh seam is not a landing.</summary>
    private const float ClimbTime = 0.1f;
    /// <summary>The pelvis spring: natural frequency (Hz) and damping ratio -- about a tenth over, settled in half a second.</summary>
    private const float DipHz = 2f, DipZeta = 0.6f;
    /// <summary>The chest's lean spring with the knob on: about the critically damped lean's pace, a sixth over.</summary>
    private const float ChestHz = 1.6f, ChestZeta = 0.5f;
    /// <summary>World units of dip past which the pelvis counts as loaded in the stats line: a millimetre, the
    /// smallest dip anyone could see.</summary>
    private const float LoadedDip = 1e-3f;

    internal static int Loaded;

    /// <summary>The dip (world units, negative is down) to add to the root's height this frame. With the knob off
    /// (<paramref name="on"/>), <paramref name="gated"/> (nothing keeps the feet down then) or on a snap it is 0 and the spring starts over; a frame of no time (a pause) holds it.</summary>
    internal static float Step(ref LegState s, float cue, float rise, float scale, bool on, bool gated, bool snap, float dt)
    {
        if (!on || gated || snap) { s.Dip = s.DipV = s.Climb = s.ClimbA = 0f; return 0f; }
        if (!(dt > 0f)) return s.Dip;
        float dip = Advance(ref s, cue, rise, scale, dt);
        if (dip < -LoadedDip) Loaded++;
        return dip;
    }

    /// <summary>Advances the pelvis spring for <paramref name="rise"/> world units climbed this frame, on the forward
    /// acceleration <paramref name="cue"/> <see cref="Weight"/> hands it; the dip (world units, negative is down).</summary>
    internal static float Advance(ref LegState s, float cue, float rise, float scale, float dt)
    {
        float a = MathF.Min(1f, dt / ClimbTime), climb = s.Climb + (rise / dt - s.Climb) * a;
        s.ClimbA += ((climb - s.Climb) / dt - s.ClimbA) * a;
        s.Climb = climb;
        Settle(ref s.Dip, ref s.DipV, -scale * Load(cue, s.ClimbA), DipHz, DipZeta, dt);
        return s.Dip;
    }

    /// <summary>Metres of dip at scale 1 for a forward acceleration and an upward vertical one (m/s²): speeding up
    /// and slowing down load alike, climbing into a rise only past <see cref="LandFloor"/>.</summary>
    internal static float Load(float accel, float lift)
        => MathF.Min(DipPerAccel * (MathF.Abs(accel) + MathF.Max(0f, lift - LandFloor)), MaxDip);

    /// <summary>The chest's lean with the knob on: <see cref="Settle"/> in place of the critically damped lean.</summary>
    internal static void Chest(ref Vector2 lean, ref Vector2 v, Vector2 target, float dt)
    {
        Settle(ref lean.X, ref v.X, target.X, ChestHz, ChestZeta, dt);
        Settle(ref lean.Y, ref v.Y, target.Y, ChestHz, ChestZeta, dt);
    }

    /// <summary>An underdamped spring toward a moving <paramref name="target"/>: <see cref="Wobble"/> on the offset from it.</summary>
    internal static void Settle(ref float x, ref float v, float target, float hz, float zeta, float dt)
    {
        float y = x - target;
        Wobble(ref y, ref v, 2f * MathF.PI * hz, zeta, dt);
        x = y + target;
    }

    /// <summary>The springs overshoot a step by at most their share and settle in time; a walk from rest dips the
    /// pelvis, steady walking and a steady descent carry no dip, a stop dips it and it settles back with little
    /// lift past neutral, and the bottom of a descent dips it.</summary>
    internal static bool SelfCheck()
    {
        const float dt = 1f / 60f;
        bool springs = StepResponse(DipHz, DipZeta, 0.12f, 0.5f) && StepResponse(ChestHz, ChestZeta, 0.2f, 0.7f);

        PoseService.Slot s = default;
        float start = 0f, steady = 0f, stop = 0f, lift = 0f, rest = 0f;
        for (int i = 0; i < 330; i++)
        {
            float t = i * dt, speed = t >= 0.2f && t < 3.2f ? 2f : 0f;
            PoseService.Motion(ref s, new Vector3(0f, speed * dt, 0f), Vector3.UnitY, false, dt);
            Weight.Step(ref s, false, dt);
            float dip = Advance(ref s.Leg, s.Cue, 0f, 1f, dt);
            if (t < 1f) start = MathF.Min(start, dip);
            else if (t >= 2f && t < 3.2f) steady = MathF.Max(steady, MathF.Abs(dip));
            else if (t >= 3.2f)
            {
                stop = MathF.Min(stop, dip);
                lift = MathF.Max(lift, dip);
                if (t >= 4.7f) rest = MathF.Max(rest, MathF.Abs(dip));
            }
        }
        bool walk = start < -0.01f && steady < 0.001f && stop < -0.01f && lift <= 0.12f * -stop && rest < 0.001f;

        LegState d = default;
        float descent = 0f, land = 0f;
        for (int i = 0; i < 180; i++)
        {
            float t = i * dt, dip = Advance(ref d, 0f, t < 1.5f ? -dt : 0f, 1f, dt);
            if (t >= 1f && t < 1.5f) descent = MathF.Max(descent, MathF.Abs(dip));
            else if (t >= 1.5f) land = MathF.Min(land, dip);
        }
        return springs && walk && descent < 0.001f && land < -0.005f;
    }

    /// <summary>A unit step through <see cref="Settle"/>: over by at most <paramref name="over"/> of it, and within
    /// 5% from <paramref name="settle"/> seconds on.</summary>
    private static bool StepResponse(float hz, float zeta, float over, float settle)
    {
        const float dt = 1f / 60f;
        float x = 0f, v = 0f, peak = 0f, late = 0f;
        for (int i = 1; i <= 120; i++)
        {
            Settle(ref x, ref v, 1f, hz, zeta, dt);
            peak = MathF.Max(peak, x);
            if (i * dt >= settle) late = MathF.Max(late, MathF.Abs(x - 1f));
        }
        return peak > 1f && peak <= 1f + over && late < 0.05f;
    }
}
