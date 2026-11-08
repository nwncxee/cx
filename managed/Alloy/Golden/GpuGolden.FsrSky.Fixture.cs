using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The FSR sky golden's fixture: a still harbour against the sky, a rope thinner than a render pixel and a mast's
/// edge, drawn at 288x162 with Alloy's jitter, rebuilt by FSR 3.1 at 320x180 and resolved, the last frames read back.
/// Its reference is the same scene drawn unjittered at 4x4 samples a pixel through the resolve and averaged.
/// </summary>
internal static partial class GpuGolden
{
    private const int SkyRw = 288, SkyRh = 162, SkyDw = 320, SkyDh = 180, SkyFrames = 48, SkyKept = 16, SkySuper = 4;
    private const float SkyRopeX0 = 40f, SkyRopeY0 = 20f, SkyRopeX1 = 130f, SkyRopeY1 = 160f, SkyRopeHalf = 0.25f, SkyMastX = 220.4f;
    private const float SkyRopeBand = 1.5f, SkySceneDepth = 0.5f;
    /// <summary>The fog fixture's in-scattered light and the share of the far light it keeps: a fog that moves the
    /// sky's shade well clear of the rope's.</summary>
    private const float SkyFogLight = 0.3f, SkyFogKeep = 0.3f;

    /// <summary>The fog fixture's volume edge, in froxels.</summary>
    private const int SkyFogSide = 4;
    private static readonly Vector3 SkyColour = new(0.20f, 0.22f, 0.17f), RopeColour = new(0.12f, 0.12f, 0.11f),
                                     MastColour = new(0.10f, 0.09f, 0.08f);

    /// <summary>The approved examples over this fixture.</summary>
    static partial void FsrResolveApprovedSelfCheck(VkBackend vk, Checker check, ExtenderLog log);

    /// <summary>The last <see cref="SkyKept"/> resolved frames, and the supersampled reference, in 0..255 per channel.</summary>
    private sealed class SkyRun
    {
        public readonly Vector3[][] Frames = new Vector3[SkyKept][];
        public Vector3[] Truth = [];
    }

    /// <summary>How far display point (<paramref name="x"/>, <paramref name="y"/>) is from the rope's centre line.</summary>
    private static float SkyRopeDist(float x, float y)
    {
        float dx = SkyRopeX1 - SkyRopeX0, dy = SkyRopeY1 - SkyRopeY0;
        float t = Math.Clamp(((x - SkyRopeX0) * dx + (y - SkyRopeY0) * dy) / (dx * dx + dy * dy), 0f, 1f);
        float ex = x - (SkyRopeX0 + t * dx), ey = y - (SkyRopeY0 + t * dy);
        return MathF.Sqrt(ex * ex + ey * ey);
    }

    /// <summary>The scene at a display point: its colour and its reversed world depth, 0 for the sky.</summary>
    private static (Vector3 C, float D) SkyScene(float x, float y)
    {
        if (x >= SkyMastX) return (MastColour, SkySceneDepth);
        if (SkyRopeDist(x, y) <= SkyRopeHalf) return (RopeColour, SkySceneDepth);
        return (SkyColour, 0f);
    }

    /// <summary>The scene point-sampled at <paramref name="w"/> x <paramref name="h"/> over the display, offset by
    /// <paramref name="jxPx"/>, <paramref name="jyPx"/> pixels the way the jitter moves a frame, the camera turned
    /// <paramref name="turn"/> display pixels along the rows.</summary>
    private static (Half[] Colour, float[] Depth) SkyDraw(int w, int h, float jxPx, float jyPx, float turn = 0f)
    {
        var c = new Half[w * h * 4];
        var d = new float[w * h];
        float sx = (float)SkyDw / w, sy = (float)SkyDh / h;
        for (int j = 0; j < h; j++)
            for (int i = 0; i < w; i++)
            {
                (Vector3 col, float z) = SkyScene((i + 0.5f - jxPx) * sx + turn, (j + 0.5f - jyPx) * sy);
                int p = j * w + i, at = p * 4;
                c[at] = (Half)col.X; c[at + 1] = (Half)col.Y; c[at + 2] = (Half)col.Z; c[at + 3] = (Half)1f;
                d[p] = z;
            }
        return (c, d);
    }

