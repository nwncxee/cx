using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Physics;

/// <summary>The collision suite over the hanging banner fixture and hand-placed walls: what a gather keeps, and
/// that the drawn cloth stays in front of a wall the wind drives it into without the wall touching any other
/// motion. Every wall is a literal plane, so each verdict is a coordinate compared with that plane.</summary>
internal static class DanglyCollidersCheck
{
    /// <summary>Where the banner is placed, and the plane its cloth rests in (world y).</summary>
    private static readonly Vector3 At = new(10f, 10f, 5f);

    internal static void Cases(Checker check)
    {
        DanglyRig? r = DanglySelfCheck.Rig(DanglySelfCheck.Banner());
        if (r == null || r.Class != DanglyClass.Swing)
        {
            check.Expect(false, "the banner rigs as Swing for the collision checks");
            return;
        }
        Gathered(check, r);
        WallStops(check, r);
        NeverTouched(check, r);
        Behind(check, r);
        Sunk(check);
    }

    /// <summary>A 6 x 4 m wall in the plane <c>y = At.Y + <paramref name="y"/></c>, its front face toward
    /// <c>+y</c> when <paramref name="towardPlusY"/>; z from 3 m below the banner's pins to 1 m above.</summary>
    private static void Wall(DanglyGather g, float y, bool towardPlusY)
    {
        Vector3 p0 = At + new Vector3(-3f, y, -3f), p1 = At + new Vector3(3f, y, -3f),
                p2 = At + new Vector3(3f, y, 1f), p3 = At + new Vector3(-3f, y, 1f);
        if (towardPlusY) { g.Offer(p0, p2, p1); g.Offer(p0, p3, p2); }
        else { g.Offer(p0, p1, p2); g.Offer(p0, p2, p3); }
    }

    private static DanglyColliders Gather(DanglyRig r, Action<DanglyGather> offer)
    {
        var g = new DanglyGather();
        var c = new DanglyColliders();
        g.Begin(r, Matrix4x4.CreateTranslation(At), DanglySelfCheck.Banner().Nodes[0].Mesh!.Positions);
        offer(g);
        g.Finish(c);
        return c;
    }

    /// <summary>Oracle: hand-placed planes. The banner rests 3 cm in front of a wall's front face: those two faces
    /// are kept, and every joint below the pins may come to 1 cm of it (the vertices rest where the joints do). The
    /// same wall's back face, a face the cloth hangs behind, a floor 2 m below the pins (past the 1.5 m drop) and a
    /// face 20 m away are not.</summary>
    private static void Gathered(Checker check, DanglyRig r)
    {
        DanglyColliders c = Gather(r, g =>
        {
            Wall(g, -0.03f, true);
            Wall(g, -0.03f, false);
            Wall(g, 0.03f, true);
            g.Offer(At + new Vector3(-3f, -3f, -2f), At + new Vector3(3f, -3f, -2f), At + new Vector3(3f, 3f, -2f));
            g.Offer(At + new Vector3(20f, 0f, 0f), At + new Vector3(21f, 0f, 0f), At + new Vector3(20f, 1f, 0f));
        });
        check.Expect(c.Faces == 2, $"a gather keeps the two faces of the wall the banner rests in front of ({c.Faces})");
        bool clear = c.Pairs > 0;
        for (int k = 0; k < c.Pairs; k++) clear &= DanglySelfCheck.Near(c.PairClear[k], 0.01f, 1e-4f);
        check.Expect(clear, $"each joint may come to 1 cm of that wall ({c.Pairs} pair(s))");
        check.Expect(c.Count[0] == 0 && c.Count[r.Segments + 1] == 0, "a column's pinned root keeps no face");
    }

    /// <summary>Oracle: the wall plane, 3 cm behind the cloth. A gusting 6 m/s wind straight into it swings the
    /// banner a metre and more through it when nothing stops it; against the wall, no drawn vertex ever passes
    /// the plane, and none comes nearer it than 8 mm: the 1 cm skin, less what a turning segment carries a vertex
    /// past the joint the skin is kept at.</summary>
    private static void WallStops(Checker check, DanglyRig r)
    {
        DanglyColliders c = Gather(r, g => Wall(g, -0.03f, true));
        float free = Drawn(r, null, new Vector3(0f, -6f, 0f), out _);
        float held = Drawn(r, c, new Vector3(0f, -6f, 0f), out _);
        check.Expect(free < -0.5f, $"with no wall, the wind drives the banner through where it would be ({free})");
        check.Expect(held > -0.03f, $"against the wall, no drawn vertex passes it ({held})");
        check.Expect(held > -0.022f, $"...and none comes within 8 mm of it ({held})");
    }

