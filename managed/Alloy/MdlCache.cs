using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>A disk cache of ENGINE-COMPILED binary models for ASCII sources: an ASCII <c>.mdl</c> costs the main
/// thread a line-at-a-time interpreter plus the per-mesh post-process a binary file stores precomputed.
/// Nothing here writes an MDL -- the compiler is the engine's own (<see cref="SymbolMap.CompileModels"/>), so the
/// round trip and its refusals (already binary, uninitialized skinmesh) are the engine's and this only counts.</summary>
internal static unsafe class MdlCache
{
    /// <summary>"CXMD" little-endian.</summary>
    private const uint Magic = 0x444D5843;   // not-an-offset: the file's first four bytes
    /// <summary>Bumped with the header layout or with what a cached payload means. The client build is a separate
    /// header word: the payload is the ENGINE's serialisation, so a new <c>nwmain</c> invalidates every file here
    /// whatever its key. Version 1 payloads were serialised from models the engine had already INSTANTIATED, so
    /// they carry that session's radius growth and texture-rotate flag -- not repairable, only discarded.</summary>
    private const int Version = 2;
    /// <summary>magic, version, build, source hash, source length, payload length, resref.</summary>
    internal const int HeaderBytes = 4 + 4 + 8 + 8 + 4 + 4 + ResRefChars;
    private const int ResRefChars = 16;
    private const string Ext = ".mdl";

    /// <summary>The <c>{0, modelDataSize, rawDataSize}</c> wrapper every binary MDL starts with (the engine's
    /// writer emits it at @0x4bc560, its reader @0x4bee60 copies the two blocks back out). Checking that the
    /// two sizes add up to the file is the one fidelity assertion available without a running renderer.</summary>
    private const int WrapperBytes = 12;

    /// <summary>Bytes of zeroed headroom in front of a served buffer, matching <see cref="ModelLoad"/>: the
    /// engine writes ten of them (<c>ptr-10</c>, <c>ptr-8</c>) and leaving ours zero makes the engine's own
    /// release a no-op if our release chain ever misses one.</summary>
    private const int PrefixBytes = 16;

    /// <summary>Main-thread milliseconds one drip may spend compiling. One compile is a tree walk, two
    /// allocations and an ~100 KiB write; at this budget an area's ASCII models fill the cache over a second or
    /// two of play instead of hitching one frame.</summary>
    private const int CompileBudgetMs = 2;
    /// <summary>Models waiting to be compiled before new offers are dropped. An area names a few hundred.</summary>
    private const int MaxQueued = 4096;
    /// <summary>Cache bytes before the oldest files are swept, and writes between sweeps.</summary>
    private const long MaxDiskBytes = 512L << 20;
    private const int PruneEvery = 64;
    private const int LogCap = 8;

    /// <summary>Between <see cref="ModelZonesService"/>'s brackets (100) and <see cref="ModelLoad"/>'s serve
    /// (0): the measurement still spans everything, and this sees the bytes ModelLoad hands back.</summary>
    private const int ServePriority = 50;

    private static ExtenderLog? _log;
    private static bool _armed;
    private static AuroraGetResourceFn* _getSlot;
    private static AuroraReleaseResourceFn* _relSlot;
    private static IoReadSyncFn* _readSlot;
    private static IntPtr _exoBaseSym;
    private static string? _dir;
    /// <summary>Resolved on first use, never at <see cref="Install"/>: our loader runs BEFORE the engine's entry
    /// point, so <c>Native.UserDirectory</c> is still null there and caching the fallback would point the whole
    /// cache at the wrong directory for the session (the <c>ConfigFile</c> deferral rule).</summary>
    private static string Dir => _dir ??= Path.Combine(Native.UserDirectory() ?? Native.DefaultUserDirectory(), "cx-models");
    /// <summary>Where <c>IODispatcher::Write</c> drops its output: the <c>MODELCOMPILER</c> alias, resolved
    /// once. Null until <see cref="CompileDir"/> has asked the engine.</summary>
    private static string? _outDir;
    private static bool _outDirAsked;
    /// <summary>The build this cache's payloads were written by, folded into every header.</summary>
    private static ulong _buildKey;

