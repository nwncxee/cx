using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// What scene light does to a lit particle (found work, E1-E3). Oracle: the stock client draws every particle
/// unlit, a factor of 1, so a lit particle takes that as its floor and light past it only adds.
/// </summary>
internal static partial class GpuGolden
{
    private static void ParticleLightSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string part = Function(ShaderSource.Get("cx_part_vmain.glsl"), "vec3 CxPartLight(");
        string floor = Function(ShaderSource.Get("cx_lightfrag.glsl"), "vec3 CxLitFloor(");
        check.Expect(part.Length > 0 && floor.Length > 0, "particle light: cx_part_vmain declares CxPartLight");
        if (part.Length == 0 || floor.Length == 0) return;

        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + floor + part + CaseBlocks(3, 3) + """
            void main()
            {
                int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
                vec3 r = CxPartLight(vec3(uIn[i], uIn[i + 1], uIn[i + 2]));
                uOut[o] = r.x; uOut[o + 1] = r.y; uOut[o + 2] = r.z;
            }
            """, "golden particle light");
        // a night forest's dim green ambient, no light at all, and a torch's orange beside the particle
        float[] cases = [0.08f, 0.12f, 0.06f, 0f, 0f, 0f, 2.0f, 1.2f, 0.4f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 3, cases, 9, "golden particle light");
        vk.Destroy(pipe);
        check.Expect(got != null, "particle light: the stage was refused (logged above)");
        if (got == null) return;
        log.Info(FormattableString.Invariant(
            $"GpuGolden: particle light, dim ({got[0]:F3}, {got[1]:F3}, {got[2]:F3}) dark ({got[3]:F3}, {got[4]:F3}, {got[5]:F3}) torch ({got[6]:F3}, {got[7]:F3}, {got[8]:F3})"));

        // Given a lit particle in an area whose ambient, sun and point light sum below 1, as a forest at night
        // When the vertex stage turns the light rig into the particle's colour factor
        // Then the factor is 1 in every channel: the particle draws as the stock client draws it
        check.Expect(Near(got[0], 1f) && Near(got[1], 1f) && Near(got[2], 1f),
                     FormattableString.Invariant($"E1: a dim area darkens a lit particle ({got[0]:F3}, {got[1]:F3}, {got[2]:F3})"));

        // Given a lit particle where no light reaches it at all
        // When the vertex stage turns the light rig into the particle's colour factor
        // Then the factor is still 1: light never makes a particle invisible
        check.Expect(Near(got[3], 1f) && Near(got[4], 1f) && Near(got[5], 1f),
                     FormattableString.Invariant($"E2: no light hides a lit particle ({got[3]:F3}, {got[4]:F3}, {got[5]:F3})"));

        // Given a lit particle beside a torch whose light sums past 1 in red and green
        // When the vertex stage turns the light rig into the particle's colour factor
        // Then those channels take the light and the blue one stays at 1, so smoke picks up the torch's colour
        check.Expect(Near(got[6], 2.0f) && Near(got[7], 1.2f) && Near(got[8], 1f),
                     FormattableString.Invariant($"E3: a torch does not brighten and tint a lit particle ({got[6]:F3}, {got[7]:F3}, {got[8]:F3})"));
    }
}
