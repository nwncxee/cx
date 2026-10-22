using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;
using static CX.Framework.EngineLayout;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// The procedural pose layer. Chains <c>Gob::Animate</c> (riding <c>DbAnim</c>'s step through <see cref="PoseRig"/>):
/// after the engine has posed a humanoid creature the modules correct it in order -- <see cref="FootPlacement"/>,
/// <see cref="Stride"/>, <see cref="UpperBody"/>, <see cref="Follow"/> -- over an animation layer of
/// <see cref="Transition"/> and <see cref="Diagonal"/>; only <see cref="Stride.Pre"/> runs first. <b>Hot</b>, no allocation.
/// </summary>
public sealed unsafe class PoseService : IExtenderService
{
    private static GobAnimateFn* _next;
    private static GobGetAdjustedScaleFn _scale;
    private static bool _armed, _creatures;

    /// <summary>World units between two frames past which the gob teleported: springs snap, nothing eases.</summary>
    private const float Teleport = 2f;
    /// <summary>Part-tree recursion guard: a cycle in a torn tree must not blow the stack. Real trees are ~6 deep.</summary>
    private const int MaxDepth = 64;
    /// <summary>Seconds the measured ground speed, yaw rate and slope average over: the engine steps a
    /// walking gob's position once per AI update on an integer-millisecond clock, so one frame's travel is noise.</summary>
    private const float MotionTime = 0.15f;
    /// <summary>Seconds the acceleration averages over: it is the derivative of a speed that steps, so it wants
    /// twice the smoothing the speed does or the lean pitch shivers.</summary>
    private const float AccelTime = 0.3f;
    /// <summary>World units/s of travel under which the path has no heading to turn (standing, turning on the spot).</summary>
    private const float MinTravel = 0.3f;

    /// <summary>Animations whose name starts with one of these (NUL-separated) get no correction: sitting,
    /// lying, knockdown and getting up, death, kneeling emotes, mounting, flying in/out and the module-defined
    /// customs. Every stock creature animation was listed to pick them.</summary>
    private static ReadOnlySpan<byte> Denied =>
        "sit\0standup\0kd\0ckd\0dead\0cdead\0die\0gu\0cgu\0getlow\0getmid\0cgetmid\0kneel\0meditate\0worship\0mount\0custom\0appear\0cappear\0disappear\0cdisappear\0"u8;

    /// <summary>The parts the layer writes, found each frame by the body's <see cref="RigMap"/> names (equipment swaps
    /// rebuild parts). The stock names are carried by 257 of 663 stock a_/c_ models, every rest orientation identity, so
    /// a bone's rest frame is the model's -- +X right, +Y forward, +Z up; a content rig's rests are taken to be the same.
    /// A gob without all six leg parts is skipped whole.</summary>
    internal struct Bones
    {
        public const int Count = 16;
        public IntPtr RThigh, RShin, RFoot, LThigh, LShin, LFoot, Torso, Neck, Head, Pelvis, RBicep, RForearm, RHand, LBicep, LForearm, LHand;
        /// <summary>By <see cref="BoneIndex"/>: r-thigh, r-shin, r-foot, l-thigh, l-shin, l-foot, torso, neck, head,
        /// pelvis, r-bicep, r-forearm, r-hand, l-bicep, l-forearm, l-hand.</summary>
        public IntPtr this[int i]
        {
            readonly get => i switch { 0 => RThigh, 1 => RShin, 2 => RFoot, 3 => LThigh, 4 => LShin, 5 => LFoot, 6 => Torso, 7 => Neck,
                                       8 => Head, 9 => Pelvis, 10 => RBicep, 11 => RForearm, 12 => RHand, 13 => LBicep, 14 => LForearm, _ => LHand };
            set { switch (i) { case 0: RThigh = value; break; case 1: RShin = value; break; case 2: RFoot = value; break;
                               case 3: LThigh = value; break; case 4: LShin = value; break; case 5: LFoot = value; break;
                               case 6: Torso = value; break; case 7: Neck = value; break; case 8: Head = value; break;
                               case 9: Pelvis = value; break; case 10: RBicep = value; break; case 11: RForearm = value; break;
                               case 12: RHand = value; break; case 13: LBicep = value; break; case 14: LForearm = value; break;
                               default: LHand = value; break; } }
        }
        public readonly bool Legs => RThigh != 0 && RShin != 0 && RFoot != 0 && LThigh != 0 && LShin != 0 && LFoot != 0;
    }

    /// <summary>One corrected gob: the engine's own values for everything written (root pos + the bones'
    /// rotations, w-first) and every module's springs. Keyed by gob AND root part so a recycled address never
    /// restores.</summary>
    internal struct Slot
    {
        public IntPtr Gob, Root;
        public long Stamp;
        public bool Applied;
        public Vector3 RootPos, LastPos;
        /// <summary>Measured motion: horizontal ground speed (world units/s), forward acceleration, yaw and its rate
        /// (rad/s, positive = left), and the signed walkmesh rise per unit of travel. Fwd/Right are the last
        /// horizontal axes, for a hit that arrives between frames.</summary>
        public float Speed, Accel, Yaw, YawRate, Slope;
        /// <summary>The turn rate of the TRAVEL (rad/s, positive = left) and the heading it was last measured at:
        /// what a body's sideways acceleration comes from. The facing's own rate is not -- the engine swings a
        /// walker's facing round in a few frames at a waypoint, and leaning and swinging the arms off that read
        /// every click as a hairpin taken at a sprint.</summary>
        public float PathRate, TravelYaw;
        public bool TravelKnown;
        public Vector3 Fwd, Right;
        /// <summary>Measured horizontal velocity (world units/s), averaged like the speed: which way a walk-back drifts.</summary>
        public Vector2 Vel;
        /// <summary><see cref="Diagonal"/>: the hips' turn off the facing (radians, positive = left) and its spring
        /// velocity; whether the body has hips to turn, <see cref="Swaps"/>'s last answer (the diagonal it plays is
        /// the straight cycle), and the facing's
        /// yaw it was last measured against (<c>anim.pivot</c> leaves a change of it on the hips).</summary>
        public float Turn, TurnV, Heading;
        public bool Hips, Swapped;
        /// <summary><see cref="Transition"/>: last frame's anim id, whether a blend runs and seconds into it, and per bone
        /// (<see cref="BoneIndex"/> order) the animation layer's last output (w-first) with its angular velocity
        /// (rad/s, parent frame), the old movement being carried on with its velocity, and the part each came from.</summary>
        public int LastAnim;
        public bool Blending;
        public float BlendT;
        public BoneQuats Out;
        public BoneVecs Spin;
        public BoneQuats Src;
        public BoneVecs SrcSpin;
        public BoneParts OutParts;
        /// <summary><see cref="FootPlacement.Weight"/>'s stairs grade and the moving hold timer.</summary>
        public float Grade, Moving;
        /// <summary><see cref="Anim.Gait"/>: the engine's own locomotion, read rather than measured. <c>Gait</c> is
        /// where the movement comes from; <c>GaitSpeed</c> and <c>GaitAccel</c> are the exact ground speed and
        /// acceleration (world units per second, per second squared) from the speed factor and rate over the engine's
        /// whole-millisecond clock; <c>Starting</c> the frame one begins; <c>GaitPhase</c>/<c>GaitCycle</c> the driven cycle's 0..1 position and length in seconds.</summary>
        public GaitMode Gait;
        public float GaitSpeed, GaitAccel, GaitPhase, GaitCycle;
        public bool Starting;
        /// <summary>Last frame's reading, for the edges: the speed factor, the animation id, and whether there was a
        /// reading at all.</summary>
        public float GaitFactor;
        public int GaitAnim;
        public bool GaitSeen;
        /// <summary><see cref="Anim.Weight"/>: the acceleration every weight cue reads -- the engine's own
        /// exact one on a path or a drive, and the measured <see cref="Accel"/> where there is no path at all
        /// (world units per second squared, signed).</summary>
        public float Cue;
        /// <summary>Foot placement: the offset springs, the floor slopes under each foot toward the toe (F) and
        /// to the right (S), the stairs weight, and which slope is re-cast this frame.</summary>
        public float OffR, VelR, OffL, VelL, FR, VFR, SR, VSR, FL, VFL, SL, VSL, W, VW;
        public int Phase;
        /// <summary>Each foot's cached floor cast (<see cref="FootPlacement.FootCast"/>).</summary>
        public FootPlacement.FootCast R, L;
        /// <summary>Stride: locomoting last frame, the rate multiplier <see cref="StrideWarp"/> paced for the next frame,
        /// and each run's engine and written rate.</summary>
        public bool Loco;
        public float RateNext;
        public RunRates Rates;
        /// <summary><see cref="StrideWarp"/>: each foot's plant, and the clip they were last timed by.</summary>
        public StrideWarp.Anchor AnchorR, AnchorL;
        public CX.Alloy.Mdl.AnimDef? PlantClip;
        /// <summary><see cref="StrideWarp"/>: the pelvis drop held at the stride's deepest need, and its easing spring.</summary>
        public float DropHold, Drop, DropV;
        /// <summary><see cref="StrideWarp"/>: the walk run last put in step, so it is seeded once.</summary>
        public long Seeded;
        /// <summary><see cref="Settle"/>: locomoting last frame, the stepping foot, whether it was held, a turn and not a stop,
        /// still turning, the stage, the step's start and the weighted foot's held world point, and the leftover easing out.</summary>
        public bool SettleLoco, SettleRight, SettlePin, SettleTurn, SettleSpin;
        public byte SettleStage;
        public float SettleT, SettleLift;
        public Vector3 SettleFrom, SettleHold;
        public Vector2 SettleOff, SettleOffV;
        /// <summary><see cref="Skating"/>: playing a combat shuffle the engine never paces.</summary>
        public bool Shuffling;
        /// <summary>Footfall: stepping this frame, when it last was, and the seconds since the last step played.</summary>
        public bool Stepping;
        public long SteppedAt;
        public float SinceStep;
        /// <summary><see cref="StrideWarp"/>: a foot planted this frame; the walk playing has no footstep events of its own.</summary>
        public bool Touchdown, Unvoiced;
        /// <summary>Upper body: the lean, aim and flinch springs, (R, F) radians.</summary>
        public Vector2 Lean, LeanV, Hit, HitV;
        public float AimPitch, AimV, AimW, AimWV;
        /// <summary><see cref="Mass"/>: the pelvis dip (world units, negative is down) and its spring velocity, and the
        /// eased vertical speed (world units/s) and its change, the landing's load.</summary>
        public float Dip, DipV, Climb, ClimbA;
        /// <summary>The look share's spring: the head's turn past the deadzone, (yaw, pitch) radians.</summary>
        public Vector2 Look, LookV;
        /// <summary><see cref="Follow"/>: the torso's turn off the animation as last measured and the frame before
        /// (rotation vectors, model frame) and whether both are measurements since the last reset, the spin it last drove
        /// with, each arm's lag behind the torso and its rate (rotation vectors, model frame), and the fade with its spring
        /// velocity; the steadied head's orientation in the heading frame, its spring's rate, and whether it is one.</summary>
        public Vector3 TorsoFix, TorsoFixWas, TorsoSpin, ArmR, ArmRV, ArmL, ArmLV, HeadV;
        public Quaternion HeadQ;
        public bool FixKnown, HeadKnown;
        public float FollowW, FollowWV;
        /// <summary>The engine's rotations, w-first, in <see cref="BoneIndex"/> order (4 floats each), and the
        /// parts they came from: a rotation is restored only onto the same part.</summary>
        public BoneQuats Rot;
        public BoneParts Parts;
    }

