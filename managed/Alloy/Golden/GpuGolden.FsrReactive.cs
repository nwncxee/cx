using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The harness behind the reactive golden's FSR example: real FSR 3.1 dispatches over a still camera, a history of
/// black frames and one that turned white, the reactive input filled flat, answering with the upscaled value at the
/// display's centre. White arrives as a one-texel lattice, not a sheet: FSR rectifies the history into a box drawn
/// from the current frame's neighbourhood, and a sheet's box clamps the black history to white at any weight.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>Black history frames before the one that turns white. Long enough that an unmasked run's history
    /// still outvotes the new frame, which is the thing the mask has to overcome.</summary>
    private const int ReactFrames = 12;

    static partial void FsrReactiveRun(VkBackend vk, ExtenderLog log, float mask, ref float got)
    {
        string aa = Post.Aa.V;
        float scale = Post.Scale.V;
        Post.Aa.V = Post.AaFsr; Post.Scale.V = 0.5f;
        ImageId color = PostImage(vk, Format.Rgba16F, FsrRw, FsrRh, ImageUse.Sampled | ImageUse.CopyDst, "golden reactive colour");
        ImageId depth = PostImage(vk, Format.R32F, FsrRw, FsrRh, ImageUse.Sampled | ImageUse.CopyDst, "golden reactive depth");
        ImageId vel = PostImage(vk, Format.Rg16F, FsrRw, FsrRh, ImageUse.Sampled | ImageUse.CopyDst, "golden reactive velocity");
        ImageId react = PostImage(vk, Format.R8, FsrRw, FsrRh, ImageUse.Sampled | ImageUse.CopyDst, "golden reactive mask");
        ImageId output = PostImage(vk, Format.Rgba16F, FsrDw, FsrDh, ImageUse.Sampled | ImageUse.Storage | ImageUse.CopySrc, "golden reactive output");
        BufferId rb = vk.CreateBuffer(new BufferDesc(FsrDw * FsrDh * 8, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden reactive read"));
        try
        {
            // every refusal leaves got at NaN, which E5 reports as a failure; each says why first, so the
            // log names the step rather than leaving a bare NaN to read
            if (vk.FsrReady(FsrRw, FsrRh, FsrDw, FsrDh) is string why) { log.Always($"GpuGolden: reactive, FSR is not ready here ({why})"); return; }
            if (color.IsNull || depth.IsNull || vel.IsNull || react.IsNull || output.IsNull || rb.IsNull)
            { log.Always("GpuGolden: reactive, an image or the readback buffer was refused (logged above)"); return; }
            var d = new float[FsrRw * FsrRh];
            Array.Fill(d, 0.5f);
            var v = new Half[FsrRw * FsrRh * 2];
            var m = new byte[FsrRw * FsrRh];
            Array.Fill(m, (byte)Math.Clamp((int)MathF.Round(mask * 255f), 0, 255));
            if (!vk.Upload(depth, 0, 0, MemoryMarshal.AsBytes(d.AsSpan()))
                || !vk.Upload(vel, 0, 0, MemoryMarshal.AsBytes(v.AsSpan()))
                || !vk.Upload(react, 0, 0, m))
            { log.Always("GpuGolden: reactive, the depth, velocity or mask upload was refused (logged above)"); return; }
            var px = new Half[FsrRw * FsrRh * 4];
            for (int n = 0; n < ReactFrames + 1; n++)
            {
                for (int j = 0; j < FsrRh; j++)
                    for (int i = 0; i < FsrRw; i++)
                    {
                        var g = (Half)(n == ReactFrames && ((i + j) & 1) == 0 ? 1f : 0f);
                        int at = (j * FsrRw + i) * 4;
                        px[at] = px[at + 1] = px[at + 2] = g; px[at + 3] = (Half)1f;
                    }
                if (!vk.Upload(color, 0, 0, MemoryMarshal.AsBytes(px.AsSpan())))
                { log.Always($"GpuGolden: reactive, frame {n}'s colour upload was refused (logged above)"); return; }
                PostChain.Jitter(n, FsrRw, FsrRh, PostChain.JitterPhases(FsrDw), out float jx, out float jy);
                var cl = new CommandList("golden reactive fsr", 1 << 10, log);
                cl.Upscale(Fsr.Command(color, depth, vel, default, output, FsrRw, FsrRh, jx, jy, 16.6f, 0.1f, 100f, 1f,
                                       n == 0, react));
                if (!vk.Submit(cl)) { log.Always($"GpuGolden: reactive, frame {n}'s upscale was refused (logged above)"); return; }
            }
            if (PostRead(vk, log, output, FsrDw, FsrDh, 8, rb) is not { } bytes)
            { log.Always("GpuGolden: reactive, the output read-back was refused (logged above)"); return; }
            ReadOnlySpan<Half> o = MemoryMarshal.Cast<byte, Half>(bytes);
            got = (float)o[((FsrDh / 2) * FsrDw + FsrDw / 2) * 4];
        }
        finally
        {
            vk.Destroy(rb); vk.Destroy(output); vk.Destroy(react); vk.Destroy(vel); vk.Destroy(depth); vk.Destroy(color);
            Post.Aa.V = aa; Post.Scale.V = scale;
        }
    }
}
