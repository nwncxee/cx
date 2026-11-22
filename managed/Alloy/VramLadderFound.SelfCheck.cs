using CX.Framework;
using Ladder = CX.Alloy.VramLadderApprovedExamples;

namespace CX.Alloy;

/// <summary>Found tests of the VRAM ladder: what its mutation pass showed the approved examples leave unasked.
/// Oracles: hand-written budgets and use, and the page counts and cube sizes they work out to by hand.</summary>
internal static class VramLadderFound
{
    /// <summary>Exactly 90% and exactly 80% of <see cref="Ladder.Budget"/>, MiB, worked by hand from its 8000.</summary>
    private const long AtGive = 7200, AtTake = 6400;

    /// <summary>Every found test on a freshly armed ladder at a 512/256 cube pair; the ladder is disarmed after.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VramLadderFound.SelfCheck");
        int cube0 = CasterPass.CubeFaceSize, dyn0 = CasterPass.CubeDynFaceSize;
        try
        {
            VramLadder.Reset(null, 512, 256);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(Ladder.Under, Ladder.Budget);
            bool whole = !VramLadder.HalfScratch;
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            check.Expect(whole && VramLadder.HalfScratch,
                         $"under 80% the scratch went whole ({whole}), and past 90% again it did not halve ({VramLadder.HalfScratch})");

            VramLadder.Reset(null, 512, 256);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.AreaChanged(Ladder.Past, Ladder.Budget, 960);
            check.Expect(VramLadder.Given == VramRung.Textures && VramLadder.PoolCap == -1,
                         $"an area change with only the textures given capped the pool: {VramLadder.Given}, {VramLadder.PoolCap} pages");

            VramLadder.Reset(null, 512, 256);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.AreaChanged(7500, Ladder.Budget, 960);
            VramLadder.AreaChanged(7300, Ladder.Budget, 960);
            check.Expect(VramLadder.PoolCap == 6, $"a second area change with room for 8 pages moved the pool's cap from 6 to {VramLadder.PoolCap}");

            VramLadder.Reset(null, 512, 256);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.AreaChanged(7900, Ladder.Budget, 0);
            check.Expect(VramLadder.PoolCap == 1, $"with no room past the pool the cap is {VramLadder.PoolCap} pages, not the 1 it floors at");

            VramLadder.Reset(null, 512, 256);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.AreaChanged(Ladder.Past, Ladder.Budget, 960);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            check.Expect(VramLadder.ShadowsLowered && VramLadder.CubeNamed == 512,
                         $"lowered ({VramLadder.ShadowsLowered}), the named cube size reads {VramLadder.CubeNamed}, not 512");
            VramLadder.Reset(null, 512, 256);
            check.Expect(CasterPass.CubeFaceSize == 512 && CasterPass.CubeDynFaceSize == 256,
                         $"re-armed while lowered, the cubes draw at {CasterPass.CubeFaceSize}/{CasterPass.CubeDynFaceSize}, not 512/256");

            // Given a freshly armed ladder
            // When a period's use is exactly 90% of the budget
            // Then nothing gives way: only use past 90% gives
            VramLadder.Reset(null, 512, 256);
            VramLadder.Period(AtGive, Ladder.Budget);
            check.Expect(VramLadder.Given == VramRung.None, $"use at exactly 90% gave the {VramLadder.Given} rung");

            // Given the scratch rung given and the scratch halved
            // When a period and then an area change see use at exactly 80% of the budget
            // Then the scratch stays half and the rung stays given: only use under 80% takes back
            VramLadder.Reset(null, 512, 256);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(AtTake, Ladder.Budget);
            bool half = VramLadder.HalfScratch;
            VramLadder.AreaChanged(AtTake, Ladder.Budget, 0);
            check.Expect(half && VramLadder.Given == VramRung.Scratch,
                         $"use at exactly 80% made the scratch whole ({!half}) or took the rung back ({VramLadder.Given})");

            int eased = VramLadder.Eased;
            VramLadder.Period(Ladder.Band, Ladder.Budget);
            VramLadder.Period(Ladder.Past, Ladder.Budget);
            VramLadder.Period(Ladder.Under, Ladder.Budget);
            check.Expect(VramLadder.Eased - eased == 2, $"a band period, a past one and an under one eased the ladder {VramLadder.Eased - eased} times, not 2");
        }
        finally
        {
            VramLadder.AreaChanged(0, Ladder.Budget, 0);
            VramLadder.Reset(null, cube0, dyn0);
            CasterPass.DrawCubesAt(cube0, dyn0);
        }
        log.Info(check.Ok ? "VramLadderFound.SelfCheck: passed" : "VramLadderFound.SelfCheck: FAILED");
    }
}
