using System;
using System.IO;
using System.IO.Compression;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Headless: the charter over two fixtures and the ledger's round trip. A subdivided cube (every
/// triangle charted, no overlap, one chart per planar face, byte-identical across runs) and one real curved
/// placeable -- <c>plc_x0_jar</c>'s <c>jar_base</c> trimesh, extracted offline and embedded gzipped as
/// <c>Alloy/ChartFixture.bin.gz</c> (verts, indices, positions, normals, ushort indices).</summary>
internal static partial class MeshChart
{
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);

        var cube = Cube(4);
        Chart(cube, useLedger: false);
        check.Expect(cube.Refused == Refusal.None, $"the cube was refused ({RefusalName[(int)cube.Refused]})");
        if (cube.Refused == Refusal.None)
        {
            check.Expect(cube.Out0N == cube.IdxCount, "the cube lost triangles in the chart");
            check.Expect(cube.Charts == 6, $"a cube's six planar faces are {cube.Charts} charts, not six");
            check.Expect(cube.W > 0 && cube.H > 0, "the cube atlas has no size");
            check.Expect(Covered(cube, out int over) && over == 0, $"chart UVs fall outside the atlas, or {over} texels are drawn twice");
            var again = Cube(4);
            Chart(again, useLedger: false);
            check.Expect(Serialize(cube).AsSpan().SequenceEqual(Serialize(again)), "two runs over the same cube differ");
            var moved = Cube(4);
            for (int i = 0; i < moved.VertCount; i++) { moved.Pos[i * 3] += 1234.5f; moved.Pos[i * 3 + 2] -= 77.25f; }
            Chart(moved, useLedger: false);
            check.Expect(moved.Content == cube.Content && Serialize(moved).AsSpan().SequenceEqual(Serialize(cube)),
              "the same mesh placed elsewhere in the world is not the same content");

            var fresh = Cube(4);
            fresh.Content = ContentKey(fresh);
            check.Expect(Deserialize(Serialize(cube), fresh) == null, "the ledger round trip refused its own bytes");
            check.Expect(fresh.OutVerts == cube.OutVerts && fresh.Out0N == cube.Out0N && fresh.W == cube.W
              && fresh.ChartUv.AsSpan(0, fresh.OutVerts * 2).SequenceEqual(cube.ChartUv.AsSpan(0, cube.OutVerts * 2)),
              "the ledger round trip changed the chart");
            byte[] blob = Serialize(cube);
            byte[] old = (byte[])blob.Clone(); BitConverter.GetBytes(LedgerVersion + 1).CopyTo(old, 4);
            check.Expect(Deserialize(old, fresh) == "version", "an older file is not refused as 'version'");
            fresh.Content ^= 1;
            check.Expect(Deserialize(blob, fresh) == "key", "another mesh's file is not refused as 'key'");
            fresh.Content ^= 1;
            check.Expect(Deserialize(blob.AsSpan(0, blob.Length - 7).ToArray(), fresh) == "truncation", "a short file is not refused as 'truncation'");
            check.Expect(Deserialize(blob.AsSpan(0, 12).ToArray(), fresh) == "truncation", "a file cut inside the header is not refused as 'truncation'");
        }

        var bad = new Job { VertCount = 3, IdxCount = 3, Pos = new float[9], Idx = new ushort[] { 0, 1, 2 } };
        try { Chart(bad, useLedger: false); check.Expect(bad.Refused != Refusal.None, "a degenerate mesh was charted"); }
        catch (Exception e) { check.Expect(false, $"a degenerate mesh threw ({e.GetType().Name})"); }

        var jar = Fixture(log);
        if (jar == null) check.Expect(false, "the curved placeable fixture is missing from the image");
        else
        {
            Chart(jar, useLedger: false);
            check.Expect(jar.Refused == Refusal.None, $"the curved placeable was refused ({RefusalName[(int)jar.Refused]})");
            if (jar.Refused == Refusal.None)
            {
                check.Expect(jar.Out0N == jar.IdxCount, "the curved placeable lost triangles");
                check.Expect(Covered(jar, out int over) && over == 0, $"the curved placeable's chart overlaps ({over} texels)");
                check.Expect(jar.OutVerts > jar.VertCount, "a closed curved mesh charted with no seam split at all");
            }
        }

        string dir = Path.Combine(Path.GetTempPath(), "cx-chart-selfcheck");
        try
        {
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir)) File.Delete(f);
            var t0 = DateTime.UtcNow.AddMinutes(-3);
            for (int i = 0; i < 3; i++)
            {
                string f = Path.Combine(dir, $"{i}.bin");
                File.WriteAllBytes(f, new byte[100]);
                File.SetLastWriteTimeUtc(f, t0.AddMinutes(i));
            }
            long left = Prune(dir, 250);
            check.Expect(left == 200 && !File.Exists(Path.Combine(dir, "0.bin")) && File.Exists(Path.Combine(dir, "2.bin")),
              "the prune did not keep the budget oldest-first");
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) { check.Expect(false, $"the prune check could not run ({e.Message})"); }

        // the memo, over two real arena ranges (CX-114: the places are MeshArena's, so the fixture files
        // them there and asserts against what it hands back)
        CX.Alloy.Db.MeshArena.FakeVaoForCheck(CX.Alloy.Db.MeshArena.Fmt.Static, 1);
        var stage = new float[4 * Stride];
        for (int i = 0; i < 4; i++) { stage[i * Stride] = i; stage[i * Stride + 1] = i * 2; }
        ushort[] i0 = { 0, 1, 2 }, i1 = { 0, 1, 2, 0, 2, 3 };
        int rTwo = CX.Alloy.Db.MeshArena.AcquireRaw(0xC114_0001, CX.Alloy.Db.MeshArena.Fmt.Static, 4, stage,
                                                    i0, 3, i1, 6, null, 0, default, default);
        int rOne = CX.Alloy.Db.MeshArena.AcquireRaw(0xC114_0002, CX.Alloy.Db.MeshArena.Fmt.Static, 4, stage,
                                                    i0, 3, null, 0, null, 0, default, default);
        CX.Alloy.Db.MeshArena.Runs(rTwo, 1, out int wantBase, out nint wantOff, out _, out _);
        CX.Alloy.Db.MeshArena.Runs(rOne, 0, out int oneBase, out nint oneOff, out _, out _);
        var memo = default(RangeMemo);
        _table[KeyOf(-7, 1)] = new Entry { Done = true, Range = rTwo };
        check.Expect(Range(ref memo, -7, 1, 2, out int mb, out nint mo, out _, out int ml)
                     && mb == wantBase && mo == wantOff && ml == 1,
          "the memo did not resolve LOD 2 to the finest level that exists");
        _table[KeyOf(-7, 1)] = new Entry { Done = true, Range = rOne };
        check.Expect(Range(ref memo, -7, 1, 2, out mb, out _, out _, out _) && mb == wantBase, "the memo re-probed inside one epoch");
        check.Expect(Range(ref memo, -7, 1, 0, out mb, out mo, out _, out _) && mb == oneBase && mo == oneOff, "a LOD change did not re-probe");
        check.Expect(PlacedKey(0xC114, System.Numerics.Vector3.Zero) != PlacedKey(0xC114, new System.Numerics.Vector3(0f, 0f, 5f)),
          "two translated copies of one mesh share an arena range (the second draws at the first one's height)");
        // a DB chart is keyed by the plain range's INDEX: an entry filed for a previous tenant must not draw
        ulong tenant = CX.Alloy.Db.MeshArena.SigOf(rOne);
        _table[KeyOf(DbSlot, rOne)] = new Entry { Done = true, Range = rTwo, Sig = tenant };
        check.Expect(tenant != 0 && Range(DbSlot, rOne, 0, out _, out _, out _, out _),
          "a DB chart filed for the range's current mesh does not draw");
        _table[KeyOf(DbSlot, rOne)] = new Entry { Done = true, Range = rTwo, Sig = tenant ^ (1UL << 48) };
        check.Expect(!Range(DbSlot, rOne, 0, out _, out _, out _, out _),
          "a DB chart filed for the range's PREVIOUS tenant still draws (recycled range index)");
        _table.Remove(KeyOf(DbSlot, rOne));
        ForgetSlot(-7);
        check.Expect(!Range(ref memo, -7, 1, 0, out _, out _, out _, out _), "the memo outlived a forgotten slot");
        CX.Alloy.Db.MeshArena.Release(rTwo);
        CX.Alloy.Db.MeshArena.ResetForCheck();

        // the job table's own round trip, no worker: claim, file under the claim, refuse a stale sig, forget one slot
        var t = new SlotJobTable<Job, Entry>(8, 1);
        t.Offer(t.Rent(), KeyOf(7, 1), 11); t.Offer(t.Rent(), KeyOf(8, 1), 12);
        check.Expect(!t.QueueHasRoom, "a full queue still has room");
        check.Expect(t.File(KeyOf(7, 1), 11, new Entry { Done = true, Sig = 11 }), "a result under its claim was refused");
        check.Expect(!t.File(KeyOf(7, 1), 99, default), "a result under a stale signature was filed");
        t.ForgetSlot(7);
        check.Expect(!t.Entries.ContainsKey(KeyOf(7, 1)) && t.Entries.ContainsKey(KeyOf(8, 1)), "ForgetSlot did not drop exactly its slot");
        t.Revoke();
        check.Expect(t.Entries.Count == 0 && t.QueueHasRoom, "Revoke left entries or queued jobs behind");

        log.Info(check.Ok ? "MeshChart.SelfCheck: passed" : "MeshChart.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A cube of side 2 with each face an N x N grid, vertices shared within a face only.</summary>
    internal static Job Cube(int n)
    {
        int fv = (n + 1) * (n + 1);
        var j = new Job { VertCount = 6 * fv, IdxCount = 6 * n * n * 6, HasNrm = true };
        j.Pos = new float[j.VertCount * 3]; j.Nrm = new float[j.VertCount * 3]; j.Idx = new ushort[j.IdxCount];
        int vi = 0, ii = 0;
        for (int f = 0; f < 6; f++)
        {
            int ax = f / 2, u = (ax + 1) % 3, v = (ax + 2) % 3;
            float sgn = (f & 1) != 0 ? 1f : -1f;
            int b = vi;
            for (int y = 0; y <= n; y++)
                for (int x = 0; x <= n; x++)
                {
                    j.Pos[vi * 3 + ax] = sgn; j.Pos[vi * 3 + u] = -1f + 2f * x / n; j.Pos[vi * 3 + v] = -1f + 2f * y / n;
                    j.Nrm[vi * 3 + ax] = sgn;
                    vi++;
                }
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int a = b + y * (n + 1) + x, c = a + n + 1;
                    j.Idx[ii++] = (ushort)a; j.Idx[ii++] = (ushort)(a + 1); j.Idx[ii++] = (ushort)c;
                    j.Idx[ii++] = (ushort)(a + 1); j.Idx[ii++] = (ushort)(c + 1); j.Idx[ii++] = (ushort)c;
                }
        }
        return j;
    }

    /// <summary>Every chart UV inside the atlas, and no texel centre strictly inside two triangles.</summary>
    private static bool Covered(Job j, out int overlaps)
    {
        overlaps = 0;
        var hit = new byte[j.W * j.H];
        for (int i = 0; i < j.OutVerts; i++)
        {
            float u = j.ChartUv[i * 2], v = j.ChartUv[i * 2 + 1];
            if (u < 0f || v < 0f || u > j.W || v > j.H) return false;
        }
        for (int t = 0; t < j.Out0N; t += 3)
        {
            int a = j.Out0[t], b = j.Out0[t + 1], c = j.Out0[t + 2];
            float ax = j.ChartUv[a * 2], ay = j.ChartUv[a * 2 + 1], bx = j.ChartUv[b * 2], by = j.ChartUv[b * 2 + 1];
            float cx = j.ChartUv[c * 2], cy = j.ChartUv[c * 2 + 1];
            int x0 = (int)MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx))), x1 = (int)MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx)));
            int y0 = (int)MathF.Floor(MathF.Min(ay, MathF.Min(by, cy))), y1 = (int)MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy)));
            for (int y = Math.Max(y0, 0); y < Math.Min(y1, j.H); y++)
                for (int x = Math.Max(x0, 0); x < Math.Min(x1, j.W); x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float e0 = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
                    float e1 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
                    float e2 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
                    bool inside = e0 > 1e-4f && e1 > 1e-4f && e2 > 1e-4f || e0 < -1e-4f && e1 < -1e-4f && e2 < -1e-4f;
                    if (inside && ++hit[y * j.W + x] > 1) overlaps++;
                }
        }
        return true;
    }

    private static Job? Fixture(ExtenderLog log)
    {
        try
        {
            using Stream? s = typeof(MeshChart).Assembly.GetManifestResourceStream("ChartFixture");
            if (s == null) return null;
            using var gz = new GZipStream(s, CompressionMode.Decompress);
            var r = new BinaryReader(gz);
            int nv = r.ReadInt32(), ni = r.ReadInt32();
            var j = new Job { VertCount = nv, IdxCount = ni, HasNrm = true, Pos = new float[nv * 3], Nrm = new float[nv * 3], Idx = new ushort[ni] };
            for (int i = 0; i < nv * 3; i++) j.Pos[i] = r.ReadSingle();
            for (int i = 0; i < nv * 3; i++) j.Nrm[i] = r.ReadSingle();
            for (int i = 0; i < ni; i++) j.Idx[i] = r.ReadUInt16();
            return j;
        }
        catch (Exception e) { log.Info($"MeshChart.SelfCheck: the fixture did not load -- {e.Message}"); return null; }
    }
}
