using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// The NWSync store (<c>&lt;userdir&gt;/nwsync</c>), read by Alloy on its own thread. A manifest resource is the
/// one container kind whose <c>RESID</c> carries no location -- the engine re-queries by
/// <c>(manifestSha1, resref, restype)</c> -- so there is nothing for a worker to seek to, and this class replays
/// that two-step query itself. Every miss is counted and the caller then reads the resource the ordinary way.
/// </summary>
internal static unsafe class NwSyncReader
{
    /// <summary>What the engine calls the directory, relative to the client's user directory. Built by
    /// <c>platform.cpp</c>'s <c>"%s/nwsyncmeta.sqlite3"</c> / <c>"%s/nwsyncdata_%u.sqlite3"</c>; the live install
    /// carries exactly those two names.</summary>
    private const string DirName = "nwsync";
    private const string MetaFile = "nwsyncmeta.sqlite3";
    private const string ShardGlob = "nwsyncdata_*.sqlite3";

    private const string MetaSql =
        "select resref_sha1 from manifest_resrefs where manifest_sha1 = ? and resref = ? and restype = ? limit 1";
    private const string DataSql = "select size, data from resrefs where sha1 = ?";

    /// <summary>A SHA-1 as the store spells it: 40 lowercase hex digits.</summary>
    private const int HexLen = 40;
    /// <summary>The engine's own resref ceiling (<c>CResRef</c> is 16 bytes).</summary>
    private const int ResRefMax = 16;
    /// <summary>More shard files than any install has had; a store past this is a bug, not a bigger library.</summary>
    private const int MaxShards = 64;
    /// <summary>The largest resource this reader will allocate for. The store's own column is an int, and the
    /// biggest NWSync payload in the wild is a few MiB; a header claiming more is corrupt.</summary>
    private const int MaxPlain = 256 << 20;

    // ---- the blob framing (CompressedBufferHandler::DecompressFromBuffer @0x54eb60) ----
    /// <summary>The <c>NSYC</c> FourCC the NWSync handler is built with, as a little-endian int.</summary>
    private const int NsycMagic = 0x4359534e;   // not-an-offset: the FourCC 'NSYC'
    /// <summary>The only header version the engine accepts (it refuses anything else outright).</summary>
    private const int NsycVersion = 3;
    /// <summary>The handler's provider slot: 0 none, 1 zlib, 2 zstd. Every live row is 2.</summary>
    private const int ProviderZstd = 2;
    /// <summary>magic + version + provider + plain size.</summary>
    private const int NsycHeader = 16;
    /// <summary>The zstd provider's own pair ahead of the frame: a format tag that must be 1, and a dictionary id.</summary>
    private const int ZstdSubHeader = 8;

    // ---- sqlite3 ----
    private const int SqliteOk = 0, SqliteRow = 100;
    /// <summary>Our own connection: read-only, used by one thread.</summary>
    private const int OpenRead = 0x1 | 0x8000;   // not-an-offset: SQLITE_OPEN_READONLY | SQLITE_OPEN_NOMUTEX
    /// <summary>The self-check alone opens this way, to build its own fixture store.</summary>
    private const int OpenWrite = 0x2 | 0x4 | 0x8000;   // not-an-offset: SQLITE_OPEN_READWRITE | _CREATE | _NOMUTEX

    private static ExtenderLog? _log;
    private static volatile bool _armed;
    /// <summary>The knob is on and the engine's sqlite3/zstd resolved: <see cref="TryRead"/> may answer.</summary>
    internal static bool Armed => _armed;

    /// <summary>Moves with each new session and each manifest made a search source: a cache of what a resref's file holds starts over then.</summary>
    internal static long ContentEdge => ((long)ServerPolicy.Session << 32) | (uint)_opens;

    /// <summary>One manifest made a search source, as the main thread published it. Immutable, so the worker
    /// reads the whole thing with one reference load and can tell "still the same store" by identity.</summary>
    private sealed record Source(string Dir, string Manifest);

    /// <summary>The live source, or null when no manifest is a search source. Written by the main thread only.</summary>
    private static volatile Source? _source;
    /// <summary>Every manifest the engine currently holds, as <c>AddManifest</c> announced them. More than one
    /// and <see cref="_source"/> stays null: which table wins a resref is the resource manager's priority walk,
    /// and this reader will not guess it.</summary>
    private static readonly List<string> _manifests = new();

