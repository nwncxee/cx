using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>
/// The blend fixture the pipeline goldens share: a full-screen triangle of one grey (0.5 with alpha 0.5) over a
/// target cleared to 0.25 with alpha 1, under a chosen blend; the answer is the red channel of one texel.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The fixture's edge in texels, and the clear and fragment values every expected answer is worked from.</summary>
    private const int BlendSide = 4;
    private const float BlendDst = 0.25f, BlendSrc = 0.5f;

    private const string BlendVs = "#version 460 core\n"
        + "void main() { vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2); gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0); }\n";
    private const string BlendFs = "#version 460 core\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(0.5); }\n";

    /// <summary>The fixture's two stages on <paramref name="vk"/>; the caller destroys both. False (logged) when refused.</summary>
    private static bool BlendStages(VkBackend vk, ExtenderLog log, out ShaderId vs, out ShaderId fs)
    {
        byte[]? v = VulkanToolchain.Compile(BlendVs, "golden blend vs", ShaderKind.VertexShader, out string ve);
        byte[]? f = VulkanToolchain.Compile(BlendFs, "golden blend fs", ShaderKind.FragmentShader, out string fe);
        if (v == null || f == null) log.Always($"GpuGolden: the blend fixture did not compile -- {(v == null ? ve : fe)}");
        vs = v == null ? default : vk.CreateShader(ShaderStage.Vertex, v, "golden blend vs");
        fs = f == null ? default : vk.CreateShader(ShaderStage.Fragment, f, "golden blend fs");
        return !vs.IsNull && !fs.IsNull;
    }

    /// <summary>A pipeline drawing the fixture's stages into <paramref name="target"/> under <paramref name="src"/> and
    /// <paramref name="dst"/>, for colour and alpha alike.</summary>
    private static PipelineId BlendPipeline(VkBackend vk, ShaderId vs, ShaderId fs, string label, Format target, BlendFactor src, BlendFactor dst)
    {
        var d = new PipelineDesc
        {
            Vs = vs, Fs = fs, Topo = Topology.Triangles, Raster = new RasterState(CullMode.None),
            Depth = new DepthState(false, false, CompareOp.Always), Label = $"{label} {src}/{dst}",
        };
        d.Targets.ColorCount = 1; d.Targets.Colors[0] = target; d.Targets.Depth = Format.Unknown; d.Targets.Samples = 1;
        d.Blend[0] = new AttachmentBlend(true, src, dst, src, dst);
        return vk.CreatePipeline(d);
    }

    /// <summary>The fixture drawn through <paramref name="pipe"/> into a fresh <paramref name="target"/> image (RGBA16F or
    /// RGBA8): one texel's red channel, NaN when a step was refused.</summary>
    private static float BlendRed(VkBackend vk, ExtenderLog log, PipelineId pipe, Format target, string label)
    {
        int bpp = target == Format.Rgba16F ? 8 : 4;
        ImageId col = PostImage(vk, target, BlendSide, BlendSide, ImageUse.ColorTarget | ImageUse.CopySrc, label);
        BufferId rb = vk.CreateBuffer(new BufferDesc(BlendSide * BlendSide * bpp, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, label + " read"));
        float red = float.NaN;
        if (!pipe.IsNull && !col.IsNull && !rb.IsNull)
        {
            var cl = new CommandList(label, 1 << 12, log);
            ReadOnlySpan<ColorAttachment> at = [new ColorAttachment(col, 0, 0, LoadOp.Clear, StoreOp.Store, new Vector4(BlendDst, BlendDst, BlendDst, 1))];
            cl.BeginRenderPass(new RenderPassDesc(at, default));
            cl.SetViewport(0, 0, BlendSide, BlendSide);
            cl.SetScissor(0, 0, BlendSide, BlendSide);
            cl.SetPipeline(pipe);
            cl.Draw(3);
            cl.EndRenderPass();
            if (vk.Submit(cl) && PostRead(vk, log, col, BlendSide, BlendSide, bpp, rb) is { } bytes)
                red = bpp == 8 ? (float)BitConverter.ToHalf(bytes, 0) : bytes[0] / 255f;
        }
        vk.Destroy(rb); vk.Destroy(col);
        return red;
    }
}
