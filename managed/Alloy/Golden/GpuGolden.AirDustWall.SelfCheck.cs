using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The shipped dust stage's <c>CxAirLayer</c> and <c>cx_air</c>'s span run on the device beside a column with no
/// floor: a wall, a pillar or a building's base, its floor 1e9 m down. Oracle: such a column lays no air, so its share
/// reads exactly nothing, and the profile's mean over a straight span is its value at the middle.</summary>
internal static partial class GpuGolden
{
    /// <summary>Per case: the cell (floor, mist, dust, dust's height), its share, the height read, half the step's
    /// height, where the ray ends, the mist's top, the quarter's floor and the slab's mist height.</summary>
    private const int WallIns = 11;

    // Given night mist and rain in a city street, a wall's column beside the street's
    // When the march reads the air a step's span of height above the street next to the wall
    // Then the wall's column adds no air, and the street's own column adds its mist: no pixel turns black.
    private static void AirDustWallSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string stage = ShaderSource.Get(AirScene.ShdCs + ".glsl");
        string[] parts =
        [
            Declaration(stage, "CX_AIR_STEP ="), Declaration(stage, "CX_AIR_DROP"),
            Declaration(stage, "CX_AIR_WALL"), "vec4 uAirSlab;\n",
            Function(AirSource, "float CxAirDust("), Function(AirSource, "float CxAirDustSpan("), Function(AirSource, "bool CxAirJoined("),
            Function(stage, "float CxAirBase("), Function(stage, "float CxAirProfile("), Function(stage, "vec2 CxAirLayer("),
        ];
        check.Expect(Array.TrueForAll(parts, p => p.Length > 0), "air dust wall: cx_airdust_cs and cx_air declare the layer and what it calls");
        if (!Array.TrueForAll(parts, p => p.Length > 0)) return;
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(WallIns, 2) + string.Concat(parts) + """
            void main()
            {
                uint i = gl_WorkGroupID.x * uint(CX_GOLD_IN), o = gl_WorkGroupID.x * uint(CX_GOLD_OUT);
                uAirSlab = vec4(0.0, 0.0, 0.0, uIn[i + 10u]);
                vec2 a = CxAirLayer(vec4(uIn[i], uIn[i + 1u], uIn[i + 2u], uIn[i + 3u]), uIn[i + 4u], uIn[i + 5u], uIn[i + 6u], uIn[i + 7u], uIn[i + 8u], uIn[i + 9u]);
                uOut[o] = a.x; uOut[o + 1u] = a.y;
            }
            """, "golden air dust wall");
        check.Expect(!pipe.IsNull, "air dust wall: the stage compiles (a refusal is logged above)");
        if (pipe.IsNull) return;
        float[] cases =
        [
            -1e9f, 0f, 0f, 0f, 0.25f, 0.5f, 0.05f, 0f, 1.5f, 0f, 1f,
            0f, 1f, 0f, 0f, 0.75f, 0.5f, 0.05f, 0f, 1.5f, 0f, 1f,
        ];
        float[]? got = RunCases(vk, log, pipe, cases.Length / WallIns, cases, cases.Length / WallIns * 2, "golden air dust wall");
        vk.Destroy(pipe);
        check.Expect(got != null, "air dust wall: the stage was refused (logged above)");
        if (got == null) return;
        check.Expect(got[0] == 0f && got[1] == 0f, $"air dust wall: the wall's column lays dust {got[0]}, mist {got[1]} half a metre over the street (want 0 and 0, no NaN)");
        check.Expect(float.IsFinite(got[3]) && got[3] > 0f, $"air dust wall: the street's own column lays mist {got[3]} there (want some)");
        float[]? span = AirRun(vk, check, log, "float CxAirDustSpan(", "    uOut[o] = CxAirDustSpan(uIn[i], uIn[i + 1u], uIn[i + 2u]);", 3, 1,
            [1e9f, 0.05f, 1.5f, 0.2f, 0.05f, 1f], "golden air dust span far");
        if (span == null) return;
        check.Expect(span[0] == 0f, $"air dust wall: a step's span 1e9 m over a floor reads {span[0]} (want 0: its two ends round together, and no NaN)");
        check.Expect(MathF.Abs(span[1] - 1.6f) < 1e-4f, $"air dust wall: a span of 0.15 m to 0.25 m in a 1 m layer reads {span[1]} (want 1.6, the profile at its middle)");
    }
}
