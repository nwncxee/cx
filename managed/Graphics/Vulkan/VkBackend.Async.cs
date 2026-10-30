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
    /// <summary>A second queue's recording resources and timeline: the async queue's, or the transfer queue's.</summary>
    private sealed class Lane(VkQueue queue, uint family, VkCommandPool pool, VkSemaphore timeline)
    {
        public readonly VkQueue Queue = queue;
        public readonly uint Family = family;
        public readonly VkCommandPool Pool = pool;
        public readonly VkSemaphore Timeline = timeline;
        /// <summary>Numbered from 0 like the graphics slots, whose numbers index the timestamp pool: never timed.</summary>
        public readonly List<Slot> Slots = new();
        /// <summary>Async submits made, which is the value the last one signalled, and the counter as last read.</summary>
        public long Submits, Completed;
    }

    /// <summary>Null on a device with no compute-only family: an async list then runs on the graphics queue.</summary>
    private readonly Lane? _async;
    /// <summary>The async value no graphics submit has waited on yet; 0 when none.</summary>
    private long _unjoined;
    /// <summary>A graphics submit already ran beside <see cref="_unjoined"/>: the next one waits on it.</summary>
    private bool _beside;
    /// <summary>The recording in progress is an async list on the async queue.</summary>
    private bool _onAsync;
    /// <summary>The recording in progress runs beside <see cref="_unjoined"/>, whose recording was <see cref="_asyncRec"/>.</summary>
    private bool _besideNow;
    private long _asyncRec = -1;
    /// <summary>An image the async recording reached that was not made <see cref="ImageUse.Async"/>, or one the beside
    /// recording moved that the async one reached: either refuses the recording by name.</summary>
    private string? _crossed;
    /// <summary>A refused async recording's barriers never ran: the lane's next recording opens with a full barrier.</summary>
    private bool _resyncAsync;
    /// <summary>A list with no commands, so uploads waiting for a graphics submit's head get one ahead of an async submit.</summary>
    private CommandList? _uploadList;

    /// <summary>The first family of <paramref name="families"/> with queues that computes and does not draw, or -1.</summary>
    private static int AsyncFamily(ReadOnlySpan<VkQueueFamilyProperties> families)
    {
        for (int k = 0; k < families.Length; k++)
            if (families[k].queueCount > 0 && (families[k].queueFlags & (VkQueueFlags.Graphics | VkQueueFlags.Compute)) == VkQueueFlags.Compute)
                return k;
        return -1;
    }

    /// <summary><paramref name="phys"/>'s queue families, read off the device. ponytail: a second walk of the table the
    /// probe already read; once per device, so not threaded back out of it.</summary>
    private static VkQueueFamilyProperties[] Families(VkInstanceApi vki, VkPhysicalDevice phys)
    {
        uint n = 0;
        vki.vkGetPhysicalDeviceQueueFamilyProperties(phys, &n, null);
        var f = new VkQueueFamilyProperties[n];
        fixed (VkQueueFamilyProperties* p = f) vki.vkGetPhysicalDeviceQueueFamilyProperties(phys, &n, p);
        return f;
    }

    private Lane OpenLane(uint family, string name)
    {
        _vkd.vkGetDeviceQueue(family, 0, out VkQueue q);
        var pci = new VkCommandPoolCreateInfo { flags = VkCommandPoolCreateFlags.ResetCommandBuffer, queueFamilyIndex = family };
        VkCommandPool pool;
        Check(_vkd.vkCreateCommandPool(&pci, &pool), $"vkCreateCommandPool ({name})");
        return new Lane(q, family, pool, MakeTimeline());
    }

    /// <summary><see cref="Submit(CommandList)"/> for a graphics list that runs beside the async submit made before it
    /// rather than waiting on it: an image it would move that the async recording reached refuses it by name. Without one
    /// pending it is a plain submit; the graphics submit after it waits on the async one either way. ponytail: buffers are
    /// not checked -- the arena takes disjoint ranges from both -- so what the two share is the caller's to keep apart.</summary>
    internal bool SubmitAlongside(CommandList list) => Submit(list, -1, beside: true);

    /// <summary>The graphics family and the async one, and with <paramref name="transfer"/> the transfer one, for a
    /// resource more than one queue reaches; exclusive when the device has neither.</summary>
    private void Share(ref VkSharingMode mode, ref uint count, ref uint* families, uint* three, bool transfer)
    {
        uint n = 0;
        three[n++] = _family;
        if (_async != null) three[n++] = _async.Family;
        if (transfer && _transfer != null) three[n++] = _transfer.Family;
        if (n == 1) return;
        mode = VkSharingMode.Concurrent;
        count = n;
        families = three;
    }

    /// <summary>Uploads still waiting for a graphics submit's head get one of their own: ahead of an async submit,
    /// which reads what the graphics submits before it left, and when staging passes its budget. False when refused.</summary>
    private bool UploadsAhead()
    {
        UploadState u = _upload;
        if (!u.Pending && u.Acquire.Count == 0 && _shippedJoin == 0) return true;
        _uploadList ??= new CommandList("vulkan uploads");
        _uploadList.Reset();
        return Submit(_uploadList, -1);
    }

    /// <summary>A second queue's objects, after the device is idle.</summary>
    private void DisposeLane(Lane? lane)
    {
        if (lane == null) return;
        foreach (Slot s in lane.Slots)
            if (s.Stage.IsNotNull) Vma.vmaDestroyBuffer(_vma, s.Stage, s.StageAlloc);
        _vkd.vkDestroySemaphore(lane.Timeline);
        _vkd.vkDestroyCommandPool(lane.Pool);
    }
}
