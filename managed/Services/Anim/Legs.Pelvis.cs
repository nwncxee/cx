using System;
using System.Numerics;
using static CX.Services.Anim.PoseMath;

namespace CX.Services.Anim;

/// <summary>The pelvis solve: each frame, from both feet's targets, how far the pelvis drops so each leg reaches its
/// target no longer than the clip draws it or <see cref="Reach"/> of its length, eased the same way down and up.</summary>
internal static partial class Legs
{
    /// <summary>The share of a leg's length the solve stretches it to at most, where the clip itself draws it shorter.</summary>
    internal const float Reach = 0.97f;
    /// <summary>The share of a leg's length the pelvis drops at most to let a leg reach.</summary>
    internal const float MaxDrop = 0.12f;
    /// <summary>Seconds the pelvis drop eases toward this frame's need over, down and back up alike: quick enough to be
    /// back up between a long stride's double supports, slow enough that one frame's need is no jolt.</summary>
    private const float DropTime = 0.06f;

    /// <summary>The pelvis drop this frame (world units, down), from where each leg's target sits off its hip joint with
    /// the pelvis at the lower floor: <paramref name="upR"/>, <paramref name="upL"/> are each foot's floor over that.
    /// <paramref name="reachR"/>, <paramref name="reachL"/>: how long each leg may be drawn (0: no bound).</summary>
    private static float Pelvis(ref LegState s, in LegFrame f, in LegPose p, float upR, float upL, out float reachR, out float reachL)
    {
        reachR = reachL = 0f;
        if (f.Gated || f.Snap || !(f.Dt > 0f)) { s.Drop = s.DropV = 0f; return 0f; }
        if (f.Walk.Present) s.LegLen = f.Walk.LegLength * f.Scale;
        Vector3 animR = f.RAnkle - f.RThigh, animL = f.LAnkle - f.LThigh;
        float lenR = Stretch(f, p, true), lenL = Stretch(f, p, false), leg = MathF.Max(s.LegLen, MathF.Max(lenR, lenL)), cap = MaxDrop * leg;
        reachR = MathF.Max(Reach * leg, lenR);
        reachL = MathF.Max(Reach * leg, lenL);
        float needR = Need(animR + p.WarpR + new Vector3(0f, 0f, upR), reachR, cap);
        float needL = Need(animL + p.WarpL + new Vector3(0f, 0f, upL), reachL, cap);
        Smooth(ref s.Drop, ref s.DropV, MathF.Max(needR, needL), f.Dt, DropTime);
        return s.Drop > FootPlacement.MinLift ? s.Drop : 0f;
    }

    /// <summary>How long the clip draws a leg (world): as long as it is this frame, or at the walk's landing, its longest.</summary>
    internal static float Stretch(in LegFrame f, in LegPose p, bool right)
    {
        Vector3 thigh = right ? f.RThigh : f.LThigh;
        float now = Vector3.Distance(right ? f.RAnkle : f.LAnkle, thigh);
        if (!f.Walk.Present) return now;
        Vector3 hip = Vector3.Transform(thigh - f.Origin, Quaternion.Conjugate(p.Legs)) / f.Scale;
        return MathF.Max(now, Vector3.Distance(right ? f.Walk.LandR : f.Walk.LandL, hip) * f.Scale);
    }

    /// <summary>Ankle delta <paramref name="delta"/> on a leg whose animated ankle sits <paramref name="leg"/> off its hip
    /// joint (world, the pelvis's offset carrying both), pulled in toward the hip to <paramref name="reach"/> when past it:
    /// the pelvis eases toward its drop, and a frame it lags is not a frame the leg is drawn stretched.</summary>
    private static Vector3 Reachable(Vector3 delta, Vector3 leg, float reach)
    {
        Vector3 d = leg + delta;
        float len = d.Length();
        return reach > 0f && len > reach ? d * (reach / len) - leg : delta;
    }

    /// <summary>The drop that lets a leg reach <paramref name="d"/> (target less hip joint, world) at
    /// <paramref name="reach"/> long, capped at <paramref name="cap"/>; under <see cref="FootPlacement.MinLift"/> is none.</summary>
    internal static float Need(Vector3 d, float reach, float cap)
    {
        float h2 = d.X * d.X + d.Y * d.Y;
        if (h2 >= reach * reach) return cap;
        float drop = Math.Clamp(-d.Z - MathF.Sqrt(reach * reach - h2), 0f, cap);
        return drop > FootPlacement.MinLift ? drop : 0f;
    }

    /// <summary>The need worked by hand for a 0.9 leg whose hip joint is 0.77 over its ankle (none 0.3 ahead, 0.054368
    /// at 0.5, the 0.108 cap past reach), and the drop over a 0.5 s cycle needing 0.054368 for its first 0.1 s: no frame
    /// takes a quarter of that need at once, it reaches 0.04, it is back under 2 mm before the next, and 0.5 s after the
    /// need ends it is gone. Oracle: the leg triangle by hand and the hand-written need.</summary>
    internal static bool PelvisCheck()
    {
        Vector3 down = new(0f, 0f, -0.77f);
        bool need = Need(down + new Vector3(0f, 0.3f, 0f), 0.873f, 0.108f) == 0f
                    && MathF.Abs(Need(down + new Vector3(0f, 0.5f, 0f), 0.873f, 0.108f) - 0.054368f) < 1e-4f
                    && Need(down + new Vector3(0f, 1f, 0f), 0.873f, 0.108f) == 0.108f;
        const float Dt = 1f / 60f;
        LegState s = default;
        LegFrame f = default;
        f.Dt = Dt;
        f.Scale = 1f;
        f.Walk.Present = true;
        f.Walk.LegLength = 0.9f;
        f.RThigh = new Vector3(0.1f, 0f, 0.85f);
        f.LThigh = new Vector3(-0.1f, 0f, 0.85f);
        f.RAnkle = new Vector3(0.1f, 0f, 0.08f);
        f.LAnkle = new Vector3(-0.1f, 0f, 0.08f);
        LegPose p = default;
        p.Legs = Quaternion.Identity;
        float prev = 0f, step = 0f, deep = 0f, high = 1f, after = 1f;
        for (int n = 0; n < 180; n++)
        {
            float t = n * Dt;
            p.WarpR = t < 2f && t % 0.5f < 0.1f ? new Vector3(0f, 0.5f, 0f) : Vector3.Zero;
            float d = Pelvis(ref s, f, p, 0f, 0f, out _, out _);
            step = MathF.Max(step, MathF.Abs(d - prev));
            prev = d;
            if (t >= 1.5f && t < 2f) { deep = MathF.Max(deep, d); if (t % 0.5f > 0.45f) high = MathF.Min(high, d); }
            if (t >= 2.5f - 1e-4f) after = d;
        }
        return need && step < 0.25f * 0.054368f && deep > 0.04f && high < 0.002f && after == 0f;
    }
}
