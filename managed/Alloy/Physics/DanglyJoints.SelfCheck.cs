using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Physics;

/// <summary>The joint solver's steady states and transients, each against a closed-form value worked out by
/// hand from the spec's force law and the fixture's authored numbers.</summary>
internal static class DanglyJointsCheck
{
    internal static void Cases(Checker check)
    {
        AtRest(check);
        SeededLeaning(check);
        Worn(check);
        SteadyWind(check);
        Bend(check);
        Pendulum(check);
        Strip(check);
        Air(check);
        Ringing(check);
        Tilt(check);
        RestLengths(check);
        Jump(check);
        FrameRate(check);
        BoneMapping(check);
        Creep(check);
        Carried(check);
    }

    /// <summary>A steady <paramref name="mean"/> wind (metres per second), no gusts or turbulence.</summary>
    private static DanglyInputs In(Matrix4x4 world, Vector3 mean, float dt,
                                  float gain = 1f, float tilt = 0f, float cage = 1.5f)
        => new(world, mean, Vector3.Zero, 0f, 0f, dt, gain, tilt, cage);

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

    /// <summary>Oracle: the identity map. A chain hanging off the axes, seeded on its rest pose, reads its joints
    /// back exactly; each segment's bone must leave its end in place, never turn it half a circle.</summary>
    private static void SeededLeaning(Checker check)
    {
        float worst = 0f;
        for (int k = 0; k < 256; k++)
        {
            ModelDef rope = DanglySelfCheck.Rope();
            Vector3[] pos = rope.Nodes[0].Mesh!.Positions;
            Matrix4x4 lean = Matrix4x4.CreateRotationX(0.0029f * k) * Matrix4x4.CreateRotationY(0.0041f * k);
            for (int i = 0; i < pos.Length; i++) pos[i] = Vector3.Transform(pos[i], lean);
            DanglyRig? r = DanglySelfCheck.Rig(rope);
            if (r == null) { check.Expect(false, "a leaning rope rigs for the seed check"); return; }
            var s = new DanglyJointsState(r.JointRest.Length);
            Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
            DanglyJoints.Step(r, s, In(Matrix4x4.Identity, Vector3.Zero, 1f / 60f), bones);
            for (int b = 1; b < r.BoneCount; b++)
            {
                Vector3 end = r.JointRest[r.BoneJoint[b]];
                worst = MathF.Max(worst, (Vector3.Transform(end, bones[b]) - end).Length());
            }
        }
        check.Expect(worst < 1e-3f, $"a leaning chain seeded on its rest pose draws that pose ({worst} off)");
    }

