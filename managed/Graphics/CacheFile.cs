using System;
using System.Collections.Generic;
using System.IO;

namespace CX.Graphics;

/// <summary>One file of CX's on-disk caches (SPIR-V modules, the pipeline cache, mesh charts, lightmap bakes),
/// read whole and written whole.</summary>
internal static class CacheFile
{
    /// <summary>The disk seam and the damaged files not yet reported.</summary>
    internal sealed class CacheFileState
    {
        internal Func<string, Stream>? Sink;
        internal readonly List<string> Damaged = new();
    }

    private static CacheFileState _st = new();

    /// <summary>Opens a file for writing in place of the disk; null is the disk.</summary>
    internal static Func<string, Stream>? Sink { set => _st.Sink = value; }

    /// <summary>The payload at <paramref name="path"/>, or null when there is none.</summary>
    internal static byte[]? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllBytes(path) : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Put <paramref name="payload"/> at <paramref name="path"/>; false when it did not land.</summary>
    internal static bool Write(string path, ReadOnlySpan<byte> payload)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using Stream s = _st.Sink?.Invoke(path) ?? new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            s.Write(payload);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The cx.log line naming the damaged files thrown away since the last report, or null.</summary>
    internal static string? Report() => null;

    /// <summary>Forget the seam and the tally.</summary>
    internal static void Reset() => _st = new();
}
