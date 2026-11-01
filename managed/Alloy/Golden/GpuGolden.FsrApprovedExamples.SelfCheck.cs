using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// FSR 3.1's approved example on the device (approved-spec), E9: a scene panning at 0.37 by 0.11 display pixels a
/// frame, drawn at 640x360 with Alloy's jitter and velocity convention, upscaled over 48 frames to 1280x720. Oracle:
/// the same scene supersampled 8x8 on the CPU, against which FSR beats a bilinear upscale and a flipped vertical
/// motion sign loses to it.
/// </summary>
internal static partial class GpuGolden
{
    private const int FsrRw = 640, FsrRh = 360, FsrDw = 1280, FsrDh = 720, FsrFrames = 48, FsrBorder = 16;
    private const float FsrPanX = 0.37f, FsrPanY = 0.11f;

    /// <summary>The scene at a display-space point: a 7-pixel checkerboard turned 0.3 rad, half and half with a zone plate.</summary>
    private static float FsrScene(float x, float y)
    {
        const float c = 0.9553365f, s = 0.29552022f;
        float u = x * c + y * s, w = -x * s + y * c;
        float check = (((int)MathF.Floor(u / 7f) + (int)MathF.Floor(w / 7f)) & 1) == 0 ? 0f : 1f;
        float dx = x - 640f, dy = y - 360f;
        return 0.5f * check + 0.25f + 0.25f * MathF.Cos((dx * dx + dy * dy) * 0.002f);
    }