    /// <summary>Buffers the engine is holding right now, so a release of ours is told from a release of the
    /// engine's without reading memory in front of a pointer we did not allocate.</summary>
    private static readonly HashSet<IntPtr> _out = new();

    /// <summary>A model whose ASCII source was seen with no cached compile, waiting for a cold point. Fed from
    /// both the main thread and the model-read workers, so every touch is under <see cref="Gate"/>.</summary>
    private readonly struct Want
    {
        internal Want(ulong hash, int srcLen) { Hash = hash; SrcLen = srcLen; }
        internal readonly ulong Hash;
        internal readonly int SrcLen;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Want> _wanted = new(StringComparer.Ordinal);
    private static readonly Queue<string> _order = new();
    /// <summary>Models the engine refused for good (a skinmesh it never post-processed, a name too long to be a
    /// resref): remembered so an area that loads one every transition is not re-offered every transition.</summary>
    private static readonly HashSet<string> _never = new(StringComparer.Ordinal);

    private static long _hitWorker, _hitMain, _mainTicks, _compiled, _compileTicks, _queued, _dropped,
                        _gone, _skinmesh, _binary, _noOutput, _bad, _errors, _said, _written, _pruned, _unproved,
                        _diskBytes = -1;

    // ---- lifecycle ----

    /// <summary>Chain the serve. Independent of <see cref="ModelLoad"/>: a model that worker did not read
    /// still reaches this link on the main thread.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        _log = log;
        _exoBaseSym = Native.Sym(SymbolMap.ExoBase);
        if (EngineFn.CompileModels == null || EngineFn.AliasGetAliasPath == null || _exoBaseSym == IntPtr.Zero)
        {
            log.Always("MdlCache: REFUSED -- compilemodelsinternal, CExoAliasList::GetAliasPath or g_pExoBase unresolved;"
                       + " every ASCII model is parsed line by line on the main thread");
            return;
        }
        if (!MdlCachePristine.Attach(log)) return;
        _getSlot = hooks.ChainAuroraGetResource(&OnGetResource, ServePriority);
        _relSlot = hooks.ChainAuroraReleaseResource(&OnReleaseResource, ServePriority);
        // the ONE point a model is provably pristine: the parse is complete and nothing has instantiated it,
        // because the caller that will has not been returned to yet
        _readSlot = hooks.ChainIoReadSync(&OnReadSync, ServePriority);
        if (_getSlot == null || _relSlot == null || _readSlot == null)
        {
            log.Always($"MdlCache: REFUSED -- CAuroraInterface::GetResource {(_getSlot != null ? "ok" : "MISSING")},"
                       + $" ReleaseResource {(_relSlot != null ? "ok" : "MISSING")},"
                       + $" IODispatcher::ReadSync {(_readSlot != null ? "ok" : "MISSING")};"
                       + " every ASCII model is parsed line by line on the main thread");
            return;
        }
        _buildKey = Hash(System.Text.Encoding.ASCII.GetBytes(EngineLayout.BuildKey));
        _armed = true;
        log.Info("MdlCache: armed -- an ASCII model is compiled once by the engine's own writer into cx-models/ beside cx.cfg,"
                 + " and every later load of the same source bytes is served the compiled binary");
    }

    internal static string Note()
        => !_armed ? "" : $"; mdlcache: {_hitWorker + _hitMain} hits ({_hitWorker} worker / {_hitMain} main,"
                          + $" {_mainTicks / (Stopwatch.Frequency / 1000)} ms main), {_compiled} compiled"
                          + $" ({_compileTicks / (Stopwatch.Frequency / 1000)} ms), {Waiting} waiting of {_queued} offered,"
                          + $" refused: {_skinmesh} skinmesh, {_binary} already binary, {_gone} gone, {_noOutput} no output,"
                          + $" {_bad} malformed, {_errors} errors, {_dropped} past the cap,"
                          + $" {_unproved} not provably pristine; {_written} written,"
                          + $" {_pruned} pruned, {(_diskBytes < 0 ? 0 : _diskBytes) >> 20} MiB"
                          + (MdlCachePristine.Note() is { Length: > 0 } pn ? "; " + pn : "");

    private static int Waiting { get { lock (Gate) return _order.Count; } }

