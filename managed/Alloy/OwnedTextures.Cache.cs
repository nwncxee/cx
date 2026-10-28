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

/// <summary>How long a built texture is kept: the VRAM-budgeted cache whose world edge retires the INDEX while
/// the resource stays warm.</summary>
internal static unsafe partial class OwnedTextures
{
    // ---- ADR 0023: the texture CACHE -- the delete funnel is the world edge for the INDEX, not the resource ----

    /// <summary>VRAM the cache may hold, in bytes. 0 = the pre-ADR-0023 behaviour (a retired index entry
    /// discards its resource). <c>alloy.texcache</c> in cx.cfg; auto (the default) = a quarter of the probe's
    /// reported VRAM, 1024 MiB when the device answers no memory query. Resolved lazily: the probe runs on
    /// the first presented frame, after this table is attached.</summary>
    private static long _budgetBytes = -1;
    private static int _budgetCfgMb = -1;
    private const int BudgetFallbackMib = 1024;
    private static long Budget()
    {
        if (_budgetBytes >= 0) return _budgetBytes;
        if (_budgetCfgMb >= 0) return _budgetBytes = (long)_budgetCfgMb * OneMiB;
        if (!AlloyRenderer.VramProbed) return -1;   // not probed yet: "unknown", not the fallback
        // a quarter of the card: NVX reports the total, Mesa's ATI_meminfo only the free pool (most of the
        // card at probe time), and a driver with neither gets the fallback
        long total = AlloyRenderer.VramTotalMib, free = AlloyRenderer.VramFreeMib;
        return _budgetBytes = (total > 0 ? total / 4 : free > 0 ? free / 4 : BudgetFallbackMib) * OneMiB;
    }
    private static long _idleBytes, _cacheHits, _cacheMisses, _cacheEvicted, _cacheKept;
    private static bool _evictDue;
    private static int _texDelete = -1;
    private static bool _handleDrop = true, _deleteNoted;
    private static readonly List<ulong> _unidle = new();
    /// <summary>Handles to release for resources that went idle -- GL, so drained in the bracket like <see cref="_pending"/>.</summary>
    private static readonly HashSet<uint> _pendingHandles = new();

    /// <summary>Post-build scalars of a plain texture, keyed on its cell-0 content key: what the warm path
    /// writes back into a fresh <c>CAurTextureBasic</c> so the engine believes it built the texture. Captured
    /// from the object after its cold build -- nothing here is derived, every field is what the engine's own
    /// loader left (numX/numY included; their writer is outside the class and was not chased).</summary>
    private struct TexMeta { public int W, H, Bpp, Compressed, NumX, NumY; public float Alpha; }
    private static readonly Dictionary<ulong, TexMeta> _meta = new();
    /// <summary>Content keys with a transcode queued or in flight: not warm-eligible (the pipeline is about
    /// to retire the name the engine would be handed).</summary>
    private static readonly HashSet<ulong> _inFlight = new();
    private static AurTextureBuildAndStoreTextureFn _origBuildAndStoreTexture;
    private static OperatorNewArrayFn _znam;
    private const int PltMarker = 4;

    /// <summary>Is this a texture the cache may stand in for? Plain 2D, no PLT scheme, no animation
    /// controller, not the engine's never-build case. Everything else takes the cold path unchanged.</summary>
    private static bool Plain(IntPtr tex)
    {
        var b = new CAurTextureBasic(tex);
        return b.FlagNeverBuild == 0 && b.FlagCube == 0 && b.Schemes == IntPtr.Zero
               && b.PltKind != PltMarker && b.Controller == IntPtr.Zero;
    }

    [UnmanagedCallersOnly]
    private static void OnBuildAndStoreTexture(IntPtr tex)
    {
        if (_armed && Budget() > 0 && Plain(tex) && TryWarm(tex)) return;
        if (_origBuildAndStoreTexture != null) _origBuildAndStoreTexture(tex);
        if (_armed && Budget() > 0) Capture(tex);
    }

