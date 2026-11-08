using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>
/// The FSR sky golden (approved-spec), E13-E15 and E17: a still rope and mast against the sky, in fog, through FSR 3.1 at
/// render scale 0.9 and the resolve. Oracle: an unjittered still frame changes by nothing, and the same scene supersampled
/// 4x4 through the resolve and averaged; the bounds are levels of the 8-bit frame, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The most a still pixel may change between frames: two 8-bit levels, under what an eye picks out.</summary>
    private const float SkyStill = 2f;

    /// <summary>How far the rope's mean shade may sit from the supersampled reference, and how much darker its darkest pixel may be.</summary>
    private const float SkyShadeMean = 3f, SkyShadeDark = 6f;

    /// <summary>How much darker than the reference's darkest pixel the mast's edge may draw.</summary>
    private const float SkyRim = 3f;

    static partial void FsrResolveApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        SkyRun? r = SkyRunScene(vk, check, log, fog: true, dome: true);
        check.Expect(r != null, "FSR sky: the golden's run was refused (logged above)");
        if (r == null) return;
        float rope = SkyWorstStep(r, SkyInRope), edge = SkyWorstStep(r, SkyInEdge);
        (float mean, _, float dark, float tDark) = SkyAgainstTruth(r, SkyInRope);
        (_, _, float eDark, float etDark) = SkyAgainstTruth(r, SkyInEdge);
        log.Info($"GpuGolden: FSR sky, rope steps {rope:F1} levels, mean {mean:F2} against the reference, darkest {dark:F0} against {tDark:F0}; "
               + $"edge steps {edge:F1}, darkest {eDark:F0} against {etDark:F0}");

        // Given FSR 3.1 at render scale 0.9 and a still camera
        // When ship rigging stands against the night sky in fog
        // Then the rigging keeps its shade from frame to frame, as still as under SMAA
        check.Expect(rope <= SkyStill, $"E13: a still rope changes by up to {rope:F1} levels between frames (at most {SkyStill})");

        // Given FSR 3.1 at render scale 0.9
        // When ship rigging stands against the night sky in fog
        // Then the rigging is the shade SMAA draws it, not darker and not broken into dots
        check.Expect(System.MathF.Abs(mean) <= SkyShadeMean && dark >= tDark - SkyShadeDark,
            $"E14: the rope sits {mean:F2} levels from the reference on average (at most {SkyShadeMean}), its darkest pixel "
          + $"{dark:F0} against the reference's {tDark:F0} (at most {SkyShadeDark} darker)");

        // Given FSR 3.1 at render scale 0.9 and a still camera
        // When a mast's edge stands against the sky
        // Then the edge has no dark rim that comes and goes from frame to frame
        check.Expect(edge <= SkyStill && eDark >= etDark - SkyRim,
            $"E15: the mast's edge changes by up to {edge:F1} levels between frames (at most {SkyStill}), its darkest pixel "
          + $"{eDark:F0} against the reference's {etDark:F0} (at most {SkyRim} darker)");

        SkyRun? f = SkyRunScene(vk, check, log, fog: true, dome: false, truth: false);
        check.Expect(f != null, "FSR sky: the fogged run was refused (logged above)");
        if (f == null) return;
        float fogRope = SkyWorstStep(f, SkyInRope);
        log.Info($"GpuGolden: FSR sky in the volumetric fog with no dome, rope steps {fogRope:F1} levels");

        // Given FSR 3.1 at render scale 0.9, the volumetric fog on and an area with no sky dome
        // When a thin far object stands against the fogged distance
        // Then it keeps its shade from frame to frame, as still as under SMAA
        check.Expect(fogRope <= SkyStill, $"E17: in the fog a still rope changes by up to {fogRope:F1} levels between frames (at most {SkyStill})");
    }
}
