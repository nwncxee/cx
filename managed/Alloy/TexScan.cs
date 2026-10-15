using System;
using System.Buffers;
using System.Numerics;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// What the texture consumers need, computed from the engine's decoded image at the upload funnel where it is
/// still in memory: the cutout hole fraction the caster pass masks on, the mean colour the fog converges on, and
/// the RGBA copy the BCn transcode encodes from. No <c>glGetTexImage</c> on the owned path: on radeonsi a readback
/// of a texture the engine's context also draws with re-lays it out and the driver reports a GPU reset.
/// </summary>
internal static unsafe class TexScan
{
    /// <summary>The engine's own format indices into its format translation table; it never uploads DXT3. An
    /// uncompressed sheet is cut into cells, compressed data is one image.</summary>
    private const int FmtDxt1 = 7, FmtDxt5 = 8;
    /// <summary>The last engine format code the compressed branch did not recognise; for the funnel diagnostic.</summary>
    internal static int LastRefusedFormat = -1;

    internal struct Result
    {
        /// <summary>Fraction of texels whose alpha falls under the cutout threshold; 0 = a slab.</summary>
        public float Holes;
        /// <summary>Fraction of texels whose alpha is neither under the cutout threshold nor opaque (&gt;= 248):
        /// the part of the image a 0.2 alpha test turns into solid that vanilla's blend keeps soft.</summary>
        public float Graded;
        /// <summary>Mean colour, 0..1 in the texture's own (sRGB) encoding; valid when <see cref="MeanOk"/>.</summary>
        public Vector3 Mean;
        public bool MeanOk;
        /// <summary>The cell as RGBA8, top row first as the engine uploads it; null when not asked for or the image
        /// is not 3- or 4-byte uncompressed. Rented from ArrayPool with room for the whole mip chain
        /// (<see cref="ChainBytes"/>), so the transcode builds its mips in place; W*H*4 bytes are valid. Whoever
        /// drops it returns it.</summary>
        public byte[]? Rgba;
        public int W, H;
        /// <summary>The engine asked for a mip chain on this texture.</summary>
        public bool Mips;
    }

    /// <summary>What the ENGINE says this texture is, straight off its own virtuals -- the counterpart to
    /// <see cref="TexProbe.TexFormat"/>, which says what GL holds. When the two disagree the upload is the
    /// suspect; when they agree the resource is.</summary>
    internal static string Describe(IntPtr tex)
    {
        if (tex == IntPtr.Zero) return "eng=?";
        var t = new CAurTexture(tex);
        return $"eng {t.GetWidth()}x{t.GetHeight()} bpp={t.GetBPP()} cells={t.GetNumX()}x{t.GetNumY()} "
             + $"cmp={t.GetCompressedSize()} fmt={t.GetCompressionFormat()} mips={t.GetMipMap() & MipCountMask} img={(t.GetImage() != IntPtr.Zero ? 1 : 0)}";
    }

    /// <summary>The mip-chain count occupies the low byte of what <c>GetMipMap</c> returns.</summary>
    private const int MipCountMask = 0xff;   // not-an-offset: the low byte of the mip word

    /// <summary>Scan one cell of the texture's decoded image. False = no image to read (already freed, or a
    /// layout this scanner does not know), and the consumers fall back to their safe verdicts.</summary>
    internal static bool Scan(IntPtr tex, int cell, float alphaCut, bool wantPixels, out Result r)
    {
        r = default;
        if (tex == IntPtr.Zero) return false;
        var t = new CAurTexture(tex);
        byte* px = (byte*)t.GetImage();
        if (px == null) return false;
        int w = t.GetWidth(), h = t.GetHeight();
        if (w <= 0 || h <= 0) return false;
        r.Mips = (t.GetMipMap() & MipCountMask) != 0;
        int cut = (int)(alphaCut * 255f);
        if (t.GetCompressedSize() > 0)
        {
            int fmt = t.GetCompressionFormat();
            byte* data = &px[t.GetNumMipHeaderBytes()];
            r.W = w; r.H = h;
            if (DdsReader.Lookup(px, out DdsReader.Info dds) && !DdsReader.EngineUploads(dds.Format))
                return ScanDecoded(data, w, h, dds.Format, cut, ref r);
            if (fmt == FmtDxt1) ScanDxt(data, w, h, false, cut, ref r);
            else if (fmt == FmtDxt5) ScanDxt(data, w, h, true, cut, ref r);
            else { LastRefusedFormat = fmt; return false; }
            return true;
        }
        int bpp = t.GetBPP(), nx = Math.Max(t.GetNumX(), 1), ny = Math.Max(t.GetNumY(), 1);
        int cw = w / nx, ch = h / ny;
        if (cw <= 0 || ch <= 0 || cell < 0 || cell >= nx * ny || bpp < 1 || bpp > 4) return false;
        int x0 = (cell % nx) * cw, y0 = (cell / nx) * ch;
        long stride = (long)w * bpp;
        ScanRaw(px, stride, bpp, x0, y0, cw, ch, cut, ref r);
        r.W = cw; r.H = ch;
        if (wantPixels && bpp >= 3)
        {
            byte[] o = ArrayPool<byte>.Shared.Rent((int)ChainBytes(cw, ch, r.Mips));
            fixed (byte* op = o)
                for (int y = 0; y < ch; y++)
                {
                    byte* s0 = &px[(y0 + y) * stride + (long)x0 * bpp];
                    byte* d0 = &op[(long)y * cw * 4];
                    for (int x = 0; x < cw; x++)
                    { byte* s = &s0[x * bpp], d = &d0[x * 4]; d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = bpp == 4 ? s[3] : (byte)255; }
                }
            r.Rgba = o;
        }
        return true;
    }

