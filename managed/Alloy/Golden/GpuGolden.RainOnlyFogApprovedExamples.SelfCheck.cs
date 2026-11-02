using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The rain-only fog golden (approved-spec, E1-E2): <c>CxVfDensity</c> and <c>CxVfScatter</c> with the haze off against
/// on, and whether the froxel pass runs for rain and the fog knob. Oracle: the stubbed haze of 0.37 subtracted by hand,
/// a sun of 0.2 and a lamp of 5, and the two run decisions, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>1 when the froxel pass runs with <paramref name="raining"/> and the fog knob <paramref name="vfogOn"/>,
    /// 0 when it does not; -1 until the choice is wired.</summary>
    static partial void FogPassRuns(bool raining, bool vfogOn, ref int runs);

    private static void RainOnlyFogApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        PipelineId pipe = FarRainStage(vk, check, "rain-only fog", """
            void main()
            {
                float d = uIn[gl_WorkGroupID.x * uint(CX_GOLD_IN)];
                uint o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                uOut[o] = CxVfDensity(vec3(d, 0.0, 1.0), d, 1.0, 1.0, 0.0);
                uOut[o + 1u] = CxVfDensity(vec3(d, 0.0, 1.0), d, 1.0, 1.0, 1.0);
                vec3 s = CxVfScatter(vec3(0.2), vec3(5.0), 0.0);
                uOut[o + 2u] = max(s.r, max(s.g, s.b));
                uOut[o + 3u] = min(s.r, min(s.g, s.b));
            }
            """);
        if (!pipe.IsNull)
        {
            float[] cases = [10f, 30f];
            float[]? got = RunCases(vk, log, pipe, 2, cases, 8, "golden rain-only fog");
            vk.Destroy(pipe);
            check.Expect(got != null, "rain-only fog: the stage was refused (logged above)");
            if (got != null)
            {
                log.Info(FormattableString.Invariant($"GpuGolden: rain-only fog, 30 m {got[4]:F3} haze off against {got[5]:F3} on, scatter {got[2]:F3}..{got[3]:F3}"));

                // Given rain and volumetric fog off
                // When the frame draws
                // Then the far rain veil is the same as it is with fog on, with none of the fog's own haze and no lamp glow
                bool veil = MathF.Abs(got[0] - (got[1] - FarRainHaze)) < 1e-6f && MathF.Abs(got[4] - (got[5] - FarRainHaze)) < 1e-6f && got[4] > 0f;
                check.Expect(veil && MathF.Abs(got[2] - 0.2f) < 1e-6f && MathF.Abs(got[3] - 0.2f) < 1e-6f,
                    $"rain-only fog E1: at 30 m the veil holds {got[4]:F3} against {got[5]:F3} minus the haze, and the light is {got[2]:F3}..{got[3]:F3} (the sun's 0.2, no lamp)");
            }
        }

        int dry = -1, wet = -1;
        FogPassRuns(false, false, ref dry);
        FogPassRuns(true, false, ref wet);

        // Given no rain and volumetric fog off
        // When the frame draws
        // Then no froxel pass runs
        check.Expect(dry == 0 && wet == 1,
            $"rain-only fog E2: with the fog off the froxel pass runs {dry} with no rain (want 0) and {wet} in rain (want 1)");
    }
}
