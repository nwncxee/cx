using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The bake cache: one blob per page bake under <c>cx-charts/</c> -- the bake's union readback
/// deflated behind a table of the rects it holds (A and B as their compressed blocks, C raw) -- with an
/// in-memory index over every blob's table, so adoption uploads straight out of the union, no allocation. Per-rect files were the
/// load: thousands of opens behind an 18k-file sweep took ten seconds on a cold box.</summary>
internal sealed class BakeCache
{
    /// <summary>"CXLP" little-endian.</summary>
    private const uint Magic = 0x504c5843;   // not-an-offset: the file's first four bytes
    /// <summary>Bumped with every change to what a baked texel holds or how the file is laid out: a file from
    /// an older bake is stale, whatever its key. 9 = the header carries the union's page and origin, so an
    /// area cache uploads a union whole instead of per rect; 10 = a third plane, C, the mover shares;
    /// 11 = C by each rect's pick, not an area channel; 12 = A and B as BC6H/BC7 blocks, unions whole blocks.</summary>
    internal const int Version = 12;
    /// <summary>Blobs written between prunes (a blob is a page: 32 is a budget's worth).</summary>
    private const int PruneEvery = 32;
    internal const string Ext = ".lmp";
    /// <summary>The area plan beside the page blobs, named by the area key.</summary>
    internal const string AreaExt = ".lma";

    /// <summary>One rect inside a blob's union: its key and place (relative to the union's origin).</summary>
    internal struct Entry { internal ulong Key; internal int X, Y, W, H; }
    /// <summary>Where a blob's union sits on its page: the layer and the origin the readback was taken at.</summary>
    internal struct Union { internal string File; internal int Page, X0, Y0, W, H; }
    /// <summary>A probe (Rect &gt;= 0), its answer (Data = the union or null, X/Y/Uw/Uh place the rect in it),
    /// a write (Data = the union readback, Table = its rects), or an area-plan read/write (Area).</summary>
    private struct Job
    {
        internal int Rect; internal ulong Key; internal int W, H;
        internal byte[]? Data; internal int X, Y, Uw, Uh; internal Entry[]? Table; internal bool Write; internal int Buf;
        internal string? File; internal int Page, X0, Y0;
        internal bool Area; internal LightmapAreaPlan? Plan;
    }
    private readonly Queue<Job> _in = new();
    private readonly Queue<Job> _out = new();
    /// <summary>Area-plan answers, their own queue so a plan never queues behind a page of probes.</summary>
    private readonly Queue<(ulong Key, LightmapAreaPlan? Plan)> _outArea = new();
    private readonly object _gate = new();
    private Thread? _thread;
    private ExtenderLog? _log;
    private int _writes;
    /// <summary>Key -&gt; where it sits in which blob. Cache thread only.</summary>
    private readonly Dictionary<ulong, (int file, Entry e)> _index = new();
    private readonly List<string> _files = new();
    /// <summary>Probes of one drain, grouped by blob so each is read and inflated once.</summary>
    private readonly Dictionary<int, List<Job>> _byFile = new();
    /// <summary>Refused blobs (version, truncation, gone) this session; the period line prints it.</summary>
    internal int Stale;
    /// <summary>Writes queued and not yet on disk: while it is above zero the render thread's readback
    /// buffer is still being read from and must not be re-used.</summary>
    internal int Outstanding;
    /// <summary>Bytes the cache thread allocated this period (unions, deflate streams).</summary>
    internal long Allocated;
    /// <summary>Page blobs read and inflated this session: one per blob a probe drain touched.</summary>
    internal int Inflates;
    /// <summary>Per readback buffer: writes still reading from it. The render thread re-uses a buffer at 0.</summary>
    internal readonly int[] Busy = new int[4];

    internal void Attach(ExtenderLog log)
    {
        _log = log;
        if (_thread != null) return;
        _thread = new Thread(Work) { IsBackground = true, Name = "cx-lmcache" };
        _thread.Start();
    }