    /// <summary>The scene through FSR 3.1 and the resolve, <see cref="SkyFrames"/> still frames, and (<paramref name="truth"/>)
    /// its reference; null when a step was refused (logged). <paramref name="fog"/> lays a uniform volumetric fog,
    /// <paramref name="dome"/> draws the area with a sky dome, and <paramref name="seed"/>, when given, fills the coverage
    /// history first; <paramref name="pan"/> turns the camera by that many display pixels a frame.</summary>
    private static unsafe SkyRun? SkyRunScene(VkBackend vk, Checker check, ExtenderLog log, bool fog, bool dome,
                                             Half? seed = null, bool truth = true, float pan = 0f)
    {
        string aa = Post.Aa.V;
        float scale = Post.Scale.V, bloom = Post.Bloom.V;
        bool skyRaw = Post.SkyRaw.V;
        Matrix4x4 skyInv = AlloyRenderer.SkyInvVp;
        float keepJx = PostChain.JitterX, keepJy = PostChain.JitterY;
        int sw = SkyDw * SkySuper, sh = SkyDh * SkySuper;
        ImageId color = PostImage(vk, Format.Rgba16F, SkyRw, SkyRh, ImageUse.Sampled | ImageUse.CopyDst, "golden sky colour");
        ImageId depth = PostImage(vk, Format.R32F, SkyRw, SkyRh, ImageUse.Sampled | ImageUse.CopyDst, "golden sky depth");
        ImageId vel = PostImage(vk, Format.Rg16F, SkyRw, SkyRh, ImageUse.Sampled | ImageUse.CopyDst, "golden sky velocity");
        ImageId up = PostImage(vk, Format.Rgba16F, SkyDw, SkyDh, ImageUse.Sampled | ImageUse.Storage | ImageUse.CopySrc, "golden sky fsr");
        ImageId into = PostImage(vk, Format.Rgba8, SkyDw, SkyDh, ImageUse.ColorTarget | ImageUse.CopySrc, "golden sky resolve");
        ImageId sColor = PostImage(vk, Format.Rgba16F, sw, sh, ImageUse.Sampled | ImageUse.CopyDst, "golden sky truth colour");
        ImageId sDepth = PostImage(vk, Format.R32F, sw, sh, ImageUse.Sampled | ImageUse.CopyDst, "golden sky truth depth");
        ImageId sInto = PostImage(vk, Format.Rgba8, sw, sh, ImageUse.ColorTarget | ImageUse.CopySrc, "golden sky truth resolve");
        ImageId vol = vk.CreateImage(new ImageDesc(ImageKind.Tex3D, Format.Rgba16F, SkyFogSide, SkyFogSide, SkyFogSide, 1, 1, 1,
                                                   ImageUse.Sampled | ImageUse.CopyDst, "golden sky fog"));
        BufferId rb = vk.CreateBuffer(new BufferDesc(sw * sh * 4, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden sky read"));
        try
        {
            Post.Aa.V = Post.AaFsr; Post.Scale.V = 0.9f; Post.Bloom.V = 0f; Post.SkyRaw.V = true;
            AlloyRenderer.SkyInvVp = dome ? Matrix4x4.Identity : default;
            string? why = vk.FsrReady(SkyRw, SkyRh, SkyDw, SkyDh);
            bool made = AlloyRenderer.VkFixtureEnsure(vk, SkyDw, SkyDh, log) && !color.IsNull && !depth.IsNull && !vel.IsNull && !up.IsNull && !into.IsNull && !sColor.IsNull
                     && !sDepth.IsNull && !sInto.IsNull && !vol.IsNull && !rb.IsNull;
            check.Expect(why == null && made, $"FSR sky: FSR is ready ({why}) and the golden's images were made (logged above)");
            if (why != null || !made) return null;
            if (fog)
            {
                var v = new Half[SkyFogSide * SkyFogSide * SkyFogSide * 4];
                for (int i = 0; i < v.Length; i += 4) { v[i] = v[i + 1] = v[i + 2] = (Half)SkyFogLight; v[i + 3] = (Half)SkyFogKeep; }
                if (!vk.Upload(vol, 0, 0, MemoryMarshal.AsBytes(v.AsSpan()))) return null;
                AlloyRenderer.VkFogFixture(vk, vol);
                FogVolume.FixtureBuilt(SkyFogSide);
            }
            var mv = new Half[SkyRw * SkyRh * 2];
            for (int i = 0; i < mv.Length; i += 2) mv[i] = (Half)(pan * SkyRw / SkyDw);
            if (!vk.Upload(vel, 0, 0, MemoryMarshal.AsBytes(mv.AsSpan()))) return null;
            if (seed is Half h && !PostStack.SkyCoverFixture(vk, SkyDw, SkyDh, h)) return null;
            var run = new SkyRun();
            int* full = stackalloc int[4] { 0, 0, SkyDw, SkyDh };
            for (int n = 0; n < SkyFrames; n++)
            {
                PostChain.Jitter(n, SkyRw, SkyRh, PostChain.JitterPhases(SkyDw), out float jx, out float jy);
                PostChain.JitterX = jx; PostChain.JitterY = jy;
                (Half[] c, float[] d) = SkyDraw(SkyRw, SkyRh, jx * SkyRw / 2f, jy * SkyRh / 2f, n * pan);
                if (!vk.Upload(color, 0, 0, MemoryMarshal.AsBytes(c.AsSpan())) || !vk.Upload(depth, 0, 0, MemoryMarshal.AsBytes(d.AsSpan())))
                    return null;
                var cl = new CommandList("golden sky", 1 << 14, log);
                PostChainVk.BeginFrame();
                cl.Upscale(Fsr.Command(color, depth, vel, default, up, SkyRw, SkyRh, jx, jy, 16.6f, 0.1f, 100f, 1f, n == 0));
                PostChainVk.SkyInput sky = PostStack.FsrSkyVk(vk, cl, depth, vel, SkyRw, SkyRh, SkyDw, SkyDh, n == 0, log);
                if (!PostChainVk.Record(vk, cl, up, sky, into, false, full, full, default, log) || !vk.Submit(cl)) return null;
                int k = n - (SkyFrames - SkyKept);
                if (k < 0) continue;
                byte[]? got = PostRead(vk, log, into, SkyDw, SkyDh, 4, rb);
                if (got == null) return null;
                run.Frames[k] = SkyLevels(got);
            }
            if (!truth) return run;
            (Half[] tc, float[] td) = SkyDraw(sw, sh, 0f, 0f, (SkyFrames - 1) * pan);
            if (!vk.Upload(sColor, 0, 0, MemoryMarshal.AsBytes(tc.AsSpan())) || !vk.Upload(sDepth, 0, 0, MemoryMarshal.AsBytes(td.AsSpan())))
                return null;
            int* big = stackalloc int[4] { 0, 0, sw, sh };
            var tl = new CommandList("golden sky truth", 1 << 14, log);
            PostChainVk.BeginFrame();
            if (!PostChainVk.Record(vk, tl, sColor, new PostChainVk.SkyInput(sDepth), sInto, false, big, big, default, log) || !vk.Submit(tl))
                return null;
            byte[]? big8 = PostRead(vk, log, sInto, sw, sh, 4, rb);
            if (big8 == null) return null;
            run.Truth = SkyAverage(SkyLevels(big8), sw);
            return run;
        }
        finally
        {
            if (fog) { FogVolume.NotBuilt(); AlloyRenderer.VkFogFixture(vk, default); }
            PostStack.SkyCoverForget(vk);
            vk.Destroy(rb); vk.Destroy(vol); vk.Destroy(sInto); vk.Destroy(sDepth); vk.Destroy(sColor);
            vk.Destroy(into); vk.Destroy(up); vk.Destroy(vel); vk.Destroy(depth); vk.Destroy(color);
            Post.Aa.V = aa; Post.Scale.V = scale; Post.Bloom.V = bloom; Post.SkyRaw.V = skyRaw;
            AlloyRenderer.SkyInvVp = skyInv;
            PostChain.JitterX = keepJx; PostChain.JitterY = keepJy;
        }
    }

    /// <summary>A read-back 8-bit frame as levels 0..255 a pixel.</summary>
    private static Vector3[] SkyLevels(byte[] bytes)
    {
        ReadOnlySpan<BlitTexel> px = MemoryMarshal.Cast<byte, BlitTexel>(bytes);
        var o = new Vector3[px.Length];
        for (int i = 0; i < px.Length; i++) o[i] = new Vector3(px[i].R, px[i].G, px[i].B);
        return o;
    }

    /// <summary>A <see cref="SkySuper"/>-times frame <paramref name="w"/> wide averaged down to the display, in light
    /// (the encode's gamma undone and redone), levels 0..255.</summary>
    private static Vector3[] SkyAverage(Vector3[] big, int w)
    {
        float g = AlloyLights.SrgbGamma;
        var o = new Vector3[SkyDw * SkyDh];
        for (int y = 0; y < SkyDh; y++)
            for (int x = 0; x < SkyDw; x++)
            {
                Vector3 sum = Vector3.Zero;
                for (int b = 0; b < SkySuper; b++)
                {
                    int row = (y * SkySuper + b) * w, left = x * SkySuper;
                    for (int a = 0; a < SkySuper; a++) sum += SkyPow(big[row + left + a] / 255f, g);
                }
                int at = y * SkyDw + x;
                o[at] = 255f * SkyPow(sum / (SkySuper * SkySuper), 1f / g);
            }
        return o;
    }

    private static Vector3 SkyPow(Vector3 v, float e) => new(MathF.Pow(v.X, e), MathF.Pow(v.Y, e), MathF.Pow(v.Z, e));

    /// <summary>The largest of a difference's three channels, by size.</summary>
    private static float SkyMax(Vector3 d) => MathF.Max(MathF.Abs(d.X), MathF.Max(MathF.Abs(d.Y), MathF.Abs(d.Z)));

    /// <summary>The display pixels within <see cref="SkyRopeBand"/> of the rope, clear of its ends.</summary>
    private static bool SkyInRope(int x, int y) => y >= 25 && y <= 155 && SkyRopeDist(x + 0.5f, y + 0.5f) <= SkyRopeBand;

    /// <summary>The display pixels astride the mast's edge.</summary>
    private static bool SkyInEdge(int x, int y) => x >= 218 && x <= 223 && y >= 10 && y <= 170;

    /// <summary>Open sky: nothing in front of it within ten pixels.</summary>
    private static bool SkyInOpen(int x, int y) => x >= 150 && x <= 200 && y >= 10 && y <= 170;

    /// <summary>The largest change any pixel of <paramref name="where"/> makes between two kept frames in a row, in levels.</summary>
    private static float SkyWorstStep(SkyRun r, Func<int, int, bool> where)
    {
        float worst = 0f;
        for (int f = 1; f < SkyKept; f++)
            for (int y = 0; y < SkyDh; y++)
                for (int x = 0; x < SkyDw; x++)
                {
                    if (!where(x, y)) continue;
                    int at = y * SkyDw + x;
                    worst = MathF.Max(worst, SkyMax(r.Frames[f][at] - r.Frames[f - 1][at]));
                }
        return worst;
    }

    /// <summary>The last frame against the reference over <paramref name="where"/>: the mean signed difference, the
    /// largest difference either way, and each one's darkest level, in levels.</summary>
    private static (float Mean, float Worst, float Darkest, float TruthDarkest) SkyAgainstTruth(SkyRun r, Func<int, int, bool> where)
    {
        Vector3[] last = r.Frames[SkyKept - 1];
        double sum = 0;
        int n = 0;
        float worst = 0f, dark = 255f, tDark = 255f;
        for (int y = 0; y < SkyDh; y++)
            for (int x = 0; x < SkyDw; x++)
            {
                if (!where(x, y)) continue;
                int at = y * SkyDw + x;
                Vector3 d = last[at] - r.Truth[at];
                sum += (d.X + d.Y + d.Z) / 3f; n++;
                worst = MathF.Max(worst, SkyMax(d));
                dark = MathF.Min(dark, last[at].Y); tDark = MathF.Min(tDark, r.Truth[at].Y);
            }
        return ((float)(sum / Math.Max(n, 1)), worst, dark, tDark);
    }
}
