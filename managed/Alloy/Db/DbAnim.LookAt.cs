using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The look-at run: <c>Gob::BeginLookAtAnimate</c> @0x49d5b0 / <c>EndLookAtAnimate</c> @0x4979e0, so
/// a creature's head follows what it talks to or fights. Angle limits are DEGREES, absolute and clamped to 89;
/// the fourth float is the deactivation distance. Each step clamps yaw/pitch to the target and resets the run
/// time, so the head eases dt/0.25 of the way, measured in the head part's PARENT frame.</summary>
public static unsafe partial class DbAnim
{
    private struct LookAtState
    {
        internal DbId Target;
        internal int Head, TargetPart;
        internal float YawLimit, PitchLimit, MaxDist;
        internal bool Active, Ending;
        /// <summary>Where the eased head is: the generated track's first key, carried between steps.</summary>
        internal Quaternion Rot;
        /// <summary>The look's share over the animation's head pose while it fades out after the end.</summary>
        internal float Weight;
        /// <summary>An aim handed in for the next step (<see cref="LookAtAim"/>), in degrees.</summary>
        internal bool Aimed;
        internal float AimYaw, AimPitch;
    }

    /// <summary>The time of the generated track's aim key (<c>BuildAnimation</c>'s key times 0 and 0.25): the
    /// head covers dt / this of the remaining turn each step, and the end fades over the same span.</summary>
    private const float LookEase = 0.25f;

    /// <summary>The engine's own clamp on both limits, at construction.</summary>
    private const float LimitMax = 89.0f;

    private static readonly LookAtState[] _lookAt = new LookAtState[SceneDb.MaxObjects];

    private static void LookAtClear(int i) => _lookAt[i] = default;

