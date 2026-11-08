using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// Regression checks beside the FSR sky golden: E16, which held before FSR's sky coverage, open sky through FSR 3.1
/// keeping the shade SMAA gives it, and the same over a coverage history of NaN. Oracle: the same scene supersampled
/// 4x4 through the resolve and averaged.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>One 8-bit level: what open sky may differ from the reference by.</summary>
    private const float SkyOpenTol = 1f;

    private static void FsrSkyRegressionSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        SkyRun? r = SkyRunScene(vk, check, log, fog: false);
        check.Expect(r != null, "FSR sky: the golden's run was refused (logged above)");
        if (r == null) return;
        (_, float worst, _, _) = SkyAgainstTruth(r, SkyInOpen);
        // Given FSR 3.1 at render scale 0.9
        // When a stretch of sky has nothing in front of it
        // Then that sky is the shade SMAA draws it
        check.Expect(worst <= SkyOpenTol, $"E16: open sky differs from the reference by up to {worst:F1} levels (at most {SkyOpenTol})");

        // a coverage image no frame has written yet can hold anything, NaN included; a fresh start must not read it
        SkyRun? g = SkyRunScene(vk, check, log, fog: false, seed: Half.NaN);
        check.Expect(g != null, "FSR sky: the run over a garbage history was refused (logged above)");
        if (g == null) return;
        (_, float gWorst, _, _) = SkyAgainstTruth(g, SkyInOpen);
        check.Expect(gWorst <= SkyOpenTol,
            $"FSR sky: over a coverage history of NaN, open sky differs from the reference by up to {gWorst:F1} levels (at most {SkyOpenTol})");
    }
}