    /// <summary>Oracle: the rope's authored cage by hand, 1.5 x 0.1 x (1 - 112/255) = 0.0841176. Jerked sideways
    /// at 3 m/s and stopped, a worn rope's joints never leave it; the same rope placed in the world swings past.</summary>
    private static void Worn(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Rope());
        if (r == null) { check.Expect(false, "the rope rigs for the worn check"); return; }
        check.Expect(JerkedReach(r, worn: true) <= 0.0841176f + 1e-4f,
                     $"a worn rope stays inside its authored cage ({JerkedReach(r, worn: true)})");
        check.Expect(JerkedReach(r, worn: false) > 0.0841176f * 2f,
                     $"a placed rope swings past that cage ({JerkedReach(r, worn: false)})");
    }

    /// <summary>The furthest any joint strays from its rest place while the part moves 0.05 a frame along y for
    /// half a second, then stops for two.</summary>
    private static float JerkedReach(DanglyRig r, bool worn)
    {
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        float reach = 0f;
        for (int f = 0; f < 150; f++)
        {
            Matrix4x4 world = Matrix4x4.CreateTranslation(0f, 0.05f * Math.Min(f, 30), 0f);
            DanglyJoints.Step(r, s, new DanglyInputs(world, Vector3.Zero, Vector3.Zero, 0f, 0f, 1f / 60f, 1f, 0f,
                                                     1.5f, worn: worn), bones);
            for (int j = 0; j < r.JointRest.Length; j++)
                reach = MathF.Max(reach, (s.X[j] - Vector3.Transform(r.JointRest[j], world)).Length());
        }
        return reach;
    }

    /// <summary>Oracle: the force balance by hand. Air on the card's broadside (tensor diag(0, 1, 0)) pulls
    /// 0.4 * U^2 per second squared, the face turning by the bend, against w^2 = 56: U = sqrt(7) leans the tip
    /// 1.5 up by 0.05 cos^3(1.909 deg) = 0.0499168 along y. A lean that has stopped moving publishes nothing.</summary>
    private static void SteadyWind(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Card());
        if (r == null) { check.Expect(false, "the card rigs for the wind check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        DanglyInputs inp = In(Matrix4x4.Identity, new Vector3(0f, 2.6457513f, 0f), 1f / 60f);
        for (int f = 0; f < 1500; f++) DanglyJoints.Step(r, s, inp, bones);
        Vector3 off = s.X[1] - r.JointRest[1];
        check.Expect(DanglySelfCheck.Rel(off.Y, 0.0499168f, 0.02f),
                     $"a steady wind leans the card by K U^2 cos^3 / w^2 = 0.0499168 ({off.Y})");
        check.Expect(MathF.Abs(off.X) < 1e-4f, "a y wind on a card leans it along y alone");
        check.Expect(!DanglyJoints.Step(r, s, inp, bones),
                     "a card leaning still in a steady wind is settled: there is nothing new to publish");
    }

    /// <summary>Oracle: the bend limit, 40 degrees. A 30 m/s gale on the card asks for a lean several metres
    /// long, so the tip must stop on the cone -- not short of it, not past it -- and still be 1.5 from its root.</summary>
    private static void Bend(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Card());
        if (r == null) { check.Expect(false, "the card rigs for the bend check"); return; }
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        DanglyInputs inp = In(Matrix4x4.Identity, new Vector3(0f, 30f, 0f), 1f / 60f);
        for (int f = 0; f < 600; f++) DanglyJoints.Step(r, s, inp, bones);
        Vector3 arm = s.X[1] - s.X[0];
        float deg = MathF.Acos(Math.Clamp(arm.Z / arm.Length(), -1f, 1f)) * 180f / MathF.PI;
        check.Expect(deg <= 40.05f && deg >= 39.5f, $"a gale bends a card to its 40 degree limit and no further ({deg})");
    }

    /// <summary>Oracle: a hanging chain's balance by hand. Broadside air on a face turned by the swing gives
    /// K U^2 cos^2 = g sin, so K U^2 = g (U = sqrt(9.81 / 0.08) on the 0.04-wide cord) hangs the rope at
    /// sin = 0.618034 -- its tip, 2 down the chain, at (0, 1.236068, -1.572302). Still air hangs it at rest.</summary>
    private static void Pendulum(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Rope());
        if (r == null) { check.Expect(false, "the rope rigs for the pendulum check"); return; }
        int tip = r.JointRest.Length - 1;
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        for (int f = 0; f < 300; f++) DanglyJoints.Step(r, s, In(Matrix4x4.Identity, Vector3.Zero, 1f / 60f), bones);
        check.Expect(DanglySelfCheck.Near(s.X[tip], new Vector3(0f, 0f, -2f), 1e-4f),
                     "a chain in still air hangs straight down its authored line");

        DanglyInputs inp = In(Matrix4x4.Identity, new Vector3(0f, 11.073617f, 0f), 1f / 60f);
        for (int f = 0; f < 3000; f++) DanglyJoints.Step(r, s, inp, bones);
        check.Expect(DanglySelfCheck.Near(s.X[tip], new Vector3(0f, 1.236068f, -1.572302f), 0.02f),
                     $"a wind of K U^2 = g blows the rope out to sin 0.618034 ({s.X[tip]})");
    }

    /// <summary>Oracle: work and energy. Steady air pushes the vine strip only along its face normal, so nothing
    /// drives it along its own width (0.23, -0.34, 0), and a passive part in steady air comes to rest: 15 s into
    /// a 3.5 m/s wind 4 degrees off edge-on, its tip is within 5 cm of that line and slower than 5 cm/s.</summary>
    private static void Strip(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Strip());
        if (r == null) { check.Expect(false, "the vine strip rigs for the flutter check"); return; }
        check.Expect(r.Class == DanglyClass.Swing && r.Columns == 1, "the vine strip is a one-column Swing");
        int tip = r.JointRest.Length - 1;
        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        Vector3 width = Vector3.Normalize(new Vector3(0.23f, -0.34f, 0f));
        Matrix4x4 world = Matrix4x4.CreateTranslation(10f, 10f, 5f);
        DanglyInputs inp = In(world, new Vector3(-1.75f, 3.0310889f, 0f), 1f / 60f);
        float side = 0f, speed = 0f;
        for (int f = 0; f < 1200; f++)
        {
            DanglyJoints.Step(r, s, inp, bones);
            if (f < 900) continue;
            Vector3 off = s.X[tip] - Vector3.Transform(r.JointRest[tip], world);
            side = MathF.Max(side, MathF.Abs(Vector3.Dot(off, width)));
            speed = MathF.Max(speed, s.V[tip].Length());
        }
        check.Expect(side < 0.05f, $"steady air does not swing a strip along its own width ({side})");
        check.Expect(speed < 0.05f, $"a strip in steady air comes to rest ({speed} m/s)");
    }

    /// <summary>Oracle: the field's own definition. With the field off the air IS the mean; on, its gusts and
    /// eddies are sines, so a minute's average comes back to the mean, and the gusts (depth 0.6) carry the
    /// downwind speed well past 40% either side of it.</summary>
    private static void Air(Checker check)
    {
        var mean = new Vector3(2f, 2f, 0f);
        check.Expect(DanglySelfCheck.Near(DanglyAir.At(new Vector3(3f, -7f, 1f), 12.5f, mean, 0f), mean, 1e-5f),
                     "with the field off the air is the steady mean");
        check.Expect(DanglyAir.At(new Vector3(3f, -7f, 1f), 12.5f, Vector3.Zero, 1f) == Vector3.Zero,
                     "no mean wind, no air");
        Vector3 sum = Vector3.Zero;
        float lo = float.MaxValue, hi = 0f, s = mean.Length();
        Vector3 down = mean / s;
        const int N = 36000;
        for (int i = 0; i < N; i++)
        {
            Vector3 a = DanglyAir.At(new Vector3(3f, -7f, 1f), i / 60f, mean, 1f);
            sum += a;
            float d = Vector3.Dot(a, down);
            lo = MathF.Min(lo, d); hi = MathF.Max(hi, d);
        }
        Vector3 avg = sum / N;
        check.Expect((avg - mean).Length() < 0.1f * s, $"the gusting air averages to its mean ({avg})");
        check.Expect(lo < 0.6f * s && hi > 1.4f * s, $"the gusts swing the speed past 40% either side ({lo}..{hi})");
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

    /// <summary>Oracle: the rope's own authored geometry -- 2 long in six segments, so every segment is
    /// 0.333333 long and stays so however hard the part is yanked.</summary>
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
        for (int j = 1; j < r.JointRest.Length; j++)
            kept &= DanglySelfCheck.Near((s.X[j] - s.X[j - 1]).Length(), 0.333333f, 1e-3f);
        check.Expect(kept, "a yanked Swing chain still measures 0.333333 a segment");
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
        var wind = new Vector3(0f, 5f, 0f);
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

    /// <summary>Oracle: hand-placed joints, no time to integrate. A Jiggle joint at rest + (0.05, -0.02, 0.03)
    /// gives that literal translation. The card's tip swung 0.3 rad about x, 1.5 * (0, sin, cos) = (0, 0.4432803,
    /// 1.4330047), and the rope's first joint 0.6 rad about y, (sin, 0, -cos) / 3 = (0.1882142, 0, -0.2751119),
    /// are each reached by a pure rotation about the root.</summary>
    private static void BoneMapping(Checker check)
    {
        Matrix4x4 world = Matrix4x4.CreateRotationX(MathF.PI / 2f)
                        * Matrix4x4.CreateTranslation(new Vector3(3f, -2f, 1f));
        var offset = new Vector3(0.05f, -0.02f, 0.03f);
        DanglyRig? blob = DanglySelfCheck.Rig(DanglySelfCheck.Blob());
        if (blob == null) { check.Expect(false, "the blob rigs for the bone check"); return; }
        Matrix4x4 b = Placed(blob, world, 1, blob.JointRest[1] + offset);
        check.Expect(DanglySelfCheck.Near(Vector3.Transform(blob.JointRest[1], b), blob.JointRest[1] + offset, 1e-4f),
                     "a Jiggle bone carries the rest tip onto the joint");
        check.Expect(DanglySelfCheck.Near(Vector3.Transform(Vector3.Zero, b), offset, 1e-4f),
                     "...as a pure translation: every vertex it reaches moves by the same offset");

        DanglyRig? card = DanglySelfCheck.Rig(DanglySelfCheck.Card());
        DanglyRig? rope = DanglySelfCheck.Rig(DanglySelfCheck.Rope());
        if (card == null || rope == null) { check.Expect(false, "the card and the rope rig for the bone check"); return; }
        Turned(check, card, world, new Vector3(0f, 0.4432803f, 1.4330047f), "a tall Sway");
        Turned(check, rope, world, new Vector3(0.1882142f, 0f, -0.2751119f), "a Swing");
    }

    /// <summary>The checks a turning bone owes: joint 1 placed at <paramref name="swung"/>, its bone carries the
    /// rest joint there, keeps the root put, and is a pure rotation.</summary>
    private static void Turned(Checker check, DanglyRig r, Matrix4x4 world, Vector3 swung, string what)
    {
        Matrix4x4 m = Placed(r, world, 1, swung);
        check.Expect(DanglySelfCheck.Near(Vector3.Transform(r.JointRest[1], m), swung, 1e-4f),
                     $"{what} bone turns its rest joint onto the swung one");
        check.Expect(DanglySelfCheck.Near(Vector3.Transform(r.JointRest[0], m), r.JointRest[0], 1e-4f),
                     $"...about {what}'s root, which stays put");
        Matrix4x4 rr = m * Matrix4x4.Transpose(m);
        check.Expect(MathF.Abs(rr.M11 - 1f) + MathF.Abs(rr.M22 - 1f) + MathF.Abs(rr.M33 - 1f)
                     + MathF.Abs(rr.M12) + MathF.Abs(rr.M13) + MathF.Abs(rr.M23) < 1e-4f,
                     $"...by a pure rotation (R Rt = I): no scale or shear reaches {what}'s normals");
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

    /// <summary>Oracle: the settled threshold, 1e-4. A wind whose square grows by a hair each frame moves the
    /// card's lean by about 1e-6 a frame, yet the published bones must never trail the solved joint by more.</summary>
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
            var wind = new Vector3(0f, MathF.Sqrt(1.4e-4f * f), 0f);
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
