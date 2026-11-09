using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Fmt = CX.Alloy.DdsReader.Fmt;

namespace CX.Alloy;

/// <summary>CPU BC1/BC2/BC3/BC7 block decoders -- level 0 of a DDS out as the RGB/RGBA raster a TGA of the same
/// image would give, for the textures the engine forces uncompressed (cubes, <c>numx/numy</c> grids, controller
/// textures) and for the coverage scan of formats the engine cannot name. Rows stay in file order, as the
/// compressed upload would have them. Pure: <c>tools/rastercheck</c> compiles it alone.</summary>
internal static class BcnDecode
{
    /// <summary>Formats this decoder takes.</summary>
    internal static bool Decodes(Fmt f) => f is Fmt.Bc1 or Fmt.Bc2 or Fmt.Bc3 or Fmt.Bc7;

    /// <summary>Bytes per decoded pixel: BC1 comes out RGB (the engine never sees its alpha), the rest RGBA.</summary>
    internal static int PixelBytes(Fmt f) => f == Fmt.Bc1 ? 3 : 4;

    /// <summary>Level 0 of a <paramref name="f"/> image, <paramref name="w"/> x <paramref name="h"/>, from
    /// <paramref name="blocks"/> into <paramref name="dst"/> (<see cref="PixelBytes"/> per pixel).</summary>
    internal static void Unpack(ReadOnlySpan<byte> blocks, int w, int h, Fmt f, Span<byte> dst)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4, bytes = f == Fmt.Bc1 ? 8 : 16, px = PixelBytes(f);
        Span<Rgba32> tile = stackalloc Rgba32[16];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int block = by * bw + bx;
                var b = blocks.Slice(block * bytes, bytes);
                if (f == Fmt.Bc7) Bc7(b, tile); else S3tc(b, f, tile);
                for (int t = 0; t < 16; t++)
                {
                    int x = bx * 4 + (t & 3), y = by * 4 + (t >> 2);
                    if (x >= w || y >= h) continue;
                    int at = (y * w + x) * px;
                    Rgba32 c = tile[t];
                    dst[at] = c.R; dst[at + 1] = c.G; dst[at + 2] = c.B;
                    if (px == 4) dst[at + 3] = c.A;
                }
            }
    }

    /// <summary>One BC1/BC2/BC3 block to 16 RGBA texels, the S3TC spec's arithmetic in integers: 565 endpoints
    /// widened by bit replication, interpolants at (2a+b)/3 and (a+2b)/3. A BC1 block with c0 &lt;= c1 takes the
    /// half-way colour and black -- the engine uploads DXT1 as <c>COMPRESSED_RGB_S3TC_DXT1</c>, so there is no
    /// punch-through alpha; a BC2/BC3 colour block is always four-colour, and BC2's alpha nibble widens by 17.</summary>
    private static void S3tc(ReadOnlySpan<byte> b, Fmt f, Span<Rgba32> tile)
    {
        Span<Rgb24> pal = stackalloc Rgb24[4];
        Span<byte> alpha = stackalloc byte[8];
        var c = f == Fmt.Bc1 ? b : b[8..];
        int c0 = BinaryPrimitives.ReadUInt16LittleEndian(c), c1 = BinaryPrimitives.ReadUInt16LittleEndian(c[2..]);
        pal[0] = Expand(c0); pal[1] = Expand(c1);
        bool four = f != Fmt.Bc1 || c0 > c1;
        Rgb24 e0 = pal[0], e1 = pal[1];
        pal[2] = four ? new Rgb24(Third(2, e0.R, e1.R), Third(2, e0.G, e1.G), Third(2, e0.B, e1.B))
                      : new Rgb24(Half(e0.R, e1.R), Half(e0.G, e1.G), Half(e0.B, e1.B));
        pal[3] = four ? new Rgb24(Third(1, e0.R, e1.R), Third(1, e0.G, e1.G), Third(1, e0.B, e1.B))
                      : default;
        uint idx = BinaryPrimitives.ReadUInt32LittleEndian(c[4..]);
        ulong abits = 0;
        if (f == Fmt.Bc3)
        {
            int a0 = b[0], a1 = b[1];
            alpha[0] = (byte)a0; alpha[1] = (byte)a1;
            if (a0 > a1) for (int i = 2; i < 8; i++) alpha[i] = (byte)(((8 - i) * a0 + (i - 1) * a1) / 7);
            else { for (int i = 2; i < 6; i++) alpha[i] = (byte)(((6 - i) * a0 + (i - 1) * a1) / 5); alpha[6] = 0; alpha[7] = 255; }
            for (int i = 0; i < 6; i++) abits |= (ulong)b[2 + i] << (8 * i);
        }
        ulong a4 = f == Fmt.Bc2 ? BinaryPrimitives.ReadUInt64LittleEndian(b) : 0;
        for (int t = 0; t < 16; t++)
        {
            Rgb24 p = pal[(int)((idx >> (2 * t)) & 3)];
            byte a = f switch
            {
                Fmt.Bc3 => alpha[(int)((abits >> (3 * t)) & 7)],
                Fmt.Bc2 => (byte)(((a4 >> (4 * t)) & 15) * 17),
                _ => Rgba32.Opaque,
            };
            tile[t] = new Rgba32(p.R, p.G, p.B, a);
        }
    }

    /// <summary>The interpolant <paramref name="num"/> thirds of the way from <paramref name="e1"/> to
    /// <paramref name="e0"/>, in the spec's integer arithmetic.</summary>
    private static byte Third(int num, byte e0, byte e1) => (byte)((num * e0 + (3 - num) * e1) / 3);

    /// <summary>The half-way colour a three-colour BC1 block takes.</summary>
    private static byte Half(byte e0, byte e1) => (byte)((e0 + e1) / 2);

    /// <summary>An RGB565 endpoint to 8-bit R, G, B by bit replication.</summary>
    private static Rgb24 Expand(int c)
    {
        int r = c >> 11, g = (c >> 5) & 63, b = c & 31;
        return new Rgb24((byte)((r << 3) | (r >> 2)), (byte)((g << 2) | (g >> 4)), (byte)((b << 3) | (b >> 2)));
    }

    /// <summary>BC7 mode table, one row per mode: subsets, partition bits, rotation bits, index-selection bit,
    /// colour bits, alpha bits, per-endpoint p-bit, shared p-bit, primary index bits, secondary index bits.</summary>
    private static ReadOnlySpan<byte> Modes => [
        3, 4, 0, 0, 4, 0, 1, 0, 3, 0,
        2, 6, 0, 0, 6, 0, 0, 1, 3, 0,
        3, 6, 0, 0, 5, 0, 0, 0, 2, 0,
        2, 6, 0, 0, 7, 0, 1, 0, 2, 0,
        1, 0, 2, 1, 5, 6, 0, 0, 2, 3,
        1, 0, 2, 0, 7, 8, 0, 0, 2, 2,
        1, 0, 0, 0, 7, 7, 1, 0, 4, 0,
        2, 6, 0, 0, 5, 5, 1, 0, 2, 0,
    ];

    /// <summary>Two-subset partitions: bit t set = texel t is in subset 1.</summary>
    private static ReadOnlySpan<ushort> Part2 => [
        0xCCCC, 0x8888, 0xEEEE, 0xECC8, 0xC880, 0xFEEC, 0xFEC8, 0xEC80, 0xC800, 0xFFEC, 0xFE80, 0xE800, 0xFFE8, 0xFF00, 0xFFF0, 0xF000,
        0xF710, 0x008E, 0x7100, 0x08CE, 0x008C, 0x7310, 0x3100, 0x8CCE, 0x088C, 0x3110, 0x6666, 0x366C, 0x17E8, 0x0FF0, 0x718E, 0x399C,
        0xAAAA, 0xF0F0, 0x5A5A, 0x33CC, 0x3C3C, 0x55AA, 0x9696, 0xA55A, 0x73CE, 0x13C8, 0x324C, 0x3BDC, 0x6996, 0xC33C, 0x9966, 0x0660,
        0x0272, 0x04E4, 0x4E40, 0x2720, 0xC936, 0x936C, 0x39C6, 0x639C, 0x9336, 0x9CC6, 0x817E, 0xE718, 0xCCF0, 0x0FCC, 0x7744, 0xEE22,
    ];

    /// <summary>Three-subset partitions: two bits per texel, texel t at bits 2t..2t+1.</summary>
    private static ReadOnlySpan<uint> Part3 => [
        0xAA685050, 0x6A5A5040, 0x5A5A4200, 0x5450A0A8, 0xA5A50000, 0xA0A05050, 0x5555A0A0, 0x5A5A5050,
        0xAA550000, 0xAA555500, 0xAAAA5500, 0x90909090, 0x94949494, 0xA4A4A4A4, 0xA9A59450, 0x2A0A4250,
        0xA5945040, 0x0A425054, 0xA5A5A500, 0x55A0A0A0, 0xA8A85454, 0x6A6A4040, 0xA4A45000, 0x1A1A0500,
        0x0050A4A4, 0xAAA59090, 0x14696914, 0x69691400, 0xA08585A0, 0xAA821414, 0x50A4A450, 0x6A5A0200,
        0xA9A58000, 0x5090A0A8, 0xA8A09050, 0x24242424, 0x00AA5500, 0x24924924, 0x24499224, 0x50A50A50,
        0x500AA550, 0xAAAA4444, 0x66660000, 0xA5A0A5A0, 0x50A050A0, 0x69286928, 0x44AAAA44, 0x66666600,
        0xAA444444, 0x54A854A8, 0x95809580, 0x96969600, 0xA85454A8, 0x80959580, 0xAA141414, 0x96960000,
        0xAAAA1414, 0xA05050A0, 0xA0A5A5A0, 0x96000000, 0x40804080, 0xA9A8A9A8, 0xAAAAAA44, 0x2A4A5254,
    ];

    /// <summary>Anchor texel of subset 1 in a two-subset partition.</summary>
    private static ReadOnlySpan<byte> Anchor2 => [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15,
    ];

    /// <summary>Anchor texels of subsets 1 and 2 in a three-subset partition.</summary>
    private static ReadOnlySpan<byte> Anchor3a => [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
        3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
        3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3,
    ];
    private static ReadOnlySpan<byte> Anchor3b => [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
        15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
        15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8,
    ];

    private static ReadOnlySpan<byte> Weights2 => [0, 21, 43, 64];
    private static ReadOnlySpan<byte> Weights3 => [0, 9, 18, 27, 37, 46, 55, 64];
    private static ReadOnlySpan<byte> Weights4 => [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    private static int Weight(int bits, int i) => bits switch { 2 => Weights2[i], 3 => Weights3[i], _ => Weights4[i] };

    /// <summary>Read <paramref name="n"/> bits LSB-first from the 128-bit block at <paramref name="pos"/>.</summary>
    private static int Bits(UInt128 v, ref int pos, int n)
    {
        int r = (int)(uint)((v >> pos) & (UInt128)((1u << n) - 1));
        pos += n;
        return r;
    }

    /// <summary>Which subset texel <paramref name="t"/> belongs to.</summary>
    private static int Subset(int ns, int part, int t)
        => ns == 1 ? 0 : ns == 2 ? (Part2[part] >> t) & 1 : (int)(Part3[part] >> (2 * t)) & 3;

    /// <summary>The texel whose index is one bit short: texel 0 for subset 0, the anchor tables for the rest.</summary>
    private static bool IsAnchor(int ns, int part, int t)
        => t == 0 || (ns == 2 && t == Anchor2[part]) || (ns == 3 && (t == Anchor3a[part] || t == Anchor3b[part]));

    /// <summary>One BC7 block to 16 RGBA texels, per the BPTC spec (GL 4.6 appendix C.2): eight modes, the 64
    /// two- and three-subset partitions and their anchors, 6-bit weights and
    /// <c>(e0*(64-w) + e1*w + 32) &gt;&gt; 6</c>. A reserved mode-8 block decodes to 0,0,0,0, not opaque black.</summary>
    private static void Bc7(ReadOnlySpan<byte> b, Span<Rgba32> tile)
    {
        var v = BinaryPrimitives.ReadUInt128LittleEndian(b);
        int mode = 0;
        while (mode < 8 && ((b[0] >> mode) & 1) == 0) mode++;
        if (mode == 8) { tile.Clear(); return; }
        var m = Modes.Slice(mode * 10, 10);
        int ns = m[0], cb = m[4], ab = m[5], ib = m[8], ib2 = m[9];
        int pos = mode + 1;
        int part = Bits(v, ref pos, m[1]), rot = Bits(v, ref pos, m[2]), isel = Bits(v, ref pos, m[3]);
        // Channel-major on the wire: every endpoint's red, then every green, then every blue, then alpha.
        Span<Endpoint> ep = stackalloc Endpoint[6];
        int ends = ns * 2;
        for (int e = 0; e < ends; e++) ep[e].R = Bits(v, ref pos, cb);
        for (int e = 0; e < ends; e++) ep[e].G = Bits(v, ref pos, cb);
        for (int e = 0; e < ends; e++) ep[e].B = Bits(v, ref pos, cb);
        for (int e = 0; e < ends; e++) ep[e].A = ab > 0 ? Bits(v, ref pos, ab) : Rgba32.Opaque;
        int cbits = cb, abits = ab;
        if (m[6] != 0 || m[7] != 0)
        {
            Span<int> p = stackalloc int[6];
            if (m[6] != 0) for (int e = 0; e < ends; e++) p[e] = Bits(v, ref pos, 1);
            else for (int sub = 0; sub < ns; sub++) { int at = sub * 2; p[at] = p[at + 1] = Bits(v, ref pos, 1); }
            for (int e = 0; e < ends; e++)
            {
                ref Endpoint q = ref ep[e];
                q.R = (q.R << 1) | p[e]; q.G = (q.G << 1) | p[e]; q.B = (q.B << 1) | p[e];
                if (ab > 0) q.A = (q.A << 1) | p[e];
            }
            cbits++; if (ab > 0) abits++;
        }
        for (int e = 0; e < ends; e++)
        {
            ref Endpoint q = ref ep[e];
            q.R = Widen(q.R, cbits); q.G = Widen(q.G, cbits); q.B = Widen(q.B, cbits);
            if (ab > 0) q.A = Widen(q.A, abits);
        }
        Span<byte> i1 = stackalloc byte[16], i2 = stackalloc byte[16];
        for (int t = 0; t < 16; t++) i1[t] = (byte)Bits(v, ref pos, IsAnchor(ns, part, t) ? ib - 1 : ib);
        if (ib2 > 0) for (int t = 0; t < 16; t++) i2[t] = (byte)Bits(v, ref pos, t == 0 ? ib2 - 1 : ib2);
        for (int t = 0; t < 16; t++)
        {
            int s = Subset(ns, part, t);
            int cw, aw;
            if (ib2 == 0) cw = aw = Weight(ib, i1[t]);
            else if (isel == 0) { cw = Weight(ib, i1[t]); aw = Weight(ib2, i2[t]); }
            else { cw = Weight(ib2, i2[t]); aw = Weight(ib, i1[t]); }
            int at = s * 2;
            Endpoint lo = ep[at], hi = ep[at + 1];
            var d = new Rgba32(Blend(lo.R, hi.R, cw), Blend(lo.G, hi.G, cw), Blend(lo.B, hi.B, cw), Blend(lo.A, hi.A, aw));
            // A rotation swaps alpha with the named channel, so the pair that was coded together decodes together.
            tile[t] = rot switch
            {
                1 => new Rgba32(d.A, d.G, d.B, d.R),
                2 => new Rgba32(d.R, d.A, d.B, d.G),
                3 => new Rgba32(d.R, d.G, d.A, d.B),
                _ => d,
            };
        }
    }

    /// <summary>One channel of a subset's pair at weight <paramref name="w"/>, in the spec's 6-bit
    /// fixed point: <c>(e0*(64-w) + e1*w + 32) &gt;&gt; 6</c>.</summary>
    private static byte Blend(int e0, int e1, int w) => (byte)((e0 * (64 - w) + e1 * w + 32) >> 6);

    /// <summary>One BC7 endpoint, a channel per field, at the block's own precision until
    /// <see cref="Widen"/> raises each to 8 bits.</summary>
    private struct Endpoint
    {
        internal int R, G, B, A;
    }

    /// <summary>An <paramref name="bits"/>-bit endpoint to 8 bits by bit replication.</summary>
    private static int Widen(int x, int bits) => (x << (8 - bits)) | (x >> (2 * bits - 8));
}
