using System;
using System.Numerics;
using CX.Engine;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Follow-through, a <see cref="PoseService"/> module (<c>anim.follow</c>): each arm is an underdamped pendulum that
/// trails what turns and carries the torso, swings through and settles, while a neck spring steadies the head against
/// what the chest does (<see cref="Steady"/>). The driving spin is measured off the animation layer's pose after the
/// other modules wrote (<see cref="Measure"/>), so authored torso motion drives nothing; any non-locomotion action eases it out.
/// </summary>
internal static unsafe class Follow
{
    private const float Deg = MathF.PI / 180f;
    /// <summary>The trailing spring: natural frequency (Hz) and damping ratio. A hanging arm's own swing, stiffened a
    /// little by the shoulder: it overshoots once, by about a fifth, and settles inside a second.</summary>
    private const float Hz = 1.8f, Zeta = 0.45f;
    private const float Omega = 2f * MathF.PI * Hz;
    /// <summary>Times the physical pendulum's swing: the engine's motion arrives measured and smoothed (<c>MotionTime</c>,
    /// <c>AccelTime</c>), which takes the edge off every push.</summary>
    private const float LagGain = 1f;
    /// <summary>Largest lag any part takes, radians.</summary>
    private const float MaxLag = 7f * Deg;
    /// <summary>Share of its upper arm's lag the forearm adds at the elbow.</summary>
    private const float ForearmShare = 0.5f;
    /// <summary>Seconds the steadied head takes to follow the pose (the spring's time, as <see cref="PoseMath.Smooth"/>):
    /// the walk's once-a-second sway comes through, a shake at a few hertz does not.</summary>
    private const float SteadyTime = 0.15f;
    /// <summary>Share of the steadied head shown over the rigid one: some of the chest's motion still reaches it,
    /// as through a real neck.</summary>
    private const float SteadyShare = 0.6f;
    /// <summary>Largest the steadied head trails the pose, radians: past it the neck carries the head along.</summary>
    private const float MaxSteady = 12f * Deg;
    /// <summary>Seconds the follow-through eases in and out over as an action ends and starts.</summary>
    private const float FadeTime = 0.12f;
    /// <summary>Radians of lag under which nothing is written.</summary>
    private const float MinLag = 1e-3f;
    /// <summary>Radians of shown lag past which a body counts as trailing in the stats line: half a degree, the
    /// least anyone could see.</summary>
    private const float ShownLag = 0.5f * Deg;
    /// <summary>Longest step the springs integrate in one go, and the substeps past which a hitch-sized dt lets them
    /// rest instead (as <see cref="PoseMath.Wobble"/>).</summary>
    private const float Substep = 1f / 120f;
    private const int MaxSteps = 12;
    /// <summary><c>torso_g</c>'s index in <see cref="PoseService.BoneIndex"/> order.</summary>
    private const int TorsoBone = 6;

    /// <summary>The pendulums, a_ba's rig in model units off <c>torso_g</c>:
    /// each shoulder, and the reach from it to the centre of swing, two thirds down an arm. ponytail: every humanoid
    /// swings as a_ba; per-model geometry is the parts' own positions.</summary>
    private static readonly Vector3 ShoulderR = new(0.201f, -0.079f, 0.35f), ShoulderL = new(-0.201f, -0.079f, 0.35f),
                                    ArmHang = new(0f, 0f, -0.4f);

    internal static int Trailed;

    /// <summary>Advances the trailing parts for a body playing <paramref name="anim"/>; true while there is anything to
    /// write (<see cref="Write"/>). <paramref name="reset"/> (a teleport, first sight) starts them at rest.</summary>
    internal static bool Step(ref PoseService.Slot s, int anim, bool gated, bool reset, float scale, float dt)
    {
        bool known = s.FixKnown && !reset && dt > 0f;
        s.FixKnown = !reset;
        Vector3 fixRate = known ? (s.TorsoFix - s.TorsoFixWas) / dt : Vector3.Zero;
        s.TorsoFixWas = s.TorsoFix;
        s.TorsoFix = Vector3.Zero;
        // the body turns its arms with the PATH, not the facing: a facing swung round at a waypoint is the engine
        // re-aiming the model, and dragging the arms off it threw them wide on every click
        Vector3 spin = fixRate + new Vector3(0f, 0f, s.PathRate);
        bool write = false;
        if (!AnimKnobs.Follow.V && s.FollowW < MinLag) Rest(ref s, spin, false);
        else
        {
            bool live = AnimKnobs.Follow.V && !gated && Transition.Movement(anim);
            Advance(ref s, spin, new Vector3(-s.Speed * s.PathRate, s.Cue, 0f) / scale, live, reset, dt);
            write = s.FollowW > MinLag;
        }
        // a head not steadied this frame starts from the pose next time, not from a stale orientation
        if (!write || reset) s.HeadKnown = false;
        return write;
    }

