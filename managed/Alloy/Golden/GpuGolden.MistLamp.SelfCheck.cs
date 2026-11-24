using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>Mist under a lamp, E24: a plain regression, since the mist took the haze's lamp gather before it had a light of its
/// own. The shipped <c>CxVfScatter</c> over <c>CxVfMistAmbient</c>'s moonlit mist, with and without an orange lamp. Oracle, by
/// hand: the lamp's froxel is brighter and orange.</summary>
internal static partial class GpuGolden
{
    /// <summary>Per case: the lamp's red, green and blue. An orange lamp, then none.</summary>
    private static readonly float[] MistLampCases = { 0.9f, 0.5f, 0.1f, 0f, 0f, 0f };

    private static void MistLampSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string src = ShaderSource.Get("cx_vfog_cs.glsl");
        string mist = Function(src, "vec3 CxVfMistAmbient("), scatter = Function(ShaderSource.Get("cx_vfrain.glsl"), "vec3 CxVfScatter(");
        check.Expect(mist.Length > 0 && scatter.Length > 0, "mist lamp: the fog stage declares CxVfMistAmbient and CxVfScatter");
        if (mist.Length == 0 || scatter.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(3, 3) + mist + scatter + """
            void main()
            {
                uint i = gl_WorkGroupID.x * uint(CX_GOLD_IN), o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                vec3 amb = CxVfMistAmbient(vec3(0.0), vec3(0.20, 0.22, 0.30), 0.0, 1.0);
                vec3 c = CxVfScatter(amb, vec3(uIn[i], uIn[i + 1u], uIn[i + 2u]), 1.0);
                uOut[o] = c.r; uOut[o + 1u] = c.g; uOut[o + 2u] = c.b;
            }
            """, "golden mist lamp");
        check.Expect(!pipe.IsNull, "mist lamp: the stage compiles (a refusal is logged above)");
        if (pipe.IsNull) return;
        float[]? got = RunCases(vk, log, pipe, 2, MistLampCases, 6, "golden mist lamp");
        vk.Destroy(pipe);
        check.Expect(got != null, "mist lamp: the stage was refused (logged above)");
        if (got == null) return;

        // Given the docks at night
        // When the player looks at mist under a lit lamp post
        // Then that mist glows in the lamp's colour, brighter than mist away from lamps.
        check.Expect(got[0] + got[1] + got[2] > got[3] + got[4] + got[5] && got[0] - got[2] > got[3] - got[5],
            $"E24 mist under the lamp scatters ({got[0]:F3},{got[1]:F3},{got[2]:F3}), away from it ({got[3]:F3},{got[4]:F3},{got[5]:F3}) (want brighter, toward orange)");
    }
}
