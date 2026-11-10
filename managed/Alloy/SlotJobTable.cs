using System;
using System.Collections.Generic;
using System.Threading;

namespace CX.Alloy;

/// <summary>A background job keyed on an ownership (slot, item), revoked with the world: the lifecycle
/// <see cref="MeshLod"/> and <see cref="MeshChart"/> share. <see cref="Entries"/> is the render thread's alone
/// -- offer, result filing, per-part read and both lifetime edges all run there, so it takes no lock, and
/// <c>_gate</c> guards the three queues, which are the whole cross-thread surface.</summary>
internal sealed class SlotJobTable<TJob, TEntry>
    where TJob : class, new()
    where TEntry : struct, ISlotEntry
{
    internal readonly Dictionary<long, TEntry> Entries;
    private readonly Queue<TJob> _in = new();
    private readonly Queue<TJob> _out = new();
    private readonly Stack<TJob> _free = new();
    private readonly object _gate = new();
    private readonly int _maxPending;
    private bool _started;

    internal SlotJobTable(int maxEntries, int maxPending)
    {
        Entries = new Dictionary<long, TEntry>(maxEntries);
        _maxPending = maxPending;
    }

    internal static long KeyOf(int slot, int item) => ((long)slot << 32) | (uint)item;

    internal bool Running => _started;

    /// <summary>Start the worker(s) once, from the offering thread. <paramref name="body"/> sees one job at a
    /// time, no GL, no engine memory -- all it reads was copied for it at the offer -- and must catch, since a
    /// throw escaping a background thread kills the process. Several workers run it concurrently and file results
    /// out of offer order, so a body wanting buffers of its own keeps them <c>[ThreadStatic]</c>.</summary>
    internal void Start(string name, Action<TJob> body, int workers = 1)
    {
        if (_started) return;
        _started = true;
        for (int i = 0; i < workers; i++)
            new Thread(() =>
            {
                while (true)
                {
                    TJob j;
                    lock (_gate)
                    {
                        while (_in.Count == 0) Monitor.Wait(_gate);
                        j = _in.Dequeue();
                    }
                    body(j);
                    lock (_gate) _out.Enqueue(j);
                }
            }) { IsBackground = true, Name = workers == 1 ? name : $"{name}-{i}" }.Start();
    }

    /// <summary>The queue IS the pending budget: an offer is refused here, before an entry is claimed.</summary>
    internal bool QueueHasRoom { get { lock (_gate) return _in.Count < _maxPending; } }

    internal TJob Rent()
    {
        lock (_gate) if (_free.Count > 0) return _free.Pop();
        return new TJob();
    }

    /// <summary>Claim the key under this signature and hand the job to the worker. The claim is what stops a
    /// later enrollment offering the same range again.</summary>
    internal void Offer(TJob j, long key, ulong sig)
    {
        TEntry claim = default; claim.Sig = sig;
        Entries[key] = claim;
        lock (_gate) { _in.Enqueue(j); Monitor.Pulse(_gate); }
    }

    /// <summary>One finished job, or false when the worker has nothing more. The caller files it and recycles it.</summary>
    internal bool TryTake(out TJob j)
    {
        lock (_gate)
        {
            if (_out.Count == 0) { j = null!; return false; }
            j = _out.Dequeue();
            return true;
        }
    }

    /// <summary>File a result under its claim. False = <see cref="Revoke"/> wiped the table while the job was in
    /// flight, or a refilled item re-claimed the key under another signature: the result describes a mesh
    /// nobody draws.</summary>
    internal bool File(long key, ulong sig, TEntry e)
    {
        if (!Entries.TryGetValue(key, out TEntry claim) || claim.Sig != sig) return false;
        Entries[key] = e;
        return true;
    }

    internal void Recycle(TJob j) { lock (_gate) _free.Push(j); }

    /// <summary>A VDO slot was vacated or re-pointed: every entry keyed on it names geometry that is no longer
    /// there. <paramref name="dropped"/> sees each one before it goes (a static lambda allocates nothing).</summary>
    internal void ForgetSlot(int slot, Action<TEntry>? dropped = null)
    {
        if (Entries.Count == 0) return;
        List<long>? dead = null;
        foreach (var kv in Entries)
            if ((int)(kv.Key >> 32) == slot) (dead ??= new List<long>()).Add(kv.Key);
        if (dead == null) return;
        for (int i = 0; i < dead.Count; i++)
        {
            if (dropped != null && Entries.TryGetValue(dead[i], out TEntry e)) dropped(e);
            Entries.Remove(dead[i]);
        }
    }

    /// <summary>The world edge: every key means nothing, and whatever the worker holds or finished goes back to
    /// the free pool unfiled. The worker itself keeps running; the caller's arenas and tallies are its own.</summary>
    internal void Revoke()
    {
        Entries.Clear();
        lock (_gate)
        {
            while (_in.Count > 0) _free.Push(_in.Dequeue());
            while (_out.Count > 0) _free.Push(_out.Dequeue());
        }
    }
}

/// <summary>What a <see cref="SlotJobTable{TJob,TEntry}"/> entry must carry: the range's signature at offer
/// time, so a pool item the engine freed and refilled under the same (slot, item) key gets new work instead of
/// the old tenant's result.</summary>
internal interface ISlotEntry
{
    ulong Sig { get; set; }
}