    // ---- reader-owned, one per reading thread ----

    /// <summary>One thread's whole connection to the store: its own handles, prepared statements and zstd
    /// context. Nothing in it is ever touched by another thread, so no statement is ever stepped twice over, and
    /// SQLite is built <c>THREADSAFE=1</c> in both client binaries, which is what makes that sound.</summary>
    private sealed class Reader
    {
        /// <summary>The source these handles were built for, or null when there are none.</summary>
        public Source? Bound;
        /// <summary>The handles are open AND match <see cref="Bound"/>. A bind that failed leaves this false with
        /// the source still latched, so a broken store is diagnosed once per manifest edge, not once per read.</summary>
        public bool Ok;
        public IntPtr Meta, MetaStmt, Dctx;
        public readonly IntPtr[] ShardDb = new IntPtr[MaxShards];
        public readonly IntPtr[] ShardStmt = new IntPtr[MaxShards];
        public int Shards;
        /// <summary>The manifest hex as SQLite sees it. NATIVE, not a pinned managed array: it is bound
        /// SQLITE_STATIC, so the pointer must stay put from the bind until the statement is reset -- a GC that
        /// moved it in between would leave the statement holding a stale address.</summary>
        public byte* ManifestUtf8;
        /// <summary>The shard <see cref="Fetch"/> last answered from: whose statement still owns the blob.</summary>
        public int LastShard;
    }

    [ThreadStatic] private static Reader? _tls;

    // ---- counters (Note) ----
    private static int _calls, _hits, _closedMiss, _metaMiss, _dataMiss, _staleMiss, _busyMiss, _errors, _opens, _ambiguous;
    /// <summary>Ticks spent in each stage of a read, summed over every thread: what says whether the store's
    /// cost is the lookup, the blob fetch or the decompression.</summary>
    private static long _metaTicks, _fetchTicks, _zstdTicks;
    /// <summary>Shards the last bind opened, for <see cref="Note"/> alone.</summary>
    private static int _noteShards;

    /// <summary>One counter, atomically: several reading threads share them, and a refusal that is not counted
    /// is a silent fallback.</summary>
    private static void Bump(ref int counter) => Interlocked.Increment(ref counter);

