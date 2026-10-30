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
    /// <summary>Translation ticks and commands per <see cref="Op"/> since the last take. Each op's time includes the
    /// clock read that ends it, ~20 ns, so the translation total carries one per command too.</summary>
    private readonly long[] _opTicks = new long[OpCount];
    private readonly int[] _opN = new int[OpCount];
    /// <summary>Buffer-update bytes since the last take, inline and staged, and the worst frame's bytes and translation
    /// ticks, with the running marks each frame's share is measured from.</summary>
    private long _inlineBytes, _stagedBytes, _peakBytes, _peakTicks, _frameBytesMark, _frameTicksMark;
    /// <summary>Every buffer an update wrote since the last take, once each.</summary>
    private readonly List<VkBuf> _upTouched = [];
    private readonly Dictionary<string, UploadTop> _upByLabel = [];
    private readonly List<UploadTop> _upTop = [];

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
        _translateTicks = _frameTicksMark = 0; _setN = _setMadeN = _drawN = _translateN = _barrierN = 0;
        return ms;
    }

    /// <summary>The frame's submits are all made: count it, bank it against the period's worst, and time nothing after it.</summary>
    internal void FrameTimed()
    {
        _gpuFrames++;
        TimeFrame = false;
        long bytes = _inlineBytes + _stagedBytes;
        _peakBytes = Math.Max(_peakBytes, bytes - _frameBytesMark);
        _peakTicks = Math.Max(_peakTicks, _translateTicks - _frameTicksMark);
        _frameBytesMark = bytes;
        _frameTicksMark = _translateTicks;
    }

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

    /// <summary>One op's translation since the last take: its milliseconds and its commands.</summary>
    internal readonly record struct OpCost(Op Op, double Ms, int N);

    /// <summary>Bytes written through <c>vkCmdUpdateBuffer</c> and through this submit's staging since the last take, the
    /// most any one timed frame wrote, and every label the updates wrote, the most bytes first (reused by the next take).</summary>
    internal List<UploadTop> TakeUploads(out long inlined, out long staged, out long peak)
    {
        inlined = _inlineBytes; staged = _stagedBytes; peak = _peakBytes;
        _inlineBytes = _stagedBytes = _peakBytes = _frameBytesMark = 0;
        _upByLabel.Clear();
        foreach (VkBuf b in _upTouched)
        {
            _upByLabel.TryGetValue(b.Desc.Label, out UploadTop t);
            _upByLabel[b.Desc.Label] = new UploadTop(b.Desc.Label, t.Bytes + b.UpBytes, t.Calls + b.UpCalls);
            b.UpBytes = 0; b.UpCalls = 0;
        }
        _upTouched.Clear();
        _upTop.Clear();
        _upTop.AddRange(_upByLabel.Values);
        _upTop.Sort(static (x, y) => y.Bytes.CompareTo(x.Bytes));
        return _upTop;
    }

    /// <summary>Each op's translation since the last take into <paramref name="into"/>, indexed by the op, and the
    /// most any one timed frame spent translating.</summary>
    internal void TakeOps(Span<OpCost> into, out double peakMs)
    {
        for (int i = 0; i < OpCount; i++) into[i] = new OpCost((Op)i, _opTicks[i] * 1000.0 / System.Diagnostics.Stopwatch.Frequency, _opN[i]);
        peakMs = _peakTicks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Array.Clear(_opTicks);
        Array.Clear(_opN);
        _peakTicks = 0;
    }

    /// <summary>Bytes the upload check writes: inline updates into two small buffers that share a label, and one
    /// inline and one staged update into the big one, past <see cref="MaxInlineUpdate"/>.</summary>
    private const int TallySmall = 256, TallyInline = 4096, TallyStaged = 98304;

    /// <summary>The translation counters against hand-made lists, once per device, two frames and a take apart. Oracle:
    /// the byte counts and labels the lists are built from, as literals -- frame one 4,608 inline over two labels and
    /// 98,304 staged (the worst, 102,912), frame two 256 more; after the take one 256-byte frame alone.</summary>
    internal void TallyDeviceCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkBackend.TallyDeviceCheck");
        check.Expect(OpCount == Enum.GetValues<Op>().Length, $"one counter per op: {OpCount} for the enum's {Enum.GetValues<Op>().Length}");
        Span<OpCost> ops = stackalloc OpCost[OpCount];
        TakeUploads(out _, out _, out _);
        TakeOps(ops, out _);
        TakeTranslateMs(out _, out _, out _, out _, out _, out _);
        var small = new BufferDesc(TallySmall, BufferUse.Storage, MemoryKind.Device, "tally check small");
        BufferId s1 = CreateBuffer(small), s2 = CreateBuffer(small);
        BufferId big = CreateBuffer(new BufferDesc(TallyInline + TallyStaged, BufferUse.Storage, MemoryKind.Device, "tally check big"));
        bool made = !s1.IsNull && !s2.IsNull && !big.IsNull;
        check.Expect(made, "the check's three buffers exist");
        if (made)
        {
            var cl = new CommandList("vk tally check", 2 * (TallySmall * 2 + TallyInline + TallyStaged), log);
            cl.UpdateBuffer(s1, 0, new byte[TallySmall]);
            cl.UpdateBuffer(s2, 0, new byte[TallySmall]);
            cl.UpdateBuffer(big, 0, new byte[TallyInline]);
            cl.UpdateBuffer(big, TallyInline, new byte[TallyStaged]);
            check.Expect(Submit(cl), "the first frame's list submits");
            FrameTimed();
            cl.Reset();
            cl.UpdateBuffer(s1, 0, new byte[TallySmall]);
            check.Expect(Submit(cl), "the second frame's list submits");
            FrameTimed();
            List<UploadTop> top = TakeUploads(out long inl, out long st, out long peak);
            TakeOps(ops, out double peakMs);
            double whole = TakeTranslateMs(out _, out _, out _, out _, out _, out _), parts = 0;
            foreach (OpCost o in ops) parts += o.Ms;
            OpCost up = ops[(int)Op.UpdateBuffer];
            check.Expect(inl == 4864 && st == 98304 && peak == 102912,
                $"256 + 256 + 4,096 + 256 bytes go inline and 98,304 through staging, the first frame's 102,912 the worst (got {inl}, {st}, {peak})");
            check.Expect(top.Count == 2 && top[0] == new UploadTop("tally check big", 102400, 2) && top[1] == new UploadTop("tally check small", 768, 3),
                $"the big buffer leads with 102,400 bytes over two updates, then the two small ones under one label with 768 over three (got {string.Join(", ", top)})");
            check.Expect(up.N == 5 && up.Ms > 0 && ops[(int)Op.Draw].N == 0,
                $"the five updates are the UpdateBuffer op's and took time; no draw is counted (got {up.N} in {up.Ms:F4} ms, {ops[(int)Op.Draw].N} draws)");
            check.Expect(parts <= whole + 1e-9 && peakMs > 0 && peakMs < whole,
                $"the ops' times are parts of the translation's, and one frame's is some of it, not all ({parts:F4} and {peakMs:F4} of {whole:F4} ms)");
            cl.Reset();
            cl.UpdateBuffer(s1, 0, new byte[TallySmall]);
            check.Expect(Submit(cl), "the list after the take submits");
            FrameTimed();
            top = TakeUploads(out inl, out st, out peak);
            TakeOps(ops, out peakMs);
            TakeTranslateMs(out _, out _, out _, out _, out _, out _);
            check.Expect(inl == 256 && st == 0 && peak == 256 && peakMs > 0 && top.Count == 1 && top[0] == new UploadTop("tally check small", 256, 1)
                      && ops[(int)Op.UpdateBuffer].N == 1,
                $"a take starts the next period from zero: one 256-byte update is all it holds, its frame the worst (got {inl}, {st}, {peak}, {peakMs:F4} ms, {string.Join(", ", top)}, {ops[(int)Op.UpdateBuffer].N} ops)");
            TakeGpuMs(out _);
        }
        Destroy(s1); Destroy(s2); Destroy(big);
        log.Info(check.Ok ? "VkBackend.TallyDeviceCheck: passed" : "VkBackend.TallyDeviceCheck: FAILED -- see above");
    }

    /// <summary>One counter per <see cref="Op"/>: the enum is contiguous from 0, and the check pins this to its length.</summary>
    internal const int OpCount = (int)Op.EndLabel + 1;
}
