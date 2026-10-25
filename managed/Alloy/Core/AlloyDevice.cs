using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The render seam's devices: GL 4.6 over Alloy's context for the GL passes, and under <c>alloy.backend vk</c>
/// the Vulkan backend that renders the frame into the composite. Made on first use inside the frame's draw (so on
/// the thread holding the context), dropped whole with the context.</summary>
internal static class AlloyDevice
{
    private sealed class DeviceState
    {
        public GlDevice? Device;
        public VkBackend? Vk;
        /// <summary>The driver refused that device on this context (logged); its user stands down without asking again.</summary>
        public bool Refused, VkRefused;
    }

    private static DeviceState _s = new();

    /// <summary>The GL device, or null when this context refused it.</summary>
    internal static IDevice? Get(ExtenderLog log)
    {
        var s = _s;
        if (s.Device == null && !s.Refused) s.Refused = (s.Device = GlDevice.TryCreate(log)) == null;
        return s.Device;
    }

    /// <summary>The Vulkan backend under <c>alloy.backend vk</c>, null otherwise or when it refused (logged).</summary>
    internal static VkBackend? Vulkan(ExtenderLog log)
    {
        var s = _s;
        if (Renderer.OnVulkan && s.Vk == null && !s.VkRefused) s.VkRefused = (s.Vk = VkBackend.TryCreate(log)) == null;
        return s.Vk;
    }

    /// <summary>A texture an unmigrated pass made, under an id a recorded pass can sample and never frees; the
    /// bridge goes when its owner records through the seam. Null when there is no device.</summary>
    internal static ImageId Borrow(uint glName, string label) => _s.Device?.Borrow(glName, label) ?? default;

    /// <summary>The GL name behind <paramref name="i"/>, for an unmigrated reader of a migrated pass's output.</summary>
    internal static uint GlName(ImageId i) => _s.Device?.Name(i) ?? 0;

    /// <summary>Context loss: every handle the GL device issued names a dead GL object, and the composite's imports went with it.</summary>
    internal static void Forget()
    {
        _s.Vk?.Dispose();
        _s = new();
    }
}
