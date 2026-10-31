using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;

namespace CX.Alloy;

/// <summary>What the device offers: the device-local heaps' budget, reported by the driver where it takes
/// VK_EXT_memory_budget and estimated by the allocator where it does not.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Probe once, then keep the free-VRAM figure fresh: the device-local heaps' budget, the driver's
    /// own figure where the device takes VK_EXT_memory_budget. Called from the frame, so <c>/knobs</c> can read a
    /// number rather than query from the chat thread.</summary>
    internal static void SampleVram()
    {
        if (_log == null || AlloyDevice.VulkanMade is not VkBackend vk) return;
        vk.DeviceLocalBytes(out long usage, out long budget, out long size, out _);
        long free = Math.Max(budget - usage, 0) / 1024, total = size / 1024;
        _vram.FreeKib = (int)Math.Min(free, int.MaxValue);
        if (_vramProbed) return;
        _vramProbed = true;
        _vramExt = vk.MemoryBudget ? "VK_EXT_memory_budget" : "the Vulkan allocator's estimate (no VK_EXT_memory_budget)";
        _vramTotalKib = (int)Math.Min(total, int.MaxValue);
        _log.Info($"AlloyRenderer: [vram] {_vramExt}, {_vramTotalKib / 1024}MiB device-local, {_vram.FreeKib / 1024}MiB free");
    }

    /// <summary>The frame line's memory part: what Alloy's allocations hold, the device's use against its budget, and
    /// the largest allocations by name. Empty with no budget to measure against -- before a frame has read the
    /// device, and on a device that answers no memory query -- because a row of zeros reads as a card holding none.</summary>
    internal static string VramLine(long heldBytes, long usageBytes, long budgetBytes, ReadOnlySpan<(string Name, long Bytes)> top)
    {
        const int Shift = 20;
        if (budgetBytes <= 0) return "";
        var line = new System.Text.StringBuilder($", vram {heldBytes >> Shift}MiB held, {usageBytes >> Shift}/{budgetBytes >> Shift}MiB of the budget used");
        if (usageBytes > budgetBytes) line.Append($", OVER BUDGET by {(usageBytes - budgetBytes) >> Shift}MiB");
        for (int i = 0; i < top.Length; i++) line.Append(i == 0 ? "; largest " : ", ").Append($"{top[i].Name} {top[i].Bytes >> Shift}MiB");
        return line.ToString();
    }

    /// <summary>The frame line's memory part as the device reads now; empty before the first frame, which is what
    /// makes the device and reads its figures.</summary>
    internal static string FrameVram()
    {
        if (!_vramProbed || AlloyDevice.VulkanMade is not VkBackend vk) return "";
        vk.DeviceLocalBytes(out long usage, out long budget, out _, out long held);
        return VramLine(held, usage, budget, vk.Largest(3));
    }

    /// <summary>The device's memory figures as last sampled, KiB.</summary>
    private sealed class VramState
    {
        public int FreeKib;
    }

    private static readonly VramState _vram = new();

    private static bool _vramProbed;
    private static string? _vramExt;
    private static int _vramTotalKib;
    /// <summary>The probe's total, MiB; 0 before the probe or when the device answers nothing.</summary>
    internal static long VramTotalMib => _vramTotalKib / 1024;
    /// <summary>Is the bindless path armed this session (probed at the first lit program build)?</summary>
    internal static bool Bindless => _bindless;
    /// <summary>ATI_meminfo (Mesa) answers FREE, never total; at probe time that is most of the card.</summary>
    internal static long VramFreeMib => _vram.FreeKib / 1024;
    internal static bool VramProbed => _vramProbed;

    /// <summary>The <c>/knobs</c> residency line: what Alloy is holding, and what the device will say about
    /// the pool it is holding it in. Null until the bracket has probed, which is the honest answer before a
    /// frame has run.</summary>
    internal static string? VramNote()
    {
        if (!_vramProbed) return null;
        string device = _vramExt == null
            ? "the device answers no memory query"
            : $"{_vramExt}: " + (_vramTotalKib > 0 ? $"{_vramTotalKib / 1024}MiB total, " : "")
              + $"{_vram.FreeKib / 1024}MiB free";
        return $"[vram] Alloy holds {OwnedTextures.PeakMib}MiB of textures at high water; {device}";
    }
}
