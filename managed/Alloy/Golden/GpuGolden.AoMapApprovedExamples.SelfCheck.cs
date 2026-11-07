using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The AO map switch and its view (approved-spec, E1 to E5 and E7 to E11): the shipped <c>CxMapOrm</c> over a map whose every texel is hand-written. Oracle:
/// those literals, multiplied by hand. The same texel answers both, so what the switch leaves is read beside what the view shows.
/// E6 is a look in the real client, since no golden here has screen-space AO.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The map every case reads: occlusion, roughness and metalness, one value each.</summary>
    private const float MapR = 0.25f, MapG = 0.5f, MapB = 0.75f;
    /// <summary>The occlusion the height map leaves in the cases that have one.</summary>
    private const float HeightOccl = 0.5f;

    /// <summary>The examples that need no GPU, which run on a host the goldens skip. Oracle: the literals in each key.</summary>
    private static void AoMapKnobApprovedSelfCheck(Checker check)
    {
        Knob declared = Ao.Map;   // first: reading it runs the static constructor that registers the key
        Knob? knob = KnobGroup.Find("ao.map");
        check.Expect(ReferenceEquals(knob, declared), "the chat key ao.map is the knob the lit stage reads");

        // Given a client that has never used /ao map
        // When a model with an AO map draws
        // Then its AO map applies, the same as today
        check.Expect(knob is Knob<bool> { Default: true }, "E10: ao.map is a toggle, on by default");

        // Given I typed /ao map 0
        // When I restart the client
        // Then AO maps are still off until I type /ao map 1
        check.Expect(knob is { Persisted: true }, "E11: ao.map is kept in cx.cfg");

        check.Expect(Renderer.Viz.Max >= 54, $"/alloy viz reaches the AO map view at 54, its ceiling is {Renderer.Viz.Max}");
    }

    private static void AoMapApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string read = Function(ShaderSource.Get("cx_ownlit_synth.glsl"), "vec3 CxMapOrm(");
        check.Expect(read.Length > 0, "cx_ownlit_synth declares CxMapOrm");
        if (read.Length == 0) return;

        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(3, 4)
            + Declaration(ShaderSource.Get("cx_slotbits.glsl"), "CX_SLOT_AO")
            + $"#define textureGrad(t, p, a, b) vec4({G(MapR)}, {G(MapG)}, {G(MapB)}, 1.0)\n" + read + """
            void main()
            {
                uint i = gl_WorkGroupID.x;
                float occl = uIn[i * uint(CX_GOLD_IN) + 2u];
                int slots = uIn[i * uint(CX_GOLD_IN)] > 0.5 ? CX_SLOT_AO : 0;
                vec3 orm = CxMapOrm(slots, vec2(0.0), vec2(0.0), vec2(0.0), uIn[i * uint(CX_GOLD_IN) + 1u], occl);
                uOut[i * uint(CX_GOLD_OUT)] = occl;
                uOut[i * uint(CX_GOLD_OUT) + 1u] = orm.r;
                uOut[i * uint(CX_GOLD_OUT) + 2u] = orm.g;
                uOut[i * uint(CX_GOLD_OUT) + 3u] = orm.b;
            }
            """, "golden ao map");
        // has a map, switched off, the occlusion the height map left
        Vector3[] cases =
        [
            new(1f, 0f, 1f), new(1f, 1f, 1f), new(0f, 0f, 1f), new(0f, 1f, 1f), new(1f, 1f, HeightOccl), new(1f, 0f, HeightOccl),
        ];
        ReadOnlySpan<float> input = MemoryMarshal.Cast<Vector3, float>(cases);
        float[]? run = pipe.IsNull ? null : RunCases(vk, log, pipe, cases.Length, input, cases.Length * 4, "golden ao map");
        vk.Destroy(pipe);
        check.Expect(run != null, "the AO map stage was refused (logged above)");
        if (run == null) return;
        ReadOnlySpan<Vector4> got = MemoryMarshal.Cast<float, Vector4>(run);
        Vector4 on = got[0], off = got[1], bareOn = got[2], bareOff = got[3], offOverHeight = got[4], onOverHeight = got[5];

        // Given a model in shade whose .mtr declares an AO map in texture11
        // When I type /ao map 0
        // Then the model's AO-darkened creases brighten, with no restart
        check.Expect(Close(on.X, MapR) && Close(off.X, 1f), $"E1: the map's occlusion, on {on.X}, off {off.X}");

        // Given the same model with AO maps switched off
        // When I type /ao map 1
        // Then the model looks exactly as it did before I switched them off
        check.Expect(on.X == MapR, $"E2: on again is the map's own value, {on.X}");

        // Given a model whose .mtr declares no texture11
        // When I type /ao map 0
        // Then the model looks the same as with AO maps on
        check.Expect(bareOn == bareOff && Close(bareOff.X, 1f), $"E3: no map, on {bareOn}, off {bareOff}");

        // Given a sword whose .mtr sets CxOrm 1, so texture11 also holds its roughness and metalness
        // When I type /ao map 0
        // Then its highlights and metal look stay the same, and only the occlusion goes
        check.Expect(Close(off.Z, MapG) && Close(off.W, MapB) && off.Z == on.Z && off.W == on.W, $"E4: roughness and metalness, on {on}, off {off}");

        // Given a model whose .mtr declares a height map as well as an AO map
        // When I type /ao map 0
        // Then /alloy viz 8 still shows the occlusion from the height map
        check.Expect(Close(offOverHeight.X, HeightOccl), $"E5: the height map's 0.5 stays, {offOverHeight.X}");

        // Given a model whose .mtr declares both an AO map and a height map
        // When I type /alloy viz 54
        // Then the model shows its AO map in black to white, with no height occlusion in it
        check.Expect(Close(onOverHeight.Y, MapR) && Close(onOverHeight.X, MapR * HeightOccl), $"E7: the view reads {onOverHeight.Y} where the factor is {onOverHeight.X}");

        // Given a model whose .mtr declares no texture11
        // When I type /alloy viz 54
        // Then the model is plain white
        check.Expect(Close(bareOn.Y, 1f), $"E8: no map reads {bareOn.Y}");

        // Given AO maps switched off with /ao map 0
        // When I type /alloy viz 54
        // Then the model still shows its AO map, so I can see what is switched off
        check.Expect(Close(off.Y, MapR), $"E9: switched off, the view reads {off.Y}");
    }

    private static bool Close(float a, float b) => MathF.Abs(a - b) < 1e-4f;
}
