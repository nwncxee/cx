using CX.Framework;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The pass and layer limits at their edges, with no device. Oracle: GL's guaranteed eight colour
    /// attachments and a six-layer image, written by hand.</summary>
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static void LimitsSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkBackend.LimitsSelfCheck");
        check.Expect(!PassShapeBad(8, false) && PassShapeBad(9, false), "MaxColors: eight colour attachments pass and nine are refused");
        check.Expect(PassShapeBad(0, false) && !PassShapeBad(0, true), "a pass needs one attachment, and depth alone is one");
        check.Expect(!LayerOutside(5, 6) && LayerOutside(6, 6), "array layer: a six-layer image has layers 0 to 5");
        log.Info(check.Ok ? "VkBackend.LimitsSelfCheck: passed" : "VkBackend.LimitsSelfCheck: FAILED");
    }
}
