using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The far rain golden (approved-spec, E1-E3): the shipped <c>CxWxNearKeep</c>, <c>CxVfRain</c> and <c>CxVfDensity</c>
/// from 0 to 40 m, open and covered, with the haze stubbed at 0.37. Oracle: near drops plus veil summing to one whole
/// rain at every distance, nothing kept at 25 m, a roof thinning the veil, and the stub's 0.37, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The distances the far rain golden walks: every half metre out to 40 m.</summary>
    private const int FarRainSteps = 81;

    /// <summary>The haze the stub returns: what a froxel holds today with no rain.</summary>
    private const float FarRainHaze = 0.37f;

    /// <summary>The stage that runs the far rain functions, or a null pipeline when the library lacks one.</summary>
    private static PipelineId FarRainStage(VkBackend vk, Checker check, string what, string main)
    {
        string lib = ShaderSource.Get("cx_vfrain.glsl");
        string keep = Function(lib, "float CxWxNearKeep("), veil = Function(lib, "float CxVfRain(");
        string density = Function(lib, "float CxVfDensity("), scatter = Function(lib, "vec3 CxVfScatter(");
        bool have = keep.Length > 0 && veil.Length > 0 && density.Length > 0 && scatter.Length > 0;
        check.Expect(have, $"{what}: cx_vfrain declares CxWxNearKeep, CxVfRain, CxVfDensity and CxVfScatter");
        if (!have) return default;
        string haze = FormattableString.Invariant($"float CxVfHaze(vec3 p) {{ return {FarRainHaze:R}; }}\n");
        return vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 4) + haze + keep + veil + density + scatter + main,
                                "golden " + what);
    }

    private static void FarRainApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        PipelineId pipe = FarRainStage(vk, check, "far rain", """
            void main()
            {
                float d = uIn[gl_WorkGroupID.x * uint(CX_GOLD_IN)];
                uint o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                uOut[o] = CxWxNearKeep(d) + CxVfRain(d, 1.0);
                uOut[o + 1u] = CxWxNearKeep(d);
                uOut[o + 2u] = CxVfRain(d, 0.0);
                uOut[o + 3u] = CxVfDensity(vec3(d, 0.0, 1.0), d, 1.0, 0.0, 1.0);
            }
            """);
        if (pipe.IsNull) return;
        var cases = new float[FarRainSteps];
        for (int i = 0; i < cases.Length; i++) cases[i] = i * 0.5f;
        float[]? got = RunCases(vk, log, pipe, FarRainSteps, cases, FarRainSteps * 4, "golden far rain");
        vk.Destroy(pipe);
        check.Expect(got != null, "far rain: the stage was refused (logged above)");
        if (got == null) return;
        float worst = 0f, worstAt = 0f;
        for (int i = 0; i < FarRainSteps; i++)
            if (MathF.Abs(got[i * 4] - 1f) > worst) { worst = MathF.Abs(got[i * 4] - 1f); worstAt = cases[i]; }
        int at20 = 40, at25 = 50;
        float open20 = got[at20 * 4] - got[at20 * 4 + 1];
        log.Info(FormattableString.Invariant($"GpuGolden: far rain, sum off one by {worst:F3} at worst ({worstAt:F1} m), kept {got[at25 * 4 + 1]:F3} at 25 m"));

        // Given rain with volumetric fog on
        // When the near drops thin out towards 25 m
        // Then the far rain veil in the fog grows over the same distance, so their sum has no step and no box edge shows at the farthest zoom
        check.Expect(worst < 0.02f && got[at25 * 4 + 1] == 0f,
            $"far rain E1: near drops plus veil miss one whole rain by {worst:F3} at {worstAt:F1} m (under 0.02), and keep {got[at25 * 4 + 1]:F3} at 25 m (want 0)");

        // Given rain, and two froxels at the same distance, one under a roof on the cover map and one under open sky
        // When the fog density is built
        // Then the froxel under the roof holds less rain than the open one
        check.Expect(got[at20 * 4 + 2] < open20,
            $"far rain E2: at 20 m the covered froxel holds {got[at20 * 4 + 2]:F3}, the open one {open20:F3}");

        // Given no rain
        // When the fog density is built
        // Then every froxel holds exactly what it holds today
        bool same = true;
        for (int i = 0; i < FarRainSteps; i++) same &= got[i * 4 + 3] == FarRainHaze;
        check.Expect(same, $"far rain E3: with no rain every froxel must hold the haze's {FarRainHaze} and nothing more");
    }
}
