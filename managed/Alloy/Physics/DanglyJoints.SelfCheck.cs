using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Physics;

/// <summary>The joint solver's steady states and transients, each against a closed-form value worked out by
/// hand from the spec's force law and the fixture's authored numbers.</summary>
internal static class DanglyJointsCheck
{
    internal static void Cases(Checker check)
    {
        AtRest(check);
        SteadyWind(check);
        Flutter(check);
        Ringing(check);
        Tilt(check);
        RestLengths(check);
        Jump(check);
        FrameRate(check);
        BoneMapping(check);
        Creep(check);
        Carried(check);
    }

    private static DanglyInputs In(Matrix4x4 world, Vector3 wind, float dt,
                                  float gain = 1f, float tilt = 0f, float drag = 0f, float cage = 1.5f)
        => new(world, wind, dt, gain, tilt, drag, cage);

    /// <summary>Oracle: the identity matrix. A seeded rig sits on its rest pose, so every bone maps a rest
    /// vertex to itself and the second frame has nothing to publish.</summary>
    private static void AtRest(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Card());
        if (r == null) { check.Expect(false, "the card rigs for the rest check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        DanglyInputs inp = In(Matrix4x4.Identity, Vector3.Zero, 1f / 60f);
        bool first = DanglyJoints.Step(r, s, inp, bones);
        bool identity = true;
        for (int b = 0; b < bones.Length; b++) identity &= bones[b] == Matrix4x4.Identity;
        check.Expect(first, "the seeding frame publishes its rest pose once");
        check.Expect(identity, "a seeded rig's bones are all identity");
        check.Expect(!DanglyJoints.Step(r, s, inp, bones),
                     "a settled rig under unchanged inputs publishes nothing");
        check.Expect(DanglySelfCheck.Near(s.X[1], r.JointRest[1], 1e-6f),
                     "seeding puts every joint on its target");
        check.Expect(s.V[1] == Vector3.Zero, "seeding leaves no velocity");
    }

    /// <summary>Oracle: vanilla's drift equilibrium by hand. The stock step drifts a vertex 20 w cap per 1/60 s,
    /// and its damping holds that at 3 period / (c t) times the drift rate: 25.714286 w cap for the card (c 112,
    /// t 1, period 0.8). A wind of 0.5 / 25.714286 along y leans it by half its cap, 0.5 * 0.3 * 0.560784 =
    /// 0.0841176 node units, and a lean that has stopped moving publishes nothing.</summary>
    private static void SteadyWind(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Card());
        if (r == null) { check.Expect(false, "the card rigs for the wind check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        var wind = new Vector3(0f, 0.019444444f, 0f);
        DanglyInputs inp = In(Matrix4x4.Identity, wind, 1f / 60f);
        for (int f = 0; f < 1500; f++) DanglyJoints.Step(r, s, inp, bones);
        Vector3 off = s.X[1] - r.JointRest[1];
        check.Expect(DanglySelfCheck.Rel(off.Y, 0.0841176f, 0.02f),
                     "a steady wind leans the joint to half its cap");
        check.Expect(MathF.Abs(off.X) + MathF.Abs(off.Z) < 1e-3f,
                     "a y wind on a card leans it along y alone");
        check.Expect(!DanglyJoints.Step(r, s, inp, bones),
                     "a card leaning still in a steady wind is settled: there is nothing new to publish");
    }

    /// <summary>Oracle: the spring's response to its own drift, by hand. A wind of 0.02 turning at 5 Hz drifts
    /// the card's joint at U = 1200 * 0.02 * 0.1682353 = 4.037647 units/s; with w^2 = 56 and 2zw = 1.2 it traces
    /// a circle of radius U sqrt(W^2 + 1.44) / sqrt((56 - W^2)^2 + 1.44 W^2) = 0.136243 at W = 10 pi. A wind
    /// too quick for the spring still moves the part, as the stock solve's does.</summary>
    private static void Flutter(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Card());
        if (r == null) { check.Expect(false, "the card rigs for the flutter check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        const float Dt = 1f / 240f, Spin = 10f * MathF.PI;
        float lo = float.MaxValue, hi = 0f;
        for (int f = 0; f < 5040; f++)
        {
            float t = f * Dt;
            var wind = new Vector3(MathF.Cos(Spin * t), MathF.Sin(Spin * t), 0f) * 0.02f;
            DanglyJoints.Step(r, s, In(Matrix4x4.Identity, wind, Dt), bones);
            if (f < 4800) continue;
            float d = (s.X[1] - r.JointRest[1]).Length();
            lo = MathF.Min(lo, d); hi = MathF.Max(hi, d);
        }
        check.Expect(DanglySelfCheck.Rel(lo, 0.136243f, 0.03f) && DanglySelfCheck.Rel(hi, 0.136243f, 0.03f),
                     $"a wind turning at 5 Hz sweeps the card round a circle of radius 0.136243 ({lo}..{hi})");
    }

    /// <summary>Oracle: the damped period of the blob's own spring, 2 pi / (15 * sqrt(1 - 0.16)) = 0.457035 s,
    /// measured between the first and third zero crossings of a released displacement.</summary>
    private static void Ringing(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Blob());
        if (r == null) { check.Expect(false, "the blob rigs for the ringing check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        const float Dt = 1f / 240f;
        DanglyInputs inp = In(Matrix4x4.Identity, Vector3.Zero, Dt, cage: 10f);
        DanglyJoints.Step(r, s, inp, bones);
        s.X[1] = r.JointRest[1] + new Vector3(0.05f, 0f, 0f);

        float prev = 0.05f, t = 0f;
        float first = 0f, third = 0f;
        int crossings = 0;
        for (int f = 0; f < 480 && crossings < 3; f++)
        {
            DanglyJoints.Step(r, s, inp, bones);
            t += Dt;
            float cur = (s.X[1] - r.JointRest[1]).X;
            if (prev != 0f && MathF.Sign(cur) != MathF.Sign(prev))
            {
                float at = t - Dt + Dt * MathF.Abs(prev) / (MathF.Abs(prev) + MathF.Abs(cur));
                crossings++;
                if (crossings == 1) first = at;
                if (crossings == 3) third = at;
            }
            prev = cur;
        }
        check.Expect(crossings == 3, "a released Jiggle joint rings rather than creeping back");
        check.Expect(crossings == 3 && DanglySelfCheck.Rel(third - first, 0.457035f, 0.03f),
                     "it rings at its own damped period, 0.457035 s");
    }

    /// <summary>Oracle: the force law's fixed point again. Upright, model down and world down agree, so the
    /// term is zero; turned a quarter turn about x the gap is sqrt(2) long, and 9.81 * sqrt(2) / 225 =
    /// 0.0616598 node units, inside the blob's 1.5 * 0.123529 cage.</summary>
    private static void Tilt(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Blob());
        if (r == null) { check.Expect(false, "the blob rigs for the tilt check"); return; }
        var up = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        DanglyInputs level = In(Matrix4x4.Identity, Vector3.Zero, 1f / 60f, tilt: 1f);
        for (int f = 0; f < 240; f++) DanglyJoints.Step(r, up, level, bones);
        check.Expect((up.X[1] - r.JointRest[1]).Length() < 1e-5f,
                     "an upright part feels no gravity: its authored pose IS the equilibrium");

        check.Expect(DanglySelfCheck.Near(Steady(r, MathF.PI / 2f, 1f / 60f), 0.0616598f, 0.0616598f * 0.02f),
                     "a part laid on its back leans by g * |down gap| / w^2");
    }

    /// <summary>The settled excursion of the blob's joint with the part turned <paramref name="turn"/> about
    /// x, stepped at <paramref name="dt"/>.</summary>
    private static float Steady(DanglyRig r, float turn, float dt)
    {
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        Matrix4x4 world = Matrix4x4.CreateRotationX(turn);
        DanglyInputs inp = In(world, Vector3.Zero, dt, tilt: 1f);
        for (int f = 0; f < (int)(6f / dt); f++) DanglyJoints.Step(r, s, inp, bones);
        return (s.X[1] - Vector3.Transform(r.JointRest[1], world)).Length();
    }

    /// <summary>Oracle: the rope's own authored geometry -- five levels half a unit apart, so every segment
    /// is 0.5 long and stays so however hard the part is yanked.</summary>
    private static void RestLengths(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Rope());
        if (r == null) { check.Expect(false, "the rope rigs for the length check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        Vector3 at = Vector3.Zero;
        DanglyJoints.Step(r, s, In(Matrix4x4.CreateTranslation(at), Vector3.Zero, 1f / 60f, cage: 100f), bones);
        for (int f = 0; f < 6; f++)
        {
            at += new Vector3(0.9f, 0f, 0f);
            DanglyJoints.Step(r, s, In(Matrix4x4.CreateTranslation(at), Vector3.Zero, 1f / 60f, cage: 100f), bones);
        }
        bool kept = true;
        for (int j = 1; j < r.JointRest.Length; j++) kept &= DanglySelfCheck.Near((s.X[j] - s.X[j - 1]).Length(), 0.5f, 1e-3f);
        check.Expect(kept, "a yanked Swing chain still measures 0.5 a segment");
        check.Expect(DanglySelfCheck.Near(s.X[0], at, 1e-5f), "the chain's root is kinematic: it rides the part");
    }

    /// <summary>Oracle: the rest pose itself. A part that jumps two units has not swung there, so every joint
    /// is back on its target with no velocity.</summary>
    private static void Jump(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Card());
        if (r == null) { check.Expect(false, "the card rigs for the jump check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        var wind = new Vector3(0f, 0.05f, 0f);
        for (int f = 0; f < 300; f++)
            DanglyJoints.Step(r, s, In(Matrix4x4.Identity, wind, 1f / 60f), bones);
        bool leaning = (s.X[1] - r.JointRest[1]).Length() > 1e-3f;
        Matrix4x4 moved = Matrix4x4.CreateTranslation(new Vector3(2f, 0f, 0f));
        DanglyJoints.Step(r, s, In(moved, wind, 1f / 60f), bones);
        bool reset = true;
        for (int j = 0; j < r.JointRest.Length; j++)
            reset &= DanglySelfCheck.Near(s.X[j], Vector3.Transform(r.JointRest[j], moved), 1e-5f)
                  && s.V[j] == Vector3.Zero;
        check.Expect(leaning, "the wind moved the joint off its target first");
        check.Expect(reset, "a two-unit jump puts every joint back on its rest pose, at rest");
    }

    /// <summary>Oracle: the same closed-form steady state as the tilt case -- a force balance has no step size
    /// in it, so a 30 fps machine and a 144 fps one must land on 0.0616598 alike.</summary>
    private static void FrameRate(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Blob());
        if (r == null) { check.Expect(false, "the blob rigs for the frame-rate check"); return; }
        check.Expect(DanglySelfCheck.Rel(Steady(r, MathF.PI / 2f, 1f / 30f), 0.0616598f, 0.02f),
                     "at 30 fps the lean is the same 0.0616598");
        check.Expect(DanglySelfCheck.Rel(Steady(r, MathF.PI / 2f, 1f / 144f), 0.0616598f, 0.02f),
                     "at 144 fps the lean is the same 0.0616598");
    }

    /// <summary>Oracle: hand-placed joints, the frame given no time to integrate. A Sway or Jiggle joint put at
    /// rest + (0.05, -0.02, 0.03) must give a bone that is that literal translation; the rope's first joint
    /// swung 0.6 rad about y keeps its 0.5 length, at (0.5 sin 0.6, 0, -0.5 cos 0.6) = (0.2823212, 0,
    /// -0.4126678), and its bone must turn the rest joint there about the root by a pure rotation.</summary>
    private static void BoneMapping(Checker check)
    {
        Matrix4x4 world = Matrix4x4.CreateRotationX(MathF.PI / 2f)
                        * Matrix4x4.CreateTranslation(new Vector3(3f, -2f, 1f));
        var offset = new Vector3(0.05f, -0.02f, 0.03f);
        foreach (DanglyRig? r in new[] { DanglySelfCheck.Rig(DanglySelfCheck.Blob()),
                                         DanglySelfCheck.Rig(DanglySelfCheck.Card()) })
        {
            if (r == null) { check.Expect(false, "the blob and the card rig for the bone check"); return; }
            Matrix4x4 b = Placed(r, world, 1, r.JointRest[1] + offset);
            check.Expect(DanglySelfCheck.Near(Vector3.Transform(r.JointRest[1], b), r.JointRest[1] + offset, 1e-4f),
                         $"a {r.Class} bone carries the rest tip onto the joint");
            check.Expect(DanglySelfCheck.Near(Vector3.Transform(Vector3.Zero, b), offset, 1e-4f),
                         $"...as a pure translation: every vertex a {r.Class} bone reaches moves by the same offset");
        }

        DanglyRig? rope = DanglySelfCheck.Rig(DanglySelfCheck.Rope());
        if (rope == null) { check.Expect(false, "the rope rigs for the bone check"); return; }
        var swung = new Vector3(0.2823212f, 0f, -0.4126678f);
        Matrix4x4 m = Placed(rope, world, 1, swung);
        check.Expect(DanglySelfCheck.Near(Vector3.Transform(rope.JointRest[1], m), swung, 1e-4f),
                     "a Swing bone turns its rest joint onto the swung one");
        check.Expect(DanglySelfCheck.Near(Vector3.Transform(rope.JointRest[0], m), rope.JointRest[0], 1e-4f),
                     "...about the segment's root, which stays put");
        Matrix4x4 rr = m * Matrix4x4.Transpose(m);
        check.Expect(MathF.Abs(rr.M11 - 1f) + MathF.Abs(rr.M22 - 1f) + MathF.Abs(rr.M33 - 1f)
                     + MathF.Abs(rr.M12) + MathF.Abs(rr.M13) + MathF.Abs(rr.M23) < 1e-4f,
                     "...and its linear part is a pure rotation (R Rt = I): no scale or shear reaches the normals");
    }

    /// <summary>Seed <paramref name="r"/> at <paramref name="world"/>, put joint <paramref name="j"/> at the
    /// node-space point <paramref name="at"/> and solve a frame of no time: the bone over that joint.</summary>
    private static Matrix4x4 Placed(DanglyRig r, Matrix4x4 world, int j, Vector3 at)
    {
        var s = new DanglyJointsState(r.JointRest.Length);
        var bones = new Matrix4x4[r.BoneCount];
        DanglyJoints.Step(r, s, In(world, Vector3.Zero, 1f / 60f, cage: 100f), bones);
        s.X[j] = Vector3.Transform(at, world);
        DanglyJoints.Step(r, s, In(world, Vector3.Zero, 0f, cage: 100f), bones);
        return bones[j];
    }

    /// <summary>Oracle: the settled threshold, 1e-4. A wind that grows by a hair each frame moves the card's lean
    /// by far less than that per frame, yet the published bones must never trail the solved joint by more.</summary>
    private static void Creep(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Card());
        if (r == null) { check.Expect(false, "the card rigs for the creep check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        for (int f = 0; f < 600; f++) DanglyJoints.Step(r, s, In(Matrix4x4.Identity, Vector3.Zero, 1f / 60f), bones);
        int published = 0;
        float worst = 0f;
        for (int f = 0; f < 1200; f++)
        {
            var wind = new Vector3(0f, 2e-6f * f, 0f);
            if (DanglyJoints.Step(r, s, In(Matrix4x4.Identity, wind, 1f / 60f), bones)) published++;
            worst = MathF.Max(worst, (s.X[1] - s.Published[1]).Length());
        }
        check.Expect(published > 0, "a lean that creeps below the threshold every frame is still published");
        check.Expect(worst <= 1.0001e-4f, $"the published bones never trail the joint by more than 1e-4 (worst {worst})");
    }

    /// <summary>Oracle: the literal false. A part carried at a steady 1.5 m/s has no acceleration to answer once
    /// its start-up swing has rung out, so its node-space shape is still and there is nothing to publish.</summary>
    private static void Carried(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Blob());
        if (r == null) { check.Expect(false, "the blob rigs for the carried check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        bool last = true;
        for (int f = 0; f < 900; f++)
            last = DanglyJoints.Step(r, s, In(Matrix4x4.CreateTranslation(f * 0.025f, 0f, 0f), Vector3.Zero, 1f / 60f), bones);
        check.Expect(!last, "a part carried at a steady speed settles: its shape in its own frame stopped changing");
    }
}
