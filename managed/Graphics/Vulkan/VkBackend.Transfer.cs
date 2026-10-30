using System;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The first family of <paramref name="families"/> that copies without drawing or computing, or -1.</summary>
    internal static int TransferFamily(ReadOnlySpan<VkQueueFamilyProperties> families) => -1;

    /// <summary>The device has a transfer queue the first fills go out on.</summary>
    internal bool HasTransfer => true;

    /// <summary>Copies sent on the transfer queue so far.</summary>
    internal long Shipped => 0;
}
