using CX.Framework;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Approved-spec: a limit the driver reports as unbounded reads as the largest int, never negative.
    /// Oracles: an AMD Windows table, whose descriptor counts are UINT32_MAX, and a plain one, both by hand.</summary>
    internal static void CapsSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var amd = new VkPhysicalDeviceLimits
        {
            maxImageDimension2D = 16384, maxImageArrayLayers = 8192, maxColorAttachments = 8,
            maxPerStageDescriptorSampledImages = uint.MaxValue, maxDescriptorSetSampledImages = uint.MaxValue,
            maxPerStageDescriptorStorageBuffers = uint.MaxValue, minUniformBufferOffsetAlignment = 16, minStorageBufferOffsetAlignment = 4,
        };
        DeviceCaps a = CapsOf(amd, aniso: false);
        check.Expect(a.MaxStorageBindings == int.MaxValue, "UINT32_MAX storage buffers a stage reads as int.MaxValue");
        check.Expect(a.MaxVertexStorageBlocks == int.MaxValue, "...and so do its vertex-stage storage blocks");
        check.Expect(a.MaxFragmentTextureUnits == int.MaxValue, "UINT32_MAX sampled images a stage reads as int.MaxValue");
        check.Expect(a.MaxCombinedTextureUnits == int.MaxValue, "UINT32_MAX sampled images a set reads as int.MaxValue");
        check.Expect(a.MaxTextureSize == 16384 && a.UniformAlign == 16 && a.StorageAlign == 4, "bounded limits pass through");
        var plain = new VkPhysicalDeviceLimits { maxPerStageDescriptorStorageBuffers = 31, maxPerStageDescriptorSampledImages = 64 };
        DeviceCaps p = CapsOf(plain, aniso: false);
        check.Expect(p.MaxStorageBindings == 31 && p.MaxFragmentTextureUnits == 64, "a plain table's counts pass through");
        log.Info(check.Ok ? "VkBackend.CapsSelfCheck: passed" : "VkBackend.CapsSelfCheck: FAILED");
    }
}
