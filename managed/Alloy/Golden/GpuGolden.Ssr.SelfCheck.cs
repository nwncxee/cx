using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>One join: this pixel's inverse depth, a ray origin's, the plane's step per pixel and the origin's
    /// farthest reach in pixels, and the gap (a share of <c>K</c>) worked out by hand.</summary>
    private readonly record struct JoinCase(string Name, float K, float Tw, float Gx, float Gy, float Rx, float Ry, double Gap);

    /// <summary>Oracle: hand arithmetic. Grazing ground at 20 m loses 0.0008 of inverse depth a row; an origin three
    /// rows up sits on the same plane (gap 0), where the plain test read 4.8%. Another surface at that reach does not,
    /// and a facing wall with no slope keeps the plain share.</summary>
    private static readonly JoinCase[] JoinCases =
    [
        new("grazing ground, origin three rows up", 0.05f, 0.0476f, 0f, 0.0008f, 1f, 3f, 0.0),
        new("grazing ground, origin on another surface", 0.05f, 0.03f, 0f, 0.0008f, 1f, 3f, 0.352),
        new("facing wall, no slope", 0.05f, 0.051f, 0f, 0f, 3f, 3f, 0.02),
    ];

    /// <summary>The SSR resolve's join on the GPU: the shipped <c>cx_ssrrec</c>'s <c>CxSsrJoinGap</c> over
    /// <see cref="JoinCases"/>.</summary>
    private static void SsrJoinSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string src = Version + "layout(local_size_x = 1) in;\n" + ShaderSource.Get("cx_ssrrec.glsl") + CaseBlocks(6, 1) + """
                     void main()
                     {
                         int b = int(gl_WorkGroupID.x) * CX_GOLD_IN;
                         uOut[gl_WorkGroupID.x] = CxSsrJoinGap(uIn[b], uIn[b + 1], vec2(uIn[b + 2], uIn[b + 3]),
                                                               vec2(uIn[b + 4], uIn[b + 5]));
                     }
                     """;
        PipelineId pipe = vk.CreateCompute(src, "golden ssr join");
        check.Expect(!pipe.IsNull, "the SSR join golden's stage builds for the device");
        if (!pipe.IsNull)
        {
            int n = JoinCases.Length;
            var flat = new float[n * 6];
            for (int i = 0; i < n; i++)
            {
                JoinCase c = JoinCases[i];
                flat[i * 6] = c.K; flat[i * 6 + 1] = c.Tw; flat[i * 6 + 2] = c.Gx;
                flat[i * 6 + 3] = c.Gy; flat[i * 6 + 4] = c.Rx; flat[i * 6 + 5] = c.Ry;
            }
            float[]? got = RunCases(vk, log, pipe, n, flat, n, "golden ssr join");
            check.Expect(got != null, "the SSR join golden dispatches and reads back");
            if (got != null)
                for (int i = 0; i < n; i++)
                    Near(check, $"ssr join, {JoinCases[i].Name}", got[i], JoinCases[i].Gap, 1e-4);
            log.Info($"GpuGolden: ssr join, {n} cases");
        }
        vk.Destroy(pipe);
    }
}
