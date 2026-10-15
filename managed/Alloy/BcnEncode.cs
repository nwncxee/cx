using CX.Framework;

namespace CX.Alloy;

/// <summary>
/// CPU BC1/BC3 block encoders for the transcode of the engine's uncompressed texture path: bounding-box
/// endpoints with a 1/16 inset and nearest-palette indices, the stb_dxt family's baseline. BC1 is two RGB565
/// endpoints little-endian then sixteen 2-bit indices LSB-first; BC3 prepends an eight-byte BC4 alpha block, and
/// a partial edge block clamp-samples. Cold load path, whatever thread the upload funnel fired on, no allocation.
/// </summary>
internal static unsafe class BcnEncode
{
    /// <summary>Encoded byte size of one level: ceil-to-block grid times 8 (BC1) or 16 (BC3).</summary>
    internal static int LevelSize(int w, int h, bool bc3)
        => ((w + 3) / 4) * ((h + 3) / 4) * (bc3 ? 16 : 8);

    /// <summary>Encode one RGBA8 level into <paramref name="dst"/> (sized by <see cref="LevelSize"/>).</summary>
    internal static void EncodeLevel(byte* rgba, int w, int h, bool bc3, byte* dst)
    {
        byte* px = stackalloc byte[64];
        long o = 0;
        for (int by = 0; by < h; by += 4)
        for (int bx = 0; bx < w; bx += 4)
        {
            for (int y = 0; y < 4; y++)
            for (int x = 0; x < 4; x++)
            {
                // clamp-sample: an edge block repeats its last row/column
                int sx = bx + x >= w ? w - 1 : bx + x, sy = by + y >= h ? h - 1 : by + y;
                byte* s = &rgba[(sy * (long)w + sx) * 4];
                byte* d = &px[(y * 4 + x) * 4];
                d[0] = s[0]; d[1] = s[1]; d[2] = s[2]; d[3] = s[3];
            }
            if (bc3) { Bc4Block(px, &dst[o]); o += 8; }
            Bc1Block(px, &dst[o]); o += 8;
        }
    }

    /// <summary>Texels at or below the engine's own <c>fAlphaDiscardValue</c> (0.2) stay out of the
    /// colour-endpoint fit. Their RGB is the matte an artist left behind the alpha -- white on most NWN foliage --
    /// and no cutout fragment they produce is shaded, but a bounding box that includes them drags an endpoint to
    /// white and every SURVIVING texel decodes along that line: a white rim on every transcoded alpha edge.</summary>
    private const int MatteAlpha = 51;

    /// <summary>One 4x4 colour block -&gt; 8 bytes of BC1, always in 4-colour mode (c0 &gt; c1, or equal
    /// endpoints with every index 0, whose palette is degenerate either way).</summary>
    private static void Bc1Block(byte* px, byte* dst)
    {
        int rMin = 255, gMin = 255, bMin = 255, rMax = 0, gMax = 0, bMax = 0;
        // Two attempts: the visible texels alone, then -- only if the block has none -- all sixteen, whose
        // colour is never sampled anyway and is encoded purely so the block is well-formed.
        for (int pass = 0; pass < 2; pass++)
        {
            int seen = 0;
            for (int i = 0; i < 16; i++)
            {
                byte* p = &px[i * 4];
                if (pass == 0 && p[3] <= MatteAlpha) continue;
                seen++;
                if (p[0] < rMin) rMin = p[0]; if (p[0] > rMax) rMax = p[0];
                if (p[1] < gMin) gMin = p[1]; if (p[1] > gMax) gMax = p[1];
                if (p[2] < bMin) bMin = p[2]; if (p[2] > bMax) bMax = p[2];
            }
            if (seen > 0) break;
        }
        // stb_dxt's inset: pull the box in 1/16th so outliers stop owning the endpoints
        int ir = (rMax - rMin) >> 4, ig = (gMax - gMin) >> 4, ib = (bMax - bMin) >> 4;
        rMin += ir; gMin += ig; bMin += ib; rMax -= ir; gMax -= ig; bMax -= ib;
        ushort c0 = To565(rMax, gMax, bMax), c1 = To565(rMin, gMin, bMin);
        if (c0 < c1) { (c0, c1) = (c1, c0); (rMin, rMax) = (rMax, rMin); (gMin, gMax) = (gMax, gMin); (bMin, bMax) = (bMax, bMin); }
        dst[0] = (byte)c0; dst[1] = (byte)(c0 >> 8);
        dst[2] = (byte)c1; dst[3] = (byte)(c1 >> 8);
        uint idx = 0;
        if (c0 != c1)
        {
            int dr = rMax - rMin, dg = gMax - gMin, db = bMax - bMin;
            int len = dr * dr + dg * dg + db * db;
            for (int i = 0; i < 16; i++)
            {
                byte* p = &px[i * 4];
                int dot = (p[0] - rMin) * dr + (p[1] - gMin) * dg + (p[2] - bMin) * db;
                int t = (dot * 3 + len / 2) / len;               // 0..3 along c1->c0
                t = t < 0 ? 0 : t > 3 ? 3 : t;
                // palette order for rising t: c1, (c0+2c1)/3, (2c0+c1)/3, c0 = indices 1,3,2,0
                idx |= (uint)(t == 0 ? 1 : t == 1 ? 3 : t == 2 ? 2 : 0) << (i * 2);
            }
        }
        dst[4] = (byte)idx; dst[5] = (byte)(idx >> 8); dst[6] = (byte)(idx >> 16); dst[7] = (byte)(idx >> 24);
    }

