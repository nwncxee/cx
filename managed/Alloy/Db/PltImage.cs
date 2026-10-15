using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace CX.Alloy.Db;

/// <summary>The PLT file decoded to the two channels the shader resolves from. A PLT pixel is (intensity,
/// layer); vanilla looks each texel up in the master palette (<c>ImageDecode</c> @0x4b6580), while Alloy
/// uploads the pairs AS an RG8 texture and resolves in the fragment shader, so the file loads once however
/// many creatures wear it. Engine-free, so <c>tools/pltcheck</c> runs it off-device over the base BIFs.</summary>
internal static unsafe class PltImage
{
    /// <summary>Bytes per PLT pixel on the GPU: R = intensity, G = layer, as RG8.</summary>
    internal const int BytesPerPixel = 2;

    /// <summary>One PLT texel as the chain stores it: intensity, then layer.</summary>
    internal struct Texel { public byte Intensity, Layer; }
    internal const int HeaderBytes = 24;
    /// <summary><c>'PLT '</c>, the dword <c>OnResourceServiced</c> compares. The shipped files carry
    /// <c>"PLT V1  "</c>; the engine never looks past the first four bytes.</summary>
    internal const uint Magic = 0x20544c50;   // not-an-offset: the file's magic dword
    /// <summary>Header fields of <c>CResPLT::OnResourceServiced</c> @0x1136640: layer count, compression
    /// flag, width, height. Then <c>width*height</c> (intensity, layer) byte pairs, no padding and no
    /// vertical flip; a non-zero flag means they are RLE-packed.</summary>
    internal const int OffLayers = 8, OffCompressed = 12, OffWidth = 16, OffHeight = 20;
    /// <summary>What the engine itself refuses past (<c>LoadTexture</c>: side &lt; 0x2001, layers &lt;
    /// 0x21).</summary>
    internal const int MaxSide = 0x2000, MaxFileLayers = 0x20;   // not-an-offset: the engine's own limits, LoadTexture @0x1134930
    /// <summary>Layers Alloy's per-instance row block carries (<see cref="SceneDb.PltLayers"/>). Not a
    /// budget: <c>CNWCAnimBaseParts::ReplaceTexture</c> @0x9d8c80 takes the PLT branch <b>only</b> when the
    /// count is exactly 10 and otherwise drops the call, so ten is all the game layer can ask for. A file
    /// declaring more is loaded and counted, and its upper layers resolve against layer 9's row.</summary>
    internal const int Layers = SceneDb.PltLayers;

    /// <summary>PLT files seen declaring more than <see cref="Layers"/> layers, and files with a pixel that
    /// actually NAMES one past the last row the per-instance block carries. The second number is the one that
    /// matters: it is how often the clamp in <c>cx_plt.glsl</c> changes a colour. Reported by
    /// <see cref="Note"/>; <c>tools/pltcheck</c> says both are 0 across every shipped PLT.</summary>
    internal static long FilesOverLayers, PixelsOverLayers;

    /// <summary>The <c>[alloy]</c> clause, empty while the ten-layer block has cost nothing.</summary>
    internal static string Note()
        => FilesOverLayers + PixelsOverLayers == 0 ? ""
           : $"plt: {FilesOverLayers} file(s) declare more than {Layers} layers"
             + (PixelsOverLayers == 0 ? " (no pixel uses one)"
                                      : $", {PixelsOverLayers} with a pixel past layer {Layers - 1} -- CLAMPED");

