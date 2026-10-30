using System;
using CX.Framework;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Approved-spec: E4, the transfer family a device's table offers. Oracles: hand-written queue-family
    /// tables in the shapes real drivers report -- a discrete card with a copy-only family, an iGPU with one family
    /// that does everything, a copy-only family whose transfer granularity is coarser than a texel.</summary>
    internal static void TransferSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        const VkQueueFlags All = VkQueueFlags.Graphics | VkQueueFlags.Compute | VkQueueFlags.Transfer;
        VkExtent3D texel = new() { width = 1, height = 1, depth = 1 }, coarse = new() { width = 16, height = 16, depth = 8 };
        VkQueueFamilyProperties[] discrete =
        [
            new() { queueFlags = All, queueCount = 16, minImageTransferGranularity = texel },
            new() { queueFlags = VkQueueFlags.Transfer, queueCount = 2, minImageTransferGranularity = texel },
            new() { queueFlags = VkQueueFlags.Compute | VkQueueFlags.Transfer, queueCount = 8, minImageTransferGranularity = texel },
        ];
        check.Expect(TransferFamily(discrete) == 1, "a card with a copy-only family 1 opens its transfer queue there");
        VkQueueFamilyProperties[] igpu = [new() { queueFlags = All, queueCount = 1, minImageTransferGranularity = texel }];
        check.Expect(TransferFamily(igpu) == -1, "E4: an iGPU whose one family draws, computes and copies opens no transfer queue");
        VkQueueFamilyProperties[] blocky =
        [
            new() { queueFlags = All, queueCount = 1, minImageTransferGranularity = texel },
            new() { queueFlags = VkQueueFlags.Transfer, queueCount = 1, minImageTransferGranularity = coarse },
        ];
        check.Expect(TransferFamily(blocky) == -1, "a copy-only family that cannot copy a single texel is passed over");
        VkQueueFamilyProperties[] empty =
        [
            new() { queueFlags = All, queueCount = 1, minImageTransferGranularity = texel },
            new() { queueFlags = VkQueueFlags.Transfer, queueCount = 0, minImageTransferGranularity = texel },
        ];
        check.Expect(TransferFamily(empty) == -1, "a copy-only family with no queues is passed over");
        log.Info(check.Ok ? "VkBackend.TransferSelfCheck: passed" : "VkBackend.TransferSelfCheck: FAILED");
    }
}
