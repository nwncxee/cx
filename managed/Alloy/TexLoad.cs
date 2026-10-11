using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CX.Engine;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy;

/// <summary>
/// Alloy reads and decodes a world texture's DDS or TGA on the <c>cx-load</c> worker, inside the window
/// <c>CNWCArea::LoadArea</c> suspends texture building in: across its tile loop every texture is named and queued
/// UNBUILT, and nothing is read, decoded or uploaded until the first <c>Scene::Render</c> after it returns. The
/// seam is the decoder slot; the engine still writes every field, mints every GL name, keeps every list.
/// </summary>
internal static unsafe partial class TexLoad
{
    /// <summary>The four restypes <c>CAuroraInterface::GetHighestPriorityTextureFormat</c> @0x1125480 probes,
    /// in its own order. Alloy prepares <see cref="DdsResType"/> and <see cref="TgaResType"/>.</summary>
    private const ushort KtxResType = 0x819, DdsResType = 0x7f1, Pc4ResType = 0x80b, TgaResType = 3;   // not-an-offset: restype ids
    /// <summary>Container kinds in the engine's priority order: a loose file beats NWSync beats a hak beats the
    /// base BIF (<c>CExoResMan::Exists</c>'s out-parameter is <see cref="EngineLayout.KeyTableKind"/>, not the
    /// priority -- the race compares container quality, and that is what this order is).</summary>
    private static ReadOnlySpan<int> KindOrder => [2, 5, 3, 1];
    private static ReadOnlySpan<ushort> FormatOrder => [KtxResType, DdsResType, Pc4ResType, TgaResType];

    /// <summary>Prepared textures held at once, and reads waiting. A heavy area queues a few hundred DDS inside
    /// <c>LoadArea</c> faster than one worker reads them; a refusal is counted as past the cap.</summary>
    private const int MaxEntries = 8192, MaxPending = 4096;
    /// <summary>Prepared bytes held at once; past it nothing more is offered, so a huge area cannot turn the
    /// prefetch into a second copy of its whole texture set in memory.</summary>
    private const long MaxHeldBytes = 512L << 20;
    /// <summary>Drips a prepared buffer may wait for its slot call before it is swept. The engine drains the
    /// whole queue within a frame or two of <c>LoadArea</c> returning; anything still here long after that was
    /// never going to be asked for.</summary>
    private const int MaxAgeDrips = 900;
    /// <summary>Drips between age sweeps: walking the whole table every frame would be the very main-thread
    /// cost this feature exists to remove.</summary>
    private const int SweepEvery = 120;
    private const int LogCap = 8;

    private static ExtenderLog? _log;
    private static bool _armed;
    private static readonly SlotJobTable<LoadJob, LoadEntry> _jobs = new(MaxEntries, MaxPending);
    private static readonly Handles _handles = new();
    private static long _held, _drip;
    /// <summary>Main-thread ticks spent in <see cref="Offer"/>, and ticks the worker has been busy (written by the
    /// worker as each job ends): what <see cref="TexStream"/>'s <c>[load]</c> line samples across <c>LoadArea</c>.</summary>
    internal static long OfferTicks, BusyTicks;
    /// <summary>ponytail: [diag] for the TGA gap (a module shipping TGA-only textures) -- offers the race found neither DDS nor TGA for, and
    /// the TGAs the engine still read, named (capped); drop once the gap is closed.</summary>
    private const int DiagCap = 40;
    private static readonly HashSet<long> _neitherKeys = new();
    private static int _diagNeither, _diagEngineTga;
    private static long _offered, _ready, _hits, _tgaHits, _misses, _refused, _resident, _neither, _nwsync, _swept, _bytes, _workTicks, _full, _late;

    /// <summary>What a worker carries: the location in, the prepared buffer out. Nothing in it points into
    /// engine memory, so nothing it holds can be freed under the worker.</summary>
    private sealed class LoadJob
    {
        public long Key;
        public ulong Sig;
        public TexLocation Loc;
        /// <summary>A DDS's level 0 (<see cref="DdsReader.PrefixBytes"/> of headroom lead it), or a TGA's pixels.</summary>
        public byte* Data;
        public DdsReader.Info Info;
        public RasterReader.Raster Tga;
        public string? Why;
        public long Ticks;
        public uint R0, R1, R2, R3;
    }

