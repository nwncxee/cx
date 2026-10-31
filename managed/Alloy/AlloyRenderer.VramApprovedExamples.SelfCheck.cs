using System;
using CX.Framework;

namespace CX.Alloy;

public sealed partial class AlloyRenderer
{
    /// <summary>Approved-spec: S1 and S2, the frame line's memory part. Oracles: hand-written device figures and
    /// allocation names, and the MiB they read as.</summary>
    internal static void VramSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AlloyRenderer.VramSelfCheck");
        const long MiB = 1 << 20;
        (string, long)[] top = [("arena Static vertices", 384 * MiB), ("vk oit accumulation", 56 * MiB), ("vk g-buffer 0", 56 * MiB)];
        string line = VramLine(3000 * MiB, 7000 * MiB, 8000 * MiB, top);
        check.Expect(line.Contains("3000MiB", StringComparison.Ordinal), $"S1: the memory part does not give the 3000MiB Alloy holds: \"{line}\"");
        check.Expect(line.Contains("arena Static vertices 384MiB", StringComparison.Ordinal) && line.Contains("vk oit accumulation 56MiB", StringComparison.Ordinal)
                     && line.Contains("vk g-buffer 0 56MiB", StringComparison.Ordinal), $"S1: the memory part does not name the three largest with their sizes: \"{line}\"");
        check.Expect(!line.Contains("OVER BUDGET", StringComparison.Ordinal), $"S2: under budget the line says OVER BUDGET: \"{line}\"");
        string over = VramLine(3000 * MiB, 9000 * MiB, 8000 * MiB, top);
        check.Expect(over.Contains("OVER BUDGET by 1000MiB", StringComparison.Ordinal), $"S2: 9000MiB used of an 8000MiB budget does not say OVER BUDGET by 1000MiB: \"{over}\"");
        check.Expect(over.Contains("arena Static vertices 384MiB", StringComparison.Ordinal) && over.Contains("vk g-buffer 0 56MiB", StringComparison.Ordinal),
                     $"S2: over budget the line does not name the three largest: \"{over}\"");
        string at = VramLine(1000 * MiB, 8000 * MiB, 8000 * MiB, top);
        check.Expect(!at.Contains("OVER BUDGET", StringComparison.Ordinal), $"S2: 8000MiB used of an 8000MiB budget says OVER BUDGET: \"{at}\"");
        string none = VramLine(0, 0, 0, []);
        check.Expect(none == "", $"S1: before the first frame, with no figure read off the device, the line has a memory part: \"{none}\"");
        log.Info(check.Ok ? "AlloyRenderer.VramSelfCheck: passed" : "AlloyRenderer.VramSelfCheck: FAILED");
    }
}
