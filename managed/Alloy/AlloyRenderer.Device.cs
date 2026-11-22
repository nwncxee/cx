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
        SampleVram(vk, _log);
    }

    /// <summary>Runs <paramref name="read"/> with the probe and the texture budget settled from <paramref name="vk"/> as
    /// a first frame on it settles them, then puts the session's back: a check reads the live figures of a device of its own.</summary>
    internal static T WithVramOf<T>(VkBackend vk, Func<T> read)
    {
        VramState was = _vram;
        TexBudgetInputs period = TexBudget.Inputs;
        _vram = new VramState();
        try
        {
            SampleVram(vk, null);
            return read();
        }
        finally { _vram = was; TexBudget.BeginPeriod(period); }
    }

    /// <summary>Start a texture budget period from <paramref name="vk"/>'s figures now: its heaps' budget, and what
    /// Alloy holds there beyond the two texture caches.</summary>
    private static void BeginTexPeriod(VkBackend vk)
    {
        vk.DeviceLocalBytes(out _, out long budget, out long size, out long held);
        long other = Math.Max(0, held - TexRegistry.HeldBytes - OwnedTextures.CacheBytes);
        TexBudget.BeginPeriod(TexBudget.InputsFrom(Renderer.TexCache.Boot, budget, size, other));
    }

    private static void SampleVram(VkBackend vk, ExtenderLog? log)
    {
        vk.DeviceLocalBytes(out long usage, out long budget, out long size, out _);
        long free = Math.Max(budget - usage, 0) / 1024, total = size / 1024;
        _vram.FreeKib = (int)Math.Min(free, int.MaxValue);
        if (_vram.Probed) return;
        _vram.Probed = true;
        _vram.Ext = vk.MemoryBudget ? "VK_EXT_memory_budget" : "the Vulkan allocator's estimate (no VK_EXT_memory_budget)";
        _vram.TotalKib = (int)Math.Min(total, int.MaxValue);
        BeginTexPeriod(vk);
        log?.Info($"AlloyRenderer: [vram] {_vram.Ext}, {_vram.TotalKib / 1024}MiB device-local, {_vram.FreeKib / 1024}MiB free");
    }

    /// <summary>The frame line's memory part: what Alloy's allocations hold, the device's use against its budget, what
    /// sits in system RAM and what idle models hold, and the largest allocations by name. Empty with no budget -- before a frame has read the
    /// device, and on a device that answers no memory query -- because a row of zeros reads as a card holding none.</summary>
    internal static string VramLine(long heldBytes, long usageBytes, long budgetBytes, ReadOnlySpan<(string Name, long Bytes)> top,
                                    long systemRamBytes = 0, long idleModelBytes = 0)
    {
        const int Shift = 20;
        if (budgetBytes <= 0) return "";
        var line = new System.Text.StringBuilder($", vram {heldBytes >> Shift}MiB held, {usageBytes >> Shift}/{budgetBytes >> Shift}MiB of the budget used");
        if (usageBytes > budgetBytes) line.Append($", OVER BUDGET by {(usageBytes - budgetBytes) >> Shift}MiB");
        line.Append($", in system RAM {systemRamBytes >> Shift}MiB, held by idle models {idleModelBytes >> Shift}MiB");
        for (int i = 0; i < top.Length; i++) line.Append(i == 0 ? "; largest " : ", ").Append($"{top[i].Name} {top[i].Bytes >> Shift}MiB");
        return line.ToString();
    }

    /// <summary>What a load kept for itself and no longer needs, let go before the heap compacts.</summary>
    internal static void DropLoadCaches()
    {
        TexScan.DropPixelPool();
        LightmapBake.ReleaseIdleReads();
    }

    /// <summary>Rows of the frame line's by-owner VRAM list.</summary>
    private const int VramOwnerRows = 14;

    /// <summary>The frame line's memory part as the device reads now, closed by the texture budget period it ends;
    /// empty before the first frame, which makes the device. The ladder takes the period's use, then the next period
    /// begins from the device's figures after whatever it gave.</summary>
    internal static string FrameVram()
    {
        if (!_vram.Probed || AlloyDevice.VulkanMade is not VkBackend vk) return "";
        string line = FrameVram(vk, _log!) + "; " + TexRegistry.BudgetLine();
        vk.DeviceLocalBytes(out long usage, out long budget, out _, out _);
        VramLadder.Period(usage >> 20, budget >> 20);
        BeginTexPeriod(vk);
        return line;
    }

    /// <summary>The frame line's memory part as <paramref name="vk"/> reads now; an allocation first seen in system
    /// RAM is warned about on <paramref name="log"/>.</summary>
    internal static string FrameVram(VkBackend vk, ExtenderLog log)
    {
        vk.DeviceLocalBytes(out long usage, out long budget, out _, out long held);
        return FrameVram(held, usage, budget, vk.SystemRamBytes(log), vk.Largest(3)) + $"; host-visible {vk.HostHeldBytes() >> 20}MiB; device blocks {vk.DeviceBlockBytes() >> 20}MiB; owners {vk.Owners(VramOwnerRows)}";
    }

    /// <summary>The frame line's memory part from the device's figures, bytes, and <paramref name="systemRamBytes"/> of
    /// Alloy's allocations in system RAM; what idle models hold it reads itself.</summary>
    internal static string FrameVram(long heldBytes, long usageBytes, long budgetBytes, long systemRamBytes,
                                     ReadOnlySpan<(string Name, long Bytes)> top)
        => VramLine(heldBytes, usageBytes, budgetBytes, top, systemRamBytes, DrawRecipe.IdleModelTextureBytes());

    /// <summary>The device's memory figures as last sampled, KiB, and which query answered: what the probe
    /// settled for the session, beside the free figure every bracket resamples.</summary>
    private sealed class VramState
    {
        public int FreeKib;
        public bool Probed;
        public string? Ext;
        public int TotalKib;
    }

    private static VramState _vram = new();

    /// <summary>The probe's total, MiB; 0 before the probe or when the device answers nothing.</summary>
    internal static long VramTotalMib => _vram.TotalKib / 1024;
    /// <summary>ATI_meminfo (Mesa) answers FREE, never total; at probe time that is most of the card.</summary>
    internal static long VramFreeMib => _vram.FreeKib / 1024;
    internal static bool VramProbed => _vram.Probed;

    /// <summary>The <c>/knobs</c> residency line: what Alloy is holding, and what the device will say about
    /// the pool it is holding it in. Null until the bracket has probed, which is the honest answer before a
    /// frame has run.</summary>
    internal static string? VramNote()
    {
        if (!_vram.Probed) return null;
        string device = _vram.Ext == null
            ? "the device answers no memory query"
            : $"{_vram.Ext}: " + (_vram.TotalKib > 0 ? $"{_vram.TotalKib / 1024}MiB total, " : "")
              + $"{_vram.FreeKib / 1024}MiB free";
        return $"[vram] Alloy holds {OwnedTextures.PeakMib}MiB of textures at high water; {device}";
    }
}
