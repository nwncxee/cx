using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The visibility accumulator over a flat surface, a uniform estimate and a history written by hand. Oracle: the blend
/// arithmetic restated here -- the history's share falls off over a tenth of the pixel's own view distance, the history
/// is first clamped into the estimate's 3x3 range widened by a tenth, and the camera's reprojection moves the texel the
/// history is read from.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>One texel of the accumulator's history pair: the visibility it answered with and the view distance it
    /// answered at.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct AccPx(Half Vis, Half Dist);

    private const int AccSide = 32;
    /// <summary>The flat fixture: one view distance everywhere, under the same folded reversed-depth projection the
    /// contact golden uses, so the stored depth is <c>near / distance</c>.</summary>
    private const float AccNear = 0.1f, AccDist = 10f, AccProjScale = 8f;
    /// <summary>The estimate the accumulator blends over, as the R8 byte the stage samples it as.</summary>
    private const byte AccRawByte = 128;
    /// <summary>The share the golden drives the blend at, and the history visibility it drives it with.</summary>
    private const float AccWeight = 0.75f, AccHist = 0.6f;
    /// <summary>A history far outside the estimate's own range: the clamp must pull it back to the pad's edge.</summary>
    private const float AccStale = 1f;
    /// <summary>The accumulator's own two numbers, restated as the oracle: the share of the pixel's view distance a
    /// history may differ by before it is another surface, and the pad on the neighbourhood clamp.</summary>
    private const float AccDisocclude = 0.1f, AccPad = 0.1f;
    /// <summary>The reprojection the camera case drives: this many texels to the right in the history.</summary>
    private const int AccShift = 4;
    /// <summary>Where the nearest-fetch case's reprojection lands, in texels: past the next texel's centre, so a
    /// nearest fetch and a filtered one cannot answer alike.</summary>
    private const float AccNearestShift = 0.6f;
    /// <summary>What a texel may differ from the oracle by: the R8 estimate and the Rg16F history each quantise.</summary>
    private const double AccTol = 2e-3;

    private static void AoAccumSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        PipelineId accum = SsaoPassVk.AccumPipeline(vk, log);
        SamplerId linear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
        check.Expect(!accum.IsNull && !linear.IsNull, "the accumulator golden's stage and sampler build for the device");
        if (accum.IsNull || linear.IsNull) { vk.Destroy(accum); vk.Destroy(linear); return; }
        float raw = AccRawByte / 255f;
        try
        {
            var cases = new (string Name, float Hist, float Dist, float Weight, double Want)[]
            {
                ("weight 0 takes the frame whole", AccHist, AccDist, 0f, raw),
                ("a history at the pixel's own distance blends", AccHist, AccDist, AccWeight, AccBlend(raw, AccHist, AccWeight)),
                ("a stale history is clamped into the neighbourhood", AccStale, AccDist, AccWeight, AccBlend(raw, raw + AccPad, AccWeight)),
                ("a disoccluded history is dropped", AccHist, AccDist * 1.3f, AccWeight, raw),
                ("a history half a tolerance away keeps half its share", AccHist, AccDist * 1.05f, AccWeight, AccBlend(raw, AccHist, AccWeight * 0.5f)),
            };
            foreach ((string name, float hist, float dist, float weight, double want) in cases)
            {
                (float[] Vis, float[] Dist)? got = AccRun(vk, log, accum, linear, AccFill(hist), AccFill(dist), Matrix4x4.Identity, weight);
                check.Expect(got != null, $"AO accumulator golden: {name} -- the draw or readback was refused (logged above)");
                if (got == null) continue;
                (float least, float most) = AccRange(got.Value.Vis);
                Near(check, $"AO accumulator golden: {name}", (least + most) / 2.0, want, AccTol);
                check.Expect(most - least < AccTol, $"AO accumulator golden: {name} is even across the flat surface, {least:F4}..{most:F4}");
                Near(check, $"AO accumulator golden: {name} stores the distance it was computed at", got.Value.Dist[AccAt(16, 16)], AccDist, 1e-2);
            }
            AccCameraCheck(vk, log, accum, linear, check, raw, cases.Length);
            AccNearestCheck(vk, log, accum, linear, check, raw);
        }
        finally { vk.Destroy(accum); vk.Destroy(linear); }
    }

    /// <summary>The disocclusion test's nearest fetch: a stored distance that alternates per column, read through a
    /// reprojection landing <see cref="AccNearestShift"/> of a texel past its own. A nearest fetch takes the
    /// neighbour's distance whole, so the share alternates with it; a filtered one would average the two neighbours
    /// and leave every column a middling share.</summary>
    private static void AccNearestCheck(VkBackend vk, ExtenderLog log, PipelineId accum, SamplerId linear, Checker check, float raw)
    {
        var dist = new float[AccSide * AccSide];
        for (int y = 0; y < AccSide; y++)
            for (int x = 0; x < AccSide; x++)
                dist[AccAt(x, y)] = AccColumnDist(x);
        Matrix4x4 shift = Matrix4x4.CreateTranslation(2f * AccNearestShift / AccSide, 0f, 0f);
        (float[] Vis, float[] Dist)? got = AccRun(vk, log, accum, linear, AccFill(AccHist), dist, shift, AccWeight);
        check.Expect(got != null, "AO accumulator golden: the nearest-fetch case's draw was refused (logged above)");
        if (got == null) return;
        for (int x = 4; x < AccSide - 4; x++)
        {
            bool kept = AccColumnDist(x + 1) == AccDist;
            Near(check, $"AO accumulator golden: the disocclusion test reads column {x}'s own neighbour, {(kept ? "which agrees" : "which is another surface")}",
                 got.Value.Vis[AccAt(x, 16)], kept ? AccBlend(raw, AccHist, AccWeight) : raw, AccTol);
        }
        log.Info("GpuGolden: ao accumulator, the disocclusion test's nearest fetch over a per-column distance");
    }

    /// <summary>The history distance per column: this surface's own, or far enough past it to be another.</summary>
    private static float AccColumnDist(int x) => (x & 1) == 0 ? AccDist : AccDist * 1.3f;

    /// <summary>The camera case: a history whose visibility ramps across the surface, read through a reprojection that
    /// slides it <see cref="AccShift"/> texels, so each pixel answers with the ramp at its own shifted texel.</summary>
    private static void AccCameraCheck(VkBackend vk, ExtenderLog log, PipelineId accum, SamplerId linear, Checker check,
                                       float raw, int blends)
    {
        var vis = new float[AccSide * AccSide];
        for (int y = 0; y < AccSide; y++)
            for (int x = 0; x < AccSide; x++)
                vis[AccAt(x, y)] = AccRamp(x);
        // a pure NDC translation: the pixel sat this far right last frame, which is half as far in uv
        Matrix4x4 shift = Matrix4x4.CreateTranslation(2f * AccShift / AccSide, 0f, 0f);
        (float[] Vis, float[] Dist)? still = AccRun(vk, log, accum, linear, vis, AccFill(AccDist), Matrix4x4.Identity, AccWeight);
        (float[] Vis, float[] Dist)? moved = AccRun(vk, log, accum, linear, vis, AccFill(AccDist), shift, AccWeight);
        check.Expect(still != null && moved != null, "AO accumulator golden: the camera case's draws were refused (logged above)");
        if (still == null || moved == null) return;
        // only the middle: the reprojection runs off the near edge at the far one, where the sampler clamps
        for (int x = 4; x < AccSide - AccShift - 4; x++)
        {
            Near(check, $"AO accumulator golden: a still camera reads the history at its own texel, column {x}",
                 still.Value.Vis[AccAt(x, 16)], AccBlend(raw, AccRamp(x), AccWeight), AccTol);
            Near(check, $"AO accumulator golden: a camera that moved reads it {AccShift} texels over, column {x}",
                 moved.Value.Vis[AccAt(x, 16)], AccBlend(raw, AccRamp(x + AccShift), AccWeight), AccTol);
        }
        log.Info($"GpuGolden: ao accumulator, {blends} blends and the camera's own reprojection over {AccSide}x{AccSide}");
    }

    /// <summary>The history ramp, inside the pad the clamp allows around a uniform estimate so the ramp itself
    /// survives and the shift is what the answer shows.</summary>
    private static float AccRamp(int x) => (float)(Half)(AccRawByte / 255f - AccPad + 2f * AccPad * x / (AccSide - 1));

    /// <summary>The blend the stage writes: the history's share <paramref name="w"/> of <paramref name="hist"/> over
    /// this frame's <paramref name="raw"/>, both already inside the clamp.</summary>
    private static double AccBlend(float raw, float hist, float w) => raw + ((float)(Half)hist - raw) * w;

    private static int AccAt(int x, int y) => y * AccSide + x;

    private static float[] AccFill(float v)
    {
        var a = new float[AccSide * AccSide];
        a.AsSpan().Fill(v);
        return a;
    }

    /// <summary>The least and greatest visibility over the middle of the target, off the edges the sampler clamps.</summary>
    private static (float Least, float Most) AccRange(float[] vis)
    {
        float least = float.MaxValue, most = float.MinValue;
        for (int y = 4; y < AccSide - 4; y++)
            for (int x = 4; x < AccSide - 4; x++)
            {
                least = MathF.Min(least, vis[AccAt(x, y)]);
                most = MathF.Max(most, vis[AccAt(x, y)]);
            }
        return (least, most);
    }

    /// <summary>One accumulator draw over the flat fixture and the history the arrays hold, at
    /// <paramref name="weight"/> and through <paramref name="reproj"/>; the target's two channels, null when a step
    /// was refused.</summary>
    private static (float[] Vis, float[] Dist)? AccRun(VkBackend vk, ExtenderLog log, PipelineId accum, SamplerId linear,
                                                       float[] histVis, float[] histDist, Matrix4x4 reproj, float weight)
    {
        const int N = AccSide * AccSide;
        ImageId depth = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, AccSide, AccSide, 1, 1, 1, 1,
                                                     ImageUse.Sampled | ImageUse.CopyDst, "golden ao accum depth"));
        ImageId src = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8, AccSide, AccSide, 1, 1, 1, 1,
                                                   ImageUse.Sampled | ImageUse.CopyDst, "golden ao accum estimate"));
        ImageId hist = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg16F, AccSide, AccSide, 1, 1, 1, 1,
                                                    ImageUse.Sampled | ImageUse.CopyDst, "golden ao accum history"));
        ImageId target = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg16F, AccSide, AccSide, 1, 1, 1, 1,
                                                      ImageUse.ColorTarget | ImageUse.CopySrc, "golden ao accum target"));
        BufferId ubo = vk.CreateBuffer(new BufferDesc(Std140.BlockBytes<SsaoBlock>(), BufferUse.Uniform, MemoryKind.Upload, "golden ao accum block"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(N * 4, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden ao accum read"));
        try
        {
            if (depth.IsNull || src.IsNull || hist.IsNull || target.IsNull || ubo.IsNull || rb.IsNull) return null;
            var d = new float[N];
            d.AsSpan().Fill(AccNear / AccDist);
            var est = new byte[N];
            est.AsSpan().Fill(AccRawByte);
            var pair = new AccPx[N];
            for (int i = 0; i < N; i++) pair[i] = new AccPx((Half)histVis[i], (Half)histDist[i]);
            if (!vk.Upload(depth, 0, 0, MemoryMarshal.AsBytes(d.AsSpan())) || !vk.Upload(src, 0, 0, est)
             || !vk.Upload(hist, 0, 0, MemoryMarshal.AsBytes(pair.AsSpan()))) return null;

            var b = new SsaoBlock
            {
                Proj = new Matrix4x4(AccProjScale, 0f, 0f, 0f, 0f, AccProjScale, 0f, 0f, 0f, 0f, 0f, -1f, 0f, 0f, AccNear, 0f),
                Reproj = reproj,
                Rt = new Vector4(1f / AccSide, 1f / AccSide, AccSide, AccSide),
                Weight = weight,
            };
            Span<byte> blk = stackalloc byte[Std140.BlockBytes<SsaoBlock>()];
            blk.Clear();
            MemoryMarshal.Write(blk, in b);
            var cl = new CommandList("golden ao accum", 1 << 12, log);
            cl.UpdateBuffer(ubo, 0, blk);
            cl.ReadImages([depth, src, hist]);
            SsaoPassVk.RecordAccum(cl, accum, target, src, hist, depth, linear, new Rect(0, 0, AccSide, AccSide),
                                   [new(UboSlots.Ssao, ubo)]);
            if (!vk.Submit(cl)) return null;
            byte[]? bytes = PostRead(vk, log, target, AccSide, AccSide, 4, rb);
            if (bytes == null) return null;
            ReadOnlySpan<AccPx> got = MemoryMarshal.Cast<byte, AccPx>(bytes);
            var vis = new float[N];
            var dist = new float[N];
            for (int i = 0; i < N; i++) { vis[i] = (float)got[i].Vis; dist[i] = (float)got[i].Dist; }
            return (vis, dist);
        }
        finally
        {
            vk.Destroy(rb); vk.Destroy(ubo); vk.Destroy(target); vk.Destroy(hist); vk.Destroy(src); vk.Destroy(depth);
        }
    }
}
