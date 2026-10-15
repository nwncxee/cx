using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>The pure half of <see cref="RasterReader"/>: resource bytes in, the engine's pixels out. No engine
/// reads, so <c>tools/rastercheck</c> compiles this file alone and runs it over real files.</summary>
public sealed unsafe partial class RasterReader
{
    /// <summary>The 18-byte TGA file header, little-endian as the file is.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct TgaHeader
    {
        /// <summary>Bytes of image id between the header and the colour map.</summary>
        public byte IdLength;
        /// <summary>1 when a colour map follows.</summary>
        public byte ColorMapType;
        /// <summary>The image type: 1/9 colour-mapped, 2/10 true colour, 3/11 greyscale; 9, 10 and 11 are RLE.</summary>
        public byte ImageType;
        /// <summary>Index of the first colour-map entry.</summary>
        public ushort ColorMapFirst;
        /// <summary>Entries in the colour map.</summary>
        public ushort ColorMapLength;
        /// <summary>Bits per colour-map entry.</summary>
        public byte ColorMapBits;
        /// <summary>Image origin, unused.</summary>
        public ushort XOrigin, YOrigin;
        /// <summary>Image size in pixels.</summary>
        public ushort Width, Height;
        /// <summary>Bits per pixel.</summary>
        public byte PixelBits;
        /// <summary>Descriptor bits, unused.</summary>
        public byte Descriptor;
    }
    /// <summary><c>ReadUnmappedRLETGA</c>'s refusal: more decoded bytes than this and the demand fails.</summary>
    private const uint TgaRleMax = 0x0fffffff;   // not-an-offset: a size cap
    /// <summary>One PLT layer's palette row: 256 four-byte entries (<c>ImageDecode</c>'s <c>row &lt;&lt; 10</c>).</summary>
    private const int PltRowBytes = 1024;
    /// <summary><c>ImageDecode</c>'s stack cache holds 15 rows (0x3c48 bytes on Linux, 15360 on Windows); more overrun it.</summary>
    private const int PltMaxLayers = 15;

    internal enum Verdict { Pixels, Rejects, Unchecked }

    internal struct Tga
    {
        public int W, H, Type;
        /// <summary>Bytes per pixel out: the header's bits over 8, as the engine's stride; for a colour-mapped file,
        /// the map entry's bytes (its indices are expanded, which the engine does not do).</summary>
        public int Px;
        /// <summary>Where the pixel, index or RLE packet stream starts: after the header, id field and colour map.</summary>
        public int Offset;
        /// <summary>A colour-mapped file's map: entry bytes (0 = true-colour), first index, entry count, where it starts.</summary>
        public int Cmap, CmapFirst, CmapLen, CmapAt;
        /// <summary>The pixels' byte size; fits an int whenever the verdict is <see cref="Verdict.Pixels"/>.</summary>
        public int Bytes => W * H * Px;
        /// <summary>A file the engine does not decode faithfully and Alloy does: colour-mapped (indices uploaded as
        /// greyscale) or greyscale RLE (type 11, refused outright). Never compared; decoded only when owning.</summary>
        public bool Beyond => Cmap != 0 || Type == 11;
        /// <summary>RLE packets rather than a verbatim pixel run.</summary>
        public bool Rle => Type == 10 || Type == 11;
    }