    private static void LookAtBegin(DbId obj, DbId target, string? ownPart, string? targetPart,
                                   float yawLimitDeg, float pitchLimitDeg, float maxDistance)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        // The engine returns without building anything when either part is missing, and so does this.
        int head = PartByName(obj, ownPart);
        int at = PartByName(target, targetPart);
        if (head < 0 || at < 0) { _lookAtNoHead++; return; }
        ref LookAtState s = ref _lookAt[i];
        s.Target = target;
        s.Head = head;
        s.TargetPart = at;
        s.YawLimit = MathF.Min(MathF.Abs(yawLimitDeg), LimitMax);
        s.PitchLimit = MathF.Min(MathF.Abs(pitchLimitDeg), LimitMax);
        s.MaxDist = MathF.Abs(maxDistance);
        // a new look starts from the head as it is now, and a look begun while one is fading picks up there
        if (!s.Active || s.Ending) s.Rot = SceneDb.PartRot[head];
        s.Active = true;
        s.Ending = false;
        s.Weight = 1f;
    }

    private static void LookAtEnd(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        ref LookAtState s = ref _lookAt[obj.Index];
        if (!s.Active) return;
        // the engine holds the head where it is and fades the run out; the animation's own pose takes it back
        s.Ending = true;
        Moved(obj.Index);
    }

    private static int _lookAtNoHead;

    /// <summary>One look-at step, after the queue advance so it writes over the animation's head pose, as the
    /// engine's index-0 add-in run does.</summary>
    private static bool LookAtStep(DbId obj, float dt)
    {
        int i = obj.Index;
        ref LookAtState s = ref _lookAt[i];
        if (!s.Active) return true;
        float ease = Math.Clamp(dt / LookEase, 0f, 1f);
        if (s.Ending)
        {
            s.Weight -= ease;
            if (s.Weight <= 0f || (uint)s.Head >= (uint)SceneDb.MaxParts || !SceneDb.Parts.LiveAt(s.Head))
            { s.Active = false; s.Ending = false; Moved(i); return true; }
            SceneDb.PartRot[s.Head] = Slerp(SceneDb.PartRot[s.Head], s.Rot, s.Weight);
            SceneDb.TouchPart(s.Head, Dirty.Anim);
            Moved(i);
            return true;
        }
        if (!SceneDb.Objects.Valid(s.Target)) { s.Ending = true; return true; }

        // Control's first act: the distance between the two OBJECTS, and the run deactivates past the limit.
        if (s.MaxDist > 0f
            && (SceneDb.ObjPos[s.Target.Index] - SceneDb.ObjPos[i]).Length() > s.MaxDist)
        { s.Ending = true; return true; }

        int head = s.Head;
        if ((uint)head >= (uint)SceneDb.MaxParts || !SceneDb.Parts.LiveAt(head)
            || !SceneDb.Parts.LiveAt(s.TargetPart)) { s.Ending = true; return true; }
        _lookAts++;

        float yaw, pitch;
        if (s.Aimed) { yaw = s.AimYaw; pitch = s.AimPitch; s.Aimed = false; }
        else
        {
            // The aim, in the frame the head part's local rotation lives in.
            if (!ParentInv(i, head, out Matrix4x4 upInv)) return true;
            Vector3 at = Vector3.Transform(Translation(SceneDb.PartWorld[s.TargetPart]), upInv);
            if (!Angles(at - SceneDb.PartPos[head], out yaw, out pitch)) return true;
        }
        yaw = Math.Clamp(yaw, -s.YawLimit, s.YawLimit);
        pitch = Math.Clamp(pitch, -s.PitchLimit, s.PitchLimit);

        CX.Engine.Quat q = BehaviorMath.Mul(BehaviorMath.YawPitchRoll(yaw, 0f, 0f),
                                            BehaviorMath.YawPitchRoll(0f, pitch, 0f));
        s.Rot = Slerp(s.Rot, new Quaternion(q.X, q.Y, q.Z, q.W), ease);
        SceneDb.PartRot[head] = s.Rot;
        SceneDb.TouchPart(head, Dirty.Anim);
        Moved(i);
        return true;
    }

    /// <summary>The inverse of the frame the head part's local rotation lives in: its parent part's, or the object's.</summary>
    private static bool ParentInv(int obj, int head, out Matrix4x4 inv)
    {
        int parent = SceneDb.PartParent[head];
        return Matrix4x4.Invert(parent >= 0 ? SceneDb.PartWorld[parent] : SceneDb.ObjWorld[obj], out inv);
    }

    /// <summary>Yaw (0 faces +Y, the engine's convention throughout) and pitch (the elevation) of a direction, in degrees.</summary>
    private static bool Angles(Vector3 d, out float yaw, out float pitch)
    {
        yaw = pitch = 0f;
        if (d.Length() < 1e-4f) return false;
        yaw = MathF.Atan2(-d.X, d.Y) * (180f / MathF.PI);
        pitch = MathF.Atan2(d.Z, MathF.Sqrt(d.X * d.X + d.Y * d.Y)) * (180f / MathF.PI);
        return true;
    }

    /// <summary>Head tracking's measure (<c>HeadTrackService</c>, which in DB mode has no
    /// <c>LookAtAnimation::Control</c> to chain): a world direction's yaw and pitch in the frame the step
    /// measures in. False when the object has no live look.</summary>
    public static bool LookAtMeasure(DbId obj, Vector3 worldDir, out float yaw, out float pitch)
    {
        yaw = pitch = 0f;
        if (!SceneDb.Objects.Valid(obj)) return false;
        ref LookAtState s = ref _lookAt[obj.Index];
        if (!s.Active || s.Ending || (uint)s.Head >= (uint)SceneDb.MaxParts || !SceneDb.Parts.LiveAt(s.Head)
            || !ParentInv(obj.Index, s.Head, out Matrix4x4 inv)) return false;
        return Angles(Vector3.TransformNormal(worldDir, inv), out yaw, out pitch);
    }

    /// <summary>The next step aims here (degrees) instead of at the target part; the look's limits still clamp.</summary>
    public static void LookAtAim(DbId obj, float yaw, float pitch)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        ref LookAtState s = ref _lookAt[obj.Index];
        s.Aimed = true; s.AimYaw = yaw; s.AimPitch = pitch;
    }

    private static Vector3 Translation(in Matrix4x4 m) => new(m.M41, m.M42, m.M43);
}