    static partial void FsrSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        string aa = Post.Aa.V;
        float scale = Post.Scale.V;
        Post.Aa.V = Post.AaFsr; Post.Scale.V = 0.5f;
        ImageId color = PostImage(vk, Format.Rgba16F, FsrRw, FsrRh, ImageUse.Sampled | ImageUse.CopyDst, "golden fsr colour");
        ImageId depth = PostImage(vk, Format.R32F, FsrRw, FsrRh, ImageUse.Sampled | ImageUse.CopyDst, "golden fsr depth");
        ImageId vel = PostImage(vk, Format.Rg16F, FsrRw, FsrRh, ImageUse.Sampled | ImageUse.CopyDst, "golden fsr velocity");
        ImageId output = PostImage(vk, Format.Rgba16F, FsrDw, FsrDh, ImageUse.Sampled | ImageUse.Storage | ImageUse.CopySrc, "golden fsr output");
        BufferId rb = vk.CreateBuffer(new BufferDesc(FsrDw * FsrDh * 8, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden fsr read"));
        try
        {
            string? why = vk.FsrReady(FsrRw, FsrRh, FsrDw, FsrDh);
            check.Expect(why == null, $"E9: FSR is ready on this GPU ({why})");
            bool made = !color.IsNull && !depth.IsNull && !vel.IsNull && !output.IsNull && !rb.IsNull;
            check.Expect(made, "E9: the golden's images or readback were refused (logged above)");
            if (why != null || !made) return;
            var d = new float[FsrRw * FsrRh];
            Array.Fill(d, 0.5f);
            var v = new Half[FsrRw * FsrRh * 2];
            for (int i = 0; i < v.Length; i += 2) { v[i] = (Half)(FsrPanX / 2f); v[i + 1] = (Half)(FsrPanY / 2f); }
            if (!vk.Upload(depth, 0, 0, MemoryMarshal.AsBytes(d.AsSpan())) || !vk.Upload(vel, 0, 0, MemoryMarshal.AsBytes(v.AsSpan())))
            {
                check.Expect(false, "E9: the golden's depth or velocity upload was refused (logged above)");
                return;
            }
            float[] truth = FsrTruth();
            float bil = FsrPsnr(FsrBilinear(), truth);
            byte[]? up = FsrRun(vk, log, color, depth, vel, output, rb, flip: false);
            byte[]? flipped = FsrRun(vk, log, color, depth, vel, output, rb, flip: true);
            check.Expect(up != null && flipped != null, "E9: an FSR run was refused (logged above)");
            if (up == null || flipped == null) return;
            float fsrDb = FsrPsnr(FsrGrey(up), truth), flipDb = FsrPsnr(FsrGrey(flipped), truth);
            log.Info($"GpuGolden: E9 FSR {fsrDb:F2} dB, flipped vertical motion {flipDb:F2} dB, bilinear {bil:F2} dB");
            check.Expect(fsrDb > bil, $"E9: FSR ({fsrDb:F2} dB) is closer to the supersampled scene than a bilinear upscale ({bil:F2} dB)");
            check.Expect(flipDb < bil, $"E9: with the vertical motion sign flipped FSR ({flipDb:F2} dB) comes out worse than bilinear ({bil:F2} dB)");
        }
        finally
        {
            vk.Destroy(rb); vk.Destroy(output); vk.Destroy(vel); vk.Destroy(depth); vk.Destroy(color);
            Post.Aa.V = aa; Post.Scale.V = scale;
        }
    }

    /// <summary>48 frames through FSR, each drawn with this frame's jitter; the last output read back, or null.</summary>
    private static byte[]? FsrRun(VkBackend vk, ExtenderLog log, ImageId color, ImageId depth, ImageId vel, ImageId output, BufferId rb, bool flip)
    {
        var px = new Half[FsrRw * FsrRh * 4];
        for (int n = 0; n < FsrFrames; n++)
        {
            PostChain.Jitter(n, FsrRw, FsrRh, PostChain.JitterPhases(FsrDw), out float jx, out float jy);
            float ox = n * FsrPanX, oy = n * FsrPanY;
            for (int j = 0; j < FsrRh; j++)
                for (int i = 0; i < FsrRw; i++)
                {
                    float sx = i + 0.5f - jx * FsrRw / 2f, sy = j + 0.5f - jy * FsrRh / 2f;
                    var g = (Half)FsrScene(sx * 2f + ox, sy * 2f + oy);
                    int at = (j * FsrRw + i) * 4;
                    px[at] = px[at + 1] = px[at + 2] = g; px[at + 3] = (Half)1f;
                }
            if (!vk.Upload(color, 0, 0, MemoryMarshal.AsBytes(px.AsSpan()))) return null;
            UpscaleCmd cmd = Fsr.Command(color, depth, vel, default, output, FsrRw, FsrRh, jx, jy, 16.6f, 0.1f, 100f, 1f, n == 0);
            if (flip) cmd = cmd with { MvScaleY = -cmd.MvScaleY };
            var cl = new CommandList("golden fsr", 1 << 10, log);
            cl.Upscale(cmd);
            if (!vk.Submit(cl)) return null;
        }
        return PostRead(vk, log, output, FsrDw, FsrDh, 8, rb);
    }

    /// <summary>The last frame's scene, 8x8 samples per display pixel.</summary>
    private static float[] FsrTruth()
    {
        var t = new float[FsrDw * FsrDh];
        float ox = (FsrFrames - 1) * FsrPanX, oy = (FsrFrames - 1) * FsrPanY;
        for (int y = 0; y < FsrDh; y++)
            for (int x = 0; x < FsrDw; x++)
            {
                float sum = 0f;
                for (int b = 0; b < 8; b++)
                    for (int a = 0; a < 8; a++) sum += FsrScene(x + (a + 0.5f) / 8f + ox, y + (b + 0.5f) / 8f + oy);
                t[y * FsrDw + x] = sum / 64f;
            }
        return t;
    }

    /// <summary>The last frame drawn unjittered at 640x360 and upscaled bilinearly.</summary>
    private static float[] FsrBilinear()
    {
        var src = new float[FsrRw * FsrRh];
        float ox = (FsrFrames - 1) * FsrPanX, oy = (FsrFrames - 1) * FsrPanY;
        for (int j = 0; j < FsrRh; j++)
            for (int i = 0; i < FsrRw; i++) src[j * FsrRw + i] = FsrScene((i + 0.5f) * 2f + ox, (j + 0.5f) * 2f + oy);
        var o = new float[FsrDw * FsrDh];
        for (int y = 0; y < FsrDh; y++)
            for (int x = 0; x < FsrDw; x++)
            {
                float u = Math.Clamp((x + 0.5f) / 2f - 0.5f, 0f, FsrRw - 1f), w = Math.Clamp((y + 0.5f) / 2f - 0.5f, 0f, FsrRh - 1f);
                int i0 = (int)u, j0 = (int)w, i1 = Math.Min(i0 + 1, FsrRw - 1), j1 = Math.Min(j0 + 1, FsrRh - 1);
                float fu = u - i0, fw = w - j0;
                float top = src[j0 * FsrRw + i0] * (1 - fu) + src[j0 * FsrRw + i1] * fu;
                float bot = src[j1 * FsrRw + i0] * (1 - fu) + src[j1 * FsrRw + i1] * fu;
                o[y * FsrDw + x] = top * (1 - fw) + bot * fw;
            }
        return o;
    }

    /// <summary>The red channel of a read-back RGBA16F image.</summary>
    private static float[] FsrGrey(byte[] rgba)
    {
        ReadOnlySpan<Half> h = MemoryMarshal.Cast<byte, Half>(rgba);
        var g = new float[FsrDw * FsrDh];
        for (int i = 0; i < g.Length; i++) g[i] = (float)h[i * 4];
        return g;
    }

    /// <summary>PSNR over the display, 16 pixels of border left out.</summary>
    private static float FsrPsnr(float[] a, float[] truth)
    {
        double se = 0;
        int n = 0;
        for (int y = FsrBorder; y < FsrDh - FsrBorder; y++)
            for (int x = FsrBorder; x < FsrDw - FsrBorder; x++)
            {
                double e = a[y * FsrDw + x] - truth[y * FsrDw + x];
                se += e * e; n++;
            }
        return (float)(10 * Math.Log10(1.0 / Math.Max(se / n, 1e-12)));
    }
}