    /// <summary>What <c>OnResourceServiced</c> will make of these bytes: pixels this decoder reproduces, a refusal
    /// (it returns 0), or a decode that touches memory past the resource, which is not reproduced. Colour-mapped
    /// files (types 1 and 9) and greyscale RLE (type 11) are the exception -- the engine uploads the indices as
    /// greyscale and refuses type 11 outright -- so Alloy decodes both properly and never compares them.</summary>
    internal static Verdict ParseTga(ReadOnlySpan<byte> raw, out Tga t, out string? why)
    {
        t = default; why = null;
        if (raw.Length < sizeof(TgaHeader)) { why = $"{raw.Length} bytes, shorter than the header"; return Verdict.Unchecked; }
        TgaHeader h = MemoryMarshal.Read<TgaHeader>(raw);
        t.Type = h.ImageType;
        t.W = h.Width;
        t.H = h.Height;
        t.Px = h.PixelBits >> 3;
        t.Offset = sizeof(TgaHeader) + h.IdLength + h.ColorMapLength * (h.ColorMapBits >> 3);
        long bytes = (long)t.W * t.H * t.Px;
        switch (t.Type)
        {
            case 1: case 9:
                int cbits = h.ColorMapBits;
                if (h.ColorMapType != 1 || h.PixelBits != 8 || (cbits != 24 && cbits != 32))
                { why = $"colour map type {h.ColorMapType}, {cbits}-bit entries, {h.PixelBits}-bit indices"; return Verdict.Unchecked; }
                t.Cmap = t.Px = cbits >> 3;
                t.CmapFirst = h.ColorMapFirst;
                t.CmapLen = h.ColorMapLength;
                t.CmapAt = sizeof(TgaHeader) + h.IdLength;
                bytes = (long)t.W * t.H * t.Px;
                if (bytes > TgaRleMax) { why = $"colour-mapped, decodes to {bytes} bytes"; return Verdict.Unchecked; }
                if (raw.Length < t.Offset || !MappedFits(raw[t.Offset..], t.W * t.H, t.Type == 9, t.CmapFirst, t.CmapLen))
                { why = "an index outside the colour map, or the stream ends before the pixels do"; return Verdict.Unchecked; }
                return Verdict.Pixels;
            case 2: case 3:
                if (t.Type != 3 && t.Px < 3) { why = $"type {t.Type} at {t.Px} byte(s)/pixel: the engine's R/B swap reaches past each pixel"; return Verdict.Unchecked; }
                if (t.Px == 0) { why = "0 bytes/pixel"; return Verdict.Unchecked; }
                if (raw.Length < t.Offset + bytes) { why = $"{raw.Length} bytes, the pixels end at {t.Offset + bytes}"; return Verdict.Unchecked; }
                return Verdict.Pixels;
            case 10:
                if (t.Px != 3 && t.Px != 4) { why = $"RLE at {t.Px} byte(s)/pixel"; return Verdict.Rejects; }
                if (bytes > uint.MaxValue) { why = $"RLE size {bytes} wraps the engine's 32-bit product"; return Verdict.Unchecked; }
                if (bytes > TgaRleMax) { why = $"RLE decodes to {bytes} bytes"; return Verdict.Rejects; }
                if (raw.Length < t.Offset || !RleFits(raw[t.Offset..], (int)bytes, t.Px)) { why = "the RLE stream ends before the pixels do"; return Verdict.Unchecked; }
                return Verdict.Pixels;
            case 11:
                if (t.Px != 1) { why = $"greyscale RLE at {t.Px} byte(s)/pixel"; return Verdict.Rejects; }
                if (bytes > TgaRleMax) { why = $"greyscale RLE decodes to {bytes} bytes"; return Verdict.Rejects; }
                if (raw.Length < t.Offset || !RleFits(raw[t.Offset..], (int)bytes, 1)) { why = "the RLE stream ends before the pixels do"; return Verdict.Unchecked; }
                return Verdict.Pixels;
            default:
                why = $"image type {t.Type}"; return Verdict.Rejects;
        }
    }

    /// <summary>Whether the RLE packets in <paramref name="src"/> fill <paramref name="n"/> bytes before the source
    /// ends; where they do not, the engine reads on past the resource.</summary>
    private static bool RleFits(ReadOnlySpan<byte> src, int n, int px)
    {
        int s = 0;
        for (int d = 0; d < n; )
        {
            if (s >= src.Length) return false;
            int head = src[s], count = (head & 0x7f) + 1;
            s += 1 + ((head & 0x80) != 0 ? px : count * px);
            if (s > src.Length) return false;
            d += count * px;
        }
        return true;
    }

