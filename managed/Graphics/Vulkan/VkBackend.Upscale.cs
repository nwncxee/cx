using CX.Rhi;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Is FSR ready on this device for frames up to <paramref name="maxW"/> x <paramref name="maxH"/> upscaled
    /// to <paramref name="displayW"/> x <paramref name="displayH"/>? Null when it is; otherwise why not.</summary>
    internal string? FsrReady(int maxW, int maxH, int displayW, int displayH) => "FSR is not built into this extender";

    private bool Upscale(UpscaleCmd c) => Fault("Upscale: FSR is not built into this extender");
}
