using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The shading frame a two-sided part's back face takes (approved-spec, E1, E2). Oracle: the vertex normal written
/// here by hand, which <c>inc_standard.shd</c>:182-183 negates on a back face and leaves alone on a front one.
/// </summary>
internal static partial class GpuGolden
{
    private static void FaceNormalApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string face = Function(ShaderSource.Get("cx_lightfrag.glsl"), "vec3 CxFaceNormal(");
        check.Expect(face.Length > 0, "face normal: cx_lightfrag declares CxFaceNormal");
        if (face.Length == 0) return;

        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + face + CaseBlocks(4, 3) + """
            void main()
            {
                int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                vec3 r = CxFaceNormal(vec3(uIn[i], uIn[i + 1], uIn[i + 2]), uIn[i + 3] > 0.5);
                uOut[o] = r.x; uOut[o + 1] = r.y; uOut[o + 2] = r.z;
            }
            """, "golden face normal");
        // a leaf card's normal, off-axis in all three so a flip cannot hide in a zero, and interpolated to
        // a length other than 1 the way a real varying arrives
        float[] cases = [0.6f, -0.48f, 0.8f, 1f, 0.6f, -0.48f, 0.8f, 0f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 2, cases, 6, "golden face normal");
        vk.Destroy(pipe);
        check.Expect(got != null, "face normal: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant(
            $"GpuGolden: face normal, front ({got[0]:F4}, {got[1]:F4}, {got[2]:F4}) back ({got[3]:F4}, {got[4]:F4}, {got[5]:F4})"));

        float len = MathF.Sqrt(0.6f * 0.6f + 0.48f * 0.48f + 0.8f * 0.8f);
        (float X, float Y, float Z) unit = (0.6f / len, -0.48f / len, 0.8f / len);

        // Given a lit part's fragment on a front face
        // When the lit stage prepares its shading frame
        // Then the shading normal is the vertex normal as interpolated
        check.Expect(Near(got[0], unit.X) && Near(got[1], unit.Y) && Near(got[2], unit.Z),
                     FormattableString.Invariant($"E1: a front face keeps its vertex normal ({got[0]:F4}, {got[1]:F4}, {got[2]:F4})"));

        // Given a two-sided part's fragment on a back face
        // When the lit stage prepares its shading frame
        // Then the shading normal is the vertex normal negated, so the far side of a leaf card lights from its own side
        check.Expect(Near(got[3], -unit.X) && Near(got[4], -unit.Y) && Near(got[5], -unit.Z),
                     FormattableString.Invariant($"E2: a back face negates it ({got[3]:F4}, {got[4]:F4}, {got[5]:F4})"));
    }

    /// <summary>Within a float's worth of the oracle: the goldens' own tolerance for a normalised component.</summary>
    private static bool Near(float got, float want) => Math.Abs(got - want) < 1e-5f;
}
