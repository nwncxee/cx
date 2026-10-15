using System;
using System.Buffers.Binary;
using System.IO;
using CX.Framework;
using static CX.Alloy.TexLoadRead;

namespace CX.Alloy;

internal static unsafe partial class TexLoad
{
    /// <summary>Off-device, no engine: a real <c>BIFF V1</c>, a real <c>BIFF E1</c> (whose entries carry the
    /// compressed flag and the plain size), a real <c>HAK V1.0</c> and a loose file are written to a temp
    /// directory and read back through the location reader, which exercises both entry strides' arithmetic.
    /// Then the refusals -- an entry past the end, a missing file, a compressed entry -- each answer a reason.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        string dir = Path.Combine(Path.GetTempPath(), "cx54-" + Environment.ProcessId);
        bool ok = true;
        var h = new Handles();
        try
        {
            Directory.CreateDirectory(dir);
            byte[] payload = new byte[300];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i * 7 + 3);
            byte[] other = new byte[64];
            for (int i = 0; i < other.Length; i++) other[i] = (byte)(200 - i);

            // --- BIFF V1: 16-byte on-disk entries {id, offset, size, restype}, never compressed ---
            string bifV1 = Path.Combine(dir, "v1.bif");
            File.WriteAllBytes(bifV1, Biff("V1  ", BiffV1Stride, payload, other, out int v1Off, out _));
            ok &= Same(ReadCopy(h, Loc(KindBif, bifV1, v1Off, payload.Length, payload.Length, false), out _), payload);

            // --- BIFF E1: 0x18-byte on-disk entries, the last two dwords the compressed flag and plain size ---
            string bifE1 = Path.Combine(dir, "e1.bif");
            File.WriteAllBytes(bifE1, Biff("E1  ", BiffE1Stride, payload, other, out int e1Off, out int e1Off2));
            ok &= Same(ReadCopy(h, Loc(KindBif, bifE1, e1Off, payload.Length, payload.Length, false), out _), payload);
            ok &= Same(ReadCopy(h, Loc(KindBif, bifE1, e1Off2, other.Length, other.Length, false), out _), other);

            // --- HAK V1.0: 8-byte reslist entries, expanded to 0x10 in memory with the flag clear ---
            string hak = Path.Combine(dir, "t.hak");
            File.WriteAllBytes(hak, Hak(payload, other, out int hOff, out int hOff2));
            ok &= Same(ReadCopy(h, Loc(KindErf, hak, hOff, payload.Length, payload.Length, false), out _), payload);
            ok &= Same(ReadCopy(h, Loc(KindErf, hak, hOff2, other.Length, other.Length, false), out _), other);

            // --- loose file: the whole file, no offset and no stored size ---
            string loose = Path.Combine(dir, "t.dds");
            File.WriteAllBytes(loose, payload);
            ok &= Same(ReadCopy(h, Loc(KindLoose, loose, 0, -1, -1, false), out _), payload);

            // --- the refusals, each with a reason ---
            ok &= Refuses(h, Loc(KindBif, bifV1, v1Off, 1 << 20, 1 << 20, false));
            ok &= Refuses(h, Loc(KindBif, Path.Combine(dir, "absent.bif"), 0, 4, 4, false));
            ok &= Refuses(h, Loc(KindBif, bifE1, e1Off, payload.Length, payload.Length, true));
            ok &= Refuses(h, Loc(KindBif, bifV1, -1, 4, 4, false));
            ok &= Refuses(h, default);

