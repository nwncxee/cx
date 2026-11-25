using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>
/// alloy.upscale: a small model diffuse rebuilt at twice its size as it loads, so stock 256 and 512 px textures hold up
/// close. Lanczos-2 with each output clamped to its four nearest source texels (no ringing), wrapping unless the texture
/// clamps so a tiling texture stays seamless, a light contrast-adaptive sharpen, then the full chain re-encoded in the
/// texture's own BC format. Only what <see cref="Job.Cuts"/> marks (a model's diffuse slot) is touched.
/// </summary>
internal static unsafe partial class TexRegistry
{
    /// <summary>How far the sharpen pushes a texel from its four neighbours' mean, at most.</summary>
    private const float UpscaleSharpen = 0.35f;
    /// <summary>Alpha at or above this stays in after the rebuild of a hard cutout.</summary>
    private const int UpscaleAlphaIn = 128;
    /// <summary>The smallest side worth the work: a tiny texture is a flat colour or a gradient.</summary>
    private const int UpscaleMinSide = 32;

    /// <summary>Bumped whenever the rebuild's output changes, so no cache file from an older rebuild is read.</summary>
    private const ulong UpscaleVersion = 4;
    /// <summary>Bytes ahead of the chain in a cache file: width, height and levels.</summary>
    private const int UpscaleHeader = 12;

    /// <summary>alloy.upscale or alloy.upscaleai changed: every model diffuse is read again under the new setting on the
    /// next game tick. The knobs change on the input thread, so the flag carries it over.</summary>
    private sealed class UpscaleReloadState { public volatile bool Wanted; }
    private static readonly UpscaleReloadState _upReload = new();

    /// <summary>The knobs' change callback: any thread.</summary>
    internal static void UpscaleChanged() => _upReload.Wanted = true;

    /// <summary>The reload <see cref="UpscaleChanged"/> asked for, on the game thread. The cache answers the upscaled
    /// images at once and the originals read as fast as ever, so the switch shows within a few frames.</summary>
    private static void ApplyUpscaleReload()
    {
        if (!_upReload.Wanted) return;
        _upReload.Wanted = false;
        lock (_lock)
            for (int i = 1; i < _high; i++)
            {
                if (_resref[i] == null || _detached[i] || !_cuts[i] || _kind[i] == TexKind.Plt || _state[i] == TexState.Free) continue;
                if (_refs[i] == 0) { Retire(i); continue; }
                Reload(i);
            }
    }

    /// <summary>Does alloy.upscale cover job <paramref name="j"/>, a <paramref name="f"/> DDS?</summary>
    private static bool UpscaleWanted(Job j, DdsReader.Fmt f)
    {
        int cap = Renderer.Upscale.V;
        return cap > 0 && j.Cuts && !j.DataTexture && !j.Tail && !j.Upgrade && j.GridX * j.GridY <= 1 && !j.Cycle
               && f is DdsReader.Fmt.Bc1 or DdsReader.Fmt.Bc3
               && Math.Max(j.W, j.H) <= cap && Math.Min(j.W, j.H) >= UpscaleMinSide;
    }

