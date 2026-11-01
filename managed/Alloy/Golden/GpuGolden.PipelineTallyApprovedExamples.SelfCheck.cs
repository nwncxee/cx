using System;
using System.Globalization;
using System.Text.RegularExpressions;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The pipeline tally (approved-spec): a pipeline the frame builds past the loading screen is counted, with its
/// milliseconds, in the period line; a period that built none says zero. Oracle: the period line's own words and
/// numbers, held to the one pipeline this golden builds by hand.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The period line's count and milliseconds of pipelines built on the frame.</summary>
    private static readonly Regex TallyBuilt = new(@"(\d+) pipelines? built on the frame in (\d+[.,]\d+) ms", RegexOptions.CultureInvariant);

    /// <summary>The count <see cref="TallyBuilt"/> reads in <paramref name="line"/>, -1 when the line does not say it.</summary>
    private static int TallyCount(string line) => TallyBuilt.Match(line) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;

    private static void PipelineTallySelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        if (!BlendStages(vk, log, out ShaderId vs, out ShaderId fs)) { check.Expect(false, "the tally golden's stages were refused (logged above)"); return; }
        bool held = vk.UnderHold;
        vk.UnderHold = false;
        vk.PipelinePeriod();
        // Given a session past the loading screen
        // When a draw needs a pipeline nobody built yet
        // Then cx.log's next period line counts it, with the milliseconds the frame spent building it
        PipelineId pipe = BlendPipeline(vk, vs, fs, "golden tally", Format.Rgba16F, BlendFactor.DstColor, BlendFactor.SrcColor);
        string line = vk.PipelinePeriod();
        Match m = TallyBuilt.Match(line);
        check.Expect(!pipe.IsNull, "the tally golden's pipeline was refused (logged above)");
        check.Expect(TallyCount(line) == 1, $"one new pipeline reads as one built on the frame: \"{line}\"");
        check.Expect(m.Success && double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture) > 0,
            $"...with the milliseconds it took, above zero: \"{line}\"");
        // Given a period in which every draw found its pipeline built
        // When the period line is written
        // Then it says zero pipelines were built on the frame
        string quiet = vk.PipelinePeriod();
        check.Expect(TallyCount(quiet) == 0, $"a period with nothing built says zero: \"{quiet}\"");
        vk.Destroy(pipe); vk.Destroy(vs); vk.Destroy(fs);
        vk.UnderHold = held;
        log.Info($"GpuGolden: pipeline tally -- \"{line}\", then \"{quiet}\"");
    }
}
