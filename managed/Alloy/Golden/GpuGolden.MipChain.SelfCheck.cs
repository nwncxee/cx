using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static partial class GpuGolden
{
    /// <summary>A chain the downsampler filled: its base size, the levels it holds, the channels a texel carries
    /// and the bytes one channel reads back as.</summary>
    private readonly record struct MipChain(int W, int H, int Levels, int Chans, int ChanBytes);

    /// <summary>What a level's texel has to be: the MAX of the block under it, or its mean, within
    /// <paramref name="Rel"/> of the value or <paramref name="Abs"/>, whichever is wider.</summary>
    private readonly record struct FoldOracle(bool Max, double Rel, double Abs);

    /// <summary>The box-average downsampler over a chain deep enough to take two dispatches. Oracle: a level's
    /// texel is the MEAN of the aligned block of the base level under it, averaged here off the base the device
    /// read back -- a direct average against the downsampler's tree of folds.</summary>
    private static void MipChainSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        const int W = 300, H = 200;
        var c = new MipChain(W, H, MipLevels(W, H), 4, sizeof(ushort));
        PipelineId pipe = AlloyRenderer.VkSpdPipe(vk, false, "golden mip chain", log);
        SamplerId smp = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Nearest, Wrap.Clamp));
        ImageId src = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, W, H, 1, 1, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst, "golden mip chain source"));
        ImageId chain = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, W, H, 1, c.Levels, 1, 1,
            ImageUse.Sampled | ImageUse.Storage | ImageUse.CopySrc, "golden mip chain"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(MipChainBytes(c), BufferUse.CopyDst | BufferUse.CopySrc,
                                                     MemoryKind.Device, "golden mip chain read"));
        try
        {
            check.Expect(!pipe.IsNull && !smp.IsNull && !src.IsNull && !chain.IsNull && !rb.IsNull,
                         "mip chain: the downsampler or one of its resources was refused (logged above)");
            if (pipe.IsNull || smp.IsNull || src.IsNull || chain.IsNull || rb.IsNull) return;
            var pattern = new Half[W * H * 4];
            for (int i = 0; i < pattern.Length; i++) pattern[i] = (Half)(i * 37 % 211);
            var cl = new CommandList("golden mip chain", 1 << 16, log);
            AlloyRenderer.VkSpdChain(cl, pipe, src, smp, chain, W, H, c.Levels, Vector2.One);
            MipChainRead(cl, c, chain, rb);
            var got = new byte[MipChainBytes(c)];
            bool ran = vk.Upload(src, 0, 0, MemoryMarshal.AsBytes(pattern.AsSpan())) && vk.Submit(cl)
                    && vk.ReadBack(rb, got);
            check.Expect(ran, "mip chain: the source fill, the dispatches or the readback was refused");
            if (!ran) return;
            check.Expect(AlloyRenderer.SpdPasses(c.Levels) > 1, $"mip chain: {c.Levels} levels fit in one dispatch, so "
                                                              + "the hand-over between passes went untested");
            ReadOnlySpan<Half> halves = MemoryMarshal.Cast<byte, Half>(got);
            var all = new float[halves.Length];
            for (int i = 0; i < all.Length; i++) all[i] = (float)halves[i];
            MipChainBase(all, pattern, check);
            // half carries ten mantissa bits, so a stored level sits within a few of its own steps
            int deepest = MipChainFolds(all, c, new FoldOracle(false, 1d / 1024d, 0.01d), check, log, "mip chain");
            check.Expect(deepest == c.Levels - 2, $"mip chain: the mean-of-the-base oracle reached level {deepest} of "
                                                + $"{c.Levels - 1}, not every level but the one the device clamps to a "
                                                + "single texel (which folds replicated edge texels, so its mean is not "
                                                + "the block's)");
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

    /// <summary>The hi-Z variant over a depth ramp whose closest surface sits at the base's first texel. Oracles:
    /// a base texel is the inverse view depth of its depth (a reversed-Z 0 is the far plane, read as 1/far, a 1
    /// the near plane as 1/near, linear between), a level's texel the MAX over the aligned block of that base,
    /// and the top level, which the device clamps, still holds the frame's closest surface.</summary>
    private static void MipChainHiZSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        const int W = 300, H = 200;
        // a near and far whose reciprocals are exact, so the oracle's lerp and the stage's own algebra agree bit for bit
        const float Near = 1f, Far = 2f;
        var c = new MipChain(W, H, MipLevels(W, H), 1, sizeof(float));
        var oracle = new FoldOracle(true, 1e-6d, 1e-6d);
        PipelineId pipe = AlloyRenderer.VkSpdPipe(vk, true, "golden hi-Z chain", log);
        SamplerId smp = vk.CreateSampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.Nearest, Wrap.Clamp));
        ImageId src = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, W, H, 1, 1, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst, "golden hi-Z source"));
        ImageId chain = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, W, H, 1, c.Levels, 1, 1,
            ImageUse.Sampled | ImageUse.Storage | ImageUse.CopySrc, "golden hi-Z chain"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(MipChainBytes(c), BufferUse.CopyDst | BufferUse.CopySrc,
                                                     MemoryKind.Device, "golden hi-Z read"));
        try
        {
            check.Expect(!pipe.IsNull && !smp.IsNull && !src.IsNull && !chain.IsNull && !rb.IsNull,
                         "hi-Z chain: the downsampler or one of its resources was refused (logged above)");
            if (pipe.IsNull || smp.IsNull || src.IsNull || chain.IsNull || rb.IsNull) return;
            // every depth a multiple of 1/256: exact in a float, and exact through the transform
            var depth = new float[W * H];
            for (int i = 0; i < depth.Length; i++) depth[i] = (i * 37 % 251) / 256f;
            depth[0] = 1f;                                // the frame's closest surface, at the texel every level folds
            depth[93 * W + 137] = 255 / 256f;             // a near spike inside a tile
            depth[(H - 1) * W + W - 1] = 255 / 256f;      // and one in the last texel the odd-sized base holds
            var cl = new CommandList("golden hi-Z chain", 1 << 16, log);
            AlloyRenderer.VkSpdChain(cl, pipe, src, smp, chain, W, H, c.Levels, new Vector2(Near, Far));
            MipChainRead(cl, c, chain, rb);
            var got = new byte[MipChainBytes(c)];
            bool ran = vk.Upload(src, 0, 0, MemoryMarshal.AsBytes(depth.AsSpan())) && vk.Submit(cl)
                    && vk.ReadBack(rb, got);
            check.Expect(ran, "hi-Z chain: the source fill, the dispatches or the readback was refused");
            if (!ran) return;
            check.Expect(AlloyRenderer.SpdPasses(c.Levels) > 1, $"hi-Z chain: {c.Levels} levels fit in one dispatch, so "
                                                              + "the hand-over between passes went untested");
            ReadOnlySpan<float> all = MemoryMarshal.Cast<byte, float>(got);
            int worst = 0;
            double gap = 0d;
            for (int i = 0; i < depth.Length; i++)
            {
                double d = Math.Abs(all[i] - InvViewDepth(depth[i], Near, Far));
                if (d > gap) { gap = d; worst = i; }
            }
            check.Expect(gap <= oracle.Abs, $"hi-Z chain: the base level reads {all[worst]} at texel {worst}, not the "
                                          + $"{InvViewDepth(depth[worst], Near, Far):F6} the depth {depth[worst]} under "
                                          + "it is worth as an inverse view depth");
            int deepest = MipChainFolds(all, c, oracle, check, log, "hi-Z chain");
            check.Expect(deepest == c.Levels - 2, $"hi-Z chain: the max-of-the-base oracle reached level {deepest} of "
                                                + $"{c.Levels - 1}, not every level but the one the device clamps to a "
                                                + "single texel (which folds replicated edge texels, so the block it "
                                                + "covers is not the aligned one)");
            float top = all[MipChainStart(c, c.Levels - 1)];
            double closest = InvViewDepth(depth[0], Near, Far);
            check.Expect(Math.Abs(top - closest) <= oracle.Abs,
                         $"hi-Z chain: the top level reads {top}, not the frame's closest surface at {closest:F6} -- a "
                       + "fold that loses the closest depth in a cell lets the march skip a hit instead of descending");
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

    /// <summary>The inverse view depth a reversed-Z depth buffer value is worth: 0 is the far plane and reads
    /// 1/<paramref name="far"/>, 1 is the near plane and reads 1/<paramref name="near"/>, linear between, because
    /// one over view depth is what interpolates linearly in screen space.</summary>
    private static double InvViewDepth(double d, double near, double far) => (1d - d) / far + d / near;

    /// <summary>Every level whose aligned block of the base still fits inside the base, held to
    /// <paramref name="o"/>'s fold of that block of the base level <paramref name="all"/> carries. Returns the
    /// deepest level it covered: the oracle stops before a level the device clamped to a single texel, whose fold
    /// takes in replicated edge texels.</summary>
    private static int MipChainFolds(ReadOnlySpan<float> all, in MipChain c, in FoldOracle o, Checker check,
                                     ExtenderLog log, string what)
    {
        int at = MipChanValues(c, 0), deepest = 0;
        double worst = 0d;
        for (int l = 1; l < c.Levels; l++)
        {
            int lw = MipSide(c.W, l), lh = MipSide(c.H, l), block = 1 << l;
            if (lw << l > c.W || lh << l > c.H) break;
            int bad = 0;
            (int At, float Got, double Want) first = (at, all[at], 0d);
            for (int y = 0; y < lh; y++)
                for (int x = 0; x < lw; x++)
                    for (int ch = 0; ch < c.Chans; ch++)
                    {
                        double fold = o.Max ? double.NegativeInfinity : 0d;
                        for (int by = 0; by < block; by++)
                            for (int bx = 0; bx < block; bx++)
                            {
                                double v = all[(((y * block + by) * c.W) + x * block + bx) * c.Chans + ch];
                                fold = o.Max ? Math.Max(fold, v) : fold + v;
                            }
                        double want = o.Max ? fold : fold / (block * block);
                        int i = at + (y * lw + x) * c.Chans + ch;
                        double d = Math.Abs(all[i] - want);
                        if (d > worst) worst = d;
                        if (d > Math.Max(Math.Abs(want) * o.Rel, o.Abs) && bad++ == 0) first = (i, all[i], want);
                    }
            check.Expect(bad == 0, $"{what}: level {l} ({lw}x{lh}) has {bad} value(s) off the fold of the base block "
                                 + $"under them -- value {first.At} reads {first.Got}, not {first.Want:F4}");
            at += MipChanValues(c, l);
            deepest = l;
        }
        log.Info($"GpuGolden: {what}, {c.W}x{c.H} base, {c.Levels} levels in {AlloyRenderer.SpdPasses(c.Levels)} "
               + $"dispatch(es), levels 1 to {deepest} within {worst:F4} of the base block's fold");
        return deepest;
    }

    /// <summary>The base level the downsampler made against the texture it sampled: a texel centre of a chain the
    /// size of its source is that source's own texel.</summary>
    private static void MipChainBase(ReadOnlySpan<float> all, ReadOnlySpan<Half> pattern, Checker check)
    {
        int worst = 0;
        float gap = 0f;
        for (int i = 0; i < pattern.Length; i++)
        {
            float d = MathF.Abs(all[i] - (float)pattern[i]);
            if (d > gap) { gap = d; worst = i; }
        }
        check.Expect(gap <= 0.05f, $"mip chain: the base level is {all[worst]} at half {worst}, not the "
                                 + $"source's {(float)pattern[worst]}");
    }

    /// <summary>Every level of <paramref name="chain"/> into <paramref name="rb"/>, level after level.</summary>
    private static void MipChainRead(CommandList cl, in MipChain c, ImageId chain, BufferId rb)
    {
        long off = 0;
        for (int l = 0; l < c.Levels; l++)
        {
            cl.CopyImageToBuffer(new CopyToBufferCmd(chain, new ImageRegion(l, 0, 0, 0),
                                                     MipSide(c.W, l), MipSide(c.H, l), rb, off));
            off += (long)MipChanValues(c, l) * c.ChanBytes;
        }
    }

    /// <summary>Levels a chain over a <paramref name="w"/> x <paramref name="h"/> base holds: one more for every
    /// halving either side survives.</summary>
    private static int MipLevels(int w, int h)
    {
        int n = 1;
        while ((w >> n) > 0 || (h >> n) > 0) n++;
        return n;
    }

    /// <summary>One side of mip <paramref name="level"/> of a <paramref name="side"/>-texel one, as the device sizes it.</summary>
    private static int MipSide(int side, int level) => Math.Max(side >> level, 1);

    /// <summary>Channel values one level of the chain holds.</summary>
    private static int MipChanValues(in MipChain c, int level) => MipSide(c.W, level) * MipSide(c.H, level) * c.Chans;

    /// <summary>Where level <paramref name="level"/> starts in a readback of the whole chain, in channel values.</summary>
    private static int MipChainStart(in MipChain c, int level)
    {
        int n = 0;
        for (int l = 0; l < level; l++) n += MipChanValues(c, l);
        return n;
    }

    /// <summary>Bytes a readback of the whole chain takes.</summary>
    private static int MipChainBytes(in MipChain c) => MipChainStart(c, c.Levels) * c.ChanBytes;
}
