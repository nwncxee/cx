using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Regression goldens beside the shading-parity examples (they hold before the change, so they carry no
/// approved-spec mark): a placed light keeps the diffuse weight, an opaque part lets no light through, and a raised
/// Diffuse wrap still widens the band without moving the peak. Oracles: the hand-written weights and literals below.
/// </summary>
internal static partial class GpuGolden
{
    private static void ShadingParitySelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        // CX-447 E2: a placed light on the red-ambient, blue-diffuse material shades blue, with deferred lights on.
        DeferredRigScene placed = ParityScene(Vector3.UnitZ, -1f, 1f,
                                              p => p with { DiffT = new Vector3(0f, 0f, 1f), AmbT = new Vector3(1f, 0f, 0f) });
        if (ParityCentre(vk, check, log, placed, "CX-447 E2 a placed light on red ambient, blue diffuse") is { } c)
            check.Expect(c.Z > 1e-3f && c.X < 1e-5f,
                         FormattableString.Invariant($"CX-447 E2: a placed light shades the wall blue, not red ({c.X:F4}, {c.Y:F4}, {c.Z:F4})"));

        // CX-449 E12: an opaque part with a placed light straight behind it receives nothing from it.
        if (ParityCentre(vk, check, log, ParityScene(-Vector3.UnitZ, -1f, 1f, p => p with { DiffT = Vector3.One }),
                         "CX-449 E12 an opaque wall, the lamp behind") is { } opaque)
            check.Expect(opaque.Y < 1e-5f, FormattableString.Invariant($"CX-449 E12: the lamp behind an opaque wall gives {opaque.Y:F5}, not 0"));

        // CX-448 E10: Diffuse wrap 1 gives more than the stock 0.056 at 90 degrees and exactly 1 head-on.
        string lf = ShaderSource.Get("cx_lightfrag.glsl");
        string wrap = Function(lf, "float CxWrap(");
        check.Expect(wrap.Length > 0, "CX-448 E10: cx_lightfrag declares CxWrap");
        if (wrap.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(2, 1) + Declaration(lf, "CX_SOFTEN")
            + "bool cxFragNormal = false;\n" + wrap + """
            void main()
            {
                uint i = gl_WorkGroupID.x;
                uOut[i] = CxWrap(uIn[i * uint(CX_GOLD_IN)], uIn[i * uint(CX_GOLD_IN) + 1u]);
            }
            """, "golden wrap band");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 2, [0f, 1f, 1f, 1f], 2, "golden wrap band");
        vk.Destroy(pipe);
        check.Expect(got != null, "CX-448 E10: the wrap stage was refused (logged above)");
        if (got == null) return;
        check.Expect(got[0] > 0.056f && got[1] == 1f,
                     FormattableString.Invariant($"CX-448 E10: wrap 1 gives {got[0]:F4} at 90 degrees (over 0.056) and {got[1]:F4} head-on (exactly 1)"));
    }
}
