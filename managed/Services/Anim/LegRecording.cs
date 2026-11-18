using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Services.Anim;

/// <summary>One recorded frame: which body it is, and the <see cref="LegFrame"/> as handed to the leg core.</summary>
internal struct LegRec
{
    public int Body;
    public LegFrame Frame;
}

/// <summary>
/// The leg-recording file: a 16-byte header (the magic <c>CXLG</c>, the format version, the byte size of a
/// <see cref="LegRec"/> and of a <see cref="LegFrame"/>) and then <see cref="LegRec"/> records end to end, each a
/// raw dump of the struct on the machine that wrote it. A file whose sizes are not this build's was made by another
/// layout of the frame, and the reader refuses it rather than misread it. Little-endian, as every host CX runs on.
/// </summary>
internal static class LegRecording
{
    /// <summary>The format version: moves when the header or the record's meaning changes (not its size, which the header carries).</summary>
    internal const int Version = 1;
    /// <summary>The header's byte length.</summary>
    internal const int HeaderSize = 16;
    private static ReadOnlySpan<byte> Magic => "CXLG"u8;

    /// <summary>Writes the header to <paramref name="to"/>.</summary>
    internal static void WriteHeader(Stream to)
    {
        Span<byte> h = stackalloc byte[HeaderSize];
        Magic.CopyTo(h);
        BitConverter.TryWriteBytes(h[4..], Version);
        BitConverter.TryWriteBytes(h[8..], Unsafe.SizeOf<LegRec>());
        BitConverter.TryWriteBytes(h[12..], Unsafe.SizeOf<LegFrame>());
        to.Write(h);
    }

    /// <summary>The recording's frames grouped by body, in the order each body was first seen; null with
    /// <paramref name="refused"/> saying why when the file is not a recording this build can read. A partial last record
    /// (the client died mid-write) is dropped.</summary>
    internal static List<(int Body, List<LegFrame> Frames)>? Read(ReadOnlySpan<byte> file, out string refused)
    {
        refused = "";
        int recSize = Unsafe.SizeOf<LegRec>();
        if (file.Length < HeaderSize || !file[..4].SequenceEqual(Magic)) { refused = "not a leg recording"; return null; }
        int version = BitConverter.ToInt32(file[4..]), rec = BitConverter.ToInt32(file[8..]), frame = BitConverter.ToInt32(file[12..]);
        if (version != Version) { refused = $"format version {version}, this build reads {Version}"; return null; }
        if (rec != recSize || frame != Unsafe.SizeOf<LegFrame>())
        {
            refused = $"recorded with a frame of {frame} bytes, this build's is {Unsafe.SizeOf<LegFrame>()}";
            return null;
        }
        var bodies = new List<(int Body, List<LegFrame> Frames)>();
        var at = new Dictionary<int, int>();
        for (int off = HeaderSize; off + recSize <= file.Length; off += recSize)
        {
            LegRec r = MemoryMarshal.Read<LegRec>(file[off..]);
            if (!at.TryGetValue(r.Body, out int i)) { at[r.Body] = i = bodies.Count; bodies.Add((r.Body, new List<LegFrame>())); }
            bodies[i].Frames.Add(r.Frame);
        }
        return bodies;
    }
}
