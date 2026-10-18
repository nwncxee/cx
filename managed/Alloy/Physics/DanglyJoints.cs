using System;
using System.Numerics;

namespace CX.Alloy.Physics;

/// <summary>What one frame of <see cref="DanglyJoints.Step"/> is driven by: the part's world transform, the
/// wind at the part (world, the engine's units), the frame time and the knobs, already read.</summary>
public readonly struct DanglyInputs
{
    public readonly Matrix4x4 PartWorld;
    public readonly Vector3 Wind;
    public readonly float Dt, WindGain, Tilt, Drag, Cage;

    public DanglyInputs(Matrix4x4 partWorld, Vector3 wind, float dt, float windGain, float tilt, float drag, float cage)
    {
        PartWorld = partWorld; Wind = wind; Dt = dt; WindGain = windGain; Tilt = tilt; Drag = drag; Cage = cage;
    }
}

/// <summary>One part's joint state, in world space: positions and velocities of every joint, the previous
/// pose the teleport test reads, and whether the part has settled.</summary>
public sealed class DanglyJointsState
{
    public readonly Vector3[] X, V;
    public Vector3 PrevPos;
    public Quaternion PrevRot;
    public bool Seeded, Settled;

    public DanglyJointsState(int joints) { X = new Vector3[joints]; V = new Vector3[joints]; }
}

/// <summary>The joint solver Sway, Jiggle and Swing share (docs/spec/dangly-motions.md, section 5).</summary>
public static class DanglyJoints
{
    /// <summary>Advance <paramref name="s"/> by one frame and write <see cref="DanglyRig.BoneCount"/> node-space
    /// bone matrices into <paramref name="bones"/>. False when the part is settled and nothing changed, so the
    /// caller publishes nothing.</summary>
    public static bool Step(DanglyRig rig, DanglyJointsState s, in DanglyInputs inp, Span<Matrix4x4> bones)
    {
        for (int b = 0; b < rig.BoneCount && b < bones.Length; b++) bones[b] = Matrix4x4.Identity;
        return false;
    }
}
