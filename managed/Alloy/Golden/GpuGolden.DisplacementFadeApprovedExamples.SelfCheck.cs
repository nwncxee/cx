using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// How parallax fades with distance (approved-spec, E9, E10 and E12): the shipped <c>CxDisplace</c> on a floor seen at a
/// low angle, over the analytic height field. Oracle: <see cref="StockDisplace"/>, <c>inc_displacement.shd</c>'s own
/// march written out, which keeps its full relief at every distance it marches.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The floor every fade example looks at, from <paramref name="depth"/> metres.</summary>
    private static DisplaceCase FadeFloor(float depth) => new(0.15f, 0.4f, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ,
                                                              new Vector3(0f, -0.9848078f, 0.1736482f), depth, 1080f);

    private static void DisplacementFadeApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string march = Function(ShaderSource.Get("cx_ownlit_synth.glsl"), "vec2 CxDisplace(");
        string steps = Declaration(ShaderSource.Get("cx_ownlit_head.glsl"), "CX_DISPLACE_STEPS");
        check.Expect(march.Length > 0 && steps.Length > 0, "cx_ownlit_synth declares CxDisplace and cx_ownlit_head CX_DISPLACE_STEPS");
        if (march.Length == 0 || steps.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + steps
            + $"float cxH(vec2 p) {{ return {HeightGlsl}; }}\n"
            + "#define textureGrad(t, p, a, b) vec4(cxH(p))\n" + march + CaseBlocks(2, 2) + """
            void main()
            {
                uint i = gl_WorkGroupID.x;
                vec2 r = CxDisplace(vec2(0.15, 0.4), mat3(1.0), vec3(0.0, -0.9848078, 0.1736482), uIn[i * uint(CX_GOLD_IN)], 1080.0,
                                    vec2(0.0), vec2(0.0), vec2(uIn[i * uint(CX_GOLD_IN) + 1u], 0.0));
                uOut[i * uint(CX_GOLD_OUT)] = r.x; uOut[i * uint(CX_GOLD_OUT) + 1u] = r.y;
            }
            """, "golden displacement fade");
        float[] cases = [400f, 0f, 100f, 0f, 400f, 0.5f];
        float[]? run = pipe.IsNull ? null : RunCases(vk, log, pipe, cases.Length / 2, cases, cases.Length, "golden displacement fade");
        vk.Destroy(pipe);
        check.Expect(run != null, "the displacement fade stage was refused (logged above)");
        if (run == null) return;

        ReadOnlySpan<Vector2> got = MemoryMarshal.Cast<float, Vector2>(run);
        DisplaceCase floor = FadeFloor(400f);
        var start = new Vector2(floor.U, floor.V);
        double far = MovedBy(StockDisplace(floor), floor), mid = MovedBy(StockDisplace(FadeFloor(100f)), floor), farOffset = MovedBy(StockDisplace(floor, 0.5), floor);
        double moved400 = (got[0] - start).Length(), moved100 = (got[1] - start).Length(), movedOffset = (got[2] - start).Length();
        log.Info(FormattableString.Invariant($"GpuGolden: displacement fade, 400 m moved {moved400:F5} of stock {far:F5}, 100 m {moved100:F5} of {mid:F5}, 400 m with an offset {movedOffset:F5} of {farOffset:F5}"));
        check.Expect(far > 0.01 && mid > 0.01 && farOffset > 0.01, "the floor is one stock parallax still moves at every distance these examples ask about");

        // Given a height-mapped floor seen at a low angle from 400 metres
        // When it draws
        // Then its relief barely moves the texture, so no line shows where parallax stops
        check.Expect(moved400 < 0.01 * far, FormattableString.Invariant($"E9: at 400 metres the relief moved the texture {moved400:F5}, stock moves it {far:F5}"));

        // Given the same floor at 100 metres
        // When it draws
        // Then its relief moves the texture less than stock parallax does, and still moves it
        check.Expect(moved100 < 0.9 * mid && moved100 > 0.1 * mid, FormattableString.Invariant($"E10: at 100 metres the relief moved the texture {moved100:F5}, stock moves it {mid:F5}"));

        // Given a height-mapped floor whose .mtr sets DisplacementOffset 0.5, seen at a low angle from 400 metres
        // When it draws
        // Then its texture barely moves, the same as a floor with no offset
        check.Expect(movedOffset < 0.01 * farOffset, FormattableString.Invariant($"E12: with an offset at 400 metres the texture moved {movedOffset:F5}, stock moves it {farOffset:F5}"));
        check.Expect((got[2] - got[0]).Length() < 0.001f, FormattableString.Invariant($"E12: ...the same as no offset: {got[2]} against {got[0]}"));
    }

    /// <summary>How far a stock march's landing lies from where <paramref name="c"/> starts.</summary>
    private static double MovedBy((double U, double V) landed, in DisplaceCase c) => double.Hypot(landed.U - c.U, landed.V - c.V);
}
