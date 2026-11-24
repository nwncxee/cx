using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.MistFixture;
using static CX.Alloy.MistMotionApprovedExamples;
using static CX.Alloy.MistPartingApprovedExamples;

namespace CX.Alloy;

/// <summary>The mist's motion, plain regressions that held before it moved like air: a still player stirs nothing, a fireball
/// leaves far mist alone, and cleared ground fills from its edge. Oracle, by hand: area fog 10's floor and a fireball of 5 m.</summary>
internal static class MistMotionRegressions
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistMotionRegressions.SelfCheck");
        using var switches = MistFixture.Switches.Force();
        StandStill(check);
        FarMist(check);
        FromTheEdge(check);
        log.Info(check.Ok ? "MistMotionRegressions.SelfCheck: passed" : "MistMotionRegressions.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given the player standing still in the mist
    // When ten seconds pass
    // Then no eddy forms around them.
    private static void StandStill(Checker check)
    {
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        DbId world = World(), me = Seat(world, Vector3.Zero, true);
        float most = 0f;
        for (int n = 0; n < 10; n++)
        {
            Hold(world, 1f);
            most = MathF.Max(most, MaxCurl(-4f, -4f, 4f, 4f));
        }
        check.Expect(most < Eddy, $"E32 over 10 s standing still the strongest curl around the player is {most:F3}/s (want under {Eddy}/s)");
        Drop(world, me);
    }

    // Given ground mist 20 m from a fireball's point
    // When the fireball detonates
    // Then the mist there is unchanged over the next second.
    private static void FarMist(Checker check)
    {
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        float before = Near(20f, 0f, 1f);
        GroundMist.Blast(Vector3.Zero, Fireball);
        float most = 0f;
        for (int n = 0; n < 10; n++)
        {
            Run(0.1f, Vector2.Zero);
            most = MathF.Max(most, MathF.Abs(Near(20f, 0f, 1f) - before));
        }
        check.Expect(most <= before * 0.01f, $"E34 20 m out {before:F4} m lay before the blast and it moved by up to {most:F4} over the next second (want within 1%)");
    }

    // Given the cleared ground a fireball left
    // When the mist flows back
    // Then it pours in from the edge, so ground near the edge has mist again before the blast point does.
    private static void FromTheEdge(Checker check)
    {
        Fogged(Flat(), Vector3.Zero);
        Run(1f, Vector2.Zero);
        float floor = Near(30f, 0f, 1f);
        GroundMist.Blast(Vector3.Zero, Fireball);
        float edgeAt = -1f, pointAt = -1f;
        for (int n = 1; n <= 600 && pointAt < 0f; n++)
        {
            Run(1f / 60f, Vector2.Zero);
            if (edgeAt < 0f && Annulus(Fireball - 1.5f, Fireball - 0.5f) >= floor * 0.5f) edgeAt = n / 60f;
            if (pointAt < 0f && Annulus(0f, 1f) >= floor * 0.5f) pointAt = n / 60f;
        }
        check.Expect(edgeAt > 0f && (pointAt < 0f || edgeAt < pointAt),
            $"E37 the ground a metre inside the edge had half its mist back at {edgeAt:F2} s, the blast point at {pointAt:F2} s (want the edge first)");
    }
}
