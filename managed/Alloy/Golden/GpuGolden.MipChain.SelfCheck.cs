using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>The single-pass downsampler over a chain deep enough to take two dispatches. Oracle: a level's
    /// texel is the MEAN of the aligned block of the base level under it, averaged here off the base the device
    /// read back -- a direct average against the downsampler's tree of folds.</summary>
    private static void MipChainSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        const int W = 300, H = 200;
        int levels = 1;
        while ((W >> levels) > 0 || (H >> levels) > 0) levels++;
        PipelineId pipe = AlloyRenderer.VkSpdPipe(vk, false, "golden mip chain", log);
        SamplerId smp = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Nearest, Wrap.Clamp));
        ImageId src = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, W, H, 1, 1, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst, "golden mip chain source"));
        ImageId chain = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, W, H, 1, levels, 1, 1,
            ImageUse.Sampled | ImageUse.Storage | ImageUse.CopySrc, "golden mip chain"));
        int bytes = 0;
        for (int l = 0; l < levels; l++) bytes += MipHalves(W, H, l) * sizeof(ushort);
        BufferId rb = vk.CreateBuffer(new BufferDesc(bytes, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device,
                                                     "golden mip chain read"));
        try
        {
            check.Expect(!pipe.IsNull && !smp.IsNull && !src.IsNull && !chain.IsNull && !rb.IsNull,
                         "mip chain: the downsampler or one of its resources was refused (logged above)");
            if (pipe.IsNull || smp.IsNull || src.IsNull || chain.IsNull || rb.IsNull) return;
            var pattern = new Half[W * H * 4];
            for (int i = 0; i < pattern.Length; i++) pattern[i] = (Half)(i * 37 % 211);
            var cl = new CommandList("golden mip chain", 1 << 16, log);
            AlloyRenderer.VkSpdChain(cl, pipe, src, smp, chain, W, H, levels, Vector2.One);
            long off = 0;
            for (int l = 0; l < levels; l++)
            {
                cl.CopyImageToBuffer(new CopyToBufferCmd(chain, new ImageRegion(l, 0, 0, 0), MipSide(W, l), MipSide(H, l), rb, off));
                off += MipHalves(W, H, l) * sizeof(ushort);
            }
            var got = new byte[bytes];
            bool ran = vk.Upload(src, 0, 0, MemoryMarshal.AsBytes(pattern.AsSpan())) && vk.Submit(cl)
                    && vk.ReadBack(rb, got);
            check.Expect(ran, "mip chain: the source fill, the dispatches or the readback was refused");
            if (!ran) return;
            check.Expect(AlloyRenderer.SpdPasses(levels) > 1, $"mip chain: {levels} levels fit in one dispatch, so the "
                                                            + "hand-over between passes went untested");
            ReadOnlySpan<Half> all = MemoryMarshal.Cast<byte, Half>(got);
            MipChainBase(all, pattern, check);
            int at = MipHalves(W, H, 0);
            for (int l = 1; l < levels; l++)
            {
                int lw = MipSide(W, l), lh = MipSide(H, l), block = 1 << l, bad = 0;
                (int At, float Got, double Want) first = (-1, 0f, 0d);
                double worst = 0d;
                for (int y = 0; y < lh; y++)
                    for (int x = 0; x < lw; x++)
                        for (int ch = 0; ch < 4; ch++)
                        {
                            double sum = 0d;
                            for (int by = 0; by < block; by++)
                                for (int bx = 0; bx < block; bx++)
                                    sum += (float)all[(((y * block + by) * W) + x * block + bx) * 4 + ch];
                            double want = sum / (block * block);
                            int i = at + (y * lw + x) * 4 + ch;
                            double d = Math.Abs((float)all[i] - want);
                            if (d > worst) { worst = d; first = (i, (float)all[i], want); }
                            // half carries ten mantissa bits, so a stored level sits within a few of its own steps
                            if (d > Math.Max(Math.Abs(want) / 1024d, 0.01d)) bad++;
                        }
                check.Expect(bad == 0, $"mip chain: level {l} ({lw}x{lh}) has {bad} half(ves) off the mean of the base "
                                     + $"block under them -- half {first.At} reads {first.Got}, not {first.Want:F4}");
                at += MipHalves(W, H, l);
                if (bad == 0 && l == levels - 1)
                    log.Info($"GpuGolden: mip chain, {W}x{H} base, {levels} levels in {AlloyRenderer.SpdPasses(levels)} "
                           + $"dispatch(es), worst {worst:F4} off the base block's mean at the top level");
            }
        }
        finally
        {
            vk.Destroy(rb);
            vk.Destroy(chain);
            vk.Destroy(src);
            vk.Destroy(smp);
            vk.Destroy(pipe);
        }
    }

    /// <summary>The base level the downsampler made against the texture it sampled: a texel centre of a chain the
    /// size of its source is that source's own texel.</summary>
    private static void MipChainBase(ReadOnlySpan<Half> all, ReadOnlySpan<Half> pattern, Checker check)
    {
        int worst = 0;
        float gap = 0f;
        for (int i = 0; i < pattern.Length; i++)
        {
            float d = MathF.Abs((float)all[i] - (float)pattern[i]);
            if (d > gap) { gap = d; worst = i; }
        }
        check.Expect(gap <= 0.05f, $"mip chain: the base level is {(float)all[worst]} at half {worst}, not the "
                                 + $"source's {(float)pattern[worst]}");
    }

    /// <summary>One side of mip <paramref name="level"/> of a <paramref name="side"/>-texel one, as the device sizes it.</summary>
    private static int MipSide(int side, int level) => Math.Max(side >> level, 1);

    /// <summary>Halves one four-channel mip level of a <paramref name="w"/> x <paramref name="h"/> image holds.</summary>
    private static int MipHalves(int w, int h, int level) => MipSide(w, level) * MipSide(h, level) * 4;
}
