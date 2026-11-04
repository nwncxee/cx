using CX.Rhi;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Is FidelityFX DoF ready on this device for <paramref name="w"/> x <paramref name="h"/> frames? Null when
    /// it is; otherwise why not.</summary>
    internal string? DofReady(int w, int h) => "not built";

    /// <summary>Why a device reporting these features cannot run the effect, or null.</summary>
    internal static string? DofRefusalOf(bool waves, bool linearDepth) => null;

    private bool DepthOfField(DofCmd c) => Fault("DepthOfField: not built");
}
