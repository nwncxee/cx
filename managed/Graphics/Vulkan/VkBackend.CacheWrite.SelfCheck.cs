using System;
using System.IO;
using CX.Framework;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>Off-device: the blob a save hands a worker lands at its path whole, leaves no temporary behind, and
    /// replaces a file already there; a path that cannot be written says so and answers false. Oracle: the bytes the
    /// file holds afterwards, read back, against a blob built here.</summary>
    internal static void CacheWriteSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkBackend.CacheWriteSelfCheck");
        string dir = Path.Combine(Path.GetTempPath(), "cx-pipecache-check-" + Environment.TickCount64);
        string path = Path.Combine(dir, "vk-pipelines-check.bin");
        try
        {
            var blob = new byte[4096];
            for (int i = 0; i < blob.Length; i++) blob[i] = (byte)(i * 7 + 3);
            check.Expect(WriteCacheFile(blob, path, log), "a writable path refused the pipeline cache blob");
            byte[] back = File.Exists(path) ? File.ReadAllBytes(path) : [];
            check.Expect(back.Length == blob.Length && back.AsSpan().SequenceEqual(blob),
                $"the file does not hold the blob the save handed over ({back.Length} of {blob.Length} bytes)");
            check.Expect(!File.Exists(path + ".tmp"), "the temporary the write moved from is still beside the file");

            var second = new byte[17];
            for (int i = 0; i < second.Length; i++) second[i] = (byte)(255 - i);
            check.Expect(WriteCacheFile(second, path, log), "a second save over an existing cache file was refused");
            byte[] after = File.ReadAllBytes(path);
            check.Expect(after.Length == second.Length && after.AsSpan().SequenceEqual(second),
                $"a second save left the earlier blob in place ({after.Length} bytes)");

            // a directory where the file should be: the move has nowhere to land, and the failure must be loud, not silent
            string blocked = Path.Combine(dir, "blocked.bin");
            Directory.CreateDirectory(blocked);
            string said = "";
            var ear = new ExtenderLog { Tap = l => said += l + "\n" };
            check.Expect(!WriteCacheFile(blob, blocked, ear), "a path that cannot hold a file reported the cache saved");
            check.Expect(said.Contains("NOT saved", StringComparison.Ordinal),
                $"a failed pipeline-cache write said nothing loud ({said.Replace('\n', ' ')})");
        }
        catch (IOException e) { check.Skip($"the host refused the temporary folder -- {e.Message}"); }
        catch (UnauthorizedAccessException e) { check.Skip($"the host refused the temporary folder -- {e.Message}"); }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        if (check.Ok) log.Info("VkBackend.CacheWriteSelfCheck: passed");
        else log.Info("VkBackend.CacheWriteSelfCheck: FAILED");
    }
}
