using System;
using System.Numerics;
using CX.Engine;

namespace CX.Services.Anim;

/// <summary>
/// The combat shuffle the engine never paces (<see cref="IsShuffle"/>), a <see cref="StrideWarp"/> companion under
/// <c>anim.stride</c>: its ground speed is the gap a position correction still has to close (<see cref="Speed"/>).
/// </summary>
internal static unsafe class Skating
{
    /// <summary>Milliseconds per second: <c>correctionMs</c> is a whole-millisecond countdown.</summary>
    private const float Milli = 1000f;

    /// <summary>The combat shuffle's animation ids, the four <c>MoveCreatureToPosition</c> picks from by the dot of
    /// the facing against the gap: forward, back, left, right. It plays them fire-and-forget at rate 1 and never paces
    /// them -- <c>CNWCObject::AnimationStationary</c> answers "stationary" for these ids, so <c>AnimationUpdate</c>
    /// never reaches <c>WalkUpdateLocation</c>, the one place outside <c>Gob::PlayAnimation</c> writing a run's rate.</summary>
    internal static bool IsShuffle(int anim) => anim is >= 0x18 and <= 0x1b;  // not-an-offset: the engine's animation ids

    /// <summary>The ground speed (world units/s) a creature's position correction is covering: the horizontal gap
    /// still to close over the countdown still to run. <c>MoveCreatureToPosition</c> closes the gap by
    /// <c>remaining/(remaining+step)</c> after taking the step off the countdown, which scales both by the same
    /// factor every update and so leaves the ratio constant for the whole correction.</summary>
    internal static float Speed(IntPtr creature, float measured)
    {
        if (creature == IntPtr.Zero) return measured;
        var c = new CNWCCreature(creature);
        Vector3 d = c.DesiredPos - new CNWCObject(creature).Position;
        return Speed(MathF.Sqrt(d.X * d.X + d.Y * d.Y), c.CorrectionMs, measured);
    }

    /// <inheritdoc cref="Speed(IntPtr,float)"/>
    /// <remarks>A spent countdown is the engine snapping to the desired position instead of lerping, and leaves
    /// nothing to read: the measured step stands in.</remarks>
    internal static float Speed(float gap, uint ms, float measured) => ms == 0 ? measured : gap * Milli / ms;

    /// <summary>The shuffle's ids, and its rate for a known gap and countdown: 0.6 m still to cover over the 512 ms a
    /// server correction opens is 1.172 m/s, worked by hand; a spent countdown falls back to the measured step.</summary>
    internal static bool SelfCheck()
    {
        if (!IsShuffle(0x18) || !IsShuffle(0x1b) || IsShuffle(0x17) || IsShuffle(0x1c) || IsShuffle(2)) return false;
        return MathF.Abs(Speed(0.6f, 512, 9f) - 1.171875f) < 1e-4f && MathF.Abs(Speed(0.6f, 0, 9f) - 9f) < 1e-4f;
    }
}
