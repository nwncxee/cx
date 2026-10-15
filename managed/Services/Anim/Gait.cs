using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Services.Anim;

/// <summary>Where a creature's movement comes from, which is what decides how it starts and how it stops:
/// <see cref="Driving"/> is bang-bang in the engine -- full speed on frame 1, a coast and a dead stop -- unless
/// <see cref="DriveEase"/> shapes it, while both path modes ramp and ease, the server's twice as hard as the
/// client's prediction. <see cref="None"/> is a creature the engine is not walking along a path.</summary>
internal enum GaitMode : byte { None, Driving, ClientPath, ServerPath }

/// <summary>
/// The engine's own locomotion signals, a <see cref="PoseService"/> module: the mode, the exact ground speed and
/// acceleration, the frame a start begins, and the driven cycle's phase and length, every one read off
/// <c>CNWCCreature</c> rather than measured -- the position steps on a whole-millisecond world clock (±13.6 % per
/// frame at 144 fps), so a differenced speed is noise where <c>rate × factor</c> is exact. <b>Hot</b>, no allocation.
/// </summary>
internal static unsafe class Gait
{
    /// <summary>The floor <c>ComputeSpeedFactor</c> clamps to: a creature under way never travels slower than a
    /// tenth of its rate.</summary>
    internal const float Floor = 0.1f;
    /// <summary>Millimetres per world unit: every engine rate is mm/s and every distance here is world units
    /// (metres).</summary>
    private const float Milli = 1f / 1000f;
    /// <summary>The rate <c>WalkUpdateLocation</c> hardcodes for the pure sidesteps 0x4e/0x4f (mm/s), in place
    /// of the creature's own walk or run rate.</summary>
    private const float SidestepRate = 1600.0001f;
    /// <summary>Bytes per waypoint: the array is <c>Vector</c>s laid end to end.</summary>
    private const int PointSize = 12;
    /// <summary>Waypoints walked for the remaining path length. Real paths are a handful of points; the cap is
    /// there so a torn count cannot turn a hot read into an unbounded walk.</summary>
    private const int MaxPoints = 256;

    /// <summary>After the mover's animation id is known: fills the gait fields on <paramref name="s"/> from the
    /// engine. <paramref name="tracked"/> says last frame's reading is this creature's and one frame old -- without it
    /// nothing that needs an edge (the acceleration, the start) is reported. A path's factor is
    /// <c>ComputeSpeedFactor</c>'s, a drive's the special-walk multiplier (1 in the engine, else the eased ramp).</summary>
    internal static void Read(ref PoseService.Slot s, IntPtr creature, IntPtr gob, int anim, bool tracked)
    {
        bool seen = tracked && s.GaitSeen;
        int wasAnim = s.GaitAnim;
        float wasFactor = s.GaitFactor, wasSpeed = s.GaitSpeed;
        bool wasMoving = s.Gait != GaitMode.None;

        s.Gait = GaitMode.None;
        s.GaitSpeed = s.GaitAccel = s.GaitPhase = s.GaitCycle = s.GaitFactor = 0f;
        s.Starting = false;
        s.GaitAnim = anim;
        s.GaitSeen = creature != IntPtr.Zero;
        if (creature == IntPtr.Zero) return;

        var c = new CNWCCreature(creature);
        float rate = Rate(anim, c);
        if (!(rate > 0f) || c.WaypointCount == 0) return;

        bool driving = c.Driving != 0;
        s.Gait = driving ? GaitMode.Driving : c.PathFromServer == 1 ? GaitMode.ServerPath : GaitMode.ClientPath;
        float factor = driving ? c.SpecialWalk : c.SpeedFactor;
        uint ms = new CNWCObject(creature).StepMs;
        s.GaitFactor = factor;
        s.GaitSpeed = rate * Milli * factor;
        // the first moving frame is the engine's lurch into gear (a path starts at the floor, a drive at full or
        // eased speed): a step in speed, not an acceleration anything should lean into
        s.GaitAccel = seen && wasMoving ? Accel(s.GaitSpeed, wasSpeed, ms) : 0f;
        // the anim edge is the start; the factor leaving 0 (what arrival writes) is the same start for a walk
        // that begins again without an idle frame in between
        s.Starting = seen && (PoseService.IsLocomotion(anim) && !PoseService.IsLocomotion(wasAnim)
                              || wasMoving && !driving && factor >= Floor && wasFactor < Floor);

        RunRef run = Driven(gob);
        if (run.IsNull) return;
        float len = run.Length;
        if (!(len > 0f)) return;
        s.GaitCycle = len;
        s.GaitPhase = Math.Clamp(run.Time / len, 0f, 1f);
    }

