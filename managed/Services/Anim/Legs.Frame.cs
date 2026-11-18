using System.Numerics;
using System.Runtime.CompilerServices;

namespace CX.Services.Anim;

/// <summary>The knob values one leg solve reads, filled by the adapter from <see cref="AnimKnobs"/> so the core
/// reads no setting of its own.</summary>
internal struct LegKnobs
{
    /// <summary><c>anim.stride</c> with the rig the scene database's (the warp needs the clips' own keys).</summary>
    public bool Stride;
    /// <summary><c>anim.footik</c>, and the steepest slope a foot is laid on, as rise per unit (tan of <c>anim.footik_tilt</c>).</summary>
    public bool FootIk;
    public float MaxSlope;
    /// <summary><c>anim.footik_lift</c>: the most one foot is set above the other, world units at scale 1.</summary>
    public float Lift;
    /// <summary><c>anim.mass</c>; <c>anim.diagonal</c> with its name hook installed; <c>anim.pivot</c>.</summary>
    public bool Mass, Diagonal, Pivot;
}

/// <summary>One floor sample under a foot: whether a floor lies in range, and its height off the body's
/// reference height (world units; read even with no hit, as the cast cache hands it over).</summary>
internal struct FloorSample
{
    public bool Hit;
    public float Off;

    public FloorSample(bool hit, float off) { Hit = hit; Off = off; }
}

/// <summary>Up to <see cref="LegFrame.Samples"/> floor samples under one foot.</summary>
[InlineArray(LegFrame.Samples)]
internal struct FloorSamples
{
    private FloorSample _sample;
}

/// <summary>One foot's floors this frame, as the adapter cast them: the samples under it, the one under where a
/// swinging foot lands, and each slope (toward the toe, toward the right) when it was re-measured.</summary>
internal struct FootFloor
{
    public FloorSamples Under;
    public int Count;
    public FloorSample Land;
    /// <summary>The toe-ward (F) and rightward (S) rise per unit; a slope not re-measured this frame holds its last.</summary>
    public bool NewF, NewS;
    public float SlopeF, SlopeS;

    /// <summary>Appends a sample under the foot; past <see cref="LegFrame.Samples"/> it is dropped.</summary>
    public void Add(FloorSample s)
    {
        if (Count < LegFrame.Samples) Under[Count++] = s;
    }

    /// <summary>The first sample under the foot; none in range when there is none.</summary>
    public readonly FloorSample First => Count > 0 ? Under[0] : default;
}

/// <summary>The walk clips on the body this frame, digested: the blended stride and the lead clip's timing at its
/// own phase. <see cref="Present"/> false is no walk to warp (or the warp is off), and the plants let go.</summary>
internal struct LegWalk
{
    public bool Present;
    /// <summary>Summed blend weight of every walk run (unclamped), each run's stride ratio and travel direction
    /// summed at that weight, and the lead clip's own direction (model frame) for when the sum cancels.</summary>
    public float Weight, Strides;
    public Vector3 Dir, LeadDir;
    /// <summary>The lead clip's stance speed (model units/s at rate 1), the lead run's engine rate (when paced) and
    /// current rate, its phase (time over length, unwrapped) and length in seconds.</summary>
    public float Speed, EngineRate, Rate, Phase, Length;
    /// <summary>The lead clip loops; carries its own footstep events; is another clip than last frame's.</summary>
    public bool Cyclic, Authored, Changed;
    /// <summary>Each ankle's lowest height and the leg's length (model units), the phase each foot lands at (-1 for
    /// none) and where its ankle is then (model frame).</summary>
    public float FloorR, FloorL, LegLength, DownR, DownL;
    public Vector3 LandR, LandL;
    /// <summary>At <see cref="Phase"/>: each foot on the ground, and 0..1 through its swing (1 on the ground).</summary>
    public bool ContactR, ContactL;
    public float SwingR, SwingL;
}

/// <summary>Everything one body's leg solve reads for one frame, as plain values: no engine handle, no call out.
/// The adapter (<see cref="PoseService"/>) fills it in stages; a self-check can fill it whole.</summary>
internal struct LegFrame
{
    /// <summary>Floor samples a foot can carry.</summary>
    public const int Samples = 4;