            // --- and the whole seam: the bytes the reader returns parse as the DDS the engine would report ---
            byte[] dds = new byte[20 + 56];
            BinaryPrimitives.WriteUInt32LittleEndian(dds, 8);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4), 8);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(8), 3);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12), 32);
            BinaryPrimitives.WriteSingleLittleEndian(dds.AsSpan(16), 0.75f);
            for (int i = 20; i < dds.Length; i++) dds[i] = (byte)i;
            string ddsHak = Path.Combine(dir, "d.hak");
            File.WriteAllBytes(ddsHak, Hak(dds, other, out int dOff, out _));
            byte[]? got = ReadCopy(h, Loc(KindErf, ddsHak, dOff, dds.Length, dds.Length, false), out _);
            ok &= got != null && DdsReader.Parse(got, out DdsReader.Info info, out _)
                  && info.W == 8 && info.H == 8 && info.Channels == 3 && info.Level0 == 32 && info.Present == 4;

            // --- the TGA seam: a 2x1 BGR file out of a hak, decoded to RGB, tagged, and freed by the release ---
            byte[] tga = new byte[18 + 6];
            tga[2] = 2; tga[12] = 2; tga[14] = 1; tga[16] = 24;
            new byte[] { 1, 2, 3, 4, 5, 6 }.CopyTo(tga, 18);
            string tgaHak = Path.Combine(dir, "t2.hak");
            File.WriteAllBytes(tgaHak, Hak(tga, other, out int tOff, out _));
            byte[]? tgot = ReadCopy(h, Loc(KindErf, tgaHak, tOff, tga.Length, tga.Length, false), out _);
            RasterReader.Raster r = default;
            byte* px = tgot == null ? null : RasterReader.PrepareTga(tgot, out r, out _);
            ok &= px != null && r == new RasterReader.Raster(2, 1, 3)
                  && new ReadOnlySpan<byte>(px, 6).SequenceEqual(new byte[] { 3, 2, 1, 6, 5, 4 });
            if (px != null)
            {
                ok &= !FreeRaster(px, 2, 1, 3);   // untagged: the engine's release would see it
                *(short*)&px[r.Bytes + RasterTagAt] = RasterTag;
                ok &= FreeRaster(px, 2, 1, 3);
            }
        }
        catch (Exception e)
        {
            ok = false;
            log.Always($"TexLoad.SelfCheck: threw -- {e.Message}");
        }
        finally
        {
            h.Dispose();
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
        log.Info(ok ? "TexLoad.SelfCheck: passed" : "TexLoad.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>Fixture shapes: a <c>V1</c> BIFF's 16-byte on-disk entry, an <c>E1</c> one's 0x18 bytes with
    /// the plain size last, and the two ERF header fields <c>CExoEncapsulatedFile::LoadHeader</c> reads.</summary>
    private const int BiffV1Stride = 16, BiffE1Stride = 24, BiffE1Plain = 20, ErfCount = 16, ErfListOffset = 32;

    private static TexLocation Loc(byte kind, string path, long offset, int stored, int plain, bool compressed)
        => new(kind, 1, path, offset, stored, plain, compressed, DdsResType);

    private static bool Same(byte[]? got, byte[] want)
        => got != null && got.AsSpan().SequenceEqual(want);

    private static bool Refuses(Handles h, in TexLocation loc)
        => ReadCopy(h, loc, out string? why) == null && why != null;

    /// <summary>A BIFF file the client's <c>LoadHeader</c> would accept: the 0x34-byte header (magic, version,
    /// variable-resource count, then the table offset at +0x10), two payloads, and the entry table.</summary>
    private static byte[] Biff(string version, int stride, byte[] a, byte[] b, out int offA, out int offB)
    {
        const int header = 0x34;   // not-an-offset: the BIFF file header's own size
        offA = header;
        offB = offA + a.Length;
        int table = offB + b.Length;
        var f = new byte[table + 2 * stride];
        "BIFF"u8.CopyTo(f);
        System.Text.Encoding.ASCII.GetBytes(version).CopyTo(f, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(0x10), (uint)table);   // not-an-offset: a fixture field of the BIFF header
        a.CopyTo(f, offA);
        b.CopyTo(f, offB);
        for (int i = 0; i < 2; i++)
        {
            Span<byte> e = f.AsSpan(table + i * stride, stride);
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)(i == 0 ? offA : offB));
            BinaryPrimitives.WriteUInt32LittleEndian(e[8..], (uint)(i == 0 ? a.Length : b.Length));
            if (stride != BiffE1Stride) continue;
            BinaryPrimitives.WriteUInt32LittleEndian(e[BiffE1Plain..], (uint)(i == 0 ? a.Length : b.Length));
        }
        return f;
    }

    /// <summary>A <c>HAK V1.0</c> file: the 160-byte ERF header carries the entry count at +0x10 and the
    /// resource-list offset at +0x20, and each list entry is {offset, size}.</summary>
    private static byte[] Hak(byte[] a, byte[] b, out int offA, out int offB)
    {
        const int header = 160, entry = 8;
        int list = header;
        offA = list + 2 * entry;
        offB = offA + a.Length;
        var f = new byte[offB + b.Length];
        "HAK V1.0"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(ErfCount), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(f.AsSpan(ErfListOffset), (uint)list);
        a.CopyTo(f, offA);
        b.CopyTo(f, offB);
        for (int i = 0; i < 2; i++)
        {
            Span<byte> e = f.AsSpan(list + i * entry, entry);
            BinaryPrimitives.WriteUInt32LittleEndian(e, (uint)(i == 0 ? offA : offB));
            BinaryPrimitives.WriteUInt32LittleEndian(e[4..], (uint)(i == 0 ? a.Length : b.Length));
        }
        return f;
    }

    /// <summary>A copy of <see cref="Handles.Read"/>'s answer, which is only valid until the next read.</summary>
    private static byte[]? ReadCopy(Handles h, in TexLocation loc, out string? why)
        => h.Read(loc, out ReadOnlySpan<byte> b, out why) ? b.ToArray() : null;
}
