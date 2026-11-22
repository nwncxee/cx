using System;
using System.Runtime.InteropServices;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The padded chain handed to the engine in place of a DDS whose stored mips stop short of 1x1.</summary>
internal static unsafe partial class DdsReader
{
    /// <summary>Deepest chain <see cref="MaxSide"/> allows: 16384 down to 1 is fifteen levels.</summary>
    private const int MaxLevels = 15;

    /// <summary>Copy the chain into <paramref name="dst"/> (<see cref="Info.ChainBytes"/> long). The levels a partial
    /// chain lacks are built from the last stored one, box-filtered and re-encoded, for a 2D BC1 or BC3; any other
    /// format repeats that level's first block, so the engine's unbounded walk still reads memory we own.</summary>
    internal static void CopyChain(ReadOnlySpan<byte> raw, in Info info, Span<byte> dst)
    {
        int bb = BlockBytes(info.Channels);
        long off = 0, lastOff = 0;
        int lastW = info.W, lastH = info.H, lv = 0;
        for (int w = info.W, h = info.H; lv < info.Present; lv++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
        {
            int sz = LevelSize(w, h, info.Channels);
            raw.Slice(info.DataOffset + (int)off, sz).CopyTo(dst.Slice((int)off, sz));
            lastOff = off; lastW = w; lastH = h;
            off += sz;
        }
        if (lv == info.Levels) return;
        if (info.Faces == 1 && (info.Format is Fmt.Bc1 or Fmt.Bc3) && Downsample(dst, info.Format, lastOff, lastW, lastH)) return;
        for (int w = Math.Max(1, lastW >> 1), h = Math.Max(1, lastH >> 1); lv < info.Levels; lv++, w = Math.Max(1, w >> 1), h = Math.Max(1, h >> 1))
        {
            int sz = LevelSize(w, h, info.Channels);
            for (int b = 0; b < sz; b += bb) dst.Slice((int)lastOff, bb).CopyTo(dst.Slice((int)off + b, bb));
            off += sz;
        }
    }

    /// <summary>Every level below the one at <paramref name="at"/> (<paramref name="w"/> x <paramref name="h"/>), written
    /// into <paramref name="chain"/> after it: that level decoded, box-filtered down to 1x1 and encoded back.
    /// False = the chain is too large to stage, and nothing was written.</summary>
    private static bool Downsample(Span<byte> chain, Fmt f, long at, int w, int h)
    {
        bool bc3 = f == Fmt.Bc3;
        int* lw = stackalloc int[MaxLevels], lh = stackalloc int[MaxLevels];
        long* lo = stackalloc long[MaxLevels];
        int n = TexScan.Chain(w, h, true, MaxLevels, lw, lh, lo, out long total);
        var buf = new NativeBuffer<byte>();
        try
        {
            if (total > int.MaxValue || !buf.Take((int)total)) return false;
            Span<byte> all = buf.Span;
            Span<byte> top = all.Slice(0, w * h * 4);
            BcnDecode.Unpack(chain.Slice((int)at, LevelSize(w, h, bc3 ? 4 : 3)), w, h, f, top);
            if (!bc3)
            {
                Span<Rgba8> wide = MemoryMarshal.Cast<byte, Rgba8>(top);
                ReadOnlySpan<Rgb8> rgb = MemoryMarshal.Cast<byte, Rgb8>(top.Slice(0, w * h * 3));
                for (int i = w * h - 1; i >= 0; i--) { Rgb8 c = rgb[i]; wide[i] = new Rgba8(c.R, c.G, c.B); }
            }
            TexScan.BuildMips(buf.Pointer, n, lw, lh, lo);
            long o = at + LevelSize(w, h, bc3 ? 4 : 3);
            for (int l = 1; l < n; l++)
            {
                int sz = BcnEncode.LevelSize(lw[l], lh[l], bc3);
                BcnEncode.EncodeLevel(all.Slice((int)lo[l], lw[l] * lh[l] * 4), lw[l], lh[l], bc3, chain.Slice((int)o, sz));
                o += sz;
            }
            return true;
        }
        finally { buf.Free(); }
    }
}
