using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Two timestamps per recording slot around its command buffer, read once the slot's submit has finished,
    /// so the read never waits; null when the queue cannot time. Nanoseconds a tick.</summary>
    private VkQueryPool _timePool;
    private double _tickNs;
    private double _gpuMsSum;
    private bool _timing;

    /// <summary>Time every graphics submit while set: the frame's own, so a copy or a check submitted outside it is never
    /// banked. Async submits are not timed; what they hide behind the graphics queue is off the frame's GPU time.</summary>
    internal bool TimeFrame;
    /// <summary>Timed frames since the last take, which the banked submit times are averaged over.</summary>
    private int _gpuFrames;
    /// <summary>Stopwatch ticks the CPU spent blocked on the timeline, and on the swapchain acquire, since the last take.</summary>
    private long _waitTicks, _acquireTicks;

    /// <summary>Stopwatch ticks spent walking a recorded list into <c>vkCmd*</c> calls, and what those calls were, since
    /// the last take: the CPU half of a Vulkan frame that is neither a wait nor Alloy's own recording.</summary>
    private long _translateTicks;
    private int _setN, _setMadeN, _drawN, _translateN;

    private void OpenTiming(in VkPhysicalDeviceProperties props)
    {
        if (props.limits.timestampComputeAndGraphics == false || props.limits.timestampPeriod <= 0f) return;
        var ci = new VkQueryPoolCreateInfo { queryType = VkQueryType.Timestamp, queryCount = MaxSlots * 2 };
        VkQueryPool pool;
        if (_vkd.vkCreateQueryPool(&ci, null, &pool) != VkResult.Success) return;
        _timePool = pool;
        _tickNs = props.limits.timestampPeriod;
    }

    /// <summary>At slot <paramref name="s"/>'s begin, its last submit finished: bank the pair it wrote, then open a new one.</summary>
    private void TimeBegin(Slot s)
    {
        if (_timePool.IsNull) return;
        uint slot = (uint)s.Index;
        if (s.Timed)
        {
            ulong* t = stackalloc ulong[2];
            if (_vkd.vkGetQueryPoolResults(_timePool, slot * 2, 2, 16, t, 8, VkQueryResultFlags.Bit64) == VkResult.Success && t[1] >= t[0])
                _gpuMsSum += (t[1] - t[0]) * _tickNs / 1e6;
            s.Timed = false;
        }
        _timing = TimeFrame;
        if (!_timing) return;
        _vkd.vkCmdResetQueryPool(_cmd, _timePool, slot * 2, 2);
        _vkd.vkCmdWriteTimestamp(_cmd, VkPipelineStageFlags.TopOfPipe, _timePool, slot * 2);
    }

    private void TimeEnd(Slot s, bool ok)
    {
        if (_timePool.IsNull || !_timing) return;
        _vkd.vkCmdWriteTimestamp(_cmd, VkPipelineStageFlags.BottomOfPipe, _timePool, (uint)s.Index * 2 + 1);
        s.Timed = ok;
    }

    /// <summary>Milliseconds blocked on the timeline and on the swapchain acquire since the last take.</summary>
    internal void TakeWaitMs(out double gpu, out double acquire)
    {
        gpu = _waitTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        acquire = _acquireTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _waitTicks = _acquireTicks = 0;
    }

    /// <summary>Milliseconds spent translating recorded lists into <c>vkCmd*</c> calls since the last take, with the bind
    /// sets (how many were newly written, and the pools retired), recorded draws and barrier calls they carried and how many lists
    /// they came from. An indirect draw counts once, whatever the GPU reads out of it.</summary>
    internal double TakeTranslateMs(out int sets, out int made, out int retired, out int draws, out int lists, out int barriers)
    {
        sets = _setN; made = _setMadeN; retired = _sets.Retired; draws = _drawN; lists = _translateN; barriers = _barrierN;
        _sets.Retired = 0;
        double ms = _translateTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _translateTicks = 0; _setN = _setMadeN = _drawN = _translateN = _barrierN = 0;
        return ms;
    }

    /// <summary>The frame's submits are all made: count it, and time nothing after it.</summary>
    internal void FrameTimed() { _gpuFrames++; TimeFrame = false; }

    /// <summary>The GPU time banked since the last take per timed frame, and how many frames; 0 when none were.</summary>
    internal double TakeGpuMs(out int n)
    {
        n = _gpuFrames;
        double avg = n == 0 ? 0 : _gpuMsSum / n;
        _gpuMsSum = 0; _gpuFrames = 0;
        return avg;
    }
}
