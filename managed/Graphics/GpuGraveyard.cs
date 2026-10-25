using CX.Framework;

namespace CX.Graphics;

/// <summary>
/// The deferred-free queue's ring: entries stamped with the frame they were retired on, and the frame clock
/// the drains advance. Scheduling only -- no GL -- so it checks off-device with no context.
/// </summary>
internal sealed class GraveyardState
{
    /// <summary>Entries the ring holds. An eviction burst retires a few hundred; past this the caller
    /// releases its object itself and the overflow is counted and named.</summary>
    internal const int Capacity = 4096;

    /// <summary>One retired object: the frame it was retired on, what it is, and its name.</summary>
    internal struct Entry
    {
        internal int Frame;
        internal GpuGraveyard.Kind Of;
        internal uint Name;
    }

    internal readonly Entry[] Ring = new Entry[Capacity];

    /// <summary>Monotonic ring cursors; the slot is the cursor modulo <see cref="Capacity"/>.</summary>
    internal long Head, Tail;
    /// <summary>Frames drained so far: the frame a retire is stamped with.</summary>
    internal int Now;
    /// <summary>The present <see cref="Advance"/> last moved the clock for, so a second bracket in the same
    /// present does not age the queue twice. -1 = no present has been seen.</summary>
    internal int LastPresent = -1;
    internal long Retired, Drained, Dropped, Overflowed, Unissued, Foreign;
    /// <summary>Each of these failures has been named once; later ones are the period line's business.</summary>
    internal bool OverflowNoted, ForeignNoted;
    /// <summary>The log, held with the rest of the owner's state rather than beside it.</summary>
    internal ExtenderLog? Log;

    internal int Count => (int)(Tail - Head);

    /// <summary>Stamp <paramref name="name"/> with the current frame and enqueue it. False = the ring is
    /// full and the caller must release the object itself; name 0 is nothing to do.</summary>
    internal bool Push(GpuGraveyard.Kind kind, uint name)
    {
        if (name == 0) return true;
        if (Count >= Capacity) { Overflowed++; return false; }
        Ring[(int)(Tail++ % Capacity)] = new Entry { Frame = Now, Of = kind, Name = name };
        Retired++;
        return true;
    }

    /// <summary>Age the queue by one frame for <paramref name="present"/>, or false when this present has
    /// already been counted. Several brackets run the deferred work in one present and only the first may
    /// move the clock, or the hold is a fraction of the frames it claims.</summary>
    internal bool Advance(int present)
    {
        if (present == LastPresent) return false;
        LastPresent = present;
        Now++;
        return true;
    }

    /// <summary>Take the oldest entry whose frame is <paramref name="hold"/> drains behind, or false while
    /// the head is still held. Entries are stamped in order, so the head is always the oldest.</summary>
    internal bool TakeDue(int hold, out GpuGraveyard.Kind kind, out uint name)
    {
        kind = default; name = 0;
        if (Count == 0) return false;
        ref Entry e = ref Ring[(int)(Head % Capacity)];
        if (e.Frame > Now - hold) return false;
        Head++;
        kind = e.Of; name = e.Name;
        Drained++;
        return true;
    }

    /// <summary>Context or device loss: every object went with it, so the ring empties without a release.
    /// A name from a dead context is a name the next context recycles for something else.</summary>
    internal void DropAll()
    {
        Dropped += Count;
        Head = Tail = 0;
    }
}

/// <summary>
/// Where an Alloy GPU object goes to die. A release the frames still in flight have not finished with is
/// undefined on an explicit API and a wasted copy on this one, so every lifetime edge retires here and the
/// drain at the frame boundary does the deleting.
/// </summary>
public static unsafe class GpuGraveyard
{
    /// <summary>What a retired name is, so the drain knows which delete to call. An arm is added the frame
    /// something retires that kind, never ahead of it.</summary>
    public enum Kind : byte { Texture, Framebuffer, Buffer, Sampler }

    /// <summary>Frames a retired object is held for. Three is the depth every fenced ring in Alloy already
    /// assumes, and <c>OwnedTextures.DeferFrames</c> reads it from here rather than declaring it again.</summary>
    public const int FramesInFlight = 3;

    private static readonly GraveyardState _s = new();

    /// <summary>Objects waiting out their frames.</summary>
    public static int Pending => _s.Count;
    /// <summary>Objects the drain has taken, and objects dropped unreleased by a lost context.</summary>
    public static long Drained => _s.Drained;
    public static long Dropped => _s.Dropped;

    public static void Init(ExtenderLog log) => _s.Log = log;

    /// <summary>Retire <paramref name="name"/>: it is deleted three drains from now, not here. Called with the
    /// 4.6 context held, which is the one thread the ring is ever touched from, and is where the drain deletes.
    /// A full ring deletes immediately rather than leak, which is loud and counted.</summary>
    public static void Retire(Kind kind, uint name)
    {
        if (!Mine("Retire")) return;
        if (_s.Push(kind, name)) return;
        if (!_s.OverflowNoted)
        {
            _s.OverflowNoted = true;
            _s.Log?.Always($"GpuGraveyard: the {GraveyardState.Capacity}-entry deferred-free queue is FULL -- "
                       + $"a {kind} is being deleted while frames may still be reading it. Something is retiring "
                       + "GPU objects faster than one frame can drain them.");
        }
        Delete(kind, name);
    }

    /// <summary>The frame boundary: age the queue once for <paramref name="present"/> and release what the GPU
    /// has finished with. Every bracket that runs the deferred work calls this; only the first ages it.</summary>
    public static void Drain(int present)
    {
        if (!Mine("Drain") || !_s.Advance(present)) return;
        while (_s.TakeDue(FramesInFlight, out Kind kind, out uint name)) Delete(kind, name);
    }