    /// <summary>A format the engine names by its same-sized stand-in (BC7 as DXT5 and so on) is scanned from its
    /// own decode; one with no CPU decoder here (BC6H, SNORM RGTC -- none carries alpha) is left unscanned.
    /// ponytail: a full level-0 decode on the main thread; move it to cx-bcn if such textures show in the Tex zone.</summary>
    private static bool ScanDecoded(byte* data, int w, int h, DdsReader.Fmt f, int cut, ref Result r)
    {
        if (!BcnDecode.Decodes(f)) { LastRefusedFormat = FmtDecodedBase + (int)f; return false; }
        int px = BcnDecode.PixelBytes(f), n = w * h * px;
        byte[] buf = ArrayPool<byte>.Shared.Rent(n);
        BcnDecode.Unpack(new ReadOnlySpan<byte>(data, DdsReader.LevelSize(w, h, DdsReader.ChannelsOf(f))), w, h, f, buf.AsSpan(0, n));
        fixed (byte* p = buf) ScanRaw(p, (long)w * px, px, 0, 0, w, h, cut, ref r);
        ArrayPool<byte>.Shared.Return(buf);
        return true;
    }

    /// <summary><see cref="LastRefusedFormat"/> for a DDS format with no decoder: this plus its <see cref="DdsReader.Fmt"/>.</summary>
    private const int FmtDecodedBase = 100;

    /// <summary>Full-resolution alpha count (a sparse scan misses the railing that IS the cutout), mean
    /// colour on a stride so a 2k sheet costs a few hundred adds.</summary>
    private static void ScanRaw(byte* px, long stride, int bpp, int x0, int y0, int cw, int ch, int cut, ref Result r)
    {
        long dropped = 0, graded = 0;
        if (bpp == 4)
            for (int y = 0; y < ch; y++)
            {
                byte* s = &px[(y0 + y) * stride + (long)x0 * 4 + 3];
                for (int x = 0; x < cw; x++) { int a = s[x * 4]; if (a < cut) dropped++; else if (a < OpaqueAlpha) graded++; }
            }
        r.Holes = dropped / (float)((long)cw * ch);
        r.Graded = graded / (float)((long)cw * ch);
        int step = Math.Max(1, Math.Max(cw, ch) / 64);
        long rs = 0, gs = 0, bs = 0, n = 0;
        for (int y = 0; y < ch; y += step)
            for (int x = 0; x < cw; x += step)
            {
                byte* s = &px[(y0 + y) * stride + (long)(x0 + x) * bpp];
                if (bpp >= 3) { rs += s[0]; gs += s[1]; bs += s[2]; }
                else { rs += s[0]; gs += s[0]; bs += s[0]; }
                n++;
            }
        if (n > 0) { r.Mean = new Vector3(rs / (255f * n), gs / (255f * n), bs / (255f * n)); r.MeanOk = true; }
    }

