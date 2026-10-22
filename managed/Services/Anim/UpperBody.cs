using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Services.Camera;
using CX.Services.Chat.Core;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Lean, aim, look-at sharing and hit reactions, a <see cref="PoseService"/> module. Every one is a small rotation of
/// the torso (a child of the root, so the legs and foot placement are untouched), summed as one axis-angle vector
/// in the character's frame: <c>R</c> = radians about the character's right (positive tips the top back), <c>F</c> =
/// about its forward (positive tips it right). The neck takes back half the lean and adds a share of a hit.
/// </summary>
internal static unsafe class UpperBody
{
    private const float Deg = MathF.PI / 180f;
    private const float Gravity = 9.81f;
    /// <summary>Share of the physical lean angle <c>atan(a/g)</c> shown for a turn, and its cap.</summary>
    private const float TurnGain = 0.25f, MaxRoll = 3f * Deg;
    /// <summary>Share of <c>atan(a/g)</c> shown for speeding up, and its cap. Slowing down leans nothing: the stop's
    /// settle steps carry it, and a chest tipping back over them reads as a recoil.</summary>
    private const float AccelGain = 0.25f, MaxPitch = 3f * Deg;
    /// <summary>Share of the climb angle leaned into, and its cap. Only while moving.</summary>
    private const float SlopeGain = 0.35f, MaxSlope = 8f * Deg;
    /// <summary>Seconds the lean takes to follow.</summary>
    private const float LeanTime = 0.2f;
    /// <summary>Share of the lean <c>neck_g</c> takes back, and share of a hit it adds.</summary>
    private const float NeckLevel = 0.5f, NeckWhip = 0.5f;
    /// <summary>Largest aim pitch, the share a melee attack takes, and the seconds the aim eases in and out over.</summary>
    private const float MaxAim = 35f * Deg, MeleeAim = 0.5f, AimTime = 0.15f, AimFade = 0.2f;
    /// <summary>The flinch spring: natural frequency (Hz), damping ratio, and its peak for a plain hit.</summary>
    private const float HitHz = 2.5f, HitZeta = 0.4f, HitPeak = 8f * Deg;
    /// <summary>Launch speed per radian of peak for <see cref="HitZeta"/>: <c>exp(zeta/sqrt(1-zeta^2) *
    /// atan(sqrt(1-zeta^2)/zeta))</c>, the underdamped overshoot factor, precomputed.</summary>
    private const float HitLaunch = 1.6587f;
    /// <summary>Largest the flinch may get however many hits stack.</summary>
    private const float MaxHit = 25f * Deg;
    /// <summary>The attack result a critical hit carries (the projectile path writes 1 hit / 3 crit).</summary>
    private const byte ResultCrit = 3;
    private const byte TypeCreature = 5;
    private const float Omega = 2f * MathF.PI * HitHz;

    private static GetGameObjectFn _getGameObject;
    private static SpawnHitVisualsFn* _hitNext;
    private static bool _aimReady;
    /// <summary>Object ids resolve (the aim target, the player's position for the detail distance).</summary>
    internal static bool Ready => _aimReady;

    internal static int Looked, Aimed, Hits;

