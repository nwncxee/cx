using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The near tier fits its box: the shipped <c>CxWxNearKeep</c> at the precipitation box's nearest edge, half of
/// <see cref="Weather.Box"/> from the camera. Oracle: nothing kept there, written by hand, so the box's edge is never
/// a line of drops and the far veil, which takes what the keep leaves, owns the whole distance past it.
/// </summary>
internal static partial class GpuGolden
{
    private static void NearTierSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string keep = Function(ShaderSource.Get("cx_vfrain.glsl"), "float CxWxNearKeep(");
        check.Expect(keep.Length > 0, "near tier: cx_vfrain declares CxWxNearKeep");
        if (keep.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 1) + keep + """
            void main() { uOut[gl_WorkGroupID.x * uint(CX_GOLD_OUT)] = CxWxNearKeep(uIn[gl_WorkGroupID.x * uint(CX_GOLD_IN)]); }
            """, "golden near tier");
        float[] cases = [Weather.Box * 0.5f];
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, cases, 1, "golden near tier");
        vk.Destroy(pipe);
        check.Expect(got != null, "near tier: the stage was refused (logged above)");
        if (got == null) return;
        check.Expect(got[0] == 0f, FormattableString.Invariant(
            $"near tier: {got[0]:F3} of the drops are kept at the box's edge, {cases[0]:F1} m out; nothing may be"));
    }
}