    /// <summary>A claim while the read is in flight, then the prepared buffer. The <c>RESID</c> is kept so the
    /// hand-back can re-prove identity against a fresh lookup: that closes both the hash-collision trap (the
    /// engine's own texture hash is anagram-blind, so a key alone is not an identity) and the alias one (a
    /// server can re-point any resref at runtime through the resman-override packet).</summary>
    private struct LoadEntry : ISlotEntry
    {
        public ulong Sig { get; set; }
        public IntPtr Data;
        public DdsReader.Info Info;
        public RasterReader.Raster Tga;
        /// <summary><see cref="DdsResType"/> or <see cref="TgaResType"/>: which slot takes it. Set on the claim too,
        /// because the engine asks the DDS slot first for every texture.</summary>
        public ushort ResType;
        public uint R0, R1, R2, R3;
        public long Bytes, Drip;
    }

    // ---- lifecycle ----

    /// <summary>Resolve the two globals, start nothing: the worker starts with the first offer.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        if (!ResLocate.Attach(out string? why))
        {
            log.Always($"TexLoad: REFUSED -- {why}; the engine reads every texture file on the main thread");
            return;
        }
        if (!Renderer.DdsRead.Boot)
        {
            log.Always("TexLoad: REFUSED -- the DDS decoder slot is the engine's (alloy.ddsread=0), so nothing prefetched here could ever be handed back; the engine reads every texture file on the main thread");
            return;
        }
        _armed = true;
        AttachRaster(log);
        log.Info($"TexLoad: armed -- every world DDS and TGA a material asks for (during an area load, inside LoadArea's texture suspend) is read and decoded on the cx-load worker");
    }

    internal static string Note()
        => !_armed ? "" : $"; texload: {_hits} served ({_tgaHits} TGA), {_misses} missed, {_ready} ready, {_bytes >> 20} MiB read, {_workTicks / (Stopwatch.Frequency / 1000)} ms on cx-load"
                          + $" (offered {_offered}, {_neither} neither DDS nor TGA, {_resident} resident, {_nwsync} from NWSync, {_refused} REFUSED, {_full} past the queue/hold cap, {_late} still reading at the hand-back, {_swept} swept)";

    // ---- the offer, main thread ----

    /// <summary>A texture a world material just put on the engine's queue. Resolves where its DDS or TGA lives and
    /// hands the read to the worker. Cold -- once per texture; during an area load that is inside <c>LoadArea</c>'s suspended
    /// window, and a material initialised mid-session offers too (a read nothing takes is swept).</summary>
    internal static void Offer(IntPtr basic)
    {
        long t0 = Stopwatch.GetTimestamp();
        OfferOne(basic);
        OfferTicks += Stopwatch.GetTimestamp() - t0;
    }

    private static void OfferOne(IntPtr basic)
    {
        if (!_armed || basic == IntPtr.Zero || new CAurTextureBasic(basic).Schemes != IntPtr.Zero) return;   // a scheme is PLT, left to the engine
        byte* name = NameOf(basic);
        if (name == null || *name == 0) return;
        long key = (long)ResLocate.Key(name);
        if (_jobs.Entries.ContainsKey(key)) return;
        _offered++;
        if (!_jobs.QueueHasRoom || _held >= MaxHeldBytes) { _full++; return; }
        ushort rt = Winner(name, out IntPtr table, out IntPtr entry);
        if (rt != DdsResType && rt != TgaResType)
        {
            _neither++;
            _neitherKeys.Add(key);
            if (TexStream.InTransition && ++_diagNeither <= DiagCap) _log?.Info($"TexLoad: [diag] offered {Marshal.PtrToStringAnsi((IntPtr)name)} (requested {Marshal.PtrToStringAnsi((IntPtr)Unsafe.AsPointer(ref new CAurTextureBasic(basic).Name))}): neither DDS nor TGA wins -- {Race(name)}");
            return;
        }
        uint* resId = stackalloc uint[ResLocate.ResIdWords];
        if (!Locate(table, entry, resId, out TexLocation loc, out string? why))
        {
            if (why == null) return;   // already counted by kind
            if (++_refused <= LogCap) _log?.Always($"TexLoad: {Marshal.PtrToStringAnsi((IntPtr)name)}: {why} -- the engine reads it");
            return;
        }
        LoadJob j = _jobs.Rent();
        j.Key = key; j.Sig = loc.TableId; j.Loc = loc; j.Data = null; j.Why = null; j.Ticks = 0;
        j.R0 = resId[0]; j.R1 = resId[1]; j.R2 = resId[2]; j.R3 = resId[3];
        _jobs.Start("cx-load", Worker);
        _jobs.Offer(j, key, loc.TableId);
        _jobs.Entries[key] = new LoadEntry { Sig = loc.TableId, ResType = rt };
    }

    /// <summary>Cached: a method group would mint a delegate at every offer, and the worker starts once.</summary>
    private static readonly Action<LoadJob> Worker = Work;

    /// <summary>The resolved name the engine will hand the decoder slot: <c>basic+0xa8</c>, or the requested
    /// name when <c>LoadImage</c> @0x487980 has not yet copied it across, which is exactly what it does.</summary>
    private static byte* NameOf(IntPtr basic)
    {
        var b = new CAurTextureBasic(basic);
        byte* resolved = (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref b.ResolvedName);
        return *resolved != 0 ? resolved : (byte*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref b.Name);
    }

    /// <summary>The engine's own format race, run without reading anything: <c>GetHighestPriorityTextureFormat</c>
    /// probes KTX, DDS, 4PC and TGA, then picks by CONTAINER KIND -- loose file &gt; NWSync &gt; hak &gt; base BIF
    /// -- taking the formats in that same order inside each kind. Returns the winning restype, 0 for none.</summary>
    private static ushort Winner(byte* name, out IntPtr table, out IntPtr entry)
    {
        table = entry = IntPtr.Zero;
        IntPtr* tables = stackalloc IntPtr[FormatOrder.Length];
        IntPtr* entries = stackalloc IntPtr[FormatOrder.Length];
        int* kinds = stackalloc int[FormatOrder.Length];
        IntPtr resman = ResLocate.ResMan;
        if (resman == IntPtr.Zero) return 0;
        for (int f = 0; f < FormatOrder.Length; f++)
        {
            kinds[f] = 0;
            if (EngineFn.ResManGetKeyEntry(resman, name, FormatOrder[f], &tables[f], &entries[f], 0) == 0) continue;
            kinds[f] = new CExoKeyTable(tables[f]).Kind;
        }
        foreach (int k in KindOrder)
            for (int f = 0; f < FormatOrder.Length; f++)
                if (kinds[f] == k)
                {
                    table = tables[f]; entry = entries[f];
                    return FormatOrder[f];
                }
        return 0;
    }

    /// <summary>[diag] What each format probe found: the container kind per restype, 0 for absent.</summary>
    private static string Race(byte* name)
    {
        IntPtr resman = ResLocate.ResMan;
        var sb = new StringBuilder();
        for (int f = 0; f < FormatOrder.Length; f++)
        {
            IntPtr table, entry;
            int kind = resman != IntPtr.Zero && EngineFn.ResManGetKeyEntry(resman, name, FormatOrder[f], &table, &entry, 0) != 0
                ? new CExoKeyTable(table).Kind : 0;
            sb.Append($"{(f > 0 ? ", " : "")}0x{FormatOrder[f]:x} kind {kind}");
        }
        return sb.ToString();
    }

    /// <summary>[diag] Why a TGA the engine just read at the raster slot was not ours.</summary>
    internal static void NoteEngineTga(IntPtr name)
    {
        if (!_armed || !TexStream.InTransition || ++_diagEngineTga > DiagCap) return;
        long key = (long)ResLocate.Key((byte*)name);
        string why = _jobs.Entries.TryGetValue(key, out LoadEntry e)
                ? $"an entry is held (restype 0x{e.ResType:x}, {(e.Data == IntPtr.Zero ? "still reading" : "ready")})"
            : _neitherKeys.Contains(key) ? "offered, but the race found neither DDS nor TGA"
            : "never offered";
        _log?.Info($"TexLoad: [diag] the engine read TGA {Marshal.PtrToStringAnsi(name)} -- {why}");
    }

    /// <summary><see cref="ResLocate.Locate"/> with the two kinds it names counted here: a resident resource
    /// (the engine answers it from memory) and an NWSync entry.</summary>
    private static bool Locate(IntPtr tablePtr, IntPtr entryPtr, uint* resId, out TexLocation loc, out string? why)
    {
        bool ok = ResLocate.Locate(tablePtr, entryPtr, resId, out loc, out why, out ResLocate.Source src);
        if (src == ResLocate.Source.Resident) _resident++;
        else if (src == ResLocate.Source.NwSync) _nwsync++;
        return ok;
    }

    // ---- the worker ----

    /// <summary>One read + parse + chain layout, on <c>cx-load</c>. Touches no engine memory and no GL: the
    /// location record was copied for it at the offer, and the buffer it allocates is its own until the main
    /// thread files it.</summary>
    private static void Work(LoadJob j)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            if (j.Loc.Kind == KindNwSync) NwSync(j);
            else if (_handles.Read(j.Loc, out ReadOnlySpan<byte> raw, out j.Why)) Prepare(j, raw);
        }
        catch (Exception e) { j.Why = e.Message; }
        j.Ticks = Stopwatch.GetTimestamp() - t0;
        Interlocked.Add(ref BusyTicks, j.Ticks);
    }

    private static void Prepare(LoadJob j, ReadOnlySpan<byte> raw)
    {
        if (j.Loc.ResType == TgaResType) j.Data = RasterReader.PrepareTga(raw, out j.Tga, out j.Why);
        else j.Data = DdsReader.Prepare(raw, out j.Info, out j.Why);
    }

    /// <summary>The worker's NWSync read: <see cref="NwSyncReader"/>'s decompressed blob, prepared in place and freed.</summary>
    private static void NwSync(LoadJob j)
    {
        Span<byte> name = stackalloc byte[j.Loc.Path.Length];
        System.Text.Encoding.ASCII.GetBytes(j.Loc.Path, name);
        if (!NwSyncReader.TryRead(name, j.Loc.ResType, out byte* data, out int size))
        {
            j.Why = "NWSync: no row (counted on the nwsync note)";
            return;
        }
        try { Prepare(j, new ReadOnlySpan<byte>(data, size)); }
        finally { NativeMemory.Free(data); }
    }

    // ---- the publish, main thread ----

    /// <summary>Once a frame on the <see cref="OwnedTextures.DripRebuilds"/> anchor: file what the worker
    /// finished and sweep what nothing ever asked for.</summary>
    internal static void Drip()
    {
        if (!_armed) return;
        _drip++;
        SwapRaster();
        Publish();
        Sweep();
    }

    /// <summary>File everything the worker finished. Also run from <see cref="Take"/>: the engine's drain lands at
    /// the top of the first frame after <c>LoadArea</c>, before that frame's drip, so without it every read the
    /// worker finished inside <c>LoadArea</c> would still be unfiled when the engine asks for it.</summary>
    private static void Publish()
    {
        while (_jobs.TryTake(out LoadJob j))
        {
            if (j.Data == null)
            {
                _jobs.Entries.Remove(j.Key);   // the engine reads it, and a later offer may try again
                if (++_refused <= LogCap) _log?.Always($"TexLoad: {j.Loc.Path}+{j.Loc.Offset}: {j.Why ?? "no bytes"} -- the engine reads it");
            }
            else
            {
                bool tga = j.Loc.ResType == TgaResType;
                long bytes = tga ? j.Tga.Bytes + RasterReader.TgaTagTail : DdsReader.PrefixBytes + j.Info.ChainBytes;
                var e = new LoadEntry { Sig = j.Sig, Data = (IntPtr)j.Data, Info = j.Info, Tga = j.Tga, ResType = j.Loc.ResType,
                                        Bytes = bytes, Drip = _drip, R0 = j.R0, R1 = j.R1, R2 = j.R2, R3 = j.R3 };
                if (_jobs.File(j.Key, j.Sig, e)) { _held += bytes; _bytes += bytes; _ready++; }
                else Free(e);
            }
            _workTicks += j.Ticks;
            j.Data = null; j.Loc = default; j.Why = null;
            _jobs.Recycle(j);
        }
    }

    /// <summary>Drop prepared buffers nothing asked for. Bounded work: only when something is held.</summary>
    private static void Sweep()
    {
        bool overBudget = _held >= MaxHeldBytes;
        if (_held == 0 || _jobs.Entries.Count == 0 || (!overBudget && _drip % SweepEvery != 0)) return;
        List<long>? dead = null;
        foreach (var kv in _jobs.Entries)
        {
            if (kv.Value.Data == IntPtr.Zero) continue;
            if (!overBudget && _drip - kv.Value.Drip < MaxAgeDrips) continue;
            (dead ??= new List<long>()).Add(kv.Key);
        }
        if (dead == null) return;
        for (int i = 0; i < dead.Count; i++)
        {
            if (!_jobs.Entries.TryGetValue(dead[i], out LoadEntry e)) continue;
            Free(e);
            _held -= e.Bytes;
            _swept++;
            _jobs.Entries.Remove(dead[i]);
        }
    }

    // ---- the hand-back, main thread, inside the engine's LoadImage ----

    /// <summary>The prepared level-0 pointer for <paramref name="name"/>, or null; ownership passes to the caller,
    /// which hands it to the engine, and the entry is dropped either way. Identity is re-proved against the same
    /// lookup the engine is about to make -- same key entry, same <c>RESID</c> -- so a hash collision or a runtime
    /// resref alias is a miss rather than the wrong texture.</summary>
    internal static byte* Take(IntPtr name, out DdsReader.Info info)
    {
        byte* data = Take(name, DdsResType, out LoadEntry e);
        info = e.Info;
        return data;
    }

    /// <summary><see cref="Take(IntPtr, out DdsReader.Info)"/> for the raster slot.</summary>
    internal static byte* TakeTga(IntPtr name, out RasterReader.Raster tga)
    {
        byte* data = Take(name, TgaResType, out LoadEntry e);
        tga = e.Tga;
        if (data != null) _tgaHits++;
        return data;
    }

    /// <summary>A texture prepared as another format is not this slot's: the engine asks the DDS slot first for
    /// every texture, so that is the race walking past, not a miss.</summary>
    private static byte* Take(IntPtr name, ushort resType, out LoadEntry e)
    {
        e = default;
        if (!_armed || name == IntPtr.Zero) return null;
        long key = (long)ResLocate.Key((byte*)name);
        if (!_jobs.Entries.TryGetValue(key, out e)) { if (resType == DdsResType) _misses++; e = default; return null; }   // every build visits the DDS slot first: counted once there
        if (e.ResType != resType) { e = default; return null; }
        if (e.Data == IntPtr.Zero)
        {
            Publish();
            if (!_jobs.Entries.TryGetValue(key, out e) || e.Data == IntPtr.Zero) { _misses++; _late++; e = default; return null; }
        }
        _jobs.Entries.Remove(key);
        _held -= e.Bytes;
        if (!StillAt((byte*)name, e))
        {
            Free(e);
            _misses++;
            e = default;
            return null;
        }
        _hits++;
        return (byte*)e.Data;
    }

    /// <summary>Release a prepared buffer the engine never took, by its format's layout.</summary>
    private static void Free(in LoadEntry e)
    {
        if (e.ResType == TgaResType) NativeMemory.Free((void*)e.Data);
        else DdsReader.FreePrepared((byte*)e.Data);
    }

    /// <summary>The engine's own lookup, again: same table, same entry, same location.</summary>
    private static bool StillAt(byte* name, in LoadEntry e)
        => ResLocate.StillAt(name, e.ResType, e.R0, e.R1, e.R2, e.R3);
}
