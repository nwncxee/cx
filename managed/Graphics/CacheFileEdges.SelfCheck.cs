using System;
using System.Diagnostics;
using System.IO;
using CX.Framework;

namespace CX.Graphics;

/// <summary>Edges of the damaged-cache report: a zeroed file (a sync placeholder never filled) and one cut inside its
/// header are named as damaged, one of another format is not, and the per-frame report waits for the damage to stop
/// before it speaks. Oracle: the literals and hand-written header bytes here, and the clock.</summary>
internal static class CacheFileEdges
{
    [SelfCheckSuite(SuitePhase.BeforeServices)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "CacheFileEdges.SelfCheck");
        string dir = Path.Combine(Path.GetTempPath(), $"cx-cachefile-edges-{Environment.ProcessId}");
        CacheFile.Reset();
        try
        {
            Directory.CreateDirectory(dir);
            string zeroed = Path.Combine(dir, "zeroed.bin");
            File.WriteAllBytes(zeroed, new byte[64]);
            check.Expect(CacheFile.Read(zeroed) == null, "a zeroed cache file is read as a miss");
            check.Expect(CacheFile.Report()?.Contains("zeroed.bin", StringComparison.Ordinal) == true, "a zeroed cache file is named as damaged");

            string stub = Path.Combine(dir, "stub.bin");
            File.WriteAllBytes(stub, [(byte)'C', (byte)'X', (byte)'C', (byte)'F', 1, 0]);
            check.Expect(CacheFile.Read(stub) == null && CacheFile.Report()?.Contains("stub.bin", StringComparison.Ordinal) == true,
                "a cache file cut inside its own header is named as damaged, not taken for an older release's");

            string later = Path.Combine(dir, "later.bin");
            File.WriteAllBytes(later, [(byte)'C', (byte)'X', (byte)'C', (byte)'F', 2, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4]);
            check.Expect(CacheFile.Read(later) == null && CacheFile.Report() == null,
                "a cache file of another format is a miss and is not named as damaged");

            string cut = Path.Combine(dir, "cut.bin");
            check.Expect(CacheFile.Write(cut, new byte[32]), "a payload is written");
            byte[] whole = File.ReadAllBytes(cut);
            File.WriteAllBytes(cut, whole[..(whole.Length - 4)]);
            CacheFile.Read(cut);
            long now = Stopwatch.GetTimestamp();
            check.Expect(CacheFile.ReportSettled(now) == null, "the report waits while damage may still be turning up");
            check.Expect(CacheFile.ReportSettled(now + Stopwatch.Frequency * 3)?.Contains("cut.bin", StringComparison.Ordinal) == true,
                "three seconds after the last damaged file, the report names it");
            check.Expect(CacheFile.ReportSettled(now + Stopwatch.Frequency * 6) == null, "and says it once");
        }
        finally
        {
            CacheFile.Reset();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        log.Info(check.Ok ? "CacheFileEdges.SelfCheck: passed" : "CacheFileEdges.SelfCheck: FAILED -- see above");
        return check.Ok;
    }
}
