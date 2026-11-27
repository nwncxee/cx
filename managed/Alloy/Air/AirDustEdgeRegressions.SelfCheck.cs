using System;
using System.Numerics;
using CX.Framework;
using static CX.Alloy.AirDustMarch;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>Where the air meets the end of the walkable floor, through the stage's march over the field the air uploads.
/// Oracle: what the live client showed, a line across the crypt's floor where the walkmesh ends, and black boxes by a
/// city's walls at night.</summary>
internal static class AirDustEdgeRegressions
{
    /// <summary>The crypt's light: a dim cold ambient and no sky.</summary>
    private static readonly Shine CryptLight = new(new Vector3(0.03f, 0.03f, 0.04f), Vector3.Zero);

    /// <summary>The floor the eye's rays land on per step across the edge, and the floor before it they cover, metres.</summary>
    private const float Stride = 0.1f, Approach = 2f;

    /// <summary>The most one stride may change what the eye keeps of the floor.</summary>
    private const float MostChange = 0.005f;

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirDustEdgeRegressions.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        FadesToTheEdge(check);
        NothingFromAWall(check);
        log.Info(check.Ok ? "AirDustEdgeRegressions.SelfCheck: passed" : "AirDustEdgeRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given crypt dust on a floor whose walkmesh ends at x = 4, unwalkable stone past it
    // When the eye 1.7 m up and 4 m back looks across the floor's last 2 m and over its edge
    // Then what it keeps of the floor changes little from one 0.1 m to the next: the dust thins out toward the edge, no
    // line shows where the walkmesh ends.
    private static void FadesToTheEdge(Checker check)
    {
        Enter(Crypt, Ground(40f, 1f, (_, _) => 0f, (x, _) => x < 4f ? Stone : Nonwalk), 0f, false);
        Go(3f, Vector2.Zero);
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        var f = new Field(Sent);
        Vector3 eye = new(0f, 0.5f, 1.7f);
        float worst = 0f, at = 0f, inland = Keep(f, eye, 4f - Approach), was = inland;
        for (float x = 4f - Approach + Stride; x <= 4f + Stride + 1e-3f; x += Stride)
        {
            float now = Keep(f, eye, x);
            if (MathF.Abs(now - was) > worst) (worst, at) = (MathF.Abs(now - was), x);
            was = now;
        }
        check.Expect(inland < 1f && worst <= MostChange,
            $"F2 the eye keeps {inland:P1} of the floor 2 m short of the edge, and one {Stride} m stride changes it by {worst:P2} at x {at:F1} (want some dust, {MostChange:P1} at most)");
    }

    // Given night mist over the water of a quay, a building's unwalkable base past x = 4
    // When the march reads a step's span of height over the water's last column, beside the building's columns
    // Then the mist there is a number, and some: the building, which has no floor, lays no air, and no pixel by its
    // wall turns black.
    private static void NothingFromAWall(Checker check)
    {
        Enter(City, Ground(40f, 1f, (_, _) => 0f, (x, _) => x < 0f ? Stone : x < 4f ? MistFixture.Water : Nonwalk), 0f, true);
        Go(Gather, Vector2.Zero);
        int gen = int.MinValue;
        GroundMist.Pack(ref gen, Sent);
        var f = new Field(Sent);
        (float dust, float mist) = f.At(new Vector3(3.9f, 0.5f, 0.1f), 0f, 0.1f, 0.05f);
        check.Expect(float.IsFinite(dust) && float.IsFinite(mist) && mist > 0f,
            $"F3 the air by the building's wall reads dust {dust}, mist {mist} a step's span over the water (want numbers, some mist)");
    }

    /// <summary>What the eye keeps of the floor at x = <paramref name="x"/> on its line of sight.</summary>
    private static float Keep(Field f, Vector3 eye, float x)
        => f.March(eye, new Vector3(x, eye.Y, 0f), CryptLight, ReadOnlySpan<Point>.Empty).Keep;
}
