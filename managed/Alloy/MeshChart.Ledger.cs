using System;
using System.IO;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The chart LEDGER: one file per mesh content under <c>charts/</c> in CX's cache folder -- versioned
/// header, content key, per-vertex source index + chart UV in atlas texel units, the split index run, atlas size.
/// Nothing Alloy-internal, so a toolset could ship these in a module. Worker threads only, several of them: every
/// tally and prune takes <c>_diskGate</c>, and a hit touches the file's write time so a live chart never ages out.</summary>
internal static partial class MeshChart
{
    /// <summary>"CXCH" little-endian.</summary>
    private const uint LedgerMagic = 0x48435843;   // not-an-offset: the file's first four bytes
    /// <summary>Bumped with the header, the UV units or <see cref="AtlasTexelsPerUnit"/>: an older file misses.</summary>
    internal const int LedgerVersion = 2;
    private const int HeaderBytes = 4 + 4 + 8 + 4 + 4 + 4 + 4 + 2 + 2;

    private static long _diskBytes = -1;
    /// <summary>The byte tally and the prune are shared by every <c>cx-chart</c> worker.</summary>
    private static readonly object _diskGate = new();

    internal static string LedgerDir() => Path.Combine(UserFolders.Cache, "charts");
    private static string LedgerFile(ulong key) => Path.Combine(LedgerDir(), $"{key:x16}.bin");

    /// <summary>Serialize a charted job. Pure, so the self-check proves the round trip with no disk.</summary>
    internal static byte[] Serialize(Job j)
    {
        var ms = new MemoryStream(HeaderBytes + j.OutVerts * 10 + j.Out0N * 2);
        var w = new BinaryWriter(ms);
        w.Write(LedgerMagic); w.Write(LedgerVersion); w.Write(j.Content);
        w.Write(j.VertCount); w.Write(j.IdxCount); w.Write(j.OutVerts); w.Write(j.Out0N);
        w.Write(j.W); w.Write(j.H);
        for (int i = 0; i < j.OutVerts; i++) { w.Write(j.Xref[i]); w.Write(j.ChartUv[i * 2]); w.Write(j.ChartUv[i * 2 + 1]); }
        for (int i = 0; i < j.Out0N; i++) w.Write(j.Out0[i]);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>The inverse, into a job whose input half is already filled. Returns the refusal as text
    /// (<c>version</c>, <c>key</c>, <c>truncation</c>) or null on success; never throws.</summary>
    internal static string? Deserialize(byte[] blob, Job j)
    {
        try
        {
            var r = new BinaryReader(new MemoryStream(blob, writable: false));
            if (blob.Length < 8 || r.ReadUInt32() != LedgerMagic || r.ReadInt32() != LedgerVersion) return "version";
            if (blob.Length < HeaderBytes) return "truncation";
            if (r.ReadUInt64() != j.Content) return "key";
            int inV = r.ReadInt32(), inI = r.ReadInt32(), nv = r.ReadInt32(), ni = r.ReadInt32();
            ushort w = r.ReadUInt16(), h = r.ReadUInt16();
            if (inV != j.VertCount || inI != j.IdxCount) return "key";
            if (nv <= 0 || nv > 65535 || ni != j.IdxCount || w == 0 || h == 0) return "truncation";
            if (blob.Length != HeaderBytes + nv * 10 + ni * 2) return "truncation";
            Grow(ref j.Xref, nv); Grow(ref j.ChartUv, nv * 2); Grow(ref j.Out0, ni);
            for (int i = 0; i < nv; i++)
            {
                ushort x = r.ReadUInt16();
                if (x >= j.VertCount) return "truncation";
                j.Xref[i] = x; j.ChartUv[i * 2] = r.ReadSingle(); j.ChartUv[i * 2 + 1] = r.ReadSingle();
            }
            for (int i = 0; i < ni; i++)
            {
                ushort x = r.ReadUInt16();
                if (x >= nv) return "truncation";
                j.Out0[i] = x;
            }
            j.OutVerts = nv; j.Out0N = ni; j.W = w; j.H = h;
            return null;
        }
        catch (Exception) { return "truncation"; }
    }

    /// <summary>Probe the ledger for this job's content. A refused file is deleted: it is junk under this
    /// version, and the mesh is rebuilt and rewritten behind it.</summary>
    private static bool LedgerLoad(Job j)
    {
        string f = LedgerFile(j.Content);
        try
        {
            if (CacheFile.Read(f) is not { } blob) return false;
            string? why = Deserialize(blob, j);
            if (why == null) { File.SetLastWriteTimeUtc(f, DateTime.UtcNow); return true; }
            j.Stale = true;   // counted in Pump: the tallies are the render thread's
            File.Delete(f);
            lock (_diskGate) _diskBytes = -1;
            return false;
        }
        catch (Exception e) { _log?.Info($"MeshChart: the chart cache could not be read -- {e.Message}"); return false; }
    }

    private static void LedgerSave(Job j)
    {
        try
        {
            string dir = LedgerDir();
            byte[] blob = Serialize(j);
            if (!CacheFile.Write(LedgerFile(j.Content), blob)) return;
            lock (_diskGate)
            {
                if (_diskBytes < 0) _diskBytes = Tally(dir);
                else _diskBytes += blob.Length;
                long budget = (long)Lightmap.Disk.V << 20;
                if (_diskBytes > budget) _diskBytes = Prune(dir, budget);
            }
        }
        catch (Exception e) { _log?.Info($"MeshChart: the chart cache could not be written -- {e.Message}"); }
    }

    private static long Tally(string dir)
    {
        long sum = 0;
        foreach (var f in new DirectoryInfo(dir).GetFiles()) sum += f.Length;
        return sum;
    }

    /// <summary>Delete oldest-first until the directory fits <paramref name="budget"/>; returns what is left.
    /// Every file in the directory: the chart ledger and the bake cache's <c>.lmp</c> blobs share the budget.</summary>
    internal static long Prune(string dir, long budget)
    {
        var files = new DirectoryInfo(dir).GetFiles();
        long sum = 0;
        foreach (var f in files) sum += f.Length;
        if (sum <= budget) return sum;
        Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
        for (int i = 0; i < files.Length && sum > budget; i++) { sum -= files[i].Length; files[i].Delete(); }
        return sum;
    }
}
