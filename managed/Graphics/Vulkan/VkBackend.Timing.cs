using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Two timestamps per submit slot around its command buffer, read once the slot's fence has passed, so the
    /// read never waits; null when the queue cannot time. Nanoseconds a tick, and each slot's pair in flight.</summary>
    private VkQueryPool _timePool;
    private double _tickNs;
    private readonly bool[] _timed = new bool[Slots];
    private double _gpuMsSum;
    private bool _timing;

    /// <summary>Time the next submit: the frame's own, so a copy or a check submitted beside it is never banked.</summary>
    internal bool TimeNextSubmit;
    private int _gpuMsN;
    /// <summary>Stopwatch ticks the CPU spent blocked on a slot fence, and on the swapchain acquire, since the last take.</summary>
    private long _fenceTicks, _acquireTicks;

    private void OpenTiming(in VkPhysicalDeviceProperties props)
    {
        if (props.limits.timestampComputeAndGraphics == false || props.limits.timestampPeriod <= 0f) return;
        var ci = new VkQueryPoolCreateInfo { queryType = VkQueryType.Timestamp, queryCount = Slots * 2 };
        VkQueryPool pool;
        if (_vkd.vkCreateQueryPool(&ci, null, &pool) != VkResult.Success) return;
        _timePool = pool;
        _tickNs = props.limits.timestampPeriod;
    }

    /// <summary>At slot <paramref name="slot"/>'s begin, its fence passed: bank the pair it last wrote, then open a new one.</summary>
    private void TimeBegin(int slot)
    {
        if (_timePool.IsNull) return;
        if (_timed[slot])
        {
            ulong* t = stackalloc ulong[2];
            if (_vkd.vkGetQueryPoolResults(_timePool, (uint)slot * 2, 2, 16, t, 8, VkQueryResultFlags.Bit64) == VkResult.Success && t[1] >= t[0])
            {
                _gpuMsSum += (t[1] - t[0]) * _tickNs / 1e6;
                _gpuMsN++;
            }
            _timed[slot] = false;
        }
        _timing = TimeNextSubmit;
        TimeNextSubmit = false;
        if (!_timing) return;
        _vkd.vkCmdResetQueryPool(_cmd, _timePool, (uint)slot * 2, 2);
        _vkd.vkCmdWriteTimestamp(_cmd, VkPipelineStageFlags.TopOfPipe, _timePool, (uint)slot * 2);
    }

    private void TimeEnd(int slot, bool ok)
    {
        if (_timePool.IsNull || !_timing) return;
        _vkd.vkCmdWriteTimestamp(_cmd, VkPipelineStageFlags.BottomOfPipe, _timePool, (uint)slot * 2 + 1);
        _timed[slot] = ok;
    }

    /// <summary>Wait on <paramref name="fence"/>, banking the blocked time into the fence wait the frame line reports.</summary>
    private VkResult WaitFence(VkFence fence)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        VkResult r = _vkd.vkWaitForFences(1, &fence, true, FenceTimeoutNs);
        _fenceTicks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        return r;
    }

    /// <summary>Milliseconds blocked on slot fences and on the swapchain acquire since the last take.</summary>
    internal void TakeWaitMs(out double fence, out double acquire)
    {
        fence = _fenceTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        acquire = _acquireTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        _fenceTicks = _acquireTicks = 0;
    }

    /// <summary>The mean GPU time of the timed submits banked since the last take, and how many; 0 when none were.</summary>
    internal double TakeGpuMs(out int n)
    {
        n = _gpuMsN;
        double avg = n == 0 ? 0 : _gpuMsSum / n;
        _gpuMsSum = 0; _gpuMsN = 0;
        return avg;
    }
}
