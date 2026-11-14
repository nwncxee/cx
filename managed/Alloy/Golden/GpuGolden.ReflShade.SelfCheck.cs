using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The share of the probe reflection a shaded surface keeps, and the view angle its Fresnel takes. Oracle: the
/// ratios worked by hand below from Rec. 709 luminance, the rule being <c>inc_lighting.shd</c>:543-582's, where
/// the environment term is a share of the light arriving at the fragment (0.24298 of 1.23576 in the shaded case).
/// </summary>
internal static partial class GpuGolden
{
    private static void ReflShadeSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string lib = ShaderSource.Get("cx_ssr.glsl");
        string shade = Function(lib, "float CxReflShade("), nov = Function(lib, "float CxReflNoV(");
        check.Expect(shade.Length > 0 && nov.Length > 0, "refl shade: cx_ssr declares CxReflShade and CxReflNoV");
        if (shade.Length == 0 || nov.Length == 0) return;

        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + shade + nov + CaseBlocks(8, 2) + """
            void main()
            {
                int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                uOut[o] = CxReflShade(vec3(uIn[i], uIn[i + 1], uIn[i + 2]), vec3(uIn[i + 3], uIn[i + 4], uIn[i + 5]));
                uOut[o + 1] = CxReflNoV(uIn[i + 6], uIn[i + 7]);
            }
            """, "golden refl shade");
        float[] cases =
        [
            1.2f, 1.1f, 0.9f,   1.2f, 1.1f, 0.9f,    0f, 0.8f,
            0.2f, 0.25f, 0.3f,  1.2f, 1.25f, 1.2f,   0.05f, 0.05f,
            0f, 0f, 0f,         0f, 0f, 0f,          0.9f, 0.3f,
            2f, 2f, 2f,         1f, 1f, 1f,          0.4f, -0.5f,
        ];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 4, cases, 8, "golden refl shade");
        vk.Destroy(pipe);
        check.Expect(got != null, "refl shade: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant(
            $"GpuGolden: refl shade {got[0]:F4} {got[2]:F4} {got[4]:F4} {got[6]:F4}, NoV {got[1]:F4} {got[3]:F4} {got[5]:F4} {got[7]:F4}"));

        check.Expect(Near(got[0], 1f), FormattableString.Invariant($"refl shade: an open sunlit surface keeps the whole reflection ({got[0]:F4})"));
        check.Expect(Math.Abs(got[2] - 0.19663f) < 1e-4f,
                     FormattableString.Invariant($"refl shade: a shaded surface keeps the share of the light it still gets ({got[2]:F4})"));
        check.Expect(Near(got[4], 1f), FormattableString.Invariant($"refl shade: an area with no light has none to lose ({got[4]:F4})"));
        check.Expect(Near(got[6], 1f), FormattableString.Invariant($"refl shade: more light than the open takes nothing past the whole ({got[6]:F4})"));

        check.Expect(Near(got[1], 0.8f), FormattableString.Invariant($"refl NoV: a mapped normal turned from the eye takes the face's angle ({got[1]:F4})"));
        check.Expect(Near(got[3], 0.05f), FormattableString.Invariant($"refl NoV: a face at a true grazing angle keeps it ({got[3]:F4})"));
        check.Expect(Near(got[5], 0.9f), FormattableString.Invariant($"refl NoV: a mapped normal toward the eye keeps its own ({got[5]:F4})"));
        check.Expect(Near(got[7], 0.4f), FormattableString.Invariant($"refl NoV: a face turned away adds nothing ({got[7]:F4})"));
    }
}