    /// <summary>The cold build just finished: remember what it produced, if every cell came through the
    /// funnel as an owned resource.</summary>
    private static void Capture(IntPtr tex)
    {
        var b = new CAurTextureBasic(tex);
        if (!AurTex.IsBasic(tex) || !Plain(tex) || b.Image != IntPtr.Zero || b.ResolvedName == 0) return;
        uint* cells = (uint*)b.CellNames;
        int nx = b.NumX, ny = b.NumY;
        if (cells == null || nx <= 0 || ny <= 0 || b.FlagBuilt == 0) return;
        for (int c = 0; c < nx * ny; c++)
            if (!_index.TryGetValue((int)cells[c], out ulong k) || !_res.ContainsKey(k)) return;
        _meta[Key(tex, 0)] = new TexMeta
        {
            W = b.Width, H = b.Height, Bpp = b.Bpp,
            Compressed = b.CompressedSize, NumX = nx, NumY = ny,
            Alpha = b.AlphaMean,
        };
    }

    /// <summary>The warm path: every cell of this texture is resident, so hand the engine Alloy's names and
    /// the scalars its build would have produced, and skip the read, the decode and the upload. The engine
    /// then binds our names for its own 2D use and, on its next Reset, hands them to
    /// DeleteTextures -- where <see cref="OnDeleteTextures"/> keeps them. Refuses on any doubt.</summary>
    private static bool TryWarm(IntPtr tex)
    {
        if (_znam == null || !AurTex.IsBasic(tex) || new CAurTextureBasic(tex).ResolvedName == 0) return false;   // the resolved name is empty until LoadImage fills it: no identity yet
        ulong k0 = Key(tex, 0);
        if (!_meta.TryGetValue(k0, out TexMeta m)) { _cacheMisses++; return false; }
        int cells = m.NumX * m.NumY;
        if (cells <= 0 || cells > 4096) return false;
        uint* names = stackalloc uint[cells > 64 ? 1 : cells];
        uint[]? big = cells > 64 ? new uint[cells] : null;
        for (int c = 0; c < cells; c++)
        {
            ulong k = (k0 & ~CellMask) | (uint)c;
            if (_inFlight.Contains(k) || !_res.TryGetValue(k, out Entry e)) { _cacheMisses++; return false; }
            if (big != null) big[c] = e.Owned; else names[c] = e.Owned;
        }
        IntPtr arrPtr = _znam((nuint)cells * 4);
        if (arrPtr == IntPtr.Zero) return false;
        Span<uint> arr = new((void*)arrPtr, cells);
        for (int c = 0; c < cells; c++)
        {
            uint name = big != null ? big[c] : names[c];
            arr[c] = name;
            _engineHeld.Add(name);
            ulong k = (k0 & ~CellMask) | (uint)c;
            _index[(int)name] = k;
            Entry e = _res[k];
            if (e.Idle != 0) { _idleBytes -= e.Bytes; _res[k] = new Entry(e.Owned, e.Bytes, e.Gen, 0); _pendingHandles.Remove(e.Owned); }   // live again: its handle must not be dropped under a record
        }
        FillWarm(tex, arrPtr, in m);
        _cacheHits++;
        FrameZones.Count(FrameZones.Ev.TexWarm);
        return true;
    }

    /// <summary>The field writes alone, so the self-check can run them over a stand-in object.</summary>
    private static void FillWarm(IntPtr tex, IntPtr names, in TexMeta m)
    {
        var b = new CAurTextureBasic(tex);
        b.CellNames = names;
        b.GlId = *(int*)names;
        b.NumX = (short)m.NumX; b.NumY = (short)m.NumY;
        b.Width = m.W; b.Height = m.H; b.Bpp = m.Bpp;
        b.CompressedSize = m.Compressed;
        b.AlphaMean = m.Alpha;
        b.Image = IntPtr.Zero;
        b.FlagBuilt = 1; b.FlagNoImage = 0;
    }

