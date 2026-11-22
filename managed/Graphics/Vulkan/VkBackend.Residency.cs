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

    /// <summary>The move-back check every frame runs once: an image in system RAM goes back to device memory once
    /// use falls under 80% of the budget, at most one try a period. Does nothing yet.</summary>
    internal void MoveBackCheck() { }

    /// <summary>Move-back checks run on this device. Zero until the check exists.</summary>
    internal long MoveBackChecks => 0;

    /// <summary>Moves back to device memory this device has tried. Zero until the check exists.</summary>
    internal int MoveBackTries => 0;
}
