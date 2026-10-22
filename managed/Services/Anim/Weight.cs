using System;
using System.Numerics;

namespace CX.Services.Anim;

/// <summary>
/// The acceleration the weight cues read, a <see cref="PoseService"/> module: the pelvis dip (<see cref="Mass"/>), the
/// lean (<see cref="UpperBody"/>) and the follow-through (<see cref="Follow"/>) all key on it. On a path or a drive it
/// is the engine's exact <see cref="PoseService.Slot.GaitAccel"/>, where a differenced drawn position gives ±0.05 m/s²
/// of clock noise; with no path -- a correction, a knockback, a shuffle -- the measured one. <b>Hot</b>, no allocation.
/// </summary>
internal static class Weight
{
    /// <summary>Largest cue (world units per second squared): the running stop ease, which is the hardest the
    /// engine ever slows a body on a path. This is weight, not a stumble.</summary>
    private const float MaxCue = 4f;
    /// <summary>The walk rate (world units/s) the self-check runs at.</summary>
    private const float WalkRate = 2f;

    /// <summary>Fills <see cref="PoseService.Slot.Cue"/> for this frame. After <see cref="Gait.Read"/>, before any
    /// module that reads the cue. <paramref name="reset"/> (a teleport, first sight) carries none.</summary>
    internal static void Step(ref PoseService.Slot s, bool reset, float dt)
        => s.Cue = reset || !(dt > 0f) ? 0f : Math.Clamp(s.Gait == GaitMode.None ? s.Accel : s.GaitAccel, -MaxCue, MaxCue);

    /// <summary>Cruise carries no cue and loads nothing; the engine's ramp and its ease each dip the pelvis by
    /// about the rate's worth of load and pitch the lean forward into the one and back out of the other; a drive
    /// at full speed carries nothing whatever the measured acceleration says, and a body with no path at all falls
    /// back to that measurement.</summary>
    internal static bool SelfCheck()
    {
        const float Dt = 1f / 60f;

        PoseService.Slot c = default;
        c.Gait = GaitMode.ClientPath;
        c.GaitSpeed = WalkRate;
        float cruiseDip = 0f, cruiseSwing = 0f;
        for (int i = 0; i < 120; i++)
        {
            Step(ref c, i == 0, Dt);
            cruiseDip = MathF.Max(cruiseDip, MathF.Abs(Mass.Advance(ref c, 0f, 1f, Dt)));
            Follow.Advance(ref c, Vector3.Zero, new Vector3(0f, c.Cue, 0f), true, i == 0, Dt);
            cruiseSwing = MathF.Max(cruiseSwing, c.ArmR.Length());
        }
        if (!(cruiseDip < 1e-4f && cruiseSwing < 1e-4f)) return false;

        float ramp = Held(GaitMode.ClientPath, WalkRate, 54, Dt), ease = Held(GaitMode.Driving, -WalkRate, 57, Dt);
        if (!(ramp < -0.012f && ramp > -0.020f && ease < -0.012f && ease > -0.020f)) return false;
        if (!(UpperBody.LeanTarget(WalkRate, 0f, WalkRate, 0f, true).X < 0f
              && UpperBody.LeanTarget(WalkRate, 0f, -WalkRate, 0f, true).X > 0f)) return false;

        PoseService.Slot d = default;
        d.Gait = GaitMode.Driving;
        d.GaitSpeed = WalkRate;
        d.Accel = 3f;
        Step(ref d, false, Dt);
        PoseService.Slot n = default;
        n.Accel = 3f;
        Step(ref n, false, Dt);
        PoseService.Slot big = default;
        big.Gait = GaitMode.ServerPath;
        big.GaitAccel = 8f;
        Step(ref big, false, Dt);
        return d.Cue == 0f && n.Cue == 3f && big.Cue == MaxCue;
    }

    /// <summary>The deepest the pelvis dips over <paramref name="frames"/> of a gait holding a steady
    /// <paramref name="accel"/>: the shape of the engine's own ramp or ease.</summary>
    private static float Held(GaitMode mode, float accel, int frames, float dt)
    {
        PoseService.Slot s = default;
        s.Gait = mode;
        s.GaitAccel = accel;
        float worst = 0f;
        for (int i = 0; i < frames; i++)
        {
            Step(ref s, false, dt);
            worst = MathF.Min(worst, Mass.Advance(ref s, 0f, 1f, dt));
        }
        return worst;
    }
}
