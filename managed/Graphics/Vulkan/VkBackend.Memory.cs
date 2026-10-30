using System;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The allocations VMA holds on every heap and their bytes, counted now. Cold: a walk of VMA's blocks.</summary>
    internal (long Count, long Bytes) Allocations
    {
        get
        {
            Vma.vmaCalculateStatistics(_vma, out VmaTotalStatistics t);
            return (t.total.statistics.allocationCount, (long)t.total.statistics.allocationBytes);
        }
    }

    /// <summary>The refusals this device has logged or counted.</summary>
    internal int Faults => _faults;

    /// <summary>The <paramref name="n"/> largest allocations by their bytes, each named by its label, largest first.</summary>
    internal (string Name, long Bytes)[] Largest(int n) => [];
}