    /// <summary>Resolves the aim target's helpers and chains the hit funnel. Each half stands down on its own.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (EngineLayout.Require(log, "Pose.aim", "CNWCObject", "CNWCCreature", "CGameObject")
            && SymbolMap.Require(log, "Pose.aim", SymbolMap.GetGameObject, SymbolMap.AppManager))
        {
            _getGameObject = EngineFn.GetGameObject;
            _aimReady = _getGameObject != null;
        }
        if (!EngineLayout.Require(log, "Pose.hit", "CNWCCombatAttackData", "CNWCCreature")
            || !SymbolMap.Require(log, "Pose.hit", SymbolMap.SpawnHitVisuals)) return;
        _hitNext = hooks.ChainSpawnHitVisuals(&OnSpawnHitVisuals, 50);
        log.Info(_hitNext == null ? "Pose: hit reactions standing down (SpawnHitVisuals chain refused)"
                                  : "Pose: chained CNWCCreature::SpawnHitVisuals (hit reactions)");
    }

    /// <summary>The one funnel a landed melee swing and a projectile impact both go through: the kick is away from
    /// the blow -- a melee call's vector points at the attacker, a projectile's along its flight -- and only a
    /// projectile's attack data carries no attacker, which is how the two are told apart.</summary>
    [UnmanagedCallersOnly]
    private static void OnSpawnHitVisuals(IntPtr creature, byte flag, IntPtr ad, VectorArg vec, IntPtr text)
    {
        Vec3 v = vec.Value;   // Win64: read before the original, which owns the caller-made copy
        (*_hitNext)(creature, flag, ad, vec, text);
        if (flag != 1 || ad == IntPtr.Zero || creature == IntPtr.Zero || !(AnimKnobs.Hit.V > 0f)) return;
        IntPtr gob = PoseService.GobOf(creature);
        if (gob == IntPtr.Zero) return;
        bool ranged = new CNWCCombatAttackData(ad).Attacker == TypingSignal.ObjectInvalid;
        Vector3 push = ranged ? new Vector3(v.X, v.Y, 0f) : new Vector3(-v.X, -v.Y, 0f);
        if (push.LengthSquared() < 1e-8f) return;
        var atk = new CNWCCombatAttackData(ad);
        float mag = Magnitude(atk.Damage, atk.Result == ResultCrit) * (AnimKnobs.Hit.V / 100f);
        PoseService.Kick(gob, Vector3.Normalize(push), mag);
        Hits++;
    }

    /// <summary>Peak flinch (radians) for a hit: a plain hit <see cref="HitPeak"/>, more with damage, a critical
    /// hit a third more. ponytail: damage is absolute, not a share of max HP (that is one more engine call), and
    /// weapon size is not read -- the upgrade is <c>CNWBaseItem+0x19</c> WeaponSize off the attack's weapon slot.</summary>
    internal static float Magnitude(int damage, bool crit)
        => HitPeak * Math.Clamp(0.5f + damage / 20f, 0.5f, 1.6f) * (crit ? 1.35f : 1f);

    /// <summary>The angular launch velocity, in the (R, F) frame, that peaks at <paramref name="peak"/> tipping the
    /// top toward <paramref name="push"/> (horizontal, unit).</summary>
    internal static Vector2 Launch(Vector3 push, Vector3 fwd, Vector3 right, float peak)
    {
        Vector3 axis = Vector3.Cross(Vector3.UnitZ, push);
        return new Vector2(Vector3.Dot(axis, right), Vector3.Dot(axis, fwd)) * (peak * Omega * HitLaunch);
    }

    /// <summary>The lean target (R, F) for a character at <paramref name="speed"/> m/s turning at
    /// <paramref name="yawRate"/> rad/s (positive = left), accelerating at <paramref name="accel"/> m/s^2 up a
    /// <paramref name="slope"/> (rise per unit of travel).</summary>
    internal static Vector2 LeanTarget(float speed, float yawRate, float accel, float slope, bool moving)
    {
        float roll = Math.Clamp(MathF.Atan(speed * yawRate / Gravity) * TurnGain, -MaxRoll, MaxRoll);
        float pitch = MathF.Min(MathF.Atan(MathF.Max(accel, 0f) / Gravity) * AccelGain, MaxPitch);
        if (moving) pitch += Math.Clamp(MathF.Atan(slope) * SlopeGain, -MaxSlope, MaxSlope);
        return new Vector2(-pitch, -roll);
    }

    /// <summary>Advances the lean, aim and flinch springs. <paramref name="hip"/> is the root part's height over
    /// the creature's feet, for a target that has no root part of its own.</summary>
    internal static void Step(ref PoseService.Slot s, IntPtr creature, Vector3 rootW, float hip, float scale,
                              float groundSpeed, bool gated, bool snap, float dt)
    {
        float lean = AnimKnobs.Lean.V / 100f;
        Vector2 leanT = gated || !(lean > 0f) ? Vector2.Zero
            : LeanTarget(groundSpeed, s.PathRate, s.Cue, s.Slope, s.Moving > 0f) * lean;
        float aimT = s.AimPitch, aimW = 0f;
        if (!gated && AnimKnobs.Aim.V && _aimReady && creature != IntPtr.Zero)
        {
            int anim = new CNWCObject(creature).Animation;
            if (IsAttack(anim) && Target(creature, hip, out Vector3 at))
            {
                Vector3 d = at - rootW;
                float flat = MathF.Sqrt(d.X * d.X + d.Y * d.Y);
                aimT = Math.Clamp(MathF.Atan2(d.Z, MathF.Max(flat, 0.3f * scale)), -MaxAim, MaxAim) * (anim == AttackRanged ? 1f : MeleeAim);
                aimW = 1f;
                Aimed++;
            }
        }
        if (snap)
        {
            s.Lean = leanT; s.LeanV = Vector2.Zero;
            s.AimPitch = aimT; s.AimW = aimW; s.AimV = s.AimWV = 0f;
            s.Hit = s.HitV = Vector2.Zero;
        }
        else
        {
            if (AnimKnobs.Mass.V) Mass.Chest(ref s.Lean, ref s.LeanV, leanT, dt);
            else Smooth(ref s.Lean, ref s.LeanV, leanT, dt, LeanTime);
            Smooth(ref s.AimPitch, ref s.AimV, aimT, dt, AimTime);
            Smooth(ref s.AimW, ref s.AimWV, aimW, dt, AimFade);
            Wobble(ref s.Hit, ref s.HitV, Omega, HitZeta, dt);
            if (s.Hit.LengthSquared() > MaxHit * MaxHit) s.Hit = Vector2.Normalize(s.Hit) * MaxHit;
        }
    }

    /// <summary>The torso rotation vector (R, F) this frame, and the neck's own on top of it.</summary>
    internal static void Rotations(in PoseService.Slot s, bool gated, out Vector2 torso, out Vector2 neck)
    {
        Vector2 hit = gated ? Vector2.Zero : s.Hit;
        torso = s.Lean + new Vector2(s.AimPitch * Math.Clamp(s.AimW, 0f, 1f), 0f) + hit;
        neck = -NeckLevel * s.Lean + NeckWhip * hit;
    }

    /// <summary>Rotates <paramref name="torso"/> by <paramref name="t"/> and <paramref name="neck"/> by
    /// <paramref name="n"/> on top, in world space about the character's axes; locals written back. A neck that
    /// is not the torso's child is left alone.</summary>
    internal static void Write(IntPtr torso, IntPtr neck, Vector3 fwdH, Vector3 right, Vector2 t, Vector2 n)
    {
        IntPtr parent = PoseRig.Parent(torso);
        if (parent == IntPtr.Zero) return;
        World(parent, out _, out Quaternion qp);
        World(torso, out _, out Quaternion qt);
        bool hasNeck = neck != IntPtr.Zero && PoseRig.Parent(neck) == torso;
        Quaternion qn = Quaternion.Identity;
        if (hasNeck) World(neck, out _, out qn);
        Quaternion r = Tip(right, fwdH, t.X, t.Y);
        Quaternion qt2 = Quaternion.Normalize(r * qt);
        SetLocal(torso, Quaternion.Conjugate(qp) * qt2);
        if (hasNeck) SetLocal(neck, Quaternion.Conjugate(qt2) * Tip(right, fwdH, n.X, n.Y) * r * qn);
    }

    /// <summary>Head turn (radians of yaw and pitch relative to the neck) that stays on the head alone: an
    /// animation's own small head motion is left as authored.</summary>
    private const float LookDeadzone = 5f * Deg;
    /// <summary>Shares of the head's turn past the deadzone the torso and the neck take, yaw then pitch; the
    /// head keeps the rest.</summary>
    private const float TorsoYaw = 0.2f, NeckYaw = 0.3f, TorsoPitch = 0.1f, NeckPitch = 0.3f;

    /// <summary>Seconds the look share takes to follow the head's turn, and to ease back out when the look-at ends.</summary>
    private const float LookTime = 0.3f;

    /// <summary>Springs <see cref="PoseService.Slot.Look"/> toward the head's turn while <paramref name="live"/>,
    /// else back to 0, so the chest eases out of a look instead of snapping. True while there is a share to write.</summary>
    internal static bool LookStep(ref PoseService.Slot s, in PoseService.Bones b, bool live, bool snap, float dt)
    {
        Vector2 target = default;
        if (!LookChain(b)) { s.Look = s.LookV = default; return false; }
        if (live && LookExcess(b, out float yaw, out float pitch)) target = new Vector2(yaw, pitch);
        if (snap) { s.Look = target; s.LookV = default; }
        else Smooth(ref s.Look, ref s.LookV, target, dt, LookTime);
        return s.Look.LengthSquared() > 1e-8f;
    }

    private static bool LookChain(in PoseService.Bones b)
        => b.Torso != IntPtr.Zero && b.Neck != IntPtr.Zero && b.Head != IntPtr.Zero
           && PoseRig.Parent(b.Head) == b.Neck && PoseRig.Parent(b.Neck) == b.Torso;

    /// <summary>The head's yaw (positive = left) and pitch (positive = up) relative to the neck, past
    /// <see cref="LookDeadzone"/>: what the engine's look-at (or an animation's big head turn) put there. False
    /// when there is nothing to share, or the chain is not torso -&gt; neck -&gt; head.</summary>
    internal static bool LookExcess(in PoseService.Bones b, out float yaw, out float pitch)
    {
        yaw = pitch = 0f;
        if (!LookChain(b)) return false;
        Vector3 f = Vector3.Transform(Vector3.UnitY, Local(b.Head));
        yaw = Excess(MathF.Atan2(-f.X, f.Y));
        pitch = Excess(MathF.Asin(Math.Clamp(f.Z, -1f, 1f)));
        return yaw != 0f || pitch != 0f;
    }

    private static float Excess(float a) => MathF.Abs(a) <= LookDeadzone ? 0f : a - MathF.CopySign(LookDeadzone, a);

    /// <summary>Hands <see cref="TorsoYaw"/>/<see cref="NeckYaw"/> of the head's turn to the torso and neck (about the
    /// neck's own up and right axes) and rewrites the head's local so its world rotation -- where it looks -- is
    /// unchanged: the engine's own look-at turns the <c>HEAD_NAME</c> node alone, up to <c>HEAD_ARC_H</c> (60° for playable races).
    /// Runs before <see cref="Write"/>, which the head then follows as a child.</summary>
    internal static void ShareLook(in PoseService.Bones b, float yaw, float pitch)
    {
        IntPtr parent = PoseRig.Parent(b.Torso);
        if (parent == IntPtr.Zero) return;
        World(parent, out _, out Quaternion qp);
        World(b.Torso, out _, out Quaternion qt);
        World(b.Neck, out _, out Quaternion qn);
        World(b.Head, out _, out Quaternion qh);
        Vector3 up = Vector3.Transform(Vector3.UnitZ, qn), side = Vector3.Transform(Vector3.UnitX, qn);
        Quaternion st = Turn(up, side, yaw * TorsoYaw, pitch * TorsoPitch);
        Quaternion sn = Turn(up, side, yaw * NeckYaw, pitch * NeckPitch);
        Quaternion qt2 = Quaternion.Normalize(st * qt), qn2 = Quaternion.Normalize(sn * st * qn);
        SetLocal(b.Torso, Quaternion.Conjugate(qp) * qt2);
        SetLocal(b.Neck, Quaternion.Conjugate(qt2) * qn2);
        SetLocal(b.Head, Quaternion.Conjugate(qn2) * qh);
        Looked++;
    }

    /// <summary>A yaw about <paramref name="up"/> after a pitch about <paramref name="side"/> (positive raises
    /// the forward, as the head's own pitch does).</summary>
    internal static Quaternion Turn(Vector3 up, Vector3 side, float yaw, float pitch)
        => Quaternion.CreateFromAxisAngle(up, yaw) * Quaternion.CreateFromAxisAngle(side, pitch);

    /// <summary>The creature animation ids of an attack (<c>CNWCObject::AnimationAttack</c>): 0xc8 ranged,
    /// 0xc9..0xcd melee at each range, 0x107..0x109 the flourishes.</summary>
    internal static bool IsAttack(int anim) => anim is >= AttackRanged and <= 0xcd or >= 0x107 and <= 0x109;
    private const int AttackRanged = 0xc8;  // not-an-offset: an animation id

    /// <summary>The lock target's aim point: its root part's world position if it is a creature with a model,
    /// else its position raised by the attacker's own <paramref name="hip"/> height.</summary>
    private static bool Target(IntPtr creature, float hip, out Vector3 at)
    {
        at = default;
        uint id = new CNWCCreature(creature).LockTarget;
        if (id == TypingSignal.ObjectInvalid) return false;
        IntPtr app = CameraService.App();
        if (app == IntPtr.Zero) return false;
        IntPtr go = _getGameObject(app, id);
        if (go == IntPtr.Zero) return false;
        IntPtr obj = new CGameObject(go).AsNWCObject().Ptr;
        if (obj == IntPtr.Zero || obj == creature) return false;
        if (new CNWCObject(obj).Type == TypeCreature)
        {
            IntPtr g = PoseService.GobOf(obj);
            IntPtr root = g == IntPtr.Zero ? IntPtr.Zero : PoseRig.Root(g);
            if (root != IntPtr.Zero) { World(root, out at, out _); return true; }
        }
        Vector3 p = new CNWCObject(obj).Position;
        at = new Vector3(p.X, p.Y, p.Z + hip);
        return true;
    }
}
