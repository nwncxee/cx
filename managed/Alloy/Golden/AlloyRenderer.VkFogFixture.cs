using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The FSR sky golden's fixture: a summed fog volume of the golden's own, bound where a stage reads <c>uVFogVol</c>.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Bind <paramref name="total"/> as this frame's built fog volume on <paramref name="vk"/>; the null id
    /// starts the fog's device state over.</summary>
    internal static void VkFogFixture(VkBackend vk, ImageId total)
        => _vkFog = total.IsNull ? new VkFogState() : new VkFogState { Dev = vk, Total = total, Built = true };

    /// <summary>The colour pass's device state for a <paramref name="w"/> x <paramref name="h"/> frame, its stand-in
    /// images among it, as a frame makes it; false when refused (logged).</summary>
    internal static bool VkFixtureEnsure(VkBackend vk, int w, int h, ExtenderLog log) => VkEnsure(vk, w, h, log);
}