    /// <summary>One rotation per bone, in <see cref="BoneIndex"/> order.</summary>
    [InlineArray(Bones.Count)]
    internal struct BoneQuats
    {
        private Quat _bone;
    }

    /// <summary>One vector per bone, in <see cref="BoneIndex"/> order.</summary>
    [InlineArray(Bones.Count)]
    internal struct BoneVecs
    {
        private Vector3 _bone;
    }

    /// <summary>The engine part each bone came from, in <see cref="BoneIndex"/> order.</summary>
    [InlineArray(Bones.Count)]
    internal struct BoneParts
    {
        private long _bone;
    }

    /// <summary>What <see cref="Stride"/> remembers of one run it paced: its id, the engine's rate and the rate it wrote.</summary>
    internal struct RunRate
    {
        public long Id;
        public float Engine, Written;
    }

    /// <summary>The runs <see cref="Stride"/> tracks on one body.</summary>
    [InlineArray(Stride.TrackedRuns)]
    internal struct RunRates
    {
        private RunRate _run;
    }

    /// <summary>Open-addressed, <see cref="Probe"/> slots per lookup. ponytail: a gob that leaves the scene
    /// without dying keeps its slot until <see cref="StaleTicks"/>, and one evicted after that and later re-added
    /// carries its last correction as a fixed offset -- the upgrade is a <c>Gob::~Gob</c> death notice.</summary>
    private static Slot[] _slots = [];
    /// <summary>Stands in for a body slot that does not exist, where a <c>ref</c> is required.</summary>
    private static Slot _none;
    private const int SlotCount = 1024, Probe = 8;
    private static long StaleTicks => System.Diagnostics.Stopwatch.Frequency * 30;

    private static int _gobs, _gated, _noSlot, _followed, _turned;

