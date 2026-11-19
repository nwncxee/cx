using System;
using System.Numerics;

namespace CX.Services.Anim;

/// <summary>One body's leg grades over a run of frames. <see cref="KneeBend"/>: the largest shortening of a leg's hip-to-ankle
/// distance against the animated one, as a share of that animated distance (0.1 is a leg a tenth shorter). <see cref="Slide"/>:
/// the largest horizontal speed of a drawn foot while its walk clip has it on the ground, world units per second.
/// <see cref="HipTurn"/>: the largest |d turn / dt| of the hips, degrees per second.</summary>
internal readonly record struct LegGrades(float KneeBend, float Slide, float HipTurn);

/// <summary>Grades what the leg solve did, from the frames it was given and the poses it gave back, and nothing else.</summary>
internal static class LegGrade
{
    /// <summary>The grades of <paramref name="poses"/>[i] answering <paramref name="frames"/>[i] for one body, in time order. A leg
    /// counts only where the frame carries a walk (its thighs are read then); a pair of frames counts for slide and turn only
    /// when the later is no teleport and has a positive step. Where the IK writes nothing the foot is the animated ankle.</summary>
    internal static LegGrades Grade(ReadOnlySpan<LegFrame> frames, ReadOnlySpan<LegPose> poses)
    {
        int n = Math.Min(frames.Length, poses.Length);
        float bend = 0f, slide = 0f, turn = 0f;
        for (int i = 0; i < n; i++)
        {
            ref readonly LegFrame f = ref frames[i];
            ref readonly LegPose p = ref poses[i];
            if (f.Walk.Present)
            {
                bend = MathF.Max(bend, Bend(f.RThigh, f.RAnkle, p, p.DeltaR));
                bend = MathF.Max(bend, Bend(f.LThigh, f.LAnkle, p, p.DeltaL));
            }
            if (i == 0 || f.Snap || !(f.Dt > 0f)) continue;
            ref readonly LegFrame q = ref frames[i - 1];
            ref readonly LegPose pq = ref poses[i - 1];
            turn = MathF.Max(turn, MathF.Abs(p.Turn - pq.Turn) / f.Dt * (180f / MathF.PI));
            if (f.Walk.ContactR && q.Walk.ContactR) slide = MathF.Max(slide, Speed(Foot(f.RAnkle, p, p.DeltaR), Foot(q.RAnkle, pq, pq.DeltaR), f.Dt));
            if (f.Walk.ContactL && q.Walk.ContactL) slide = MathF.Max(slide, Speed(Foot(f.LAnkle, p, p.DeltaL), Foot(q.LAnkle, pq, pq.DeltaL), f.Dt));
        }
        return new LegGrades(bend, slide, turn);
    }

    /// <summary>The pelvis's offset: the drop or rise.</summary>
    private static Vector3 Pelvis(in LegPose p) => new(0f, 0f, p.Body);

    /// <summary>Where a foot is drawn: the animated ankle carried by the pelvis, and moved by its IK delta when the IK writes.</summary>
    private static Vector3 Foot(Vector3 ankle, in LegPose p, Vector3 delta) => ankle + Pelvis(p) + (p.Ik ? delta : Vector3.Zero);

    private static float Speed(Vector3 now, Vector3 before, float dt) => MathF.Sqrt(Vector2.DistanceSquared(new(now.X, now.Y), new(before.X, before.Y))) / dt;

    /// <summary>The share a leg is shortened by: the animated hip-to-ankle distance less the drawn one, over the animated one.</summary>
    private static float Bend(Vector3 thigh, Vector3 ankle, in LegPose p, Vector3 delta)
    {
        float animated = Vector3.Distance(thigh, ankle);
        if (!(animated > 1e-4f)) return 0f;
        float drawn = Vector3.Distance(thigh + Pelvis(p), Foot(ankle, p, delta));
        return MathF.Max(0f, (animated - drawn) / animated);
    }
}
