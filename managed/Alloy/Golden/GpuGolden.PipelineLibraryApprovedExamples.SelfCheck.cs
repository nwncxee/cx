using System;
using System.Globalization;
using System.Text.RegularExpressions;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Pipeline libraries (approved-spec): the device's pipeline path named once at open; a first-use pipeline of stages
/// already in use linked within 2 ms and drawing that frame; the worker's optimised pipeline taking over and drawing
/// the same. Oracle: the fixture's answers worked by hand, the open line's words, and the 2 ms bound the example sets.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The period line's slowest build on the frame, in milliseconds.</summary>
    private static readonly Regex TallyWorst = new(@"worst (\d+[.,]\d+) ms", RegexOptions.CultureInvariant);

    /// <summary>How long the golden waits for the worker's optimised pipeline before calling it missing.</summary>
    private const int OptimisedWaitMs = 10000;

    private static void PipelineLibrarySelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        if (!BlendStages(vk, log, out ShaderId vs, out ShaderId fs)) { check.Expect(false, "the library golden's stages were refused (logged above)"); return; }
        bool held = vk.UnderHold, libraries = vk.Libraries;
        vk.UnderHold = false;
        // Given a device with the graphics pipeline library (an RTX 3070)
        // When Alloy opens
        // Then cx.log says once that first-use pipelines are linked from libraries
        check.Expect(VkBackend.PipelinePathLine(true) == "first-use pipelines are linked from libraries",
            $"the open line names the library path: \"{VkBackend.PipelinePathLine(true)}\"");
        // Given a device without it
        // When Alloy opens
        // Then cx.log says once that pipelines are built whole, and Alloy draws as before
        check.Expect(VkBackend.PipelinePathLine(false) == "pipelines are built whole",
            $"the open line names the whole-pipeline path: \"{VkBackend.PipelinePathLine(false)}\"");
        vk.Libraries = false;
        PipelineId whole = BlendPipeline(vk, vs, fs, "golden whole", Format.Rgba16F, BlendFactor.One, BlendFactor.One);
        float wholeRed = BlendRed(vk, log, whole, Format.Rgba16F, "golden whole");
        check.Expect(MathF.Abs(wholeRed - 0.75f) < 1e-3f, $"a whole pipeline drew {wholeRed}, by hand 0.75 (one plus one)");
        vk.Destroy(whole);
        vk.Libraries = libraries;
        if (!libraries) check.Skip("this device has no graphics pipeline library: the linked cases cannot run here");
        else LinkedCases(vk, check, log, vs, fs);
        vk.Destroy(vs); vk.Destroy(fs);
        vk.UnderHold = held;
    }

    /// <summary>The two cases that need the library path: a fast link on the frame, then the worker's swap.</summary>
    private static void LinkedCases(VkBackend vk, Checker check, ExtenderLog log, ShaderId vs, ShaderId fs)
    {
        PipelineId warm = BlendPipeline(vk, vs, fs, "golden linked warm", Format.Rgba16F, BlendFactor.One, BlendFactor.One);
        check.Expect(!warm.IsNull, "the stages' first pipeline was refused (logged above)");
        vk.PipelinePeriod();
        // Given a device with the library and a material drawn for the first time mid-play
        // When its first frame draws
        // Then that frame spends no more than 2 ms building its pipeline, and the material is on screen that frame
        PipelineId fresh = BlendPipeline(vk, vs, fs, "golden linked fresh", Format.Rgba8, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha);
        string line = vk.PipelinePeriod();
        float linked = BlendRed(vk, log, fresh, Format.Rgba8, "golden linked fresh");
        double worst = TallyWorst.Match(line) is { Success: true } w ? double.Parse(w.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture) : double.NaN;
        check.Expect(TallyCount(line) == 1 && worst <= 2.0, $"a first-use pipeline of stages in use was linked within 2 ms: \"{line}\"");
        check.Expect(MathF.Abs(linked - 0.375f) < 1.5f / 255, $"...and drew that frame: {linked}, by hand 0.375 (half of 0.5 over half of 0.25)");
        check.Expect(!vk.Optimised(fresh), "a pipeline linked this frame is not yet the worker's optimised one");
        // Given that linked pipeline
        // When the worker finishes the optimised one
        // Then the material draws with the optimised pipeline from the next frame, and looks the same
        int waited = 0;
        while (!vk.Optimised(fresh) && waited < OptimisedWaitMs)
        {
            vk.SwapOptimised();
            if (vk.Optimised(fresh)) break;
            BlockingWait.Sleep(5, "the pipeline golden waits for the worker's optimised pipeline");
            waited += 5;
        }
        check.Expect(vk.Optimised(fresh), $"the worker's optimised pipeline took over within {OptimisedWaitMs} ms");
        float optimised = BlendRed(vk, log, fresh, Format.Rgba8, "golden linked optimised");
        check.Expect(optimised == linked, $"the optimised pipeline drew {optimised}, the linked one {linked}: they must look the same");
        vk.Destroy(fresh); vk.Destroy(warm);
        log.Info($"GpuGolden: pipeline library -- \"{line}\", optimised after {waited} ms");
    }
}
