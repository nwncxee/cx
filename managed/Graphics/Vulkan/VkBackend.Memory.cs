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

    /// <summary>The device has a lazily allocated memory type, so an <see cref="CX.Rhi.ImageUse.Transient"/> target can
    /// be backed by none; false on a desktop GPU, where such a target takes ordinary device memory.</summary>
    private bool HasLazyMemory()
    {
        VkPhysicalDeviceMemoryProperties mp;
        _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
        for (int i = 0; i < mp.memoryTypeCount; i++)
            if ((mp.memoryTypes[i].propertyFlags & VkMemoryPropertyFlags.LazilyAllocated) != 0) return true;
        return false;
    }

    /// <summary>The refusals this device has logged or counted.</summary>
    internal int Faults => _faults;

    /// <summary>The words the last refusal put in cx.log (or would have, past the logged few).</summary>
    internal string LastFault => _lastFault;

    /// <summary>The last refusal was for want of heap room, so the same allocation may fit once room returns.</summary>
    internal bool LastRefusalWasRoom => _lastNoRoom;

    /// <summary>Shader modules this device still holds. A module is only wanted while a pipeline is being built,
    /// so a build that leaves one behind leaves driver memory with no owner and nothing in the log.</summary>
    internal int LiveShaders => _shaders.Count;

    /// <summary>What the device-local heaps hold, their budget, their size and what Alloy's own allocations in them
    /// take, bytes, as the allocator sees them, so under <c>alloy.vramcap</c> the size and budget are the cap's. One
    /// walk, so the frame line reads every figure of one moment. Any thread that owns the device.</summary>
    internal void DeviceLocalBytes(out long usage, out long budget, out long size, out long held)
    {
        usage = 0; budget = 0; size = 0; held = 0;
        VkPhysicalDeviceMemoryProperties* props;
        Vma.vmaGetMemoryProperties(_vma, &props);
        ref readonly VkPhysicalDeviceMemoryProperties mp = ref *props;
        VmaBudget* b = stackalloc VmaBudget[(int)VK_MAX_MEMORY_HEAPS];
        Vma.vmaGetHeapBudgets(_vma, b);
        Vma.vmaCalculateStatistics(_vma, out VmaTotalStatistics t);
        for (int i = 0; i < mp.memoryHeapCount; i++)
        {
            if (!DeviceLocal(mp, i)) continue;
            usage += (long)b[i].usage;
            budget += (long)b[i].budget;
            size += (long)mp.memoryHeaps[i].size;
            held += (long)t.memoryHeap[i].statistics.allocationBytes;
        }
    }

    /// <summary>The marker every cx.log line about <c>alloy.vramcap</c> opens with.</summary>
    internal const string CapPrefix = "[vram] cap ";

    /// <summary>The marker of the line that says <c>alloy.vramcap</c> caps the device-local heaps at <paramref name="mib"/>.</summary>
    internal static string CapTag(long mib) => $"{CapPrefix}{mib}MiB";

    /// <summary>The marker of the line that says <c>alloy.vramcap</c> is past the card's size and caps nothing.</summary>
    internal const string NoCapTag = CapPrefix + "none";

    /// <summary>The marker of the creation line that says memory priorities are on: render targets, shadows and the
    /// mesh arena high, textures low.</summary>
    internal const string PriorityOnTag = "[vram] priority high=targets,shadows,arena low=textures";

    /// <summary>The marker of the creation line that says the driver lacks <c>VK_EXT_memory_priority</c>.</summary>
    internal const string PriorityAbsentTag = "[vram] priority absent";

    /// <summary>The marker a refusal carries when no heap, device-local or system RAM, has room for the allocation.</summary>
    internal const string NoRoomTag = "[vram] no heap room";

    /// <summary>Fills <paramref name="limits"/> so the device-local heaps together hold <paramref name="capMib"/> MiB, each
    /// its share by size, whole MiB, and every other heap unlimited; false, with no limit set, for a cap of 0 or one
    /// the card does not exceed. Says once in cx.log which it was.</summary>
    private bool HeapLimits(long capMib, ulong* limits)
    {
        if (capMib <= 0) return false;
        VkPhysicalDeviceMemoryProperties mp;
        _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
        long card = 0;
        int last = -1;
        for (int i = 0; i < mp.memoryHeapCount; i++)
            if (DeviceLocal(mp, i)) { card += (long)mp.memoryHeaps[i].size; last = i; }
        long cap = capMib << 20;
        if (last < 0 || cap >= card)
        {
            _log.Always($"VkBackend: {NoCapTag} -- alloy.vramcap {capMib}MiB is not below the card's {card >> 20}MiB device-local, so nothing is capped");
            return false;
        }
        long given = 0;
        for (int i = 0; i < (int)VK_MAX_MEMORY_HEAPS; i++)
        {
            limits[i] = VK_WHOLE_SIZE;
            if (i >= mp.memoryHeapCount || !DeviceLocal(mp, i)) continue;
            long share = i == last ? cap - given : (long)((Int128)cap * (long)mp.memoryHeaps[i].size / card) >> 20 << 20;
            limits[i] = (ulong)share;
            given += share;
        }
        _log.Always($"VkBackend: {CapTag(capMib)} -- alloy.vramcap holds the device-local heaps to {capMib}MiB of the card's {card >> 20}MiB");
        return true;
    }

    /// <summary>The size of each device-local heap in bytes, in heap order, as the allocator sees it: under
    /// <c>alloy.vramcap</c>, each heap's share of the cap.</summary>
    internal long[] DeviceLocalHeapSizes()
    {
        VkPhysicalDeviceMemoryProperties* props;
        Vma.vmaGetMemoryProperties(_vma, &props);
        var sizes = new System.Collections.Generic.List<long>();
        for (int i = 0; i < props->memoryHeapCount; i++)
            if (DeviceLocal(*props, i)) sizes.Add((long)props->memoryHeaps[i].size);
        return sizes.ToArray();
    }

    /// <summary>The largest device-local heap as the allocator sees it: its size (its share of <c>alloy.vramcap</c>), the
    /// blocks VMA took from it and the bytes allocated inside them. Images and arena buffers land there.</summary>
    internal void LargestHeap(out long size, out long blocks, out long held)
    {
        VkPhysicalDeviceMemoryProperties* props;
        Vma.vmaGetMemoryProperties(_vma, &props);
        Vma.vmaCalculateStatistics(_vma, out VmaTotalStatistics t);
        size = 0; blocks = 0; held = 0;
        for (int i = 0; i < props->memoryHeapCount; i++)
        {
            if (!DeviceLocal(*props, i) || (long)props->memoryHeaps[i].size <= size) continue;
            size = (long)props->memoryHeaps[i].size;
            blocks = (long)t.memoryHeap[i].statistics.blockBytes;
            held = (long)t.memoryHeap[i].statistics.allocationBytes;
        }
    }

    /// <summary>Bytes VMA holds on heaps that are not device-local: system RAM the frame's staging and mapped buffers
    /// keep resident, which no VRAM figure counts.</summary>
    internal long HostHeldBytes() => BlockBytes(deviceLocal: false);

    /// <summary>Bytes of device-local blocks VMA keeps from the driver, used or not: past the allocations they hold,
    /// this is memory a freed resource left inside a block that is still partly in use.</summary>
    internal long DeviceBlockBytes() => BlockBytes(deviceLocal: true);

    /// <summary>Bytes of the blocks VMA keeps from the driver on the heaps that are, or are not, device-local.</summary>
    private long BlockBytes(bool deviceLocal)
    {
        VkPhysicalDeviceMemoryProperties mp;
        _vki.vkGetPhysicalDeviceMemoryProperties(_phys, &mp);
        Vma.vmaCalculateStatistics(_vma, out VmaTotalStatistics t);
        long blocks = 0;
        for (int i = 0; i < mp.memoryHeapCount; i++)
            if (DeviceLocal(mp, i) == deviceLocal) blocks += (long)t.memoryHeap[i].statistics.blockBytes;
        return blocks;
    }

    /// <summary>Heap <paramref name="heap"/> of <paramref name="mp"/> is device-local.</summary>
    private static bool DeviceLocal(in VkPhysicalDeviceMemoryProperties mp, int heap)
        => (mp.memoryHeaps[heap].flags & VkMemoryHeapFlags.DeviceLocal) != 0;

    /// <summary>Every live allocation summed by its label, the digits taken out and every world texture one row,
    /// largest first, past <paramref name="n"/> folded into one row. Cold: a walk of every live image and buffer.</summary>
    internal string Owners(int n)
    {
        if (!OnThread("Owners")) return "";
        var sum = new System.Collections.Generic.Dictionary<string, (int Count, long Bytes)>();
        void Add(string label, VmaAllocation a)
        {
            VmaAllocationInfo info;
            Vma.vmaGetAllocationInfo(_vma, a, &info);
            string key = label.StartsWith("tex ", StringComparison.Ordinal) ? "tex" : string.Concat(System.Linq.Enumerable.Where(label, c => !char.IsAsciiDigit(c)));
            sum.TryGetValue(key, out (int Count, long Bytes) was);
            sum[key] = (was.Count + 1, was.Bytes + (long)info.size);
        }
        foreach (VkImg g in _images.Live()) if (g.Group == null && !g.Window) Add(g.Desc.Label, g.Alloc);
        foreach (AliasGroup g in _alias.Values) Add("alias " + g.Name, g.Alloc);
        foreach (VkBuf b in _buffers.Live()) Add(b.Desc.Label, b.Alloc);
        var rows = System.Linq.Enumerable.ToArray(System.Linq.Enumerable.OrderByDescending(sum, r => r.Value.Bytes));
        var line = new System.Text.StringBuilder();
        long rest = 0; int others = 0;
        for (int i = 0; i < rows.Length; i++)
        {
            if (i >= n) { rest += rows[i].Value.Bytes; others += rows[i].Value.Count; continue; }
            line.Append(i == 0 ? "" : ", ").Append(rows[i].Key).Append(" x").Append(rows[i].Value.Count).Append(' ').Append(rows[i].Value.Bytes >> 20).Append("MiB");
        }
        if (others > 0) line.Append($", {others} others {rest >> 20}MiB");
        return line.ToString();
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
        foreach (VkImg g in _images.Live()) if (g.Group == null && !g.Window) Offer(g.Desc.Label, g.Alloc);
        foreach (AliasGroup g in _alias.Values) Offer("alias " + g.Name, g.Alloc);
        foreach (VkBuf b in _buffers.Live()) Offer(b.Desc.Label, b.Alloc);
        return have == n ? top : top[..have];
    }
}
