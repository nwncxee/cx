using System;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The chain a cut-out sheet goes up with: an alpha-weighted one, built here. The device's own filter and
/// a file's stored mips both average colour across the holes, so a minified grass blade or leaf draws a share of
/// whatever the art hides under its transparent texels.</summary>
internal static unsafe partial class OwnedTextures
{
    /// <summary>An uncompressed cell's own chain instead of the device's filter. True = this sheet cuts and its
    /// chain is what went up, and <see cref="VkTextures.Has"/> says whether the device took every level; false =
    /// a sheet with no holes, which the caller mips its own way.</summary>
    internal static bool MirrorRasterChain(VkBackend vk, uint dst, ImageId id, ReadOnlySpan<byte> level0, int w, int h, int levels)
    {
        if (levels < 2 || MipChain.Raster(level0, w, h) is not byte[] chain) return false;
        if (UploadChain(vk, dst, id, chain, Format.Rgba8, w, h, levels)) _borrow.CutoutChains++;
        return true;
    }

    /// <summary>A stored compressed chain, its mips rewritten alpha-weighted first when the sheet cuts. False = the
    /// device refused a level and the name is forgotten.</summary>
    internal static bool MirrorStoredChain(VkBackend vk, uint dst, ImageId id, Span<byte> chain, int w, int h, int levels)
    {
        if (MipChain.Dds(chain, w, h, levels, DdsReader.Fmt.Bc3)) _borrow.CutoutChains++;
        return UploadChain(vk, dst, id, chain, Format.Bc3, w, h, levels);
    }

    /// <summary>Every level of <paramref name="chain"/> into <paramref name="id"/>, level 0 first. A level the chain
    /// is too short for, or one the device refuses, forgets the name: half a chain is not a texture.</summary>
    private static bool UploadChain(VkBackend vk, uint dst, ImageId id, ReadOnlySpan<byte> chain, Format f, int w, int h, int levels)
    {
        long at = 0;
        for (int l = 0, lw = w, lh = h; l < levels; l++, lw = Math.Max(1, lw >> 1), lh = Math.Max(1, lh >> 1))
        {
            int size = ChainLevel(f, lw, lh);
            if (at + size > chain.Length || !vk.Upload(id, l, 0, chain.Slice((int)at, size)))
            {
                VkTextures.Forget(dst);
                return false;
            }
            at += size;
        }
        return true;
    }

    /// <summary>One level's bytes as a chain lays them out: a BC3 block grid, or four channels a texel.</summary>
    private static int ChainLevel(Format f, int w, int h) => f == Format.Bc3 ? BcnEncode.LevelSize(w, h, true) : w * h * 4;

    /// <summary>The engine's compressed levels as one chain, each read from behind its own header run. False when a
    /// level is not the size BC3 gives it, which is a layout the rebuild does not transcribe; the buffer is the
    /// caller's to free either way.</summary>
    private static bool StoredChain(CAurTexture t, int w, int h, int levels, ref NativeBuffer<byte> buf)
    {
        long total = 0;
        for (int l = 0, lw = w, lh = h; l < levels; l++, lw = Math.Max(1, lw >> 1), lh = Math.Max(1, lh >> 1))
            total += BcnEncode.LevelSize(lw, lh, true);
        if (total > int.MaxValue || !buf.Take((int)total)) return false;
        long at = 0;
        for (int l = 0; l < levels; l++)
        {
            ReadOnlySpan<byte> level = t.CompressedBytes(l, out int lw, out int lh);
            if (level.Length != BcnEncode.LevelSize(lw, lh, true)) return false;
            level.CopyTo(buf.Span.Slice((int)at, level.Length));
            at += level.Length;
        }
        return true;
    }
}
