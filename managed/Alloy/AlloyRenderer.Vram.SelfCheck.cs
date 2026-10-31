using System;
using CX.Framework;

namespace CX.Alloy;

public sealed partial class AlloyRenderer
{
    /// <summary>Holds that a device exactly at its budget is not over it; turns red when the comparison takes the
    /// budget itself as an overrun. Oracle: hand-written figures.</summary>
    internal static void VramBudgetSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VramBudgetSelfCheck");
        const long MiB = 1 << 20;
        string at = VramLine(1000 * MiB, 8000 * MiB, 8000 * MiB, []);
        check.Expect(!at.Contains("OVER BUDGET", StringComparison.Ordinal), $"8000MiB used of an 8000MiB budget says OVER BUDGET: \"{at}\"");
        log.Info(check.Ok ? "AlloyRenderer.VramBudgetSelfCheck: passed" : "AlloyRenderer.VramBudgetSelfCheck: FAILED");
    }
}
