using CX.Framework;

namespace CX.Services.Radial;

internal static partial class IconFrames
{
    /// <summary>A frame's known test, the cache's size and the palette side cap, at their edges. Oracle: 4,096
    /// frames and a 1,024-texel side, written by hand.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void LimitsSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "IconFrames.LimitsSelfCheck");
        check.Expect(new Frame(64, 64, 64, 64).Known && !new Frame(64, 0, 64, 64).Known, "a frame with no height is not known");
        var s = new IconFramesState();
        for (ulong k = 0; k < 4096; k++) Remember(s, k, default);
        int full = s.ByName.Count;
        Remember(s, 9999, default);
        check.Expect(full == 4096 && s.ByName.Count == 1, $"MaxCached: the cache holds 4,096 frames and the next starts it again ({full}, then {s.ByName.Count})");
        check.Expect(!TooBig(1024, 1024) && TooBig(1024, 1025) && TooBig(1025, 1), "a palette 1,025 texels tall or wide is too big");
        log.Info(check.Ok ? "IconFrames.LimitsSelfCheck: passed" : "IconFrames.LimitsSelfCheck: FAILED");
    }
}
