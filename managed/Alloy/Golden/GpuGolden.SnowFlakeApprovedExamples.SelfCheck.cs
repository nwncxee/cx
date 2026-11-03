using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The snow flake golden (approved-spec, B6): the shipped <c>CxWxCover</c> for kind 2 against a soft disc. Oracle: full at the centre, half-way up its
/// ramp at three quarters out, nothing at the rim and past it, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    private static void SnowFlakeSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string cover = Function(ShaderSource.Get("cx_wx_shade.glsl"), "float CxWxCover(");
        check.Expect(cover.Length > 0, "snow flake: cx_wx_shade declares CxWxCover");
        if (cover.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 4) + cover + """
            void main()
            {
                uint o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                uOut[o] = CxWxCover(2, vec2(0.0), 1.0, 1.0);
                uOut[o + 1u] = CxWxCover(2, vec2(0.75, 0.0), 1.0, 1.0);
                uOut[o + 2u] = CxWxCover(2, vec2(0.0, 1.0), 1.0, 1.0);
                uOut[o + 3u] = CxWxCover(2, vec2(0.9, 0.9), 1.0, 1.0);
            }
            """, "golden snow flake");
        float[] cases = [0f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, cases, 4, "golden snow flake");
        vk.Destroy(pipe);
        check.Expect(got != null, "snow flake: the stage was refused (logged above)");
        if (got == null) return;

        // Given snow falling instead of rain
        // When it is drawn
        // Then each flake is the soft white disc it is today
        check.Expect(got[0] == 1f && MathF.Abs(got[1] - 0.5f) <= 0.01f && got[2] == 0f && got[3] == 0f,
            FormattableString.Invariant($"snow flake B6: a soft white disc, centre {got[0]:F3} (1), three quarters out {got[1]:F3} (0.5), rim {got[2]:F3} and corner {got[3]:F3} (0)"));
    }
}
