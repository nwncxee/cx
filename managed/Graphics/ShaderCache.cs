using System;
using System.IO;
using CX.Framework;

namespace CX.Graphics;

/// <summary>The on-disk folder Vulkan's compiled SPIR-V modules and its pipeline cache live in,
/// <c>shaders/</c> in CX's cache folder, swept of stale entries at first use.</summary>
internal static partial class ShaderCache
{
    /// <summary>Entries unused this long are swept at the session's first lookup: every CX release or shader
    /// edit orphans the previous set.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromDays(30);

    /// <summary>How long a superseded pipeline cache blob is left alone before <see cref="Retire"/> takes it: a
    /// machine that alternates two drivers for one GPU writes each blob within the window, so both survive.
    /// Age never decides the blob belonging to the driver running now -- <see cref="Superseded"/> does.</summary>
    private static readonly TimeSpan StalePipelines = TimeSpan.FromDays(7);

    /// <summary>What every Vulkan pipeline cache blob's name starts with; the rest of it is the driver identity
    /// the blob's own header carries, and the only identity a driver reads its data back under.</summary>
    internal const string PipelinePrefix = "vk-pipelines-";

    /// <summary>The folder last swept: a folder the session moves to is swept once, on its first use from any thread.</summary>
    private sealed class ShaderCacheState { internal string? Swept; }

    private static readonly ShaderCacheState _st = new();

    /// <inheritdoc cref="ShaderCache"/>
    internal static string Dir
    {
        get
        {
            string dir = Path.Combine(UserFolders.Cache, "shaders");
            if (System.Threading.Interlocked.Exchange(ref _st.Swept, dir) != dir) Sweep(dir);
            return dir;
        }
    }

    private static void Sweep(string dir)
    {
        ExtenderLog? log = KnobGroup.Log;
        int gone = 0;
        long freed = 0;
        try
        {
            if (Directory.Exists(dir))
                foreach (string f in Directory.EnumerateFiles(dir))
                {
                    var about = new FileInfo(f);
                    if (DateTime.UtcNow - about.LastWriteTimeUtc <= Stale) continue;
                    long bytes = about.Length;
                    try { File.Delete(f); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        log?.Always($"ShaderCache: {about.Name} is stale and was NOT deleted -- {e.Message}");
                        continue;
                    }
                    gone++;
                    freed += bytes;
                }
        }
        catch (IOException e) { log?.Always($"ShaderCache: {dir} could NOT be swept -- {e.Message}"); }
        catch (UnauthorizedAccessException e) { log?.Always($"ShaderCache: {dir} could NOT be swept -- {e.Message}"); }
        if (gone > 0) log?.Info($"ShaderCache: swept {gone} stale file(s), {freed / 1024} KiB");
    }

    /// <summary>Whether <paramref name="leaf"/> is a blob for this GPU under a driver identity it no longer
    /// reports, which vkCreatePipelineCache would ignore as initial data and no driver here can read again.
    /// <paramref name="mine"/>, the leaf the driver running now reads, is never superseded at any age; another
    /// GPU's is left to the age sweep, and one written inside the window stays, so two live ICDs both keep theirs.</summary>
    internal static bool Superseded(string leaf, string mine, TimeSpan age)
    {
        if (!mine.StartsWith(PipelinePrefix, StringComparison.Ordinal) || leaf == mine || age <= StalePipelines)
            return false;
        int gpu = mine.LastIndexOf('-') + 1;   // the name is vk-pipelines-<vendor>-<device>-<cache uuid>.bin
        return gpu > 0 && leaf.StartsWith(mine[..gpu], StringComparison.Ordinal);
    }

    /// <summary>Delete the blobs in <paramref name="dir"/> that <see cref="Superseded"/> answers for beside
    /// <paramref name="mine"/>, each one named with the megabytes it frees: a retired driver build's pipelines
    /// are a hundred megabytes nothing on this machine will ever read again. Loud when one refuses to go.</summary>
    internal static void Retire(string dir, string mine, ExtenderLog? log)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.EnumerateFiles(dir, PipelinePrefix + "*"))
            {
                var about = new FileInfo(f);
                if (!Superseded(about.Name, mine, DateTime.UtcNow - about.LastWriteTimeUtc)) continue;
                long bytes = about.Length;
                try { File.Delete(f); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { log?.Always($"ShaderCache: the retired pipeline cache {about.Name} was NOT deleted -- {e.Message}"); continue; }
                log?.Always($"ShaderCache: dropped {about.Name} ({bytes / (1024 * 1024)} MB) -- this GPU under a driver "
                          + "identity it no longer reports, so no driver here could read it again");
            }
        }
        catch (IOException e) { log?.Always($"ShaderCache: the retired pipeline caches in {dir} were NOT swept -- {e.Message}"); }
        catch (UnauthorizedAccessException e) { log?.Always($"ShaderCache: the retired pipeline caches in {dir} were NOT swept -- {e.Message}"); }
    }
}