    /// <summary>Ask for a key's rect; the answer (a union and the rect's place in it, or null for a miss) comes
    /// back through <see cref="TryTake"/>.</summary>
    internal void Probe(int rect, ulong key, int w, int h)
    {
        lock (_gate) { _in.Enqueue(new Job { Rect = rect, Key = key, W = w, H = h }); Monitor.Pulse(_gate); }
    }

    /// <summary>Queue a page bake's union readback (A blocks, B blocks, C texels over <paramref name="uw"/> x
    /// <paramref name="uh"/>) for the disk as one blob, with the rects it holds and where the union sits
    /// on its page. Returns the blob's file name, minted here so the area plan can name it before the
    /// write lands.</summary>
    internal string Save(byte[] union, int buf, int page, int x0, int y0, int uw, int uh, Entry[] table)
    {
        string file = $"{DateTime.UtcNow.Ticks:x16}{Ext}";
        Interlocked.Increment(ref Outstanding); Interlocked.Increment(ref Busy[buf]);
        lock (_gate) { _in.Enqueue(new Job { Rect = -1, Data = union, Buf = buf, Page = page, X0 = x0, Y0 = y0, Uw = uw, Uh = uh, Table = table, Write = true, File = file }); Monitor.Pulse(_gate); }
        return file;
    }

    /// <summary>Read the area plan for <paramref name="key"/> and inflate every union its page plan
    /// names, so the area edge can install them whole. The answer -- a plan or a counted refusal -- comes back
    /// through <see cref="TryTakeArea"/>.</summary>
    internal void PrefetchArea(ulong key)
    {
        lock (_gate) { _in.Enqueue(new Job { Rect = -1, Area = true, Key = key }); Monitor.Pulse(_gate); }
    }

    /// <summary>Write an area plan, and touch the page blobs it names so a plan that is read every
    /// session does not outlive them under the prune.</summary>
    internal void SaveArea(LightmapAreaPlan plan)
    {
        lock (_gate) { _in.Enqueue(new Job { Rect = -1, Area = true, Write = true, Plan = plan }); Monitor.Pulse(_gate); }
    }

    /// <summary>An area-plan answer. Plan null = a miss or a refusal, already logged and counted.</summary>
    internal bool TryTakeArea(out ulong key, out LightmapAreaPlan? plan)
    {
        lock (_gate)
        {
            if (_outArea.Count == 0) { key = 0; plan = null; return false; }
            (key, plan) = _outArea.Dequeue();
            return true;
        }
    }

    internal bool TryTake(out int rect, out ulong key, out byte[]? data, out int x, out int y, out int uw, out int uh)
    {
        lock (_gate)
        {
            if (_out.Count == 0) { rect = -1; key = 0; data = null; x = y = uw = uh = 0; return false; }
            Job j = _out.Dequeue();
            rect = j.Rect; key = j.Key; data = j.Data; x = j.X; y = j.Y; uw = j.Uw; uh = j.Uh;
            return true;
        }
    }

    /// <summary>The world edge: answers in flight name rects that no longer exist. Writes and area
    /// jobs are kept -- the area prefetch is posted at <c>LoadArea</c>'s return, which is BEFORE the edge that
    /// drains, and its answer is guarded by the area key on the way in.</summary>
    internal void Drain()
    {
        lock (_gate)
        {
            var keep = new Queue<Job>();
            while (_in.Count > 0) { Job j = _in.Dequeue(); if (j.Write || j.Area) keep.Enqueue(j); }
            while (keep.Count > 0) _in.Enqueue(keep.Dequeue());
            _out.Clear();
        }
    }

