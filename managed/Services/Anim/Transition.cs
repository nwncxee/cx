using System;
using System.Numerics;
using CX.Engine;
using CX.Framework;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>
/// Movement changes blended, a <see cref="PoseService"/> module (<c>anim.blend</c>). The engine crossfades only a
/// change of animation NAME, so a change that keeps it -- walk to walk-back, or a diagonal swapped for the straight
/// cycle -- flips the pace in one frame; here each tracked bone's last pose keeps moving with its own decaying angular
/// velocity and is crossfaded into what the engine now poses. This is the ANIMATION layer: corrections go on top.
/// </summary>
internal static unsafe class Transition
{
    /// <summary>Seconds the old movement takes to hand over to the new one.</summary>
    private const float BlendTime = 0.3f;
    /// <summary>Seconds over which the carried movement's velocity halves: it follows through, then settles.</summary>
    private const float SpinHalfLife = 0.06f;
    /// <summary>Radians per second a bone's measured velocity is clamped to: a restarted cycle's jump is not a swing.</summary>
    private const float MaxSpin = 15f;
    internal static int Blends;

    /// <summary>Anim ids a change between is blended: standing, combat-ready, the turns on the spot and every walk and run.</summary>
    internal static bool Movement(int anim) => anim is 0 or 1 or >= 0x50 and <= 0x53 || PoseService.IsLocomotion(anim);

    /// <summary>Starts a blend when a <paramref name="tracked"/> body's played anim id changed between two walks or runs -- a
    /// diagonal swapped for the straight cycle is no change, and a start or stop is the engine's own crossfade -- and
    /// advances one that runs; true while it does. A blend seeds from the last output and velocity <see cref="Pose"/> recorded.</summary>
    internal static bool Begin(ref PoseService.Slot s, int anim, bool tracked, float dt)
    {
        int played = Diagonal.Played(anim, s.Swapped);
        bool change = played != s.LastAnim && PoseService.IsLocomotion(played) && PoseService.IsLocomotion(s.LastAnim);
        s.LastAnim = played;
        if (!AnimKnobs.Blend.V || !tracked) return s.Blending = false;
        if (change)
        {
            s.Blending = true;
            s.BlendT = 0f;
            Blends++;
            s.Src = s.Out;
            s.SrcSpin = s.Spin;
        }
        if (s.Blending && (s.BlendT += dt) >= BlendTime) s.Blending = false;
        return s.Blending;
    }

    /// <summary>The animation layer's pose for this frame: while <paramref name="blending"/>, each bone carried on from the
    /// old movement and crossfaded into the engine's; always, the output and its velocity recorded for the next change.
    /// The caller has saved the engine's values before a blend writes.</summary>
    internal static void Pose(ref PoseService.Slot s, in PoseService.Bones b, bool blending, bool tracked, float dt)
    {
        float w = Ease01(s.BlendT / BlendTime), decay = MathF.Exp(-dt * MathF.Log(2f) / SpinHalfLife);
        for (int i = 0; i < PoseService.Bones.Count; i++)
        {
            IntPtr p = b[i];
            bool same = tracked && p != IntPtr.Zero && s.OutParts[i] == (long)p;
            s.OutParts[i] = (long)p;
            if (p == IntPtr.Zero) continue;
            Quaternion e = Local(p), q = e;
            if (blending && same)
            {
                Vector3 v = s.SrcSpin[i] * decay;
                Quaternion from = Quaternion.Normalize(Exp(v * dt) * s.Src[i]);
                s.SrcSpin[i] = v;
                s.Src[i] = from;
                q = Quaternion.Slerp(from, e, w);
                SetLocal(p, q);
            }
            s.Spin[i] = same && dt > 0f ? Clamp(Log(q * Quaternion.Conjugate(s.Out[i])) / dt) : Vector3.Zero;
            s.Out[i] = q;
        }
    }

    /// <summary>Smoothstep over [0, 1]: the hand-over starts and ends at rest.</summary>
    private static float Ease01(float x) => x <= 0f ? 0f : x >= 1f ? 1f : x * x * (3f - 2f * x);

    private static Vector3 Clamp(Vector3 v)
    {
        float l = v.Length();
        return l > MaxSpin ? v * (MaxSpin / l) : v;
    }

    /// <summary>A knee swinging at 4 rad/s reverses (walk to walk-back) through the real <see cref="Begin"/>
    /// and <see cref="Pose"/> over a fake engine part: the blend starts where the swing was, carries it on
    /// with no frame's step past twice the swing's own, and hands over; a stop and a start blend nothing. Oracle:
    /// hand-written bounds on the angle read back off the part the layer writes, and literal anim ids.</summary>
    internal static bool SelfCheck()
    {
        // Staged: the master and the blend toggle both, so the player's cx.cfg cannot decide whether the
        // blend under test runs at all.
        bool en0 = AnimKnobs.Enabled.V;
        var bk = AnimKnobs.G.Get("blend") as Knob<bool>;
        bool bv0 = bk?.V ?? false;
        AnimKnobs.Enabled.V = true;
        if (bk != null) bk.V = true;
        try
        {
            const int Walk = 2, WalkBack = 3;
            float dt = 1f / 60f, spin = 4f;
            int bytes = EngineLayout.PartRot + 16;
            byte* part = stackalloc byte[bytes];
            for (int i = 0; i < bytes; i++) part[i] = 0;
            IntPtr p = (IntPtr)part;
            PoseService.Bones b = default;
            b.RThigh = p;                       // bone 0 is the knee this models; the rest stay absent
            PoseService.Slot s = default;
            s.LastAnim = Walk;

            // The swing, measured: five frames of the walk cycle carrying the knee forward, so the layer has
            // a velocity to carry on with when the movement changes.
            float a = 0f;
            for (int i = 0; i < 5; i++)
            {
                a += spin * dt;
                SetLocal(p, Quaternion.CreateFromAxisAngle(Vector3.UnitX, a));
                Pose(ref s, in b, Begin(ref s, Walk, tracked: true, dt), tracked: true, dt);
            }
            float swung = a;

            // ...and the reversal: the same cycle played backward, so the authored pose travels the other way
            // while the carried swing is still going forward.
            float prev = 0f, maxStep = 0f, gap = 0f;
            bool first = true, carried = false, blended = false;
            for (int i = 0; i < 40; i++)
            {
                a -= spin * dt;
                SetLocal(p, Quaternion.CreateFromAxisAngle(Vector3.UnitX, a));
                bool blending = Begin(ref s, WalkBack, tracked: true, dt);
                Pose(ref s, in b, blending, tracked: true, dt);
                Quaternion q = Local(p);
                float angle = 2f * MathF.Atan2(q.X, q.W);
                if (first) { carried = angle > swung; first = false; }
                else maxStep = MathF.Max(maxStep, MathF.Abs(angle - prev));
                prev = angle;
                if (blending) { blended = true; gap = MathF.Abs(angle - a); }
            }
            // The hand-over is complete by its last frame: within 3 degrees of the authored pose, which the
            // carried swing had drifted more than a radian away from.
            bool landed = blended && gap < 0.05f;
            // a stop and a start change the animation's name, which the engine crossfades: no second blend over it
            s.LastAnim = Walk;
            bool stop = Begin(ref s, 0, tracked: true, dt), start = Begin(ref s, Walk, tracked: true, dt);
            return bk != null && carried && maxStep <= 2f * spin * dt && landed && !stop && !start
                   && Movement(2) && Movement(3) && Movement(0) && !Movement(0x2f);
        }
        finally
        {
            AnimKnobs.Enabled.V = en0;
            if (bk != null) bk.V = bv0;
        }
    }
}
