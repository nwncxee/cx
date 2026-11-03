using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The light softening's approved examples (approved-spec, E4..E8): the shipped <c>CxWrap</c> and
/// <c>CxSunLight</c> as compute stages. Oracle: <see cref="StockSoft"/>, <c>inc_lighting.shd</c>'s softened N.L
/// written out from the stock shader, and the literals the stock curve gives (0.43 at 60 degrees, 0.056 at 90).
/// </summary>
internal static partial class GpuGolden
{
    /// <summary><c>inc_lighting.shd</c>:324-347 at quality 2 with <c>LIGHT_SOFTENING_VALUE</c> 0.25: the share of a
    /// light a surface at <paramref name="ndl"/> takes; a normal-mapped one is gated at 0, any other at -0.25.</summary>
    private static double StockSoft(double ndl, bool normalMapped)
    {
        if (!(ndl > (normalMapped ? 0.0 : -0.25))) return 0.0;
        double t = Math.Clamp((ndl * (1.0 - 0.2) + 0.2) / 2.0, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t) * 2.0;
    }

    /// <summary>The cosine of <paramref name="deg"/> degrees: a surface that far off the light.</summary>
    private static float Off(double deg) => (float)Math.Cos(deg * Math.PI / 180.0);

    private static void SofteningApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        // (N.L, wrap, normal-mapped): 60, 90, 105 and 95 degrees off the light, the last with and without a normal map
        float[] cases = [Off(60), 0f, 0f, Off(90), 0f, 0f, Off(105), 0f, 0f, Off(95), 0f, 1f, Off(95), 0f, 0f];
        if (WrapCases(vk, check, log, cases) is not { } got) return;
        log.Info(FormattableString.Invariant($"GpuGolden: softening {got[0]:F4} at 60, {got[1]:F4} at 90, {got[2]:F4} at 105, {got[3]:F4}/{got[4]:F4} at 95"));

        // Given a smooth surface with no normal map, lit by one distant placed light, with Diffuse wrap at 0
        // When the surface faces 60 degrees off the light
        // Then it receives 0.43 of the head-on brightness, as stock does, not Lambert's 0.5
        check.Expect(Math.Abs(got[0] - StockSoft(Off(60), false)) < 1e-4 && Math.Abs(got[0] - 0.432f) < 1e-3f,
                     FormattableString.Invariant($"CX-448 E4: 60 degrees off the light takes {got[0]:F4}, stock 0.432"));

        // Given the same surface and light, with Diffuse wrap at 0
        // When the surface faces 90 degrees off the light, and then 105 degrees off it
        // Then it receives 0.056 at 90 degrees and nothing at 105, as stock does
        check.Expect(Math.Abs(got[1] - 0.056f) < 1e-3f && got[2] == 0f,
                     FormattableString.Invariant($"CX-448 E5: {got[1]:F4} at 90 degrees (stock 0.056) and {got[2]:F4} at 105 (stock 0)"));

        // Given a normal-mapped surface lit by one distant placed light, with Diffuse wrap at 0
        // When the surface faces 95 degrees off the light
        // Then it receives nothing, as stock does, while a surface with no normal map at the same angle still receives some
        check.Expect(got[3] == 0f && Math.Abs(got[4] - StockSoft(Off(95), false)) < 1e-4 && got[4] > 0f,
                     FormattableString.Invariant($"CX-448 E6: 95 degrees takes {got[3]:F4} normal-mapped (stock 0), {got[4]:F4} without (stock {StockSoft(Off(95), false):F4})"));

        SunSofteningApproved(vk, check, log);
    }

    /// <summary>E7 and E8: the shipped <c>CxSunLight</c> under a white sun at wrap 0, the surface 60 and 90 degrees
    /// off it. Oracle: <see cref="StockSoft"/> for both the diffuse and the highlight's light. E8 names a placed light;
    /// the walk's highlight rides the same softened dot by construction (<c>spc = dif * lobe</c>), the sun's is its own.</summary>
    private static void SunSofteningApproved(VkBackend vk, Checker check, ExtenderLog log)
    {
        string stage = SunStage(ShaderSource.Get("cx_lightfrag.glsl"));
        check.Expect(stage.Length > 0, "CX-448 E7: cx_lightfrag declares CxWrap, CxSunLight and CxTranslucent");
        if (stage.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 2) + stage + """
            void main()
            {
                uint i = gl_WorkGroupID.x;
                float c = uIn[i * uint(CX_GOLD_IN)];
                vec3 spec;
                uOut[i * uint(CX_GOLD_OUT)] = CxSunLight(vec3(sqrt(max(1.0 - c * c, 0.0)), 0.0, c), 1.0, 1.0, spec).g;
                uOut[i * uint(CX_GOLD_OUT) + 1u] = spec.g;
            }
            """, "golden sun softening");
        float[] cases = [Off(60), Off(90)];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 2, cases, 4, "golden sun softening");
        vk.Destroy(pipe);
        check.Expect(got != null, "CX-448 E7: the sun stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant($"GpuGolden: sun softening {got[0]:F4}/{got[1]:F4} at 60, {got[2]:F4}/{got[3]:F4} at 90"));

        // Given a smooth surface with no normal map under the sun, with Diffuse wrap at 0
        // When the surface faces 60 degrees off the sun
        // Then it receives 0.43 of the sun's head-on brightness, as stock does
        check.Expect(Math.Abs(got[0] - StockSoft(Off(60), false)) < 1e-4,
                     FormattableString.Invariant($"CX-448 E7: the sun at 60 degrees gives {got[0]:F4}, stock {StockSoft(Off(60), false):F4}"));

        // Given a glossy surface lit by one distant placed light, with Diffuse wrap at 0
        // When the light moves from 60 degrees off the surface to 90 degrees off it
        // Then the highlight dims by the same share the diffuse light does, as stock
        check.Expect(Math.Abs(got[1] - StockSoft(Off(60), false)) < 1e-4 && Math.Abs(got[3] - StockSoft(Off(90), false)) < 1e-4,
                     FormattableString.Invariant($"CX-448 E8: the highlight's light is {got[1]:F4} at 60 and {got[3]:F4} at 90; the diffuse's is {got[0]:F4} and {got[2]:F4}"));
    }
}
