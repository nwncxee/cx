using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The ground mist's vertical profile, the fog placeables' E2: the shipped <c>CxMistDensity</c> over a column 1 m deep
/// whose floor is at 0 m. Oracle, by hand: mist at a quarter metre is there, and at the emitter's 10 m it is not.</summary>
internal static partial class GpuGolden
{
    /// <summary>The heights the profile golden reads: a quarter metre above the floor, and an emitter's 10 m.</summary>
    private static readonly float[] MistHeights = { 0.25f, 10f };

    private static void MistProfileApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string src = ShaderSource.Get("cx_mist.glsl"), fn = Function(src, "float CxMistDensity(");
        check.Expect(fn.Length > 0, "mist profile: cx_mist declares CxMistDensity");
        if (fn.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 1) + Declaration(src, "CX_MIST_CAP") + Declaration(src, "CX_MIST_INSIDE") + fn + """
            void main()
            {
                float z = uIn[gl_WorkGroupID.x * uint(CX_GOLD_IN)];
                uOut[gl_WorkGroupID.x * uint(CX_GOLD_OUT)] = CxMistDensity(1.0, 0.0, z);
            }
            """, "golden mist profile");
        check.Expect(!pipe.IsNull, "mist profile: the stage compiles (a refusal is logged above)");
        if (pipe.IsNull) return;
        float[]? got = RunCases(vk, log, pipe, MistHeights.Length, MistHeights, MistHeights.Length, "golden mist profile");
        vk.Destroy(pipe);
        check.Expect(got != null, "mist profile: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant($"GpuGolden: mist profile, {got[0]:F4} at 0.25 m, {got[1]:F4} at 10 m"));

        // Given Volumetric fog is on and a fog emitter floats 10 m above the ground
        // When the player looks at it
        // Then the mist lies on the ground below the emitter, not at the emitter's height
        check.Expect(got[0] > 0f && got[1] < got[0] * 0.01f,
            $"CX-680 E2: a column 1 m deep holds {got[0]:F4} at 0.25 m (want above 0) and {got[1]:F4} at the emitter's 10 m (want under 1% of it)");
    }
}
