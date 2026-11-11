using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The reactive mask golden (approved-spec, E1-E5): the shipped <c>CxReactive</c>, <c>CxReactiveShare</c> and
/// <c>CxTaaReactive</c> as a compute stage, and FSR fed a frame whose colour jumped, with and without a mask.
/// Oracle: four times the share capped at 0.9, the larger of the two weights, and FSR's own output with no mask,
/// all written by hand here.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>FSR's output at a pixel that turned from black to white this frame, after a black history, with a reactive
    /// mask of <paramref name="mask"/> over the frame; NaN until the reactive input reaches FSR.</summary>
    static partial void FsrReactiveRun(VkBackend vk, ExtenderLog log, float mask, ref float got);

    private static void ReactiveApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string src = ShaderSource.Get("cx_reactive.glsl");
        string share = Function(src, "float CxReactiveShare(");
        string fn = Function(src, "float CxReactive(");
        string taa = Function(ShaderSource.Get("cx_taa_f.glsl"), "float CxTaaReactive(");
        bool have = share.Length > 0 && fn.Length > 0 && taa.Length > 0;
        check.Expect(have, "reactive: cx_reactive declares CxReactive and CxReactiveShare, and cx_taa_f CxTaaReactive");
        if (have)
        {
            PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(3, 2) + share + fn + taa + """
                void main()
                {
                    uint i = gl_WorkGroupID.x * uint(CX_GOLD_IN);
                    float cover = CxReactiveShare(vec3(0.2, 0.3, 0.1), uIn[i], false);
                    uOut[gl_WorkGroupID.x * uint(CX_GOLD_OUT)] = CxReactive(cover);
                    uOut[gl_WorkGroupID.x * uint(CX_GOLD_OUT) + 1u] = CxTaaReactive(uIn[i + 1u], uIn[i + 2u]);
                }
                """, "golden reactive");
            float[] cases = [0.1f, 0.1f, 0.6f, 0.5f, 0f, 0f, 0f, 0.3f, 0f];
            float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 3, cases, 6, "golden reactive");
            vk.Destroy(pipe);
            check.Expect(got != null, "reactive: the stage was refused (logged above)");
            if (got != null)
            {
                // Given a pixel a beam, particle, halo or weather draw covered by a share of 0.1
                // When the reactive mask is written
                // Then the mask there is 0.4, four times the share
                Near(check, "reactive E1: a share of 0.1", got[0], 0.4, 1e-4);

                // Given a pixel those draws covered by a share of 0.5
                // When the reactive mask is written
                // Then the mask there is 0.9, its cap
                Near(check, "reactive E2: a share of 0.5", got[2], 0.9, 1e-4);

                // Given a pixel none of those passes drew on
                // When the reactive mask is written
                // Then the mask there is 0, and the image matches today's
                Near(check, "reactive E3: no share", got[4], 0.0, 1e-6);
                Near(check, "reactive E3: TAA's weight of 0.3 under a zero mask", got[5], 0.3, 1e-6);

                // Given TAA at render scale above 1, a pixel whose history weight is 0.1 and whose mask is 0.6
                // When TAA resolves the pixel
                // Then it uses a weight of 0.6
                Near(check, "reactive E4: TAA weight 0.1 under mask 0.6", got[1], 0.6, 1e-6);
            }
        }

        float bare = float.NaN, masked = float.NaN;
        FsrReactiveRun(vk, log, 0f, ref bare);
        FsrReactiveRun(vk, log, 0.9f, ref masked);
        log.Info(FormattableString.Invariant($"GpuGolden: reactive, FSR {bare:F3} with no mask, {masked:F3} under 0.9"));

        // Given FSR at render scale 1 or below
        // When the frame draws
        // Then FSR receives the mask as its reactive input
        check.Expect(!float.IsNaN(bare) && !float.IsNaN(masked) && masked > bare + 0.1f,
            $"reactive E5: FSR under a 0.9 mask gives {masked:F3}, which must sit above its {bare:F3} with none");
    }
}
