using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The mip chain of a texture whose alpha holes cut a surface: built on the decode worker, in place of the
/// device's alpha-blind filter and a file's stored mips. Colour never comes from under a hole, and each level keeps
/// level 0's share of texels past the cut, so the fragment test needs no distance term.</summary>
internal static unsafe class MipChain
{
    /// <summary>One RGBA8 texel, as a chain lays them out.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Texel(byte R, byte G, byte B, byte A);

    /// <summary>The engine's 0.2 discard as a byte: a texel at or under it is a hole.</summary>
    private const int Hole = 51;
    private const int MaxLevels = 16;
    private const int Bpp = 4;
    /// <summary>The texel at column <paramref name="x"/>, row <paramref name="y"/> of a level <paramref name="w"/> wide.</summary>
    internal static int Cell(int w, int x, int y) => y * w + x;

    /// <summary>The whole RGBA8 chain of a sheet with holes; null for one with none.</summary>
    internal static byte[]? Raster(ReadOnlySpan<byte> rgba, int w, int h)
    {
        if (!HasHoles(rgba)) return null;
        var chain = new byte[TexScan.ChainBytes(w, h, true)];
        Build(rgba, w, h, chain);
        return chain;
    }

    /// <summary><see cref="Raster"/> into a native allocation the caller frees, for a decoded job's data to become:
    /// no second managed copy waits beside it for the upload. Null for a sheet with no holes.</summary>
    internal static byte* RasterNative(ReadOnlySpan<byte> rgba, int w, int h, out long bytes)
    {
        bytes = 0;
        if (!HasHoles(rgba)) return null;
        bytes = TexScan.ChainBytes(w, h, true);
        var chain = new NativeBuffer<byte>();
        chain.Take(checked((int)bytes));
        Build(rgba, w, h, chain.Span);
        return chain.Pointer;
    }

    /// <summary>A stored BC3 chain with holes, its mips rewritten in place and its level 0 blocks kept; false when it
    /// stays as stored: no mips, no holes, or another format. Its RGBA working copy is freed before this returns.
    /// ponytail: BC1's decoder drops punch-through alpha; decode it to reach BC1 cut-outs.</summary>
    internal static bool Dds(Span<byte> chain, int w, int h, int levels, DdsReader.Fmt f)
    {
        if (levels < 2 || f != DdsReader.Fmt.Bc3) return false;
        int top = w * h * Bpp;
        long need = TexScan.ChainBytes(w, h, true);
        var scratch = new NativeBuffer<byte>();
        scratch.Take(checked((int)need));
        try { return Rewrite(chain, w, h, levels, f, top, scratch.Span); }
        finally { scratch.Free(); }
    }

    private static bool Rewrite(Span<byte> chain, int w, int h, int levels, DdsReader.Fmt f, int top, Span<byte> work)
    {
        BcnDecode.Unpack(chain[..BcnEncode.LevelSize(w, h, true)], w, h, f, work[..top]);
        if (!HasHoles(work[..top])) return false;
        Build(work[..top], w, h, work);
        long from = top, to = BcnEncode.LevelSize(w, h, true);
        for (int l = 1, lw = Math.Max(1, w >> 1), lh = Math.Max(1, h >> 1); l < levels; l++, lw = Math.Max(1, lw >> 1), lh = Math.Max(1, lh >> 1))
        {
            int size = BcnEncode.LevelSize(lw, lh, true), bytes = lw * lh * Bpp;
            BcnEncode.EncodeLevel(work.Slice((int)from, bytes), lw, lh, true, chain.Slice((int)to, size));
            from += bytes; to += size;
        }
        return true;
    }

    private static bool HasHoles(ReadOnlySpan<byte> rgba)
    {
        foreach (Texel t in MemoryMarshal.Cast<byte, Texel>(rgba)) if (t.A <= Hole) return true;
        return false;
    }

