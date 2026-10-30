using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The post golden (approved-spec), E2 "the picture is the same": the bloom chain over a fixed scene drawn as the
/// shipped fragment passes and as compute on one device, level 0 held to the half float's precision. Oracle: the
/// fragment chain, built from the shipped <c>cx_bloomdn_fs</c> and <c>cx_bloomup_fs</c> texts.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>A half float carries 11 significant bits.</summary>
    private const float PostHalfTol = 1f / 512f;

    /// <summary>The bloom fixture's scene: four levels deep, the smallest past the chain's 8-texel floor.</summary>
    private const int BloomW = 300, BloomH = 180;

    /// <summary>A dim scene with bright cores and single-texel fireflies, so the threshold, the knee and the first
    /// level's firefly weighting all take some texel.</summary>
    private static float[] PostBloomScene()
    {
        var px = new float[BloomW * BloomH * 4];
        for (int y = 0; y < BloomH; y++)
            for (int x = 0; x < BloomW; x++)
                for (int k = 0; k < 4; k++)
                {
                    float v = 0.3f * PostHash(x, y, k);
                    if (((x / 23) + (y / 17)) % 5 == 0) v += 2f + 3f * PostHash(x / 23, y / 17, k);
                    if (PostHash(x, y, 29) > 0.995f) v += 40f;
                    px[(y * BloomW + x) * 4 + k] = k == 3 ? 1f : v;
                }
        return px;
    }

    /// <summary>E2's bloom: the chain's level 0 after the downsample and the upsample back, drawn as fragments and
    /// as compute over the same scene, held texel by texel to the half float's precision.</summary>
    private static void PostSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId hdr = PostImage(vk, Format.Rgba32F, BloomW, BloomH, ImageUse.Sampled | ImageUse.CopyDst, "golden bloom scene");
        int lw = BloomW / 2, lh = BloomH / 2;
        BufferId rb = vk.CreateBuffer(new BufferDesc(lw * lh * 8, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden bloom read"));
        try
        {
            bool made = !hdr.IsNull && !rb.IsNull && vk.Upload(hdr, 0, 0, MemoryMarshal.AsBytes(PostBloomScene().AsSpan()));
            check.Expect(made, "E2 bloom: the golden's scene or readback was refused (logged above)");
            if (!made) return;
            byte[]? frag = PostBloomRun(vk, log, hdr, false, rb), comp = PostBloomRun(vk, log, hdr, true, rb);
            check.Expect(frag != null, "E2 bloom: the fragment chain (the oracle) was refused (logged above)");
            check.Expect(comp != null, "E2 bloom: the compute bloom was refused or is missing (logged above)");
            if (frag == null || comp == null) return;
            float worst = PostWorstRel(frag, comp);
            check.Expect(worst <= PostHalfTol, $"E2 bloom: the compute level 0 differs from the fragment chain's by {worst:0.#####} (relative)");
            log.Info($"GpuGolden: E2 bloom, {lw}x{lh} level 0, worst {worst:0.#####}");
        }
        finally
        {
            vk.Destroy(rb);
            vk.Destroy(hdr);
        }
    }

    /// <summary>One run of the bloom chain over <paramref name="hdr"/>, its level 0 read back; null when refused.</summary>
    private static byte[]? PostBloomRun(VkBackend vk, ExtenderLog log, ImageId hdr, bool compute, BufferId rb)
    {
        var cl = new CommandList("golden bloom", 1 << 14, log);
        PostChainVk.BeginFrame();
        ImageId level0 = PostChainVk.BloomVk(vk, cl, hdr, BloomW, BloomH, compute, log);
        if (level0.IsNull || !vk.Submit(cl)) return null;
        return PostRead(vk, log, level0, BloomW / 2, BloomH / 2, 8, rb);
    }

    /// <summary>A fixed pseudo-random 0..1 per texel and channel, the fixtures' only source of variation.</summary>
    private static float PostHash(int x, int y, int k) => ((x * 73856093 ^ y * 19349663 ^ k * 83492791) & 1023) / 1023f;

    /// <summary>The worst difference between two half-float images, relative where a value is past 1.</summary>
    private static float PostWorstRel(byte[] a, byte[] b)
    {
        ReadOnlySpan<Half> fa = MemoryMarshal.Cast<byte, Half>(a), fb = MemoryMarshal.Cast<byte, Half>(b);
        float worst = 0f;
        for (int i = 0; i < fa.Length; i++)
            worst = MathF.Max(worst, MathF.Abs((float)fa[i] - (float)fb[i]) / MathF.Max(1f, MathF.Abs((float)fa[i])));
        return worst;
    }

    private static ImageId PostImage(VkBackend vk, Format f, int w, int h, ImageUse use, string label)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, f, w, h, 1, 1, 1, 1, use, label));

    /// <summary><paramref name="img"/>'s <paramref name="w"/> x <paramref name="h"/> texels of <paramref name="bpp"/>
    /// bytes through <paramref name="rb"/>; null when refused.</summary>
    private static byte[]? PostRead(VkBackend vk, ExtenderLog log, ImageId img, int w, int h, int bpp, BufferId rb)
    {
        var cl = new CommandList("golden post readback", 1 << 10, log);
        cl.CopyImageToBuffer(new CopyToBufferCmd(img, new ImageRegion(0, 0, 0, 0), w, h, rb, 0));
        var bytes = new byte[w * h * bpp];
        return vk.Submit(cl) && vk.ReadBack(rb, bytes) ? bytes : null;
    }
}
