using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Dynamic blend (approved-spec): one set of stages drawn under eight blend modes builds one pipeline where the
/// device makes blend dynamic, and eight where it does not, and every mode draws the same texel either way. Oracle:
/// each mode's answer worked by hand from the fixture's 0.5 (alpha 0.5) over 0.25 (alpha 1).
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The eight modes and the red each leaves: src * Fs + dst * Fd with src 0.5, dst 0.25, src alpha 0.5,
    /// dst alpha 1 -- by hand, not from any blend code.</summary>
    private static readonly (BlendFactor Src, BlendFactor Dst, float Red)[] BlendModes =
    [
        (BlendFactor.One, BlendFactor.One, 0.75f),
        (BlendFactor.Zero, BlendFactor.One, 0.25f),
        (BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, 0.375f),
        (BlendFactor.One, BlendFactor.Zero, 0.5f),
        (BlendFactor.DstColor, BlendFactor.Zero, 0.125f),
        (BlendFactor.Zero, BlendFactor.SrcColor, 0.125f),
        (BlendFactor.SrcAlpha, BlendFactor.One, 0.5f),
        (BlendFactor.OneMinusDstAlpha, BlendFactor.One, 0.25f),
    ];

    private static void DynamicBlendSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        if (!BlendStages(vk, log, out ShaderId vs, out ShaderId fs)) { check.Expect(false, "the blend golden's stages were refused (logged above)"); return; }
        bool held = vk.UnderHold, dynamic = vk.DynamicBlend;
        vk.UnderHold = false;
        // Given a device with dynamic blend state (an RTX 3070)
        // When one material is drawn under eight different blend modes
        // Then cx.log counts one pipeline for it, not eight
        if (!dynamic) check.Skip("this device has no dynamic blend state: the one-pipeline case cannot run here");
        else BlendEight(vk, check, log, vs, fs, 1, "dynamic");
        // Given a device without dynamic blend state
        // When the same material is drawn under eight blend modes
        // Then it draws exactly as before, cx.log counts eight pipelines, and the log says once which blend path the device took
        vk.DynamicBlend = false;
        BlendEight(vk, check, log, vs, fs, BlendModes.Length, "baked");
        vk.DynamicBlend = dynamic;
        check.Expect(VkBackend.BlendPathLine(true) == "blend is dynamic state, one pipeline for every blend mode",
            $"the open line names the dynamic blend path: \"{VkBackend.BlendPathLine(true)}\"");
        check.Expect(VkBackend.BlendPathLine(false) == "blend is baked into each pipeline",
            $"the open line names the baked blend path: \"{VkBackend.BlendPathLine(false)}\"");
        vk.Destroy(vs); vk.Destroy(fs);
        vk.UnderHold = held;
    }

    /// <summary>The fixture under every <see cref="BlendModes"/> entry: <paramref name="built"/> pipelines in the period
    /// line, and each mode's texel its hand-worked red.</summary>
    private static void BlendEight(VkBackend vk, Checker check, ExtenderLog log, ShaderId vs, ShaderId fs, int built, string path)
    {
        vk.PipelinePeriod();
        Span<PipelineId> pipes = stackalloc PipelineId[BlendModes.Length];
        for (int i = 0; i < BlendModes.Length; i++)
            pipes[i] = BlendPipeline(vk, vs, fs, $"golden blend {path}", Format.Rgba16F, BlendModes[i].Src, BlendModes[i].Dst);
        string line = vk.PipelinePeriod();
        check.Expect(TallyCount(line) == built, $"eight blend modes on the {path} path built {built} pipeline(s): \"{line}\"");
        for (int i = 0; i < BlendModes.Length; i++)
        {
            float red = BlendRed(vk, log, pipes[i], Format.Rgba16F, $"golden blend {path} {i}");
            check.Expect(MathF.Abs(red - BlendModes[i].Red) < 1e-3f,
                $"{BlendModes[i].Src}/{BlendModes[i].Dst} on the {path} path drew {red}, by hand {BlendModes[i].Red}");
        }
        foreach (PipelineId p in pipes) vk.Destroy(p);
        log.Info($"GpuGolden: dynamic blend, {path} path -- \"{line}\"");
    }
}
