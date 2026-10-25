using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>The render seam's device over Alloy's 4.6 context, shared by every migrated pass: made on first use
/// inside the frame's draw (so on the thread holding the context), dropped whole with the context.</summary>
internal static class AlloyDevice
{
    private sealed class DeviceState
    {
        public GlDevice? Device;
        /// <summary>The driver refused the device on this context (logged); a pass stands down without asking again.</summary>
        public bool Refused;
    }

    private static DeviceState _s = new();

    /// <summary>The device, or null when this context refused it.</summary>
    internal static GlDevice? Get(ExtenderLog log)
    {
        var s = _s;
        if (s.Device == null && !s.Refused) s.Refused = (s.Device = GlDevice.TryCreate(log)) == null;
        return s.Device;
    }

    /// <summary>Context loss: every handle the device issued names a dead GL object.</summary>
    internal static void Forget() => _s = new();
}