    public void Initialize(ExtenderLog log, HookService hooks)
    {
        _ = AnimKnobs.G;
        SelfCheck(log);
        RigMap.Init(log);
        GaitClips.Init(log);
        if (!EngineLayout.Require(log, "Pose", "Gob", "Part", "MdlNode", "Model", "AnimRun", "Animation", "CHitInfo", "Scene")) return;
        if (!SymbolMap.Require(log, "Pose", SymbolMap.GobAnimate, SymbolMap.PartGetWorld, SymbolMap.GobGetAdjustedScale,
                               SymbolMap.SceneHitCheck, SymbolMap.HitInfoReset, SymbolMap.CurrentScene)) return;
        SceneRay.Init();
        PoseRig.Db = CX.Alloy.Renderer.On;
        if (!PoseRig.Db) log.Info("Pose: Alloy is off -- stride warping needs the clips' own keys, so the engine paces every walk");
        GetWorld = EngineFn.PartGetWorld;
        _scale = EngineFn.GobGetAdjustedScale;
        if (!SceneRay.Armed || !PoseRig.Db && (GetWorld == null || _scale == null)) { log.Info("Pose: standing down (symbols unresolved)"); return; }
        _creatures = EngineLayout.Require(log, "Pose.creature", "CNWCObject", "CNWCCreature");
        _slots = new Slot[SlotCount];
        _pending = new Pending[PendingCount];
        if (PoseRig.Db)
        {
            // DB mode owns Gob::Animate (the seam answers its slot), so the layer rides DbAnim's step instead
            DbAnim.PoseBefore = &DbBefore;
            DbAnim.PoseAfter = &DbAfter;
        }
        else
        {
            _next = hooks.ChainGobAnimate(&OnAnimate, 50);
            if (_next == null) { log.Info("Pose: standing down (Gob::Animate chain refused)"); return; }
        }
        _armed = true;
        UpperBody.Install(log, hooks);
        Footfall.Install(log, hooks);
        Diagonal.Install(log, hooks);
        log.Info($"Pose: {(PoseRig.Db ? "riding DbAnim's step" : "chained Gob::Animate")} (blend {On(AnimKnobs.Blend.V)}, diagonal {On(AnimKnobs.Diagonal.V)}, pivot {On(AnimKnobs.Pivot.V)}, footik {On(AnimKnobs.FootIk.V)}, stride {On(AnimKnobs.Stride.V)}, footsteps {On(AnimKnobs.Footsteps.V)}, lookbody {On(AnimKnobs.LookBody.V)}, follow {On(AnimKnobs.Follow.V)}, lean {AnimKnobs.Lean.V:0}%, mass {On(AnimKnobs.Mass.V)}, aim {On(AnimKnobs.Aim.V)}, hitreact {AnimKnobs.Hit.V:0}%)");
    }

    private static string On(bool b) => b ? "on" : "off";

    private static bool AnyOn => AnimKnobs.FootIk.V || AnimKnobs.Stride.V || AnimKnobs.Footsteps.V || AnimKnobs.SyncAttach.V
                                 || AnimKnobs.LookBody.V || AnimKnobs.Lean.V > 0f || AnimKnobs.Aim.V || AnimKnobs.Hit.V > 0f || AnimKnobs.Diagonal.V || AnimKnobs.Blend.V || AnimKnobs.Mass.V || AnimKnobs.Pivot.V
                                 || AnimKnobs.Follow.V;

    [UnmanagedCallersOnly]
    private static void OnAnimate(IntPtr gob, float dt)
    {
        GobAnimateFn next = *_next;
        if (!_armed || gob == IntPtr.Zero || new Gob(gob).ExternalType != GobKindCreature) { next(gob, dt); return; }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();

        IntPtr root = PoseRig.Root(gob);
        IntPtr body = Owner(gob);
        Bones bones = default;
        if (root != IntPtr.Zero) PoseRig.FindBones(gob, root, RigOf(gob, body), ref bones);
        bool found = bones.Legs;
        int slot = Find(gob);
        if (slot >= 0)
        {
            ref Slot s = ref _slots[slot];
            if (s.Applied)
            {
                if (s.Root == root) Restore(ref s, bones);
                s.Applied = false;
            }
        }
        if (body != IntPtr.Zero) { Attached(gob, root, bones, slot, body, next, dt, t0); return; }
        // out of the view volume the engine does not even advance the gob's time: nothing to correct
        found &= PoseRig.InView(gob) && Near(gob);
        if (slot >= 0 && found && _slots[slot].Root == root) Stride.Pre(ref _slots[slot], gob);

        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        next(gob, dt);
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();

        if (found && AnyOn) Correct(gob, root, bones, slot, dt);
        CopyToAttachments(gob, bones);
        _ticks += t1 - t0 + System.Diagnostics.Stopwatch.GetTimestamp() - t2;
    }

    /// <summary>DB mode's half of <see cref="OnAnimate"/>, before <c>DbAnim</c> poses <paramref name="obj"/>: the
    /// corrections come off (a crossfade blends from the column), a body's runs are paced, an attachment's are
    /// phase-locked to its body -- which DB mode stepped FIRST this frame, so the copied time is taken back by
    /// this frame's step. True when a pose column was written.</summary>
    private static bool DbBefore(DbId obj, float dt)
    {
        IntPtr gob = DbBody(obj, out IntPtr root, out Bones bones, out IntPtr body);
        if (gob == IntPtr.Zero) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int slot = Find(gob);
        if (slot >= 0)
        {
            ref Slot s = ref _slots[slot];
            if (s.Applied)
            {
                if (s.Root == root) Restore(ref s, bones);
                s.Applied = false;
            }
        }
        if (body != IntPtr.Zero)
        {
            int bs = Find(body);
            bool tracked = bs >= 0;
            Stride.Sync(gob, body, ref tracked ? ref _slots[bs] : ref _none, tracked, tracked ? Stride.Of(_slots[bs]) : 1f, dt);
        }
        else if (slot >= 0 && bones.Legs && PoseRig.InView(gob) && Near(gob) && _slots[slot].Root == root)
            Stride.Pre(ref _slots[slot], gob);
        _ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return PoseRig.TakeWrote();
    }

    /// <summary>DB mode's half of <see cref="OnAnimate"/>, after <c>DbAnim</c> posed <paramref name="obj"/> (and
    /// composed it): a body is corrected; an attachment saves its own pose and takes its body's final rotation
    /// on every bone both have -- the body already finished this frame, so it pulls rather than waits.</summary>
    private static bool DbAfter(DbId obj, float dt)
    {
        IntPtr gob = DbBody(obj, out IntPtr root, out Bones bones, out IntPtr body);
        if (gob == IntPtr.Zero) return false;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        int slot = Find(gob);
        if (body != IntPtr.Zero) DbAttached(gob, root, bones, slot, body);
        else if (bones.Legs && PoseRig.InView(gob) && Near(gob) && AnyOn) Correct(gob, root, bones, slot, dt);
        _ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return PoseRig.TakeWrote();
    }

    /// <summary>The creature gob (facade shell) behind <paramref name="obj"/> with its root part, bones and the body
    /// it is attached to (<see cref="Owner"/>); zero when the layer is off or the object is no creature's.</summary>
    private static IntPtr DbBody(DbId obj, out IntPtr root, out Bones bones, out IntPtr body)
    {
        root = body = IntPtr.Zero;
        bones = default;
        if (!_armed) return IntPtr.Zero;
        IntPtr gob = Facade.ObjectShell(obj);
        if (gob == IntPtr.Zero || new Gob(gob).ExternalType != GobKindCreature) return IntPtr.Zero;
        root = PoseRig.Root(gob);
        body = Owner(gob);
        if (root != IntPtr.Zero) PoseRig.FindBones(gob, root, RigOf(gob, body), ref bones);
        return gob;
    }

    /// <summary>The names <paramref name="gob"/>'s bones go by: its creature's rig, read off the body (<paramref name="owner"/>
    /// for an attachment), so an attachment matches by its body's names.</summary>
    private static byte[][] RigOf(IntPtr gob, IntPtr owner)
    {
        IntPtr creature = _creatures ? new Gob(gob).ExternalData : IntPtr.Zero;
        return RigMap.Of(owner != IntPtr.Zero ? owner : gob, creature == IntPtr.Zero ? -1 : RigMap.Appearance(creature));
    }

    /// <summary><see cref="Attached"/> and <see cref="CopyToAttachments"/> as one pull: save the attachment's
    /// own pose (restored before its next one), then copy the body's final local rotations onto it.</summary>
    private static void DbAttached(IntPtr gob, IntPtr root, in Bones bones, int slot, IntPtr body)
    {
        bool tracked = Find(body) >= 0;
        bool copy = root != IntPtr.Zero && (AnimKnobs.SyncAttach.V || tracked) && PoseRig.InView(body);
        if (slot < 0 && copy) slot = Claim(gob);
        if (!copy || slot < 0) return;
        ref Slot s = ref _slots[slot];
        s.Stamp = System.Diagnostics.Stopwatch.GetTimestamp();
        Save(ref s, root, bones);
        s.Root = root;
        s.Applied = true;
        Bones from = default;
        IntPtr broot = PoseRig.Root(body);
        if (broot != IntPtr.Zero) PoseRig.FindBones(body, broot, RigOf(body, IntPtr.Zero), ref from);
        for (int k = 0; k < Bones.Count; k++)
            if (bones[k] != IntPtr.Zero && from[k] != IntPtr.Zero) PoseRig.SetLocal(bones[k], PoseRig.Local(from[k]));
        _followed++;
    }

    /// <summary>Within <c>anim.distance</c> of the player's creature (always, when the player cannot be
    /// resolved). The player's position is read once per <see cref="PlayerTicks"/>, not per creature.</summary>
    private static bool Near(IntPtr gob)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (now - _playerAt > PlayerTicks)
        {
            _playerAt = now;
            IntPtr me = UpperBody.Ready ? CameraService.PlayerCreature() : IntPtr.Zero;
            _hasPlayer = me != IntPtr.Zero;
            if (_hasPlayer) _player = new CNWCObject(me).Position;
        }
        if (!_hasPlayer) return true;
        float d = AnimKnobs.Distance.V;
        return Vector3.DistanceSquared(PoseRig.Position(gob), _player) <= d * d;
    }

    /// <summary>Whether <see cref="Diagonal"/> swaps <paramref name="gob"/>'s diagonal for the straight cycle: with
    /// <c>anim.diagonal</c> on, for a body the layer corrected within <see cref="FreshTicks"/>, so its turn follows. The
    /// answer is kept on the slot: the gob plays whichever name it was last given, and the turn goes by that.</summary>
    internal static bool Swaps(IntPtr gob)
    {
        if (!_armed || gob == IntPtr.Zero) return false;
        int slot = Find(gob);
        if (slot < 0) return false;
        ref Slot s = ref _slots[slot];
        s.Swapped = AnimKnobs.Diagonal.V && s.Hips && System.Diagnostics.Stopwatch.GetTimestamp() - s.Stamp < FreshTicks;
        return s.Swapped;
    }

    /// <summary>How recently a body must have been corrected to count as one the layer is posing: several frames,
    /// so a slow frame does not flip the swap.</summary>
    private static long FreshTicks => System.Diagnostics.Stopwatch.Frequency / 4;
    /// <summary>How recently a body must have been corrected for last frame's pose to be carried into a blend.</summary>
    private static long TrackTicks => System.Diagnostics.Stopwatch.Frequency / 10;

    private static Vector3 _player;
    private static bool _hasPlayer;
    private static long _playerAt;
    private static long PlayerTicks => System.Diagnostics.Stopwatch.Frequency / 200;
    private static long _ticks;

    /// <summary>The body's corrections, when it is in the world scene and has (or gets) a slot.</summary>
    private static void Correct(IntPtr gob, IntPtr root, in Bones bones, int slot, float dt)
    {
        IntPtr scene = SceneRay.WorldScene();
        if (scene == IntPtr.Zero || !PoseRig.InScene(gob, scene)) return;
        if (slot < 0 && (slot = Claim(gob)) < 0) { _noSlot++; return; }
        Apply(ref _slots[slot], gob, root, bones, scene, dt);
    }

    /// <summary>The body gob a creature gob is attached to, or zero when it IS the body (or has no creature).
    /// A cloak, robe, tail or wings carries the creature as external data too (<c>CNWCCreature::LoadModel</c>),
    /// and ~85% of robe models have a full set of leg bones, so "has legs" does not tell a body; the creature's
    /// own model does.</summary>
    private static IntPtr Owner(IntPtr gob)
    {
        if (!_creatures) return IntPtr.Zero;
        IntPtr creature = new Gob(gob).ExternalData;
        if (creature == IntPtr.Zero) return IntPtr.Zero;
        IntPtr body = GobOf(creature);
        return body == gob ? IntPtr.Zero : body;
    }

    /// <summary>An attachment computes nothing of its own: before it animates its runs are phase-locked to the body's
    /// (<see cref="Stride.Sync"/>: time, rate times the stride factor, blend), and after it saves its engine pose and
    /// waits for its body (<see cref="Pending"/>) -- it animates just before the body in the same frame, as
    /// <c>Scene::Animate</c> walks backwards -- which then copies onto it (<see cref="CopyToAttachments"/>).</summary>
    private static void Attached(IntPtr gob, IntPtr root, in Bones bones, int slot, IntPtr body,
                                 GobAnimateFn next, float dt, long t0)
    {
        int bs = Find(body);
        bool tracked = bs >= 0;
        Stride.Sync(gob, body, ref tracked ? ref _slots[bs] : ref _none, tracked, tracked ? Stride.Of(_slots[bs]) : 1f);
        bool copy = root != IntPtr.Zero && (AnimKnobs.SyncAttach.V || tracked) && PoseRig.InView(body);
        if (slot < 0 && copy) slot = Claim(gob);

        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        next(gob, dt);
        _ticks += t1 - t0;

        if (!copy || slot < 0) return;
        ref Slot s = ref _slots[slot];
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        s.Stamp = now;
        Save(ref s, root, bones);
        s.Root = root;
        s.Applied = true;
        Pend(body, gob, slot, now);
    }

