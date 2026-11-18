using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using CX.Alloy.Mdl;

namespace CX.Graphics;

/// <summary>One file of CX's on-disk caches (SPIR-V modules, the pipeline cache, mesh charts, lightmap bakes), read
/// whole and written whole behind a header holding the payload's length and hash. A file whose payload no longer
/// matches is damaged: deleted, read as a miss, and named once in cx.log. Writes go through a temporary name, so a
/// cut-short write never sits under the real one.</summary>
internal static class CacheFile
{
    /// <summary>The disk seam and the damaged files not yet reported.</summary>
    internal sealed class CacheFileState
    {
        internal Func<string, Stream>? Sink;
        internal readonly List<string> Damaged = new();
        internal long LastDamage;
    }

    private static CacheFileState _st = new();

    /// <summary>"CXCF" little-endian: the first four bytes of every cache file this release writes.</summary>
    private const uint Magic = 0x46435843;   // not-an-offset: the file's first four bytes
    /// <summary>Bumped with the header's layout.</summary>
    private const uint Format = 1;
    /// <summary>Magic, format, payload length, payload hash.</summary>
    internal const int HeaderBytes = 24;

    /// <summary>Opens a file for writing in place of the disk; null is the disk.</summary>
    internal static Func<string, Stream>? Sink { set => _st.Sink = value; }

    /// <summary>The payload at <paramref name="path"/>, or null when there is none, it was written by an older release
    /// (deleted, silently), or it is damaged (deleted, and counted for <see cref="Report"/>). Never throws.</summary>
    internal static byte[]? Read(string path)
    {
        byte[] file;
        try
        {
            if (!File.Exists(path)) return null;
            file = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        string? why = Check(file);
        if (why == null) return file[HeaderBytes..];
        try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (why != "older")
        {
            var st = _st;
            lock (st.Damaged) { st.Damaged.Add(Path.GetFileName(path)); st.LastDamage = System.Diagnostics.Stopwatch.GetTimestamp(); }
        }
        return null;
    }

    /// <summary>Null when <paramref name="file"/> is a whole cache file; "older" when it is no file of this format,
    /// "damaged" when it was one and its payload no longer matches.</summary>
    private static string? Check(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderBytes) return file.IndexOfAnyExcept((byte)0) < 0 ? "damaged" : "older";
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(file);
        if (magic == 0) return "damaged";
        if (magic != Magic || BinaryPrimitives.ReadUInt32LittleEndian(file[4..]) != Format) return "older";
        ulong length = BinaryPrimitives.ReadUInt64LittleEndian(file[8..]);
        if (length != (ulong)(file.Length - HeaderBytes)) return "damaged";
        return BinaryPrimitives.ReadUInt64LittleEndian(file[16..]) == MdlParser.ContentHash(file[HeaderBytes..]) ? null : "damaged";
    }

    /// <summary>Whether <paramref name="s"/> opens with this format's header; on true it stands at the payload. For a
    /// reader that wants the start of a payload without the whole file (the bake index).</summary>
    internal static bool Framed(Stream s)
    {
        Span<byte> head = stackalloc byte[HeaderBytes];
        return s.Read(head) == HeaderBytes && BinaryPrimitives.ReadUInt32LittleEndian(head) == Magic
               && BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) == Format;
    }

    /// <summary>Put <paramref name="payload"/> at <paramref name="path"/> through a temporary beside it; false when it
    /// did not land, and then nothing of it is left behind.</summary>
    internal static bool Write(string path, ReadOnlySpan<byte> payload)
    {
        string tmp = $"{path}.{Environment.ProcessId}.tmp";
        Span<byte> head = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(head, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], Format);
        BinaryPrimitives.WriteUInt64LittleEndian(head[8..], (ulong)payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(head[16..], MdlParser.ContentHash(payload));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using (Stream s = _st.Sink?.Invoke(tmp) ?? new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                s.Write(head);
                s.Write(payload);
            }
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Delete(tmp); } catch (Exception d) when (d is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>The cx.log line naming the damaged files thrown away since the last report, or null when there were none.</summary>
    internal static string? Report()
    {
        var st = _st;
        lock (st.Damaged)
        {
            if (st.Damaged.Count == 0) return null;
            string line = $"CacheFile: {st.Damaged.Count} damaged cache file(s) thrown away and rebuilt: {string.Join(", ", st.Damaged)}";
            st.Damaged.Clear();
            return line;
        }
    }

    /// <summary>How long the tally waits for more damage before it reports: an area's reads land over many frames,
    /// and one line names them all.</summary>
    private static readonly long Quiet = System.Diagnostics.Stopwatch.Frequency * 2;

    /// <summary><see cref="Report"/>, once no damaged file has turned up for <see cref="Quiet"/>; null before then.
    /// Called every frame, so it allocates nothing while there is nothing to say.</summary>
    internal static string? ReportSettled(long now)
    {
        var st = _st;
        lock (st.Damaged)
            if (st.Damaged.Count == 0 || now - st.LastDamage < Quiet) return null;
        return Report();
    }

    /// <summary>Forget the seam and the tally.</summary>
    internal static void Reset() => _st = new();
}
