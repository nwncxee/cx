using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The queue's timeline: submit number n signals value n + 1 on one timeline semaphore, so "has it finished" is the
/// counter read against a value and every lifetime waits on a value, never on a fence. The CPU waits in one place,
/// a present FramesInFlight frames on; a submit waits only when every recording slot is still running.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    private readonly VkSemaphore _timeline;
    /// <summary>The timeline's counter as last read: every submit whose value is at or below it has finished.</summary>
    private long _completed;
    /// <summary>The timeline value each of the last <see cref="FramesInFlight"/> frames ended on, by frame number.</summary>
    private readonly long[] _frameEnds = new long[FramesInFlight];
    private long _frames;

    /// <summary>The value the next submit signals: what a use recorded now, or an object freed now, waits out.</summary>
    private long Next => _submits + 1;

    /// <summary>Has the timeline reached <paramref name="value"/>, as last read? Conservative between reads.</summary>
    private bool Passed(long value) => value <= _completed;

    /// <summary>Has the timeline reached <paramref name="value"/>, reading the counter when the last read says no?</summary>
    private bool PassedNow(long value) => Passed(value) || value <= Refresh();

    /// <summary>Read the timeline's counter now; a refused read keeps the last one, which only errs late.</summary>
    private long Refresh()
    {
        ulong v;
        VkResult r = _vkd.vkGetSemaphoreCounterValue(_timeline, &v);
        if (r == VkResult.Success) _completed = (long)v;
        else Fault($"vkGetSemaphoreCounterValue returned {r}");
        return _completed;
    }

    private VkSemaphore MakeTimeline()
    {
        var tci = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline, initialValue = 0 };
        var ci = new VkSemaphoreCreateInfo { pNext = &tci };
        VkSemaphore s;
        Check(_vkd.vkCreateSemaphore(&ci, &s), "vkCreateSemaphore (timeline)");
        return s;
    }

    /// <summary>Block until the timeline reaches <paramref name="value"/>, banking the blocked time into the wait the
    /// frame line reports.</summary>
    private VkResult WaitFor(long value)
    {
        if (PassedNow(value)) return VkResult.Success;
        VkSemaphore sem = _timeline;
        ulong v = (ulong)value;
        var wi = new VkSemaphoreWaitInfo { semaphoreCount = 1, pSemaphores = &sem, pValues = &v };
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        VkResult r = _vkd.vkWaitSemaphores(&wi, WaitTimeoutNs);
        _waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        Refresh();
        return r;
    }

    /// <summary>The least recently submitted recording slot once it has finished -- so each slot's timestamps bank in
    /// turn -- else a new one while the list has room, else that one waited out. Null (logged) when none came.</summary>
    private Slot? TakeSlot()
    {
        Refresh();
        Slot? oldest = null;
        foreach (Slot s in _slots) if (oldest == null || s.Done < oldest.Done) oldest = s;
        if (oldest != null && Passed(oldest.Done)) return oldest;
        if (_slots.Count < MaxSlots)
        {
            var ai = new VkCommandBufferAllocateInfo { commandPool = _cmdPool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
            VkCommandBuffer cb;
            VkResult a = _vkd.vkAllocateCommandBuffers(&ai, &cb);
            if (a != VkResult.Success) { Fault($"a recording slot's command buffer was refused ({a})"); return null; }
            var made = new Slot(_slots.Count) { Cmd = cb };
            _slots.Add(made);
            return made;
        }
        VkResult waited = WaitFor(oldest!.Done);
        if (waited == VkResult.Success) return oldest;
        Fault(waited == VkResult.ErrorDeviceLost
            ? "the device was LOST running an earlier submit -- a GPU fault in a recorded pass"
            : $"no recording slot came free ({waited}) -- a semaphore never signalled, or the GPU is stuck");
        return null;
    }

    /// <summary>At a present: wait for the frame <see cref="FramesInFlight"/> back to finish, the one place the CPU
    /// waits on the GPU in a frame that keeps up.</summary>
    private VkResult WaitFrame() => WaitFor(_frameEnds[_frames % FramesInFlight]);

    /// <summary>The frame ends on the last submit made so far.</summary>
    private void EndFrame() => _frameEnds[_frames++ % FramesInFlight] = _submits;

    /// <summary>The acquire semaphore of the frame being presented: the frame <see cref="FramesInFlight"/> back, which
    /// last waited on it, has finished.</summary>
    private VkSemaphore Acquired => _acquired[_frames % FramesInFlight];

    /// <summary>A command buffer (when not null) on the queue, waiting on <paramref name="wait"/> (when not null) at
    /// the transfer stage and signalling <paramref name="signal"/> (when not null) and, <paramref name="counted"/>,
    /// <see cref="Next"/> on the timeline: accepted, <see cref="_submits"/> is then the value it signalled.</summary>
    private VkResult QueueSubmit(VkCommandBuffer cb, VkSemaphore wait, VkSemaphore signal, bool counted = true)
    {
        var cbi = new VkCommandBufferSubmitInfo { commandBuffer = cb };
        var w = new VkSemaphoreSubmitInfo { semaphore = wait, stageMask = VkPipelineStageFlags2.AllTransfer };
        VkSemaphoreSubmitInfo* sig = stackalloc VkSemaphoreSubmitInfo[2];
        uint n = 0;
        if (counted) sig[n++] = new VkSemaphoreSubmitInfo { semaphore = _timeline, value = (ulong)Next, stageMask = VkPipelineStageFlags2.AllCommands };
        if (signal.IsNotNull) sig[n++] = new VkSemaphoreSubmitInfo { semaphore = signal, stageMask = VkPipelineStageFlags2.AllCommands };
        var si = new VkSubmitInfo2
        {
            waitSemaphoreInfoCount = wait.IsNull ? 0u : 1u, pWaitSemaphoreInfos = &w,
            commandBufferInfoCount = cb.IsNull ? 0u : 1u, pCommandBufferInfos = &cbi,
            signalSemaphoreInfoCount = n, pSignalSemaphoreInfos = sig,
        };
        VkResult r = _vkd.vkQueueSubmit2(_queue, 1, &si, default);
        if (r == VkResult.Success && counted) _submits++;
        return r;
    }

    /// <summary><paramref name="cb"/> run with the queue waited out, off the timeline: a submit number a caller took
    /// while recording still names the submit it recorded into.</summary>
    private VkResult SubmitAndIdle(VkCommandBuffer cb, out bool queued)
    {
        VkResult r = QueueSubmit(cb, default, default, counted: false);
        queued = r == VkResult.Success;
        if (!queued) return r;
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        r = _vkd.vkQueueWaitIdle(_queue);
        _waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return r;
    }

    /// <summary>An acquire whose blit was never submitted: a submit that only waits on it, so the semaphore is unsignalled
    /// again before a later frame acquires into it.</summary>
    private void Unacquire()
    {
        if (QueueSubmit(default, Acquired, default, counted: false) != VkResult.Success) Fault("the unused acquire could not be waited out");
    }
}
