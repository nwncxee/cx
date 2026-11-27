using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The shipped <c>cx_airdustup</c> upsample of the marched air run on the device over chosen blocks: a floor
/// blends its four blocks, a pillar takes none of the wall's air behind it, and a post nearer than every block takes
/// the nearest one in depth. Oracle: the light and keep each case should land on, worked out by hand.</summary>
internal static partial class GpuGolden
{
    private static void AirDustUpSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string src = ShaderSource.Get(AirScene.DustUpCs + ".glsl");
        string fn = Function(src, "vec4 CxAirUp("), edge = Declaration(src, "CX_AIR_EDGE");
        check.Expect(fn.Length > 0 && edge.Length > 0, "air dust up: cx_airdustup declares CxAirUp and CX_AIR_EDGE");
        if (fn.Length == 0 || edge.Length == 0) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(23, 4) + edge + fn + """
            vec4 T(uint at) { return vec4(uIn[at], uIn[at + 1u], uIn[at + 2u], uIn[at + 3u]); }
            void main()
            {
                uint i = gl_WorkGroupID.x * uint(CX_GOLD_IN), o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                vec4 c = CxAirUp(uIn[i], T(i + 1u), vec2(uIn[i + 5u], uIn[i + 6u]), T(i + 7u), T(i + 11u), T(i + 15u), T(i + 19u));
                uOut[o] = c.r; uOut[o + 1u] = c.g; uOut[o + 2u] = c.b; uOut[o + 3u] = c.a;
            }
            """, "golden air dust up");
        check.Expect(!pipe.IsNull, "air dust up: the stage compiles (a refusal is logged above)");
        if (pipe.IsNull) return;
        float[] cases =
        [
            0.1f, 0.1f, 0.1f, 0.1f, 0.1f, 0.25f, 0.5f,
            0.2f, 0f, 0f, 0.5f, 0.6f, 0f, 0f, 0.5f, 0.2f, 0f, 0f, 0.5f, 0.6f, 0f, 0f, 0.5f,
            0.2f, 0.2f, 0.1f, 0.2f, 0.1f, 0.9f, 0.5f,
            0f, 0f, 0f, 1f, 0.5f, 0.5f, 0.5f, 0.2f, 0f, 0f, 0f, 1f, 0.5f, 0.5f, 0.5f, 0.2f,
            0.3f, 0.1f, 0.12f, 0.1f, 0.1f, 0.5f, 0.5f,
            0.5f, 0.5f, 0.5f, 0.2f, 0.1f, 0.1f, 0.1f, 0.8f, 0.5f, 0.5f, 0.5f, 0.2f, 0.5f, 0.5f, 0.5f, 0.2f,
        ];
        float[]? got = RunCases(vk, log, pipe, cases.Length / 23, cases, cases.Length / 23 * 4, "golden air dust up");
        vk.Destroy(pipe);
        check.Expect(got != null, "air dust up: the stage was refused (logged above)");
        if (got == null) return;
        ReadOnlySpan<Vector4> air = MemoryMarshal.Cast<float, Vector4>(got.AsSpan());
        static bool Near(Vector4 c, float r, float a) => MathF.Abs(c.X - r) < 1e-4f && MathF.Abs(c.W - a) < 1e-4f;
        check.Expect(Near(air[0], 0.3f, 0.5f), $"air dust up: a floor reads {air[0]} (want light 0.3, keep 0.5: three parts left, one part right)");
        check.Expect(Near(air[1], 0f, 1f), $"air dust up: a pillar before a dusty wall reads {air[1]} (want light 0, keep 1: none of the wall's air)");
        check.Expect(Near(air[2], 0.1f, 0.8f), $"air dust up: a post nearer than every block reads {air[2]} (want light 0.1, keep 0.8: the nearest block in depth)");
    }
}
