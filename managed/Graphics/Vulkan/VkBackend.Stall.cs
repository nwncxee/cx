using System.Text;
using CX.Framework;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

/// <summary>
/// The stall report: every graphics-queue submit leaves a note of what it waited on, and the first wait that runs out
/// its limit writes each queue's counters and the oldest unfinished submits to the log, once. A stalled queue then
/// names itself, where a bare timeout says only that something never finished.
/// </summary>
internal sealed unsafe partial class VkBackend
{
    /// <summary>One graphics-queue submit: the timeline value it signals (0 off the timeline), the counted submits
    /// queued before it, whether it waits an acquire semaphore, and the async and transfer values it waits (0 none).</summary>
    internal readonly record struct SubmitNote(long Value, long After, bool Acquire, long Async, long Transfer, string Name);

    /// <summary>Submits remembered, and the most the report prints.</summary>
    private const int StallRing = 64, StallShown = 6;

    private sealed class StallState
    {
        public readonly SubmitNote[] Ring = new SubmitNote[StallRing];
        public long Count;
        public bool Said;
        /// <summary>The list the next graphics submit records, for its note.</summary>
        public string Name = "";
        /// <summary>The graphics value the last async submit waits on.</summary>
        public long AsyncWaits;
    }

    private readonly StallState _stall = new();

    private void NoteSubmit(in SubmitNote n) => _stall.Ring[_stall.Count++ % StallRing] = n;

    /// <summary>What holds the submit <paramref name="n"/> back, given the async and transfer counters as read: the
    /// first of its waits that has not been met, else the GPU itself.</summary>
    internal static string StallVerdict(in SubmitNote n, long asyncDone, long transferDone)
        => n.Transfer > transferDone ? $"the TRANSFER queue has not reached {n.Transfer} (at {transferDone})"
         : n.Async > asyncDone ? $"the ASYNC queue has not reached {n.Async} (at {asyncDone})"
         : n.Acquire ? "its other waits are met: the window's ACQUIRE semaphore never signalled, or the GPU is still running it"
         : "it waits on nothing unmet: the GPU is still running it, or the queue behind an earlier submit";

    /// <summary>The report, once a session: a wait on <paramref name="sem"/> for <paramref name="value"/> ran out its
    /// limit (a null semaphore is the window's acquire).</summary>
    private void SayStall(VkSemaphore sem, long value)
    {
        StallState st = _stall;
        if (st.Said) return;
        st.Said = true;
        long done = Refresh();
        long aDone = _async is QueueLane a ? Refresh(a.Timeline, ref a.Completed) : 0;
        long tDone = _transfer is QueueLane t ? Refresh(t.Timeline, ref t.Completed) : 0;
        string what = sem.IsNull ? "the window's next image" : sem == _timeline ? $"graphics value {value}"
                    : _async != null && sem == _async.Timeline ? $"async value {value}" : $"transfer value {value}";
        var sb = new StringBuilder();
        sb.Append($"VkBackend: [stall] the wait for {what} timed out. graphics {done}/{_submits} finished");
        sb.Append(_async is QueueLane al ? $", async {aDone}/{al.Submits} (last waits graphics {st.AsyncWaits}, unjoined {_unjoined}, beside {_beside})" : ", no async queue");
        sb.Append(_transfer is QueueLane tl ? $", transfer {tDone}/{tl.Submits} (join owed {_shippedJoin})" : ", no transfer queue");
        sb.Append($"; frame {_frames} ends [{string.Join(",", _frameEnds)}], acquire slots waited by [{string.Join(",", _scr.WaitedBy)}]");
        sb.Append($", held {(_held.Slot != null ? _held.Name : "none")}, {_swap.Presents} present(s)");
        _log.Always(sb.ToString());
        int shown = 0;
        long first = st.Count > StallRing ? st.Count - StallRing : 0;
        for (long i = first; i < st.Count && shown < StallShown; i++)
        {
            ref readonly SubmitNote n = ref st.Ring[i % StallRing];
            bool pending = n.Value != 0 ? n.Value > done : n.After >= done;
            if (!pending) continue;
            string id = n.Value != 0 ? $"submit {n.Value}" : $"an off-timeline submit after {n.After}";
            _log.Always($"VkBackend: [stall] {id} '{n.Name}' (acquire {n.Acquire}, async {n.Async}, transfer {n.Transfer}) -- "
                      + (shown == 0 ? StallVerdict(n, aDone, tDone) : "queued behind it"));
            shown++;
        }
        if (shown == 0) _log.Always("VkBackend: [stall] no graphics submit is unfinished -- the wait is on another queue or the window");
    }

    /// <summary>Approved-spec: the verdict names the first unmet wait. Oracles: hand-written notes and counters.</summary>
    internal static void StallSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        var both = new SubmitNote(Value: 10, After: 9, Acquire: true, Async: 4, Transfer: 7, Name: "frame");
        check.Expect(StallVerdict(both, asyncDone: 4, transferDone: 6).Contains("TRANSFER queue has not reached 7 (at 6)"),
                     "a submit whose transfer wait is unmet is held by the transfer queue");
        check.Expect(StallVerdict(both, asyncDone: 3, transferDone: 7).Contains("ASYNC queue has not reached 4 (at 3)"),
                     "with the transfer wait met, an unmet async wait holds it");
        check.Expect(StallVerdict(both, asyncDone: 4, transferDone: 7).Contains("ACQUIRE"),
                     "with both queues' waits met, a submit that waits the acquire names it");
        var plain = both with { Acquire = false, Async = 0, Transfer = 0 };
        check.Expect(StallVerdict(plain, asyncDone: 0, transferDone: 0).Contains("waits on nothing unmet"),
                     "a submit with no waits is the GPU's own");
        log.Info(check.Ok ? "VkBackend.StallSelfCheck: passed" : "VkBackend.StallSelfCheck: FAILED");
    }
}
