using System;
using System.Numerics;
using CX.Framework;
using CX.Rhi;
using Vortice.ShaderCompiler;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The screen's edge in pixels: halves split on a row boundary.</summary>
    private const int ScreenSide = 4;

    /// <summary>A full-screen triangle, counter-clockwise in GL's terms, handing its clip-space height to the fragment.</summary>
    private const string ScreenVs = "#version 460 core\nlayout(location = 0) out float vY;\n"
        + "void main() { vec2 p = vec2((gl_VertexIndex << 1) & 2, gl_VertexIndex & 2) * 2.0 - 1.0; vY = p.y; gl_Position = vec4(p, 0.0, 1.0); }\n";

    /// <summary>Red over GL's upper half (clip y above 0), blue under it.</summary>
    private const string ScreenFs = "#version 460 core\nlayout(location = 0) in float vY;\nlayout(location = 0) out vec4 o;\n"
        + "void main() { o = vY > 0.0 ? vec4(1.0, 0.0, 0.0, 1.0) : vec4(0.0, 0.0, 1.0, 1.0); }\n";

    /// <summary>White everywhere.</summary>
    private const string WhiteFs = "#version 460 core\nlayout(location = 0) out vec4 o;\nvoid main() { o = vec4(1.0); }\n";

    /// <summary>Regression: the frame lands upright and in its own colours on a screen whose rows run from the top, a
    /// clip given in GL's rows clips the same rows, and a snapshot reads GL's rows back. Oracles: hand-written colours
    /// per row of the window, top first, and per row of the engine's GL_RGB snapshot, bottom first. The surface gate's
    /// refusals are checked against hand-written usage bits.</summary>
    internal void ScreenCheck(Checker check, ExtenderLog log)
    {
        check.Expect(SurfaceRefusal(VkImageUsageFlags.TransferDst)?.Contains("drawn into", StringComparison.Ordinal) == true,
            "a surface whose images can only be copied into is refused as one that cannot be drawn into");
        check.Expect(SurfaceRefusal(VkImageUsageFlags.ColorAttachment) != null && SurfaceRefusal(VkImageUsageFlags.ColorAttachment | VkImageUsageFlags.TransferDst) == null,
            "a surface is taken once its images can be drawn and blitted into, and not before");
        check.Expect(FlipRows(4, 0, 2) == (2, 2) && FlipRows(4, 3, 1) == (0, 1) && FlipRows(4, 2, 5) == (0, 2),
            "GL's rows from the bottom as rows from the top: the lower half is rows 2-3, the top row is row 0, a clip past the top stops at it");

        byte[]? vs = VulkanToolchain.Compile(ScreenVs, "screen check vs", ShaderKind.VertexShader, out string ve);
        byte[]? fs = VulkanToolchain.Compile(ScreenFs, "screen check fs", ShaderKind.FragmentShader, out string fe);
        byte[]? ws = VulkanToolchain.Compile(WhiteFs, "screen check white", ShaderKind.FragmentShader, out string we);
        check.Expect(vs != null && fs != null && ws != null, $"the screen check's stages compile ({ve}{fe}{we})");
        if (vs == null || fs == null || ws == null || !Resize(ScreenSide, ScreenSide)) return;
        ShaderId v = CreateShader(ShaderStage.Vertex, vs, "screen check vs"), f = CreateShader(ShaderStage.Fragment, fs, "screen check fs");
        ShaderId w = CreateShader(ShaderStage.Fragment, ws, "screen check white");
        PipelineId halves = ScreenPipe(v, f, "screen check halves"), white = ScreenPipe(v, w, "screen check white");
        BufferId rb = CreateBuffer(new BufferDesc(ScreenSide * ScreenSide * 4, BufferUse.CopyDst, MemoryKind.Readback, "screen check read"));
        try
        {
            check.Expect(!halves.IsNull && !white.IsNull && !rb.IsNull, "the screen check's pipelines and readback exist");
            if (halves.IsNull || white.IsNull || rb.IsNull) return;
            ImageId screen = Screen;
            check.Expect(IsScreen(screen), "with no window the frame draws a screen laid out as the window's");

            var cl = new CommandList("screen check", 1 << 10, log);
            DrawScreen(cl, screen, halves, LoadOp.Clear, null);
            byte[]? top = ScreenRows(screen, rb, log);
            check.Expect(top != null && Row(top, 0) == 0xFF0000 && Row(top, 1) == 0xFF0000 && Row(top, 2) == 0x0000FF && Row(top, 3) == 0x0000FF,
                $"E1: the window shows red over its top half and blue under it (rows top first: {Rows(top)})");

            byte[] rgb = new byte[ScreenSide * 3 * ScreenSide];
            string? why;
            fixed (byte* p = rgb) why = NullGl.ReadScreen(this, log, 0, 0, ScreenSide, ScreenSide, p);
            check.Expect(why == null && Rgb(rgb, 0) == 0x0000FF && Rgb(rgb, ScreenSide - 1) == 0xFF0000,
                $"E6: the snapshot's bottom row is blue and its top row red, as GL_RGB rows bottom first ({why ?? $"{Rgb(rgb, 0):X6} .. {Rgb(rgb, ScreenSide - 1):X6}"})");
            byte[] one = new byte[4];
            fixed (byte* p = one) why = NullGl.ReadScreen(this, log, 0, ScreenSide - 1, 1, 1, p);
            check.Expect(why == null && (one[0] << 16 | one[1] << 8 | one[2]) == 0xFF0000,
                $"E6: a snapshot of GL's top row alone reads red ({why ?? $"{one[0]},{one[1]},{one[2]}"})");

            DrawScreen(cl, screen, white, LoadOp.Clear, new Rect(0, 0, ScreenSide, ScreenSide / 2));
            byte[]? clip = ScreenRows(screen, rb, log);
            check.Expect(clip != null && Row(clip, 0) == 0 && Row(clip, 1) == 0 && Row(clip, 2) == 0xFFFFFF && Row(clip, 3) == 0xFFFFFF,
                $"E3: a clip to GL's lower half keeps the window's top half black (rows top first: {Rows(clip)})");
        }
        finally
        {
            Present(1, log);
            Destroy(rb); Destroy(halves); Destroy(white); Destroy(v); Destroy(f); Destroy(w);
        }
    }

    /// <summary>A pipeline drawing <paramref name="vs"/> and <paramref name="fs"/> into the screen, culling GL's back faces.</summary>
    private PipelineId ScreenPipe(ShaderId vs, ShaderId fs, string label)
    {
        var d = new PipelineDesc
        {
            Vs = vs, Fs = fs, Topo = Topology.Triangles, Raster = new RasterState(CullMode.Back),
            Depth = new DepthState(false, false, CompareOp.Always), Label = label,
        };
        d.Targets.ColorCount = 1; d.Targets.Colors[0] = ScreenFormat; d.Targets.Depth = Format.Unknown; d.Targets.Samples = 1;
        return CreatePipeline(d);
    }

    /// <summary>One full-screen triangle through <paramref name="pipe"/> over a black clear, clipped to
    /// <paramref name="clip"/> (GL's rows) when one is given.</summary>
    private void DrawScreen(CommandList cl, ImageId screen, PipelineId pipe, LoadOp load, Rect? clip)
    {
        cl.Reset();
        ReadOnlySpan<ColorAttachment> at = [new ColorAttachment(screen, 0, 0, load, StoreOp.Store, new Vector4(0f, 0f, 0f, 1f))];
        cl.BeginRenderPass(new RenderPassDesc(at));
        cl.SetViewport(0, 0, ScreenSide, ScreenSide);
        if (clip is Rect c) cl.SetScissor(c.X, c.Y, c.W, c.H);
        cl.SetPipeline(pipe);
        cl.SetRasterState(new RasterState(CullMode.Back));
        cl.Draw(3);
        cl.EndRenderPass();
        Submit(cl);
    }

    /// <summary>The screen's texels as the window shows them, row 0 at the top, as RGB; null when the read failed.</summary>
    private byte[]? ScreenRows(ImageId screen, BufferId rb, ExtenderLog log)
    {
        var cl = new CommandList("screen check read", 256, log);
        cl.CopyImageToBuffer(new CopyToBufferCmd(screen, new ImageRegion(0, 0, 0, 0), ScreenSide, ScreenSide, rb, 0));
        var raw = new byte[ScreenSide * ScreenSide * 4];
        if (!Submit(cl) || !WaitSubmitted() || !ReadMapped(rb, 0, raw) || !DescOf(screen, out ImageDesc d)) return null;
        // the window's byte order, by hand: BGRA stores blue first, RGBA red first
        if (d.Fmt == Format.Bgra8)
            for (int i = 0; i < raw.Length; i += 4) (raw[i], raw[i + 2]) = (raw[i + 2], raw[i]);
        return raw;
    }

    /// <summary>Row <paramref name="y"/>'s colour as 0xRRGGBB when every texel in it agrees, else -1.</summary>
    private static int Row(byte[] rgba, int y)
    {
        int first = -1;
        for (int x = 0; x < ScreenSide; x++)
        {
            int at = (y * ScreenSide + x) * 4, c = rgba[at] << 16 | rgba[at + 1] << 8 | rgba[at + 2];
            if (first < 0) first = c;
            else if (c != first) return -1;
        }
        return first;
    }

    private static string Rows(byte[]? rgba)
        => rgba == null ? "not read" : $"{Row(rgba, 0):X6} {Row(rgba, 1):X6} {Row(rgba, 2):X6} {Row(rgba, 3):X6}";

    /// <summary>The first texel of GL_RGB row <paramref name="y"/> (each row padded to 4 bytes) as 0xRRGGBB.</summary>
    private static int Rgb(byte[] rgb, int y)
    {
        int at = y * ((ScreenSide * 3 + 3) & ~3);
        return rgb[at] << 16 | rgb[at + 1] << 8 | rgb[at + 2];
    }
}