    /// <summary>Once, when the thread starts: every blob's table into the index (oldest first, so a later bake
    /// of a key wins), then the dead weight out -- blobs of another format version and the per-rect files of
    /// the old scheme (<c>.lmr</c>), which the prune counted against the budget and never deleted on its own.
    /// The index comes first: on a cold box the delete of 18k files is seconds, and the probes queue behind it.</summary>
    private void BuildIndex()
    {
        try
        {
            string dir = MeshChart.LedgerDir();
            if (!Directory.Exists(dir)) return;
            var blobs = new DirectoryInfo(dir).GetFiles("*" + Ext);
            Array.Sort(blobs, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            int stale = 0; long staleBytes = 0;
            foreach (var f in blobs)
            {
                Entry[]? table = ReadTable(f.FullName);
                if (table == null) { staleBytes += f.Length; f.Delete(); stale++; continue; }
                Index(f.FullName, table);
            }
            int old = 0; long oldBytes = 0;
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.lmr")) { oldBytes += f.Length; f.Delete(); old++; }
            _log?.Info($"LightmapBake: bake cache indexed -- {_index.Count} rects in {_files.Count} page blobs"
                       + (stale > 0 ? $", {stale} blobs of older formats swept ({staleBytes >> 20} MiB)" : "")
                       + (old > 0 ? $", {old} per-rect files of the old scheme swept ({oldBytes >> 20} MiB)" : ""));
        }
        catch (Exception e) { _log?.Info($"LightmapBake: the bake cache index faulted -- {e.Message}"); }
    }

    private void Index(string file, Entry[] table)
    {
        int id = _files.Count; _files.Add(file);
        for (int i = 0; i < table.Length; i++) _index[table[i].Key] = (id, table[i]);
    }

    /// <summary>The blob's table alone (the header, no inflate); null = not a blob of this version.</summary>
    private static Entry[]? ReadTable(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            var r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) return null;
            r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32(); r.ReadInt32();   // page, x0, y0, uw, uh
            int n = r.ReadInt32();
            if (n <= 0 || n > 1 << 20) return null;
            var table = new Entry[n];
            for (int i = 0; i < n; i++) table[i] = new Entry { Key = r.ReadUInt64(), X = r.ReadInt32(), Y = r.ReadInt32(), W = r.ReadInt32(), H = r.ReadInt32() };
            return table;
        }
        catch (Exception) { return null; }
    }

    private void Work()
    {
        BuildIndex();
        var batch = new List<Job>();
        while (true)
        {
            batch.Clear();
            lock (_gate)
            {
                while (_in.Count == 0) Monitor.Wait(_gate);
                while (_in.Count > 0) batch.Add(_in.Dequeue());
            }
            long a0 = GC.GetAllocatedBytesForCurrentThread();
            try { Serve(batch); }
            catch (Exception e) { _log?.Info($"LightmapBake: the bake cache faulted -- {e.Message}"); }
            finally { Interlocked.Add(ref Allocated, GC.GetAllocatedBytesForCurrentThread() - a0); }
        }
    }

    /// <summary>One drain of the queue: the writes, then the probes grouped by blob -- each blob read and
    /// inflated once, every rect of it answered off that one union, the union then let go.</summary>
    private void Serve(List<Job> batch)
    {
        _byFile.Clear();
        for (int k = 0; k < batch.Count; k++)
        {
            Job j = batch[k];
            if (j.Area) { if (j.Write) WriteArea(j.Plan!); else ReadArea(j.Key); continue; }
            if (j.Write) { try { WriteBlob(j); } catch (Exception e) { _log?.Info($"LightmapBake: a blob write faulted -- {e.Message}"); } finally { Interlocked.Decrement(ref Outstanding); Interlocked.Decrement(ref Busy[j.Buf]); } continue; }
            if (_index.TryGetValue(j.Key, out var hit) && hit.e.W == j.W && hit.e.H == j.H)
            {
                // the place is taken NOW: a write later in this batch can re-point the key at another blob
                j.X = hit.e.X; j.Y = hit.e.Y;
                if (!_byFile.TryGetValue(hit.file, out var list)) _byFile[hit.file] = list = new List<Job>();
                list.Add(j);
            }
            else Answer(j);
        }
        foreach (var (file, jobs) in _byFile)
        {
            byte[]? union = LoadUnion(_files[file], out Union u, out _);
            if (union == null) Forget(file); else Interlocked.Increment(ref Inflates);
            for (int k = 0; k < jobs.Count; k++)
            {
                Job j = jobs[k];
                if (union != null) { j.Data = union; j.Uw = u.W; j.Uh = u.H; }
                Answer(j);
            }
        }
        _byFile.Clear();
    }

    private void Answer(Job j) { lock (_gate) _out.Enqueue(j); }

    /// <summary>A blob that would not load (pruned, cut, or another version): its keys miss from now on.</summary>
    private void Forget(int file)
    {
        Interlocked.Increment(ref Stale);
        var dead = new List<ulong>();
        foreach (var kv in _index) if (kv.Value.file == file) dead.Add(kv.Key);
        for (int i = 0; i < dead.Count; i++) _index.Remove(dead[i]);
        try { if (System.IO.File.Exists(_files[file])) System.IO.File.Delete(_files[file]); } catch (Exception) { }
    }

    /// <summary>Header + table + deflated union; pure. The header names the page and the origin the readback
    /// was taken at, which is what lets an area plan upload a union whole.</summary>
    internal static void SerializeTo(Stream s, int page, int x0, int y0, int uw, int uh, Entry[] table, byte[] union)
    {
        var bw = new BinaryWriter(s);
        bw.Write(Magic); bw.Write(Version); bw.Write(page); bw.Write(x0); bw.Write(y0); bw.Write(uw); bw.Write(uh); bw.Write(table.Length);
        for (int i = 0; i < table.Length; i++) { bw.Write(table[i].Key); bw.Write(table[i].X); bw.Write(table[i].Y); bw.Write(table[i].W); bw.Write(table[i].H); }
        bw.Flush();
        using var z = new DeflateStream(s, CompressionLevel.Fastest, leaveOpen: true);
        z.Write(union, 0, uw * uh * LightmapBake.TexelBytes);
    }

    /// <summary>The inverse of the union half; the table is <see cref="ReadTable"/>'s. Null names the refusal
    /// (version, truncation) -- never throws.</summary>
    internal static byte[]? Deserialize(Stream s, out Union u, out Entry[]? table, out string? why)
    {
        why = null; u = default; table = null;
        try
        {
            var r = new BinaryReader(s);
            if (r.ReadUInt32() != Magic || r.ReadInt32() != Version) { why = "version"; return null; }
            u.Page = r.ReadInt32(); u.X0 = r.ReadInt32(); u.Y0 = r.ReadInt32(); u.W = r.ReadInt32(); u.H = r.ReadInt32();
            int n = r.ReadInt32();
            if (n <= 0 || n > 1 << 20 || u.W <= 0 || u.H <= 0 || u.W > LightmapBake.PageSize || u.H > LightmapBake.PageSize
                || u.W % LightmapBake.Block != 0 || u.H % LightmapBake.Block != 0 || u.X0 % LightmapBake.Block != 0 || u.Y0 % LightmapBake.Block != 0
                || (uint)u.Page >= LightmapBake.MaxPages || (uint)u.X0 > LightmapBake.PageSize || (uint)u.Y0 > LightmapBake.PageSize) { why = "truncation"; return null; }
            table = new Entry[n];
            for (int i = 0; i < n; i++) table[i] = new Entry { Key = r.ReadUInt64(), X = r.ReadInt32(), Y = r.ReadInt32(), W = r.ReadInt32(), H = r.ReadInt32() };
            int bytes = u.W * u.H * LightmapBake.TexelBytes;
            var union = new byte[bytes];
            using var z = new DeflateStream(s, CompressionMode.Decompress, leaveOpen: true);
            int got = 0;
            while (got < bytes) { int k = z.Read(union, got, bytes - got); if (k <= 0) break; got += k; }
            if (got != bytes) { why = "truncation"; return null; }
            return union;
        }
        catch (Exception) { why = "truncation"; return null; }
    }

    /// <summary>Null on any refusal or I/O fault: a throw here would strand every probe of the batch in Probing.</summary>
    private byte[]? LoadUnion(string file, out Union u, out Entry[]? table)
    {
        u = default; table = null;
        try
        {
            if (!System.IO.File.Exists(file)) return null;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
            byte[]? union = Deserialize(fs, out u, out table, out string? why);
            if (union == null) _log?.Info($"LightmapBake: blob {Path.GetFileName(file)} refused ({why}) -- its page re-bakes");
            return union;
        }
        catch (Exception e) { _log?.Info($"LightmapBake: blob {Path.GetFileName(file)} unreadable ({e.Message}) -- its page re-bakes"); return null; }
    }

    /// <summary>One blob per page bake, named by the wall clock (the index orders by mtime anyway); its rects
    /// join the index at once so a repack inside the session hits them.</summary>
    private void WriteBlob(Job j)
    {
        string dir = MeshChart.LedgerDir();
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, j.File!);
        using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
            SerializeTo(fs, j.Page, j.X0, j.Y0, j.Uw, j.Uh, j.Table!, j.Data!);
        Index(file, j.Table!);
        if (++_writes % PruneEvery == 0) MeshChart.Prune(dir, (long)Lightmap.Disk.V << 20);
    }

    /// <summary>Cache thread: the area plan and every union its page plan names, inflated and held for
    /// the area edge. A plan whose version, key or settings moved, or one of whose page blobs is gone or has
    /// moved on its page, is refused by name and deleted -- the area then bakes from its rects. Never throws:
    /// a stranded prefetch would leave the edge waiting for an answer that never comes.</summary>
    private void ReadArea(ulong key)
    {
        string dir = MeshChart.LedgerDir();
        string file = Path.Combine(dir, $"{key:x16}{AreaExt}");
        LightmapAreaPlan? plan = null;
        string? why = null;
        try
        {
            if (!File.Exists(file)) why = "first visit";
            else
            {
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan))
                    plan = LightmapAreaPlan.Deserialize(fs, out why);
                if (plan != null && plan.Key != key) { plan = null; why = "key"; }
                if (plan != null) plan = Inflate(dir, plan, ref why);
            }
        }
        catch (Exception e) { plan = null; why = e.Message; }
        if (plan == null && why != "first visit")
        {
            try { if (File.Exists(file)) File.Delete(file); } catch (Exception) { }
            Interlocked.Increment(ref Stale);
            _log?.Info($"LightmapBake: the area bake cache for {key:x16} is refused ({why}) -- the area packs and probes its rects as on a first visit");
        }
        else if (plan == null) _log?.Info($"LightmapBake: no area bake cache for {key:x16} -- first visit");
        lock (_gate) _outArea.Enqueue((key, plan));
    }

    /// <summary>Every union of a plan, read and inflated; null (with <paramref name="why"/> set) the moment one
    /// blob is gone, refused, or no longer describes the page place the plan recorded.</summary>
    private LightmapAreaPlan? Inflate(string dir, LightmapAreaPlan plan, ref string? why)
    {
        long total = 0;
        for (int i = 0; i < plan.Unions.Length; i++) total += (long)plan.Unions[i].W * plan.Unions[i].H * LightmapBake.TexelBytes;
        if (total > LightmapAreaPlan.MaxUnionBytes) { why = $"{total >> 20} MiB of unions, past the {LightmapAreaPlan.MaxUnionBytes >> 20} MiB the install may hold"; return null; }
        plan.UnionBytes = new byte[plan.Unions.Length][];
        plan.UnionTables = new Entry[plan.Unions.Length][];
        for (int i = 0; i < plan.Unions.Length; i++)
        {
            ref Union want = ref plan.Unions[i];
            byte[]? bytes = LoadUnion(Path.Combine(dir, want.File), out Union got, out Entry[]? table);
            if (bytes == null || table == null) { why = $"page blob {want.File} is gone or refused"; return null; }
            if (got.Page != want.Page || got.X0 != want.X0 || got.Y0 != want.Y0 || got.W != want.W || got.H != want.H)
            { why = $"page blob {want.File} sits at page {got.Page} ({got.X0},{got.Y0}) {got.W}x{got.H}, the plan says page {want.Page} ({want.X0},{want.Y0}) {want.W}x{want.H}"; return null; }
            plan.UnionBytes[i] = bytes; plan.UnionTables[i] = table;
        }
        return plan;
    }

    /// <summary>Cache thread: the plan of a complete visit, plus a touch on every page blob it names so
    /// the prune ages them together rather than leaving a plan pointing at files it outlived.</summary>
    private void WriteArea(LightmapAreaPlan plan)
    {
        try
        {
            string dir = MeshChart.LedgerDir();
            Directory.CreateDirectory(dir);
            using (var fs = new FileStream(Path.Combine(dir, $"{plan.Key:x16}{AreaExt}"), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                plan.SerializeTo(fs);
            DateTime now = DateTime.UtcNow;
            for (int i = 0; i < plan.Unions.Length; i++)
            {
                string f = Path.Combine(dir, plan.Unions[i].File);
                try { if (File.Exists(f)) File.SetLastWriteTimeUtc(f, now); } catch (Exception) { }
            }
            MeshChart.Prune(dir, (long)Lightmap.Disk.V << 20);
        }
        catch (Exception e) { _log?.Info($"LightmapBake: the area bake cache for {plan.Key:x16} could not be written -- {e.Message}"); }
    }

    /// <summary>Headless: the round trip of a two-rect union and the refusals.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "BakeCache.SelfCheck");
        const int uw = 32, uh = 16;
        var union = new byte[uw * uh * LightmapBake.TexelBytes];
        for (int i = 0; i < union.Length; i++) union[i] = (byte)(i * 7);
        var table = new[] { new Entry { Key = 0x1234, X = 8, Y = 4, W = 16, H = 8 }, new Entry { Key = 0x5678, X = 0, Y = 12, W = 32, H = 4 } };
        var ms = new MemoryStream();
        SerializeTo(ms, 3, 512, 256, uw, uh, table, union);
        byte[] blob = ms.ToArray();
        byte[]? back = Deserialize(new MemoryStream(blob), out Union bu, out Entry[]? bt, out string? why);
        check.Expect(back != null && why == null && bu.W == uw && bu.H == uh && back.AsSpan().SequenceEqual(union), "the union round-trips byte-identical");
        check.Expect(bu.Page == 3 && bu.X0 == 512 && bu.Y0 == 256, "the union's page and origin round-trip (v9: what lets an area plan upload it whole)");
        check.Expect(bt != null && bt.Length == 2 && bt[1].Key == 0x5678 && bt[1].Y == 12 && bt[0].W == 16, "the table round-trips");
        check.Expect(Deserialize(new MemoryStream(blob, 0, blob.Length / 2), out _, out _, out why) == null && why == "truncation", "a cut blob is refused as truncation");
        blob[4] ^= 1;
        check.Expect(Deserialize(new MemoryStream(blob), out _, out _, out why) == null && why == "version", "another version is refused as version");
        // the address of the block (A, B: 16 bytes a 4x4 block) or texel (C: 4 bytes) at (8, 4) of a 32x16
        // union, worked by hand: block row 1 of 8 blocks + block 2 = block 10; C row 4 of 32 + 8 = texel 136
        check.Expect(LightmapBake.UnionOffset(0, 8, 4, uw, uh) == 160 && LightmapBake.UnionOffset(1, 8, 4, uw, uh) == 512 + 160
                     && LightmapBake.UnionOffset(2, 8, 4, uw, uh) == 1024 + 544, "a rect's first block or texel is addressed by plane, row and column of the union");
        var odd = new MemoryStream();
        SerializeTo(odd, 0, 0, 0, 30, 16, table, union);
        check.Expect(Deserialize(new MemoryStream(odd.ToArray()), out _, out _, out why) == null && why == "truncation", "a union that is not whole blocks is refused");
        var shifted = new MemoryStream();
        SerializeTo(shifted, 0, 514, 256, uw, uh, table, union);
        check.Expect(Deserialize(new MemoryStream(shifted.ToArray()), out _, out _, out why) == null && why == "truncation", "a union whose origin is off the block grid is refused");
        log.Info(check.Ok ? "BakeCache.SelfCheck: passed" : "BakeCache.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}

internal static partial class LightmapBake
{
    private static readonly BakeCache _cache = new();

    /// <summary>Byte offset of block-aligned (x, y) in a union readback: plane 0 = A and 1 = B, 16-byte blocks
    /// row-major; 2 = C, 4-byte texels row-major after both.</summary>
    internal static long UnionOffset(int plane, int x, int y, int uw, int uh)
        => plane < 2 ? (long)plane * uw * uh + (long)y * uw + 4L * x : 2L * uw * uh + ((long)y * uw + x) * 4;
}
