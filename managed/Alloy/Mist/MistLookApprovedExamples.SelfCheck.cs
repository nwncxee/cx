using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Night mist on the quay parts where the player walks. Oracle, by hand: the walked path's strip and a strip
/// beside it, read through the air fixture's one density reader.</summary>
internal static class MistLookApprovedExamples
{
    [SelfCheckSuite(SuitePhase.BeforeServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MistLookApprovedExamples.SelfCheck");
        using var air = AirFixture.Switches.Force();
        VisibleTrail(check);
        log.Info(check.Ok ? "MistLookApprovedExamples.SelfCheck: passed" : "MistLookApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given night mist on the quay
    // When the player walks through it
    // Then it leaves a trail behind them.
    private static void VisibleTrail(Checker check)
    {
        AirFixture.Enter(AirFixture.City, AirFixture.Quay(), 10f, true);
        AirFixture.Go(300f, Vector2.Zero);
        var from = new Vector3(-4f, 0f, 0f);
        DbId world = MistPartingApprovedExamples.World(), me = MistPartingApprovedExamples.Seat(world, from, true);
        AirFixture.Still(world, AirFixture.Frames(0.1f));
        AirFixture.Walk(world, me, from, new Vector3(1f, 0f, 0f), true, AirFixture.Pace(5f));
        float path = AirFixture.Path(AirLayer.NightMist, -4f, 0f), beside = AirFixture.Beside(AirLayer.NightMist, -4f, 0f);
        MistPartingApprovedExamples.Drop(world, me);
        check.Expect(beside > 0f && beside - path >= 0.05f, $"E26 the walk leaves a trail {beside - path:F3} m deep ({path:F3} m on the path, {beside:F3} m beside it; want a trail)");
    }
}
