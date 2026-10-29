using System;
using System.Collections.Generic;
using CX.Rhi;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The async compute queue: a second queue on a family that computes without drawing, its own recording slots and its
/// own timeline. An async submit waits on the last graphics submit; the first graphics submit after it may be asked to
/// run beside it, and the one after that (or the first, when not asked) waits on it. Every lifetime stays on the
/// graphics timeline, so an object an async submit reaches outlives the graphics submit that waits on it.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>The async queue's recording resources and timeline.</summary>
    private sealed class AsyncLane(VkQueue queue, uint family, VkCommandPool pool, VkSemaphore timeline)
    {
        public readonly VkQueue Queue = queue;
        public readonly uint Family = family;
        public readonly VkCommandPool Pool = pool;
        public readonly VkSemaphore Timeline = timeline;
        public readonly List<Slot> Slots = new();
        /// <summary>Async submits made, which is the value the last one signalled, and the counter as last read.</summary>
        public long Submits, Completed;
    }

    /// <summary>Null on a device with no compute-only family: an async list then runs on the graphics queue.</summary>
    private readonly AsyncLane? _async;
    /// <summary>The graphics family and the async one, for a resource both queues reach.</summary>
    private readonly uint[] _families = new uint[2];
    /// <summary>The async value no graphics submit has waited on yet; 0 when none.</summary>
    private long _unjoined;
    /// <summary>A graphics submit already ran beside <see cref="_unjoined"/>: the next one waits on it.</summary>
    private bool _beside;
    /// <summary>The submit being made was asked to run beside the async one before it.</summary>
    private bool _besideAsked;
    /// <summary>The recording in progress is an async list on the async queue.</summary>
    private bool _onAsync;
    /// <summary>An image the async recording reached that was not made <see cref="ImageUse.Async"/>.</summary>
    private string? _crossed;
    private CommandList? _uploadList;

    /// <summary>The first family of <paramref name="families"/> with queues that computes and does not draw, or -1.</summary>
    private static int AsyncFamily(ReadOnlySpan<VkQueueFamilyProperties> families)
    {
        for (int k = 0; k < families.Length; k++)
            if (families[k].queueCount > 0 && (families[k].queueFlags & (VkQueueFlags.Graphics | VkQueueFlags.Compute)) == VkQueueFlags.Compute)
                return k;
        return -1;
    }

    /// <summary><paramref name="phys"/>'s async family, read off the device.</summary>
    private static int AsyncFamily(VkInstanceApi vki, VkPhysicalDevice phys)
    {
        uint n = 0;
        vki.vkGetPhysicalDeviceQueueFamilyProperties(phys, &n, null);
        Span<VkQueueFamilyProperties> f = new VkQueueFamilyProperties[n];
        fixed (VkQueueFamilyProperties* p = f) vki.vkGetPhysicalDeviceQueueFamilyProperties(phys, &n, p);
        return AsyncFamily(f);
    }

    private AsyncLane OpenAsync(uint family)
    {
        _vkd.vkGetDeviceQueue(family, 0, out VkQueue q);
        var pci = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = family };
        VkCommandPool pool;
        Check(_vkd.vkCreateCommandPool(&pci, &pool), "vkCreateCommandPool (async)");
        _families[0] = _family;
        _families[1] = family;
        return new AsyncLane(q, family, pool, MakeTimeline());
    }

    /// <summary><see cref="Submit(CommandList)"/> for a graphics list that runs beside the async submit made before it
    /// rather than waiting on it: what it does must not touch what that submit reads or writes. Without one pending it
    /// is a plain submit; the graphics submit after it waits on the async one either way.</summary>
    internal bool SubmitAlongside(CommandList list)
    {
        _besideAsked = true;
        try { return Submit(list, -1); }
        finally { _besideAsked = false; }
    }

    /// <summary>An async submit reads what the graphics submits before it left: uploads still waiting for a graphics
    /// submit's head get one of their own first. False when it was refused.</summary>
    private bool UploadsAhead()
    {
        UploadState u = _upload;
        if (u.Copies.Count == 0 && u.Zero.Count == 0) return true;
        _uploadList ??= new CommandList("vulkan uploads");
        _uploadList.Reset();
        return Submit(_uploadList, -1);
    }

    /// <summary>The async lane's objects, after the device is idle.</summary>
    private void DisposeAsync()
    {
        if (_async == null) return;
        foreach (Slot s in _async.Slots)
            if (s.Stage.IsNotNull) Vma.vmaDestroyBuffer(_vma, s.Stage, s.StageAlloc);
        _vkd.vkDestroySemaphore(_async.Timeline);
        _vkd.vkDestroyCommandPool(_async.Pool);
    }
}
