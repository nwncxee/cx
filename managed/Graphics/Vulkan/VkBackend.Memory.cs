using System;
using Vortice.Vulkan;
using static Vortice.Vulkan.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The allocations VMA holds on every heap and their bytes, counted now. Cold: a walk of VMA's blocks.</summary>
    internal (long Count, long Bytes) Allocations
    {
        get
        {
            if (!OnThread("Allocations")) return (0, 0);
            Vma.vmaCalculateStatistics(_vma, out VmaTotalStatistics t);
            return (t.total.statistics.allocationCount, (long)t.total.statistics.allocationBytes);
        }
    }

    /// <summary>The refusals this device has logged or counted.</summary>
    internal int Faults => _faults;

    /// <summary>What the device-local heaps hold, their budget, their size and what Alloy's own allocations in them
    /// take, bytes: the driver's figures with VK_EXT_memory_budget, the allocator's estimate without. One walk, so
    /// the frame line reads every figure of one moment. Any thread that owns the device.</summary>
    internal void DeviceLocalBytes(out long usage, out long budget, out long size, out long held)
    {
        usage = 0; budget = 0; size = 0; held = 0;
        VkPhysicalDeviceMemoryProperties mp;
        _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
        VmaBudget* b = stackalloc VmaBudget[(int)VK_MAX_MEMORY_HEAPS];
        Vma.vmaGetHeapBudgets(_vma, b);
        Vma.vmaCalculateStatistics(_vma, out VmaTotalStatistics t);
        for (int i = 0; i < mp.memoryHeapCount; i++)
        {
            if ((mp.memoryHeaps[i].flags & VkMemoryHeapFlags.DeviceLocal) == 0) continue;
            usage += (long)b[i].usage;
            budget += (long)b[i].budget;
            size += (long)mp.memoryHeaps[i].size;
            held += (long)t.memoryHeap[i].statistics.allocationBytes;
        }
    }

    /// <summary>The <paramref name="n"/> largest allocations by their bytes, each named by its label (an alias group by
    /// its group), largest first. Cold: a walk of every live image and buffer.</summary>
    internal (string Name, long Bytes)[] Largest(int n)
    {
        if (!OnThread("Largest")) return [];
        var top = new (string Name, long Bytes)[n];
        int have = 0;
        void Offer(string name, VmaAllocation a)
        {
            VmaAllocationInfo info;
            Vma.vmaGetAllocationInfo(_vma, a, &info);
            long bytes = (long)info.size;
            if (have == n && bytes <= top[n - 1].Bytes) return;
            int at = have < n ? have++ : n - 1;
            while (at > 0 && top[at - 1].Bytes < bytes) { top[at] = top[at - 1]; at--; }
            top[at] = (name, bytes);
        }
        foreach (VkImg g in _images.Live()) if (g.Group == null) Offer(g.Desc.Label, g.Alloc);
        foreach (AliasGroup g in _alias.Values) Offer("alias " + g.Name, g.Alloc);
        foreach (VkBuf b in _buffers.Live()) Offer(b.Desc.Label, b.Alloc);
        return have == n ? top : top[..have];
    }
}
