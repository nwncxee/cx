using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Rhi;
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
    /// <summary>Translation ticks and commands per <see cref="Op"/>, and the upload bytes, since the last take.</summary>
    private readonly long[] _opTicks = new long[OpCount];
    private readonly int[] _opN = new int[OpCount];
    private long _inlineBytes, _stagedBytes;
    /// <summary>Every buffer an update wrote since the last take, once each.</summary>
    private readonly List<VkBuf> _upTouched = [];
    private readonly Dictionary<string, UploadTop> _upByLabel = [];

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

    /// <summary>One buffer label's <see cref="Op.UpdateBuffer"/> traffic since the last take.</summary>
    internal readonly record struct UploadTop(string Label, long Bytes, int Calls);

    /// <summary>Bytes written through <c>vkCmdUpdateBuffer</c> and through this submit's staging since the last take,
    /// the update calls, and <paramref name="top"/> refilled with every label they wrote, the most bytes first.</summary>
    internal void TakeUploads(out long inlined, out long staged, out int calls, List<UploadTop> top)
    {
        inlined = _inlineBytes; staged = _stagedBytes; calls = 0;
        _inlineBytes = _stagedBytes = 0;
        _upByLabel.Clear();
        foreach (VkBuf b in _upTouched)
        {
            calls += b.UpCalls;
            _upByLabel.TryGetValue(b.Desc.Label, out UploadTop t);
            _upByLabel[b.Desc.Label] = new UploadTop(b.Desc.Label, t.Bytes + b.UpBytes, t.Calls + b.UpCalls);
            b.UpBytes = 0; b.UpCalls = 0;
        }
        _upTouched.Clear();
        top.Clear();
        top.AddRange(_upByLabel.Values);
        top.Sort((x, y) => y.Bytes.CompareTo(x.Bytes));
    }

    /// <summary>Milliseconds and commands per <see cref="Op"/> translated since the last take, indexed by the op.</summary>
    internal void TakeOps(Span<double> ms, Span<int> n)
    {
        for (int i = 0; i < OpCount; i++)
        {
            ms[i] = _opTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            n[i] = _opN[i];
        }
        Array.Clear(_opTicks);
        Array.Clear(_opN);
    }

    /// <summary>Bytes the upload check writes: inline updates into two small buffers that share a label, and one
    /// inline and one staged update into the big one, past <see cref="MaxInlineUpdate"/>.</summary>
    private const int TallySmall = 256, TallyInline = 4096, TallyStaged = 98304;

    /// <summary>The translation counters against hand-made lists, once per device. Oracle: the byte counts and labels
    /// the lists are built from, as literals -- 4,608 inline over two labels and 98,304 staged in four updates, then 256.</summary>
    internal void TallyDeviceCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkBackend.TallyDeviceCheck");
        var top = new List<UploadTop>();
        Span<double> ms = stackalloc double[OpCount];
        Span<int> n = stackalloc int[OpCount];
        TakeUploads(out _, out _, out _, top);
        TakeOps(ms, n);
        TakeTranslateMs(out _, out _, out _, out _, out _, out _);
        var small = new BufferDesc(TallySmall, BufferUse.Storage, MemoryKind.Device, "tally check small");
        BufferId s1 = CreateBuffer(small), s2 = CreateBuffer(small);
        BufferId big = CreateBuffer(new BufferDesc(TallyInline + TallyStaged, BufferUse.Storage, MemoryKind.Device, "tally check big"));
        bool made = !s1.IsNull && !s2.IsNull && !big.IsNull;
        check.Expect(made, "the check's three buffers exist");
        if (made)
        {
            var cl = new CommandList("vk tally check", 4096 + TallyStaged * 2, log);
            cl.UpdateBuffer(s1, 0, new byte[TallySmall]);
            cl.UpdateBuffer(s2, 0, new byte[TallySmall]);
            cl.UpdateBuffer(big, 0, new byte[TallyInline]);
            cl.UpdateBuffer(big, TallyInline, new byte[TallyStaged]);
            check.Expect(Submit(cl), "the check's list submits");
            TakeUploads(out long inl, out long st, out int calls, top);
            TakeOps(ms, n);
            double whole = TakeTranslateMs(out _, out _, out _, out _, out _, out _), parts = 0;
            foreach (double m in ms) parts += m;
            check.Expect(inl == 4608 && st == 98304 && calls == 4,
                $"256 + 256 + 4,096 bytes go inline and 98,304 through staging over four updates (got {inl}, {st}, {calls})");
            check.Expect(top.Count == 2 && top[0] == new UploadTop("tally check big", 102400, 2) && top[1] == new UploadTop("tally check small", 512, 2),
                $"the big buffer leads with 102,400 bytes over two updates, then the two small ones under one label with 512 over two (got {string.Join(", ", top)})");
            check.Expect(n[(int)Op.UpdateBuffer] == 4 && ms[(int)Op.UpdateBuffer] > 0 && n[(int)Op.Draw] == 0,
                $"the four updates are the UpdateBuffer op's and took time; no draw is counted (got {n[(int)Op.UpdateBuffer]} in {ms[(int)Op.UpdateBuffer]:F4} ms, {n[(int)Op.Draw]} draws)");
            check.Expect(parts <= whole + 1e-9, $"the ops' times are parts of the translation's, never more ({parts:F4} of {whole:F4} ms)");
            cl.Reset();
            cl.UpdateBuffer(s1, 0, new byte[TallySmall]);
            check.Expect(Submit(cl), "the second list submits");
            TakeUploads(out inl, out st, out calls, top);
            TakeOps(ms, n);
            check.Expect(inl == 256 && st == 0 && calls == 1 && top.Count == 1 && top[0] == new UploadTop("tally check small", 256, 1)
                      && n[(int)Op.UpdateBuffer] == 1,
                $"a take starts the next period from zero: one 256-byte update is all the next one holds (got {inl}, {st}, {calls}, {string.Join(", ", top)}, {n[(int)Op.UpdateBuffer]} ops)");
        }
        Destroy(s1); Destroy(s2); Destroy(big);
        log.Info(check.Ok ? "VkBackend.TallyDeviceCheck: passed" : "VkBackend.TallyDeviceCheck: FAILED -- see above");
    }

    /// <summary>One slot per <see cref="Op"/>.</summary>
    internal static readonly int OpCount = Enum.GetValues<Op>().Length;
}
