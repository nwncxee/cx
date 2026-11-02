using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The by-feature pipeline tally: a pipeline is counted under the feature its label names, under the hold or on the
/// frame after it, a label naming none is counted under none, and a new hold starts the counts again. Oracle: the
/// feature line's words, held to the pipelines this golden builds by hand.
/// </summary>
internal static partial class GpuGolden
{
    private static void FeatureTallySelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        if (!BlendStages(vk, log, out ShaderId vs, out ShaderId fs)) { check.Expect(false, "the feature tally golden's stages were refused (logged above)"); return; }
        bool held = vk.UnderHold;
        vk.UnderHold = false;
        vk.UnderHold = true;
        PipelineId grass = BlendPipeline(vk, vs, fs, "vk grass golden", Format.Rg16F, BlendFactor.One, BlendFactor.One);
        vk.UnderHold = false;
        PipelineId probe = BlendPipeline(vk, vs, fs, "vk probe golden", Format.R32F, BlendFactor.One, BlendFactor.One);
        PipelineId other = BlendPipeline(vk, vs, fs, "golden no feature", Format.R16F, BlendFactor.One, BlendFactor.One);
        string line = vk.FeatureLine;
        check.Expect(!grass.IsNull && !probe.IsNull && !other.IsNull, "a feature tally golden pipeline was refused (logged above)");
        check.Expect(line.Contains(": grass 1 under the hold, 0 on the frame;"), $"a grass pipeline under the hold counts as grass's, under it: \"{line}\"");
        check.Expect(line.Contains("; environment probe 0 under the hold, 1 on the frame;"),
            $"a probe pipeline past the hold counts as the probe's, on the frame: \"{line}\"");
        check.Expect(line.Contains("; particles 0 under the hold, 0 on the frame;") && line.Contains("; CX windows 0 under the hold, 0 on the frame"),
            $"a pipeline whose label names no feature counts under none: \"{line}\"");
        vk.UnderHold = true;
        string again = vk.FeatureLine;
        check.Expect(again.Contains(": grass 0 under the hold, 0 on the frame;") && again.Contains("; environment probe 0 under the hold, 0 on the frame;"),
            $"a new hold starts the counts again: \"{again}\"");
        vk.Destroy(grass); vk.Destroy(probe); vk.Destroy(other); vk.Destroy(vs); vk.Destroy(fs);
        vk.UnderHold = held;
        log.Info($"GpuGolden: feature tally -- \"{line}\", then \"{again}\"");
    }
}
