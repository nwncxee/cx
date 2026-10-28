using System;
using System.IO;
using CX.Framework;

namespace CX.Graphics;

/// <summary>The on-disk folder Vulkan's compiled SPIR-V modules and its pipeline cache live in,
/// <c>cx-shaders/</c> beside cx.cfg, swept of stale entries at first use.</summary>
internal static class ShaderCache
{
    /// <summary>Entries unused this long are swept at the session's first lookup: every CX release or shader
    /// edit orphans the previous set.</summary>
    private static readonly TimeSpan Stale = TimeSpan.FromDays(30);

    private static string? _dir;

    /// <inheritdoc cref="ShaderCache"/>
    internal static string Dir =>
        _dir ??= Sweep(Path.Combine(Native.UserDirectory() ?? Native.DefaultUserDirectory(), "cx-shaders"));

    private static string Sweep(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                foreach (string f in Directory.EnumerateFiles(dir))
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > Stale) File.Delete(f);
        }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
        return dir;
    }
}
