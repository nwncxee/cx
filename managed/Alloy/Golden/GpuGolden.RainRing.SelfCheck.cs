using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The ground ring's colour: the shipped <c>CxSplashRingLight</c> over the light falling on it. Oracle: dirt reflects about
/// a tenth of its light, written by hand, and a ring on it must not outshine it, nor vanish.
/// </summary>
internal static partial class GpuGolden
{
    private static void RainRingSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string src = ShaderSource.Get("cx_splash_f.glsl");
        string ring = Function(src, "vec3 CxSplashRingLight(");
        check.Expect(ring.Length > 0, "cx_splash_f declares CxSplashRingLight");
        if (ring.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, 1)
                                           + Declaration(src, "CX_SPLASH_RING_REFLECT") + ring + """
                                           void main() { uOut[0] = CxSplashRingLight(vec3(uIn[0])).g; }
                                           """, "golden rain ring");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 1, [1f], 1, "golden rain ring");
        vk.Destroy(pipe);
        check.Expect(got != null, "the rain ring golden's stage was refused (logged above)");
        if (got == null) return;
        log.Info($"GpuGolden: a rain ring under unit light sends {got[0]} to the eye");
        check.Expect(got[0] > 0f && got[0] < 0.1f, $"a ring under unit light sends {got[0]} to the eye, not under dirt's tenth");
    }
}
