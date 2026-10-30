using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The post golden (approved-spec), E2 "the picture is the same": a fixed scene through the shipped fragment chain
/// and through the compute post on one device, held to one 8-bit step of the output. Oracle: the fragment chain,
/// built from the shipped <c>cx_taa_f</c>, <c>cx_taa_fs</c> and bloom texts and drawn the way the post draws them.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The display size of every case, and where the stage's rect sits in its target.</summary>
    private const int PostW = 37, PostH = 21, PostX = 3, PostY = 2;

    /// <summary>One output step and the history's tolerance: a half float carries 11 significant bits.</summary>
    private const float PostStep = 1f / 255f, PostHistTol = 1f / 512f;

    private static void PostSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        PostTaaCase(vk, check, log, "E2 TAA at render size", PostW, PostH, false, 0.1f, 0f);
        PostTaaCase(vk, check, log, "E2 TAA upsampled", 29, 17, true, 0.1f, 0f);
        PostTaaCase(vk, check, log, "E2 TAA first frame", PostW, PostH, false, 1f, 0f);
        PostTaaCase(vk, check, log, "E2 TAA dithered", PostW, PostH, false, 0.1f, PostStep);
        PostBloomCase(vk, check, log);
    }

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
    private static void PostBloomCase(VkBackend vk, Checker check, ExtenderLog log)
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
            ReadOnlySpan<Half> fa = MemoryMarshal.Cast<byte, Half>(frag), fb = MemoryMarshal.Cast<byte, Half>(comp);
            float worst = 0f;
            for (int i = 0; i < fa.Length; i++)
                worst = MathF.Max(worst, MathF.Abs((float)fa[i] - (float)fb[i]) / MathF.Max(1f, MathF.Abs((float)fa[i])));
            check.Expect(worst <= PostHistTol, $"E2 bloom: the compute level 0 differs from the fragment chain's by {worst:0.#####} (relative)");
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

    /// <summary>A scene of bright and dark blocks, a softer history under it, a velocity that reaches outside the
    /// frame and depth blocks across the colour's edges: every arm of the accumulate takes some pixel.</summary>
    private static float PostHash(int x, int y, int k) => ((x * 73856093 ^ y * 19349663 ^ k * 83492791) & 1023) / 1023f;

    private static float[] PostCur(int w, int h)
    {
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 4; k++)
                    px[(y * w + x) * 4 + k] = k == 3 ? 1f : (((x / 3) + (y / 2)) & 1) == 0 ? 0.2f + 0.3f * PostHash(x, y, k) : 2.5f + PostHash(x, y, k);
        return px;
    }

    private static float[] PostHist(int w, int h)
    {
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 4; k++)
                    px[(y * w + x) * 4 + k] = k == 3 ? 1f : 0.6f + 0.5f * MathF.Sin(x * 0.4f + k) * MathF.Cos(y * 0.3f) + (PostHash(x, y, 7) > 0.95f ? 6f : 0f);
        return px;
    }

    private static float[] PostVel(int w, int h)
    {
        var px = new float[w * h * 2];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                px[(y * w + x) * 2] = (PostHash(x, y, 11) - 0.5f) * 6f;
                px[(y * w + x) * 2 + 1] = (PostHash(x, y, 13) - 0.5f) * 6f;
            }
        return px;
    }

    private static float[] PostDepth(int w, int h)
    {
        var px = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[y * w + x] = ((x / 4 + y / 3) % 3) * 0.3f + 0.05f + 0.01f * PostHash(x, y, 17);
        return px;
    }

    /// <summary>One TAA case: the scene at <paramref name="rw"/> x <paramref name="rh"/> (the render size), resolved
    /// at the display size by both paths, their targets and new histories compared texel by texel. Undithered they
    /// agree to one step; a dither of <paramref name="dither"/> adds its own span, since each stage hashes its noise.</summary>
    private static void PostTaaCase(VkBackend vk, Checker check, ExtenderLog log, string name, int rw, int rh, bool upsample,
                                    float blend, float dither)
    {
        int tw = PostW + 2 * PostX, th = PostH + 2 * PostY;
        ImageId cur = PostImage(vk, Format.Rgba32F, rw, rh, ImageUse.Sampled | ImageUse.CopyDst, "golden post cur");
        ImageId vel = PostImage(vk, Format.Rg32F, rw, rh, ImageUse.Sampled | ImageUse.CopyDst, "golden post vel");
        ImageId depth = PostImage(vk, Format.R32F, rw, rh, ImageUse.Sampled | ImageUse.CopyDst, "golden post depth");
        ImageId hist = PostImage(vk, Format.Rgba32F, PostW, PostH, ImageUse.Sampled | ImageUse.CopyDst, "golden post hist");
        ImageId[] histOut = new ImageId[2], into = new ImageId[2];
        for (int i = 0; i < 2; i++)
        {
            histOut[i] = PostImage(vk, Format.Rgba16F, PostW, PostH, ImageUse.Sampled | ImageUse.ColorTarget | ImageUse.Storage | ImageUse.CopySrc, "golden post hist out");
            into[i] = PostImage(vk, Format.Rgba8, tw, th, ImageUse.Sampled | ImageUse.ColorTarget | ImageUse.Storage | ImageUse.CopySrc, "golden post target");
        }
        BufferId rb = vk.CreateBuffer(new BufferDesc(tw * th * 8, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden post read"));
        try
        {
            bool made = !cur.IsNull && !vel.IsNull && !depth.IsNull && !hist.IsNull && !rb.IsNull
                     && !histOut[0].IsNull && !histOut[1].IsNull && !into[0].IsNull && !into[1].IsNull
                     && vk.Upload(cur, 0, 0, MemoryMarshal.AsBytes(PostCur(rw, rh).AsSpan()))
                     && vk.Upload(vel, 0, 0, MemoryMarshal.AsBytes(PostVel(rw, rh).AsSpan()))
                     && vk.Upload(depth, 0, 0, MemoryMarshal.AsBytes(PostDepth(rw, rh).AsSpan()))
                     && vk.Upload(hist, 0, 0, MemoryMarshal.AsBytes(PostHist(PostW, PostH).AsSpan()));
            check.Expect(made, $"{name}: the golden's images or uploads were refused (logged above)");
            if (!made) return;
            var jitter = new Vector2(0.3f, -0.2f);
            const float sharp = 0.3f;
            bool drew = PostTaaFragment(vk, log, cur, depth, vel, hist, histOut[0], into[0], blend, jitter, upsample, sharp, dither);
            check.Expect(drew, $"{name}: the fragment chain (the oracle) was refused (logged above)");
            var cl = new CommandList("golden post fused", 1 << 14, log);
            var f = new TaaFusedVk.Frame(cur, depth, vel, hist, histOut[1], into[1], PostX, PostY, PostW, PostH,
                                         blend, jitter, upsample, sharp, dither);
            bool fused = TaaFusedVk.Record(vk, cl, in f, log) && vk.Submit(cl);
            check.Expect(fused, $"{name}: the fused TAA was refused or is missing (logged above)");
            if (!drew || !fused) return;
            byte[]? a = PostRead(vk, log, into[0], tw, th, 4, rb), b = PostRead(vk, log, into[1], tw, th, 4, rb);
            byte[]? ha = PostRead(vk, log, histOut[0], PostW, PostH, 8, rb), hb = PostRead(vk, log, histOut[1], PostW, PostH, 8, rb);
            check.Expect(a != null && b != null && ha != null && hb != null, $"{name}: a readback was refused (logged above)");
            if (a == null || b == null || ha == null || hb == null) return;
            int worst = 0, wx = 0, wy = 0;
            for (int y = 0; y < PostH; y++)
                for (int x = 0; x < PostW; x++)
                    for (int k = 0; k < 4; k++)
                    {
                        int at = ((y + PostY) * tw + x + PostX) * 4 + k, d = Math.Abs(a[at] - b[at]);
                        if (d > worst) { worst = d; wx = x; wy = y; }
                    }
            int allowed = dither > 0f ? 2 : 1;
            check.Expect(worst <= allowed, $"{name}: the fused target differs from the fragment chain's by {worst} steps at ({wx},{wy})");
            ReadOnlySpan<Half> fa = MemoryMarshal.Cast<byte, Half>(ha), fb = MemoryMarshal.Cast<byte, Half>(hb);
            float hw = 0f;
            for (int i = 0; i < fa.Length; i++)
            {
                float va = (float)fa[i], vb = (float)fb[i];
                hw = MathF.Max(hw, MathF.Abs(va - vb) / MathF.Max(1f, MathF.Abs(va)));
            }
            check.Expect(hw <= PostHistTol, $"{name}: the fused history differs from the fragment chain's by {hw:0.#####} (relative)");
            log.Info($"GpuGolden: {name}, worst target step {worst}, worst history {hw:0.#####}");
        }
        finally
        {
            vk.Destroy(rb);
            foreach (ImageId i in (ReadOnlySpan<ImageId>)[cur, vel, depth, hist, histOut[0], histOut[1], into[0], into[1]]) vk.Destroy(i);
        }
    }

    /// <summary>The oracle: the accumulate into <paramref name="histOut"/>, then the taa stage's copy-out with its sharpen
    /// and the dither into <paramref name="into"/> at the case's rect, as the post stack draws the pair.</summary>
    private static bool PostTaaFragment(VkBackend vk, ExtenderLog log, ImageId cur, ImageId depth, ImageId vel, ImageId hist,
                                        ImageId histOut, ImageId into, float blend, Vector2 jitter, bool upsample, float sharp,
                                        float dither)
    {
        if (!PostChainVk.Ensure(vk, log)) return false;
        int lBlend = -1, lJitter = -1, lUp = -1, lDither = -1, lSharp = -1;
        GuiVkProg? acc = PostChainVk.Program(vk, PostChain.FullscreenVs, ShaderSource.Get("cx_taa_f.glsl"), "golden taa accumulate", p =>
        {
            lBlend = p.Loc("uBlend"); lJitter = p.Loc("uJitterPx"); lUp = p.Loc("uUpsample");
        }, log);
        GuiVkProg? stage = PostChainVk.Program(vk, ShaderStages.Source("cx_post_vs"), ShaderStages.Source("cx_taa_fs"), "golden taa stage", p =>
        {
            lDither = p.Loc("uDither"); lSharp = p.Loc("uSharp");
        }, log);
        if (acc == null || stage == null) return false;
        var cl = new CommandList("golden post fragment", 1 << 14, log);
        PostChainVk.BeginFrame();
        PostChainVk.Open(acc);
        LooseBlock.Set(lBlend, blend);
        LooseBlock.Set(lUp, upsample ? 1 : 0);
        LooseBlock.Set(lJitter, [jitter.X, jitter.Y]);
        int at = PostChainVk.Close(cl, acc);
        PostChainVk.Draw(cl, acc, Format.Rgba16F, PostChainVk.Blend.None, histOut, LoadOp.DontCare, new Rect(0, 0, PostW, PostH), at,
                         new PostChainVk.Inputs(cur, default, depth, hist, vel, HistLinear: true), log);
        PostChainVk.Open(stage);
        LooseBlock.Set(lDither, dither);
        LooseBlock.Set(lSharp, sharp);
        at = PostChainVk.Close(cl, stage);
        PostChainVk.Draw(cl, stage, Format.Rgba8, PostChainVk.Blend.None, into, LoadOp.Clear, new Rect(PostX, PostY, PostW, PostH), at,
                         new PostChainVk.Inputs(cur, default, depth, histOut), log);
        return vk.Submit(cl);
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
