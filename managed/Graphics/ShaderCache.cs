using System;
using System.IO;
using CX.Framework;

namespace CX.Graphics;

/// <summary>The on-disk folder Vulkan's compiled SPIR-V modules and its pipeline cache live in,
/// <c>cx-shaders/</c> beside cx.cfg, swept of stale entries at first use.</summary>
internal static partial class ShaderCache
{
    /// <summary>Entries unused this long are swept at the session's first lookup: every CX release or shader
    /// edit orphans the previous set.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromDays(30);

    /// <summary>A pipeline cache blob goes sooner than a module: it is a hundred megabytes where a module is
    /// kilobytes, and any session that brings the device up rewrites the blob it read, so a blob left unwritten
    /// this long belongs to a driver identity that no longer answers on this machine.</summary>
    private static readonly TimeSpan StalePipelines = TimeSpan.FromDays(7);

    /// <summary>What every Vulkan pipeline cache blob's name starts with; the rest of it is the driver identity
    /// the blob's own header carries, and the only identity a driver reads its data back under.</summary>
    internal const string PipelinePrefix = "vk-pipelines-";

    /// <summary>Whether the sweep deletes <paramref name="leaf"/>, last written <paramref name="age"/> ago.</summary>
    internal static bool Sweeps(string leaf, TimeSpan age)
        => age > (leaf.StartsWith(PipelinePrefix, StringComparison.Ordinal) ? StalePipelines : Stale);

    private static string? _dir;

    /// <inheritdoc cref="ShaderCache"/>
    internal static string Dir =>
        _dir ??= Sweep(Path.Combine(Native.UserDirectory() ?? Native.DefaultUserDirectory(), "cx-shaders"));

    private static string Sweep(string dir)
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
                    string leaf = about.Name;
                    if (!Sweeps(leaf, DateTime.UtcNow - about.LastWriteTimeUtc)) continue;
                    long bytes = about.Length;
                    try { File.Delete(f); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        log?.Always($"ShaderCache: {leaf} is stale and was NOT deleted -- {e.Message}");
                        continue;
                    }
                    gone++;
                    freed += bytes;
                    if (leaf.StartsWith(PipelinePrefix, StringComparison.Ordinal))
                        log?.Always($"ShaderCache: dropped the pipeline cache {leaf} ({bytes / (1024 * 1024)} MB), "
                                  + "unwritten since before the window -- a session on that driver rebuilds its pipelines");
                }
        }
        catch (IOException e) { log?.Always($"ShaderCache: {dir} could NOT be swept -- {e.Message}"); }
        catch (UnauthorizedAccessException e) { log?.Always($"ShaderCache: {dir} could NOT be swept -- {e.Message}"); }
        if (gone > 0) log?.Info($"ShaderCache: swept {gone} stale file(s), {freed / 1024} KiB");
        return dir;
    }
}