    /// <summary>Level 0 from <paramref name="rgba"/> (it may already sit at the front of <paramref name="chain"/>), the
    /// levels below alpha-weighted, holes filled from above, each level holding level 0's coverage.</summary>
    private static void Build(ReadOnlySpan<byte> rgba, int w, int h, Span<byte> chain)
    {
        ReadOnlySpan<Texel> top = MemoryMarshal.Cast<byte, Texel>(rgba);
        int kept = 0;
        foreach (Texel t in top) if (t.A > Hole) kept++;
        double share = (double)kept / top.Length;
        int* lw = stackalloc int[MaxLevels], lh = stackalloc int[MaxLevels];
        long* off = stackalloc long[MaxLevels];
        int levels = TexScan.Chain(w, h, true, MaxLevels, lw, lh, off, out _);
        rgba.CopyTo(chain);
        fixed (byte* px = chain) TexScan.BuildMips(px, levels, lw, lh, off);
        for (int l = levels - 2; l >= 0; l--)
            FillHoles(MemoryMarshal.Cast<byte, Texel>(chain.Slice((int)off[l], lw[l] * lh[l] * Bpp)), lw[l], lh[l],
                      MemoryMarshal.Cast<byte, Texel>(chain.Slice((int)off[l + 1], lw[l + 1] * lh[l + 1] * Bpp)), lw[l + 1], lh[l + 1]);
        for (int l = 1; l < levels; l++)
            KeepCoverage(MemoryMarshal.Cast<byte, Texel>(chain.Slice((int)off[l], lw[l] * lh[l] * Bpp)), share);
    }

    /// <summary>Off-device: a sheet with no holes keeps the device's chain and a level 0 leaf keeps its texels. Oracle: hand-painted texels.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "MipChain.SelfCheck");
        var stone = new byte[8 * 8 * Bpp];
        MemoryMarshal.Cast<byte, Texel>(stone.AsSpan()).Fill(new Texel(90, 90, 90, 255));
        check.Expect(Raster(stone, 8, 8) == null, "an opaque sheet was rebuilt");
        var edge = new byte[8 * 8 * Bpp];
        MemoryMarshal.Cast<byte, Texel>(edge.AsSpan()).Fill(new Texel(90, 90, 90, Hole + 1));
        check.Expect(Raster(edge, 8, 8) == null, "a sheet with every texel just past the cut was rebuilt");
        var leaf = new byte[8 * 8 * Bpp];
        Span<Texel> t = MemoryMarshal.Cast<byte, Texel>(leaf.AsSpan());
        t.Fill(new Texel(200, 200, 200, 0));
        t[0] = new Texel(10, 20, 30, 255);
        byte[]? chain = Raster(leaf, 8, 8);
        check.Expect(chain != null && chain[0] == 10 && chain[1] == 20 && chain[2] == 30 && chain[3] == 255, "level 0's leaf texel changed");
        check.Expect(chain != null && chain.Length == (64 + 16 + 4 + 1) * Bpp, $"8x8 chain is {chain?.Length} bytes");
        check.Expect(!Dds(stackalloc byte[16], 4, 4, 1, DdsReader.Fmt.Bc3), "a one-level file was rebuilt");
        check.Expect(!Dds(stackalloc byte[16], 4, 4, 3, DdsReader.Fmt.Bc7), "a BC7 file was rebuilt");
        log.Info(check.Ok ? "MipChain.SelfCheck: passed" : "MipChain.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>A hole's colour from the coarser level above it, which holds the covered colour of its whole
    /// neighbourhood: a filter across an edge mixes leaf with leaf, never with whatever was painted under the hole.</summary>
    private static void FillHoles(Span<Texel> level, int w, int h, ReadOnlySpan<Texel> above, int aw, int ah)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int at = Cell(w, x, y);
                if (level[at].A > Hole) continue;
                Texel p = above[Cell(aw, Math.Min(x >> 1, aw - 1), Math.Min(y >> 1, ah - 1))];
                level[at] = new Texel(p.R, p.G, p.B, level[at].A);
            }
    }

    /// <summary>Scales a level's alpha so <paramref name="share"/> of its texels stand past the cut, as they do at level
    /// 0 (Castano, "Computing Alpha Mipmaps"). The histogram names the alpha that lands just past it; no search needed.</summary>
    private static void KeepCoverage(Span<Texel> level, double share)
    {
        Span<int> histogram = stackalloc int[256];
        histogram.Clear();
        foreach (Texel t in level) histogram[t.A]++;
        int want = (int)Math.Round(share * level.Length), have = 0, edge = 256;
        for (int a = 255; a >= 1 && have < want; a--)
        {
            if (want - have < have + histogram[a] - want) break;
            have += histogram[a];
            edge = a;
        }
        const int PastCut = Hole + 1;
        for (int i = 0; i < level.Length; i++)
        {
            Texel t = level[i];
            level[i] = new Texel(t.R, t.G, t.B, (byte)Math.Min(255, t.A * PastCut / edge));
        }
    }
}
