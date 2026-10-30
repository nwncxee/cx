using CX.Framework;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Approved-spec: the shading-rate tile follows the device. Oracles: hand-picked sizes (8 on a device
    /// fixed at 8, 16 on one fixed at 16) and range ends; R8_UINT as vulkan_core.h's 13; the rate attachment's barrier
    /// scope as vulkan_core.h's FRAGMENT_SHADING_RATE_ATTACHMENT bits, by hand.</summary>
    internal static void RateSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        check.Expect(RateTexel(8, 8) == 8, "a device fixed at 8x8 rate texels gets 8-pixel tiles");
        check.Expect(RateTexel(16, 16) == 16, "a device fixed at 16x16 rate texels gets 16-pixel tiles");
        check.Expect(RateTexel(8, 32) == 16, "a device offering 8..32 gets the preferred 16");
        check.Expect(RateTexel(32, 64) == 32, "a device whose smallest texel is 32 gets 32");
        check.Expect(RateTexel(4, 8) == 8, "a device whose largest texel is 8 gets 8");
        check.Expect(RateTexel(32, 16) == 0, "an empty texel range means no rate image");
        check.Expect((int)Of(Format.R8Ui) == 13, "the rate image's format is vulkan_core.h's R8_UINT 13");
        check.Expect(ScopeIs(VkImageLayout.FragmentShadingRateAttachmentOptimalKHR, 0x400000, 0x800000),
            "the rate attachment's barrier scope: FRAGMENT_SHADING_RATE_ATTACHMENT stage 0x400000, its read 0x800000");
        log.Info(check.Ok ? "VkBackend.RateSelfCheck: passed" : "VkBackend.RateSelfCheck: FAILED");
    }
}