    /// <summary>Oracle: a surface the part never reaches changes nothing. Blown away from the wall it rests in
    /// front of, the banner's motion is the same to the micrometre with the wall as without it.</summary>
    private static void NeverTouched(Checker check, DanglyRig r)
    {
        DanglyColliders c = Gather(r, g => Wall(g, -0.03f, true));
        float lo0 = Drawn(r, null, new Vector3(0f, 6f, 0f), out float hi0);
        float lo1 = Drawn(r, c, new Vector3(0f, 6f, 0f), out float hi1);
        check.Expect(MathF.Abs(hi1 - hi0) < 1e-6f && MathF.Abs(lo1 - lo0) < 1e-6f,
                     $"blown away from its wall, the banner swings exactly as it does with none ({hi0} / {hi1})");
        check.Expect(hi0 > 0.5f, $"...and it does swing out ({hi0})");
    }

    /// <summary>Oracle: a face is a wall only for what rests in front of it. The banner hangs 3 cm behind a face
    /// toward <c>+y</c> and 3 cm in front of one toward <c>-y</c>: neither is kept.</summary>
    private static void Behind(Checker check, DanglyRig r)
    {
        DanglyColliders c = Gather(r, g => { Wall(g, 0.03f, true); Wall(g, -0.03f, false); });
        check.Expect(c.Faces == 0, $"a face the cloth hangs behind is no wall for it ({c.Faces})");
    }

    /// <summary>Oracle: the vine strand's own coordinates against a hand-placed plane. Its chain hangs 6.5 cm in
    /// front of a wall at <c>x = -0.07</c> while its far edge (x = -0.12) is sunk 5 cm inside it: the wall still
    /// holds the chain, which keeps 1 cm off it and never crosses it in a gusting wind blown straight at it.</summary>
    private static void Sunk(Checker check)
    {
        ModelDef m = DanglySelfCheck.Strip();
        DanglyRig? r = DanglySelfCheck.Rig(m);
        if (r == null) { check.Expect(false, "the vine strip rigs for the sunk-edge check"); return; }
        Matrix4x4 world = Matrix4x4.CreateTranslation(At);
        float x = At.X - 0.07f;
        var g = new DanglyGather();
        var c = new DanglyColliders();
        g.Begin(r, world, m.Nodes[0].Mesh!.Positions);
        Vector3 a = new(x, At.Y - 3f, At.Z - 3f), b = new(x, At.Y + 3f, At.Z - 3f),
                e = new(x, At.Y + 3f, At.Z + 2f), d = new(x, At.Y - 3f, At.Z + 2f);
        g.Offer(a, b, e);
        g.Offer(a, e, d);
        g.Finish(c);
        bool clear = c.Pairs > 0;
        for (int k = 0; k < c.Pairs; k++) clear &= DanglySelfCheck.Near(c.PairClear[k], 0.01f, 1e-4f);
        check.Expect(c.Faces == 2 && clear,
                     $"a strand whose far edge is sunk in its wall still keeps 1 cm off it ({c.Faces}, {c.Pairs})");

        var s = new DanglyJointsState(r.JointRest.Length);
        Span<Matrix4x4> bones = stackalloc Matrix4x4[r.BoneCount];
        float lo = float.MaxValue;
        for (int f = 0; f < 600; f++)
        {
            var inp = new DanglyInputs(world, new Vector3(-6f, 0f, 0f), Vector3.Zero, f / 60f, 1f, 1f / 60f,
                                       1f, 1f, 1.5f, c);
            DanglyJoints.Step(r, s, inp, bones);
            for (int j = 0; j < r.JointRest.Length; j++) lo = MathF.Min(lo, s.X[j].X - x);
        }
        check.Expect(lo > 0f, $"...and its chain never crosses it in a wind blown at it ({lo})");
    }

    /// <summary>Ten seconds of gusting air at 60 fps, the cloth skinned as the vertex stage draws it: the lowest
    /// and highest world y (less <c>At.Y</c>) any drawn vertex reaches after the first half second.</summary>
    private static float Drawn(DanglyRig r, DanglyColliders? walls, Vector3 wind, out float highest)
    {
        Vector3[] rest = DanglySelfCheck.Banner().Nodes[0].Mesh!.Positions;
        SkinDef skin = r.Skin!;
        var s = new DanglyJointsState(r.JointRest.Length);
        var bones = new Matrix4x4[r.BoneCount];
        Matrix4x4 world = Matrix4x4.CreateTranslation(At);
        const float dt = 1f / 60f;
        float lo = float.MaxValue;
        highest = float.MinValue;
        for (int f = 0; f < 600; f++)
        {
            var inp = new DanglyInputs(world, wind, Vector3.Zero, f * dt, 1f, dt, 1f, 1f, 1.5f, walls);
            DanglyJoints.Step(r, s, inp, bones);
            if (f < 30) continue;
            for (int v = 0; v < rest.Length; v++)
            {
                Vector3 p = Vector3.Zero;
                Vector4 w = skin.Weights[v];
                for (int e = 0; e < 4; e++)
                {
                    float we = e == 0 ? w.X : e == 1 ? w.Y : e == 2 ? w.Z : w.W;
                    if (we > 0f) p += we * Vector3.Transform(rest[v], bones[skin.BoneIndices[v * 4 + e]]);
                }
                lo = MathF.Min(lo, p.Y);
                highest = MathF.Max(highest, p.Y);
            }
        }
        return lo;
    }
}