    /// <summary>Arm the reader. Once, from the pass roll-call.</summary>
    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        _armed = true;
    }

    /// <summary>Hook the two manifest edges, so <see cref="Open"/>/<see cref="Close"/> track the engine's own
    /// notion of which NWSync manifest is a search source.</summary>
    internal static void Install(ExtenderLog log, HookService hooks)
    {
        if (!_armed) return;
        _origAdd = hooks.InstallResManAddManifest(&OnAddManifest);
        _origRemove = hooks.InstallResManRemoveManifest(&OnRemoveManifest);
        if (_origAdd == null || _origRemove == null)
        {
            _armed = false;
            log.Always("NwSyncReader: the manifest edges did not hook -- the reader stands down (nothing to open it)");
        }
    }

    private static ResManAddManifestFn _origAdd;
    private static ResManRemoveManifestFn _origRemove;

    [UnmanagedCallersOnly]
    private static int OnAddManifest(IntPtr resman, IntPtr sha1, uint priority)
    {
        int ok = _origAdd(resman, sha1, priority);
        // AddKeyTable @0x53b490 returns 1 only when BuildNewTable succeeded AND the table was inserted; a 0
        // means no kind-5 table exists, so there is nothing for this reader to answer for.
        if (ok != 0) Open(ReadHex(sha1));
        return ok;
    }

    [UnmanagedCallersOnly]
    private static int OnRemoveManifest(IntPtr resman, IntPtr sha1)
    {
        string? hex = ReadHex(sha1);
        int r = _origRemove(resman, sha1);
        if (hex != null) Close(hex);
        return r;
    }

    /// <summary>The hash's hex text, straight out of the engine's <c>Hash::SHA1</c>, or null when it is empty
    /// or malformed. Main thread: this pointer is the caller's stack.</summary>
    private static string? ReadHex(IntPtr sha1)
    {
        if (sha1 == IntPtr.Zero || EngineLayout.Sha1Hex == 0) return null;
        var h = new Sha1(sha1);
        if (h.Empty != 0) return null;                      // the flag is INVERTED: non-zero means no hash
        Span<byte> hex = h.Hex;
        int n = hex.IndexOf((byte)0);
        if (n != HexLen) return null;
        return System.Text.Encoding.ASCII.GetString(hex[..HexLen]);
    }

    /// <summary>A manifest became a search source. MAIN THREAD.</summary>
    /// <param name="manifestHex">40 lowercase hex digits, as the key table names it.</param>
    /// <param name="dirOverride">The store directory; null means the client's own <c>&lt;userdir&gt;/nwsync</c>.
    /// Only the self-check passes one.</param>
    internal static void Open(string? manifestHex, string? dirOverride = null)
    {
        if (!_armed || manifestHex is not { Length: HexLen }) return;
        if (!_manifests.Contains(manifestHex)) _manifests.Add(manifestHex);
        Publish(dirOverride);
    }

    /// <summary>That manifest stopped being a search source. MAIN THREAD.</summary>
    internal static void Close(string? manifestHex = null)
    {
        if (manifestHex == null) _manifests.Clear();
        else _manifests.Remove(manifestHex);
        Publish(null);
    }

    /// <summary>Republish what the worker should be reading: exactly one manifest, or nothing.</summary>
    private static void Publish(string? dirOverride)
    {
        string? dir = dirOverride ?? Store();
        if (_manifests.Count != 1 || dir == null)
        {
            if (_manifests.Count > 1 && _source != null) Fail("ambiguous", ref _ambiguous,
                $"{_manifests.Count} NWSync manifests are search sources at once -- which one owns a resref is the " +
                "resource manager's priority walk, which this reader does not replay, so it stands down");
            _source = null;
            return;
        }
        _source = new Source(dir, _manifests[0]);
        _opens++;
    }

    /// <summary>The store directory beside the client's own <c>nwn.ini</c>, or null before the engine has one.</summary>
    private static string? Store()
    {
        string? user = Native.UserDirectory();
        return user == null ? null : Path.Combine(user, DirName);
    }

    /// <summary>
    /// The resource's bytes, decompressed, or false. Any thread: the caller's own handles are built on its first
    /// call and rebound when the manifest changes under it. <paramref name="resref"/> is extensionless and
    /// lowercased before binding -- binary collation, and the store holds only lowercase. On true
    /// <paramref name="data"/> is <c>NativeMemory.Alloc</c>ed and the CALLER frees it; every miss is counted.
    /// </summary>
    internal static bool TryRead(ReadOnlySpan<byte> resref, ushort restype, out byte* data, out int size)
    {
        data = null;
        size = 0;
        if (!_armed) return false;
        Bump(ref _calls);

        Reader r = _tls ??= new Reader();
        Source? src = _source;
        if (src == null)
        {
            // Only the owning thread may close these, and it is here now.
            if (r.Bound != null) Unbind(r);
            Bump(ref _closedMiss);
            return false;
        }
        if (!ReferenceEquals(src, r.Bound)) r.Ok = Rebind(r, src);
        if (!r.Ok) { Bump(ref _closedMiss); return false; }

        if (resref.Length is 0 or > ResRefMax) { Bump(ref _metaMiss); return false; }
        byte* rr = stackalloc byte[ResRefMax];
        int rn = 0;
        for (int i = 0; i < resref.Length && resref[i] != 0; i++)
        {
            byte c = resref[i];
            rr[rn++] = (byte)(c is >= (byte)'A' and <= (byte)'Z' ? c + ('a' - 'A') : c);
        }
        if (rn == 0) { Bump(ref _metaMiss); return false; }

        byte* sha = stackalloc byte[HexLen + 1];
        if (!MetaLookup(r, rr, rn, restype, sha)) return false;
        if (!Fetch(r, sha, out void* blob, out int blobLen)) return false;
        bool ok = Unframe(r, (byte*)blob, blobLen, out data, out size);
        // The blob belongs to the statement, so it must be let go before anything else steps it.
        EngineFn.Sqlite3Reset(r.ShardStmt[r.LastShard]);
        if (!ok) return false;

        if (!ReferenceEquals(_source, src))
        {
            // The manifest changed while this call was in flight: these bytes may not be the ones the live
            // store would answer with, and a caller cannot tell. Throw them away.
            NativeMemory.Free(data);
            data = null;
            size = 0;
            Bump(ref _staleMiss);
            return false;
        }
        Bump(ref _hits);
        return true;
    }

    /// <summary>This thread will not read again: its handles go now. The two loader workers never exit, so only
    /// a thread that does (the self-check's) has to say so.</summary>
    internal static void CloseThread()
    {
        if (_tls != null) Unbind(_tls);
    }

    /// <summary>The period line's clause.</summary>
    internal static string Note()
    {
        if (!_armed) return "";
        string state = _source == null ? "closed" : "open";
        long ms = Stopwatch.Frequency / 1000;
        return $", nwsyncread {state} ({_noteShards} shard(s), {_opens} manifest edge(s)): {_calls} call(s), {_hits} hit, " +
               $"{_metaTicks / ms} ms lookup, {_fetchTicks / ms} ms fetch, {_zstdTicks / ms} ms unzip, " +
               $"{_metaMiss} unknown resref, {_dataMiss} no blob, {_closedMiss} closed, {_busyMiss} locked, " +
               $"{_staleMiss} stale, {_ambiguous} ambiguous, {_errors} error(s)";
    }

    // ---------------------------------------------------------------- reader internals

    /// <summary>Tear one thread's handles down and build them again for <paramref name="src"/>.</summary>
    private static bool Rebind(Reader r, Source src)
    {
        Unbind(r);
        r.Bound = src;   // latched ahead of every failure path, so a bad store is not re-diagnosed per read
        if (EngineFn.Sqlite3OpenV2 == null || EngineFn.ZstdDecompressDCtx == null)
        {
            Fail("nosym", ref _errors, "sqlite3/zstd did not resolve in this client -- the reader cannot open the store");
            _armed = false;
            return false;
        }
        if (r.ManifestUtf8 == null) r.ManifestUtf8 = (byte*)NativeMemory.Alloc(HexLen);
        System.Text.Encoding.ASCII.GetBytes(src.Manifest, new Span<byte>(r.ManifestUtf8, HexLen));

        string meta = Path.Combine(src.Dir, MetaFile);
        if (!File.Exists(meta))
        {
            Fail("nometa", ref _errors, $"no {MetaFile} under {src.Dir} -- a manifest is a search source with no store behind it");
            return false;
        }
        if (!OpenDb(meta, OpenRead, out r.Meta) || !Prepare(r.Meta, MetaSql, out r.MetaStmt)) return false;

        string[] files;
        try { files = Directory.GetFiles(src.Dir, ShardGlob); }
        catch (IOException e) { Fail("noshards", ref _errors, $"cannot list {src.Dir}: {e.Message}"); return false; }
        // A glob is not an anchor on every platform, and SQLite's own journal siblings sit right beside these
        // files; only a name that really ends in the extension is a shard.
        files = Array.FindAll(files, f => f.EndsWith(".sqlite3", StringComparison.Ordinal));
        Array.Sort(files, StringComparer.Ordinal);
        if (files.Length > MaxShards)
        {
            Fail("shardcap", ref _errors, $"{files.Length} shard files under {src.Dir}, past the {MaxShards} this reader carries");
            return false;
        }
        for (int i = 0; i < files.Length; i++)
        {
            if (!OpenDb(files[i], OpenRead, out IntPtr db)) return false;
            if (!Prepare(db, DataSql, out IntPtr st)) { EngineFn.Sqlite3CloseV2(db); return false; }
            r.ShardDb[r.Shards] = db;
            r.ShardStmt[r.Shards] = st;
            r.Shards++;
        }
        if (r.Shards == 0)
        {
            Fail("noshards", ref _errors, $"no {ShardGlob} under {src.Dir} -- the metadata has no data behind it");
            return false;
        }
        if (r.Dctx == IntPtr.Zero)
        {
            // One context per reading thread for the life of the process, exactly as the engine keeps one per
            // thread and never frees it: nwmain.exe exports no ZSTD_freeDCtx we could anchor.
            r.Dctx = EngineFn.ZstdCreateDCtx();
            if (r.Dctx == IntPtr.Zero) { Fail("nodctx", ref _errors, "ZSTD_createDCtx returned null"); return false; }
        }
        _noteShards = r.Shards;
        _log?.Info($"NwSyncReader: manifest {src.Manifest} open over {r.Shards} shard(s) in {src.Dir} for {Thread.CurrentThread.Name ?? "a reader"}");
        return true;
    }

    /// <summary>Drop one thread's handles. Safe to call twice, and only ever from the thread that owns them.</summary>
    private static void Unbind(Reader r)
    {
        for (int i = 0; i < r.Shards; i++)
        {
            if (r.ShardStmt[i] != IntPtr.Zero) EngineFn.Sqlite3Finalize(r.ShardStmt[i]);
            if (r.ShardDb[i] != IntPtr.Zero) EngineFn.Sqlite3CloseV2(r.ShardDb[i]);
            r.ShardStmt[i] = IntPtr.Zero;
            r.ShardDb[i] = IntPtr.Zero;
        }
        r.Shards = 0;
        r.Ok = false;
        if (r.MetaStmt != IntPtr.Zero) { EngineFn.Sqlite3Finalize(r.MetaStmt); r.MetaStmt = IntPtr.Zero; }
        if (r.Meta != IntPtr.Zero) { EngineFn.Sqlite3CloseV2(r.Meta); r.Meta = IntPtr.Zero; }
        r.Bound = null;
    }

    private static bool OpenDb(string path, int flags, out IntPtr db)
    {
        db = IntPtr.Zero;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(path + "\0");
        IntPtr h;
        fixed (byte* p = utf8) if (EngineFn.Sqlite3OpenV2(p, &h, flags, null) != SqliteOk)
        {
            string why = h == IntPtr.Zero ? "out of memory" : Msg(h);
            if (h != IntPtr.Zero) EngineFn.Sqlite3CloseV2(h);
            Fail("open", ref _errors, $"cannot open {path}: {why}");
            return false;
        }
        // Never wait: the engine's downloader and housekeeping write these files from its task threads, and a
        // worker blocked on their transaction is worse than a miss.
        EngineFn.Sqlite3BusyTimeout(h, 0);
        db = h;
        return true;
    }

    private static bool Prepare(IntPtr db, string sql, out IntPtr stmt)
    {
        stmt = IntPtr.Zero;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(sql + "\0");
        IntPtr st;
        fixed (byte* p = utf8) if (EngineFn.Sqlite3PrepareV2(db, p, -1, &st, null) != SqliteOk || st == IntPtr.Zero)
        {
            Fail("prepare", ref _errors, $"cannot prepare \"{sql}\": {Msg(db)}");
            return false;
        }
        stmt = st;
        return true;
    }

    private static string Msg(IntPtr db)
    {
        byte* p = EngineFn.Sqlite3ErrMsg == null ? null : EngineFn.Sqlite3ErrMsg(db);
        return p == null ? "?" : Marshal.PtrToStringUTF8((IntPtr)p) ?? "?";
    }

    /// <summary>manifest + resref + restype -&gt; the resource's own SHA-1 hex, written NUL-terminated into
    /// <paramref name="sha"/> (41 bytes).</summary>
    private static bool MetaLookup(Reader r, byte* resref, int rn, ushort restype, byte* sha)
    {
        long t0 = Stopwatch.GetTimestamp();
        IntPtr st = r.MetaStmt;
        EngineFn.Sqlite3Reset(st);
        // SQLITE_STATIC (a null destructor): both buffers stay put until the reset below.
        EngineFn.Sqlite3BindText(st, 1, r.ManifestUtf8, HexLen, IntPtr.Zero);
        EngineFn.Sqlite3BindText(st, 2, resref, rn, IntPtr.Zero);
        EngineFn.Sqlite3BindInt(st, 3, restype);
        int rc = EngineFn.Sqlite3Step(st);
        bool got = false;
        if (rc != SqliteRow)
        {
            if (Busy(rc)) Bump(ref _busyMiss); else Bump(ref _metaMiss);
        }
        else
        {
            byte* text = EngineFn.Sqlite3ColumnText(st, 0);
            int n = EngineFn.Sqlite3ColumnBytes(st, 0);
            if (text == null || n != HexLen)
            {
                Fail("metarow", ref _errors, $"manifest_resrefs gave a {n}-byte resref_sha1 (expected {HexLen})");
            }
            else
            {
                Buffer.MemoryCopy(text, sha, HexLen, HexLen);
                sha[HexLen] = 0;
                got = true;
            }
        }
        EngineFn.Sqlite3Reset(st);
        Interlocked.Add(ref _metaTicks, Stopwatch.GetTimestamp() - t0);
        return got;
    }

    /// <summary>The stored blob for a SHA-1: every shard is queried in name order and the first row wins (the
    /// engine's own shard-to-hash set lives in its heap, so there is nothing to ask). The pointer is the
    /// statement's and is live only until that statement is reset -- the caller does that.</summary>
    private static bool Fetch(Reader r, byte* sha, out void* blob, out int len)
    {
        long t0 = Stopwatch.GetTimestamp();
        blob = null;
        len = 0;
        bool busy = false;
        for (int i = 0; i < r.Shards; i++)
        {
            IntPtr st = r.ShardStmt[i];
            EngineFn.Sqlite3Reset(st);
            EngineFn.Sqlite3BindText(st, 1, sha, HexLen, IntPtr.Zero);
            int rc = EngineFn.Sqlite3Step(st);
            if (rc != SqliteRow)
            {
                busy |= Busy(rc);
                EngineFn.Sqlite3Reset(st);
                continue;
            }
            void* p = EngineFn.Sqlite3ColumnBlob(st, 1);
            int n = EngineFn.Sqlite3ColumnBytes(st, 1);
            if (p == null || n <= 0)
            {
                EngineFn.Sqlite3Reset(st);
                continue;
            }
            r.LastShard = i;
            blob = p;
            len = n;
            Interlocked.Add(ref _fetchTicks, Stopwatch.GetTimestamp() - t0);
            return true;
        }
        Interlocked.Add(ref _fetchTicks, Stopwatch.GetTimestamp() - t0);
        if (busy) Bump(ref _busyMiss); else Bump(ref _dataMiss);
        return false;
    }

    /// <summary>SQLITE_BUSY (5) or SQLITE_LOCKED (6): the engine's own writer has the file.</summary>
    private static bool Busy(int rc) => rc is 5 or 6;

    /// <summary>NSYC header + the zstd provider's pair + a raw frame -&gt; the plain bytes.</summary>
    private static bool Unframe(Reader r, byte* blob, int len, out byte* data, out int size)
    {
        data = null;
        size = 0;
        if (len < NsycHeader + ZstdSubHeader)
        {
            Fail("short", ref _errors, $"a {len}-byte NWSync blob cannot carry its own header");
            return false;
        }
        int* hdr = (int*)blob;
        int magic = hdr[0], version = hdr[1], provider = hdr[2], plain = hdr[3];
        if (magic != NsycMagic || version != NsycVersion)
        {
            Fail("header", ref _errors, $"NWSync blob header is magic 0x{magic:x8} version {version} (expected NSYC version {NsycVersion})");
            return false;
        }
        if (provider != ProviderZstd)
        {
            Fail("provider", ref _errors, $"NWSync blob uses compression provider {provider}; this reader implements {ProviderZstd} (zstd) only");
            return false;
        }
        if (plain <= 0 || plain > MaxPlain)
        {
            Fail("plainsize", ref _errors, $"NWSync blob declares a {plain}-byte resource");
            return false;
        }
        byte* sub = &blob[NsycHeader];
        int* subHdr = (int*)sub;
        if (subHdr[0] != 1 || subHdr[1] != 0)
        {
            Fail("zstdsub", ref _errors,
                 $"zstd payload is tag {subHdr[0]} dictionary {subHdr[1]}; this reader carries no dictionary");
            return false;
        }
        byte* frame = &sub[ZstdSubHeader];
        nuint frameLen = (nuint)(len - NsycHeader - ZstdSubHeader);
        byte* dst = (byte*)NativeMemory.Alloc((nuint)plain);
        long t0 = Stopwatch.GetTimestamp();
        nuint got = EngineFn.ZstdDecompressDCtx(r.Dctx, dst, (nuint)plain, frame, frameLen);
        Interlocked.Add(ref _zstdTicks, Stopwatch.GetTimestamp() - t0);
        if (got != (nuint)plain)
        {
            // An error comes back as a size_t far past any real length, so this one test covers both "failed"
            // and "disagreed with its own header".
            NativeMemory.Free(dst);
            Fail("zstd", ref _errors, $"zstd produced {got} bytes for a blob declaring {plain}");
            return false;
        }
        data = dst;
        size = plain;
        return true;
    }

    private static readonly HashSet<string> _said = new();

    /// <summary>The first of each reason in full, the rest on the period line.</summary>
    private static void Fail(string reason, ref int counter, string why)
    {
        Interlocked.Increment(ref counter);
        // Both the main thread's manifest edges and the worker's reads reach here, so the "said it once" set
        // is the one piece of shared mutable state in the class.
        bool first;
        lock (_said) first = _said.Add(reason);
        if (first) _log?.Always($"NwSyncReader [{reason}]: {why}");
    }

    // ---------------------------------------------------------------- self-check

    /// <summary>A 96-byte payload compressed to a 47-byte zstd frame, generated once and carried here so the
    /// check needs no compressor -- the engine exports a decompressor, not an encoder, on both platforms.</summary>
    private const string FixtureFrame =
        "28b52ffd0068350100f86378206e7773796e632073656c662d636865636b207061796c6f61643b206301003b48ca09";
    private const string FixturePlain =
        "cx nwsync self-check payload; cx nwsync self-check payload; cx nwsync self-check payload; cx nws";
    private const string FixtureResRef = "cx_check";
    private const ushort FixtureResType = 2022;

    /// <summary>
    /// Builds a throw-away store -- one meta DB and one shard, through the engine's own sqlite3 -- and reads a
    /// known resource back out of it: the two queries, the NSYC framing, the zstd frame, the three refusals
    /// (unknown resref, unknown restype, closed reader) and two threads reading at once. Skips LOUDLY where the
    /// host exports no sqlite3, because a check that quietly passes on an empty host is worse than no check.
    /// </summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        _log ??= log;
        var check = new Checker(log, "NwSyncReader.SelfCheck");
        if (EngineFn.Sqlite3OpenV2 == null || EngineFn.Sqlite3Exec == null || EngineFn.ZstdDecompressDCtx == null)
            return check.Skip("this host exports no sqlite3/zstd, so the store cannot be built or read");
        if (EngineFn.Sqlite3ThreadSafe != null && EngineFn.Sqlite3ThreadSafe() == 0)
        {
            log.Always("NwSyncReader.SelfCheck: FAILED -- sqlite3_threadsafe() is 0, so a private connection per thread is not sound");
            return false;
        }

        string dir = Path.Combine(Path.GetTempPath(), "cx-nwsync-selfcheck-" + Environment.ProcessId.ToString());
        string? skip = null;
        bool armed = _armed, ok;
        Source? live = _source;
        _armed = true;
        try
        {
            Directory.CreateDirectory(dir);
            string manifest = new string('a', HexLen);
            string sha = new string('b', HexLen);
            byte[] frame = Convert.FromHexString(FixtureFrame);
            byte[] plain = System.Text.Encoding.ASCII.GetBytes(FixturePlain);
            byte[] blob = new byte[NsycHeader + ZstdSubHeader + frame.Length];
            BitConverter.TryWriteBytes(blob.AsSpan(0), NsycMagic);
            BitConverter.TryWriteBytes(blob.AsSpan(4), NsycVersion);
            BitConverter.TryWriteBytes(blob.AsSpan(8), ProviderZstd);
            BitConverter.TryWriteBytes(blob.AsSpan(12), plain.Length);
            BitConverter.TryWriteBytes(blob.AsSpan(NsycHeader), 1);            // the provider's format tag
            frame.CopyTo(blob.AsSpan(NsycHeader + ZstdSubHeader));

            ok = Exec(Path.Combine(dir, MetaFile),
                      "create table if not exists manifest_resrefs (manifest_sha1 text not null, resref_sha1 text not null, " +
                      "resref text not null, restype int not null); delete from manifest_resrefs; " +
                      $"insert into manifest_resrefs values ('{manifest}', '{sha}', '{FixtureResRef}', {FixtureResType});");
            ok &= Exec(Path.Combine(dir, "nwsyncdata_0.sqlite3"),
                       "create table if not exists resrefs (sha1 text not null primary key, data blob not null, size int not null); " +
                       "delete from resrefs; " +
                       $"insert into resrefs values ('{sha}', x'{Convert.ToHexString(blob)}', {blob.Length});");

            _source = null;
            _manifests.Clear();
            CloseThread();
            lock (_said) _said.Clear();
            ok &= !TryRead("cx_check"u8, FixtureResType, out _, out _);        // closed: refused, not read

            Open(manifest, dir);
            ok &= _source != null;
            ok &= TryRead("cx_check"u8, FixtureResType, out byte* got, out int n) && n == plain.Length;
            if (ok)
            {
                ok &= new ReadOnlySpan<byte>(got, n).SequenceEqual(plain);
                NativeMemory.Free(got);
            }
            // The store is binary-collated and the engine writes lowercase, so an upper-case resref must still hit.
            ok &= TryRead("CX_CHECK"u8, FixtureResType, out byte* up, out int un) && un == plain.Length;
            if (up != null) NativeMemory.Free(up);
            ok &= !TryRead("cx_nope"u8, FixtureResType, out _, out _);         // unknown resref
            ok &= !TryRead("cx_check"u8, 1, out _, out _);                     // right resref, wrong restype
            ok &= _tls is { Shards: 1 } && _errors == 0;
            ok &= Concurrent(log, plain);

            // Two manifests at once: the reader stands down rather than guess the priority walk.
            lock (_said) _said.Add("ambiguous");   // the fixture's stand-down is expected: no warning in a user's log
            Open(new string('c', HexLen), dir);
            ok &= _source == null && !TryRead("cx_check"u8, FixtureResType, out _, out _);
            Close();
            ok &= _source == null;
        }
        catch (IOException e)
        {
            skip = $"cannot build a fixture store under {dir}: {e.Message}";
            ok = true;
        }
        finally
        {
            CloseThread();
            _manifests.Clear();
            _source = live;
            _armed = armed;
            _calls = _hits = _closedMiss = _metaMiss = _dataMiss = _staleMiss = _busyMiss = _errors = _opens = _ambiguous = 0;
            _metaTicks = _fetchTicks = _zstdTicks = 0;
            _noteShards = 0;
            lock (_said) _said.Clear();
            try { Directory.Delete(dir, true); } catch (IOException) { /* a leftover fixture is harmless */ }
        }
        if (skip != null) return check.Skip(skip);
        log.Info(ok ? "NwSyncReader.SelfCheck: passed" : "NwSyncReader.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>Reads a thread beyond the main one runs in the concurrency check. Enough that the two overlap
    /// for real: one read apiece would interleave by luck, not by construction.</summary>
    private const int CheckReaders = 2, CheckReads = 64;

    /// <summary>Two threads read the same fixture store at once, each proving every byte of the payload. Their
    /// handles are their own, so each closes them before it exits.</summary>
    private static bool Concurrent(ExtenderLog log, byte[] plain)
    {
        int bad = 0;
        var threads = new Thread[CheckReaders];
        for (int t = 0; t < CheckReaders; t++)
        {
            threads[t] = new Thread(() =>
            {
                for (int i = 0; i < CheckReads; i++)
                {
                    if (!TryRead("cx_check"u8, FixtureResType, out byte* got, out int n)
                        || n != plain.Length || !new ReadOnlySpan<byte>(got, n).SequenceEqual(plain))
                        Interlocked.Increment(ref bad);
                    if (got != null) NativeMemory.Free(got);
                }
                CloseThread();
            }) { IsBackground = true, Name = $"cx-nwsync-check-{t}" };
            threads[t].Start();
        }
        foreach (Thread th in threads) th.Join();
        if (bad != 0) log.Always($"NwSyncReader.SelfCheck: FAILED -- {bad} of {CheckReaders * CheckReads} concurrent reads did not come back whole");
        return bad == 0;
    }

    /// <summary>Run one script against a read-write connection, for the self-check's fixture store only.</summary>
    private static bool Exec(string path, string sql)
    {
        if (!OpenDb(path, OpenWrite, out IntPtr db)) return false;
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(sql + "\0");
        int rc;
        fixed (byte* p = utf8) rc = EngineFn.Sqlite3Exec(db, p, IntPtr.Zero, IntPtr.Zero, null);
        if (rc != SqliteOk) Fail("fixture", ref _errors, $"cannot build {path}: {Msg(db)}");
        EngineFn.Sqlite3CloseV2(db);
        return rc == SqliteOk;
    }
}
