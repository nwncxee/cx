using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The colour tap's filtering (approved-spec, E13 and E14): the shipped <c>CxColourTap</c> over analytic colour fields,
/// with one pixel as one unit of the coordinate. Oracle: the field itself read at the pixel, written by hand, which is
/// what one colour sample sees.
/// </summary>
internal static partial class GpuGolden
{
    private static void ColourFilterApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string tap = Function(ShaderSource.Get("cx_ownlit_synth.glsl"), "vec4 CxColourTap(");
        check.Expect(tap.Length > 0, "cx_ownlit_synth declares CxColourTap");
        if (tap.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n"
            + "float cxMode;\nfloat cxField(vec2 p) { return cxMode > 0.5 ? 0.5 + 0.01 * p.x : 0.5 + 0.5 * sin(3.14159265 * p.x); }\n"
            + "#define textureGrad(t, p, a, b) vec4(cxField(p))\n" + tap + CaseBlocks(2, 1) + """
            void main()
            {
                uint i = gl_WorkGroupID.x;
                cxMode = uIn[i * uint(CX_GOLD_IN) + 1u];
                uOut[i * uint(CX_GOLD_OUT)] = CxColourTap(vec2(uIn[i * uint(CX_GOLD_IN)], 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0)).r;
            }
            """, "golden colour filter");
        const int Phases = 16;
        var cases = new Vector2[Phases * 2];
        for (int k = 0; k < Phases; k++)
        {
            cases[k] = new Vector2(k / 8f, 0f);
            cases[Phases + k] = new Vector2(k * 3f, 1f);
        }
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, Phases * 2, MemoryMarshal.Cast<Vector2, float>(cases), Phases * 2, "golden colour filter");
        vk.Destroy(pipe);
        check.Expect(got != null, "the colour filter stage was refused (logged above)");
        if (got == null) return;

        float low = 1f, high = 0f, worst = 0f;
        for (int k = 0; k < Phases; k++)
        {
            low = MathF.Min(low, got[k]);
            high = MathF.Max(high, got[k]);
            worst = MathF.Max(worst, MathF.Abs(got[Phases + k] - (0.5f + 0.01f * k * 3f)));
        }

        // Given a floor whose colour grain alternates from one pixel to the next
        // When the camera moves by a fraction of a pixel
        // Then a pixel's colour swings at most three quarters as far as it does with one colour sample
        check.Expect(high - low <= 0.75f && high - low > 0.1f, FormattableString.Invariant($"E13: the colour swung {high - low:F3}, one colour sample swings 1.000"));

        // Given a floor whose colour changes slowly across many pixels
        // When it draws
        // Then each pixel's colour is the same as with one colour sample
        check.Expect(worst < 1e-4f, FormattableString.Invariant($"E14: a pixel's colour differed from one colour sample by {worst:F5}"));
    }
}