    /// <summary>Seconds this frame; a teleport or first sight (springs land); an animation the layer leaves alone;
    /// corrected recently enough that last frame's heading counts.</summary>
    public float Dt;
    public bool Snap, Gated, Tracked;
    public LegKnobs Knobs;
    /// <summary>The body's rendered origin (world), its rotation (the gob frame, unturned), that rotation's horizontal
    /// forward and right, and its scale.</summary>
    public Vector3 Origin;
    public Quaternion Facing;
    public Vector3 FwdH, Right;
    public float Scale;
    /// <summary>Measured motion: ground speed (world units/s), yaw rate (rad/s, positive = left), the weight cue's
    /// acceleration, this frame's vertical rise (world units), the stairs weight and the horizontal velocity.</summary>
    public float Ground, YawRate, Cue, Rise, Weight;
    public Vector2 Vel;
    /// <summary>Locomoting; paced (locomoting or shuffling); standing still in a movement animation.</summary>
    public bool Loco, Paced, Standing;
    /// <summary>The hip turn's inputs: the creature's anim id, whether its diagonal plays the straight cycle, and
    /// whether the body has hips to turn.</summary>
    public int Anim;
    public bool Swapped, Hips;
    /// <summary>The animated ankles (world, after the hip turn is written), and the thighs, read when a walk is present.</summary>
    public Vector3 RAnkle, LAnkle, RThigh, LThigh;
    public LegWalk Walk;
    /// <summary>Seconds the idle still blends in, and where it sets each foot (world) when the settle sampled it.</summary>
    public float Fade;
    public bool HasStance;
    public Vector3 StanceR, StanceL;
    public FootFloor FloorR, FloorL;
}

/// <summary>What one leg solve hands back: the hip turn and the legs' frame, what the adapter writes to the body's
/// slot, where each foot is drawn, and the pelvis offset, ankle deltas and foot tilts the IK writes.</summary>
internal struct LegPose
{
    /// <summary>A hip turn to write, its angle (radians, positive = left), and the legs' rotation it gives with its
    /// forward, horizontal forward and horizontal right.</summary>
    public bool Turned;
    public float Turn;
    public Quaternion Legs;
    public Vector3 LegFwd, LegFwdH, LegRight;
    /// <summary>The rate multiplier the next frame's runs play at; a foot planted this frame; the walk has no
    /// footstep events; feet in the air have their landing predicted.</summary>
    public float RateNext;
    public bool Touchdown, Unvoiced, Landing;
    public StrideWarp.Ahead AheadR, AheadL;
    /// <summary>Each ankle's stride warp and settle offset (world), the point it is drawn at (where its floor is
    /// cast), and the pelvis drop the longer stride needs.</summary>
    public Vector3 WarpR, WarpL, FootR, FootL;
    public float Drop;
    /// <summary>The pelvis offset (world units, up), each ankle's world target delta from its animated point with
    /// the pelvis moved, each foot's world tilt, and whether the leg IK has anything to write.</summary>
    public float Body;
    public Vector3 DeltaR, DeltaL;
    public Quaternion TiltR, TiltL;
    public bool Ik;
    /// <summary>The pelvis's horizontal offset (world units) and each foot's turn about the vertical off its animated
    /// direction (radians, positive = left). No stage sets them yet: the pelvis only moves up and down and a foot keeps
    /// the animation's direction.</summary>
    public Vector2 Shift;
    public float YawR, YawL;
}

/// <summary>What one body's leg stages keep between frames: the hip turn, the plants and pelvis drop, the settle,
/// the foot offset springs and the pelvis dip. Lives in its <see cref="PoseService.Slot"/>.</summary>
internal struct LegState
{
    /// <summary><see cref="Diagonal"/>: the hips' turn off the facing (radians, positive = left), its spring velocity,
    /// and the facing's yaw it was last measured against.</summary>
    public float Turn, TurnV, Heading;
    /// <summary><see cref="StrideWarp"/>: each foot's plant, and whether the walk has been blended in to plant them.</summary>
    public StrideWarp.Anchor AnchorR, AnchorL;
    public bool Planting;
    /// <summary><see cref="StrideWarp"/>: the pelvis drop held at the stride's deepest need, and its easing spring.</summary>
    public float DropHold, Drop, DropV;
    /// <summary><see cref="Settle"/>: locomoting last frame, the stepping foot, a turn and not a stop, still turning, stepping
    /// at once, the leftovers folded; the stage, its clock, the step's lift and length; the step's start and the weighted
    /// foot's held world point; each foot's leftover easing out.</summary>
    public bool SettleLoco, SettleRight, SettleTurn, SettleSpin, SettleNow, SettleFolded;
    public byte SettleStage;
    public float SettleT, SettleLift, SettleStep;
    public Vector3 SettleFrom, SettleHold;
    public Vector2 SettleOff, SettleOffV, SettleOffO, SettleOffOV;
    /// <summary>Each ankle's stride-warp offset last frame (world), which a stop's settle takes the feet over from.</summary>
    public Vector3 WarpR, WarpL;
    /// <summary>Foot placement: the offset springs, the floor slopes under each foot toward the toe (F) and to the
    /// right (S), and the stairs weight, each with its spring velocity.</summary>
    public float OffR, VelR, OffL, VelL, FR, VFR, SR, VSR, FL, VFL, SL, VSL, W, VW;
    /// <summary><see cref="Mass"/>: the pelvis dip (world units, negative is down) and its spring velocity, and the
    /// eased vertical speed (world units/s) and its change, the landing's load.</summary>
    public float Dip, DipV, Climb, ClimbA;
}
