using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The mist the fog's integral takes along one slice of a ray: a plain regression for the banded street, where each
/// slice ramped its mist across itself. The shipped <c>CxMistDepth</c> against a fine sum of <c>CxMistDensity</c> along the
/// same segment, over an even field 1 m deep on a floor at 0 m.</summary>
internal static partial class GpuGolden
{
    /// <summary>The shipped functions the depth golden splices, in the order they declare.</summary>
    private static readonly string[] MistDepthLibrary =
        { "float CxMistDensity(", "float CxMistRise(", "float CxMistTop(", "bool CxMistColumn(", "float CxMistDepth(" };

    /// <summary>Per case, a segment's ends (x, y, z twice): down a camera's ray onto the street, a slice of it through
    /// the thinning band, one grazing the floor, two level ones, one wholly above the mist and one under the floor.</summary>
    private static readonly float[] MistDepthCases =
    {
        0f, 0f, 9f, 12f, 0.5f, -0.2f,
        6f, 0f, 0.7f, 7.3f, 0f, 0.05f,
        2f, 1f, 0.02f, 9f, 1.5f, 0.12f,
        -3f, 2f, 0.1f, 3f, 2f, 0.1f,
        -3f, 2f, 0.45f, 3f, 2f, 0.45f,
        0f, 0f, 2f, 1f, 0f, 3f,
        0f, 0f, -1f, 1f, 0f, -0.5f,
    };

    /// <summary>Floats per depth case.</summary>
    private const int MistDepthFloats = 6;

    private static void MistDepthSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string src = ShaderSource.Get("cx_mist.glsl"), lib = Declaration(src, "CX_MIST_CAP") + Declaration(src, "CX_MIST_INSIDE");
        foreach (string sig in MistDepthLibrary)
        {
            string fn = Function(src, sig);
            check.Expect(fn.Length > 0, $"mist depth: cx_mist declares {sig}");
            if (fn.Length == 0) return;
            lib += fn;
        }
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(MistDepthFloats, 2)
            + "vec4 uMistHead = vec4(-128.0, -128.0, 256.0, 1.0), uMistFlow = vec4(0.0);\n"
            + "vec4 CxMistTap(ivec2 c, int side) { return vec4(0.0, 1.0, 0.0, 0.0); }\n"
            + ShaderSource.Get("cx_noise.glsl") + lib + """
            void main()
            {
                uint b = gl_WorkGroupID.x * uint(CX_GOLD_IN), o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                vec3 a = vec3(uIn[b], uIn[b + 1u], uIn[b + 2u]), e = vec3(uIn[b + 3u], uIn[b + 4u], uIn[b + 5u]);
                vec3 m = 0.5 * (a + e);
                float top = CxMistTop(1.0, m.xy, 0.0), sum = 0.0;
                const int steps = 4000;
                for (int i = 0; i < steps; i++)
                    sum += CxMistDensity(top, 0.0, mix(a, e, (float(i) + 0.5) / float(steps)).z);
                uOut[o] = CxMistDepth(a, e);
                uOut[o + 1u] = sum * distance(a, e) / float(steps);
            }
            """, "golden mist depth");
        check.Expect(!pipe.IsNull, "mist depth: the stage compiles (a refusal is logged above)");
        if (pipe.IsNull) return;
        int n = MistDepthCases.Length / MistDepthFloats;
        float[]? got = RunCases(vk, log, pipe, n, MistDepthCases, n * 2, "golden mist depth");
        vk.Destroy(pipe);
        check.Expect(got != null, "mist depth: the stage was refused (logged above)");
        if (got == null) return;
        ReadOnlySpan<Vector2> pairs = MemoryMarshal.Cast<float, Vector2>(got.AsSpan());
        for (int i = 0; i < n; i++)
            check.Expect(MathF.Abs(pairs[i].X - pairs[i].Y) <= 0.01f * pairs[i].Y + 1e-4f,
                $"mist depth: segment {i} takes {pairs[i].X:F5} of mist, a fine sum along it {pairs[i].Y:F5} (want within 1%)");
        check.Expect(pairs[0].X > 0.01f && pairs[5].X == 0f && pairs[6].X == 0f,
            $"mist depth: the street's ray takes {pairs[0].X:F4}, the air above {pairs[5].X:F4}, under the floor {pairs[6].X:F4} (want some, none, none)");
    }
}