    /// <summary>Evict idle resources, oldest first, until the cache fits its budget. Bracket-side (it
    /// discards GL names); cold -- runs only after a retire or a swap raised <see cref="_evictDue"/>.</summary>
    private static void Evict()
    {
        _evictDue = false;
        bool all = _flushAll; _flushAll = false;
        long budget = all ? 0 : Budget();
        if (!all && (budget <= 0 || _bytes <= budget)) return;
        var referenced = new HashSet<ulong>(_index.Values);
        var idle = new List<KeyValuePair<ulong, Entry>>();
        foreach (var kv in _res)
        {
            if (kv.Value.Idle == 0) continue;
            if (referenced.Contains(kv.Key)) { _idleBytes -= kv.Value.Bytes; _pendingHandles.Remove(kv.Value.Owned); _unidle.Add(kv.Key); continue; }
            idle.Add(kv);
        }
        foreach (ulong k in _unidle) { Entry e = _res[k]; _res[k] = new Entry(e.Owned, e.Bytes, e.Gen, 0); }
        _unidle.Clear();
        idle.Sort((a, b) => a.Value.Idle.CompareTo(b.Value.Idle));
        int n = 0;
        foreach (var kv in idle)
        {
            if (!all && _bytes <= budget) break;
            _res.Remove(kv.Key);
            _meta.Remove(kv.Key & ~CellMask);
            _idleBytes -= kv.Value.Bytes;
            Discard(kv.Value);   // the record marks land in Drain, once for the whole batch
            n++;
        }
        _cacheEvicted += n;
        FrameZones.Count(FrameZones.Ev.TexEvict, n);
    }

    /// <summary>The MODULE edge. The cache is keyed on resref + schemes, and two modules ship different pixels
    /// under one resref, so a resource that outlives its module would be handed to the next one wearing the
    /// wrong image. Meta goes now (no warm hand-back from here on), the idle bytes on the next drain; names
    /// the index still references stay, because they draw what they name.</summary>
    internal static void ModuleUnloaded()
    {
        _meta.Clear();
        _flushAll = true; _evictDue = true; _cacheFlushes++;
    }

    private static bool _flushAll;
    private static long _cacheFlushes;

    internal static string CacheNote()
    {
        long b = Budget();
        return $"texcache: {_bytes / OneMiB} MiB resident ({_idleBytes / OneMiB} idle) of {(b > 0 ? (b / OneMiB).ToString() : b == 0 ? "off" : "unprobed")} MiB, " +
               $"{_cacheHits} warm builds, {_cacheMisses} misses, {_cacheKept} kept across the world edge, {_cacheEvicted} evicted, {_meta.Count} keys known, {_indexStale} stale index hits, {_cacheFlushes} module flushes";
    }