    /// <summary>One frame of the two arm springs, driven by the torso's <paramref name="spin"/> (rad/s) and the body's
    /// <paramref name="accel"/> (model units/s²), both in the model frame, while <paramref name="live"/>; the fade eases
    /// toward it either way.</summary>
    internal static void Advance(ref PoseService.Slot s, Vector3 spin, Vector3 accel, bool live, bool reset, float dt)
    {
        if (reset) { Rest(ref s, spin, live); return; }
        Vector3 dw = spin - s.TorsoSpin;
        s.TorsoSpin = spin;
        Smooth(ref s.FollowW, ref s.FollowWV, live ? 1f : 0f, dt, FadeTime);
        if (!live) dw = accel = Vector3.Zero;
        Trail(ref s.ArmR, ref s.ArmRV, dw, accel, ShoulderR, ArmHang, dt);
        Trail(ref s.ArmL, ref s.ArmLV, dw, accel, ShoulderL, ArmHang, dt);
    }

    private static void Rest(ref PoseService.Slot s, Vector3 spin, bool live)
    {
        s.TorsoSpin = spin;
        s.ArmR = s.ArmRV = s.ArmL = s.ArmLV = Vector3.Zero;
        s.FollowW = live ? 1f : 0f;
        s.FollowWV = 0f;
    }