    /// <summary>Attachments that animated and wait for their body this frame: (body, attachment, its slot,
    /// when). A body consumes its own; an entry older than <see cref="PendingTicks"/> (its body never animated)
    /// is dropped unread, so a stale attachment pointer is never written through.</summary>
    private struct Pending { public IntPtr Body, Gob; public int Slot; public long At; }
    private static Pending[] _pending = [];
    private static int _pendingCount;
    private const int PendingCount = 64;
    private static long PendingTicks => System.Diagnostics.Stopwatch.Frequency / 20;

    private static void Pend(IntPtr body, IntPtr gob, int slot, long now)
    {
        for (int i = _pendingCount - 1; i >= 0; i--)
            if (_pending[i].Gob == gob || now - _pending[i].At > PendingTicks) _pending[i] = _pending[--_pendingCount];
        if (_pendingCount < PendingCount) _pending[_pendingCount++] = new Pending { Body = body, Gob = gob, Slot = slot, At = now };
    }

    /// <summary>After the body's own pose and corrections: each waiting attachment takes the body's FINAL local
    /// rotation on every bone both have -- its skin binds to bones inside its own gob (<c>PartSkin</c>), so nothing
    /// the body does reaches it otherwise. Only onto a part the attachment saved this frame, from a slot still its
    /// own; the body's raise is not copied, since the attachment hangs off the body's root part already.</summary>
    private static void CopyToAttachments(IntPtr body, in Bones bones)
    {
        if (_pendingCount == 0) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = _pendingCount - 1; i >= 0; i--)
        {
            Pending p = _pending[i];
            if (p.Body != body) continue;
            _pending[i] = _pending[--_pendingCount];
            if (now - p.At > PendingTicks) continue;
            ref Slot s = ref _slots[p.Slot];
            if (s.Gob != p.Gob || !s.Applied) continue;
            for (int k = 0; k < Bones.Count; k++)
                if (s.Parts[k] != 0 && bones[k] != IntPtr.Zero)
                    PoseRig.SetLocal((IntPtr)s.Parts[k], PoseRig.Local(bones[k]));
            _followed++;
        }
    }

    /// <summary>One pass over the part tree, matching every bone name at once (first match wins).</summary>
    internal static void FindBones(IntPtr part, byte[][] rig, ref Bones bones, int depth)
    {
        if (depth > MaxDepth) return;
        IntPtr node = new Part(part).Node.Ptr;
        int bone = node == IntPtr.Zero ? -1 : BoneIndex(new MdlNode(node).Chars, rig);
        if (bone >= 0 && bones[bone] == IntPtr.Zero) bones[bone] = part;
        IntPtr* kids = (IntPtr*)new Part(part).Children;
        int n = new Part(part).ChildCount;
        for (int i = 0; i < n && kids != null; i++)
            if (kids[i] != IntPtr.Zero) FindBones(kids[i], rig, ref bones, depth + 1);
    }

    /// <summary>The bone <paramref name="name"/> is in <paramref name="rig"/>, whole and case-insensitive: 0..5 for r-thigh,
    /// r-shin, r-foot, l-thigh, l-shin, l-foot, 6 torso, 7 neck, 8 head, 9 pelvis, 10..15 for r-bicep, r-forearm, r-hand,
    /// l-bicep, l-forearm, l-hand; -1 for none.</summary>
    internal static int BoneIndex(byte* name, byte[][] rig)
    {
        for (int i = 0; i < Bones.Count; i++)
            if (rig[i].Length > 0 && NameIs(name, rig[i])) return i;
        return -1;
    }

    /// <summary>ASCII case-insensitive compare of a C string against a lowercase NUL-terminated name.</summary>
    private static bool NameIs(byte* s, ReadOnlySpan<byte> lit)
    {
        for (int i = 0; i < lit.Length; i++)
            if (Lower(s[i]) != lit[i]) return false;
        return true;
    }

    internal static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c | 0x20) : c;

    /// <summary>True when any animation on the gob's run list is one <see cref="Denied"/> names: the one being
    /// blended from counts as much as the one blended to, so a sit-down or a get-up never gets a correction.</summary>
    private static bool Gated(IntPtr gob)
    {
        int n = PoseRig.RunCount(gob);
        bool gated = false;
        for (int i = 0; i < n; i++)
        {
            RunRef r = PoseRig.Run(gob, i);
            if (r.IsNull) continue;
            if (r.Denied) gated = true;
        }
        return gated;
    }

    internal static bool IsDenied(byte* name)
    {
        ReadOnlySpan<byte> list = Denied;
        while (list.Length > 0)
        {
            int end = list.IndexOf((byte)0);
            ReadOnlySpan<byte> prefix = list[..end];
            int i = 0;
            while (i < prefix.Length && Lower(name[i]) == prefix[i]) i++;
            if (i == prefix.Length) return true;
            list = list[(end + 1)..];
        }
        return false;
    }

    /// <summary>The creature animation ids that walk or run (the chase drivers' test, <see cref="ObjAnimation"/>).</summary>
    internal static bool IsLocomotion(int anim) => anim is 2 or 3 or 4 or 0x4e or 0x4f or >= 0x54 and <= 0x57;

    private static int Hash(IntPtr gob) => (int)(((ulong)gob >> 4 ^ (ulong)gob >> 14) & (SlotCount - 1));

    private static int Find(IntPtr gob)
    {
        int h = Hash(gob);
        for (int i = 0; i < Probe; i++)
            if (_slots[(h + i) & (SlotCount - 1)].Gob == gob) return (h + i) & (SlotCount - 1);
        return -1;
    }

    /// <summary>A free slot in the probe window, else one untouched for <see cref="StaleTicks"/>; -1 = the gob
    /// goes uncorrected this frame (it has nothing to restore, so skipping is always safe).</summary>
    private static int Claim(IntPtr gob)
    {
        int h = Hash(gob);
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int i = 0; i < Probe; i++)
        {
            ref Slot s = ref _slots[(h + i) & (SlotCount - 1)];
            if (s.Gob != IntPtr.Zero && now - s.Stamp < StaleTicks) continue;
            s = default;
            s.Gob = gob;
            s.Stamp = now;
            s.RateNext = 1f;
            return (h + i) & (SlotCount - 1);
        }
        return -1;
    }

    /// <summary>Puts the engine's own values back BEFORE it animates: a crossfade blends from the part's current local
    /// value (<c>NewController::Control</c>) and an unkeyed part keeps whatever it holds, so a correction left in place
    /// would leak into the next crossfade and accumulate. The root first and on its own (no animation keys it); each
    /// rotation only onto the part it was saved from and found again this frame, as a part the tree lost may be freed.</summary>
    private static void Restore(ref Slot s, in Bones bones)
    {
        PoseRig.SetPos(s.Root, s.RootPos);
        for (int i = 0; i < Bones.Count; i++)
            if (bones[i] != IntPtr.Zero && bones[i] == (IntPtr)s.Parts[i]) PoseRig.SetLocal(bones[i], s.Rot[i]);
    }

    private static void Save(ref Slot s, IntPtr root, in Bones bones)
    {
        s.RootPos = PoseRig.Pos(root);
        for (int i = 0; i < Bones.Count; i++)
        {
            s.Parts[i] = (long)bones[i];
            if (bones[i] != IntPtr.Zero) s.Rot[i] = PoseRig.Local(bones[i]);
        }
    }

    private static void Apply(ref Slot s, IntPtr gob, IntPtr root, in Bones bones, IntPtr scene, float dt)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp(), since = now - s.Stamp;
        s.Stamp = now;
        float scale = PoseRig.Scale(gob, _scale);
        if (!(scale > 1e-4f)) return;
        World(root, out Vector3 rootW, out Quaternion rootWq);
        Vector3 rootL = PoseRig.Pos(root);
        Quaternion adj = rootWq * Quaternion.Conjugate(Local(root));
        Vector3 adjPos = rootW - Vector3.Transform(rootL * scale, adj);
        Axes(adj, out _, out Vector3 fwdH, out Vector3 right);

        bool snap = s.Root != root || Vector3.DistanceSquared(adjPos, s.LastPos) > Teleport * Teleport;
        Vector3 moved = snap ? Vector3.Zero : adjPos - s.LastPos;
        float weight = FootPlacement.Weight(ref s.Grade, ref s.Moving, moved, dt, scale);
        Motion(ref s, moved, fwdH, snap, dt);
        s.LastPos = adjPos;
        s.Root = root;

        bool gated = Gated(gob);
        if (gated) _gated++;
        IntPtr creature = _creatures ? new Gob(gob).ExternalData : IntPtr.Zero;
        int anim = creature != IntPtr.Zero ? new CNWCObject(creature).Animation : 0;
        s.Loco = !gated && s.Moving > 0f && creature != IntPtr.Zero && IsLocomotion(anim);
        s.Shuffling = !gated && creature != IntPtr.Zero && Skating.IsShuffle(anim);
        s.Fwd = fwdH; s.Right = right;

        bool tracked = !snap && since < TrackTicks;
        Gait.Read(ref s, creature, gob, anim, tracked);
        Weight.Step(ref s, !tracked, dt);
        bool blending = Transition.Begin(ref s, anim, tracked, dt);
        s.Hips = bones.Pelvis != IntPtr.Zero;
        bool turned = Diagonal.Step(ref s, anim, fwdH, right, snap, tracked, dt);
        // the animation layer goes first, over the engine's own values: every correction below reads its pose
        bool saved = blending || turned;
        if (saved) Save(ref s, root, bones);
        Transition.Pose(ref s, bones, blending, tracked, dt);
        Quaternion legs = adj;
        if (turned)
        {
            Diagonal.Write(bones, s.Turn);
            legs *= Quaternion.CreateFromAxisAngle(Vector3.UnitZ, s.Turn);
            _turned++;
        }
        Axes(legs, out Vector3 legFwd, out Vector3 legFwdH, out Vector3 legRight);

        World(bones.RFoot, out Vector3 rAnkle, out _);
        World(bones.LFoot, out Vector3 lAnkle, out _);
        Quaternion toModel = Quaternion.Conjugate(legs);
        Vector3 mr = Vector3.Transform(rAnkle - adjPos, toModel) / scale, ml = Vector3.Transform(lAnkle - adjPos, toModel) / scale;
        // the engine's own speed, not the drawn step: exact where the whole-millisecond clock leaves the measured one
        // +/-14 % a frame -- a shuffle's gap over its countdown, a path's or a drive's rate times its factor
        float ground = s.Shuffling ? Skating.Speed(creature, s.Speed) : s.Gait != GaitMode.None ? s.GaitSpeed : s.Speed;
        StrideWarp.Step(ref s, gob, bones, legs, adjPos, scale, ground, s.Loco || s.Shuffling, gated || snap, dt,
                        out Vector3 warpR, out Vector3 warpL, out float drop);
        Settle.Feet(ref s, AnimKnobs.Stride.V && PoseRig.Db && !gated && !snap && dt > 0f, rAnkle + warpR, lAnkle + warpL,
                    Settle.Fade(Gait.Driven(gob)), s.Hips && Transition.Movement(anim) && !IsLocomotion(anim), s.YawRate,
                    scale, dt, out Vector3 setR, out Vector3 setL);
        warpR += setR;
        warpL += setL;

        FootPlacement.Compute(ref s, scene, gob, rAnkle + warpR, lAnkle + warpL, adjPos.Z, legFwdH, legRight, scale, weight, gated, snap, dt, out var feet);
        Footfall.Step(ref s, creature, snap, dt);
        UpperBody.Step(ref s, creature, rootW, rootW.Z - adjPos.Z, scale, s.Speed, gated, snap, dt);
        UpperBody.Rotations(s, gated, out Vector2 torsoRot, out Vector2 neckRot);

        float body = MathF.Min(feet.OffR, feet.OffL) + Mass.Step(ref s, moved.Z, scale, gated, snap, dt) - drop;
        bool ik = !gated && (MathF.Abs(feet.OffR - body) + MathF.Abs(feet.OffL - body) > FootPlacement.MinLift
                             || !feet.TiltR.IsIdentity || !feet.TiltL.IsIdentity
                             || warpR.LengthSquared() + warpL.LengthSquared() > FootPlacement.MinLift * FootPlacement.MinLift);
        bool upper = bones.Torso != IntPtr.Zero && torsoRot.LengthSquared() + neckRot.LengthSquared() > 1e-8f;
        bool look = UpperBody.LookStep(ref s, bones, !gated && AnimKnobs.LookBody.V && PoseRig.LookingAt(gob), snap, dt);
        bool follow = Follow.Step(ref s, anim, gated, !tracked, scale, dt);
        if (!saved && MathF.Abs(body) < FootPlacement.MinLift && !ik && !upper && !look && !follow) return;

        if (!saved) Save(ref s, root, bones);
        s.Applied = true;
        _gobs++;
        PoseRig.SetPos(root, rootL + Vector3.Transform(new Vector3(0f, 0f, body / scale), toModel));
        if (ik)
        {
            float cap = AnimKnobs.Lift.V * scale;
            Leg(bones.RThigh, bones.RShin, bones.RFoot, warpR + new Vector3(0f, 0f, MathF.Min(feet.OffR - body, cap)), legFwd, feet.TiltR);
            Leg(bones.LThigh, bones.LShin, bones.LFoot, warpL + new Vector3(0f, 0f, MathF.Min(feet.OffL - body, cap)), legFwd, feet.TiltL);
        }
        if (look) UpperBody.ShareLook(bones, s.Look.X, s.Look.Y);
        if (upper) UpperBody.Write(bones.Torso, bones.Neck, fwdH, right, torsoRot, neckRot);
        // after every torso write: the arms and head hang from it, and the measure reads what it was turned by
        Follow.Measure(ref s, bones.Torso);
        if (follow) Follow.Write(ref s, bones, fwdH, right, dt);
    }

    /// <summary>The model's forward in the world, its horizontal unit, and the horizontal right, for the gob-frame
    /// rotation <paramref name="adj"/>.</summary>
    private static void Axes(Quaternion adj, out Vector3 fwd, out Vector3 fwdH, out Vector3 right)
    {
        fwd = Vector3.Transform(Vector3.UnitY, adj);
        fwdH = new Vector3(fwd.X, fwd.Y, 0f);
        fwdH = fwdH.LengthSquared() > 1e-8f ? Vector3.Normalize(fwdH) : Vector3.UnitY;
        right = new Vector3(fwdH.Y, -fwdH.X, 0f);
    }

    /// <summary>Eases the measured motion: ground speed, yaw rate and signed slope over <see cref="MotionTime"/>,
    /// forward acceleration over <see cref="AccelTime"/>. A snap zeroes the rates rather than reading a teleport as a sprint.</summary>
    internal static void Motion(ref Slot s, Vector3 moved, Vector3 fwdH, bool snap, float dt)
    {
        float yaw = MathF.Atan2(fwdH.Y, fwdH.X);
        if (snap || !(dt > 0f)) { s.Yaw = yaw; s.YawRate = s.Accel = s.Speed = s.Slope = s.PathRate = 0f; s.Vel = Vector2.Zero; s.TravelKnown = false; return; }
        float a = MathF.Min(1f, dt / MotionTime);
        s.Vel += (new Vector2(moved.X, moved.Y) / dt - s.Vel) * a;
        float run = MathF.Sqrt(moved.X * moved.X + moved.Y * moved.Y);
        float speed = s.Speed + (run / dt - s.Speed) * a;
        s.Accel += ((speed - s.Speed) / dt - s.Accel) * MathF.Min(1f, dt / AccelTime);
        s.Speed = speed;
        float turn = yaw - s.Yaw;
        if (turn > MathF.PI) turn -= 2f * MathF.PI; else if (turn < -MathF.PI) turn += 2f * MathF.PI;
        s.YawRate += (turn / dt - s.YawRate) * a;
        s.Yaw = yaw;
        float slow = MathF.Min(1f, dt / AccelTime), path = 0f;
        if (s.Vel.LengthSquared() > MinTravel * MinTravel)
        {
            float h = MathF.Atan2(s.Vel.Y, s.Vel.X);
            if (s.TravelKnown)
            {
                path = h - s.TravelYaw;
                if (path > MathF.PI) path -= 2f * MathF.PI; else if (path < -MathF.PI) path += 2f * MathF.PI;
                path /= dt;
            }
            s.TravelYaw = h;
            s.TravelKnown = true;
        }
        else s.TravelKnown = false;
        s.PathRate += (path - s.PathRate) * slow;
        if (run > 1e-4f) s.Slope += (moved.Z / run - s.Slope) * a;
    }

    /// <summary>The path turn, not the facing's: a facing snapped 90 degrees over 0.1 s on a straight 4 m/s walk
    /// leaves <see cref="Slot.PathRate"/> near 0, and a 0.5 rad/s curve settles near 0.5.</summary>
    private static bool PathTurnCheck()
    {
        const float dt = 1f / 60f;
        Slot s = default;
        float worst = 0f;
        for (int i = 0; i < 60; i++)
        {
            float yaw = i < 30 ? 0f : MathF.Min(1f, (i - 30) * dt / 0.1f) * MathF.PI / 2f;
            Motion(ref s, new Vector3(0f, 4f * dt, 0f), new Vector3(-MathF.Sin(yaw), MathF.Cos(yaw), 0f), i == 0, dt);
            worst = MathF.Max(worst, MathF.Abs(s.PathRate));
        }
        bool straight = worst < 0.05f && MathF.Abs(UpperBody.LeanTarget(s.Speed, s.PathRate, 0f, 0f, true).Y) < 0.01f;
        Slot c = default;
        for (int i = 0; i < 180; i++)
        {
            float h = 0.5f * i * dt;
            Motion(ref c, new Vector3(-MathF.Sin(h), MathF.Cos(h), 0f) * (4f * dt), new Vector3(-MathF.Sin(h), MathF.Cos(h), 0f), i == 0, dt);
        }
        return straight && MathF.Abs(c.PathRate - 0.5f) < 0.05f;
    }

    /// <summary>Starts a flinch on <paramref name="gob"/>: <paramref name="peak"/> radians toward
    /// <paramref name="push"/>. Only a gob the layer already corrects has a slot; any other (no humanoid legs,
    /// not in the world scene) is not one the flinch could be drawn on.</summary>
    internal static void Kick(IntPtr gob, Vector3 push, float peak)
    {
        if (!_armed) return;
        int slot = Find(gob);
        if (slot < 0) return;
        ref Slot s = ref _slots[slot];
        s.HitV += UpperBody.Launch(push, s.Fwd, s.Right, peak);
    }

    /// <summary>True while <see cref="Footfall"/> steps <paramref name="creature"/>'s body: it stepped within
    /// <paramref name="hold"/> ticks and was locomoting then.</summary>
    internal static bool Stepping(IntPtr creature, long hold)
    {
        if (!_armed || creature == IntPtr.Zero) return false;
        IntPtr gob = GobOf(creature);
        int slot = gob == IntPtr.Zero ? -1 : Find(gob);
        if (slot < 0) return false;
        ref Slot s = ref _slots[slot];
        return s.Stepping && System.Diagnostics.Stopwatch.GetTimestamp() - s.SteppedAt < hold;
    }

    /// <summary>A creature's Gob, through the engine's own accessor: <c>GetModel(0xff, 1)</c> forwards to the
    /// CNWCAnimBase at +0x78 (as <see cref="HeadTrackService"/> reaches it).</summary>
    internal static IntPtr GobOf(IntPtr creature)
    {
        if (*(IntPtr*)creature == IntPtr.Zero) return IntPtr.Zero;
        return new CNWCCreature(creature).GetModel(0xff, 1);
    }

    /// <summary>Moves one ankle by <paramref name="delta"/> world units: two-bone IK in world space, written back as
    /// locals. The foot keeps its animated world orientation, turned by <paramref name="tilt"/> about the ankle.
    /// A chain whose parents are not thigh-&gt;shin-&gt;foot is left alone.</summary>
    private static void Leg(IntPtr thigh, IntPtr shin, IntPtr foot, Vector3 delta, Vector3 fwd, Quaternion tilt)
    {
        bool moves = delta.LengthSquared() >= FootPlacement.MinLift * FootPlacement.MinLift;
        if (!moves && tilt.IsIdentity) return;
        IntPtr hip = PoseRig.Parent(thigh);
        if (hip == IntPtr.Zero || PoseRig.Parent(shin) != thigh || PoseRig.Parent(foot) != shin) return;
        World(shin, out Vector3 k, out Quaternion qs);
        World(foot, out Vector3 a, out Quaternion qf);
        if (moves)
        {
            World(hip, out _, out Quaternion qp);
            World(thigh, out Vector3 h, out Quaternion qt);
            if (TwoBone(h, k, a, a + delta, fwd, out Quaternion r1, out Quaternion r2))
            {
                Quaternion qt2 = Quaternion.Normalize(r1 * qt);
                qs = Quaternion.Normalize(r2 * r1 * qs);
                SetLocal(thigh, Quaternion.Conjugate(qp) * qt2);
                SetLocal(shin, Quaternion.Conjugate(qt2) * qs);
            }
        }
        SetLocal(foot, Quaternion.Conjugate(qs) * tilt * qf);
    }

    /// <summary>The period clause: corrected gobs, casts, what the casts landed on, the gobs refused (an
    /// animation gate, or no slot), and each module's count. Resets its counters -- one caller, once per period.</summary>
    internal static string Stats(int frames)
    {
        if (!_armed || frames <= 0) return "";
        float f = frames;
        string s = FormattableString.Invariant(
            $"pose {System.Diagnostics.Stopwatch.GetElapsedTime(0, _ticks).TotalMilliseconds / f:F2} ms/f, {_gobs / f:F1} gobs/f, {FootPlacement.Rays / f:F1} rays/f, hits {FootPlacement.Tile / f:F1} tile + {FootPlacement.Placeable / f:F1} placeable/f, refused {_gated / f:F1} gated + {_noSlot / f:F1} no slot/f, {FootPlacement.Stairs / f:F1} on stairs/f, {Stride.Strided / f:F1} strided, {StrideWarp.Period(frames)}, {Stride.Synced / f:F1} attachment runs synced + {_followed / f:F1} attachments posed/f, {UpperBody.Looked / f:F1} looking/f, {Follow.Trailed / f:F1} trailing/f, {UpperBody.Aimed / f:F1} aiming/f, {Mass.Loaded / f:F1} pelvis loaded/f, {UpperBody.Hits} flinches, {Footfall.Steps} footsteps on contact + {Footfall.Skipped} authored skipped, {_turned / f:F1} turned into a diagonal/f + {Diagonal.Swapped} diagonal names swapped, {Transition.Blends} movement changes blended, {Settle.Steps} settle steps");
        _gobs = _gated = _noSlot = _followed = _turned = Diagonal.Swapped = Transition.Blends = Mass.Loaded = Follow.Trailed = Settle.Steps = 0;
        _ticks = 0;
        FootPlacement.Rays = FootPlacement.Tile = FootPlacement.Placeable = FootPlacement.Stairs = 0;
        Stride.Strided = Stride.Synced = UpperBody.Looked = UpperBody.Aimed = UpperBody.Hits = Footfall.Steps = Footfall.Skipped = 0;
        return s;
    }

    /// <summary>The <c>/anim</c> status line: the pose layer that post-processes the animations.</summary>
    internal static string Report() => !_armed ? "pose layer unhooked" : "pose layer on";

    /// <summary>The pure parts off-device: bone names, the animation gate, the springs, the two-bone solve, the clip
    /// sampler, the gait analysis and the stride warp, and the signs of lean, aim and flinch.</summary>
    public static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "Pose.SelfCheck");
        byte[][] st = RigMap.Stock;
        fixed (byte* a = "RThigh_g\0"u8, b = "lfoot_g\0"u8, c = "rthigh_g01\0"u8, d = "Torso_g\0"u8, e = "neck_g\0"u8, g = "head_g\0"u8)
            check.Expect(BoneIndex(a, st) == 0 && BoneIndex(b, st) == 5 && BoneIndex(c, st) == -1 && BoneIndex(d, st) == 6
                         && BoneIndex(e, st) == 7 && BoneIndex(g, st) == 8,
                         "bone names match whole and case-insensitive");
        fixed (byte* a = "sitdown\0"u8, b = "ckdbckps\0"u8, c = "pause1\0"u8, d = "cwalkf\0"u8, e = "gustandb\0"u8, f = "greeting\0"u8)
            check.Expect(IsDenied(a) && IsDenied(b) && IsDenied(e) && !IsDenied(c) && !IsDenied(d) && !IsDenied(f),
                         "sitting, knockdown and getting up are gated; standing, walking and greeting are not");

        float x = 0f, v = 0f, peak = 0f;
        for (int i = 0; i < 120; i++) { Smooth(ref x, ref v, 0.3f, 1f / 60f, 0.12f); peak = MathF.Max(peak, x); }
        float y = 0f, w = 0f;
        Smooth(ref y, ref w, 0.3f, 0.5f, 0.12f);
        check.Expect(MathF.Abs(x - 0.3f) < 1e-3f && peak <= 0.3f + 1e-4f && y > 0f && y <= 0.3f,
                     "the spring settles on its target without overshoot, and a hitch-sized dt stays bounded");

        Vector3 h = new(0f, 0f, 1f), k = new(0f, 0.05f, 0.5f), an = new(0f, 0f, 0f), t = new(0f, 0f, 0.2f);
        bool ok = TwoBone(h, k, an, t, Vector3.UnitY, out Quaternion r1, out Quaternion r2);
        Vector3 k2 = h + Vector3.Transform(k - h, r1);
        Vector3 a2 = k2 + Vector3.Transform(Vector3.Transform(an - k, r1), r2);
        check.Expect(ok && Vector3.Distance(a2, t) < 1e-3f, "the ankle reaches the lifted target");
        check.Expect(MathF.Abs(Vector3.Distance(h, k2) - Vector3.Distance(h, k)) < 1e-4f
                     && MathF.Abs(Vector3.Distance(k2, a2) - Vector3.Distance(k, an)) < 1e-4f, "bone lengths are kept");
        check.Expect(k2.Y > k.Y, "the knee bends forward");
        ok = TwoBone(h, new Vector3(0.05f, 0f, 0.5f), an, t, Vector3.UnitY, out r1, out _);
        Vector3 k3 = h + Vector3.Transform(new Vector3(0.05f, 0f, -0.5f), r1);
        check.Expect(ok && k3.Y > 0.1f && MathF.Abs(k3.X) < 0.06f, "a leg splayed sideways still bends its knee forward, not further out");
        ok = TwoBone(h, new Vector3(0f, 0f, 0.5f), an, t, Vector3.UnitY, out r1, out _);
        check.Expect(ok && Vector3.Transform(new Vector3(0f, 0f, -0.5f), r1).Y > 0.1f, "a straight leg bends its knee forward");
        t = new Vector3(0.1f, 0.15f, 0.05f);
        ok = TwoBone(h, k, an, t, Vector3.UnitY, out r1, out r2);
        k2 = h + Vector3.Transform(k - h, r1);
        a2 = k2 + Vector3.Transform(Vector3.Transform(an - k, r1), r2);
        check.Expect(ok && Vector3.Distance(a2, t) < 1e-3f, "the ankle reaches a target moved sideways and forward (a locked foot)");
        Vector3 kx = new(0.12f, 0.03f, 0.5f);
        ok = TwoBone(h, kx, an, an, Vector3.UnitY, out r1, out r2);
        bool still = ok && Vector3.Distance(h + Vector3.Transform(kx - h, r1), kx) < 1e-4f;
        TwoBone(h, kx, an, an + new Vector3(0f, 0.002f, 0f), Vector3.UnitY, out r1, out _);
        Vector3 kSmall = h + Vector3.Transform(kx - h, r1);
        check.Expect(still && Vector3.Distance(kSmall, kx) < 0.01f,
                     "a knee bent sideways (legs crossing) stays in its own plane: no move returns it exactly, a tiny move barely shifts it");
        check.Expect(Soften(0.5f, 0.6f, 1f) == 0.5f && Soften(0.99f, 0.8f, 1f) < 0.99f && Soften(5f, 0.8f, 1f) <= 1f
                     && Soften(0.97f, 0.97f, 1f) == 0.97f,
                     "soft IK eases a stretch into full reach, never past it, and leaves an unmoved ankle alone");

        float gr = 0f, mv = 0f, dt60 = 1f / 60f, wUp = 1f, wFlat = 0f, wStop = 0f;
        bool steady = true;
        for (int i = 0; i < 30; i++)
        {
            wUp = FootPlacement.Weight(ref gr, ref mv, (i & 1) == 0 ? new Vector3(0.08f, 0f, 0.05f) : Vector3.Zero, dt60, 1f);
            if (i > 10 && wUp != FootPlacement.StairWeight) steady = false;
        }
        for (int i = 0; i < 20; i++) wStop = FootPlacement.Weight(ref gr, ref mv, Vector3.Zero, dt60, 1f);
        float gf = 0f, mf = 0f;
        for (int i = 0; i < 30; i++) wFlat = FootPlacement.Weight(ref gf, ref mf, new Vector3(0.04f, 0f, 0f), dt60, 1f);
        check.Expect(steady && wStop == 1f && wFlat == 1f,
                     "walking a stair-steep walkmesh (position advancing every other frame) holds the stairs weight without flicker; stopping on it, or walking a flat platform, is full placement");
        Vector3 toe = Vector3.Transform(Vector3.UnitY, FootPlacement.FootTilt(Vector3.UnitY, Vector3.UnitX, 0.5f, 0f));
        Vector3 sole = Vector3.Transform(Vector3.UnitX, FootPlacement.FootTilt(Vector3.UnitY, Vector3.UnitX, 0f, -0.3f));
        check.Expect(MathF.Abs(toe.Z / toe.Y - 0.5f) < 1e-3f && MathF.Abs(sole.Z / sole.X + 0.3f) < 1e-3f
                     && FootPlacement.FootTilt(Vector3.UnitY, Vector3.UnitX, 0f, 0f).IsIdentity,
                     "a tilted foot lies on the slope: toe up a rise, sideways down a fall, untouched on the flat");


        Vector3 turned = Vector3.Transform(Vector3.UnitY, UpperBody.Turn(Vector3.UnitZ, Vector3.UnitX, 0.3f, 0.2f));
        check.Expect(turned.X < -0.2f && turned.Z > 0.1f,
                     "look share: a positive yaw turns the chest left (as the head's own yaw reads), a positive pitch raises it");
        Vector2 left = UpperBody.LeanTarget(4f, 1f, 0f, 0f, true), speedUp = UpperBody.LeanTarget(2f, 0f, 3f, 0f, true);
        Vector3 topL = Vector3.Transform(Vector3.UnitZ, Tip(Vector3.UnitX, Vector3.UnitY, left.X, left.Y));
        Vector3 topA = Vector3.Transform(Vector3.UnitZ, Tip(Vector3.UnitX, Vector3.UnitY, speedUp.X, speedUp.Y));
        check.Expect(topL.X < -0.05f && topA.Y > 0.03f, "lean: a left turn tips the top left, speeding up tips it forward");
        Vector3 chest = Vector3.Transform(Vector3.UnitY, Tip(Vector3.UnitX, Vector3.UnitY, 20f * MathF.PI / 180f, 0f));
        check.Expect(chest.Z > 0.3f, "aim: a positive pitch (target above) raises the chest's forward");
        Vector2 hx = Vector2.Zero, hv = UpperBody.Launch(-Vector3.UnitY, Vector3.UnitY, Vector3.UnitX, 0.14f);
        float hitPeak = 0f;
        for (int i = 0; i < 120; i++)
        {
            Wobble(ref hx, ref hv, 2f * MathF.PI * 2.5f, 0.4f, dt60);
            Vector3 top = Vector3.Transform(Vector3.UnitZ, Tip(Vector3.UnitX, Vector3.UnitY, hx.X, hx.Y));
            if (i == 5 && !(top.Y < 0f)) hitPeak = -1f;
            if (hitPeak >= 0f) hitPeak = MathF.Max(hitPeak, hx.Length());
        }
        check.Expect(hitPeak > 0.12f && hitPeak < 0.16f && hx.Length() < 0.01f,
                     "flinch: a blow from the front tips the top back, peaks at its magnitude and settles");
        check.Expect(Skating.SelfCheck(), "the combat shuffle is paced by the gap it still has to close over the countdown it has left, and a spent countdown falls back to the measured step");
        check.Expect(SampleCheck(), "clip sampler: a thigh keyed from rest to a quarter turn puts its ankle, half way, where the leg worked by hand puts it, and an unkeyed leg stays at rest");
        check.Expect(GaitClip.SelfCheck(), "gait analysis: a synthetic walk reads its 1.5 units/s stance speed, its travel direction, 60 % contact per foot a half cycle apart and its swing progress; a crawl and a swim are refused as not upright, a float as off the ground and a stand as no stride");
        check.Expect(Settle.SelfCheck(), "stops and turns through the feet: the settle step's arc leaves the ground and lands back on it, the weighted foot does not move through the crossfade and only the other one lifts, a foot left a foot's width from its stance spot takes a second step and one left nearer does not; a standing turn steps round foot by foot, the turning side first, and ends on the stance");
        check.Expect(StrideWarp.SelfCheck(), "stride warp: the pace splits as the square root between rate and stride and the rate takes what the stride cannot; a long stride drops the pelvis just enough to reach; a planted foot holds, drags only past its hold and eases its offset out over the swing; a walk driven from half to twice its pace never slides a planted foot nor jumps a drawn one");
        check.Expect(Footfall.SelfCheck(), "a footstep plays once per planted foot, a double within a sprint's gap merged");
        check.Expect(Gait.SelfCheck(), "gait: the acceleration is positive up the ramp, zero in cruise and negative through the ease");
        fixed (byte* a = "pelvis_g\0"u8, b = "Lbicep_g\0"u8, c = "rforearm_g\0"u8, d = "lhand_g\0"u8, e = "lhand\0"u8, g = "Lshoulder_g\0"u8)
            check.Expect(BoneIndex(a, st) == 9 && BoneIndex(b, st) == 13 && BoneIndex(c, st) == 11 && BoneIndex(d, st) == 15
                         && BoneIndex(e, st) == -1 && BoneIndex(g, st) == -1,
                         "the hips and arms match by their _g bone names only");
        check.Expect(Transition.SelfCheck(), "movement changes: the old movement carries on and crossfades into the new one, continuous where it starts, a reversal slows through zero instead of flipping");
        check.Expect(Mass.SelfCheck(), "mass: a start, a stop and the bottom of a drop dip the pelvis and it settles back a little past neutral; steady walking and a steady descent carry no dip; the chest's lean overshoots a step by under a fifth");
        check.Expect(Diagonal.SelfCheck(), "diagonals: the sideways diagonal plays the straight walk or run, turned so it faces the travel; walking back diagonally leads with its back; the hips come round within a quarter second, without overshoot, and a swapped diagonal is no movement change");
        check.Expect(Diagonal.LagCheck(), "heading changes: the hips keep their world heading as the facing turns, never past the twist cap, and come round without overshoot; a steady bend trails by a bounded lag");
        check.Expect(PathTurnCheck(), "a walker whose FACING swings round at a waypoint while its travel goes straight on reads no path turn and leans by nothing; a steady curve reads its own rate");
        check.Expect(Follow.SelfCheck(), "follow-through: a turn on the spot swings the arms behind, then they swing through; a stop swings them forward; either settles to a tenth of its swing within 1.2 s, the lag is capped, and an action eases it out; the neck keeps a shake off the head but not the sway, and carries it along past its cap");
        check.Expect(Weight.SelfCheck(), "weight cues: a cruise on a path loads nothing; the engine's ramp and a drive's ease each dip the pelvis by the rate's worth of load and pitch the lean into and out of the travel; a drive at full speed carries no cue at all, a body off any path falls back to the measured acceleration, and no cue passes the running stop's");
        string[] stockNodes = ["root", "rthigh_g", "rshin_g", "rfoot_g", "lthigh_g", "lshin_g", "lfoot_g"];
        string[] ownNodes = ["root", "Bip_R_Thigh", "Bip_R_Calf", "Bip_R_Foot", "Bip_L_Thigh", "Bip_L_Calf", "Bip_L_Foot"];
        check.Expect(DbRigCheck(stockNodes, st, out bool legs) && legs, "DB rig: the bones are found by node name in a SceneDb part range, a lifted ankle's IK reads its own writes back through the world chain, and the restore puts the animation's values back -- root height and every bone");
        check.Expect(RigMapCheck(out byte[][] mapped), "rig map: a supermodel's row names the limbs, a blank cell names no bone, the appearance names the head, and a chain with no row keeps the stock names");
        check.Expect(DbRigCheck(ownNodes, mapped, out legs) && legs && DbRigCheck(ownNodes, st, out bool unmapped) && !unmapped,
                     "rig map: a skeleton with its own bone names gets foot IK when the map names its legs, and none on the stock names alone");
        log.Info(check.Ok ? "Pose.SelfCheck: passed" : "Pose.SelfCheck: FAILED");
    }

    /// <summary>A hand-written rig map row, as the engine's 2DA hands its cells over (bone order, blank as empty),
    /// against hand-written names: what <paramref name="rig"/>, the map's rig for a model whose supermodel has the
    /// row, must match and must not.</summary>
    private static bool RigMapCheck(out byte[][] rig)
    {
        var rows = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["cx_pose_skel"] = ["Bip_R_Thigh", "Bip_R_Calf", "Bip_R_Foot", "Bip_L_Thigh", "Bip_L_Calf", "Bip_L_Foot", "Bip_Spine", "", "", "",
                                "", "", "", "", "", ""],
        };
        rig = RigMap.Build(["cx_pose_kid", "CX_POSE_SKEL"], "Wolf_head", rows);
        byte[][] none = RigMap.Build(["cx_pose_kid"], "head_g", rows);
        fixed (byte* thigh = "BIP_R_THIGH\0"u8, foot = "bip_r_foot\0"u8, spine = "bip_spine\0"u8, head = "wolf_head\0"u8, stock = "rthigh_g\0"u8)
            return BoneIndex(thigh, rig) == 0 && BoneIndex(foot, rig) == 2 && BoneIndex(spine, rig) == 6
                   && BoneIndex(head, rig) == 8 && BoneIndex(stock, rig) == -1 && rig[7].Length == 0 && rig[9].Length == 0
                   && BoneIndex(stock, none) == 0 && BoneIndex(thigh, none) == -1;
    }

    /// <summary>A seven-node biped leg rig in a fresh scene-database object, parts built and the object matrix composed:
    /// a root, and per side a thigh at (±0.1, 0, 1) with a shin 0.5 below it (0.02 forward) and a foot 0.5 below that.</summary>
    private static DbId TestRig(string[] names)
    {
        var m = new CX.Alloy.Mdl.ModelDef { Name = "cx_pose_rig", AnimScale = 1f, BMin = new Vector3(-1f), BMax = new Vector3(1f) };
        int[] parent = [-1, 0, 1, 2, 0, 4, 5];
        Vector3[] at = [Vector3.Zero, new(0.1f, 0f, 1f), new(0f, 0.02f, -0.5f), new(0f, 0f, -0.5f),
                        new(-0.1f, 0f, 1f), new(0f, 0.02f, -0.5f), new(0f, 0f, -0.5f)];
        m.Nodes = new CX.Alloy.Mdl.NodeDef[names.Length];
        for (int i = 0; i < names.Length; i++)
        {
            int kids = 0;
            for (int k = 0; k < names.Length; k++) if (parent[k] == i) kids++;
            var c = new int[kids];
            for (int k = 0, j = 0; k < names.Length; k++) if (parent[k] == i) c[j++] = k;
            m.Nodes[i] = new CX.Alloy.Mdl.NodeDef { Name = names[i], Index = i, Parent = parent[i], Children = c,
                                                    Kind = CX.Alloy.Mdl.NodeKind.Dummy, Position = at[i] };
            m.NodeByName[names[i]] = i;
        }
        DbId obj = SceneDb.Objects.Alloc();
        if (obj.IsNull) return obj;
        int o = obj.Index;
        SceneDb.ObjModel[o] = m;
        SceneDb.ObjPos[o] = new Vector3(3f, 4f, 5f);
        SceneDb.ObjRot[o] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.7f);
        SceneDb.ObjScale[o] = 1f;
        SceneDb.ObjAttachTo[o] = default;
        DbAnim.ObjectCreated(obj);
        if (CX.Alloy.Db.PartTree.Build(obj, m)) DbAnim.PartGetWorld(SceneDb.ObjPartFirst[o], out _, out _);
        else { FreeRig(obj); return default; }
        return obj;
    }

    private static void FreeRig(DbId obj)
    {
        DbAnim.ObjectRetired(obj);
        CX.Alloy.Db.PartTree.Free(obj);
        SceneDb.Objects.Free(obj);
    }

    /// <summary>The cold clip sampler against a leg worked by hand: an animation turning <see cref="TestRig"/>'s right
    /// thigh from rest to 90° about X over 1 s puts the right ankle, at 0.5 s, at (0.1, 0.721249, 0.307035) -- the shin
    /// and foot, 1.0 below the thigh and 0.02 forward, turned 45° -- while the unkeyed left ankle stays at (-0.1, 0.02, 0).</summary>
    private static bool SampleCheck()
    {
        DbId obj = TestRig(["root", "rthigh_g", "rshin_g", "rfoot_g", "lthigh_g", "lshin_g", "lfoot_g"]);
        if (obj.IsNull) return false;
        try
        {
            float h = MathF.Sqrt(0.5f);
            var swing = new CX.Alloy.Mdl.ControllerTrack { Type = CX.Alloy.Mdl.Ctl.Orientation, Columns = 4, Times = [0f, 1f],
                                                           Values = [0f, 0f, 0f, 1f, h, 0f, 0f, h] };
            var anim = new CX.Alloy.Mdl.AnimDef { Name = "cx_swing", Length = 1f, Nodes =
            [
                new CX.Alloy.Mdl.AnimNode { Name = "root" },
                new CX.Alloy.Mdl.AnimNode { Name = "rthigh_g", Parent = 0, Controllers = [swing] },
            ] };
            int first = SceneDb.ObjPartFirst[obj.Index];
            Vector3 r = DbAnim.SampledPos(first + 3, anim, 0.5f, 1f), l = DbAnim.SampledPos(first + 6, anim, 0.5f, 1f);
            return Vector3.Distance(r, new Vector3(0.1f, 0.721249f, 0.307035f)) < 1e-4f
                   && Vector3.Distance(l, new Vector3(-0.1f, 0.02f, 0f)) < 1e-4f;
        }
        finally { FreeRig(obj); }
    }

    /// <summary>The layer over a synthetic SceneDb leg pair (root, then thigh -> shin -> foot each side, named
    /// <paramref name="names"/>) found by <paramref name="rig"/>: what DB mode's step drives. <paramref name="legs"/>
    /// says the rig found all six; with fewer there is nothing to drive, and it passes. Leaves the table as it found it.</summary>
    private static bool DbRigCheck(string[] names, byte[][] rig, out bool legs)
    {
        legs = false;
        DbId obj = TestRig(names);
        if (obj.IsNull) return false;
        int o = obj.Index;
        bool db = PoseRig.Db, ok = false;
        PoseRig.Db = true;
        try
        {
            int first = SceneDb.ObjPartFirst[o];
            Bones b = default;
            PoseRig.FindBones(obj, rig, ref b);
            legs = b.Legs;
            if (!legs) return true;
            IntPtr root = PoseRig.Handle(first);
            ok = PoseRig.Index(b.RFoot) == first + 3 && PoseRig.Index(b.LThigh) == first + 4;
            PoseRig.World(b.RFoot, out Vector3 foot0, out _);
            DbAnim.PartGetWorld(first + 3, out Vector3 composed, out _);
            ok &= Vector3.Distance(foot0, composed) < 1e-4f;
            Slot s = default;
            Save(ref s, root, b);
            s.Root = root;
            Quaternion shin0 = PoseRig.Local(b.RShin);
            PoseRig.TakeWrote();
            PoseRig.SetPos(root, new Vector3(0f, 0f, 0.05f));
            Leg(b.RThigh, b.RShin, b.RFoot, new Vector3(0f, 0f, 0.1f), Vector3.UnitY, Quaternion.Identity);
            PoseRig.World(b.RFoot, out Vector3 foot1, out _);
            ok &= PoseRig.TakeWrote() && MathF.Abs(foot1.Z - foot0.Z - 0.15f) < 2e-3f
                  && Quaternion.Dot(PoseRig.Local(b.RShin), shin0) < 0.9999f;
            Restore(ref s, b);
            PoseRig.World(b.RFoot, out Vector3 foot2, out _);
            ok &= Vector3.Distance(foot2, foot0) < 1e-4f && PoseRig.Pos(root) == Vector3.Zero
                  && MathF.Abs(MathF.Abs(Quaternion.Dot(PoseRig.Local(b.RShin), shin0)) - 1f) < 1e-6f;
            return ok;
        }
        finally
        {
            PoseRig.Db = db;
            PoseRig.TakeWrote();
            FreeRig(obj);
        }
    }
}
