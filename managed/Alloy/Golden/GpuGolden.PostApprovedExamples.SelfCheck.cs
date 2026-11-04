using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The post golden (approved-spec), E2 "the picture is the same": the bloom chain over a fixed scene drawn as the
/// shipped fragment passes and as compute on one device, level 0 held to its own format's precision. Oracle: the
/// fragment chain, built from the shipped <c>cx_bloomdn_fs</c> and <c>cx_bloomup_fs</c> texts.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>A half float carries 11 significant bits.</summary>
    private const float PostHalfTol = 1f / 512f;

    /// <summary>The widest relative step each channel of the packed levels has over five exponent bits: six mantissa
    /// bits in red and green, five in blue.</summary>
    private const float PostWideTol = 1f / 64f, PostBlueTol = 1f / 32f;

    /// <summary>One texel of a bloom level: three packed floats in one word, or four half floats.</summary>
    private const int PostPackedBytes = 4, PostHalfBytes = 8;

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
    /// as compute over the same scene, held texel by texel to the precision the levels' own format holds.</summary>
    private static void PostSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        ImageId hdr = PostImage(vk, Format.Rgba32F, BloomW, BloomH, ImageUse.Sampled | ImageUse.CopyDst, "golden bloom scene");
        int lw = BloomW / 2, lh = BloomH / 2;
        // the levels' format is the device's answer, so the readback is sized by the wider of the two it can be
        BufferId rb = vk.CreateBuffer(new BufferDesc(lw * lh * PostHalfBytes, BufferUse.CopyDst | BufferUse.CopySrc,
                                                     MemoryKind.Device, "golden bloom read"));
        try
        {
            bool made = !hdr.IsNull && !rb.IsNull && vk.Upload(hdr, 0, 0, MemoryMarshal.AsBytes(PostBloomScene().AsSpan()));
            check.Expect(made, "E2 bloom: the golden's scene or readback was refused (logged above)");
            if (!made) return;
            (byte[]? frag, Format f) = PostBloomRun(vk, log, hdr, false, rb);
            (byte[]? comp, _) = PostBloomRun(vk, log, hdr, true, rb);
            check.Expect(frag != null, "E2 bloom: the fragment chain (the oracle) was refused (logged above)");
            check.Expect(comp != null, "E2 bloom: the compute bloom was refused or is missing (logged above)");
            if (frag == null || comp == null) return;
            // Given the bloom levels stored as R11G11B10F
            // When the compute chain's level 0 is compared with the fragment chain's
            // Then red and green differ by at most 1/64, relative
            // And blue differs by at most 1/32, relative
            (float wide, float blue) = PostWorstRel(frag, comp, f);
            (float wideTol, float blueTol) = PostTol(f);
            check.Expect(wide <= wideTol && blue <= blueTol,
                $"E2 bloom: the {f} compute level 0 differs from the fragment chain's by {wide:0.#####} in red or green "
              + $"and {blue:0.#####} in blue (relative)");
            // Given a packed word built by hand from the format's fields: red 1.0, green 2.0, blue 4.0
            // When it is unpacked
            // Then red reads 1, green reads 2 and blue reads 4, out of bits 0 to 10, 11 to 21 and 22 to 31
            // red 1.0 is exponent 15 over six mantissa bits, green 2.0 exponent 16, blue 4.0 exponent 17 over five
            uint triple = (544u << 22) | (1024u << 11) | 960u;
            check.Expect(PostUnpack(triple, 0) == 1f && PostUnpack(triple, 1) == 2f && PostUnpack(triple, 2) == 4f,
                "a packed word's red reads out of bits 0..10, its green out of 11..21 and its blue out of 22..31");
            log.Info($"GpuGolden: E2 bloom, {lw}x{lh} {f} level 0, worst {wide:0.#####} wide, {blue:0.#####} blue");
        }
        finally
        {
            vk.Destroy(rb);
            vk.Destroy(hdr);
        }
    }

    /// <summary>One run of the bloom chain over <paramref name="hdr"/>, its level 0 read back in the format the chain
    /// gave it; a null read when refused.</summary>
    private static (byte[]? Bytes, Format Fmt) PostBloomRun(VkBackend vk, ExtenderLog log, ImageId hdr, bool compute, BufferId rb)
    {
        var cl = new CommandList("golden bloom", 1 << 14, log);
        PostChainVk.BeginFrame();
        ImageId level0 = PostChainVk.BloomVk(vk, cl, hdr, BloomW, BloomH, compute, log);
        if (level0.IsNull || !vk.Submit(cl)) return (null, PackedColour.Wide);
        Format f = PostChainVk.FormatOf(level0);
        return (PostRead(vk, log, level0, BloomW / 2, BloomH / 2, PostBytes(f), rb), f);
    }

    /// <summary>A fixed pseudo-random 0..1 per texel and channel, the fixtures' only source of variation.</summary>
    private static float PostHash(int x, int y, int k) => ((x * 73856093 ^ y * 19349663 ^ k * 83492791) & 1023) / 1023f;

    /// <summary>A texel of a level of <paramref name="f"/>, in bytes.</summary>
    private static int PostBytes(Format f) => f == PackedColour.Packed ? PostPackedBytes : PostHalfBytes;

    /// <summary>What the two chains may differ by in a level of <paramref name="f"/>: one quantum of its own step in
    /// each channel group, so a quantum of divergence passes and a mistaken tap or weight does not.</summary>
    private static (float Wide, float Blue) PostTol(Format f)
        => f == PackedColour.Packed ? (PostWideTol, PostBlueTol) : (PostHalfTol, PostHalfTol);

    /// <summary>The worst difference between two level reads of <paramref name="f"/>, relative where a value is past
    /// 1: red and green together, then blue, which the packed format holds to a step of its own.</summary>
    private static (float Wide, float Blue) PostWorstRel(byte[] a, byte[] b, Format f)
    {
        float wide = 0f, blue = 0f;
        if (f == PackedColour.Packed)
        {
            ReadOnlySpan<uint> wa = MemoryMarshal.Cast<byte, uint>(a), wb = MemoryMarshal.Cast<byte, uint>(b);
            for (int i = 0; i < wa.Length; i++)
                for (int k = 0; k < 3; k++) PostWorst(PostUnpack(wa[i], k), PostUnpack(wb[i], k), k, ref wide, ref blue);
            return (wide, blue);
        }
        ReadOnlySpan<Half> ha = MemoryMarshal.Cast<byte, Half>(a), hb = MemoryMarshal.Cast<byte, Half>(b);
        for (int i = 0; i + 4 <= ha.Length; i += 4)
        {
            ReadOnlySpan<Half> ta = ha.Slice(i, 4), tb = hb.Slice(i, 4);
            for (int k = 0; k < 3; k++) PostWorst((float)ta[k], (float)tb[k], k, ref wide, ref blue);
        }
        return (wide, blue);
    }

    /// <summary>Channel <paramref name="k"/>'s relative difference folded into the group it belongs to.</summary>
    private static void PostWorst(float x, float y, int k, ref float wide, ref float blue)
    {
        float d = MathF.Abs(x - y) / MathF.Max(1f, MathF.Abs(x));
        if (k == 2) blue = MathF.Max(blue, d);
        else wide = MathF.Max(wide, d);
    }

    /// <summary>Channel <paramref name="k"/> of a <c>B10G11R11_UFLOAT</c> word: red and green carry six mantissa bits
    /// over five of exponent at bias 15, blue five, and an all-zero exponent is a subnormal.</summary>
    private static float PostUnpack(uint w, int k)
    {
        int bits = k == 2 ? 10 : 11, at = k == 0 ? 0 : k == 1 ? 11 : 22, mBits = bits - 5;
        uint v = (w >> at) & ((1u << bits) - 1u);
        int exp = (int)(v >> mBits);
        float mant = (v & ((1u << mBits) - 1u)) / (float)(1 << mBits);
        return exp == 0 ? mant * MathF.Pow(2f, -14f) : (1f + mant) * MathF.Pow(2f, exp - 15);
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
