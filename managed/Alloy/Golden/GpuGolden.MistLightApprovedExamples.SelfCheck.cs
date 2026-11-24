using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The light a froxel of mist scatters at night: the shipped <c>CxVfMistAmbient</c> over black fog and a pale blue
/// moon. Oracle, by hand: a froxel of mist alone shows the moon, one of haze alone the fog colour.</summary>
internal static partial class GpuGolden
{
    /// <summary>Per case: haze density, mist density. Mist alone, then haze alone.</summary>
    private static readonly float[] MistLightCases = { 0f, 1f, 1f, 0f };

    private static void MistLightApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string fn = Function(ShaderSource.Get("cx_vfog_cs.glsl"), "vec3 CxVfMistAmbient(");
        check.Expect(fn.Length > 0, "mist light: cx_vfog_cs declares CxVfMistAmbient");
        if (fn.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(2, 3) + fn + """
            void main()
            {
                uint i = gl_WorkGroupID.x * uint(CX_GOLD_IN), o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                vec3 c = CxVfMistAmbient(vec3(0.0), vec3(0.20, 0.22, 0.30), uIn[i], uIn[i + 1u]);
                uOut[o] = c.r; uOut[o + 1u] = c.g; uOut[o + 2u] = c.b;
            }
            """, "golden mist light");
        check.Expect(!pipe.IsNull, "mist light: the stage compiles (a refusal is logged above)");
        if (pipe.IsNull) return;
        float[]? got = RunCases(vk, log, pipe, 2, MistLightCases, 6, "golden mist light");
        vk.Destroy(pipe);
        check.Expect(got != null, "mist light: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant($"GpuGolden: mist light, mist ({got[0]:F3},{got[1]:F3},{got[2]:F3}), haze ({got[3]:F3},{got[4]:F3},{got[5]:F3})"));

        // Given the docks at night, where the area's fog colour is black
        // When the player looks at mist away from lamps
        // Then it shows pale in the moonlight's colour rather than black, lighter than with the area's fog colour alone.
        check.Expect(got[2] > got[0] && got[0] + got[1] + got[2] > got[3] + got[4] + got[5],
            $"E25 mist under the moon scatters ({got[0]:F3},{got[1]:F3},{got[2]:F3}) against haze's ({got[3]:F3},{got[4]:F3},{got[5]:F3}) (want blue and lighter)");
        check.Expect(got[3] == 0f && got[4] == 0f && got[5] == 0f, "E25 ...and haze alone keeps the fog colour's black");
    }
}
