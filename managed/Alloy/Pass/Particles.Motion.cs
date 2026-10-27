using System.Numerics;

namespace CX.Alloy;

/// <summary>A particle is moved by the engine's simulation, not by a matrix, so each record carries the centre
/// it held last frame.</summary>
internal static unsafe partial class Particles
{
    /// <summary>A step no particle takes in one frame, in world units: past it the slot is a POOLED one the
    /// engine recycled under us, not a particle that moved, and its stored centre is another particle's.</summary>
    private const float PrevJumpMax = 8f;

    /// <summary>Last frame's centre for a record slot: the centre the slot held, or this frame's own where
    /// there is no previous frame to take one from -- the mirror is not last frame's (<paramref name="fresh"/>),
    /// or the step is past <see cref="PrevJumpMax"/>. Either way the particle gets a zero step, which is the
    /// camera field's own answer rather than a reprojection across the area.</summary>
    private static Vector3 PrevCentre(bool fresh, in Vector3 held, in Vector3 centre)
    {
        if (fresh) return centre;
        return (centre - held).LengthSquared() > PrevJumpMax * PrevJumpMax ? centre : held;
    }
}
