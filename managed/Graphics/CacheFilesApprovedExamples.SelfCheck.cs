using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CX.Alloy;
using CX.Framework;
using CX.Graphics.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Graphics;

/// <summary>A damaged cache file never changes what is drawn (approved examples E13-E16, E18-E20): each cache is
/// filled through its own write, damaged three ways, and read back through its own read. Oracle: what the same work
/// gives with no cache at all (a fresh compile, a chart with the ledger off, the blob as written, an empty seed).</summary>
internal static class CacheFilesApprovedExamples
{
    /// <summary>A file cut to half its length on a four-byte boundary (a write stops between whole words), the same length of zeros, and a copy with byte <paramref name="at"/> changed.</summary>
    internal static (string How, byte[] Bytes)[] Damage(byte[] good, int at)
    {
        byte[] changed = (byte[])good.Clone();
        changed[at] ^= 0x5a;
        return [("cut short", good[..(good.Length / 2 & ~3)]), ("zeroed", new byte[good.Length]), ($"byte {at} changed", changed)];
    }

    /// <summary>The disk filling up: a file that takes <paramref name="room"/> bytes, then refuses the rest.</summary>
    internal sealed class FullDisk(string path, int room) : Stream
    {
        private readonly FileStream _f = new(path, FileMode.Create, FileAccess.Write, FileShare.None);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            int fit = (int)Math.Min(buffer.Length, Math.Max(0, room - _f.Length));
            _f.Write(buffer[..fit]);
            _f.Flush();
            if (fit < buffer.Length) throw new IOException("No space left on device");
        }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _f.Length;
        public override long Position { get => _f.Position; set => throw new NotSupportedException(); }
        public override void Flush() => _f.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) _f.Dispose(); base.Dispose(disposing); }
    }

    /// <summary>The files in <paramref name="dir"/> that are not in <paramref name="before"/>.</summary>
    internal static string[] Added(string dir, HashSet<string> before) =>
        UserFoldersApprovedExamples.Files(dir).Where(f => !before.Contains(f)).ToArray();

    internal static HashSet<string> Listing(string dir) => [.. UserFoldersApprovedExamples.Files(dir)];

    /// <summary>Whether two charted jobs hold the same chart.</summary>
    internal static bool SameChart(MeshChart.Job a, MeshChart.Job b) =>
        a.Refused == b.Refused && a.OutVerts == b.OutVerts && a.Out0N == b.Out0N && a.W == b.W && a.H == b.H
        && a.Xref.AsSpan(0, a.OutVerts).SequenceEqual(b.Xref.AsSpan(0, b.OutVerts))
        && a.ChartUv.AsSpan(0, a.OutVerts * 2).SequenceEqual(b.ChartUv.AsSpan(0, b.OutVerts * 2))
        && a.Out0.AsSpan(0, a.Out0N).SequenceEqual(b.Out0.AsSpan(0, b.Out0N));

    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "CacheFilesApprovedExamples.SelfCheck");
        if (Native.Sym(SymbolMap.AppManager) != IntPtr.Zero) return check.Skip("on-device: the check damages cache files and a game session has its real ones");
        bool shaderc = true;
        foreach (string leaf in VulkanToolchain.Libraries) shaderc &= VulkanToolchain.Load(leaf);
        if (!shaderc) check.Skip("the staged shaderc did not load: the shader cache examples are not run");
        string root = Path.Combine(Path.GetTempPath(), $"cx-cachefiles-{Environment.ProcessId}");
        string ud = Path.Combine(root, "Neverwinter Nights");
        Directory.CreateDirectory(ud);
        UserFolders.Settle(UserFoldersApprovedExamples.Aliases(ud), ud, Path.Combine(ud, "logs"), log);
        CacheFile.Reset();
        byte[]? Compile(string source) => VulkanToolchain.Compile(source, "cachefiles-probe", ShaderKind.ComputeShader, out _);
        (byte[]? Good, string? File) Cached(string source)
        {
            var before = Listing(ShaderCache.Dir);
            byte[]? good = Compile(source);
            string? file = Added(ShaderCache.Dir, before).FirstOrDefault(f => f.EndsWith(".spv"));
            return (good, file);
        }
        (MeshChart.Job Reference, string? File) Charted(int n)
        {
            var reference = MeshChart.Cube(n);
            MeshChart.Chart(reference, useLedger: false);
            var before = Listing(MeshChart.LedgerDir());
            MeshChart.Chart(MeshChart.Cube(n));
            return (reference, Added(MeshChart.LedgerDir(), before).FirstOrDefault(f => f.EndsWith(".bin")));
        }
        try
        {
            if (shaderc)
            {
                // Given a shader cache file cut short, zeroed, or with some bytes changed
                // When the player loads an area that uses that shader
                // Then the area looks the same as it does with an empty cache
                string s13 = UserFoldersApprovedExamples.Probe("e13");
                var (good, file) = Cached(s13);
                check.Expect(good != null && file != null, "E13 the probe stage compiled and left a cache file");
                if (good != null && file != null)
                {
                    byte[] written = File.ReadAllBytes(file);
                    foreach (var (how, bytes) in Damage(written, written.Length / 2))
                    {
                        File.WriteAllBytes(file, bytes);
                        byte[]? got = Compile(s13);
                        check.Expect(got != null && got.AsSpan().SequenceEqual(good), $"E13 a shader cache file {how} gives the module a fresh compile gives");
                    }
                }
            }

            // Given a mesh chart or lightmap bake cache file cut short, zeroed, or with some bytes changed
            // When the player loads the area it belongs to
            // Then every object draws where it does with an empty cache, and none is missing
            var (reference, chartFile) = Charted(3);
            check.Expect(chartFile != null, "E14 the chart left a ledger file");
            if (chartFile != null)
                foreach (var (how, bytes) in Damage(File.ReadAllBytes(chartFile), 39))
                {
                    File.WriteAllBytes(chartFile, bytes);
                    var again = MeshChart.Cube(3);
                    MeshChart.Chart(again);
                    check.Expect(SameChart(reference, again), $"E14 a chart cache file {how} gives the chart an empty cache gives");
                }
            const int uw = 32, uh = 16;
            var union = new byte[uw * uh * LightmapBake.TexelBytes];
            for (int i = 0; i < union.Length; i++) union[i] = (byte)(i * 7);
            var table = new[] { new BakeCache.Entry { Key = 0x1234, X = 8, Y = 4, W = 16, H = 8 }, new BakeCache.Entry { Key = 0x5678, X = 0, Y = 12, W = 32, H = 4 } };
            string blobFile = Path.Combine(MeshChart.LedgerDir(), "cachefiles-check.lmp");
            check.Expect(BakeCache.WriteBlobFile(blobFile, 3, 512, 256, uw, uh, table, union), "E14 the bake blob was written");
            foreach (var (how, bytes) in Damage(File.ReadAllBytes(blobFile), 44))
            {
                File.WriteAllBytes(blobFile, bytes);
                byte[]? got = BakeCache.ReadBlobFile(blobFile, out _, out BakeCache.Entry[]? gotTable, null);
                bool asWritten = got != null && got.AsSpan().SequenceEqual(union) && gotTable != null && gotTable.Length == 2
                                 && gotTable[0].Key == 0x1234 && gotTable[0].X == 8 && gotTable[0].Y == 4 && gotTable[1].Key == 0x5678 && gotTable[1].Y == 12;
                check.Expect(got == null || asWritten, $"E14 a bake cache file {how} is read as a miss, never as other lightmap texels or rects");
            }

            // Given the pipeline cache file cut short, zeroed, or with some bytes changed
            // When the player starts the game with Alloy on
            // Then Alloy starts, and the first area looks the same as it does with an empty cache
            string pipeFile = Path.Combine(ShaderCache.Dir, "vk-pipelines-check.bin");
            var blob = new byte[256];
            new Random(7).NextBytes(blob);
            check.Expect(VkBackend.WriteCacheFile(blob, pipeFile, log), "E15 the pipeline cache was written");
            foreach (var (how, bytes) in Damage(File.ReadAllBytes(pipeFile), 100))
            {
                File.WriteAllBytes(pipeFile, bytes);
                byte[] seed = VkBackend.PipelineSeed(pipeFile);
                check.Expect(seed.Length == 0 || seed.AsSpan().SequenceEqual(blob), $"E15 a pipeline cache file {how} seeds the driver with nothing, as an empty cache does");
            }

            if (shaderc)
            {
                // Given two damaged shader cache files and one damaged chart cache file
                // When the player loads the area that reads them
                // Then cx.log says, once, that three damaged cache files were thrown away and rebuilt, and names them
                string sa = UserFoldersApprovedExamples.Probe("e16a"), sb = UserFoldersApprovedExamples.Probe("e16b");
                var (_, fa) = Cached(sa);
                var (_, fb) = Cached(sb);
                var (_, fc) = Charted(4);
                CacheFile.Reset();
                foreach (string? f in new[] { fa, fb, fc })
                    if (f != null) { byte[] b = File.ReadAllBytes(f); b[b.Length / 2] ^= 0x5a; File.WriteAllBytes(f, b); }
                Compile(sa); Compile(sb); MeshChart.Chart(MeshChart.Cube(4));
                string? line = CacheFile.Report();
                check.Expect(line != null && line.Contains('3') && new[] { fa, fb, fc }.All(f => f != null && line.Contains(Path.GetFileName(f))),
                    $"E16 one line counts three damaged files and names each: {line}");
                check.Expect(CacheFile.Report() == null, "E16 the line is said once");

                // Given a damaged shader cache file was thrown away and rebuilt in one launch
                // When the player starts again and loads the same area
                // Then cx.log says no shader was compiled again and no cache file was damaged
                string s18 = UserFoldersApprovedExamples.Probe("e18");
                var (good18, f18) = Cached(s18);
                if (f18 != null) { byte[] b = File.ReadAllBytes(f18); b[b.Length / 2] ^= 0x5a; File.WriteAllBytes(f18, b); }
                Compile(s18);
                CacheFile.Report();
                int fresh = VulkanToolchain.FreshCompiles;
                byte[]? next = Compile(s18);
                check.Expect(VulkanToolchain.FreshCompiles == fresh && CacheFile.Report() == null && next != null && good18 != null && next.AsSpan().SequenceEqual(good18),
                    $"E18 the next launch compiled {VulkanToolchain.FreshCompiles - fresh} stage(s) again and reads the module a fresh compile gives");

                // Given the game was killed while CX was writing a cache file
                // When the player starts again and loads the same area
                // Then the area looks the same as it does with an empty cache
                string s19 = UserFoldersApprovedExamples.Probe("e19");
                var (good19, f19) = Cached(s19);
                if (good19 != null && f19 != null)
                {
                    byte[] whole = File.ReadAllBytes(f19);
                    File.WriteAllBytes(f19, whole[..(whole.Length / 2 & ~3)]);
                    File.WriteAllBytes(f19 + ".4242.tmp", whole[..(whole.Length / 3)]);
                    byte[]? got = Compile(s19);
                    check.Expect(got != null && got.AsSpan().SequenceEqual(good19), "E19 a write the kill cut short gives the module a fresh compile gives");
                }

                // Given the disk holding the cache folder is full
                // When CX tries to save a cache file
                // Then no part of that file is left in the cache folder, and the area still draws
                var before = Listing(ShaderCache.Dir);
                CacheFile.Sink = p => new FullDisk(p, 16);
                byte[]? drawn = Compile(UserFoldersApprovedExamples.Probe("e20"));
                CacheFile.Sink = null;
                string[] left = Added(ShaderCache.Dir, before);
                check.Expect(drawn != null, "E20 the stage still compiles on a full disk");
                check.Expect(left.Length == 0, $"E20 no part of the file is left: {string.Join(", ", left.Select(Path.GetFileName))}");
            }
        }
        finally
        {
            CacheFile.Reset();
            UserFolders.Reset();
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        log.Info(check.Ok ? "CacheFilesApprovedExamples.SelfCheck: passed" : "CacheFilesApprovedExamples.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
