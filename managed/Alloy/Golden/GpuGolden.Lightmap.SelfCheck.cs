using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The lightmap-encoder golden: the finish's scratch planes encoded into BC6H and BC7 blocks by the shipped
/// <c>cx_bakeenc_c</c>, copied into real block-compressed images and read back through the driver's own decoder.
/// The bounds are the formats', measured: a block cannot follow two coloured lights crossing to better than a
/// percent, nor a 6x shadow step past mode 1's delta range, and directions and shares hold to a few 8-bit steps.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The fixture page's side in texels, and in 4x4 blocks.</summary>
    private const int LmN = 64, LmB = LmN / 4;

    /// <summary>The block band the encoder is asked for: one block in from each edge, so the blocks around it
    /// stay as cleared and a write past the band shows.</summary>
    private const int LmFirst = 1, LmBand = LmB - 2;

    /// <summary>The byte page C is pre-filled with, so a texel the finish has no answer for is seen to be left
    /// alone rather than written black.</summary>
    private const byte LmUntouched = 85;

    /// <summary>Oracle: <see cref="LmFixture"/>, the finish's rule written out here -- covered texels as baked,
    /// an uncovered one the nearest covered within two, else no texel -- against what the driver's BC6H and BC7
    /// decoders make of the blocks the encoder wrote. The encoder is never asked what it should have produced.</summary>
    private static void LightmapSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        LmFixture(out float[] pa, out float[] pb, out float[] pc, out LmTexel?[] want);
        int covered = 0;
        foreach (LmTexel? w in want) if (w != null) covered++;
        check.Expect(covered > LmN * LmN / 2 && covered < LmN * LmN,
            $"the fixture finishes {covered} of {LmN * LmN} texels -- some covered, some past the dilation's reach");

        string encoder = ShaderSource.Get("cx_bakeenc_c.glsl");
        string src = Version + "#define CX_BAKEENC_LAYER 1\n"
                   + encoder[Math.Min(Version.Length, encoder.Length)..]
                       .Replace("uniform ivec4 uRegion;", $"const ivec4 uRegion = ivec4({LmFirst}, {LmFirst}, {LmBand}, {LmBand});",
                                StringComparison.Ordinal)
                       .Replace("uniform int uPageLayer;", $"const int uPageLayer = {LmLayer};", StringComparison.Ordinal);
        check.Expect(encoder.StartsWith(Version, StringComparison.Ordinal)
                  && src.Contains("const ivec4 uRegion", StringComparison.Ordinal)
                  && src.Contains("const int uPageLayer", StringComparison.Ordinal),
            "cx_bakeenc_c opens with the version line this golden moves its layer define past, and still declares "
          + "uRegion and uPageLayer as the loose uniforms it folds out");
        PipelineId enc = Compute(vk, src, "golden lightmap encoder", out ShaderId encCs);
        PipelineId dec = Compute(vk, FrameTap.ReadbackCs(LmDecodeWords), "golden lightmap decoder", out ShaderId decCs);
        check.Expect(!enc.IsNull && !dec.IsNull, "the lightmap golden's encoder and decoder stages build for the device");

        var r = new LmRes();
        if (!enc.IsNull && !dec.IsNull && LmMake(vk, r))
        {
            if (LmEncode(vk, log, r, enc, pa, pb, pc))
            {
                float[]? irr = LmDecode(vk, log, r.BcA, dec, "irradiance");
                float[]? dir = LmDecode(vk, log, r.BcB, dec, "direction");
                byte[]? shares = LmShares(vk, log, r);
                check.Expect(irr != null && dir != null && shares != null,
                    "the encoded blocks decode through the driver and page C reads back");
                if (irr != null && dir != null && shares != null) LmJudge(check, log, want, irr, dir, shares);
            }
            else check.Expect(false, "the lightmap golden's fixture uploads and the encoder dispatches");
        }
        LmFree(vk, r);
        vk.Destroy(enc);
        vk.Destroy(encCs);
        vk.Destroy(dec);
        vk.Destroy(decCs);
    }

    /// <summary>The page-array layer the encoder is told to write, so a write to the other one shows.</summary>
    private const int LmLayer = 1;

    /// <summary>Words a decoded texel reads back as: a float target's RGBA is two half pairs, which is how a
    /// decoded BC block comes off the device.</summary>
    private const int LmDecodeWords = 2;

    /// <summary>The golden's images: three scratch planes, the two block lanes the encoder writes, the
    /// block-compressed pair those are copied into, and the page layer plane C lands on.</summary>
    private sealed class LmRes
    {
        public ImageId ScrA, ScrB, ScrC, BlkA, BlkB, BcA, BcB, PageC;
    }

    private static bool LmMake(VkBackend vk, LmRes r)
    {
        r.ScrA = LmPlane(vk, "golden lightmap scratch A");
        r.ScrB = LmPlane(vk, "golden lightmap scratch B");
        r.ScrC = LmPlane(vk, "golden lightmap scratch C");
        r.BlkA = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32Ui, LmB, LmB, 1, 1, 1, 1,
            ImageUse.Storage | ImageUse.CopySrc, "golden lightmap blocks A"));
        r.BlkB = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32Ui, LmB, LmB, 1, 1, 1, 1,
            ImageUse.Storage | ImageUse.CopySrc, "golden lightmap blocks B"));
        r.BcA = LmBc(vk, Format.Bc6U, "golden lightmap irradiance");
        r.BcB = LmBc(vk, Format.Bc7, "golden lightmap direction");
        r.PageC = vk.CreateImage(new ImageDesc(ImageKind.Tex2DArray, Format.Rgba8, LmN, LmN, 1, 1, 2, 1,
            ImageUse.Storage | ImageUse.CopySrc | ImageUse.CopyDst | ImageUse.Sampled, "golden lightmap shares"));
        return !r.ScrA.IsNull && !r.ScrB.IsNull && !r.ScrC.IsNull && !r.BlkA.IsNull && !r.BlkB.IsNull
            && !r.BcA.IsNull && !r.BcB.IsNull && !r.PageC.IsNull;
    }

    private static ImageId LmPlane(VkBackend vk, string label)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, LmN, LmN, 1, 1, 1, 1,
                                        ImageUse.Sampled | ImageUse.CopyDst, label));

    private static ImageId LmBc(VkBackend vk, Format f, string label)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, f, LmN, LmN, 1, 1, 1, 1,
                                        ImageUse.Sampled | ImageUse.CopyDst, label));

    private static void LmFree(VkBackend vk, LmRes r)
    {
        vk.Destroy(r.ScrA); vk.Destroy(r.ScrB); vk.Destroy(r.ScrC);
        vk.Destroy(r.BlkA); vk.Destroy(r.BlkB);
        vk.Destroy(r.BcA); vk.Destroy(r.BcB); vk.Destroy(r.PageC);
    }

    /// <summary>The planes uploaded as halves, the block lanes cleared, page C filled with
    /// <see cref="LmUntouched"/>, the band encoded, and the blocks copied into the compressed pair.</summary>
    private static bool LmEncode(VkBackend vk, ExtenderLog log, LmRes r, PipelineId enc,
                                 float[] pa, float[] pb, float[] pc)
    {
        var fill = new byte[LmN * LmN * 4];
        Array.Fill(fill, LmUntouched);
        if (!vk.Upload(r.ScrA, 0, 0, Halves(pa)) || !vk.Upload(r.ScrB, 0, 0, Halves(pb)) || !vk.Upload(r.ScrC, 0, 0, Halves(pc))
            || !vk.Upload(r.PageC, 0, 0, fill) || !vk.Upload(r.PageC, 0, LmLayer, fill))
            return false;
        var cl = new CommandList("golden lightmap encode", 1 << 14, log);
        cl.ClearImage(r.BlkA);
        cl.ClearImage(r.BlkB);
        cl.ReadImages([r.ScrA, r.ScrB, r.ScrC]);
        cl.SetPipeline(enc);
        cl.BindSet(0, new BindSet(default, default,
            [new ImageBinding(0, r.ScrA), new ImageBinding(1, r.ScrB), new ImageBinding(2, r.ScrC)],
            [new ImageBinding(0, r.BlkA), new ImageBinding(1, r.BlkB), new ImageBinding(2, r.PageC)]));
        int groups = (LmBand + LightmapBake.EncGroup - 1) / LightmapBake.EncGroup;
        cl.Dispatch(groups, groups);
        // one Rgba32Ui texel is one 128-bit block, which is what makes the two formats copy-compatible
        cl.CopyImage(new CopyImageCmd(r.BlkA, new ImageRegion(0, 0, 0, 0), r.BcA, new ImageRegion(0, 0, 0, 0), LmB, LmB, 1));
        cl.CopyImage(new CopyImageCmd(r.BlkB, new ImageRegion(0, 0, 0, 0), r.BcB, new ImageRegion(0, 0, 0, 0), LmB, LmB, 1));
        return vk.Submit(cl);
    }

    /// <summary>One decoded plane: <paramref name="bc"/> sampled by the read-back stage into half pairs, unpacked
    /// into four floats a texel. Null when a step was refused.</summary>
    private static float[]? LmDecode(VkBackend vk, ExtenderLog log, ImageId bc, PipelineId dec, string what)
    {
        BufferId outb = vk.CreateBuffer(new BufferDesc(LmN * LmN * LmDecodeWords * sizeof(uint),
            BufferUse.Storage | BufferUse.CopySrc, MemoryKind.Device, "golden lightmap " + what));
        float[]? got = null;
        if (!outb.IsNull)
        {
            var cl = new CommandList("golden lightmap decode", 1 << 12, log);
            cl.ReadImages([bc]);
            cl.SetPipeline(dec);
            cl.BindSet(0, new BindSet(default, [new BufferBinding(SsboSlots.ReadbackOut, outb)], [new ImageBinding(0, bc)], default));
            int groups = (LmN + FrameTap.ReadbackGroup - 1) / FrameTap.ReadbackGroup;
            cl.Dispatch(groups, groups);
            var words = new uint[LmN * LmN * LmDecodeWords];
            if (vk.Submit(cl) && vk.ReadBack(outb, MemoryMarshal.AsBytes(words.AsSpan())))
            {
                got = new float[LmN * LmN * 4];
                for (int i = 0; i < LmN * LmN; i++)
                {
                    got[i * 4] = (float)BitConverter.UInt16BitsToHalf((ushort)words[i * 2]);
                    got[i * 4 + 1] = (float)BitConverter.UInt16BitsToHalf((ushort)(words[i * 2] >> 16));
                    got[i * 4 + 2] = (float)BitConverter.UInt16BitsToHalf((ushort)words[i * 2 + 1]);
                    got[i * 4 + 3] = (float)BitConverter.UInt16BitsToHalf((ushort)(words[i * 2 + 1] >> 16));
                }
            }
        }
        vk.Destroy(outb);
        return got;
    }

    /// <summary>Page C's own layer, straight out of the image: the shares are already 8-bit.</summary>
    private static byte[]? LmShares(VkBackend vk, ExtenderLog log, LmRes r)
    {
        BufferId outb = vk.CreateBuffer(new BufferDesc(LmN * LmN * 4, BufferUse.CopyDst | BufferUse.CopySrc,
            MemoryKind.Device, "golden lightmap shares read"));
        byte[]? got = null;
        if (!outb.IsNull)
        {
            var cl = new CommandList("golden lightmap shares", 1 << 12, log);
            cl.CopyImageToBuffer(new CopyToBufferCmd(r.PageC, new ImageRegion(0, 0, 0, LmLayer), LmN, LmN, outb, 0));
            var bytes = new byte[LmN * LmN * 4];
            if (vk.Submit(cl) && vk.ReadBack(outb, bytes)) got = bytes;
        }
        vk.Destroy(outb);
        return got;
    }

    private static byte[] Halves(float[] v)
    {
        var h = new ushort[v.Length];
        for (int i = 0; i < v.Length; i++) h[i] = BitConverter.HalfToUInt16Bits((Half)v[i]);
        return MemoryMarshal.AsBytes(h.AsSpan()).ToArray();
    }

    /// <summary>The decoded planes against <paramref name="want"/>. The bounds are each format's, measured: one or
    /// two lines a BC6H block cannot follow two coloured lights crossing, nor a 6x shadow step past mode 1's delta
    /// range; directions and shares hold to a few 8-bit steps, and nothing is written outside the band.</summary>
    private static void LmJudge(Checker check, ExtenderLog log, LmTexel?[] want, float[] irr, float[] dir, byte[] shares)
    {
        double smooth = 0, all = 0, sum = 0, dirErr = 0, shareErr = 0;
        int n = 0, stray = 0, notBlack = 0;
        for (int y = 0; y < LmN; y++)
            for (int x = 0; x < LmN; x++)
            {
                int i = (y * LmN + x) * 4;
                LmTexel? at = LmInBand(x, y) ? want[y * LmN + x] : null;
                if (at is not LmTexel w)
                {
                    if (shares[i] != LmUntouched || shares[i + 1] != LmUntouched
                        || shares[i + 2] != LmUntouched || shares[i + 3] != LmUntouched) stray++;
                    if (!LmBlockFinished(want, x, y) && (irr[i] != 0f || irr[i + 1] != 0f || irr[i + 2] != 0f)) notBlack++;
                    continue;
                }
                double rel = 0;
                for (int c = 0; c < 3; c++) rel = Math.Max(rel, Math.Abs(irr[i + c] - w.Irr[c]) / Math.Max(w.Irr[c], 1e-3));
                all = Math.Max(all, rel);
                sum += rel;
                n++;
                if (!LmEdgeBlock(x, y)) smooth = Math.Max(smooth, rel);
                for (int c = 0; c < 3; c++) dirErr = Math.Max(dirErr, Math.Abs(dir[i + c] - w.Dir[c]) * 255);
                for (int c = 0; c < 4; c++) shareErr = Math.Max(shareErr, Math.Abs(shares[i + c] / 255.0 - w.Share[c]) * 255);
            }
        double mean = sum / Math.Max(n, 1);
        check.Expect(n > 0, "the band the encoder wrote holds finished texels to judge");
        check.Expect(mean <= 0.015, $"BC6H irradiance is {mean * 100:F2}% out on average, past 1.5%");
        check.Expect(smooth <= 0.04, $"BC6H irradiance is {smooth * 100:F2}% out at worst off the shadow edge, past 4%");
        check.Expect(all <= 0.10, $"BC6H irradiance is {all * 100:F2}% out at worst on the shadow edge, past 10%");
        check.Expect(dirErr <= 6.0, $"the BC7 direction is {dirErr:F1} 8-bit steps out, past 6");
        check.Expect(shareErr <= 1.01, $"the mover shares are {shareErr:F2} 8-bit steps out, past 1");
        check.Expect(stray == 0, $"{stray} share texels were written outside what the finish answered for");
        check.Expect(notBlack == 0, $"{notBlack} texels of blocks the encoder never wrote came back non-black");
        log.Info($"GpuGolden: lightmap encoder, irradiance {mean * 100:F2}% mean, {smooth * 100:F2}% max off the shadow "
               + $"edge, {all * 100:F2}% on it, direction {dirErr:F1} and shares {shareErr:F2} 8-bit steps, "
               + $"{stray} stray share texels, {notBlack} texels of empty blocks not black");
    }
}
