using System;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>A pass's GPU buffer kept at least as long as its frame asks, remade larger when it is not. Each buffer
/// carries its own size, so a buffer made late is never held to the size another was given.</summary>
internal static class HeldBuffer
{
    /// <summary>Hold <paramref name="id"/> at least <paramref name="bytes"/> long, <paramref name="cap"/> its own size;
    /// true when it was made anew this call.</summary>
    internal static bool Grow(VkBackend vk, ref BufferId id, ref long cap, long bytes, BufferUse use, MemoryKind kind, string label)
    {
        if (!id.IsNull && bytes <= cap) return false;
        vk.Destroy(id);
        cap = Math.Max(bytes, 4);
        id = vk.CreateBuffer(new BufferDesc(cap, use, kind, label));
        return !id.IsNull;
    }

    /// <summary><see cref="Grow"/> on device memory, zeroed when new: an indirect command or count read out of
    /// uninitialised memory can hang the GPU.</summary>
    internal static void Zeroed(VkBackend vk, CommandList cl, ref BufferId id, ref long cap, long bytes, BufferUse use, string label)
    {
        if (Grow(vk, ref id, ref cap, bytes, use, MemoryKind.Device, label)) cl.ZeroBuffer(id);
    }
}
