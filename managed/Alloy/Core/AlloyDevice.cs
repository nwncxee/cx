using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The render seam's device: the Vulkan backend that renders the frame into the composite. Made on
/// first use inside the frame's draw, dropped whole with the device.</summary>
internal static class AlloyDevice
{
    private sealed class DeviceState
    {
        public VkBackend? Vk;
        /// <summary>The driver refused the device (logged); its user stands down without asking again.</summary>
        public bool VkRefused;
    }

    private static DeviceState _s = new();

    /// <summary>The Vulkan backend if a frame has made it already; never makes one.</summary>
    internal static VkBackend? VulkanMade => _s.Vk;

    /// <summary>The Vulkan backend on Vulkan, null otherwise or when it refused (logged).</summary>
    internal static VkBackend? Vulkan(ExtenderLog log)
    {
        var s = _s;
        if (Renderer.OnVulkan && s.Vk == null && !s.VkRefused)
        {
            s.VkRefused = (s.Vk = VkBackend.TryCreate(log)) == null;
            if (s.Vk != null && !SsboSlots.CheckDevice(s.Vk.Caps, log)) { s.Vk.Dispose(); s.Vk = null; s.VkRefused = true; }
            // the seam's two image copies against a fixture, before the first pass records one
            s.Vk?.CopyDeviceCheck(log);
            s.Vk?.TallyDeviceCheck(log);
        }
        return s.Vk;
    }

    /// <summary>Device loss: the backend and the composite's images go with it.</summary>
    internal static void Forget()
    {
        _s.Vk?.Dispose();
        _s = new();
    }
}