    /// <summary>Bytes of an RG8 mip chain of <paramref name="levels"/> levels from
    /// <paramref name="w"/>x<paramref name="h"/>.</summary>
    internal static long ChainBytes(int w, int h, int levels)
    {
        long n = 0;
        for (int l = 0; l < levels; l++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
            n += (long)w * h * BytesPerPixel;
        return n;
    }

    /// <summary>Parse the header. False names why; nothing is allocated.</summary>
    internal static bool Parse(ReadOnlySpan<byte> raw, out int w, out int h, out int layers, out bool rle, out string? why)
    {
        w = h = layers = 0; rle = false; why = null;
        if (raw.Length < HeaderBytes) { why = $"{raw.Length} bytes, shorter than the 24-byte header"; return false; }
        if (BinaryPrimitives.ReadUInt32LittleEndian(raw) != Magic) { why = "the first dword is not 'PLT '"; return false; }
        layers = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[OffLayers..]);
        rle = BinaryPrimitives.ReadUInt32LittleEndian(raw[OffCompressed..]) != 0;
        w = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[OffWidth..]);
        h = (int)BinaryPrimitives.ReadUInt32LittleEndian(raw[OffHeight..]);
        if (w <= 0 || h <= 0 || w > MaxSide || h > MaxSide) { why = $"{w}x{h} is outside the engine's 1..{MaxSide}"; return false; }
        if (layers <= 0 || layers > MaxFileLayers) { why = $"{layers} layers, outside the engine's 1..{MaxFileLayers}"; return false; }
        long want = (long)w * h * BytesPerPixel;
        if (!rle && raw.Length - HeaderBytes < want)
        { why = $"{raw.Length - HeaderBytes} bytes of pairs for a {w}x{h} image, which needs {want}"; return false; }
        return true;
    }

    /// <summary>The file as an RG8 mip chain in one native buffer, levels packed base-first. Null with
    /// <paramref name="why"/> set on any refusal. Worker thread; the caller frees the buffer with
    /// <see cref="NativeMemory.Free"/>.</summary>
    internal static byte* Decode(ReadOnlySpan<byte> raw, out int w, out int h, out int levels, out string? why)
    {
        levels = 0;
        if (!Parse(raw, out w, out h, out int layers, out bool rle, out why)) return null;
        if (layers > Layers) FilesOverLayers++;
        levels = TexRegistry.Levels(w, h);
        long bytes = ChainBytes(w, h, levels);
        byte* dst = (byte*)NativeMemory.Alloc((nuint)bytes);
        long want = (long)w * h * BytesPerPixel;
        if (rle)
        {
            if (!Unrle(raw[HeaderBytes..], new Span<byte>(dst, (int)want), out why))
            { NativeMemory.Free(dst); return null; }
        }
        else raw.Slice(HeaderBytes, (int)want).CopyTo(new Span<byte>(dst, (int)want));
        int top = 0;
        for (long i = 1; i < want; i += 2) if (dst[i] > top) top = dst[i];
        if (top >= Layers) PixelsOverLayers++;
        BuildMips(dst, w, h, levels);
        return dst;
    }

    /// <summary><c>CResPLT::ReadRLEPLT</c> @0x1136560, byte for byte: a control byte whose low seven bits are
    /// <c>count - 1</c>; its top bit set means one byte repeated, clear means <c>count</c> literal bytes. The
    /// stream is the interleaved pair stream, so a run may straddle a pixel -- which is why this decodes bytes
    /// and not pixels.</summary>
    internal static bool Unrle(ReadOnlySpan<byte> src, Span<byte> dst, out string? why)
    {
        why = null;
        int s = 0, d = 0;
        while (d < dst.Length)
        {
            if (s >= src.Length) { why = $"the RLE stream ends after {d} of {dst.Length} bytes"; return false; }
            byte ctl = src[s++];
            int count = (ctl & 0x7f) + 1;
            if (count > dst.Length - d) count = dst.Length - d;
            if ((ctl & 0x80) != 0)
            {
                if (s >= src.Length) { why = "the RLE stream ends on a run byte"; return false; }
                dst.Slice(d, count).Fill(src[s++]);
            }
            else
            {
                if (s + count > src.Length) { why = $"an RLE literal run of {count} overruns the stream"; return false; }
                src.Slice(s, count).CopyTo(dst.Slice(d, count));
                s += count;
            }
            d += count;
        }
        return true;
    }

    /// <summary>The PLT mip chain, built once at load: the layer is the MAJORITY of the four children and the
    /// intensity the mean over those carrying it. Averaging a layer index is meaningless, so a PLT samples
    /// NEAREST and the bilinear happens in <c>cx_plt.glsl</c> over RESOLVED colours. A 2-2 tie goes by the
    /// destination texel's parity; taking the lower index walked <c>pmh0_chest004</c>'s metal 63% -> 79%.</summary>
    internal static void BuildMips(byte* chain, int w, int h, int levels)
    {
        var px = (Texel*)chain;
        long srcOff = 0;
        for (int l = 1; l < levels; l++)
        {
            int sw = w, sh = h;
            w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1);
            long dstOff = srcOff + (long)sw * sh;
            for (int y = 0; y < h; y++)
            {
                int y0 = y * 2, y1 = Math.Min(y0 + 1, sh - 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * 2, x1 = Math.Min(x0 + 1, sw - 1);
                    Reduce(px, srcOff, sw, x0, y0, x1, y1, (x ^ y) & 1, out byte inten, out byte layer);
                    px[dstOff + (long)y * w + x] = new Texel { Intensity = inten, Layer = layer };
                }
            }
            srcOff = dstOff;
        }
    }

    /// <summary>One 2x2 reduction: majority layer, mean intensity over the children holding it. A tie is
    /// decided by <paramref name="parity"/> -- the destination texel's <c>(x ^ y) &amp; 1</c> -- which takes
    /// the lowest tied layer on one texel and the next one along on its neighbour, so neither layer grows
    /// at the other's expense down the chain.</summary>
    private static void Reduce(Texel* s, long at, int sw, int x0, int y0, int x1, int y1, int parity, out byte inten, out byte layer)
    {
        Texel a = s[at + (long)y0 * sw + x0];
        Texel b = s[at + (long)y0 * sw + x1];
        Texel c = s[at + (long)y1 * sw + x0];
        Texel e = s[at + (long)y1 * sw + x1];
        int best = a.Layer, bestN = 0;
        for (int k = 0; k < 4; k++)
        {
            int cand = (k == 0 ? a : k == 1 ? b : k == 2 ? c : e).Layer;
            int n = (a.Layer == cand ? 1 : 0) + (b.Layer == cand ? 1 : 0) + (c.Layer == cand ? 1 : 0) + (e.Layer == cand ? 1 : 0);
            if (n > bestN) { best = cand; bestN = n; }
            else if (n == bestN && cand != best)
            {
                if (cand < best ? parity == 0 : parity != 0) best = cand;
            }
        }
        int sum = 0, cnt = 0;
        if (a.Layer == best) { sum += a.Intensity; cnt++; }
        if (b.Layer == best) { sum += b.Intensity; cnt++; }
        if (c.Layer == best) { sum += c.Intensity; cnt++; }
        if (e.Layer == best) { sum += e.Intensity; cnt++; }
        inten = (byte)((sum + (cnt >> 1)) / cnt);
        layer = (byte)best;
    }

    /// <summary>The shader's resolve, on the CPU: <c>colour = master[row[layer]][intensity]</c>, which is
    /// <c>ImageDecode</c>'s <c>palette[(rows[layer] &lt;&lt; 10) + intensity*4]</c> one lookup at a time.
    /// Returns RGBA8 packed little-endian, exactly as <c>RasterReader.DecodePlt</c> reads it.</summary>
    internal static uint Resolve(ReadOnlySpan<byte> master, ReadOnlySpan<ushort> rows, byte intensity, byte layer)
    {
        int l = layer < rows.Length ? layer : rows.Length - 1;
        int at = (rows[l] << 10) + intensity * 4;
        return (uint)at + 4 <= (uint)master.Length ? BinaryPrimitives.ReadUInt32LittleEndian(master[at..]) : 0;
    }
}
