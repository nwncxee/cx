using System;
using CX.Framework;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The device gate's approved examples (approved-spec, E1): a Vulkan 1.3 device lacking depth clip control
/// runs Alloy; one below 1.3 is refused for its version. Oracle: the extension names and versions written by hand.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>Every extension the gate could ask about but the one named.</summary>
    private static Func<string, bool> AllBut(string missing) => n => n != missing;

    private static void DepthGateSelfCheck(Checker check)
    {
        string? e1 = Gate(new VkVersion(1, 3, 0), AllBut("VK_EXT_depth_clip_control"));
        check.Expect(e1 == null, $"E1: a Vulkan 1.3 GPU lacking VK_EXT_depth_clip_control is refused ({e1})");
        string? below = Gate(new VkVersion(1, 2, 0), AllBut("none"));
        check.Expect(below != null && below.Contains("1.3", StringComparison.Ordinal),
            $"E1 boundary: a Vulkan 1.2 GPU is not refused for its version ({below ?? "not refused"})");
    }
}
