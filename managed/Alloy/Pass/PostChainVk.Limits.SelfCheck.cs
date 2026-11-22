using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class PostChainVk
{
    /// <summary>The bloom chain's sizes and the uniform buffer's end, at their edges, with no device. Oracle: the
    /// halvings worked by hand and the 32 KiB buffer written as a number.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void LimitsSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "PostChainVk.LimitsSelfCheck");
        check.Expect(BloomLevels(4096, 4096) == 6, $"MaxLevels: a 4096 square stops at six bloom levels, not {BloomLevels(4096, 4096)}");
        check.Expect(BloomLevels(64, 64) == 3 && BloomLevels(8, 8) == 1, "a level needs 8 texels a side, and a scene gets one level at least");
        check.Expect(Level(1, 0) == 1 && Level(4, 2) == 1 && Level(64, 1) == 16, "one texel: a level never shrinks below one texel");
        check.Expect(!UboOver(32768 - 16, 16) && UboOver(32768 - 16, 17), "UboBytes: a block may end at 32 KiB and not one byte past");
        log.Info(check.Ok ? "PostChainVk.LimitsSelfCheck: passed" : "PostChainVk.LimitsSelfCheck: FAILED");
    }
}