    /// <summary>DXT1/DXT5 blocks: alpha per texel exactly (DXT1's 1-bit key when c0 &lt;= c1, DXT5's 3-bit
    /// ramp), mean from the block endpoints -- a block's texels sit between them.</summary>
    private static void ScanDxt(byte* data, int w, int h, bool dxt5, int cut, ref Result r)
    {
        int bw = (w + 3) / 4, bh = (h + 3) / 4, bytes = dxt5 ? 16 : 8;
        long dropped = 0, graded = 0, rs = 0, gs = 0, bs = 0;
        int* alpha = stackalloc int[8];
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                byte* b = &data[((long)by * bw + bx) * bytes];
                byte* c = dxt5 ? &b[8] : b;
                int c0 = c[0] | (c[1] << 8), c1 = c[2] | (c[3] << 8);
                uint idx = (uint)(c[4] | (c[5] << 8) | (c[6] << 16) | (c[7] << 24));
                rs += Expand5(c0 >> 11) + Expand5(c1 >> 11);
                gs += Expand6((c0 >> 5) & 63) + Expand6((c1 >> 5) & 63);
                bs += Expand5(c0 & 31) + Expand5(c1 & 31);
                ulong abits = 0;
                if (dxt5)
                {
                    int a0 = b[0], a1 = b[1];
                    alpha[0] = a0; alpha[1] = a1;
                    if (a0 > a1) for (int i = 2; i < 8; i++) alpha[i] = ((8 - i) * a0 + (i - 1) * a1) / 7;
                    else { for (int i = 2; i < 6; i++) alpha[i] = ((6 - i) * a0 + (i - 1) * a1) / 5; alpha[6] = 0; alpha[7] = 255; }
                    for (int i = 0; i < 6; i++) abits |= (ulong)b[2 + i] << (8 * i);
                }
                for (int t = 0; t < 16; t++)
                {
                    int x = bx * 4 + (t & 3), y = by * 4 + (t >> 2);
                    if (x >= w || y >= h) continue;
                    int a = dxt5 ? alpha[(int)((abits >> (3 * t)) & 7)]
                                 : (c0 <= c1 && ((idx >> (2 * t)) & 3) == 3 ? 0 : 255);
                    if (a < cut) dropped++; else if (a < OpaqueAlpha) graded++;
                }
            }
        long blocks = (long)bw * bh;
        r.Holes = dropped / (float)((long)w * h);
        r.Graded = graded / (float)((long)w * h);
        if (blocks > 0) { r.Mean = new Vector3(rs / (510f * blocks), gs / (510f * blocks), bs / (510f * blocks)); r.MeanOk = true; }
    }

    /// <summary>The mean colour alone of a decoded raster (1, 3 or 4 bytes per texel), 0..1 in the image's
    /// own encoding -- the value <see cref="Result.Mean"/> carries, for a texture the registry decoded.</summary>
    internal static bool MeanOfRaster(byte* px, int w, int h, int bpp, out Vector3 mean)
    {
        Result r = default;
        ScanRaw(px, (long)w * bpp, bpp, 0, 0, w, h, 0, ref r);
        mean = r.Mean;
        return r.MeanOk;
    }

    /// <summary>...and of a BC1/BC3 top level, from the block endpoints as <see cref="ScanDxt"/> takes it.</summary>
    internal static bool MeanOfDxt(byte* data, int w, int h, bool dxt5, out Vector3 mean)
    {
        Result r = default;
        ScanDxt(data, w, h, dxt5, 0, ref r);
        mean = r.Mean;
        return r.MeanOk;
    }

    /// <summary>Alpha at or above this counts as opaque for <see cref="Result.Graded"/>.</summary>
    private const int OpaqueAlpha = 248;

    private static int Expand5(int v) => (v << 3) | (v >> 2);
    private static int Expand6(int v) => (v << 2) | (v >> 4);

    /// <summary>Box-filter mip chain in place, colour alpha-weighted: level i+1 written from level i.
    /// <paramref name="off"/> and the sizes are the chain the caller laid out with <see cref="Chain"/>.</summary>
    internal static void BuildMips(byte* px, int levels, int* lw, int* lh, long* off)
    {
        for (int i = 1; i < levels; i++)
        {
            int sw = lw[i - 1], sh = lh[i - 1], dw = lw[i], dh = lh[i];
            byte* s = &px[off[i - 1]], d = &px[off[i]];
            for (int y = 0; y < dh; y++)
            {
                int y0 = Math.Min(y * 2, sh - 1), y1 = Math.Min(y * 2 + 1, sh - 1);
                for (int x = 0; x < dw; x++)
                {
                    int x0 = Math.Min(x * 2, sw - 1), x1 = Math.Min(x * 2 + 1, sw - 1);
                    byte* p00 = &s[((long)y0 * sw + x0) * 4], p01 = &s[((long)y0 * sw + x1) * 4];
                    byte* p10 = &s[((long)y1 * sw + x0) * 4], p11 = &s[((long)y1 * sw + x1) * 4];
                    byte* o = &d[((long)y * dw + x) * 4];
                    // #499: alpha-weighted colour -- a transparent texel's (black) colour must not darken the
                    // level. Alpha itself is the plain mean; a fully transparent box keeps the plain mean too.
                    int wa = p00[3] + p01[3] + p10[3] + p11[3];
                    for (int k = 0; k < 3; k++)
                        o[k] = wa == 0 ? (byte)((p00[k] + p01[k] + p10[k] + p11[k] + 2) >> 2)
                                       : (byte)((p00[k] * p00[3] + p01[k] * p01[3] + p10[k] * p10[3] + p11[k] * p11[3] + wa / 2) / wa);
                    o[3] = (byte)((wa + 2) >> 2);
                }
            }
        }
    }

    /// <summary>RGBA8 bytes of the chain <see cref="Chain"/> lays out (uncapped, so never less).</summary>
    internal static long ChainBytes(int w, int h, bool mips)
    {
        for (long total = 0; ; w = Math.Max(w >> 1, 1), h = Math.Max(h >> 1, 1))
        {
            total += (long)w * h * 4;
            if (!mips || (w == 1 && h == 1)) return total;
        }
    }

    /// <summary>Lay out a chain from <paramref name="w"/> x <paramref name="h"/> down to 1x1 (one level when
    /// <paramref name="mips"/> is false), capped at <paramref name="max"/> levels. Returns the level count;
    /// <paramref name="total"/> is the RGBA8 byte size of the whole chain.</summary>
    internal static int Chain(int w, int h, bool mips, int max, int* lw, int* lh, long* off, out long total)
    {
        int n = 0; total = 0;
        for (; n < max; n++)
        {
            lw[n] = w; lh[n] = h; off[n] = total;
            total += (long)w * h * 4;
            if (!mips || (w == 1 && h == 1)) { n++; break; }
            w = Math.Max(w >> 1, 1); h = Math.Max(h >> 1, 1);
        }
        return n;
    }

    /// <summary>Off-device: a DXT1 block keyed transparent on index 3 counts its holes, a DXT5 ramp block
    /// counts alpha under the cut exactly, the chain lays out to 1x1, and a 2x2 box of the four corner
    /// colours averages to grey.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        // DXT1: c0 = 0 <= c1 = 0xffff -> 4-colour+key mode; indices all 3 -> every texel transparent
        byte* d1 = stackalloc byte[8] { 0, 0, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff };
        Result r = default; ScanDxt(d1, 4, 4, false, 51, ref r);
        check.Expect(r.Holes == 1f, $"dxt1 keyed block: holes {r.Holes}");
        byte* d1s = stackalloc byte[8] { 0xff, 0xff, 0, 0, 0, 0, 0, 0 };   // c0 > c1: opaque mode
        r = default; ScanDxt(d1s, 4, 4, false, 51, ref r);
        check.Expect(r.Holes == 0f && r.MeanOk && MathF.Abs(r.Mean.X - 0.5f) < 0.01f, $"dxt1 opaque block: holes {r.Holes} mean {r.Mean}");
        // DXT5: a0 = 255, a1 = 0, indices: texel 0 -> 1 (alpha 0), the rest 0 (alpha 255)
        byte* d5 = stackalloc byte[16]; d5[0] = 255; d5[1] = 0; d5[2] = 1;
        r = default; ScanDxt(d5, 4, 4, true, 51, ref r);
        check.Expect(MathF.Abs(r.Holes - 1f / 16f) < 1e-6f, $"dxt5 ramp block: holes {r.Holes}");
        int* lw = stackalloc int[16], lh = stackalloc int[16]; long* off = stackalloc long[16];
        int n = Chain(8, 2, true, 16, lw, lh, off, out long total);
        check.Expect(n == 4 && lw[3] == 1 && lh[3] == 1 && total == (16 + 4 + 2 + 1) * 4 && ChainBytes(8, 2, true) == total, $"chain 8x2: {n} levels, {total} bytes");
        check.Expect(Chain(64, 64, false, 16, lw, lh, off, out _) == 1, "no mips = one level");
        byte* px = stackalloc byte[(4 + 1) * 4];
        for (int i = 0; i < 4; i++) { px[i * 4] = (byte)(i * 85); px[i * 4 + 1] = 255; px[i * 4 + 2] = 0; px[i * 4 + 3] = 255; }
        n = Chain(2, 2, true, 16, lw, lh, off, out _);
        BuildMips(px, n, lw, lh, off);
        check.Expect(px[16] == 128 && px[17] == 255 && px[18] == 0 && px[19] == 255, $"box mip: {px[16]},{px[17]},{px[18]},{px[19]}");
        // #499: a transparent black texel must not pull the level's colour toward black -- the alpha-weighted
        // mean keeps the covered colour, and only the alpha averages down.
        for (int i = 0; i < 4; i++) { px[i * 4] = 200; px[i * 4 + 1] = 100; px[i * 4 + 2] = 0; px[i * 4 + 3] = 255; }
        px[12] = 0; px[13] = 0; px[14] = 0; px[15] = 0;
        BuildMips(px, n, lw, lh, off);
        check.Expect(px[16] == 200 && px[17] == 100 && px[18] == 0 && px[19] == 191, $"alpha-weighted mip: {px[16]},{px[17]},{px[18]},{px[19]}");
        log.Info(check.Ok ? "TexScan.SelfCheck: passed" : "TexScan.SelfCheck: FAILED");
        return check.Ok;
    }
}
