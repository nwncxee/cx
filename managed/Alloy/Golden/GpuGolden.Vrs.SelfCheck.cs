using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The shading-rate golden's runner, and its feedback case: a tile the rate image shaded 2x2 last frame, whose
/// blocks are flat inside but differ from block to block, goes back to one invocation per pixel.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>Scene values at perceptual 0.54 and 0.61 (0.2916 / 0.7084, 0.3721 / 0.6279), worked by hand.</summary>
    private const float VrsP54 = 0.411632f, VrsP61 = 0.592610f;

    /// <summary>What a tile shaded 2x2 looks like: flat 2x2 blocks in a checkerboard of perceptual 0.50 against 0.61
    /// (tile 0) or against 0.54 (tile 1).</summary>
    private static float VrsCoarsePixel(int t, int x, int y)
        => (((x >> 1) + (y >> 1)) & 1) == 0 ? VrsP50 : t == 0 ? VrsP61 : VrsP54;

    /// <summary>Oracle: hand-worked rates. A step of s between neighbouring 2x2 blocks is a gradient of s / 2 per
    /// pixel: 0.055 for tile 0, over the 0.03 threshold on both axes, so 1x1 (0); 0.02 for tile 1, under it, so it
    /// stays 2x2 (5). Inside each block the step is 0, which alone would lock a coarse tile coarse.</summary>
    private static void VrsCoarseSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        byte[]? got = VrsRates(vk, log, 2, VrsCoarsePixel);
        check.Expect(got != null, "the coarse-feedback golden's pipeline, images or readback were refused (logged above)");
        if (got == null) return;
        check.Expect(got[0] == 0, $"a coarse-shaded tile whose blocks step 0.11 reads rate {got[0]}, not full rate (0)");
        check.Expect(got[1] == 5, $"a coarse-shaded tile whose blocks step 0.04 reads rate {got[1]}, not 2x2 (5)");
        log.Info($"GpuGolden: shading rate, {VrsTiles} pattern tiles and 2 coarse-shaded ones, rates {Convert.ToHexString(got.AsSpan(0, 2))}");
    }

    /// <summary>The rate <c>cx_vrs_cs</c> picks for each of <paramref name="tiles"/> 16x16 tiles side by side, the
    /// scene's grey level at each pixel from <paramref name="pixel"/> (tile, x, y in the tile); null when refused.</summary>
    private static byte[]? VrsRates(VkBackend vk, ExtenderLog log, int tiles, Func<int, int, int, float> pixel)
    {
        int w = tiles * VrsEdge, h = VrsEdge;
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float l = pixel(x / VrsEdge, x % VrsEdge, y);
                int at = (y * w + x) * 4;
                px[at] = px[at + 1] = px[at + 2] = l;
                px[at + 3] = 1f;
            }
        PipelineId pipe = Compute(vk, ShaderSource.Get("cx_vrs_cs.glsl"), "golden shading rate", out ShaderId cs);
        ImageId scene = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32F, w, h, 1, 1, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst, "golden rate scene"));
        ImageId rate = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8Ui, tiles, 1, 1, 1, 1, 1,
            ImageUse.Storage | ImageUse.CopySrc, "golden rate image"));
        int words = (tiles + 3) & ~3;
        BufferId outb = vk.CreateBuffer(new BufferDesc(words, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden rate read"));
        byte[]? got = null;
        if (!pipe.IsNull && !scene.IsNull && !rate.IsNull && !outb.IsNull && vk.Upload(scene, 0, 0, MemoryMarshal.AsBytes(px.AsSpan())))
        {
            var cl = new CommandList("golden shading rate", 1 << 12, log);
            cl.ReadImages([scene]);
            cl.SetPipeline(pipe);
            cl.BindSet(0, new BindSet(default, default, [new ImageBinding(0, scene)], [new ImageBinding(0, rate)]));
            cl.PushConstants(0, MemoryMarshal.AsBytes((ReadOnlySpan<int>)[BitConverter.SingleToInt32Bits(VrsContrast), VrsEdge]));
            cl.Dispatch(tiles, 1);
            cl.CopyImageToBuffer(new CopyToBufferCmd(rate, new ImageRegion(0, 0, 0, 0), tiles, 1, outb, 0));
            var bytes = new byte[words];
            if (vk.Submit(cl) && vk.ReadBack(outb, bytes)) got = bytes;
        }
        vk.Destroy(outb);
        vk.Destroy(rate);
        vk.Destroy(scene);
        vk.Destroy(pipe);
        vk.Destroy(cs);
        return got;
    }
}
