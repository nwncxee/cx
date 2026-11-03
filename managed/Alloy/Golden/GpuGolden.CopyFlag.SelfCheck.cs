using System;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// An image an undeclared copy names is refused at each entry point that would transfer it -- a recorded copy out of
/// it, and the mip chain asked for off the device -- and the refusal names the image. Oracles: the hand-written label
/// the image was made with, and the usage it was not made for.
/// </summary>
internal static partial class GpuGolden
{
    private const string CopyFlagLabel = "golden copyless image";

    /// <summary>Holds that a copy out of an image made without the copy-source flag is refused by the image's own
    /// label at both entry points; turns red when either one lets it through to the device instead.</summary>
    private static void CopyFlagSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId src = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 2, 2, 1, 2, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst, CopyFlagLabel));
        ImageId dst = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, 2, 2, 1, 1, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst, "golden copy destination"));
        try
        {
            check.Expect(!src.IsNull && !dst.IsNull, "the copyless image or its destination was refused (logged above)");
            if (src.IsNull || dst.IsNull) return;
            int faults = vk.Faults;
            var cl = new CommandList("golden undeclared copy", 1 << 10, log);
            cl.CopyImage(new CopyImageCmd(src, new ImageRegion(0, 0, 0, 0), dst, new ImageRegion(0, 0, 0, 0), 2, 2, 1));
            check.Expect(!vk.Submit(cl), "a recorded copy out of an image made without the copy-source flag was accepted");
            check.Expect(vk.Faults == faults + 1, $"the recorded copy logged {vk.Faults - faults} refusals, not 1");
            check.Expect(vk.LastFault.Contains(CopyFlagLabel, StringComparison.Ordinal)
                      && vk.LastFault.Contains("copy source", StringComparison.Ordinal),
                         $"the recorded copy's refusal does not name the image and the usage it lacks: \"{vk.LastFault}\"");
            faults = vk.Faults;
            vk.GenerateMips(src);
            check.Expect(vk.Faults == faults + 1, $"the mip chain off the device logged {vk.Faults - faults} refusals, not 1");
            check.Expect(vk.LastFault.Contains(CopyFlagLabel, StringComparison.Ordinal)
                      && vk.LastFault.Contains("copy source", StringComparison.Ordinal),
                         $"the mip chain's refusal does not name the image and the usage it lacks: \"{vk.LastFault}\"");
        }
        finally
        {
            vk.Destroy(dst);
            vk.Destroy(src);
        }
    }
}
