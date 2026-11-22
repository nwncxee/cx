using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The image's memory sits on a heap that is not device-local: system RAM the GPU reads across the bus.
    /// An alias member answers for its group's allocation. False for a stale id or the window's own image.</summary>
    internal bool InSystemRam(ImageId i)
        => _images.TryGet(i.Value, out VkImg img) && !img.Window && OffDevice(img.Group != null ? img.Home : img.Alloc);

    /// <summary>The buffer's memory sits on a heap that is not device-local. False for a stale id.</summary>
    internal bool InSystemRam(BufferId b) => _buffers.TryGet(b.Value, out VkBuf buf) && OffDevice(buf.Alloc);

    private bool OffDevice(VmaAllocation a)
    {
        if (a.IsNull) return false;
        VmaAllocationInfo info;
        Vma.vmaGetAllocationInfo(_vma, a, &info);
        VkPhysicalDeviceMemoryProperties mp;
        _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
        uint heap = mp.memoryTypes[(int)info.memoryType].heapIndex;
        return (mp.memoryHeaps[(int)heap].flags & VkMemoryHeapFlags.DeviceLocal) == 0;
    }

    /// <summary>Every device-local memory type is also host-visible: an integrated GPU, whose one heap is system RAM.</summary>
    internal bool UnifiedMemory
    {
        get
        {
            VkPhysicalDeviceMemoryProperties mp;
            _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
            for (int i = 0; i < mp.memoryTypeCount; i++)
            {
                VkMemoryPropertyFlags f = mp.memoryTypes[i].propertyFlags;
                if ((f & VkMemoryPropertyFlags.DeviceLocal) != 0 && (f & VkMemoryPropertyFlags.HostVisible) == 0) return false;
            }
            return true;
        }
    }

    /// <summary>The priority of what draws every frame: render targets, shadow arrays, alias groups, the mesh arena.</summary>
    private const float HighPriority = 1f;

    /// <summary>The priority of a sampled-only image, a texture: the driver pages these out first.</summary>
    private const float LowPriority = 0.25f;

    /// <summary>The memory priority of an image made for <paramref name="use"/>; ignored without the extension.</summary>
    private static float PriorityOf(ImageUse use)
        => (use & (ImageUse.ColorTarget | ImageUse.DepthTarget | ImageUse.Storage)) != 0 ? HighPriority : LowPriority;

    /// <summary>The memory priority of a buffer in <paramref name="m"/>: every device buffer draws every frame.</summary>
    private static float PriorityOf(MemoryKind m) => m == MemoryKind.Device ? HighPriority : LowPriority;

    /// <summary>The move-back check every frame runs once: an image in system RAM goes back to device memory once
    /// use falls under 80% of the budget, at most one try a period. Does nothing yet.</summary>
    internal void MoveBackCheck() { }

    /// <summary>Move-back checks run on this device. Zero until the check exists.</summary>
    internal long MoveBackChecks => 0;

    /// <summary>Moves back to device memory this device has tried. Zero until the check exists.</summary>
    internal int MoveBackTries => 0;
}
