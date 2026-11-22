using CX.Framework;

namespace CX.Alloy;

public sealed partial class AlloyRenderer
{
    /// <summary>The never-cull radius and the dirty list's capacity at their edges. Oracle: the radius 1e30 and an
    /// eight-slot list, written by hand.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void LimitsSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.LimitsSelfCheck");
        check.Expect(Uncullable(1e30f) && !Uncullable(1e29f), "NeverCull: a radius of 1e30 is never culled and 1e29 is");
        check.Expect(!DirtyFull(6, 2, 8) && DirtyFull(6, 3, 8), "full: an eight-slot list takes two more after six, not three");
        log.Info(check.Ok ? "AlloyRenderer.LimitsSelfCheck: passed" : "AlloyRenderer.LimitsSelfCheck: FAILED");
    }
}
