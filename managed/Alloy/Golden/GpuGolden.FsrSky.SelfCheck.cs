using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// Regression checks beside the FSR sky golden: E16, which held before FSR's sky coverage, open sky through FSR 3.1
/// keeping the shade SMAA gives it, and the same over a coverage history of NaN; E19, which held when written. Oracle: the same scene supersampled
/// 4x4 through the resolve and averaged, itself pinned to the sky's colour encoded by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>One 8-bit level: what open sky may differ from the reference by.</summary>
    private const float SkyOpenTol = 1f;

    /// <summary>The fixture's sky, 0.20 0.22 0.17, at gamma 2.2 in 8-bit levels: what raw open sky must come out as.</summary>
    /// <summary>A slow turn: the scene slides this many display pixels a frame.</summary>
    private const float SkyTurn = 0.37f;

    /// <summary>How far either side of a turning rope a dark trail is looked for, in display pixels.</summary>
    private const float SkyTrail = 4f;

    /// <summary>A pixel of open sky, row 90 and column 175 of the display.</summary>
    private const int SkyOpenAt = 90 * SkyDw + 175;

    private static readonly System.Numerics.Vector3 SkyOpenLevels = new(122.7f, 128.1f, 113.9f);

    private static void FsrSkyRegressionSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        SkyRun? r = SkyRunScene(vk, check, log, fog: false, dome: true);
        check.Expect(r != null, "FSR sky: the golden's run was refused (logged above)");
        if (r == null) return;
        (_, float worst, _, _) = SkyAgainstTruth(r, SkyInOpen);
        System.Numerics.Vector3 open = r.Truth[SkyOpenAt];
        check.Expect(SkyMax(open - SkyOpenLevels) <= SkyOpenTol,
            $"FSR sky: the reference's open sky is {open} levels, not the sky's own {SkyOpenLevels}");
        // Given FSR 3.1 at render scale 0.9
        // When a stretch of sky has nothing in front of it
        // Then that sky is the shade SMAA draws it
        check.Expect(worst <= SkyOpenTol, $"E16: open sky differs from the reference by up to {worst:F1} levels (at most {SkyOpenTol})");

        // a coverage image no frame has written yet can hold anything, NaN included; a fresh start must not read it
        SkyRun? g = SkyRunScene(vk, check, log, fog: false, dome: true, seed: Half.NaN);
        check.Expect(g != null, "FSR sky: the run over a garbage history was refused (logged above)");
        if (g == null) return;
        (_, float gWorst, _, _) = SkyAgainstTruth(g, SkyInOpen);
        check.Expect(gWorst <= SkyOpenTol,
            $"FSR sky: over a coverage history of NaN, open sky differs from the reference by up to {gWorst:F1} levels (at most {SkyOpenTol})");

        SkyRun? t = SkyRunScene(vk, check, log, fog: true, dome: false, pan: SkyTurn);
        check.Expect(t != null, "FSR sky: the turning run was refused (logged above)");
        if (t == null) return;
        const float moved = (SkyFrames - 1) * SkyTurn;
        Func<int, int, bool> trail = (x, y) => y >= 25 && y <= 155 && SkyRopeDist(x + 0.5f + moved, y + 0.5f) <= SkyTrail;
        (float tMean, float tWorst, _, _) = SkyAgainstTruth(t, trail);
        float tDarker = SkyWorstDarker(t, trail);
        log.Info($"GpuGolden: FSR sky turning, rope and trail {tMean:F2} levels from the reference, worst {tWorst:F1}, at most {tDarker:F1} darker");
        // Given FSR 3.1 at render scale 0.9
        // When the camera turns slowly past ship rigging against the sky
        // Then the rigging keeps its shade, with no dark trail behind it
        check.Expect(MathF.Abs(tMean) <= SkyShadeMean && tDarker <= SkyShadeDark,
            $"E19: turning, the rope and the sky beside it sit {tMean:F2} levels from the reference (at most {SkyShadeMean}), a pixel up to {tDarker:F1} darker (at most {SkyShadeDark})");
    }
}