    /// <summary>The rate (mm/s) <c>WalkUpdateLocation</c> walks <paramref name="anim"/> at: the walk rate for
    /// 2, 3, 0x54 and 0x55, the run rate for 4, 0x56 and 0x57, a hardcoded <see cref="SidestepRate"/> for the
    /// pure sidesteps, and nothing at all for an animation the engine does not move along a path.</summary>
    internal static float Rate(int anim, CNWCCreature c) => anim switch
    {
        2 or 3 or 0x54 or 0x55 => c.WalkRate,     // not-an-offset: the engine's animation ids
        4 or 0x56 or 0x57 => c.RunRate,           // not-an-offset: the engine's animation ids
        0x4e or 0x4f => SidestepRate,             // not-an-offset: the engine's animation ids
        _ => 0f,
    };

    /// <summary>The acceleration (world units per second squared) of a body whose speed went from
    /// <paramref name="was"/> to <paramref name="speed"/> m/s over <paramref name="ms"/> whole milliseconds of world
    /// time -- the engine's clock, not the frame's: they disagree by up to a millisecond. Speed, not factor: a drive's
    /// walk-to-run change swaps the rate under a factor <see cref="DriveEase"/> rescales to keep the speed.</summary>
    internal static float Accel(float speed, float was, uint ms) => ms == 0 ? 0f : (speed - was) * 1000f / ms;

    /// <summary>The 2-D path still ahead of the creature, in world units: from the desired position to the
    /// cursor waypoint and along every leg after it, exactly the walk <c>ComputeSpeedFactor</c> makes (z is
    /// forced to 0 in each leg). 0 when the list is not one we can walk. A drive's is the lead
    /// <c>DrivePlayer</c> last laid, less what has been walked of it.</summary>
    internal static float Remaining(CNWCCreature c)
    {
        byte* first = (byte*)c.Waypoints, at = (byte*)c.WaypointCursor;
        int n = Math.Min((int)c.WaypointCount, MaxPoints);
        if (first == null || at == null || n <= 0) return 0f;
        long k = at - first;
        if (k < 0 || k >= (long)n * PointSize) return 0f;
        Span<Vector3> points = new(first, n);
        Vector3 p = c.DesiredPos;
        float px = p.X, py = p.Y, sum = 0f;
        for (int i = (int)(k / PointSize); i < n; i++)
        {
            Vector3 q = points[i];
            float dx = q.X - px, dy = q.Y - py;
            sum += MathF.Sqrt(dx * dx + dy * dy);
            px = q.X; py = q.Y;
        }
        return sum;
    }

    /// <summary>The run the engine is driving: the newest that is neither queued nor fading out.
    /// <c>Gob::Animate</c> appends, so the last live run is the animation being blended to.</summary>
    internal static RunRef Driven(IntPtr gob)
    {
        for (int i = PoseRig.RunCount(gob) - 1; i >= 0; i--)
        {
            RunRef r = PoseRig.Run(gob, i);
            if (r.IsNull || (r.Flags & (EngineLayout.AnimRunQueued | EngineLayout.AnimRunFadingOut)) != 0) continue;
            return r;
        }
        return default;
    }

    /// <summary>The acceleration is positive up the ramp, zero in cruise and negative through the ease: a 2 m/s
    /// client-path ramp climbs by ms/1000 of its rate per update and the ease falls as fast, 2 m/s² either way.</summary>
    internal static bool SelfCheck()
    {
        float up = Accel(0.3f, 0.2f, 50);
        float cruise = Accel(2f, 2f, 7);
        float down = Accel(1f, 1.1f, 50);
        return up > 0f && MathF.Abs(up - 2f) < 0.01f && cruise == 0f && down < 0f && MathF.Abs(down + 2f) < 0.01f;
    }
}
