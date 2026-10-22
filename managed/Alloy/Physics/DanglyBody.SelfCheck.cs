using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Physics;

/// <summary>The worn-strand suite: the capsule a part's box gives, the push out of it, and a worn rope that swings
/// past the clothing cage yet stays inside its cone and off a body capsule beside it. Every expected value is a
/// literal worked out by hand from the fixture's box or the solver's stated cone.</summary>
internal static class DanglyBodyCheck
{
    internal static void Cases(Checker check)
    {
        Capsule(check);
        Push(check);
        Strand(check);
        Nudge(check);
    }

    /// <summary>Oracle: the box by hand. 0.2 x 0.2 x 1 standing on the origin is a capsule up z from 0.1 to 0.9
    /// of radius 0.1; a 2 cm thick box is a buckle, refused.</summary>
    private static void Capsule(Checker check)
    {
        var b = new DanglyBody();
        bool took = b.AddBox(new Vector3(-0.1f, -0.1f, 0f), new Vector3(0.1f, 0.1f, 1f), Matrix4x4.Identity);
        check.Expect(took && b.Count == 1, "a limb-sized box becomes a capsule");
        check.Expect(b.Count == 1 && DanglySelfCheck.Near(b.A[0], new Vector3(0f, 0f, 0.1f), 1e-5f)
                     && DanglySelfCheck.Near(b.B[0], new Vector3(0f, 0f, 0.9f), 1e-5f)
                     && DanglySelfCheck.Near(b.R[0], 0.1f, 1e-6f),
                     "the capsule runs the box's long axis at its thinnest half-width");
        check.Expect(!b.AddBox(new Vector3(-0.01f, -0.1f, 0f), new Vector3(0.01f, 0.1f, 0.3f), Matrix4x4.Identity),
                     "a box thinner than a limb is no capsule");
    }

    /// <summary>Oracle: the radius plus the clearance, 0.1 + 0.03. A joint 5 cm off the axis whose rest place is
    /// outside goes out to 0.13 along x; one whose rest place is inside the capsule stays where it is.</summary>
    private static void Push(Checker check)
    {
        var b = new DanglyBody();
        b.AddBox(new Vector3(-0.1f, -0.1f, 0f), new Vector3(0.1f, 0.1f, 1f), Matrix4x4.Identity);
        Span<Vector3> x = stackalloc Vector3[] { Vector3.Zero, new(0.05f, 0f, 0.5f), new(0.05f, 0f, 0.5f) };
        ReadOnlySpan<Vector3> target = stackalloc Vector3[] { Vector3.Zero, new(0.5f, 0f, 0.5f), new(0f, 0f, 0.5f) };
        ReadOnlySpan<int> parent = stackalloc int[] { -1, 0, 0 };
        Span<Vector3> shove = stackalloc Vector3[3];
        b.Push(x, target, parent, 3, shove);
        check.Expect(DanglySelfCheck.Near(x[1], new Vector3(0.13f, 0f, 0.5f), 1e-5f),
                     $"a joint is pushed out to the capsule plus its clearance ({x[1]})");
        check.Expect(x[2] == new Vector3(0.05f, 0f, 0.5f), "a joint authored inside the capsule is left alone");
    }

    /// <summary>Oracles: the clothing cage by hand, 0.0841176 (as the worn check); the strand cone, 60 degrees;
    /// the capsule's radius plus clearance, 0.13, from its axis 0.25 beside the rope. Jerked away from the capsule
    /// at 3 m/s, the worn rope trails into the capsule's side unless the body holds it.</summary>
    private static void Strand(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Rope());
        if (r == null) { check.Expect(false, "the rope rigs for the strand check"); return; }
        check.Expect(r.Strand, "a Swing authored with 0.1 of give is a strand");
        Swung held = Jerk(r, withBody: true), free = Jerk(r, withBody: false);
        check.Expect(held.Reach > 2f * 0.0841176f, $"a worn strand swings well past the clothing cage ({held.Reach})");
        check.Expect(held.Angle <= 60.5f, $"a worn strand stays inside its 60 degree cone ({held.Angle})");
        check.Expect(held.Gap >= 0.13f - 1e-3f, $"a worn strand rests against the body, not in it ({held.Gap})");
        check.Expect(free.Gap < 0.1f, $"without the body the same swing passes through it ({free.Gap})");
    }

    /// <summary>Oracle: the nudge itself, 0.1. A capsule resting against the rope, carried 10 cm into it in one
    /// frame, pushes it inelastically: the rope is moved no further than the body moved, so its tip rises less than
    /// 0.1. A push turned into velocity flings it many times that.</summary>
    private static void Nudge(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Rope());
        if (r == null) { check.Expect(false, "the rope rigs for the nudge check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        var body = new DanglyBody();
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        int tip = r.JointRest.Length - 1;
        float rise = 0f;
        for (int f = 0; f < 120; f++)
        {
            Matrix4x4 world = Matrix4x4.CreateTranslation(f < 30 ? 0f : -0.1f, 0f, 0f);
            body.Clear();
            body.AddBox(new Vector3(0.03f, -0.1f, -1.4f), new Vector3(0.23f, 0.1f, 0f), world);
            DanglyJoints.Step(r, s, new DanglyInputs(world, Vector3.Zero, Vector3.Zero, 0f, 0f, 1f / 60f, 1f, 0f,
                                                     1.5f, worn: true, body: body), bones);
            rise = MathF.Max(rise, s.X[tip].Z - Vector3.Transform(r.JointRest[tip], world).Z);
        }
        check.Expect(rise < 0.1f, $"a body nudged into a resting strand does not fling it up ({rise})");
    }

    private readonly record struct Swung(float Reach, float Angle, float Gap);

    /// <summary>The rope worn on a part that moves 0.05 a frame along -x for half a second then stops, with a
    /// capsule 0.25 along +x riding the part: the furthest a joint strays, the widest segment angle, and the
    /// closest any joint comes to the capsule's axis.</summary>
    private static Swung Jerk(DanglyRig r, bool withBody)
    {
        var s = new DanglyJointsState(r.JointRest.Length);
        var body = new DanglyBody();
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        float reach = 0f, angle = 0f, gap = float.MaxValue;
        for (int f = 0; f < 150; f++)
        {
            Matrix4x4 world = Matrix4x4.CreateTranslation(-0.05f * Math.Min(f, 30), 0f, 0f);
            body.Clear();
            body.AddBox(new Vector3(0.15f, -0.1f, -1.4f), new Vector3(0.35f, 0.1f, 0f), world);
            DanglyJoints.Step(r, s, new DanglyInputs(world, Vector3.Zero, Vector3.Zero, 0f, 0f, 1f / 60f, 1f, 0f,
                                                     1.5f, worn: true, body: withBody ? body : null), bones);
            for (int j = 0; j < r.JointRest.Length; j++)
            {
                int up = r.JointParent[j];
                if (up < 0) continue;
                reach = MathF.Max(reach, (s.X[j] - Vector3.Transform(r.JointRest[j], world)).Length());
                Vector3 seg = Vector3.Normalize(s.X[j] - s.X[up]);
                angle = MathF.Max(angle, MathF.Acos(Math.Clamp(-seg.Z, -1f, 1f)) * 180f / MathF.PI);
                gap = MathF.Min(gap, (s.X[j] - DanglyBody.Closest(body.A[0], body.B[0], s.X[j])).Length());
            }
        }
        return new Swung(reach, angle, gap);
    }
}
