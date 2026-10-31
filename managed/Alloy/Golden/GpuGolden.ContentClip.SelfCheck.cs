using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>The content halves' <c>clipParams</c> on the GPU: the shipped <c>CxConClip</c> and <c>CxConClipV</c>
    /// over the m_proj the frame hands content. Oracle: the near 0.5 and far 200 the fixture's camera is built with.</summary>
    private static void ContentClipSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string fs = Function(ShaderSource.Get("cx_content_f.glsl"), "vec3 CxConClip(");
        string vs = Function(ShaderSource.Get("cx_content_v.glsl"), "vec3 CxConClipV(");
        check.Expect(fs.Length > 0 && vs.Length > 0, "the content halves no longer declare CxConClip and CxConClipV");
        if (fs.Length == 0 || vs.Length == 0) return;
        System.Numerics.Matrix4x4 proj = WorldDepth.Fold(AlloyCamera.Perspective(45f, 1f, 0.5f, 200f));
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 6)
                                           + $"const mat4 m_proj = {Mat(proj)};\n" + fs + vs
                                           + "void main() { vec3 f = CxConClip(), v = CxConClipV();\n"
                                           + "  uOut[0] = f.x; uOut[1] = f.y; uOut[2] = f.z; uOut[3] = v.x; uOut[4] = v.y; uOut[5] = v.z; }\n",
                                           "golden content clip");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, [0f], 6, "golden content clip");
        vk.Destroy(pipe);
        check.Expect(got != null, "the content clip golden's stage was refused (logged above)");
        if (got == null) return;
        float[] want = [0.5f, 200f, 0.9975f];
        for (int k = 0; k < 6; k++)
            check.Expect(Math.Abs(got[k] - want[k % 3]) <= want[k % 3] * 0.001f,
                         $"content {(k < 3 ? "fragment" : "vertex")} clipParams[{k % 3}] reads {got[k]}, not {want[k % 3]}");
    }
}
