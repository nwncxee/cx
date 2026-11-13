using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The highlight mask's resolve over frames: the shipped <c>CxHlResolve</c> fed a jittered edge, a covered pixel and
/// an edge that moved away. Oracle: hand-written swing and coverage limits, against one frame's mask swinging 0 to 1.
/// </summary>
internal static partial class GpuGolden
{
    private static void HighlightResolveSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string fn = Function(ShaderSource.Get("cx_hiliteres_f.glsl"), "vec4 CxHlResolve(");
        check.Expect(fn.Length > 0, "cx_hiliteres_f declares CxHlResolve");
        if (fn.Length == 0) return;
        const int Frames = 32, Tail = 8;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(1, Tail) + fn + $$"""
            void main()
            {
                uint i = gl_WorkGroupID.x, o = i * uint(CX_GOLD_OUT);
                float keep = uIn[i];
                vec4 hist = vec4(i == 2u ? 1.0 : 0.5);
                for (int f = 0; f < {{Frames}}; f++)
                {
                    vec4 cur = i == 0u ? vec4(fract(float(f) * 0.618034)) : vec4(i == 1u ? 1.0 : 0.0);
                    vec4 lo = i == 0u ? vec4(0.0) : cur, hi = i == 0u ? vec4(1.0) : cur;
                    hist = CxHlResolve(hist, cur, lo, hi, keep);
                    if (f >= {{Frames - Tail}}) uOut[o + uint(f - {{Frames - Tail}})] = hist.a;
                }
            }
            """, "golden highlight resolve");
        float k = Highlight.VkHistoryKeep;
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 3, [k, k, k], 3 * Tail, "golden highlight resolve");
        vk.Destroy(pipe);
        check.Expect(got != null, "the highlight resolve stage was refused (logged above)");
        if (got == null) return;

        ReadOnlySpan<float> edge = got.AsSpan(0, Tail), covered = got.AsSpan(Tail, Tail), moved = got.AsSpan(2 * Tail, Tail);
        float low = 1f, high = 0f;
        foreach (float v in edge) { low = MathF.Min(low, v); high = MathF.Max(high, v); }
        check.Expect(high - low < 0.3f && high > 0.2f && low < 0.8f,
                     FormattableString.Invariant($"a jittered outline edge swings {high - low:F3} once resolved; one frame's mask swings up to 1"));
        check.Expect(covered[0] == 1f, FormattableString.Invariant($"a covered pixel reads fully covered ({covered[0]:F3})"));
        check.Expect(moved[0] == 0f, FormattableString.Invariant($"an edge that moved away leaves no coverage behind ({moved[0]:F3})"));
    }
}
