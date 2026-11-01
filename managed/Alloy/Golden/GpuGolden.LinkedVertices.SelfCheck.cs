using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>
/// A pipeline linked from libraries fetches its vertices as a whole one does, and stages in use over one layout fetch
/// through a second layout of another stride by that layout: a full-screen triangle with an offset colour attribute.
/// Oracle: the colour written into the buffer, 128/255 red.
/// </summary>
internal static partial class GpuGolden
{
    private const string VertsVs = "#version 460 core\nlayout(location = 0) in vec3 p;\nlayout(location = 1) in vec4 c;\n"
        + "layout(location = 0) out vec4 vc;\nvoid main() { vc = c; gl_Position = vec4(p, 1.0); }\n";
    private const string VertsFs = "#version 460 core\nlayout(location = 0) in vec4 vc;\nlayout(location = 0) out vec4 o;\n"
        + "void main() { o = vc; }\n";

    /// <summary>The fixture's element: a position and an RGBA8 colour, 16 bytes.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct GoldVert { public Vector3 P; public uint C; }

    private static void LinkedVerticesSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        byte[]? v = VulkanToolchain.Compile(VertsVs, "golden verts vs", ShaderKind.VertexShader, out string ve);
        byte[]? f = VulkanToolchain.Compile(VertsFs, "golden verts fs", ShaderKind.FragmentShader, out string fe);
        if (v == null || f == null) { check.Expect(false, $"the vertex fixture did not compile -- {(v == null ? ve : fe)}"); return; }
        ShaderId vs = vk.CreateShader(ShaderStage.Vertex, v, "golden verts vs"), fs = vk.CreateShader(ShaderStage.Fragment, f, "golden verts fs");
        VertexLayoutId layout = vk.CreateVertexLayout(new VertexLayoutDesc(
            [new(0, 3, VertexType.Float, 0), new(1, 4, VertexType.U8Norm, 12), new(7, 2, VertexType.Float, 4)], 16, "golden verts"));
        BufferId vb = vk.CreateBuffer(new BufferDesc(48, BufferUse.Vertex | BufferUse.CopyDst, MemoryKind.Device, "golden verts"));
        GoldVert[] tri = [new() { P = new(-1, -1, 0), C = 0xFF000080 }, new() { P = new(3, -1, 0), C = 0xFF000080 }, new() { P = new(-1, 3, 0), C = 0xFF000080 }];
        check.Expect(vk.Upload(vb, 0, MemoryMarshal.AsBytes(tri.AsSpan())), "the vertex fixture's buffer was refused");
        bool libraries = vk.Libraries;
        vk.Libraries = false;
        float whole = VertsRed(vk, log, vs, fs, layout, vb, "golden verts whole");
        vk.Libraries = libraries;
        check.Expect(MathF.Abs(whole - 128 / 255f) < 1.5f / 255, $"a whole pipeline fetched red {whole}, written 128/255");
        if (libraries)
        {
            float linked = VertsRed(vk, log, vs, fs, layout, vb, "golden verts linked");
            check.Expect(MathF.Abs(linked - 128 / 255f) < 1.5f / 255, $"a linked pipeline fetched red {linked}, written 128/255");
        }
        VertexLayoutId wide = vk.CreateVertexLayout(new VertexLayoutDesc(
            [new(1, 4, VertexType.U8Norm, 0), new(0, 3, VertexType.Float, 8)], 20, "golden verts wide"));
        BufferId wb = vk.CreateBuffer(new BufferDesc(60, BufferUse.Vertex | BufferUse.CopyDst, MemoryKind.Device, "golden verts wide"));
        var w = new byte[60];
        for (int i = 0; i < 3; i++)
        {
            BitConverter.TryWriteBytes(w.AsSpan(i * 20), 0xFF000080u);
            BitConverter.TryWriteBytes(w.AsSpan(i * 20 + 8), tri[i].P.X);
            BitConverter.TryWriteBytes(w.AsSpan(i * 20 + 12), tri[i].P.Y);
            BitConverter.TryWriteBytes(w.AsSpan(i * 20 + 16), 0f);
        }
        check.Expect(vk.Upload(wb, 0, w), "the wide vertex buffer was refused");
        PipelineId first = VertsPipeline(vk, vs, fs, layout, "golden verts first");
        float second = VertsRed(vk, log, vs, fs, wide, wb, "golden verts second layout");
        check.Expect(MathF.Abs(second - 128 / 255f) < 1.5f / 255, $"stages in use over one layout fetched red {second} through another, written 128/255");
        vk.Destroy(first); vk.Destroy(wb);
        vk.Destroy(vb); vk.Destroy(vs); vk.Destroy(fs);
    }

    /// <summary>The vertex fixture's pipeline over <paramref name="layout"/>, into one RGBA8 target.</summary>
    private static PipelineId VertsPipeline(VkBackend vk, ShaderId vs, ShaderId fs, VertexLayoutId layout, string label)
    {
        var d = new PipelineDesc
        {
            Vs = vs, Fs = fs, Layout = layout, Topo = Topology.Triangles, Raster = new RasterState(CullMode.None),
            Depth = new DepthState(false, false, CompareOp.Always), Label = label,
        };
        d.Targets.ColorCount = 1; d.Targets.Colors[0] = Format.Rgba8; d.Targets.Depth = Format.Unknown; d.Targets.Samples = 1;
        return vk.CreatePipeline(d);
    }

    /// <summary>The vertex fixture drawn through a fresh pipeline into a fresh RGBA8 image: one texel's red.</summary>
    private static float VertsRed(VkBackend vk, ExtenderLog log, ShaderId vs, ShaderId fs, VertexLayoutId layout, BufferId vb, string label)
    {
        PipelineId pipe = VertsPipeline(vk, vs, fs, layout, label);
        ImageId col = PostImage(vk, Format.Rgba8, BlendSide, BlendSide, ImageUse.ColorTarget | ImageUse.CopySrc, label);
        BufferId rb = vk.CreateBuffer(new BufferDesc(BlendSide * BlendSide * 4, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, label + " read"));
        float red = float.NaN;
        if (!pipe.IsNull && !col.IsNull && !rb.IsNull)
        {
            var cl = new CommandList(label, 1 << 12, log);
            ReadOnlySpan<ColorAttachment> at = [new ColorAttachment(col, 0, 0, LoadOp.Clear, StoreOp.Store, Vector4.Zero)];
            cl.BeginRenderPass(new RenderPassDesc(at));
            cl.SetViewport(0, 0, BlendSide, BlendSide);
            cl.SetScissor(0, 0, BlendSide, BlendSide);
            cl.SetPipeline(pipe);
            cl.BindVertexBuffer(vb);
            cl.Draw(3);
            cl.EndRenderPass();
            if (vk.Submit(cl) && PostRead(vk, log, col, BlendSide, BlendSide, 4, rb) is { } bytes) red = bytes[0] / 255f;
        }
        vk.Destroy(rb); vk.Destroy(col); vk.Destroy(pipe);
        return red;
    }
}
