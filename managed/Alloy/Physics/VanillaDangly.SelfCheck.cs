using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Physics;

/// <summary>The stock solve, step for step. Every expected number is one pass of the spec's formula worked
/// out by hand on a single vertex at <c>c = 100</c>, tightness 1, period 1, displacement 1 and a 1/60 s
/// step.</summary>
internal static class VanillaDanglyCheck
{
    private static readonly Vector3[] OneRest = { Vector3.Zero };
    private static readonly float[] OneCons = { 100f };

    internal static void Cases(Checker check)
    {
        OneStep(check);
        Clamp(check);
        Wind(check);
        Teleport(check);
        Pinned(check);
        Interpolated(check);
    }

    /// <summary>A fresh state seeded on the pose the test hands it, so the next call's pose delta is
    /// identity.</summary>
    private static VanillaDanglyState Seeded(int verts, Vector3 pos, Quaternion rot, Span<Vector3> dst,
                                             ReadOnlySpan<Vector3> rest, ReadOnlySpan<float> cons)
    {
        var s = new VanillaDanglyState(verts);
        VanillaDangly.Run(rest, cons, 1f, 1f, 1f, s, pos, rot, Vector3.Zero, 0f, dst);
        return s;
    }

    /// <summary>Oracle: one pass of the spec's formula by hand. From 0.1 off rest, the spring kick is
    /// -0.1 * 100 * 0.5 * 1 * (1/60) = -0.0833333 and the position lands at 0.1 - 0.0833333/60 =
    /// 0.0986111.</summary>
    private static void OneStep(Checker check)
    {
        Span<Vector3> dst = stackalloc Vector3[1];
        VanillaDanglyState s = Seeded(1, Vector3.Zero, Quaternion.Identity, dst, OneRest, OneCons);
        s.Cur[0] = new Vector3(0.1f, 0f, 0f);
        VanillaDangly.Run(OneRest, OneCons, 1f, 1f, 1f, s, Vector3.Zero, Quaternion.Identity,
                          Vector3.Zero, VanillaDangly.StepDt, dst);
        check.Expect(DanglySelfCheck.Near(s.Vel[0].X, -0.0833333f, 1e-5f),
                     "one step's spring kick on a vertex 0.1 off rest is -0.0833333");
        check.Expect(DanglySelfCheck.Near(s.Cur[0].X, 0.0986111f, 1e-5f),
                     "and it leaves the vertex at 0.0986111");
        check.Expect(MathF.Abs(s.Cur[0].Y) + MathF.Abs(s.Cur[0].Z) == 0f,
                     "a displacement along x stays along x");
    }

    /// <summary>Oracle: the authored excursion limit, <c>1 * (1 - 100/255) = 0.6078431</c>, which the stock
    /// solve applies per axis to the stored position.</summary>
    private static void Clamp(Checker check)
    {
        Span<Vector3> dst = stackalloc Vector3[1];
        VanillaDanglyState s = Seeded(1, Vector3.Zero, Quaternion.Identity, dst, OneRest, OneCons);
        s.Cur[0] = new Vector3(5f, 0f, 0f);
        VanillaDangly.Run(OneRest, OneCons, 1f, 1f, 1f, s, Vector3.Zero, Quaternion.Identity,
                          Vector3.Zero, VanillaDangly.StepDt, dst);
        check.Expect(DanglySelfCheck.Near(s.Cur[0].X, 0.6078431f, 1e-5f),
                     "a vertex five units out is clamped to its authored 0.6078431");
    }