    /// <summary>Whether <paramref name="n"/> indices (RLE packets if <paramref name="rle"/>) fit in <paramref name="src"/>
    /// and every one that lands in the pixels names an entry of the map.</summary>
    private static bool MappedFits(ReadOnlySpan<byte> src, int n, bool rle, int first, int len)
    {
        int s = 0;
        for (int d = 0; d < n; )
        {
            int count = n - d;
            bool run = false;
            if (rle)
            {
                if (s >= src.Length) return false;
                int head = src[s++];
                count = (head & 0x7f) + 1; run = (head & 0x80) != 0;
            }
            int used = run ? 1 : count;
            if (s + used > src.Length) return false;
            for (int k = 0; k < count && d < n; k++, d++)
            {
                int idx = src[run ? s : s + k] - first;
                if ((uint)idx >= (uint)len) return false;
            }
            s += used;
        }
        return true;
    }

    /// <summary>A <see cref="MappedFits"/> index stream into map entries, as the file stores them (BGR/BGRA).</summary>
    private static void ExpandMapped(ReadOnlySpan<byte> raw, in Tga t, Span<byte> dst)
    {
        var map = raw.Slice(t.CmapAt, t.CmapLen * t.Cmap);
        var src = raw[t.Offset..];
        int px = t.Cmap, s = 0, n = t.W * t.H;
        for (int p = 0, d = 0; p < n; )
        {
            int count = n - p;
            bool run = false;
            if (t.Type == 9) { int head = src[s++]; count = (head & 0x7f) + 1; run = (head & 0x80) != 0; }
            for (int k = 0; k < count && p < n; k++, p++, d += px)
                map.Slice((src[run ? s : s + k] - t.CmapFirst) * px, px).CopyTo(dst.Slice(d, px));
            s += run ? 1 : count;
        }
    }

    /// <summary>The engine's pixels for a <see cref="Verdict.Pixels"/> TGA into <paramref name="dst"/>
    /// (<see cref="Tga.Bytes"/> long), which may be the file's own pixels (the engine's in-place decode), quirks
    /// and all: the descriptor's top-origin bit is never honoured, RLE packets cross rows and are clipped at the
    /// last pixel, and every type but 3 and 11 swaps bytes 0 and 2 at the pixel stride.</summary>
    internal static void DecodeTga(ReadOnlySpan<byte> raw, in Tga t, Span<byte> dst)
    {
        int n = t.Bytes, px = t.Px;
        if (t.Cmap != 0) ExpandMapped(raw, t, dst);
        else if (!t.Rle)
        {
            var src = raw.Slice(t.Offset, n);
            if (!Unsafe.AreSame(ref MemoryMarshal.GetReference(src), ref MemoryMarshal.GetReference(dst))) src.CopyTo(dst);
        }
        else
        {
            var src = raw[t.Offset..];
            int s = 0, d = 0;
            while (d < n)
            {
                int head = src[s], run = ((head & 0x7f) + 1) * px;
                if ((head & 0x80) != 0)
                {
                    var pixel = src.Slice(s + 1, px);
                    for (int k = 0; k < run && d < n; k += px, d += px) pixel.CopyTo(dst.Slice(d, px));
                    s += 1 + px;
                }
                else
                {
                    int c = Math.Min(run, n - d);
                    src.Slice(s + 1, c).CopyTo(dst.Slice(d, c));
                    d += c; s += 1 + run;
                }
            }
        }
        if (t.Type != 3 && t.Type != 11)
            for (int i = 0; i < n; i += px) (dst[i], dst[i + 2]) = (dst[i + 2], dst[i]);
    }

    /// <summary>The first of the <paramref name="px"/> pairs whose layer has no cached row (the engine reads stale
    /// stack there), or -1.</summary>
    internal static int PltStrays(ReadOnlySpan<byte> px, int layers)
    {
        for (int i = 1; i < px.Length; i += 2)
            if (px[i] >= layers) return i >> 1;
        return -1;
    }

    /// <summary><c>ImageDecode</c>'s composite into <paramref name="dst"/> (one entry per pixel), for pixels
    /// <see cref="PltStrays"/> passed.</summary>
    internal static void DecodePlt(ReadOnlySpan<byte> px, ReadOnlySpan<byte> palette, ReadOnlySpan<int> rows, Span<uint> dst)
    {
        for (int i = 0; i < dst.Length; i++)
            dst[i] = BinaryPrimitives.ReadUInt32LittleEndian(palette[((rows[px[2 * i + 1]] << 10) + px[2 * i] * 4)..]);
    }
}
