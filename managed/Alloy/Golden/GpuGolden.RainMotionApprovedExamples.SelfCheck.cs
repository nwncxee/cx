using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The rain motion golden (approved-spec, E1, E2 and E4): the shipped <c>CxWxVelocity</c> either side of its
/// <c>CX_WX_CORE</c> cut-off, and whether the frame records rain's motion for each anti-aliasing choice. Oracle: a drop
/// vector of (3, -7) and a ground vector of (-1, 0.5), and the AA choices that read motion, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>1 when the frame records rain's motion under <paramref name="fsr"/> and <paramref name="taa"/>, 0 when it
    /// does not; -1 until the choice is wired.</summary>
    static partial void RainMotionWanted(bool fsr, bool taa, ref int wanted);

    private static void RainMotionApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string lib = ShaderSource.Get("cx_wx_motion.glsl");
        string fn = Function(lib, "vec2 CxWxVelocity("), core = Declaration(lib, "CX_WX_CORE");
        bool have = fn.Length > 0 && core.Length > 0;
        check.Expect(have, "rain motion: cx_wx_motion declares CxWxVelocity and CX_WX_CORE");
        if (have)
        {
            PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 2) + core + fn + """
                void main()
                {
                    float cov = clamp(CX_WX_CORE + uIn[gl_WorkGroupID.x * uint(CX_GOLD_IN)], 0.0, 1.0);
                    vec2 v = CxWxVelocity(cov, vec2(3.0, -7.0), vec2(-1.0, 0.5));
                    uOut[gl_WorkGroupID.x * uint(CX_GOLD_OUT)] = v.x;
                    uOut[gl_WorkGroupID.x * uint(CX_GOLD_OUT) + 1u] = v.y;
                }
                """, "golden rain motion");
            float[] cases = [0.05f, -0.05f];
            float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 2, cases, 4, "golden rain motion");
            vk.Destroy(pipe);
            check.Expect(got != null, "rain motion: the stage was refused (logged above)");
            if (got != null)
            {
                log.Info(FormattableString.Invariant($"GpuGolden: rain motion, core ({got[0]:F2}, {got[1]:F2}), edge ({got[2]:F2}, {got[3]:F2})"));

                // Given a streak pixel whose coverage is above the core cut-off
                // When motion vectors are written
                // Then the pixel holds the drop's own fall motion
                check.Expect(got[0] == 3f && got[1] == -7f, $"rain motion E1: the core holds ({got[0]:F2}, {got[1]:F2}), the drop's is (3, -7)");

                // Given a streak pixel on its thin, faint edge, below the cut-off
                // When motion vectors are written
                // Then the ground's own vector stays in that pixel, so the ground does not smear along the rain
                check.Expect(got[2] == -1f && got[3] == 0.5f, $"rain motion E2: the edge holds ({got[2]:F2}, {got[3]:F2}), the ground's is (-1, 0.5)");
            }
        }

        int off = -1, fsr = -1, taa = -1;
        RainMotionWanted(false, false, ref off);
        RainMotionWanted(true, false, ref fsr);
        RainMotionWanted(false, true, ref taa);

        // Given anti-aliasing off or SMAA
        // When rain falls
        // Then the colour it draws is the same as before this change
        check.Expect(off == 0 && fsr == 1 && taa == 1,
            $"rain motion E4: rain's motion is recorded {off} with no temporal resolve (want 0), {fsr} under FSR and {taa} under TAA (want 1)");
    }
}
