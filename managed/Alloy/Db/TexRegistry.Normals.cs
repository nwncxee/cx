using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy.Db;

/// <summary>
/// light.ainormals: a normal map derived from a diffuse that has none, through DeepBump on DirectML (native/ai/cxai.cpp,
/// slot 1). The slot keys as the diffuse at <see cref="NormalCell"/>; it lands a flat normal at once and its background
/// upgrade replaces that with the model's map, kept as BC1 in the disk cache. Where the model cannot run it stays flat.
/// </summary>
internal static unsafe partial class TexRegistry
{
    /// <summary>The key cell that marks a slot as the derived normal map of the diffuse named by its resref.</summary>
    internal const int NormalCell = MaxCells - 1;
    /// <summary>The key cell that marks a slot as the derived HEIGHT map of its resref (light.relief), built with the normals.</summary>
    internal const int HeightCell = MaxCells - 2;
    /// <summary>...and its derived ROUGHNESS map (light.aimaterial), from the same run.</summary>
    internal const int RoughCell = MaxCells - 3;
    /// <summary>The stock generated roughness (cx_matsynth's CX_ROUGH_MAX) as a texel: a bare surface's look, the placeholder.</summary>
    private const byte RoughStock = 140;
    /// <summary>Gauss-Seidel sweeps of the height solve: at the coarse level, then at full size from its upsample.</summary>
    private const int HeightCoarseSweeps = 300, HeightFineSweeps = 40, HeightCoarseSide = 128;

    private const string NormalModelFile = "cx_normals.onnx";
    private const int AiSlotNormals = 1;
    /// <summary>The model's fixed tile, read at half-tile steps and feathered, so no seam is left at a tile edge.</summary>
    private const int NormalTile = 256, NormalStep = NormalTile / 2;
    /// <summary>The largest side the map is built at: a larger diffuse is halved first (a normal map needs less).</summary>
    private const int NormalMaxSide = 1024;
    /// <summary>Bumped whenever the derived map changes, so no older cache file is read.</summary>
    private const ulong NormalVersion = 1;
    /// <summary>The flat tangent-space normal, as stored.</summary>
    private const byte NormalFlatXY = 128, NormalFlatZ = 255;
    private const int NormalFlatSide = 4;

    private static readonly AiState _aiN = new();

