using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// A list naming an image destroyed after it was recorded is refused, and the refusal names that image.
/// Oracle: the hand-written label the image was made with.
/// </summary>
internal static partial class GpuGolden
{
    private const string StaleLabel = "golden destroyed target";

    /// <summary>Holds that a pass recorded onto an image, then the image destroyed before the submit, is refused by
    /// the image's own label; turns red when the refusal says only "stale image".</summary>
    private static void StaleNamedSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId a = Target(vk, 4, StaleLabel, null);
        check.Expect(!a.IsNull, "the stale-name target was refused (logged above)");
        if (a.IsNull) return;
        int faults = vk.Faults;
        var cl = new CommandList("golden stale name", 1 << 10, log);
        ClearInto(cl, a, new Vector4(1, 0, 0, 1));
        vk.Destroy(a);
        check.Expect(!vk.Submit(cl), "a list onto an image destroyed after it was recorded was accepted");
        check.Expect(vk.Faults == faults + 1, $"the stale list logged {vk.Faults - faults} refusals, not 1");
        check.Expect(vk.LastFault.Contains(StaleLabel, StringComparison.Ordinal),
                     $"the stale-image refusal does not name the image: \"{vk.LastFault}\"");
    }
}
