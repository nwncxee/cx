using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The render seam's device, shared by every migrated pass: GL 4.6 over Alloy's context, or Vulkan inside it
/// under <c>alloy.backend vk</c>. Made on first use inside the frame's draw (so on the thread holding the context),
/// dropped whole with the context.</summary>
internal static class AlloyDevice
{
    private sealed class DeviceState
    {
        public IDevice? Device;
        public GlDevice? Gl;
        public VkBackend? Vk;
        /// <summary>The backend refused the device on this context (logged); a pass stands down without asking again.</summary>
        public bool Refused;
    }

    private static DeviceState _s = new();

    /// <summary>The device, or null when this context refused it.</summary>
    internal static IDevice? Get(ExtenderLog log)
    {
        var s = _s;
        if (s.Device == null && !s.Refused)
        {
            s.Device = Renderer.OnVulkan ? s.Vk = VkBackend.TryCreate(log) : s.Gl = GlDevice.TryCreate(log);
            s.Refused = s.Device == null;
        }
        return s.Device;
    }

    /// <summary>Can a list submit command by command? GL only: a Vulkan submit is a whole command buffer.</summary>
    internal static bool Immediate => _s.Gl != null;

    /// <summary>A texture an unmigrated pass made, under an id a recorded pass can sample and never frees; the
    /// bridge goes when its owner records through the seam. Null when there is no device.</summary>
    internal static ImageId Borrow(uint glName, string label)
        => _s.Gl?.Borrow(glName, label) ?? _s.Vk?.Borrow(glName, label) ?? default;

    /// <summary>The GL name behind <paramref name="i"/>, for an unmigrated reader of a migrated pass's output.</summary>
    internal static uint GlName(ImageId i) => _s.Gl?.Name(i) ?? _s.Vk?.GlName(i) ?? 0;

    /// <summary>Context loss: every handle the device issued names a dead GL object, and a Vulkan device's imports went with it.</summary>
    internal static void Forget()
    {
        _s.Vk?.Dispose();
        _s = new();
    }
}
