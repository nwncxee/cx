using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The reactive mask under blended geometry: the shipped <c>CxReactiveAlpha</c> over the coverage the OIT resolve
/// reads off its revealage. Oracle: the alpha itself, capped at 0.9, written by hand.
/// </summary>
internal static partial class GpuGolden
{
    private static void BlendedReactiveSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string fn = Function(ShaderSource.Get("cx_reactive.glsl"), "float CxReactiveAlpha(");
        check.Expect(fn.Length > 0, "cx_reactive declares CxReactiveAlpha");
        if (fn.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 1) + fn + """
            void main()
            {
                uOut[gl_WorkGroupID.x] = CxReactiveAlpha(uIn[gl_WorkGroupID.x]);
            }
            """, "golden blended reactive");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 4, [0f, 0.25f, 0.6f, 1f], 4, "golden blended reactive");
        vk.Destroy(pipe);
        check.Expect(got != null, "the blended reactive stage was refused (logged above)");
        if (got == null) return;
        check.Expect(got[0] == 0f, FormattableString.Invariant($"a pixel no blended part covers stays unreactive ({got[0]:F3})"));
        check.Expect(MathF.Abs(got[1] - 0.25f) < 1e-6f, FormattableString.Invariant($"a pane a quarter opaque is a quarter reactive ({got[1]:F3})"));
        check.Expect(MathF.Abs(got[2] - 0.6f) < 1e-6f, FormattableString.Invariant($"a fading wall at 0,6 is 0,6 reactive ({got[2]:F3})"));
        check.Expect(MathF.Abs(got[3] - 0.9f) < 1e-6f, FormattableString.Invariant($"a fully opaque blended pixel stops at 0,9 ({got[3]:F3})"));
    }
}
