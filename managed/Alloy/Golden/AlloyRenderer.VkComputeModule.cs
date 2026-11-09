using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>A translated compute stage is built, its pipeline destroyed, and the device asked what it still
    /// holds. Oracle: the module count read off the device before the build.</summary>
    internal static void VkComputeModuleCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        int before = vk.LiveShaders;
        VkDefStage? st = VkDefBuild(vk, DeferredLights.ShdCs, null, "golden compute module", log, null, out _);
        check.Expect(st != null, "the deferred walk did not build (logged above)");
        if (st == null) return;
        vk.Destroy(st.Pipe);
        check.Expect(vk.LiveShaders == before,
          $"the build left {vk.LiveShaders - before} shader module(s) on the device, not 0");
    }
}
