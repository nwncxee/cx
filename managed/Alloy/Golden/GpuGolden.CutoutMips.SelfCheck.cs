using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// A cut-out atlas's mips on the device: the owned upload's own chain against the driver's filter and against a
/// file's stored chain, over a card sheet painted here whose transparent texels hide a pale matte. Oracles: the
/// sheet's own leaf colour and its hand-counted coverage, and the driver's answer for the alpha-blind chains --
/// which this reads first, so the fixture is the reported bug and not a shape that passes either way.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The fixture's side; 64 leaves a level 2 big enough to count texels on.</summary>
    private const int CutSide = 64;
    /// <summary>The level the blades of a mid-field card sample, and the one this reads back.</summary>
    private const int CutLevel = 2;
    private const byte CutLeafR = 40, CutLeafG = 90, CutLeafB = 40, CutMatteR = 230, CutMatteG = 220, CutMatteB = 180;
    /// <summary>Alpha at or under which a texel is a hole, as the engine's 0.2 discard reads it.</summary>
    private const int CutHole = 51;
    /// <summary>Red a kept texel may reach: the leaf's own plus what rounding and a BCn endpoint fit add.</summary>
    private const int CutLeafLimit = CutLeafR + 12;
    /// <summary>Texels the leaf covers: one diagonal stripe in three.</summary>
    private const float CutCoverage = 1f / 3f;

    private static void CutoutMipsSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        int levels = MipLevels(CutSide, CutSide);
        byte[] sheet = CutSheet(onlyLeaf: false);
        int side = CutSide >> CutLevel;

        // Given a grass atlas whose transparent texels hide a dark-against-leaf matte
        // When the device fills the chain with its own alpha-blind filter
        // Then a kept texel at the level a mid-field blade samples carries the matte: the bug, on this driver
        byte[]? device = CutLevelRgba(vk, check, log, "device mips", levels, img =>
        {
            if (!vk.Upload(img, 0, 0, sheet)) return false;
            vk.GenerateMips(img);
            return true;
        });
        int deviceRed = device == null ? 0 : CutBrightest(device, side);
        if (device != null)
            check.Expect(deviceRed > CutLeafLimit,
                $"cut-out mips: the driver's own level {CutLevel} keeps the leaf's red ({deviceRed}), so this "
              + "fixture does not reproduce the darkening the rebuild is for");

        // When the owned upload builds the sheet's chain instead
        // Then every kept texel at that level is the leaf's own colour, and the card keeps its share of texels
        byte[]? own = CutLevelRgba(vk, check, log, "owned chain", levels,
                                   img => OwnedTextures.MirrorRasterChain(vk, 0, img, sheet, CutSide, CutSide, levels));
        int ownRed = own == null ? 0 : CutBrightest(own, side);
        float ownKept = own == null ? 0f : CutKept(own);
        if (own != null)
        {
            check.Expect(ownRed <= CutLeafLimit,
                $"cut-out mips: a kept texel of the owned chain's level {CutLevel} is red {ownRed}, past the leaf's {CutLeafR}");
            check.Expect(Math.Abs(ownKept - CutCoverage) <= 0.1f,
                $"cut-out mips: level {CutLevel} keeps {ownKept:F2} of its texels, not about {CutCoverage:F2}");
        }

        // Given a sheet with no holes at all
        // When it comes through the same upload
        // Then it is left to the device, as it was before the rebuild
        byte[] slab = CutSheet(onlyLeaf: true);
        ImageId plain = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, CutSide, CutSide, 1, levels, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden cutout mips opaque"));
        check.Expect(!plain.IsNull && !OwnedTextures.MirrorRasterChain(vk, 0, plain, slab, CutSide, CutSide, levels),
                     "cut-out mips: a sheet with no holes was given a chain of its own");
        vk.Destroy(plain);

        CutoutStoredSelfCheck(vk, check, log, sheet, levels, side, out int storedRed, out int bc3Red);
        log.Info($"GpuGolden: cut-out mips, level {CutLevel} kept red {ownRed} against the driver's {deviceRed} over a leaf of "
               + $"{CutLeafR}, coverage {ownKept:F2}; stored BC3 {storedRed}, rewritten {bc3Red}");
    }

    /// <summary>The same sheet as a file's stored BC3 chain, box-filtered over its holes the way a texture tool
    /// leaves it: the stored mips read pale through the driver's decoder, the rewritten ones do not.</summary>
    private static void CutoutStoredSelfCheck(VkBackend vk, Checker check, ExtenderLog log, byte[] sheet, int levels, int side,
                                              out int storedRed, out int bc3Red)
    {
        bc3Red = 0;
        byte[] stored = CutStoredBc3(sheet, levels);
        var asStored = new byte[side * side * 4];
        long at = 0;
        for (int l = 0, w = CutSide; l < CutLevel; l++, w >>= 1) at += BcnEncode.LevelSize(w, w, true);
        BcnDecode.Unpack(stored.AsSpan((int)at, BcnEncode.LevelSize(side, side, true)), side, side, DdsReader.Fmt.Bc3, asStored);
        storedRed = CutBrightest(asStored, side);
        check.Expect(storedRed > CutLeafLimit,
            $"cut-out mips: the stored BC3 level {CutLevel} keeps the leaf's red ({storedRed}), so the stored "
          + "chain does not reproduce the darkening either");

        byte[] rewritten = (byte[])stored.Clone();
        byte[]? blocks = CutLevelBlocks(vk, check, log, levels, side,
                                        img => OwnedTextures.MirrorStoredChain(vk, 0, img, rewritten, CutSide, CutSide, levels));
        if (blocks == null) return;
        var got = new byte[side * side * 4];
        BcnDecode.Unpack(blocks, side, side, DdsReader.Fmt.Bc3, got);
        bc3Red = CutBrightest(got, side);
        check.Expect(bc3Red <= CutLeafLimit,
            $"cut-out mips: a kept texel of the uploaded BC3 level {CutLevel} is red {bc3Red}, past the leaf's {CutLeafR}");
    }

    /// <summary>Level <see cref="CutLevel"/> of an RGBA8 texture <paramref name="fill"/> filled, read back through
    /// the device; null when a resource, the fill or the read was refused (which <paramref name="check"/> says).</summary>
    private static byte[]? CutLevelRgba(VkBackend vk, Checker check, ExtenderLog log, string what, int levels, Func<ImageId, bool> fill)
    {
        int side = CutSide >> CutLevel;
        ImageId img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, CutSide, CutSide, 1, levels, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, $"golden cutout mips {what}"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(side * side * 4, BufferUse.CopyDst | BufferUse.CopySrc,
                                                    MemoryKind.Device, $"golden cutout mips {what} read"));
        try
        {
            check.Expect(!img.IsNull && !rb.IsNull, $"cut-out mips: the {what} texture or its readback was refused (logged above)");
            if (img.IsNull || rb.IsNull) return null;
            // A refused fill is the regression this golden is for: the rebuild declining leaves nothing to read.
            bool filled = fill(img);
            check.Expect(filled, $"cut-out mips: the {what} fill did not run -- its levels never reached the device");
            if (!filled) return null;
            var cl = new CommandList($"golden cutout mips {what}", 1 << 10, log);
            cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(CutLevel, 0, 0, 0), side, side, rb, 0));
            var got = new byte[side * side * 4];
            if (vk.Submit(cl) && vk.ReadBack(rb, got)) return got;
            check.Expect(false, $"cut-out mips: the {what} fill or its readback was refused");
            return null;
        }
        finally { vk.Destroy(rb); vk.Destroy(img); }
    }

    /// <summary>Level <see cref="CutLevel"/>'s BC3 blocks off a compressed texture <paramref name="fill"/> filled.</summary>
    private static byte[]? CutLevelBlocks(VkBackend vk, Checker check, ExtenderLog log, int levels, int side, Func<ImageId, bool> fill)
    {
        ImageId img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Bc3, CutSide, CutSide, 1, levels, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden cutout mips stored"));
        int bytes = BcnEncode.LevelSize(side, side, true);
        BufferId rb = vk.CreateBuffer(new BufferDesc(bytes, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device,
                                                    "golden cutout mips stored read"));
        try
        {
            check.Expect(!img.IsNull && !rb.IsNull, "cut-out mips: the stored-chain texture or its readback was refused (logged above)");
            if (img.IsNull || rb.IsNull) return null;
            check.Expect(fill(img), "cut-out mips: the stored chain's upload was refused");
            var cl = new CommandList("golden cutout mips stored", 1 << 10, log);
            cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(CutLevel, 0, 0, 0), side, side, rb, 0));
            var got = new byte[bytes];
            if (vk.Submit(cl) && vk.ReadBack(rb, got)) return got;
            check.Expect(false, "cut-out mips: the stored chain's readback was refused");
            return null;
        }
        finally { vk.Destroy(rb); vk.Destroy(img); }
    }

    /// <summary>Leaves in diagonal stripes on a pale matte the holes hide, the shape of a grass card;
    /// <paramref name="onlyLeaf"/> paints the same sheet with no holes in it.</summary>
    private static byte[] CutSheet(bool onlyLeaf)
    {
        var px = new byte[CutSide * CutSide * 4];
        Span<CX.Alloy.Db.MipChain.Texel> t = MemoryMarshal.Cast<byte, CX.Alloy.Db.MipChain.Texel>(px.AsSpan());
        for (int y = 0; y < CutSide; y++)
            for (int x = 0; x < CutSide; x++)
                t[CX.Alloy.Db.MipChain.Cell(CutSide, x, y)] = onlyLeaf || (x + y) % 3 == 0
                    ? new CX.Alloy.Db.MipChain.Texel(CutLeafR, CutLeafG, CutLeafB, 255)
                    : new CX.Alloy.Db.MipChain.Texel(CutMatteR, CutMatteG, CutMatteB, 0);
        return px;
    }

    /// <summary>The sheet as a stored BC3 chain, every level the plain box average of the one above it -- holes
    /// and all, which is what a texture tool writes.</summary>
    private static byte[] CutStoredBc3(byte[] sheet, int levels)
    {
        long total = 0;
        for (int l = 0, w = CutSide; l < levels; l++, w = Math.Max(1, w >> 1)) total += BcnEncode.LevelSize(w, w, true);
        var o = new byte[total];
        byte[] level = (byte[])sheet.Clone();
        long at = 0;
        for (int l = 0, w = CutSide; l < levels; l++, w = Math.Max(1, w >> 1))
        {
            int size = BcnEncode.LevelSize(w, w, true);
            BcnEncode.EncodeLevel(level.AsSpan(0, w * w * 4), w, w, true, o.AsSpan((int)at, size));
            at += size;
            if (w == 1) break;
            level = CutBox(level, w);
        }
        return o;
    }

    /// <summary>One box-average halving of an RGBA8 level, alpha averaged with the colour as a tool averages it.</summary>
    private static byte[] CutBox(byte[] level, int w)
    {
        int n = w / 2;
        var next = new byte[n * n * 4];
        ReadOnlySpan<CX.Alloy.Db.MipChain.Texel> from = MemoryMarshal.Cast<byte, CX.Alloy.Db.MipChain.Texel>(level.AsSpan());
        Span<CX.Alloy.Db.MipChain.Texel> to = MemoryMarshal.Cast<byte, CX.Alloy.Db.MipChain.Texel>(next.AsSpan());
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int x2 = x + x, y2 = y + y;
                CX.Alloy.Db.MipChain.Texel a = from[CX.Alloy.Db.MipChain.Cell(w, x2, y2)], b = from[CX.Alloy.Db.MipChain.Cell(w, x2 + 1, y2)];
                CX.Alloy.Db.MipChain.Texel c = from[CX.Alloy.Db.MipChain.Cell(w, x2, y2 + 1)], d = from[CX.Alloy.Db.MipChain.Cell(w, x2 + 1, y2 + 1)];
                to[CX.Alloy.Db.MipChain.Cell(n, x, y)] = new CX.Alloy.Db.MipChain.Texel((byte)((a.R + b.R + c.R + d.R + 2) >> 2), (byte)((a.G + b.G + c.G + d.G + 2) >> 2),
                                                                (byte)((a.B + b.B + c.B + d.B + 2) >> 2), (byte)((a.A + b.A + c.A + d.A + 2) >> 2));
            }
        return next;
    }

    /// <summary>The worst kept texel's red over a level, and over the midpoints a bilinear read keeps.</summary>
    private static int CutBrightest(ReadOnlySpan<byte> level, int w)
    {
        ReadOnlySpan<CX.Alloy.Db.MipChain.Texel> t = MemoryMarshal.Cast<byte, CX.Alloy.Db.MipChain.Texel>(level);
        int worst = 0;
        for (int y = 0; y < w; y++)
            for (int x = 0; x < w; x++)
            {
                CX.Alloy.Db.MipChain.Texel o = t[CX.Alloy.Db.MipChain.Cell(w, x, y)], e = t[CX.Alloy.Db.MipChain.Cell(w, (x + 1) % w, y)];
                if (o.A > CutHole) worst = Math.Max(worst, o.R);
                if ((o.A + e.A) / 2 > CutHole) worst = Math.Max(worst, (o.R + e.R) / 2);
            }
        return worst;
    }

    /// <summary>The share of a level's texels that stand past the discard.</summary>
    private static float CutKept(ReadOnlySpan<byte> level)
    {
        ReadOnlySpan<CX.Alloy.Db.MipChain.Texel> t = MemoryMarshal.Cast<byte, CX.Alloy.Db.MipChain.Texel>(level);
        int kept = 0;
        foreach (CX.Alloy.Db.MipChain.Texel p in t) if (p.A > CutHole) kept++;
        return (float)kept / t.Length;
    }
}