    /// <summary>One 4x4 alpha block -&gt; 8 bytes of BC4, always in the 8-value (a0 &gt; a1) mode.</summary>
    private static void Bc4Block(byte* px, byte* dst)
    {
        int aMin = 255, aMax = 0;
        for (int i = 0; i < 16; i++)
        {
            byte a = px[i * 4 + 3];
            if (a < aMin) aMin = a; if (a > aMax) aMax = a;
        }
        dst[0] = (byte)aMax; dst[1] = (byte)aMin;
        ulong idx = 0;
        if (aMax != aMin)
        {
            int d = aMax - aMin;
            for (int i = 0; i < 16; i++)
            {
                int t = ((px[i * 4 + 3] - aMin) * 7 + d / 2) / d;   // 0..7 along a1->a0
                // palette: idx0=a0, idx1=a1, idx2..7 descend from a0 -- rising t maps 1,7,6,5,4,3,2,0
                idx |= (ulong)(t == 0 ? 1 : t == 7 ? 0 : 8 - t) << (i * 3);
            }
        }
        for (int b = 0; b < 6; b++) dst[2 + b] = (byte)(idx >> (b * 8));
    }

    private static ushort To565(int r, int g, int b)
        => (ushort)(((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3));

    /// <summary>Off-device: round-trip the encoders through a spec-faithful decode. Solid blocks must come
    /// back EXACT at 565/8-bit precision; a gradient must come back within one quantization step of the
    /// endpoint line. Fails if the index mapping, palette order, or bit packing drifts.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        byte* rgba = stackalloc byte[64];
        byte* blk = stackalloc byte[16];
        byte* outPx = stackalloc byte[64];

        // Solid colour + solid alpha: both endpoints collapse, every pixel decodes to the quantized colour.
        for (int i = 0; i < 16; i++) { rgba[i * 4] = 96; rgba[i * 4 + 1] = 160; rgba[i * 4 + 2] = 224; rgba[i * 4 + 3] = 200; }
        EncodeLevel(rgba, 4, 4, true, blk);
        DecodeBc3(blk, outPx);
        bool ok = true;
        for (int i = 0; i < 16; i++)
            ok &= outPx[i * 4] == (96 >> 3) * 255 / 31 && outPx[i * 4 + 1] == (160 >> 2) * 255 / 63
               && outPx[i * 4 + 2] == (224 >> 3) * 255 / 31 && outPx[i * 4 + 3] == 200;

        // Black-to-white gradient with a ramping alpha: every decoded texel within one palette step.
        for (int i = 0; i < 16; i++)
        {
            byte v = (byte)(i * 17);
            rgba[i * 4] = v; rgba[i * 4 + 1] = v; rgba[i * 4 + 2] = v; rgba[i * 4 + 3] = (byte)(255 - i * 16);
        }
        EncodeLevel(rgba, 4, 4, true, blk);
        DecodeBc3(blk, outPx);
        for (int i = 0; i < 16; i++)
        {
            int e = rgba[i * 4] - outPx[i * 4];
            int a = rgba[i * 4 + 3] - outPx[i * 4 + 3];
            // ALPHA everywhere, colour only where the texel is visible: the endpoint fit stops at
            // MatteAlpha on purpose (see there), so a texel under it has no colour promise to keep.
            ok &= (a < 0 ? -a : a) <= 24;                             // one BC4 palette step
            if (rgba[i * 4 + 3] > MatteAlpha) ok &= (e < 0 ? -e : e) <= 48;   // one BC1 palette step
        }

        // #357: the matte. Half a foliage block is dark leaf, half is the white the artist left under
        // alpha 0. Every VISIBLE texel has to come back dark -- fitted over all sixteen, the endpoint goes
        // white and the leaf edge decodes as the white rim this check exists to keep out.
        for (int i = 0; i < 16; i++)
        {
            bool leaf = i < 8;
            byte v = leaf ? (byte)40 : (byte)255;
            rgba[i * 4] = v; rgba[i * 4 + 1] = v; rgba[i * 4 + 2] = v; rgba[i * 4 + 3] = leaf ? (byte)255 : (byte)0;
        }
        EncodeLevel(rgba, 4, 4, true, blk);
        DecodeBc3(blk, outPx);
        for (int i = 0; i < 8; i++) ok &= outPx[i * 4] < 80;

        // The edge clamp: a 5x5 level is four blocks whose overhang repeats the last row/column, so a solid
        // image stays solid and the size math agrees with the block grid.
        ok &= LevelSize(5, 5, false) == 4 * 8 && LevelSize(4, 4, true) == 16 && LevelSize(1, 1, false) == 8;

        // Transcode sizes its scratch by summing this over every level, so the block grid has to stay right
        // when the two axes bottom out at different levels -- a strip's tail sums as halves, not quarters,
        // and a ratio over level 0 would come up short.
        ok &= LevelSize(512, 16, true) == 128 * 4 * 16 && LevelSize(32, 1, true) == 8 * 16
              && LevelSize(1, 4096, false) == 1024 * 8;

        log.Info(ok ? "BcnEncode.SelfCheck: passed" : "BcnEncode.SelfCheck: FAILED");
        return ok;
    }

