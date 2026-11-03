using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>A base of the blit chain's golden: wide enough for six levels and taller than one texel at most of
    /// them, so the last levels exercise the side a halving runs out of first.</summary>
    private const int BlitChainW = 32, BlitChainH = 16;

    /// <summary>The rounding a level's texel is allowed against the box average of the level under it: the blit lands an
    /// eight-bit channel, and the oracle reads the level under it back rather than compounding its own averages.</summary>
    private const int BlitChainSlack = 1;

    /// <summary>One texel of the chain's readback, so a level is walked as texels rather than as channel lanes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BlitTexel
    {
        public byte R, G, B, A;
    }

    /// <summary>One upload's base and <see cref="VkBackend.GenerateMips"/>'s whole chain off it, every level read back.
    /// Oracle: each level's texel is the box average of the 2x2 of the level under it, which the readback supplies.</summary>
    private static void BlitChainSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        int levels = MipLevels(BlitChainW, BlitChainH);
        ImageId img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, BlitChainW, BlitChainH, 1, levels, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc, "golden blit chain"));
        long bytes = BlitChainTexels(levels) * 4;
        BufferId rb = vk.CreateBuffer(new BufferDesc(bytes, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden blit chain read"));
        try
        {
            check.Expect(!img.IsNull && !rb.IsNull, "blit chain: the texture or its readback was refused (logged above)");
            if (img.IsNull || rb.IsNull) return;
            BlitTexel[] texels = BlitChainBase();
            bool ran = vk.Upload(img, 0, 0, MemoryMarshal.AsBytes(texels.AsSpan()));
            vk.GenerateMips(img);
            var cl = new CommandList("golden blit chain readback", 1 << 12, log);
            long off = 0;
            for (int l = 0; l < levels; l++)
            {
                cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(l, 0, 0, 0), MipSide(BlitChainW, l), MipSide(BlitChainH, l), rb, off));
                off += (long)MipSide(BlitChainW, l) * MipSide(BlitChainH, l) * 4;
            }
            var got = new byte[bytes];
            ran = ran && vk.Submit(cl) && vk.ReadBack(rb, got);
            check.Expect(ran, "blit chain: the base fill, the chain or the readback was refused (logged above)");
            if (!ran) return;
            Span<BlitTexel> all = MemoryMarshal.Cast<byte, BlitTexel>(got.AsSpan());
            check.Expect(got.AsSpan(0, texels.Length * 4).SequenceEqual(MemoryMarshal.AsBytes(texels.AsSpan())),
                         "blit chain: the base level does not read back the texels uploaded into it");
            int folded = BlitChainFolds(check, all, levels);
            check.Expect(folded == levels - 1, $"blit chain: the oracle held {folded} of the {levels - 1} levels the chain makes");
        }
        finally
        {
            vk.Destroy(rb);
            vk.Destroy(img);
        }
    }

    /// <summary>Every level past the base held against the box average of the level under it, over every channel; how
    /// many levels that covered.</summary>
    private static int BlitChainFolds(Checker check, Span<BlitTexel> all, int levels)
    {
        int worst = 0, worstLevel = 0, covered = 0;
        for (int l = 1; l < levels; l++)
        {
            covered++;
            int bw = MipSide(BlitChainW, l - 1), bh = MipSide(BlitChainH, l - 1);
            int w = MipSide(BlitChainW, l), h = MipSide(BlitChainH, l);
            Span<BlitTexel> below = all.Slice((int)BlitChainTexels(l - 1), bw * bh);
            Span<BlitTexel> here = all.Slice((int)BlitChainTexels(l), w * h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int x0 = x * 2, y0 = y * 2;
                    int x1 = Math.Min(x0 + 1, bw - 1), y1 = Math.Min(y0 + 1, bh - 1);
                    int top = y0 * bw, bottom = y1 * bw, at = y * w + x;
                    BlitTexel a = below[top + x0], b = below[top + x1], c = below[bottom + x0], d = below[bottom + x1];
                    BlitTexel got = here[at];
                    int gap = Math.Max(Math.Max(Math.Abs(got.R - Box(a.R, b.R, c.R, d.R)), Math.Abs(got.G - Box(a.G, b.G, c.G, d.G))),
                                       Math.Max(Math.Abs(got.B - Box(a.B, b.B, c.B, d.B)), Math.Abs(got.A - Box(a.A, b.A, c.A, d.A))));
                    if (gap > worst) { worst = gap; worstLevel = l; }
                }
        }
        check.Expect(worst <= BlitChainSlack, $"blit chain: level {worstLevel} is {worst} off the box average of the level "
                                            + $"under it, past the {BlitChainSlack} an eight-bit channel rounds by");
        return covered;
    }

    /// <summary>The average of a 2x2 block's one channel, rounded as an eight-bit channel lands it.</summary>
    private static int Box(byte a, byte b, byte c, byte d) => (a + b + c + d + 2) / 4;

    /// <summary>Texels the chain's levels below <paramref name="level"/> hold.</summary>
    private static long BlitChainTexels(int level)
    {
        long n = 0;
        for (int l = 0; l < level; l++) n += (long)MipSide(BlitChainW, l) * MipSide(BlitChainH, l);
        return n;
    }

    /// <summary>The base level's texels: each channel its own function of the texel's place, so a level folded off the
    /// wrong block or the wrong channel reads nothing like the average of the right one.</summary>
    private static BlitTexel[] BlitChainBase()
    {
        var px = new BlitTexel[BlitChainW * BlitChainH];
        for (int y = 0, o = 0; y < BlitChainH; y++)
            for (int x = 0; x < BlitChainW; x++, o++)
                px[o] = new BlitTexel
                {
                    R = (byte)(x * 8), G = (byte)(y * 16), B = (byte)(255 - x * 8), A = (byte)((x + y) % 2 == 0 ? 64 : 192),
                };
        return px;
    }
}