    /// <summary>The cache key of an upscale: the original level 0's bytes and every term the rebuild reads.</summary>
    private static ulong UpscaleKey(ReadOnlySpan<byte> level0, Job j, DdsReader.Fmt f)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in level0) h = (h ^ b) * 1099511628211UL;
        foreach (ulong t in (ReadOnlySpan<ulong>)[UpscaleVersion, (ulong)f, (ulong)j.W, (ulong)j.H, j.Clamp ? 1UL : 0,
                                                  j.Cov == TexCoverage.Cutout ? 1UL : 0, j.EnvMap ? 1UL : 0])
            h = (h ^ t) * 1099511628211UL;
        return h;
    }

    /// <summary>The cache key with whether the AI rebuild made it and at what scale folded in, so a classic or 2x rebuild
    /// never answers for an AI 4x one.</summary>
    private static ulong UpscaleKeyAi(ulong key, bool ai, int factor)
        => (((key ^ (ai ? 0xA1UL + (ulong)AiModelWanted : 0x0CUL)) * 1099511628211UL ^ (ulong)factor) * 1099511628211UL
            ^ (ai ? (ulong)MathF.Round(UpscaleProtect * 20f) : 0UL)) * 1099511628211UL;

    private static string UpscalePath(string resref, ulong key)
        => System.IO.Path.Combine(UserFolders.Cache, "textures", $"{resref.ToLowerInvariant()}-{key:x16}.up");

    /// <summary>Job <paramref name="j"/>'s chain (<paramref name="stored"/>, format <paramref name="f"/>) swapped for its
    /// upscaled one when the disk cache holds it; true when it did. A miss is <see cref="QueueUpgrade"/>'s to build.</summary>
    private static bool UpscaleDds(Job j, DdsReader.Fmt f, ReadOnlySpan<byte> stored)
    {
        if (!UpscaleWanted(j, f)) return false;
        ulong key = UpscaleKey(stored[..DdsReader.LevelSize(j.W, j.H, j.Channels)], j, f);
        int factor = UpscaleFactor(j.W, j.H, AiWanted);
        if (CacheFile.Read(UpscalePath(j.Resref, UpscaleKeyAi(key, AiWanted, factor))) is not { Length: > UpscaleHeader } blob) return false;
        int w2 = BitConverter.ToInt32(blob, 0), h2 = BitConverter.ToInt32(blob, 4), levels = BitConverter.ToInt32(blob, 8);
        if (w2 != j.W * factor || h2 != j.H * factor || levels != Levels(w2, h2)) return false;
        var chain = new NativeBuffer<byte>();
        chain.TakeExact(blob.Length - UpscaleHeader);
        blob.AsSpan(UpscaleHeader).CopyTo(chain.Span);
        DdsReader.FreePrepared(j.Data);
        j.Data = chain.Pointer; j.Native = true;
        j.W = w2; j.H = h2; j.Levels = levels; j.Bytes = chain.Length;
        return true;
    }

    /// <summary>A finished job alloy.upscale covers and the cache did not hold: its level 0 copied into a background
    /// upgrade, which <see cref="UpgradeWork"/> rebuilds on an idle worker and whose upload replaces this one.</summary>
    private static void QueueUpgrade(Job j, DdsReader.Fmt f, ReadOnlySpan<byte> stored)
    {
        if (j.Native || j.Why != null || !UpscaleWanted(j, f)) return;
        ReadOnlySpan<byte> level0 = stored[..DdsReader.LevelSize(j.W, j.H, j.Channels)];
        Job u = Rent();
        u.Slot = j.Slot; u.Gen = j.Gen; u.Revision = j.Revision; u.Resref = j.Resref; u.Kind = j.Kind; u.Loc = j.Loc;
        u.Data = null; u.Chained = false; u.Cuts = j.Cuts; u.DataTexture = false; u.Why = null;
        u.W = j.W; u.H = j.H; u.Levels = 0; u.Bytes = 0; u.Channels = j.Channels; u.GlFmt = j.GlFmt;
        u.Compressed = true; u.GenMips = false; u.Clamp = j.Clamp; u.Nearest = j.Nearest; u.Mip = j.Mip;
        u.Cov = j.Cov; u.CovWhy = j.CovWhy; u.Mean = j.Mean; u.MeanOk = j.MeanOk; u.BumpyShiny = j.BumpyShiny; u.EnvMap = j.EnvMap;
        u.Fast = false; u.Tail = false; u.Native = false; u.GridX = 1; u.GridY = 1; u.FullW = 0; u.Cycle = false;
        u.Pixels = null; u.Arturo = default; u.Ripple = null;
        u.Upgrade = true; u.Normal = false; u.Height = false; u.Rough = false; u.UpSource = level0.ToArray(); u.UpFmt = f; u.UpKey = UpscaleKey(level0, j, f);
        lock (_gate) { _inUp.Enqueue(u); Monitor.PulseAll(_gate); }
    }

    /// <summary>The background rebuild: decode, upscale, the full chain re-encoded (cutout mips kept as
    /// <see cref="MipChain.Dds"/> keeps them), written to the disk cache, and left on the job for its upload.</summary>
    private static void UpgradeWork(Job j)
    {
        DdsReader.Fmt f = j.UpFmt;
        bool bc3 = f == DdsReader.Fmt.Bc3;
        bool hard = j.Cov == TexCoverage.Cutout;
        byte[] src = DecodeRgba(j.UpSource, j.W, j.H, f);
        j.UpSource = null;
        byte[] level = Upscale2x(src, j.W, j.H, !j.Clamp, hard);
        int factor = UpscaleFactor(j.W, j.H, true);
        byte[]? ai = AiUpscale(src, j.W, j.H, !j.Clamp, level, factor, hard);
        if (ai != null) level = ai;
        else factor = 2;
        int w2 = j.W * factor, h2 = j.H * factor;
        int levels = Levels(w2, h2);
        long bytes = 0;
        for (int l = 0, lw = w2, lh = h2; l < levels; l++, lw = Math.Max(lw / 2, 1), lh = Math.Max(lh / 2, 1))
            bytes += BcnEncode.LevelSize(lw, lh, bc3);
        byte[] file = new byte[UpscaleHeader + bytes];
        BitConverter.TryWriteBytes(file.AsSpan(0, 4), w2);
        BitConverter.TryWriteBytes(file.AsSpan(4, 4), h2);
        BitConverter.TryWriteBytes(file.AsSpan(8, 4), levels);
        Span<byte> chainOut = file.AsSpan(UpscaleHeader);
        int at = 0;
        for (int l = 0, lw = w2, lh = h2; l < levels; l++)
        {
            int n = BcnEncode.LevelSize(lw, lh, bc3);
            BcnEncode.EncodeLevel(level, lw, lh, bc3, chainOut.Slice(at, n));
            at += n;
            if (l + 1 < levels) level = Halve(level, lw, lh, out lw, out lh);
        }
        if (!j.EnvMap) MipChain.Dds(chainOut, w2, h2, levels, f);
        string path = UpscalePath(j.Resref, UpscaleKeyAi(j.UpKey, ai != null, factor));
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        CacheFile.Write(path, file);
        var chain = new NativeBuffer<byte>();
        chain.TakeExact(chainOut.Length);
        chainOut.CopyTo(chain.Span);
        j.Data = chain.Pointer; j.Native = true;
        string how = ai != null ? $"AI {AiModelNames[AiModelWanted]}, {factor}x, protect {UpscaleProtect:F2}" : "classic 2x";
        j.UpNote = $"upscaled {j.W}x{j.H} -> {w2}x{h2} ({how})";
        j.W = w2; j.H = h2; j.Levels = levels; j.Bytes = chain.Length;
    }

    /// <summary>Level 0 of a BC1 or BC3 image as RGBA8. The decoder answers BC1 as RGB, three bytes a texel (the engine
    /// never sees its alpha), so that is widened here with alpha 255; read as RGBA it was scrambled colour and black.</summary>
    internal static byte[] DecodeRgba(ReadOnlySpan<byte> blocks, int w, int h, DdsReader.Fmt f)
    {
        int px = BcnDecode.PixelBytes(f);
        byte[] raw = new byte[w * h * px];
        BcnDecode.Unpack(blocks, w, h, f, raw);
        if (px == 4) return raw;
        byte[] rgba = new byte[w * h * 4];
        for (int i = 0, o = 0; i < raw.Length; i += 3, o += 4)
        {
            rgba[o] = raw[i]; rgba[o + 1] = raw[i + 1]; rgba[o + 2] = raw[i + 2]; rgba[o + 3] = 255;
        }
        return rgba;
    }

    /// <summary>RGBA8 <paramref name="src"/> at twice its size: Lanczos-2, each output clamped to the range of the four
    /// source texels nearest it, then sharpened; <paramref name="hard"/> keeps a cutout's alpha binary.</summary>
    internal static byte[] Upscale2x(byte[] src, int w, int h, bool wrap, bool hard)
    {
        int w2 = w * 2, h2 = h * 2;
        // the two output phases sit a quarter texel either side of a source centre: four taps each, their source
        // columns and rows worked out once (the wrap's modulo stays out of the inner loops)
        Span<float> we = stackalloc float[4], wo = stackalloc float[4];
        Taps(0.75f, we);   // an even output's frac past its left tap
        Taps(0.25f, wo);
        int[][] tx = TapIndex(w2, w, wrap), ty = TapIndex(h2, h, wrap);
        int[] nx = NearIndex(w2, w, wrap), ny = NearIndex(h2, h, wrap);
        float[] mid = new float[w2 * h * 4];
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 0; x < w2; x++)
            {
                bool even = (x & 1) == 0;
                float k0 = even ? we[0] : wo[0], k1 = even ? we[1] : wo[1], k2 = even ? we[2] : wo[2], k3 = even ? we[3] : wo[3];
                int t0 = At(tx[0][x], row, 1, 0), t1 = At(tx[1][x], row, 1, 0);
                int t2 = At(tx[2][x], row, 1, 0), t3 = At(tx[3][x], row, 1, 0);
                int o = At(x, y, w2, 0);
                for (int c = 0; c < 4; c++)
                    mid[o + c] = k0 * src[t0 + c] + k1 * src[t1 + c] + k2 * src[t2 + c] + k3 * src[t3 + c];
            }
        }
        float[] up = new float[w2 * h2 * 4];
        for (int y = 0; y < h2; y++)
        {
            bool even = (y & 1) == 0;
            float k0 = even ? we[0] : wo[0], k1 = even ? we[1] : wo[1], k2 = even ? we[2] : wo[2], k3 = even ? we[3] : wo[3];
            int r0 = At(0, ty[0][y], w2, 0), r1 = At(0, ty[1][y], w2, 0);
            int r2 = At(0, ty[2][y], w2, 0), r3 = At(0, ty[3][y], w2, 0);
            int sy = y >> 1, syn = ny[y];
            for (int x = 0; x < w2; x++)
            {
                int sx = x >> 1, sxn = nx[x];
                int q = At(x, 0, 0, 0), o = At(x, y, w2, 0);
                int a0 = At(sx, sy, w, 0), b0 = At(sxn, sy, w, 0), d0 = At(sx, syn, w, 0), e0 = At(sxn, syn, w, 0);
                for (int c = 0; c < 4; c++)
                {
                    float s = k0 * mid[r0 + q + c] + k1 * mid[r1 + q + c] + k2 * mid[r2 + q + c] + k3 * mid[r3 + q + c];
                    // the anti-ringing clamp: never past the four source texels this output lies between
                    byte a = src[a0 + c], b = src[b0 + c], d = src[d0 + c], e = src[e0 + c];
                    float lo = Math.Min(Math.Min(a, b), Math.Min(d, e)), hi = Math.Max(Math.Max(a, b), Math.Max(d, e));
                    up[o + c] = Math.Clamp(s, lo, hi);
                }
            }
        }
        byte[] dst = new byte[w2 * h2 * 4];
        int[] xl = new int[w2], xr = new int[w2];
        for (int x = 0; x < w2; x++) { xl[x] = Edge(x - 1, w2, wrap); xr[x] = Edge(x + 1, w2, wrap); }
        for (int y = 0; y < h2; y++)
        {
            int yu = Edge(y - 1, h2, wrap), yd = Edge(y + 1, h2, wrap);
            for (int x = 0; x < w2; x++)
            {
                int o = At(x, y, w2, 0);
                int l = At(xl[x], y, w2, 0), r = At(xr[x], y, w2, 0);
                int u = At(x, yu, w2, 0), dn = At(x, yd, w2, 0);
                for (int c = 0; c < 3; c++)
                {
                    float v = up[o + c], m = 0.25f * (up[l + c] + up[r + c] + up[u + c] + up[dn + c]);
                    // contrast-adaptive: a texel already far from its neighbours is pushed less
                    float ks = UpscaleSharpen * (1f - Math.Min(Math.Abs(v - m) / 64f, 1f));
                    dst[o + c] = (byte)Math.Clamp(v + (v - m) * ks + 0.5f, 0f, 255f);
                }
                float al = up[o + 3];
                dst[o + 3] = hard ? (al >= UpscaleAlphaIn ? (byte)255 : (byte)0) : (byte)Math.Clamp(al + 0.5f, 0f, 255f);
            }
        }
        return dst;
    }

    /// <summary>Per tap, for each of <paramref name="n2"/> outputs, the source index (of <paramref name="n"/>) it reads.</summary>
    private static int[][] TapIndex(int n2, int n, bool wrap)
    {
        int[][] t = [new int[n2], new int[n2], new int[n2], new int[n2]];
        for (int i = 0; i < n2; i++)
        {
            int i0 = (i >> 1) - ((i & 1) == 0 ? 1 : 0);
            for (int k = 0; k < 4; k++) t[k][i] = Edge(i0 - 1 + k, n, wrap);
        }
        return t;
    }

    /// <summary>For each output, the source neighbour on its side of the nearest texel: the clamp's second column or row.</summary>
    private static int[] NearIndex(int n2, int n, bool wrap)
    {
        int[] t = new int[n2];
        for (int i = 0; i < n2; i++) t[i] = Edge((i >> 1) + ((i & 1) == 0 ? -1 : 1), n, wrap);
        return t;
    }

    /// <summary>Lanczos-2 weights for the four taps around a sample <paramref name="frac"/> past the second, normalised.</summary>
    private static void Taps(float frac, Span<float> k)
    {
        float sum = 0f;
        for (int t = 0; t < 4; t++) { k[t] = Lanczos2(t - 1 - frac); sum += k[t]; }
        for (int t = 0; t < 4; t++) k[t] /= sum;
    }

    private static float Lanczos2(float x)
    {
        if (x == 0f) return 1f;
        if (MathF.Abs(x) >= 2f) return 0f;
        float px = MathF.PI * x;
        return 2f * MathF.Sin(px) * MathF.Sin(px / 2f) / (px * px);
    }

    /// <summary>Channel <paramref name="c"/> of texel (<paramref name="x"/>, <paramref name="y"/>) in a <paramref name="w"/>
    /// wide RGBA8 or RGBA float image.</summary>
    private static int At(int x, int y, int w, int c) => (y * w + x) * 4 + c;

    private static int Edge(int i, int n, bool wrap) => wrap ? ((i % n) + n) % n : Math.Clamp(i, 0, n - 1);

    /// <summary>One box-filtered mip below RGBA8 <paramref name="src"/>.</summary>
    private static byte[] Halve(byte[] src, int w, int h, out int w2, out int h2)
    {
        w2 = Math.Max(w / 2, 1); h2 = Math.Max(h / 2, 1);
        byte[] d = new byte[w2 * h2 * 4];
        for (int y = 0; y < h2; y++)
            for (int x = 0; x < w2; x++)
                for (int c = 0; c < 4; c++)
                {
                    int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                    int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                    d[At(x, y, w2, c)] = (byte)((src[At(x0, y0, w, c)] + src[At(x1, y0, w, c)]
                                                     + src[At(x0, y1, w, c)] + src[At(x1, y1, w, c)] + 2) >> 2);
                }
        return d;
    }

    /// <summary>Off-device: a flat texture stays flat, a step stays inside its two levels, and a hard cutout stays binary.</summary>
    internal static bool UpscaleSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log);
        byte[] flat = new byte[8 * 8 * 4];
        Array.Fill(flat, (byte)120);
        byte[] f2 = Upscale2x(flat, 8, 8, true, false);
        check.Expect(f2.Length == 16 * 16 * 4 && Array.TrueForAll(f2, b => b == 120), "a flat texture upscales flat");
        byte[] step = new byte[8 * 8 * 4];
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 8; x++)
                for (int c = 0; c < 4; c++) step[At(x, y, 8, c)] = x < 4 ? (byte)40 : (byte)200;
        byte[] s2 = Upscale2x(step, 8, 8, false, true);
        bool inside = true, binary = true;
        for (int i = 0; i < s2.Length; i += 4)
        {
            for (int c = 0; c < 3; c++) inside &= s2[i + c] >= 40 - 40 * UpscaleSharpen && s2[i + c] <= 200 + 55;
            binary &= s2[i + 3] is 0 or 255;
        }
        check.Expect(inside, "a step's upscale rings no further than the sharpen allows");
        check.Expect(binary, "a hard cutout's alpha stays 0 or 255");
        // the round trip the loader runs, per format: encode a known image, decode it as the upscale does, upscale,
        // re-encode, decode again -- the colour must come back near the original's and alpha where it was
        foreach (DdsReader.Fmt f in (ReadOnlySpan<DdsReader.Fmt>)[DdsReader.Fmt.Bc1, DdsReader.Fmt.Bc3])
        {
            bool bc3 = f == DdsReader.Fmt.Bc3;
            byte[] img = new byte[32 * 32 * 4];
            for (int y = 0; y < 32; y++)
                for (int x = 0; x < 32; x++)
                {
                    img[At(x, y, 32, 0)] = (byte)(60 + x * 4); img[At(x, y, 32, 1)] = (byte)(90 + y * 3);
                    img[At(x, y, 32, 2)] = 70; img[At(x, y, 32, 3)] = bc3 && x < 8 ? (byte)0 : (byte)255;
                }
            byte[] enc = new byte[BcnEncode.LevelSize(32, 32, bc3)];
            BcnEncode.EncodeLevel(img, 32, 32, bc3, enc);
            byte[] up = Upscale2x(DecodeRgba(enc, 32, 32, f), 32, 32, true, bc3);
            byte[] enc2 = new byte[BcnEncode.LevelSize(64, 64, bc3)];
            BcnEncode.EncodeLevel(up, 64, 64, bc3, enc2);
            byte[] back = DecodeRgba(enc2, 64, 64, f);
            long sum0 = 0, sum1 = 0;
            for (int i = 0; i < img.Length; i += 4) sum0 += img[i] + img[i + 1] + img[i + 2];
            for (int i = 0; i < back.Length; i += 4) sum1 += back[i] + back[i + 1] + back[i + 2];
            double mean0 = sum0 / (32.0 * 32 * 3), mean1 = sum1 / (64.0 * 64 * 3);
            check.Expect(Math.Abs(mean0 - mean1) < 6, $"{f}: an upscaled texture keeps its brightness ({mean0:F1} -> {mean1:F1})");
            check.Expect(back[At(40, 20, 64, 3)] == 255 && (!bc3 || back[At(4, 20, 64, 3)] == 0),
                         $"{f}: alpha stays where it was through the round trip");
        }
        log.Info(check.Ok ? "TexRegistry.UpscaleSelfCheck: passed" : "TexRegistry.UpscaleSelfCheck: FAILED");
        return check.Ok;
    }
}