    // ---- the key ----

    private const ulong Mix1 = 0xFF51AFD7ED558CCDUL;    // not-an-offset: murmur3's fmix64 multiplier
    private const ulong Mix2 = 0xC4CEB9FE1A85EC53UL;    // not-an-offset: murmur3's fmix64 multiplier
    private const ulong Golden = 0x9E3779B97F4A7C15UL;  // not-an-offset: the odd golden-ratio multiplier
    private const ulong Seed = 0x27D4EB2F165667C5UL;    // not-an-offset: an odd 64-bit seed

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Fmix(ulong z)
    {
        z ^= z >> 33; z *= Mix1; z ^= z >> 33; z *= Mix2; z ^= z >> 33;
        return z;
    }

    /// <summary>A 64-bit hash of the source bytes -- the whole identity of a cache entry, so container, hak order,
    /// NWSync generation and a builder's edit are all one question. A collision would serve the wrong model, so the
    /// filename carries the resref and the header repeats the source length: only same-resref, same-length,
    /// different-content bytes could ever reach it.</summary>
    internal static ulong Hash(ReadOnlySpan<byte> src)
    {
        ulong h = Seed ^ (ulong)(uint)src.Length;
        int i = 0;
        for (; i + 8 <= src.Length; i += 8)
            h = System.Numerics.BitOperations.RotateLeft(h ^ Fmix(BitConverter.ToUInt64(src.Slice(i, 8))), 27) * Golden + Seed;
        ulong tail = 0;
        for (; i < src.Length; i++) tail = (tail << 8) | src[i];
        return Fmix(h ^ tail);
    }

    /// <summary>The cache file for one source: resref for legibility and for the lookup, the content hash for
    /// identity. Pure -- the self-check round-trips it with no disk.</summary>
    internal static string FileName(string resref, ulong hash) => $"{resref}.{hash:x16}{Ext}";

    // ---- the file ----

    /// <summary>Write the 48-byte header for one entry. Pure.</summary>
    internal static void WriteHeader(Span<byte> h, ulong build, ulong hash, int srcLen, int payload, string resref)
    {
        h.Clear();
        BitConverter.TryWriteBytes(h, Magic);
        BitConverter.TryWriteBytes(h[4..], Version);
        BitConverter.TryWriteBytes(h[8..], build);
        BitConverter.TryWriteBytes(h[16..], hash);
        BitConverter.TryWriteBytes(h[24..], srcLen);
        BitConverter.TryWriteBytes(h[28..], payload);
        for (int i = 0; i < resref.Length && i < ResRefChars; i++) h[32 + i] = (byte)resref[i];
    }

    /// <summary>The inverse: the payload length this file claims, or a refusal word. Every field is re-proved
    /// against what the caller asked for, so a build bump, a format bump, a truncated file or a hash collision
    /// with a different source length is a miss and never stale geometry. Pure.</summary>
    internal static string? ReadHeader(ReadOnlySpan<byte> file, ulong build, ulong hash, int srcLen, string resref, out int payload)
    {
        payload = 0;
        if (file.Length < HeaderBytes) return "truncation";
        if (BitConverter.ToUInt32(file) != Magic || BitConverter.ToInt32(file[4..]) != Version) return "version";
        if (BitConverter.ToUInt64(file[8..]) != build) return "build";
        if (BitConverter.ToUInt64(file[16..]) != hash || BitConverter.ToInt32(file[24..]) != srcLen) return "key";
        int n = BitConverter.ToInt32(file[28..]);
        if (n < WrapperBytes || HeaderBytes + n != file.Length) return "truncation";
        for (int i = 0; i < ResRefChars; i++)
        {
            byte want = i < resref.Length ? (byte)resref[i] : (byte)0;
            if (file[32 + i] != want) return "key";
        }
        payload = n;
        return null;
    }