    /// <summary>One arm's lag <paramref name="x"/> behind the torso (rotation vector, model frame) and its rate
    /// <paramref name="v"/>. The torso's change of spin <paramref name="dw"/> turns out from under it -- save about the
    /// arm's own length -- and moves its <paramref name="pivot"/>, and the body's <paramref name="accel"/> swings it, as
    /// a pendulum reaching <paramref name="hang"/> from the pivot to its centre of swing; the spring pulls it back.</summary>
    internal static void Trail(ref Vector3 x, ref Vector3 v, Vector3 dw, Vector3 accel, Vector3 pivot, Vector3 hang, float dt)
    {
        if (!(dt > 0f)) return;
        float l = hang.Length();
        Vector3 d = hang / l;
        Vector3 turn = dw - d * Vector3.Dot(d, dw);
        v -= LagGain * (turn + Vector3.Cross(d, Vector3.Cross(dw, pivot)) / l);
        Vector3 push = -LagGain / l * Vector3.Cross(d, accel);
        int n = (int)MathF.Ceiling(dt / Substep);
        if (n > MaxSteps) { x = v = Vector3.Zero; return; }
        float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            v += (push - Omega * Omega * x - 2f * Zeta * Omega * v) * h;
            x += v * h;
        }
        float m = x.Length();
        if (m <= MaxLag) return;
        x *= MaxLag / m;
        Vector3 u = x / MaxLag;
        v -= u * MathF.Max(0f, Vector3.Dot(v, u));
    }

    /// <summary>Records how far the other modules turned the torso off the animation layer's pose this frame (a
    /// rotation vector in the torso's parent frame, which is the model's), for the next frame's spin. After the last
    /// torso write.</summary>
    internal static void Measure(ref PoseService.Slot s, IntPtr torso)
    {
        if (torso == IntPtr.Zero) return;
        s.TorsoFix = Log(Local(torso) * Quaternion.Conjugate(s.Out[TorsoBone]));
    }

    /// <summary>Turns each upper arm by its lag about the shoulder and the forearm by <see cref="ForearmShare"/> more
    /// about the elbow, and steadies the head, over whatever the pose holds: after the torso's and the head's last
    /// writes, since the arms hang from the one and the steadying reads the other. A part whose parent is not the
    /// expected one is left alone.</summary>
    internal static void Write(ref PoseService.Slot s, in PoseService.Bones b, Vector3 fwdH, Vector3 right, float dt)
    {
        if (b.Torso == IntPtr.Zero) return;
        World(b.Torso, out _, out Quaternion qt);
        float w = Math.Clamp(s.FollowW, 0f, 1f);
        Arm(b.Torso, qt, b.RBicep, b.RForearm, s.ArmR * w, fwdH, right);
        Arm(b.Torso, qt, b.LBicep, b.LForearm, s.ArmL * w, fwdH, right);
        float head = 0f;
        if (b.Neck != IntPtr.Zero && b.Head != IntPtr.Zero && Parent(b.Neck) == b.Torso && Parent(b.Head) == b.Neck)
        {
            Quaternion facing = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.Atan2(-fwdH.X, fwdH.Y));
            Quaternion qn = qt * Local(b.Neck), pose = Quaternion.Conjugate(facing) * qn * Local(b.Head);
            if (!s.HeadKnown) { s.HeadQ = pose; s.HeadV = Vector3.Zero; s.HeadKnown = true; }
            Vector3 lag = Steady(ref s.HeadQ, ref s.HeadV, pose, dt) * (w * SteadyShare);
            Turn(b.Head, qn, Exp(Vector3.Transform(lag, facing)));
            head = lag.Length();
        }
        if (MathF.Max(w * MathF.Max(s.ArmR.Length(), s.ArmL.Length()), head) > ShownLag) Trailed++;
    }

    /// <summary>One frame of the neck: <paramref name="q"/>, the steadied head's orientation in the heading frame, follows
    /// <paramref name="pose"/>, the orientation the pose gives it, through a critically damped spring on the difference
    /// (<paramref name="v"/> its rate), held within <see cref="MaxSteady"/>. The difference, as a rotation vector in the
    /// heading frame: how far the steadied head is turned off the pose's.</summary>
    internal static Vector3 Steady(ref Quaternion q, ref Vector3 v, Quaternion pose, float dt)
    {
        Vector3 lag = Log(q * Quaternion.Conjugate(pose));
        if (dt > 0f)
        {
            Smooth(ref lag.X, ref v.X, 0f, dt, SteadyTime);
            Smooth(ref lag.Y, ref v.Y, 0f, dt, SteadyTime);
            Smooth(ref lag.Z, ref v.Z, 0f, dt, SteadyTime);
        }
        float m = lag.Length();
        if (m > MaxSteady)
        {
            lag *= MaxSteady / m;
            Vector3 u = lag / MaxSteady;
            v -= u * MathF.Max(0f, Vector3.Dot(v, u));
        }
        q = Quaternion.Normalize(Exp(lag) * pose);
        return lag;
    }

    private static void Arm(IntPtr torso, Quaternion qt, IntPtr bicep, IntPtr forearm, Vector3 lag, Vector3 fwdH, Vector3 right)
    {
        if (bicep == IntPtr.Zero || Parent(bicep) != torso) return;
        Quaternion qb = Turn(bicep, qt, InWorld(lag, fwdH, right));
        if (forearm != IntPtr.Zero && Parent(forearm) == bicep) Turn(forearm, qb, InWorld(lag * ForearmShare, fwdH, right));
    }

    /// <summary>Turns <paramref name="part"/>, whose parent's world rotation is <paramref name="qp"/>, by the world
    /// rotation <paramref name="r"/> about its own pivot; its new world rotation.</summary>
    private static Quaternion Turn(IntPtr part, Quaternion qp, Quaternion r)
    {
        Quaternion q = Quaternion.Normalize(r * qp * Local(part));
        SetLocal(part, Quaternion.Conjugate(qp) * q);
        return q;
    }

    /// <summary>The world rotation of a model-frame rotation vector: +X the character's right, +Y its forward, +Z up.</summary>
    private static Quaternion InWorld(Vector3 lag, Vector3 fwdH, Vector3 right) => Exp(right * lag.X + fwdH * lag.Y + Vector3.UnitZ * lag.Z);

    private static IntPtr Parent(IntPtr part) => PoseRig.Parent(part);

    /// <summary>A turn on the spot (6 rad/s for a second, measured as <see cref="PoseService.Motion"/> does) swings the
    /// arms behind, overshooting by under a third through the turn and swinging through at the stop; a walk that stops
    /// swings them forward. Either settles to a tenth within 1.2 s, no lag passes the cap even at an instant about-face,
    /// an action starting mid-swing eases it out, and the neck (<see cref="SteadyCheck"/>) keeps a shake off the head.</summary>
    internal static bool SelfCheck()
    {
        const float dt = 1f / 60f;
        PoseService.Slot s = default;
        float peakR = 0f, overshootR = 0f, through = 0f, late = 0f, peak = 0f;
        bool behind = false;
        for (int i = 0; i < 150; i++)
        {
            float t = i * dt;
            float yaw = t < 1f ? 6f * t : 6f;
            Vector3 fwd = new(-MathF.Sin(yaw), MathF.Cos(yaw), 0f);
            PoseService.Motion(ref s, Vector3.Zero, fwd, i == 0, dt);
            Advance(ref s, new Vector3(0f, 0f, s.YawRate), new Vector3(-s.Speed * s.YawRate, s.Accel, 0f), true, i == 0, dt);
            peak = MathF.Max(peak, Shown(s));
            if (i == 6) behind = s.ArmR.X < -Deg && s.ArmL.X > Deg;
            if (t < 1f) { peakR = MathF.Min(peakR, s.ArmR.X); if (t > 0.3f) overshootR = MathF.Max(overshootR, s.ArmR.X); }
            else if (t < 2.2f) through = MathF.Max(through, s.ArmR.X);
            else late = MathF.Max(late, Shown(s));
        }
        bool turn = behind && overshootR < 0.3f * -peakR && through > Deg && late < 0.1f * through && peak <= MaxLag + 1e-4f;

        PoseService.Slot w = default;
        float fwdArm = 0f, rest = 0f;
        for (int i = 0; i < 180; i++)
        {
            float t = i * dt;
            PoseService.Motion(ref w, new Vector3(0f, t < 1f ? 2f * dt : 0f, 0f), Vector3.UnitY, i == 0, dt);
            Advance(ref w, new Vector3(0f, 0f, w.YawRate), new Vector3(-w.Speed * w.YawRate, w.Accel, 0f), true, i == 0, dt);
            if (t >= 1f && t < 1.6f) fwdArm = MathF.Max(fwdArm, MathF.Min(w.ArmR.X, w.ArmL.X));
            if (t >= 2.2f) rest = MathF.Max(rest, Shown(w));
        }
        bool stop = fwdArm > Deg && rest < 0.1f * fwdArm;

        PoseService.Slot f = default;
        Advance(ref f, Vector3.Zero, Vector3.Zero, true, true, dt);
        Advance(ref f, new Vector3(0f, 0f, 200f), Vector3.Zero, true, false, dt);
        bool capped = f.ArmR.Length() <= MaxLag + 1e-4f && f.ArmR.Length() > 0.5f * MaxLag;
        float first = 0f;
        for (int i = 0; i < 30; i++)
        {
            Advance(ref f, new Vector3(0f, 0f, 200f), Vector3.Zero, false, false, dt);
            if (i == 0) first = f.FollowW;
        }
        bool fades = first > 0.5f && f.FollowW < 0.02f;
        return turn && stop && capped && fades && SteadyCheck();
    }

    /// <summary>The neck at 144 fps: a 3° shake at 8 Hz on top of the chest's 6° sway at 1 Hz reaches the head at under
    /// half of it, while the sway keeps most of its size; a sudden 40° snap of the pose is carried along past
    /// <see cref="MaxSteady"/>; a still pose leaves nothing behind. About one axis a rotation adds, so the shake that
    /// reaches the head is the difference from a neck given the sway alone.</summary>
    private static bool SteadyCheck()
    {
        const float dt = 1f / 144f;
        Quaternion q = Quaternion.Identity, qs = Quaternion.Identity;
        Vector3 v = Vector3.Zero, vs = Vector3.Zero;
        float shake = 0f, sway = 0f;
        for (int i = 0; i < 720; i++)
        {
            float t = i * dt;
            float slow = 6f * Deg * MathF.Sin(2f * MathF.PI * t), fast = 3f * Deg * MathF.Sin(16f * MathF.PI * t);
            float both = slow + fast + SteadyShare * Steady(ref q, ref v, Quaternion.CreateFromAxisAngle(Vector3.UnitX, slow + fast), dt).X;
            float alone = slow + SteadyShare * Steady(ref qs, ref vs, Quaternion.CreateFromAxisAngle(Vector3.UnitX, slow), dt).X;
            if (i < 288) continue;
            shake = MathF.Max(shake, MathF.Abs(both - alone));
            sway = MathF.Max(sway, MathF.Abs(alone));
        }
        bool filters = shake < 0.5f * 3f * Deg && sway > 0.6f * 6f * Deg;

        q = Quaternion.Identity; v = Vector3.Zero;
        Quaternion snap = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 40f * Deg);
        float most = Steady(ref q, ref v, snap, dt).Length();
        for (int i = 0; i < 288; i++) Steady(ref q, ref v, snap, dt);
        float settled = Steady(ref q, ref v, snap, dt).Length();
        return filters && most <= MaxSteady + 1e-4f && settled < 0.1f * Deg;
    }

    /// <summary>The largest lag the arms show.</summary>
    private static float Shown(in PoseService.Slot s) => MathF.Max(s.ArmR.Length(), s.ArmL.Length());
}
