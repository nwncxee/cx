using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The split's interior test (E8, which held before the deferred route, so its test carries no approved-spec mark): the
/// shipped <c>CxSolidCut</c> over coverage and part opacity. Oracle: the 1 / 0 verdicts written here by hand.
/// </summary>
internal static partial class GpuGolden
{
    private static void ContentSolidSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string head = ShaderSource.Get("cx_ownlit_head.glsl");
        string cut = Function(ShaderSource.Get("cx_ownlit_synth.glsl"), "bool CxSolidCut(");
        check.Expect(cut.Length > 0, "cx_ownlit_synth declares CxSolidCut");
        if (cut.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(3, 1)
                                           + "int uSolid;\n" + Declaration(head, "CX_SOLID_ALPHA") + cut + """
                                           void main()
                                           {
                                               int at = int(gl_WorkGroupID.x) * CX_GOLD_IN;
                                               uSolid = int(uIn[at + 2]);
                                               uOut[gl_WorkGroupID.x] = CxSolidCut(uIn[at], uIn[at + 1]) ? 1.0 : 0.0;
                                           }
                                           """, "golden content solid");
        // coverage, part opacity, the interior draw?
        float[] cases = [1f, 1f, 1f,   0.5f, 1f, 1f,   1f, 0.5f, 1f,   0.5f, 1f, 0f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, cases.Length / 3, cases, cases.Length / 3, "golden content solid");
        vk.Destroy(pipe);
        check.Expect(got != null, "the content solid golden's stage was refused (logged above)");
        if (got == null) return;
        log.Info($"GpuGolden: content solid cut verdicts {got[0]} {got[1]} {got[2]} {got[3]} (want 0 1 1 0)");
        check.Expect(got[0] == 0f, "a fully covered texel is cut from the interior");
        check.Expect(got[1] == 1f, "a soft edge draws solid in the interior");
        // Given a ghosted creature, faded below full opacity, wearing such hair
        // When it draws
        // Then no part of the hair draws solid, and the creature stays see-through
        check.Expect(got[2] == 1f, "a ghost's fully covered hair draws solid");
        check.Expect(got[3] == 0f, "a soft edge is cut outside the interior draw");
    }
}