    /// <summary>The <c>{0, modelDataSize, rawDataSize}</c> wrapper and its two blocks account for the whole
    /// payload -- the only fidelity assertion available off-device. Pure.</summary>
    internal static bool WrapperAddsUp(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < WrapperBytes || BitConverter.ToUInt32(payload) != 0) return false;
        long model = BitConverter.ToUInt32(payload[4..]), raw = BitConverter.ToUInt32(payload[8..]);
        return WrapperBytes + model + raw == payload.Length;
    }

    /// <summary>The compiled payload for a source, or null. Never throws: a miss is the engine's own read.</summary>
    private static byte[]? Payload(string resref, ulong hash, int srcLen)
    {
        try
        {
            string p = Path.Combine(Dir, FileName(resref, hash));
            if (!File.Exists(p)) return null;
            byte[] file = File.ReadAllBytes(p);
            string? why = ReadHeader(file, _buildKey, hash, srcLen, resref, out int n);
            if (why != null) { File.Delete(p); return null; }
            return file[HeaderBytes..];
        }
        catch (Exception e)
        {
            System.Threading.Interlocked.Increment(ref _errors);
            Say($"MdlCache: reading {resref}: {e.Message} -- the engine reads the ASCII source");
            return null;
        }
    }

    // ---- the worker serve (path a): zero main-thread cost ----

    /// <summary>On a model-read worker (<c>cx-mdl</c>/<c>cx-model</c>) with an <c>.mdl</c> just read: the compiled
    /// bytes for an already-cached ASCII source, else <paramref name="raw"/> unchanged plus an offer to compile it.
    /// The returned span is the caller's to copy before it returns. Serving binary on this first ask sends the
    /// ASCII path's second, <c>dofile</c> read down its binary branch, so that read never happens.</summary>
    internal static ReadOnlySpan<byte> Worker(byte[] name, ReadOnlySpan<byte> raw)
    {
        if (!_armed || raw.Length == 0 || raw[0] == 0) return raw;   // empty, or already binary
        string resref = ResRef(name);
        if (resref.Length == 0) return raw;
        ulong h = Hash(raw);
        byte[]? hit = Payload(resref, h, raw.Length);
        if (hit == null) { Queue(resref, h, raw.Length); return raw; }
        System.Threading.Interlocked.Increment(ref _hitWorker);
        return hit;
    }

    private static string ResRef(byte[] name)
    {
        int n = Array.IndexOf(name, (byte)0);
        if (n < 0) n = name.Length;
        return n == 0 ? "" : System.Text.Encoding.ASCII.GetString(name, 0, n).ToLowerInvariant();
    }

    // ---- the main-thread serve (path b) ----

    /// <summary>Whether <paramref name="ext"/> names a model, by the engine's own test.</summary>
    private static bool IsMdl(byte* ext)
    {
        if (ext == null) return false;
        byte* p = *ext == (byte)'.' ? ext + 1 : ext;
        return (p[0] | 0x20) == 'm' && (p[1] | 0x20) == 'd' && (p[2] | 0x20) == 'l' && p[3] == 0;
    }

    [UnmanagedCallersOnly]
    private static byte* OnGetResource(byte* name, byte* ext, int* size)
    {
        byte* got = (*_getSlot)(name, ext, size);
        if (!_armed || got == null || size == null || *size <= 0 || *got == 0 || name == null || *name == 0 || !IsMdl(ext))
            return got;   // nothing, already binary, or not a model: the engine's own bytes stand
        long t0 = Stopwatch.GetTimestamp();
        byte* ours = Swap(name, got, *size, out int n);
        _mainTicks += Stopwatch.GetTimestamp() - t0;
        if (ours == null) return got;
        (*_relSlot)((IntPtr)got);   // the engine's ASCII buffer goes back down the chain it came from
        *size = n;
        _hitMain++;
        return ours;
    }

    /// <summary>The compiled bytes for an ASCII buffer the engine is about to parse, in an allocation laid out
    /// as the engine's own, or null with the source offered for compiling instead.</summary>
    private static byte* Swap(byte* name, byte* src, int len, out int size)
    {
        size = 0;
        string resref = Marshal.PtrToStringAnsi((IntPtr)name)!.ToLowerInvariant();
        if (resref.Length == 0) return null;
        ulong h = Hash(new ReadOnlySpan<byte>(src, len));
        byte[]? payload = Payload(resref, h, len);
        if (payload == null) { Queue(resref, h, len); return null; }
        byte* block = (byte*)NativeMemory.AllocZeroed((nuint)(PrefixBytes + payload.Length));
        byte* data = block + PrefixBytes;
        payload.CopyTo(new Span<byte>(data, payload.Length));
        _out.Add((IntPtr)data);
        size = payload.Length;
        return data;
    }

    [UnmanagedCallersOnly]
    private static void OnReleaseResource(IntPtr data)
    {
        if (_armed && data != IntPtr.Zero && _out.Remove(data)) { NativeMemory.Free((byte*)data - PrefixBytes); return; }
        (*_relSlot)(data);
    }

    // ---- the compile ----

    /// <summary>Offer a model for compiling. Called from both threads.</summary>
    private static void Queue(string resref, ulong hash, int srcLen)
    {
        if (resref.Length > ResRefChars) { lock (Gate) _never.Add(resref); return; }   // not a resref: the compile call would truncate it
        lock (Gate)
        {
            if (_wanted.ContainsKey(resref) || _never.Contains(resref)) return;
            if (_order.Count >= MaxQueued) { _dropped++; return; }
            _wanted[resref] = new Want(hash, srcLen);
            _order.Enqueue(resref);
            _queued++;
        }
    }

    /// <summary>Once a frame: compile what is waiting, at a cold point only and under a budget. Never inside
    /// <c>LoadArea</c> -- the whole point is that the compile is paid when nobody is waiting on the frame.</summary>
    internal static void Drip()
    {
        if (!_armed) return;
        lock (Gate) { if (_order.Count == 0) return; }
        if (!TexStream.ColdPoint) return;
        if (_outDirAsked && _outDir == null) { lock (Gate) { _order.Clear(); _wanted.Clear(); } return; }
        long t0 = Stopwatch.GetTimestamp(), budget = CompileBudgetMs * Stopwatch.Frequency / 1000;
        do
        {
            string resref;
            Want w;
            lock (Gate)
            {
                if (_order.Count == 0) break;
                resref = _order.Dequeue();
                if (!_wanted.Remove(resref, out w)) continue;
            }
            Compile(resref, w);
        } while (Stopwatch.GetTimestamp() - t0 < budget);
        _compileTicks += Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>One model through the engine's own compiler, then into the cache. Every outcome is counted and
    /// none of them can make a load fail: a refusal leaves exactly today's behaviour, the ASCII parse.</summary>
    private static void Compile(string resref, Want w)
    {
        string? outDir = CompileDir();
        if (outDir == null) return;
        byte* name = stackalloc byte[ResRefChars + 1];
        int n = 0;
        for (; n < resref.Length && n < ResRefChars; n++) name[n] = (byte)resref[n];
        name[n] = 0;
        // nothing is cached from a model this did not see pristine: a missing or stale snapshot is left for the
        // next session rather than serialised with this session's render state baked into it
        IntPtr live = EngineFn.BinaryFindModel == null ? IntPtr.Zero : EngineFn.BinaryFindModel(name);
        if (!MdlCachePristine.Begin(resref, live, out float[] saved))
        {
            _unproved++;
            MdlCachePristine.Forget(resref);
            return;
        }
        byte* msg;
        try { msg = EngineFn.CompileModels(1, name); }   // asciiOnly: an already-binary model is the engine's own refusal
        finally { MdlCachePristine.End(resref, saved); }
        if (Starts(msg, "Success: Compiled")) { _gone++; return; }   // no model of that name is loaded any more
        if (!Starts(msg, "Successfully compiled"))
        {
            if (Contains(msg, "skinmesh")) _skinmesh++;
            else if (Contains(msg, "binary format")) _binary++;
            else { _errors++; Say($"MdlCache: the engine refused to compile {resref}: {Marshal.PtrToStringAnsi((IntPtr)msg)?.TrimEnd()}"); }
            lock (Gate) _never.Add(resref);   // the engine's verdict on this model does not change within a session
            return;
        }
        _compiled++;
        try
        {
            string? produced = Produced(outDir, resref);
            if (produced == null) { _noOutput++; Say($"MdlCache: the engine reported {resref} compiled but no file appeared in {outDir} -- nothing cached"); return; }
            byte[] payload = File.ReadAllBytes(produced);
            File.Delete(produced);
            if (!WrapperAddsUp(payload)) { _bad++; Say($"MdlCache: {resref} compiled to {payload.Length} bytes whose wrapper does not add up -- not cached"); return; }
            Store(resref, w, payload);
        }
        catch (Exception e) { _errors++; Say($"MdlCache: caching {resref}: {e.Message}"); }
    }

    /// <summary>A model the engine has just parsed, before anything instantiates it: snapshot the two fields
    /// instantiation mutates, for whichever cold point compiles it (<see cref="MdlCachePristine"/>). Only for
    /// a model already queued, so a session that caches nothing pays one dictionary probe per read.</summary>
    [UnmanagedCallersOnly]
    private static IntPtr OnReadSync(IntPtr dispatcher, byte* name)
    {
        IntPtr tree = (*_readSlot)(dispatcher, name);
        if (!_armed || tree == IntPtr.Zero || name == null || *name == 0) return tree;
        // Lower-cased, because that is the spelling Queue files under (ResRef does the same fold): the engine
        // asks FindModel with whatever case the content wrote, and an Ordinal miss here would refuse the
        // model's compile for good -- it is only ever queued on a cache MISS, so it would never be retried.
        string resref = (Marshal.PtrToStringAnsi((IntPtr)name) ?? "").ToLowerInvariant();
        bool want;
        lock (Gate) want = _wanted.ContainsKey(resref);
        if (want) MdlCachePristine.Took(resref, tree);
        return tree;
    }

    /// <summary>The file the engine's writer just produced. <c>IODispatcher::Write</c> names it after
    /// <c>Model+0x10</c>, which <c>compilemodelsinternal</c> matched <c>strncasecmp</c>-wise, so it agrees with
    /// the resref except possibly in case -- hence the one directory walk when the exact name misses.</summary>
    private static string? Produced(string dir, string resref)
    {
        string exact = Path.Combine(dir, resref + Ext);
        if (File.Exists(exact)) return exact;
        foreach (string f in Directory.EnumerateFiles(dir, "*" + Ext))
            if (string.Equals(Path.GetFileNameWithoutExtension(f), resref, StringComparison.OrdinalIgnoreCase)) return f;
        return null;
    }

    /// <summary>Header + payload, written to a neighbouring temp name and renamed: a reader never sees a file
    /// half written, whatever happens to the process in between.</summary>
    private static void Store(string resref, Want w, byte[] payload)
    {
        Directory.CreateDirectory(Dir);
        byte[] file = new byte[HeaderBytes + payload.Length];
        WriteHeader(file, _buildKey, w.Hash, w.SrcLen, payload.Length, resref);
        payload.CopyTo(file, HeaderBytes);
        string final = Path.Combine(Dir, FileName(resref, w.Hash));
        string tmp = final + ".tmp";
        File.WriteAllBytes(tmp, file);
        File.Move(tmp, final, overwrite: true);
        _written++;
        if (_diskBytes >= 0) _diskBytes += file.Length;
        if (_written == 1 || _written % PruneEvery == 0) Prune();   // the first write also settles the byte tally
    }

    /// <summary>Oldest-first past <see cref="MaxDiskBytes"/>, the <c>BakeCache</c> shape. One directory listing
    /// per sweep, and a pruned entry is simply a later miss.</summary>
    private static void Prune()
    {
        try
        {
            var files = new DirectoryInfo(Dir).GetFiles("*" + Ext);
            long total = 0;
            for (int i = 0; i < files.Length; i++) total += files[i].Length;
            _diskBytes = total;
            if (total <= MaxDiskBytes) return;
            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            long[] sizes = new long[files.Length];
            for (int i = 0; i < files.Length; i++) sizes[i] = files[i].Length;
            int drop = PruneCount(sizes, MaxDiskBytes);
            for (int i = 0; i < drop; i++) { files[i].Delete(); total -= sizes[i]; _pruned++; }
            _diskBytes = total;
        }
        catch (Exception e) { _errors++; Say($"MdlCache: pruning {Dir}: {e.Message}"); }
    }

    /// <summary>How many of <paramref name="oldestFirst"/> go, so what is left fits <paramref name="cap"/>.
    /// Pure, and never more than the cap demands.</summary>
    internal static int PruneCount(ReadOnlySpan<long> oldestFirst, long cap)
    {
        long total = 0;
        for (int i = 0; i < oldestFirst.Length; i++) total += oldestFirst[i];
        int drop = 0;
        while (drop < oldestFirst.Length && total > cap) total -= oldestFirst[drop++];
        return drop;
    }

    /// <summary>Where <c>IODispatcher::Write</c> drops its output: the engine's own <c>MODELCOMPILER</c> alias,
    /// asked once. An empty alias means the writer returns false without writing anything, so the whole compile
    /// half stands down loudly rather than compiling into nowhere every frame.</summary>
    private static string? CompileDir()
    {
        if (_outDirAsked) return _outDir;
        const string Alias = "MODELCOMPILER";
        IntPtr basePtr = *(IntPtr*)_exoBaseSym;
        IntPtr aliases = basePtr == IntPtr.Zero ? IntPtr.Zero : new CExoBase(basePtr).AliasList.Ptr;
        if (aliases == IntPtr.Zero) return null;   // the engine is not up yet; ask again next drip
        _outDirAsked = true;
        byte* text = stackalloc byte[Alias.Length + 1];
        for (int i = 0; i < Alias.Length; i++) text[i] = (byte)Alias[i];
        text[Alias.Length] = 0;
        // A CExoString is {char* buf; int len; int cap}; GetAliasPath only reads it, so a forged shell serves.
        long* shell = stackalloc long[2];
        shell[0] = (long)text;
        shell[1] = Alias.Length;
        IntPtr got = new CExoAliasList(aliases).AliasGetAliasPath((IntPtr)shell, 0);
        byte* path = got == IntPtr.Zero ? null : *(byte**)got;
        if (path == null || *path == 0) { Stand($"the {Alias} alias is empty in nwn.ini, so the engine's writer has nowhere to write"); return null; }
        _outDir = Marshal.PtrToStringAnsi((IntPtr)path);
        return _outDir;
    }

    private static void Stand(string why)
    {
        _errors++;
        _log?.Always($"MdlCache: no model will be compiled -- {why}; cached models already on disk are still served");
    }

    // ---- small helpers ----

    private static bool Starts(byte* s, string lit)
    {
        if (s == null) return false;
        for (int i = 0; i < lit.Length; i++) if (s[i] != (byte)lit[i]) return false;
        return true;
    }

    private static bool Contains(byte* s, string lit)
    {
        if (s == null) return false;
        for (int i = 0; s[i] != 0 && i < 256; i++)
        {
            int j = 0;
            while (j < lit.Length && s[i + j] == (byte)lit[j]) j++;
            if (j == lit.Length) return true;
        }
        return false;
    }

    /// <summary>Loud but capped: a cache that cannot write says so, it does not fail a load.</summary>
    private static void Say(string line)
    {
        if (System.Threading.Interlocked.Increment(ref _said) <= LogCap) _log?.Always(line);
    }

    // ---- self-check ----

    /// <summary>Off-device: the key, the header's five refusals, the wrapper assertion and the prune's ordering
    /// rule -- everything about this that can be wrong without a running engine. Oracle for the key: golden
    /// digests pinned as literals. It names files on the player's disk, so a change to it is a change of
    /// identity that silently invalidates every cached model, and that has to be a deliberate edit here.
    /// </summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MdlCache.SelfCheck");
        byte[] a = new byte[1000], b = new byte[1000];
        for (int i = 0; i < a.Length; i++) a[i] = b[i] = (byte)(i * 7 + 1);
        b[999] ^= 1;
        ulong ha = Hash(a), hb = Hash(b);
        check.Expect(ha == 0x0DC6D5A465B1D8ADUL, $"the key for the golden 1000 bytes is {ha:x16}, not 0dc6d5a465b1d8ad");
        check.Expect(Hash(ReadOnlySpan<byte>.Empty) == 0x9353DFC8A195F3E2UL,
                     $"the key for no bytes at all is {Hash(ReadOnlySpan<byte>.Empty):x16}, not 9353dfc8a195f3e2");
        check.Expect(Hash("abc"u8) == 0x78F9BC69A6845B7BUL, $"the key for a 3-byte tail is {Hash("abc"u8):x16}, not 78f9bc69a6845b7b");
        check.Expect(ha != hb, "one flipped bit in the last byte did not change the hash");
        check.Expect(Hash(a.AsSpan(0, 999)) != ha, "a shorter prefix hashes the same as the whole");
        check.Expect(FileName("tcn01_a01_01", 0x0123456789abcdefUL) == "tcn01_a01_01.0123456789abcdef.mdl",
                     $"the cache file is named {FileName("tcn01_a01_01", 0x0123456789abcdefUL)}");

        // A round trip, then each field spoiled in turn.
        byte[] payload = new byte[WrapperBytes + 40];
        BitConverter.TryWriteBytes(payload.AsSpan(4), 30u);
        BitConverter.TryWriteBytes(payload.AsSpan(8), 10u);
        check.Expect(WrapperAddsUp(payload), "a wrapper whose two sizes fill the payload was refused");
        BitConverter.TryWriteBytes(payload.AsSpan(8), 11u);
        check.Expect(!WrapperAddsUp(payload), "a wrapper whose sizes overrun the payload was accepted");
        BitConverter.TryWriteBytes(payload.AsSpan(8), 10u);
        payload[0] = 1;
        check.Expect(!WrapperAddsUp(payload), "an ASCII first byte passed the wrapper check");
        payload[0] = 0;

        byte[] file = new byte[HeaderBytes + payload.Length];
        WriteHeader(file, 0xB01DUL, ha, a.Length, payload.Length, "tcn01_a01_01");
        payload.CopyTo(file, HeaderBytes);
        check.Expect(ReadHeader(file, 0xB01DUL, ha, a.Length, "tcn01_a01_01", out int n) == null && n == payload.Length,
                     "a freshly written header did not read back");
        check.Expect(ReadHeader(file, 0xB01EUL, ha, a.Length, "tcn01_a01_01", out _) == "build", "another client build was accepted");
        check.Expect(ReadHeader(file, 0xB01DUL, hb, a.Length, "tcn01_a01_01", out _) == "key", "another source hash was accepted");
        check.Expect(ReadHeader(file, 0xB01DUL, ha, a.Length - 1, "tcn01_a01_01", out _) == "key", "another source length was accepted");
        check.Expect(ReadHeader(file, 0xB01DUL, ha, a.Length, "tcn01_a01_02", out _) == "key", "another resref was accepted");
        check.Expect(ReadHeader(file, 0xB01DUL, ha, a.Length, "tcn01_a01_0", out _) == "key", "a resref that is a prefix of the entry's was accepted");
        check.Expect(ReadHeader(file.AsSpan(0, file.Length - 1), 0xB01DUL, ha, a.Length, "tcn01_a01_01", out _) == "truncation", "a truncated file was accepted");
        check.Expect(ReadHeader(file.AsSpan(0, 8), 0xB01DUL, ha, a.Length, "tcn01_a01_01", out _) == "truncation", "a header-less file was accepted");
        file[4]++;
        check.Expect(ReadHeader(file, 0xB01DUL, ha, a.Length, "tcn01_a01_01", out _) == "version", "another format version was accepted");
        file[4]--;
        file[0]++;
        check.Expect(ReadHeader(file, 0xB01DUL, ha, a.Length, "tcn01_a01_01", out _) == "version", "a file that is not ours was accepted");

        // The prune's rule: oldest first, and it stops as soon as what is left fits.
        long[] sizes = [10, 20, 30, 40];
        check.Expect(PruneCount(sizes, 50) == 3, $"pruning 10+20+30+40 to 50 bytes dropped {PruneCount(sizes, 50)} files, not 3");
        check.Expect(PruneCount(sizes, 100) == 0, "a directory already inside the cap was pruned");
        check.Expect(PruneCount(sizes, 90) == 1, "dropping the oldest file alone already fits 90 bytes");
        check.Expect(PruneCount(sizes, 0) == 4, "a zero cap left files behind");
        check.Expect(PruneCount(ReadOnlySpan<long>.Empty, 0) == 0, "an empty directory pruned something");

        if (check.Ok) log.Info("MdlCache.SelfCheck: passed");
        return check.Ok;
    }
}