    /// <summary>Context or device loss: drop the queue whole. The objects died with the context, so deleting
    /// their names here would aim a delete at whatever the next context recycles those numbers for.</summary>
    public static void Drop() => _s.DropAll();

    /// <summary>Does this thread hold the 4.6 context? The ring is not locked, and its whole contract is that
    /// one thread touches it at a time -- which the context grants, game thread or render thread. A caller
    /// without it is refused loudly: the name then leaks until the context goes, which is visible, not silent.</summary>
    private static bool Mine(string what)
    {
        if (GlContextService.Current46) return true;
        _s.Foreign++;
        if (!_s.ForeignNoted)
        {
            _s.ForeignNoted = true;
            _s.Log?.Always($"GpuGraveyard: {what} was called WITHOUT the 4.6 context held, which is a caller "
                         + "outside Alloy's bracket. Refused -- the queue is not locked and a second thread in "
                         + "it would tear the ring. Whatever was being retired now leaks until the context goes.");
        }
        return false;
    }

    /// <summary>One clause of the context's period line; the last three terms are the ones that are a bug.</summary>
    public static string Note()
        => $"deferred frees: {_s.Drained} freed, {_s.Count} pending, {_s.Dropped} dropped with a context"
         + (_s.Overflowed == 0 ? "" : $", {_s.Overflowed} DELETED IN FLIGHT (queue full)")
         + (_s.Unissued == 0 ? "" : $", {_s.Unissued} NOT DELETED (no entry point)")
         + (_s.Foreign == 0 ? "" : $", {_s.Foreign} REFUSED off-bracket (leaked)");

    private static void Delete(Kind kind, uint name)
    {
        // Every one of these is glDelete<N>(count, names), so the kind picks the entry point and one call
        // issues it; an unresolved one is counted rather than deleting nothing in silence.
        delegate* unmanaged<int, uint*, void> del = kind switch
        {
            Kind.Framebuffer => Gl.DeleteFramebuffers,
            Kind.Buffer => Gl.DeleteBuffers,
            Kind.Sampler => Gl.DeleteSamplers,
            _ => Gl.DeleteTextures,
        };
        if (del == null) { _s.Unissued++; return; }
        del(1, &name);
    }

    /// <summary>The queue's scheduling, over hand-written frame and present sequences on rings of its own:
    /// the hold, the retire order, one clock tick per present, a lost context, and a full ring. Oracles are
    /// the literal 3 and the literal names, never the constant or a second call into the code under test.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "GpuGraveyard.SelfCheck");
        var q = new GraveyardState();

        q.Push(Kind.Texture, 10);
        q.Push(Kind.Framebuffer, 11);
        q.Push(Kind.Texture, 0);
        check.Expect(q.Count == 2 && q.Retired == 2, "two retires and a zero name did not leave two in the ring");
        for (int f = 1; f <= 2; f++)
        {
            q.Advance(f);
            check.Expect(!q.TakeDue(3, out _, out _), $"a name retired on frame 0 came due on frame {f}");
        }
        q.Advance(3);
        check.Expect(q.TakeDue(3, out Kind k0, out uint n0) && k0 == Kind.Texture && n0 == 10,
                     "the first name retired was not the first released on the third drain");
        check.Expect(q.TakeDue(3, out Kind k1, out uint n1) && k1 == Kind.Framebuffer && n1 == 11,
                     "the second name retired was not released behind the first");
        check.Expect(!q.TakeDue(3, out _, out _), "an empty ring handed out an entry");
        check.Expect(q.Drained == 2, "the drained count disagrees with the two names taken");

        // A later retire must not be released with an earlier one that came due on the same drain.
        q.Push(Kind.Texture, 20);
        q.Advance(4); q.Advance(5);
        q.Push(Kind.Texture, 21);
        q.Advance(6);
        check.Expect(q.TakeDue(3, out _, out uint due) && due == 20, "the older of two stamps was not released first");
        check.Expect(!q.TakeDue(3, out _, out _), "a name retired two frames later came due with it");

        var twice = new GraveyardState();
        twice.Push(Kind.Texture, 40);
        check.Expect(twice.Advance(1) && !twice.Advance(1) && !twice.Advance(1),
                     "a second bracket in one present moved the clock again");
        twice.Advance(2); twice.Advance(2); twice.Advance(3); twice.Advance(3);
        check.Expect(twice.TakeDue(3, out _, out _), "three presents of two brackets each did not age the queue three frames");

        var lost = new GraveyardState();
        lost.Push(Kind.Framebuffer, 30);
        lost.Push(Kind.Texture, 31);
        lost.DropAll();
        check.Expect(lost.Count == 0 && lost.Dropped == 2, "a lost context left names in the queue");
        for (int f = 1; f <= 4; f++) lost.Advance(f);
        check.Expect(!lost.TakeDue(3, out _, out _), "a dropped queue released a name from the dead context");
        check.Expect(lost.Drained == 0, "a dropped name was counted as drained");

        var full = new GraveyardState();
        for (int i = 0; i < GraveyardState.Capacity; i++) full.Push(Kind.Texture, (uint)(i + 1));
        check.Expect(!full.Push(Kind.Texture, 999999), "a full ring accepted one more name");
        check.Expect(full.Overflowed == 1 && full.Count == GraveyardState.Capacity, "the refused push was not counted as an overflow");
        for (int f = 1; f <= 4; f++) full.Advance(f);
        check.Expect(full.TakeDue(3, out _, out uint first) && first == 1, "the full ring's head was not its oldest name");

        if (check.Ok) log.Info("GpuGraveyard.SelfCheck: passed");
    }
}
