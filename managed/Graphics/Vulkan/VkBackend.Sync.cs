using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The timelines: graphics submit number n signals value n + 1 on the graphics timeline, which every lifetime waits on;
/// the async and transfer queues count their own, which only a graphics submit's join waits on. "Has it finished" is a
/// counter read against a value, never a fence. The CPU waits at a present FramesInFlight frames on, or when every
/// slot still runs.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    private readonly VkSemaphore _timeline;
    /// <summary>The timeline's counter as last read: every submit whose value is at or below it has finished.</summary>
    private long _completed;
    /// <summary>The timeline value each of the last <see cref="FramesInFlight"/> frames ended on, by frame number.</summary>
    private readonly long[] _frameEnds = new long[FramesInFlight];
    private long _frames;

    /// <summary>The graphics value a use recorded now, or an object freed now, waits out: the next submit's, or the one
    /// after it while the graphics submit beside an async one is still to come -- the first that waits on it.</summary>
    private long Next => _submits + (_onAsync || (_unjoined != 0 && !_beside) ? 2 : 1);

    /// <summary>Has the timeline reached <paramref name="value"/>, as last read? Conservative between reads.</summary>
    private bool Passed(long value) => value <= _completed;

    /// <summary>Has the timeline reached <paramref name="value"/>, reading the counter when the last read says no?</summary>
    private bool PassedNow(long value) => Passed(value) || value <= Refresh();

    private long Refresh() => Refresh(_timeline, ref _completed);

    /// <summary>Read <paramref name="sem"/>'s counter into <paramref name="completed"/>; a refused read keeps the last one,
    /// which only errs late.</summary>
    private long Refresh(VkSemaphore sem, ref long completed)
    {
        ulong v;
        VkResult r = _vkd.vkGetSemaphoreCounterValue(sem, &v);
        if (r == VkResult.Success) completed = (long)v;
        else Fault($"vkGetSemaphoreCounterValue returned {r}");
        return completed;
    }

    private VkSemaphore MakeTimeline()
    {
        var tci = new VkSemaphoreTypeCreateInfo { semaphoreType = VkSemaphoreType.Timeline, initialValue = 0 };
        var ci = new VkSemaphoreCreateInfo { pNext = &tci };
        VkSemaphore s;
        Check(_vkd.vkCreateSemaphore(&ci, &s), "vkCreateSemaphore (timeline)");
        return s;
    }

    private VkResult WaitFor(long value) => WaitFor(_timeline, value, ref _completed);

    /// <summary>Block until <paramref name="sem"/> reaches <paramref name="value"/>, banking the blocked time into the
    /// wait the frame line reports.</summary>
    private VkResult WaitFor(VkSemaphore sem, long value, ref long completed)
    {
        if (value <= completed || value <= Refresh(sem, ref completed)) return VkResult.Success;
        ulong v = (ulong)value;
        var wi = new VkSemaphoreWaitInfo { semaphoreCount = 1, pSemaphores = &sem, pValues = &v };
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        VkResult r = _vkd.vkWaitSemaphores(&wi, WaitTimeoutNs);
        _waitTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        Refresh(sem, ref completed);
        return r;
    }

    private Slot? TakeSlot() => TakeSlot(_slots, _cmdPool, _timeline, ref _completed);

    /// <summary>The least recently submitted of <paramref name="slots"/> once it has finished on <paramref name="sem"/> --
    /// so each slot's timestamps bank in turn -- else a new one from <paramref name="pool"/> while the list has room, else
    /// that one waited out. Null (logged) when none came.</summary>
    private Slot? TakeSlot(System.Collections.Generic.List<Slot> slots, VkCommandPool pool, VkSemaphore sem, ref long completed)
    {
        Refresh(sem, ref completed);
        Slot? oldest = null;
        foreach (Slot s in slots) if (oldest == null || s.Done < oldest.Done) oldest = s;
        if (oldest != null && oldest.Done <= completed) return oldest;
        if (slots.Count < MaxSlots)
        {
            var ai = new VkCommandBufferAllocateInfo { commandPool = pool, level = VkCommandBufferLevel.Primary, commandBufferCount = 1 };
            VkCommandBuffer cb;
            VkResult a = _vkd.vkAllocateCommandBuffers(&ai, &cb);
            if (a != VkResult.Success) { Fault($"a recording slot's command buffer was refused ({a})"); return null; }
            var made = new Slot(slots.Count) { Cmd = cb };
            slots.Add(made);
            return made;
        }
        VkResult waited = WaitFor(sem, oldest!.Done, ref completed);
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
    private void EndFrame()
    {
        _frameEnds[_frames++ % FramesInFlight] = _submits;
        // VMA refreshes its heap budget from the driver as the frame index moves
        Vma.vmaSetCurrentFrameIndex(_vma, (uint)_frames);
    }

    /// <summary>The acquire semaphore of the frame being presented: the frame <see cref="FramesInFlight"/> back, which
    /// last waited on it, has finished.</summary>
    private VkSemaphore Acquired => _acquired[_frames % FramesInFlight];

    /// <summary>A command buffer (when not null) on the queue, waiting on <paramref name="wait"/> (when not null) at the
    /// transfer stage and on the transfer submit not yet waited on, signalling <paramref name="signal"/> (when not null)
    /// and, <paramref name="counted"/>, the next timeline value, then in <see cref="_submits"/>. A counted submit also
    /// waits on the async submit not yet waited on, unless it is the one asked to run beside it.</summary>
    private VkResult QueueSubmit(VkCommandBuffer cb, VkSemaphore wait, VkSemaphore signal, bool counted = true, bool besideAsked = false)
    {
        var cbi = new VkCommandBufferSubmitInfo { commandBuffer = cb };
        VkSemaphoreSubmitInfo* w = stackalloc VkSemaphoreSubmitInfo[3];
        uint nw = 0;
        if (wait.IsNotNull) w[nw++] = new VkSemaphoreSubmitInfo { semaphore = wait, stageMask = VkPipelineStageFlags2.AllTransfer };
        bool beside = counted && besideAsked && _unjoined != 0 && !_beside;
        bool join = counted && _unjoined != 0 && !beside;
        if (join) w[nw++] = new VkSemaphoreSubmitInfo { semaphore = _async!.Timeline, value = (ulong)_unjoined, stageMask = VkPipelineStageFlags2.AllCommands };
        bool shipped = cb.IsNotNull && _shippedJoin != 0;
        if (shipped) w[nw++] = new VkSemaphoreSubmitInfo { semaphore = _transfer!.Timeline, value = (ulong)_shippedJoin, stageMask = VkPipelineStageFlags2.AllCommands };
        VkSemaphoreSubmitInfo* sig = stackalloc VkSemaphoreSubmitInfo[2];
        uint n = 0;
        if (counted) sig[n++] = new VkSemaphoreSubmitInfo { semaphore = _timeline, value = (ulong)(_submits + 1), stageMask = VkPipelineStageFlags2.AllCommands };
        if (signal.IsNotNull) sig[n++] = new VkSemaphoreSubmitInfo { semaphore = signal, stageMask = VkPipelineStageFlags2.AllCommands };
        var si = new VkSubmitInfo2
        {
            waitSemaphoreInfoCount = nw, pWaitSemaphoreInfos = w,
            commandBufferInfoCount = cb.IsNull ? 0u : 1u, pCommandBufferInfos = &cbi,
            signalSemaphoreInfoCount = n, pSignalSemaphoreInfos = sig,
        };
        VkResult r = _vkd.vkQueueSubmit2(_queue, 1, &si, default);
        if (r == VkResult.Success && shipped) _shippedJoin = 0;
        if (r != VkResult.Success || !counted) return r;
        _submits++;
        if (beside) _beside = true;
        else if (join) { _unjoined = 0; _beside = false; }
        return r;
    }

    /// <summary>The recorded <paramref name="cb"/> on the async queue, after the last graphics submit.</summary>
    private VkResult AsyncSubmit(VkCommandBuffer cb)
    {
        QueueLane a = _async!;
        var cbi = new VkCommandBufferSubmitInfo { commandBuffer = cb };
        var w = new VkSemaphoreSubmitInfo { semaphore = _timeline, value = (ulong)_submits, stageMask = VkPipelineStageFlags2.AllCommands };
        var sig = new VkSemaphoreSubmitInfo { semaphore = a.Timeline, value = (ulong)(a.Submits + 1), stageMask = VkPipelineStageFlags2.AllCommands };
        var si = new VkSubmitInfo2
        {
            waitSemaphoreInfoCount = _submits > 0 ? 1u : 0u, pWaitSemaphoreInfos = &w,
            commandBufferInfoCount = 1, pCommandBufferInfos = &cbi,
            signalSemaphoreInfoCount = 1, pSignalSemaphoreInfos = &sig,
        };
        VkResult r = _vkd.vkQueueSubmit2(a.Queue, 1, &si, default);
        if (r == VkResult.Success) _unjoined = ++a.Submits;
        return r;
    }

    /// <summary>The recorded <paramref name="cb"/> on the transfer queue <paramref name="t"/>, waiting on nothing:
    /// accepted, <paramref name="t"/>'s submit count is the value it signalled.</summary>
    private VkResult TransferSubmit(QueueLane t, VkCommandBuffer cb)
    {
        var cbi = new VkCommandBufferSubmitInfo { commandBuffer = cb };
        var sig = new VkSemaphoreSubmitInfo { semaphore = t.Timeline, value = (ulong)(t.Submits + 1), stageMask = VkPipelineStageFlags2.AllCommands };
        var si = new VkSubmitInfo2 { commandBufferInfoCount = 1, pCommandBufferInfos = &cbi, signalSemaphoreInfoCount = 1, pSignalSemaphoreInfos = &sig };
        VkResult r = _vkd.vkQueueSubmit2(t.Queue, 1, &si, default);
        if (r == VkResult.Success) t.Submits++;
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
