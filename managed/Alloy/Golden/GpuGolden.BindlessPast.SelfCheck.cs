using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// A texture index past what the device seats reads the empty texture, and one inside reads its texture. A plain
/// regression golden: the behaviour held before the bindless cap moved out of the shader text, so it cannot go red
/// first. Oracle: a white texel uploaded here by hand, and the backend's zero texel.
/// </summary>
internal static partial class GpuGolden
{
    private const string BindlessPastCs = """
        #version 460 core
        #extension GL_ARB_bindless_texture : require
        layout(local_size_x = 1) in;
        """;

    private const string BindlessPastBody = """
        void main()
        {
            vec4 inside = textureLod(sampler2D(uvec2(floatBitsToUint(uIn[0]), 0u)), vec2(0.5), 0.0);
            vec4 past = textureLod(sampler2D(uvec2(floatBitsToUint(uIn[1]), 0u)), vec2(0.5), 0.0);
            for (int i = 0; i < 4; i++) { uOut[i] = inside[i]; uOut[4 + i] = past[i]; }
        }
        """;

    private static void BindlessPastSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId white = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 1, 1, 1, 1, 1, 1, ImageUse.Sampled | ImageUse.CopyDst, "golden bindless white"));
        bool up = !white.IsNull && vk.Upload(white, 0, 0, [255, 255, 255, 255]);
        BindlessHandle h = up ? vk.Bindless(white, default) : default;
        VkProgramText t = VkGlsl.TranslateCompute(BindlessPastCs + "\n" + CaseBlocks(2, 8) + BindlessPastBody, new Dictionary<string, uint>());
        check.Expect(t.Error == null, $"the bindless golden translates ({t.Error})");
        PipelineId pipe = t.Error == null ? vk.CreateCompute(t.Cs, "golden bindless past") : default;
        // Given a texture index past what the GPU seats
        // When a stage samples it
        // Then it reads the empty texture, as it does today
        float[]? got = pipe.IsNull || h.Value == 0 ? null
                     : RunCases(vk, log, pipe, 1, [BitConverter.UInt32BitsToSingle((uint)h.Value), BitConverter.UInt32BitsToSingle(VkGlsl.BindlessCap + 7)], 8, "golden bindless past");
        check.Expect(got != null, "the bindless golden's texture, pipeline or readback was refused (logged above)");
        if (got != null)
        {
            check.Expect(got[0] == 1 && got[1] == 1 && got[2] == 1 && got[3] == 1, $"an index inside the table reads its white texel ({got[0]}, {got[1]}, {got[2]}, {got[3]})");
            check.Expect(got[4] == 0 && got[5] == 0 && got[6] == 0 && got[7] == 0, $"an index past the table reads the zero texel ({got[4]}, {got[5]}, {got[6]}, {got[7]})");
        }
        if (h.Value != 0) vk.Unbind(h);
        vk.Destroy(pipe); vk.Destroy(white);
    }
}