    /// <summary>Oracle: the spec's drift term by hand -- <c>20 * 0.01 * 1 * (1 - 100/255) = 0.1215686</c> --
    /// taken into the stored position, and rotated into part space by the conjugate, so a quarter turn about
    /// z turns an x wind into a -y drift.</summary>
    private static void Wind(Checker check)
    {
        Span<Vector3> dst = stackalloc Vector3[1];
        var wind = new Vector3(0.01f, 0f, 0f);
        VanillaDanglyState s = Seeded(1, Vector3.Zero, Quaternion.Identity, dst, OneRest, OneCons);
        VanillaDangly.Run(OneRest, OneCons, 1f, 1f, 1f, s, Vector3.Zero, Quaternion.Identity,
                          wind, VanillaDangly.StepDt, dst);
        check.Expect(DanglySelfCheck.Near(s.Cur[0], new Vector3(0.1215686f, 0f, 0f), 1e-5f),
                     "wind drifts the stored position by 20 * wind * displacement * (1 - c/255)");

        Quaternion turn = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        VanillaDanglyState t = Seeded(1, Vector3.Zero, turn, dst, OneRest, OneCons);
        VanillaDangly.Run(OneRest, OneCons, 1f, 1f, 1f, t, Vector3.Zero, turn,
                          wind, VanillaDangly.StepDt, dst);
        check.Expect(DanglySelfCheck.Near(t.Cur[0], new Vector3(0f, -0.1215686f, 0f), 1e-5f),
                     "on a part turned a quarter about z the same wind drifts along -y");
    }

    /// <summary>Oracle: the rest positions and the velocity the test wrote. The stock solve copies rest over
    /// the dynamic positions on a teleport and leaves the velocity alone -- it does NOT clear it.</summary>
    private static void Teleport(Checker check)
    {
        Span<Vector3> dst = stackalloc Vector3[1];
        VanillaDanglyState s = Seeded(1, Vector3.Zero, Quaternion.Identity, dst, OneRest, OneCons);
        s.Cur[0] = new Vector3(0.2f, 0f, 0f);
        s.Vel[0] = new Vector3(1f, 2f, 3f);
        float moved = VanillaDangly.Run(OneRest, OneCons, 1f, 1f, 1f, s, new Vector3(2f, 0f, 0f),
                                        Quaternion.Identity, Vector3.Zero, VanillaDangly.StepDt, dst);
        check.Expect(float.IsPositiveInfinity(moved), "a teleport reports an unbounded move");
        check.Expect(s.Cur[0] == OneRest[0] && s.Prev[0] == OneRest[0],
                     "a teleport copies rest over both stepped states");
        check.Expect(s.Vel[0] == new Vector3(1f, 2f, 3f), "a teleport keeps the velocity, as the stock solve does");
        check.Expect(s.Acc == 0f, "a teleport empties the step accumulator");
    }

    /// <summary>Oracle: the pinned vertex's own rest position. The stock solve skips every vertex whose
    /// constraint is not above zero, so it is never written and never drawn anywhere else.</summary>
    private static void Pinned(Checker check)
    {
        Vector3[] rest = { new Vector3(0.5f, 0f, 0f), new Vector3(1f, 0f, 0f) };
        float[] cons = { 0f, 100f };
        Span<Vector3> dst = stackalloc Vector3[2];
        VanillaDanglyState s = Seeded(2, Vector3.Zero, Quaternion.Identity, dst, rest, cons);
        for (int f = 0; f < 4; f++)
            VanillaDangly.Run(rest, cons, 1f, 1f, 1f, s, Vector3.Zero, Quaternion.Identity,
                              new Vector3(0.01f, 0f, 0f), VanillaDangly.StepDt, dst);
        check.Expect(s.Cur[0] == rest[0] && dst[0] == rest[0], "a constraint-0 vertex never leaves rest");
        check.Expect(s.Cur[1].X != rest[1].X, "while its c = 100 neighbour drifts with the wind");
    }

    /// <summary>Oracle: the midpoint of the two hand-computed steps -- rest+0.1 before the step and 0.0986111
    /// after -- which is what half a step of accumulator must draw.</summary>
    private static void Interpolated(Checker check)
    {
        Span<Vector3> dst = stackalloc Vector3[1];
        VanillaDanglyState s = Seeded(1, Vector3.Zero, Quaternion.Identity, dst, OneRest, OneCons);
        s.Cur[0] = new Vector3(0.1f, 0f, 0f);
        VanillaDangly.Run(OneRest, OneCons, 1f, 1f, 1f, s, Vector3.Zero, Quaternion.Identity,
                          Vector3.Zero, VanillaDangly.StepDt * 1.5f, dst);
        check.Expect(DanglySelfCheck.Near(s.Acc, VanillaDangly.StepDt * 0.5f, 1e-6f),
                     "one and a half steps of time leaves half a step over");
        check.Expect(DanglySelfCheck.Near(dst[0].X, 0.0993056f, 1e-5f),
                     "half a step over draws halfway between the last two steps");
    }
}
