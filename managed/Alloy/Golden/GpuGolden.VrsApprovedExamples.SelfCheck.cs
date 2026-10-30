using System;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The shading-rate golden (approved-spec, examples E1-E6): six hand-made 16x16 tiles through the shipped
/// <c>cx_vrs_cs</c>, each tile's rate read back off the device and held to the rate its approved example names.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>Tiles in the fixture, side by side, and their edge in pixels.</summary>
    private const int VrsTiles = 6, VrsEdge = 16;

    /// <summary>The approved examples' threshold: 0.03 on the 0..1 perceptual scale.</summary>
    private const float VrsContrast = 0.03f;

    /// <summary>Scene values whose perceptual level, sqrt(L / (1 + L)), is 0.50, 0.51 and 0.55: worked by hand
    /// (0.25 / 0.75, 0.2601 / 0.7399, 0.3025 / 0.6975), not computed from the shader's map.</summary>
    private const float VrsP50 = 0.33333334f, VrsP51 = 0.351534f, VrsP55 = 0.433692f;

    /// <summary>Oracle: the approved examples' rates as Vulkan encodes them, (log2 width) &lt;&lt; 2 | log2 height --
    /// E1 flat 2x2 = 5, E2 checkerboard 1x1 = 0, E3 vertical stripes 1x2 = 1, E4 horizontal stripes 2x1 = 4,
    /// E5 a 0.01 step 2x2 = 5, E6 a 0.05 step 1x2 = 1.</summary>
    private static readonly byte[] VrsWant = [5, 0, 1, 4, 5, 1];

    /// <summary>One fixture pixel's scene value: tile <paramref name="t"/> at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static float VrsPixel(int t, int x, int y) => t switch
    {
        0 => 0.5f,
        1 => ((x + y) & 1) == 0 ? 0f : 1f,
        2 => (x & 1) == 0 ? 0f : 1f,
        3 => (y & 1) == 0 ? 0f : 1f,
        4 => (x & 1) == 0 ? VrsP50 : VrsP51,
        _ => (x & 1) == 0 ? VrsP50 : VrsP55,
    };

    private static void VrsSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        const int w = VrsTiles * VrsEdge, h = VrsEdge;
        var px = new float[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float l = VrsPixel(x / VrsEdge, x % VrsEdge, y);
                int at = (y * w + x) * 4;
                px[at] = px[at + 1] = px[at + 2] = l;
                px[at + 3] = 1f;
            }
        PipelineId pipe = Compute(vk, ShaderSource.Get("cx_vrs_cs.glsl"), "golden shading rate", out ShaderId cs);
        ImageId scene = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32F, w, h, 1, 1, 1, 1,
            ImageUse.Sampled | ImageUse.CopyDst, "golden rate scene"));
        ImageId rate = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R8Ui, VrsTiles, 1, 1, 1, 1, 1,
            ImageUse.Storage | ImageUse.CopySrc, "golden rate image"));
        BufferId outb = vk.CreateBuffer(new BufferDesc(8, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "golden rate read"));
        byte[]? got = null;
        if (!pipe.IsNull && !scene.IsNull && !rate.IsNull && !outb.IsNull && vk.Upload(scene, 0, 0, MemoryMarshal.AsBytes(px.AsSpan())))
        {
            var cl = new CommandList("golden shading rate", 1 << 12, log);
            cl.ReadImages([scene]);
            cl.SetPipeline(pipe);
            cl.BindSet(0, new BindSet(default, default, [new ImageBinding(0, scene)], [new ImageBinding(0, rate)]));
            cl.PushConstants(0, MemoryMarshal.AsBytes((ReadOnlySpan<int>)[BitConverter.SingleToInt32Bits(VrsContrast), VrsEdge]));
            cl.Dispatch(VrsTiles, 1);
            cl.CopyImageToBuffer(new CopyToBufferCmd(rate, new ImageRegion(0, 0, 0, 0), VrsTiles, 1, outb, 0));
            var bytes = new byte[8];
            if (vk.Submit(cl) && vk.ReadBack(outb, bytes)) got = bytes;
        }
        check.Expect(got != null, "the shading-rate golden's pipeline, images or readback were refused (logged above)");
        if (got != null)
            for (int t = 0; t < VrsTiles; t++)
                check.Expect(got[t] == VrsWant[t], $"shading-rate tile E{t + 1}: rate {got[t]}, the approved example says {VrsWant[t]}");
        vk.Destroy(outb);
        vk.Destroy(rate);
        vk.Destroy(scene);
        vk.Destroy(pipe);
        vk.Destroy(cs);
    }
}
