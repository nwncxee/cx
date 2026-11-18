using System;
using System.Diagnostics;
using System.IO;
using CX.Framework;

namespace CX.Graphics;

/// <summary>Edges of the damaged-cache report: a zeroed file (a sync placeholder never filled) is named as damaged, and
/// the per-frame report waits for the damage to stop before it speaks. Oracle: the literals written here and the clock.</summary>
internal static class CacheFileEdges
{
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