    /// <summary>Keep-then-evict, the delete filter, and the warm fill over a stand-in object.</summary>
    internal static void SelfCheckCache(ExtenderLog log)
    {
        bool ok = true;
        long svBudget = _budgetBytes; int svCfg = _budgetCfgMb; long svBytes = _bytes, svIdle = _idleBytes;
        var svRes = new Dictionary<ulong, Entry>(_res); var svIdx = new Dictionary<int, ulong>(_index);
        var svOwned = new HashSet<uint>(_owned); int svPend = _pending.Count, svPendH = _pendingHandles.Count;
        var svMeta = new Dictionary<ulong, TexMeta>(_meta);
        _res.Clear(); _index.Clear(); _owned.Clear(); _meta.Clear(); _bytes = 0; _idleBytes = 0;
        _budgetCfgMb = 1; _budgetBytes = OneMiB;   // 1 MiB budget

        int svDrip = _dripFrame;
        ulong kA = 0xA00000000UL, kB = 0xB00000000UL;
        Replace(kA, 11, 101, 600 * 1024); _dripFrame = 10;
        Replace(kB, 12, 102, 600 * 1024); _index[13] = kB; _dripFrame = 20;   // B under two engine names
        uint* dead = stackalloc uint[3] { 11, 102, 12 };
        // the engine retires its own name 11 (A idle, kept) and 12 (B marked idle though 13 still points at it),
        // and hands back OWNED name 102 (kept, filtered from the driver call)
        uint kept = FilterRetire(3, dead, out uint passN);
        ok &= passN == 2 && kept == 1 && _res.ContainsKey(kA) && _res[kA].Idle == 20 && !_index.ContainsKey(11)
              && _res.ContainsKey(kB) && _res[kB].Idle == 20 && _pendingHandles.Count == svPendH + 2;
        // over budget: the referenced one is un-idled (handle drop withdrawn), the idle one goes
        Evict();
        ok &= !_res.ContainsKey(kA) && _res.ContainsKey(kB) && _res[kB].Idle == 0 && _bytes == 600 * 1024
              && _idleBytes == 0 && !_pendingHandles.Contains(102);
        // #397: the module edge flushes every idle resource under ANY budget and forgets every key's meta
        _meta[kB & ~CellMask] = default; _budgetBytes = 1L << 40;
        uint* dead2 = stackalloc uint[1] { 13 };
        FilterRetire(1, dead2, out _);
        ModuleUnloaded(); Evict();
        ok &= !_res.ContainsKey(kB) && _meta.Count == 0 && _bytes == 0 && !_flushAll;
        _dripFrame = svDrip;
        // the warm fill over a stand-in object
        byte* obj = stackalloc byte[0x100];
        new Span<byte>(obj, 0x100).Clear();
        uint* arr = stackalloc uint[2] { 7, 8 };
        var m = new TexMeta { W = 64, H = 32, Bpp = 4, Compressed = 0, NumX = 2, NumY = 1, Alpha = 0.5f };
        FillWarm((IntPtr)obj, (IntPtr)arr, in m);
        var objView = new CAurTextureBasic((IntPtr)obj);
        ok &= objView.GlId == 7 && objView.NumX == 2 && objView.NumY == 1
              && objView.Width == 64 && objView.Height == 32 && objView.FlagBuilt == 1
              && objView.Image == IntPtr.Zero && objView.AlphaMean == 0.5f;
        // the key: an anagram family (the engine's hash folds these to ONE value) is two resources, case is
        // not, and the cell lives in the low field
        byte* obj2 = stackalloc byte[0x100];
        new Span<byte>(obj2, 0x100).Clear();
        static void Name(byte* o, string n) { byte* d = NameOf((IntPtr)o); for (int i = 0; i < n.Length; i++) d[i] = (byte)n[i]; d[n.Length] = 0; }
        Name(obj, "tex_core_0145_d"); Name(obj2, "tex_core_0154_d");
        ok &= Key((IntPtr)obj, 0) != Key((IntPtr)obj2, 0);
        Name(obj2, "TEX_CORE_0145_D");
        ok &= Key((IntPtr)obj, 0) == Key((IntPtr)obj2, 0) && (Key((IntPtr)obj, 3) & CellMask) == 3
              && (Key((IntPtr)obj, 3) & ~CellMask) == Key((IntPtr)obj, 0);

        _res.Clear(); foreach (var kv in svRes) _res[kv.Key] = kv.Value;
        _index.Clear(); foreach (var kv in svIdx) _index[kv.Key] = kv.Value;
        _owned.Clear(); foreach (uint o in svOwned) _owned.Add(o);
        _meta.Clear(); foreach (var kv in svMeta) _meta[kv.Key] = kv.Value;
        if (_pending.Count > svPend) _pending.RemoveRange(svPend, _pending.Count - svPend);
        _pendingHandles.Remove(101); _pendingHandles.Remove(102);
        _bytes = svBytes; _idleBytes = svIdle; _budgetBytes = svBudget; _budgetCfgMb = svCfg;
        log.Info(ok ? "OwnedTextures.SelfCheckCache: passed" : "OwnedTextures.SelfCheckCache: FAILED -- keep/evict/filter/fill/key drifted");
    }
}