    /// <summary>Job <paramref name="j"/>, a derived-normal slot: its cached map, or a flat one and a queued build.
    /// Only a DDS (BC1 or BC3) or a TGA diffuse is read; anything else fails the slot, which then binds nothing.</summary>
    private static void DecodeNormal(Job j, ReadOnlySpan<byte> raw)
    {
        j.Cov = TexCoverage.Opaque;
        j.CovWhy = "derived normal map (light.ainormals) -> Opaque, unscanned";
        j.Cuts = false;
        bool plt = j.Kind == TexKind.Plt;
        // water is found by its .txi, after the recipe asked: its own ripple maps are the surface
        if (j.BumpyShiny) { j.Why = "water takes no derived normal map"; return; }
        byte[] src;
        int w, h;
        DdsReader.Fmt fmt = DdsReader.Fmt.None;
        if (plt)
        {
            // armour and clothing: the PLT's intensity channel is the surface's own shading, which the model reads as relief
            byte* rg = PltImage.Decode(raw, out w, out h, out _, out string? pw);
            if (rg == null) { j.Why = pw ?? "not a PLT"; return; }
            ReadOnlySpan<byte> texels = new(rg, w * h * PltImage.BytesPerPixel);
            src = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    byte v = texels[TexelOf(x, y, w, PltImage.BytesPerPixel, 0)];
                    src[At(x, y, w, 0)] = v; src[At(x, y, w, 1)] = v; src[At(x, y, w, 2)] = v; src[At(x, y, w, 3)] = 255;
                }
            NativeMemory.Free(rg);
        }
        else if (j.Loc.ResType == TgaResType)
        {
            byte* px = RasterReader.PrepareTga(raw, out RasterReader.Raster r, out string? tw);
            if (px == null) { j.Why = tw ?? "the TGA was refused"; return; }
            w = r.W; h = r.H;
            src = new byte[w * h * 4];
            ReadOnlySpan<byte> sheet = new(px, r.Bytes);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    for (int c = 0; c < 4; c++)
                        src[At(x, y, w, c)] = c == 3 ? (byte)255 : sheet[TexelOf(x, y, w, r.Px, Math.Min(c, r.Px - 1))];
            NativeMemory.Free(px);
        }
        else
        {
            if (!DdsReader.Parse(raw, out DdsReader.Info info, out string? dw)) { j.Why = dw ?? "the DDS was refused"; return; }
            if (info.Faces != 1 || info.Format is not (DdsReader.Fmt.Bc1 or DdsReader.Fmt.Bc3))
            { j.Why = $"{info.Format} takes no derived normal map"; return; }
            w = info.W; h = info.H; fmt = info.Format;
            src = raw.Slice(info.DataOffset, DdsReader.LevelSize(w, h, info.Channels)).ToArray();
        }
        if (Math.Min(w, h) < UpscaleMinSide) { j.Why = "too small to take a derived normal map"; return; }
        ulong key = NormalKey(src, w, h, j.Clamp);
        if (!NormalCached(j, key)) NormalFlat(j, src, w, h, fmt, key);
        if (j.Height) j.CovWhy = "derived height map (light.relief) -> Opaque, unscanned";
    }

    private static ulong NormalKey(byte[] src, int w, int h, bool clamp)
    {
        ulong k = 14695981039346656037UL;
        foreach (byte b in src) k = (k ^ b) * 1099511628211UL;
        foreach (ulong t in (ReadOnlySpan<ulong>)[NormalVersion, (ulong)w, (ulong)h, clamp ? 1UL : 0])
            k = (k ^ t) * 1099511628211UL;
        return k;
    }

    private static string NormalPath(string resref, ulong key, bool height = false, bool rough = false)
        => System.IO.Path.Combine(UserFolders.Cache, "textures", $"{resref.ToLowerInvariant()}-{key:x16}.{(rough ? "rgh" : height ? "hgt" : "nrm")}");

    /// <summary>The cached BC1 chain on <paramref name="j"/>; false when the cache does not hold it.</summary>
    private static bool NormalCached(Job j, ulong key)
    {
        if (CacheFile.Read(NormalPath(j.Resref, key, j.Height, j.Rough)) is not { Length: > UpscaleHeader } blob) return false;
        int w = BitConverter.ToInt32(blob, 0), h = BitConverter.ToInt32(blob, 4), levels = BitConverter.ToInt32(blob, 8);
        if (w <= 0 || h <= 0 || levels != Levels(w, h)) return false;
        NormalChain(j, blob.AsSpan(UpscaleHeader), w, h, levels);
        return true;
    }

    /// <summary>A BC1 chain as <paramref name="j"/>'s upload.</summary>
    private static void NormalChain(Job j, ReadOnlySpan<byte> bc1, int w, int h, int levels)
    {
        var chain = new NativeBuffer<byte>();
        chain.TakeExact(bc1.Length);
        bc1.CopyTo(chain.Span);
        j.Data = chain.Pointer; j.Native = true;
        j.W = w; j.H = h; j.Levels = levels; j.Bytes = chain.Length;
        j.Channels = 3; j.GlFmt = DdsReader.GlFormat(DdsReader.Fmt.Bc1); j.Compressed = true; j.GenMips = false;
    }

    /// <summary>A flat normal as <paramref name="j"/>'s upload, and the build of the real one queued behind it.</summary>
    private static void NormalFlat(Job j, byte[] src, int w, int h, DdsReader.Fmt fmt, ulong key)
    {
        var flat = new NativeBuffer<byte>();
        flat.TakeExact(NormalFlatSide * NormalFlatSide * 4);
        Span<byte> px = flat.Span;
        for (int y = 0; y < NormalFlatSide; y++)
            for (int x = 0; x < NormalFlatSide; x++)
            {
                // a flat normal, or a height map at its top everywhere: no relief, so nothing slides before the real one lands
                byte xy = j.Rough ? RoughStock : j.Height ? (byte)255 : NormalFlatXY;
                px[At(x, y, NormalFlatSide, 0)] = xy; px[At(x, y, NormalFlatSide, 1)] = xy;
                px[At(x, y, NormalFlatSide, 2)] = j.Rough ? RoughStock : NormalFlatZ; px[At(x, y, NormalFlatSide, 3)] = 255;
            }
        j.Data = flat.Pointer; j.Native = true; j.W = j.H = NormalFlatSide; j.Levels = Levels(NormalFlatSide, NormalFlatSide);
        j.Channels = 4; j.GlFmt = EngineTokens.RGBA8; j.Compressed = false; j.GenMips = true; j.Bytes = flat.Length;
        if (!Lighting.AiNormals.Boot) return;
        Job u = Rent();
        u.Slot = j.Slot; u.Gen = j.Gen; u.Revision = j.Revision; u.Resref = j.Resref; u.Kind = j.Kind; u.Loc = j.Loc;
        u.Data = null; u.Chained = false; u.Cuts = false; u.DataTexture = false; u.Why = null;
        u.W = w; u.H = h; u.Levels = 0; u.Bytes = 0; u.Channels = 0; u.GlFmt = 0;
        u.Compressed = true; u.GenMips = false; u.Clamp = j.Clamp; u.Nearest = j.Nearest; u.Mip = j.Mip;
        u.Cov = j.Cov; u.CovWhy = j.CovWhy; u.Mean = default; u.MeanOk = false; u.BumpyShiny = false; u.EnvMap = false;
        u.Fast = false; u.Tail = false; u.Native = false; u.GridX = 1; u.GridY = 1; u.FullW = 0; u.Cycle = false;
        u.Pixels = null; u.Arturo = default; u.Ripple = null;
        u.Upgrade = true; u.Normal = true; u.Height = j.Height; u.Rough = j.Rough; u.UpSource = src; u.UpFmt = fmt; u.UpKey = key;
        lock (_gate) { _inUp.Enqueue(u); Monitor.PulseAll(_gate); }
    }

    /// <summary>The background build: the source halved to <see cref="NormalMaxSide"/>, the model run over it, the BC1
    /// chain cached and left on the job. A model that cannot run fails the upgrade and the flat map stays.</summary>
    private static void NormalWork(Job j)
    {
        // a refused model fails the rest unread
        if (_aiN.State == AiRefused) { j.UpSource = null; j.Why = $"the AI normal maps are off -- {_aiN.Why}"; return; }
        // the twin slot's build may have written this one's map already: one model run makes both
        if (NormalCached(j, j.UpKey)) { j.UpSource = null; j.UpNote = j.Height ? "height map from the cache" : "normal map from the cache"; return; }
        byte[] src = j.UpFmt == DdsReader.Fmt.None ? j.UpSource! : DecodeRgba(j.UpSource, j.W, j.H, j.UpFmt);
        j.UpSource = null;
        int w = j.W, h = j.H;
        while (Math.Max(w, h) > NormalMaxSide) src = Halve(src, w, h, out w, out h);
        byte[]? level;
        lock (_aiN)
        {
            if (!NormalAiReady()) { j.Why = $"the AI normal maps are off -- {_aiN.Why}"; return; }
            level = NormalRun(src, w, h, !j.Clamp);
        }
        if (level == null) { j.Why = "a tile did not run"; return; }
        byte[] height = HeightFromNormals(level, w, h, !j.Clamp), rough = RoughFromMaps(level, src, w, h, !j.Clamp);
        byte[] nrmFile = ChainFile(level, w, h), hgtFile = ChainFile(height, w, h), rghFile = ChainFile(rough, w, h);
        foreach ((byte[] f, bool isHeight, bool isRough) in (ReadOnlySpan<(byte[], bool, bool)>)[(nrmFile, false, false), (hgtFile, true, false), (rghFile, false, true)])
        {
            string at = NormalPath(j.Resref, j.UpKey, isHeight, isRough);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(at)!);
            CacheFile.Write(at, f);
        }
        byte[] mine = j.Rough ? rghFile : j.Height ? hgtFile : nrmFile;
        NormalChain(j, mine.AsSpan(UpscaleHeader), w, h, Levels(w, h));
        j.UpNote = $"{(j.Rough ? "roughness" : j.Height ? "height" : "normal")} map derived at {w}x{h} (DeepBump)";
    }

    /// <summary>A whole BC1 chain of RGBA8 <paramref name="level"/> in the cache's file layout (size, levels, chain).</summary>
    private static byte[] ChainFile(byte[] level, int w, int h)
    {
        int levels = Levels(w, h);
        long bytes = 0;
        for (int l = 0, lw = w, lh = h; l < levels; l++, lw = Math.Max(lw / 2, 1), lh = Math.Max(lh / 2, 1))
            bytes += BcnEncode.LevelSize(lw, lh, false);
        byte[] file = new byte[UpscaleHeader + bytes];
        BitConverter.TryWriteBytes(file.AsSpan(0, 4), w);
        BitConverter.TryWriteBytes(file.AsSpan(4, 4), h);
        BitConverter.TryWriteBytes(file.AsSpan(8, 4), levels);
        Span<byte> chainOut = file.AsSpan(UpscaleHeader);
        int at = 0;
        // box mips shorten a bumpy texel's xy, which the shader reads as flatter: the right answer far away
        for (int l = 0, lw = w, lh = h; l < levels; l++)
        {
            int n = BcnEncode.LevelSize(lw, lh, false);
            BcnEncode.EncodeLevel(level, lw, lh, false, chainOut.Slice(at, n));
            at += n;
            if (l + 1 < levels) level = Halve(level, lw, lh, out lw, out lh);
        }
        return file;
    }

    /// <summary>The height field RGBA8 normal map <paramref name="nrm"/> describes, white at its top: the slopes integrated by
    /// a Poisson solve, coarse first and then refined at full size, wrapping where the texture tiles.</summary>
    private static byte[] HeightFromNormals(byte[] nrm, int w, int h, bool wrap)
    {
        byte[] coarse = nrm;
        int cw = w, ch = h;
        while (Math.Max(cw, ch) > HeightCoarseSide) coarse = Halve(coarse, cw, ch, out cw, out ch);
        float[] hc = new float[cw * ch];
        Solve(hc, Divergence(coarse, cw, ch, wrap), cw, ch, wrap, HeightCoarseSweeps);
        // the coarse answer up to full size as the start, in texel units of the finer grid
        float[] hf = new float[w * h];
        float k = (float)w / cw;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                hf[Px(x, y, w)] = hc[Px(Math.Min(x * cw / w, cw - 1), Math.Min(y * ch / h, ch - 1), cw)] * k;
        Solve(hf, Divergence(nrm, w, h, wrap), w, h, wrap, HeightFineSweeps);
        // to 0..1 over the 1st..99th percentile, so one deep pit does not flatten the rest
        float[] sorted = (float[])hf.Clone();
        Array.Sort(sorted);
        float lo = sorted[sorted.Length / 100], hi = sorted[sorted.Length - 1 - sorted.Length / 100];
        float span = MathF.Max(hi - lo, 1e-4f);
        byte[] dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte v = (byte)Math.Clamp((hf[Px(x, y, w)] - lo) / span * 255f + 0.5f, 0f, 255f);
                dst[At(x, y, w, 0)] = v; dst[At(x, y, w, 1)] = v; dst[At(x, y, w, 2)] = v; dst[At(x, y, w, 3)] = 255;
            }
        return dst;
    }

    private static int Px(int x, int y, int w) => y * w + x;

    /// <summary>A roughness map from the derived normals and the colour: bumpy, creviced texels matte, smooth and bright ones
    /// glossier -- worn stone and plaster against polished flags and fittings -- between <see cref="RoughLo"/> and <see cref="RoughHi"/>.</summary>
    private static byte[] RoughFromMaps(byte[] nrm, byte[] alb, int w, int h, bool wrap)
    {
        byte[] dst = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float bump = 0f;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                        bump += 1f - (nrm[At(Edge(x + dx, w, wrap), Edge(y + dy, h, wrap), w, 2)] / 127.5f - 1f);
                bump /= 9f;
                int o = At(x, y, w, 0);
                float lum = (0.2126f * alb[o] + 0.7152f * alb[o + 1] + 0.0722f * alb[o + 2]) / 255f;
                float r = Math.Clamp(RoughBase + RoughBump * bump + RoughDark * (1f - lum), RoughLo, RoughHi);
                byte v = (byte)(r * 255f + 0.5f);
                dst[o] = v; dst[o + 1] = v; dst[o + 2] = v; dst[o + 3] = 255;
            }
        return dst;
    }

    private const float RoughBase = 0.32f, RoughBump = 4f, RoughDark = 0.25f, RoughLo = 0.28f, RoughHi = 0.9f;

    /// <summary>The divergence of the height gradient a normal map encodes: dh/du = -nx/nz, dh/dv = -ny/nz (green points down
    /// the image, as the shader's frame reads it).</summary>
    private static float[] Divergence(byte[] nrm, int w, int h, bool wrap)
    {
        float[] gx = new float[w * h], gy = new float[w * h], div = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float nx = nrm[At(x, y, w, 0)] / 127.5f - 1f, ny = nrm[At(x, y, w, 1)] / 127.5f - 1f;
                float nz = MathF.Max(nrm[At(x, y, w, 2)] / 127.5f - 1f, 0.2f);
                gx[Px(x, y, w)] = -nx / nz; gy[Px(x, y, w)] = -ny / nz;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                div[Px(x, y, w)] = gx[Px(x, y, w)] - gx[Px(Edge(x - 1, w, wrap), y, w)] + gy[Px(x, y, w)] - gy[Px(x, Edge(y - 1, h, wrap), w)];
        return div;
    }

    /// <summary><paramref name="sweeps"/> Gauss-Seidel passes of the Poisson equation (the Laplacian of h is <paramref name="div"/>).</summary>
    private static void Solve(float[] hgt, float[] div, int w, int h, bool wrap, int sweeps)
    {
        for (int it = 0; it < sweeps; it++)
            for (int y = 0; y < h; y++)
            {
                int yu = Edge(y - 1, h, wrap), yd = Edge(y + 1, h, wrap);
                for (int x = 0; x < w; x++)
                {
                    float sum = hgt[Px(Edge(x - 1, w, wrap), y, w)] + hgt[Px(Edge(x + 1, w, wrap), y, w)]
                              + hgt[Px(x, yu, w)] + hgt[Px(x, yd, w)];
                    hgt[Px(x, y, w)] = (sum - div[Px(x, y, w)]) * 0.25f;
                }
            }
    }

    /// <summary>The normal model, opened on first use. Caller holds <see cref="_aiN"/>.</summary>
    private static bool NormalAiReady()
    {
        if (_aiN.State == AiUp) return true;
        if (_aiN.State != AiUntried) return false;
        _aiN.State = AiRefused;
        if (!OperatingSystem.IsWindows()) { _aiN.Why = "Windows-only in this build"; return NormalSaid(); }
        string dir = VulkanToolchain.InstallFolder;
        string model = System.IO.Path.Combine(dir, NormalModelFile);
        if (!System.IO.File.Exists(model)) { _aiN.Why = $"{NormalModelFile} is not beside version.dll"; return NormalSaid(); }
        int e;
        try { e = cxai_open(AiSlotNormals, dir, model); }
        catch (Exception x) when (x is DllNotFoundException or EntryPointNotFoundException)
        { _aiN.Why = "this build was made without the AI runtime"; return NormalSaid(); }
        if (e != 0) { _aiN.Why = AiOpenWhy(e, NormalModelFile); return NormalSaid(); }
        _aiN.State = AiUp;
        _log?.Always("TexRegistry: the AI normal maps are up (DeepBump on DirectML)");
        return true;
    }

    private static bool NormalSaid()
    {
        _log?.Always($"TexRegistry: the AI normal maps are OFF -- {_aiN.Why}. Surfaces keep their stock shading.");
        return false;
    }

    /// <summary>RGBA8 <paramref name="src"/>'s normal map, RGBA8 with x, y, z in RGB and green pointing down the image as
    /// the shader's frame reads it. Caller holds <see cref="_aiN"/>; null when a tile failed.</summary>
    private static byte[]? NormalRun(byte[] src, int w, int h, bool wrap)
    {
        const int t = NormalTile;
        float[] input = new float[t * t], output = new float[3 * t * t];
        float[] sum = new float[w * h * 4];
        for (int ty = -NormalStep; ty < h; ty += NormalStep)
            for (int tx = -NormalStep; tx < w; tx += NormalStep)
            {
                // the model reads brightness as height
                for (int y = 0; y < t; y++)
                {
                    int sy = Edge(ty + y, h, wrap);
                    for (int x = 0; x < t; x++)
                    {
                        int o = At(Edge(tx + x, w, wrap), sy, w, 0);
                        input[Plane(0, x, y, t)] = (src[o] + src[o + 1] + src[o + 2]) / (3f * 255f);
                    }
                }
                if (cxai_run(AiSlotNormals, input, 1, t, t, output, output.Length) != 0) return null;
                for (int y = 0; y < t; y++)
                {
                    int oy = ty + y;
                    if (!wrap && (oy < 0 || oy >= h)) continue;
                    oy = Edge(oy, h, true);
                    float wy = Feather(y);
                    for (int x = 0; x < t; x++)
                    {
                        int ox = tx + x;
                        if (!wrap && (ox < 0 || ox >= w)) continue;
                        ox = Edge(ox, w, true);
                        float k = wy * Feather(x);
                        for (int c = 0; c < 3; c++) sum[At(ox, oy, w, c)] += k * (output[Plane(c, x, y, t)] * 2f - 1f);
                    }
                }
            }
        byte[] dst = new byte[w * h * 4];
        for (int p = 0; p < sum.Length; p += 4)
        {
            float nx = sum[p], ny = -sum[p + 1], nz = MathF.Max(sum[p + 2], 1e-3f);   // OpenGL green flipped
            float len = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
            dst[p] = NormalByte(nx / len); dst[p + 1] = NormalByte(ny / len); dst[p + 2] = NormalByte(nz / len);
            dst[p + 3] = 255;
        }
        return dst;
    }

    /// <summary>A tent across the tile, so overlapping tiles cross-fade; the sum is normalised, so its scale does not matter.</summary>
    private static float Feather(int i) => MathF.Max(1f - MathF.Abs((i + 0.5f) / NormalStep - 1f), 1e-3f);

    /// <summary>Channel <paramref name="c"/> of texel (<paramref name="x"/>, <paramref name="y"/>) in a raster of
    /// <paramref name="px"/> bytes a texel.</summary>
    private static int TexelOf(int x, int y, int w, int px, int c) => (y * w + x) * px + c;

    private static byte NormalByte(float v) => (byte)Math.Clamp(v * 127.5f + 128f, 0f, 255f);

    /// <summary>cxai_open's stage code in words.</summary>
    private static string AiOpenWhy(int e, string file) => e switch
    {
        1 => "onnxruntime.dll is not beside version.dll",
        6 => "DirectML found no Direct3D 12 GPU",
        7 => $"the runtime refused {file}",
        _ => $"the runtime would not start (stage {e})",
    };
}