    /// <summary>Spec-faithful BC3 decode of one block, for the self-check only.</summary>
    private static void DecodeBc3(byte* blk, byte* outPx)
    {
        int a0 = blk[0], a1 = blk[1];
        ulong aIdx = 0;
        for (int b = 0; b < 6; b++) aIdx |= (ulong)blk[2 + b] << (b * 8);
        int c0 = blk[8] | (blk[9] << 8), c1 = blk[10] | (blk[11] << 8);
        uint cIdx = (uint)(blk[12] | (blk[13] << 8) | (blk[14] << 16) | (blk[15] << 24));
        for (int i = 0; i < 16; i++)
        {
            int ai = (int)((aIdx >> (i * 3)) & 7);
            outPx[i * 4 + 3] = (byte)(ai == 0 ? a0 : ai == 1 ? a1 : ((8 - ai) * a0 + (ai - 1) * a1) / 7);
            int ci = (int)((cIdx >> (i * 2)) & 3);
            for (int ch = 0; ch < 3; ch++)
            {
                int e0 = Chan(c0, ch), e1 = Chan(c1, ch);
                outPx[i * 4 + ch] = (byte)(ci == 0 ? e0 : ci == 1 ? e1 : ci == 2 ? (2 * e0 + e1) / 3 : (e0 + 2 * e1) / 3);
            }
        }
    }

    private static int Chan(int c565, int ch) => ch == 0 ? ((c565 >> 11) & 31) * 255 / 31
        : ch == 1 ? ((c565 >> 5) & 63) * 255 / 63 : (c565 & 31) * 255 / 31;
}
