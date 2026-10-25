using System;
using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The delete path: the engine's texture-delete funnel, the filter that keeps a retired resource for the
/// cache, and the bracket drain that frees names and drops dead bindless handles.</summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>Capped: a driver that refuses one upload refuses every texture like it, and the first few
    /// lines say which as well as any number of them would.</summary>
    private static int _diagLeft = 8;

    /// <summary>A 16384-wide texture has 15 mips; the cap only bounds a driver that answers a nonzero width
    /// forever.</summary>
    private const int MaxLevels = 16;

    private const long OneMiB = 1024 * 1024;

    /// <summary>The engine is deleting these GL names -- THE world edge for a texture: a resource the engine
    /// no longer holds is one Alloy can never be handed a tid for again, so the index entry goes and the
    /// texture behind it is queued. An area transition fires this and nothing else Alloy watches. Issues no
    /// GL -- this runs wherever the engine happened to be -- so <see cref="Drain"/> does the deleting.</summary>
    [UnmanagedCallersOnly]
    private static void OnDeleteTextures(uint n, uint* names)
    {
        if (Budget() > 0 && names != null && n > 0 && n <= 4096)
        {
            // ADR 0023: names the engine holds that are OURS (a warm build handed them out) must survive its
            // Reset -- they are the cache. Only the rest reach the driver.
            uint* pass = stackalloc uint[(int)n];
            FilterRetire(n, names, out uint passN, pass);
            if (passN > 0 && _origDelete != null) _origDelete(passN, pass);
            return;
        }
        Retire(n, names);
        if (_origDelete != null) _origDelete(n, names);
    }

    /// <summary>The cache-mode delete: owned names are kept (and counted), engine names retire their index
    /// entry and put the resource to IDLE rather than discarding it. Returns the kept count; <paramref name="pass"/>
    /// (if given) receives the names the driver may still delete.</summary>
    private static uint FilterRetire(uint n, uint* names, out uint passN, uint* pass = null)
    {
        uint kept = 0; passN = 0;
        for (uint i = 0; i < n; i++)
        {
            uint name = names[i];
            _engineHeld.Remove(name);
            bool ours = _owned.Contains(name);
            if (ours) { kept++; _cacheKept++; ScrubEngineBoundCache(name); }   // the GL name survives; only its INDEX entry retires below -- and the engine's cache must not say it is bound (#461)
            else
            {
                if (pass != null) pass[passN] = name;
                passN++;
                ForgetBorrow((int)name);
            }
            if (!_index.TryGetValue((int)name, out ulong key)) continue;
            _index.Remove((int)name);
            _retired++;
            if (!_res.TryGetValue(key, out Entry e) || e.Idle != 0) continue;
            // Marked idle without asking whether another tid still points at it: that is an O(index) question
            // per name on the transition frame. Evict asks it once per pass and un-idles the ones still referenced.
            _res[key] = new Entry(e.Owned, e.Bytes, e.Gen, Math.Max(_dripFrame, 1));
            _idleBytes += e.Bytes;
            _pendingHandles.Add(e.Owned);   // #153's residency is per name; an idle resource holds no handle slot
            _evictDue = true;
        }
        return kept;
    }

    /// <summary>The delete handler's body, split out so <see cref="SelfCheck"/> can exercise it.</summary>
    private static void Retire(uint n, uint* names)
    {
        if (names == null) return;
        for (uint i = 0; i < n; i++)
        {
            int name = (int)names[i];
            _engineHeld.Remove((uint)name);
            ForgetBorrow(name);
            if (!_index.TryGetValue(name, out ulong key)) continue;
            _index.Remove(name);
            _retired++;
            if (!_res.TryGetValue(key, out Entry e)) continue;
            _res.Remove(key);
            // The engine is deleting this very name NOW (its cell was ours): a second delete from Drain lands
            // on whatever the driver has since given that name to -- immediate on Mesa, which is the
            // corrupted-UI-texture class. The handle is forgotten without GL: its texture is already gone.
            if (e.Owned == (uint)name) { _owned.Remove(e.Owned); _stats.Remove(e.Owned); _bytes -= e.Bytes; _pendingForget.Add(e.Owned); }
            else Discard(e);
        }
    }
    /// <summary>Owned names the engine deleted itself: records marked, handle tombstoned, no GL.</summary>
    private static readonly HashSet<uint> _pendingForget = new();
    /// <summary>Names whose delete is held for <see cref="DeferFrames"/> drains: the last draw that sampled
    /// their handle may still be in flight, and a driver that resolves bindless descriptors at batch
    /// completion (zink) faults on a destroyed image there. The records already carry the new name.</summary>
    private static readonly List<uint>[] _deferred = MakeDeferred();
    /// <summary>Read from <see cref="GpuGraveyard.FramesInFlight"/>: the same quantity, declared once.</summary>
    private const int DeferFrames = GpuGraveyard.FramesInFlight;

    /// <summary>One list per frame of the hold plus the one being filled, so the due slot is never the slot
    /// this drain writes.</summary>
    private static List<uint>[] MakeDeferred()
    {
        var a = new List<uint>[DeferFrames + 1];
        for (int i = 0; i < a.Length; i++) a[i] = new List<uint>();
        return a;
    }
    private static int _drainSeq;

    /// <summary>Is this name one of ours? The bindless path mints a handle only when it is -- a handle on an
    /// engine texture is a handle on an object the engine may delete or re-upload under us.</summary>
    internal static bool IsOwned(int name) => name > 0 && _owned.Contains((uint)name);

    /// <summary>A name Alloy itself deletes leaves the index NOW. The driver reissues a freed name at once,
    /// and an index entry that outlived its texture hands the next engine texture wearing that name the OLD
    /// resource's image. The engine's own deletes retire through <see cref="FilterRetire"/> /
    /// <see cref="Retire"/>.</summary>
    private static void Forget(uint name) { _index.Remove((int)name); ForgetBorrow((int)name); }
    /// <summary>Fresh engine names that arrived at a funnel still indexed -- entries that outlived their
    /// texture. Expected 0; anything else names a delete path <see cref="Forget"/> does not cover.</summary>
    private static long _indexStale;
    /// <summary>Frames since the table was attached, counted on the frame anchor: the clock an idle
    /// resource's stamp above, the eviction order and the census settle window are measured in.</summary>
    private static int _dripFrame;
    /// <summary>The engine just generated <paramref name="name"/> (GenTextures in glImageBasic), so an index
    /// entry on it can only be stale: drop it, count it. An OWNED name here is a refresh into our own name.</summary>
    private static void FreshName(uint name) { if (!_owned.Contains(name) && _index.Remove((int)name)) _indexStale++; }

    /// <summary>The name an owned draw should sample for this engine texture id: Alloy's, or
    /// <paramref name="engineTid"/> itself when the table is stood down or the texture never came through the
    /// upload funnel. Hot -- per Alloy part per frame -- two dictionary probes and no GL. Its one allocation
    /// is the borrowed set growing, once per DISTINCT engine name Alloy never adopted.</summary>
    internal static int Draw(int engineTid)
    {
        if (!_armed || engineTid <= 0) return engineTid;
        if (_index.TryGetValue(engineTid, out ulong key) && _res.TryGetValue(key, out Entry e)) return (int)e.Owned;
        if (_borrowed.Add(engineTid)) CountBorrow(engineTid);
        return engineTid;
    }

    /// <summary>Delete what <see cref="Retire"/> and the re-upload path queued. Once per frame from inside the
    /// 4.6 bracket, where the names actually live; nothing to do is the normal case. Residency first, because
    /// a resident bindless handle outlives the texture it names: the drop goes through the handle cache, since
    /// asking the driver would MINT handles for textures that never had one.</summary>
    internal static void Drain()
    {
        // Evict FIRST: its discards join this frame's batch, and its un-idle of a referenced key keeps that
        // handle (it used to remove from a set already dropped and cleared). Then the per-slot marks for the
        // whole batch, then the drops -- no epoch bump anywhere on this path.
        if (_evictDue) Evict();
        DropDeadHandles();
        // this drain's names are held; the ones queued DeferFrames drains ago go now
        int slot = _drainSeq++ % _deferred.Length;
        List<uint> due = _deferred[(slot + _deferred.Length - DeferFrames) % _deferred.Length];
        if (_pending.Count > 0) { _deferred[slot].AddRange(_pending); _pending.Clear(); }
        if (due.Count == 0) return;
        int n = due.Count;
        // auto: deletes are only unsafe where zink's bindless bookkeeping is live -- vetoed bindless = no handles = safe
        bool delete = _texDelete == 1 || (_texDelete < 0 && !(GlContextService.Zink && AlloyRenderer.Bindless));
        if (!delete && !_deleteNoted) { _deleteNoted = true; _log?.Info("OwnedTextures: owned textures are NEVER deleted this session (zink with bindless forced, or alloy.texdelete=0) -- names leak until the context goes"); }
        for (int i = 0; i < n; i++)
        {
            uint name = due[i];
            if (_handleDrop) AlloyRenderer.DropHandle(name);   // residency first: a resident handle outlives its texture
            else AlloyRenderer.ForgetHandle(name);
            Forget(name);
            if (delete && Gl.DeleteTextures != null) Gl.DeleteTextures(1, &name);
        }
        due.Clear();
        _log?.Info($"OwnedTextures: {(delete ? "released" : "retired (kept alive)")} {n} owned textures (engine names retired so far: {_retired})");
    }

    /// <summary>The handle half of <see cref="Drain"/>, callable ahead of it: the caster bake runs BEFORE the
    /// colour bracket's drain in the same frame, so it must first drop (and zero out of the records) every
    /// handle whose texture died since the last drain. Idempotent within a frame, and inside a 4.6 bracket.</summary>
    internal static void DropDeadHandles()
    {
        if (_pendingForget.Count == 0 && _pendingHandles.Count == 0 && _pendingMarked) return;
        if (_pendingForget.Count > 0)
        {
            foreach (uint f in _pendingForget) _pendingHandles.Add(f);   // for the record marks below
            AlloyRenderer.NoteOwnedNamesRetired(_pending, _pendingHandles);
            foreach (uint f in _pendingForget) { _pendingHandles.Remove(f); AlloyRenderer.ForgetHandle(f); }
            _pendingForget.Clear();
        }
        else AlloyRenderer.NoteOwnedNamesRetired(_pending, _pendingHandles);
        _pendingMarked = true;
        if (_pendingHandles.Count > 0)
        {
            foreach (uint h in _pendingHandles) AlloyRenderer.DropHandle(h);
            _pendingHandles.Clear();
        }
    }
    /// <summary>Every name in <c>_pending</c> has had its records marked; cleared by the one adder.</summary>
    private static bool _pendingMarked;
}
