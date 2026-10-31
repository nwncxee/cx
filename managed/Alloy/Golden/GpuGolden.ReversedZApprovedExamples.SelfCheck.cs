using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>
/// The world depth goldens (approved-spec, E2 to E5): flat quads at hand-picked distances drawn through the world's
/// translation, matrix and depth state on the device, which quad won each pixel and its depth read back. Oracles: the
/// distances, pixel columns and GL window depths written by hand; the decoded distances held to the drawn ones.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>The fixture's square target edge in pixels.</summary>
    private const int ZSide = 64;

    /// <summary>The engine camera's own defaults, near 0.1 and a 45 degree view, with a far plane past the 300 m cases.</summary>
    private const float ZNear = 0.1f, ZFar = 500f, ZFovy = 45f;

    /// <summary>One flat quad facing the camera: NDC x from <see cref="X0"/> to <see cref="X1"/>, y from
    /// <see cref="Y0"/> to 1, at <see cref="Dist"/> metres; its id is its 1-based place in the draw.</summary>
    private readonly record struct ZQuad(double X0, double X1, double Dist, double Y0 = -1);

    private static void ReversedZSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        Matrix4x4 proj = WorldDepth.Fold(AlloyCamera.Perspective(ZFovy, 1f, ZNear, ZFar));
        ZFightCase(vk, check, log, proj, 300, "E2");
        ZFightCase(vk, check, log, proj, 5, "E2 boundary");
        ZDistanceCase(vk, check, log, proj);
        ZHakCase(vk, check, log, proj);
        ZGlDepthCase(vk, check, log);
    }

    /// <summary>Pairs E2 draws, each a step further than the last, so no single lucky rounding passes it.</summary>
    private const int ZPairs = 16;

    /// <summary>E2: two full-screen quads 1 cm apart, the nearer drawn first, at <paramref name="dist"/> m and at
    /// <see cref="ZPairs"/> - 1 more places up to 0.6 m past it; every pixel of every pair must keep the nearer.</summary>
    private static void ZFightCase(VkBackend vk, Checker check, ExtenderLog log, Matrix4x4 proj, double dist, string tag)
    {
        int far = 0;
        for (int k = 0; k < ZPairs; k++)
        {
            double at = dist + k * 0.0371;
            float[]? px = ZDraw(vk, log, [new ZQuad(-1, 1, at), new ZQuad(-1, 1, at + 0.01)], proj, $"golden {tag}");
            check.Expect(px != null, $"{tag}: the depth golden's draw was refused (logged above)");
            if (px == null) return;
            for (int i = 0; i < ZSide * ZSide; i++) if (px[i * 2] != 1f) far++;
        }
        check.Expect(far == 0, $"{tag}: {far} of {ZPairs * ZSide * ZSide} pixels show the farther of two surfaces 1 cm apart at {dist} m");
    }

    /// <summary>E3: quads at 2, 30 and 300 m in the lower half, sky above; the shared decode reads each back within
    /// 0.1 percent, and the sky at the far plane or past it.</summary>
    private static void ZDistanceCase(VkBackend vk, Checker check, ExtenderLog log, Matrix4x4 proj)
    {
        double[] want = [2, 30, 300];
        float[]? px = ZDraw(vk, log, [new ZQuad(-1, -1 / 3.0, want[0], 0), new ZQuad(-1 / 3.0, 1 / 3.0, want[1], 0),
                                      new ZQuad(1 / 3.0, 1, want[2], 0)], proj, "golden E3");
        string fn = Function(ShaderSource.Get("cx_scenedepth.glsl"), "float CxDepthViewZ(");
        check.Expect(fn.Length > 0, "E3: cx_scenedepth no longer declares float CxDepthViewZ(float d, mat4 proj)");
        check.Expect(px != null, "E3: the depth golden's draw was refused (logged above)");
        if (px == null || fn.Length == 0) return;
        int row = ZSide / 4;
        float[] d = [px[(row * ZSide + ZSide / 6) * 2 + 1], px[(row * ZSide + ZSide / 2) * 2 + 1],
                     px[(row * ZSide + ZSide * 5 / 6) * 2 + 1], WorldDepth.Clear];
        PipelineId pipe = vk.CreateCompute(Version + "layout(local_size_x = 1) in;\n" + CaseBlocks(4, 4) + $"const mat4 cxM = {Mat(proj)};\n"
                                           + fn + "void main() { uint i = gl_WorkGroupID.x; uOut[i] = CxDepthViewZ(uIn[i], cxM); }\n",
                                           "golden E3 decode");
        float[]? got = pipe.IsNull ? null : RunCases(vk, log, pipe, 4, d, 4, "golden E3 decode");
        vk.Destroy(pipe);
        check.Expect(got != null, "E3: the decode stage was refused (logged above)");
        if (got == null) return;
        for (int k = 0; k < 3; k++)
            check.Expect(Math.Abs(got[k] - want[k]) <= want[k] * 0.001, $"E3: a surface {want[k]} m away reads back as {got[k]} m");
        check.Expect(got[3] >= ZFar * 0.999f, $"E3 boundary: the empty sky reads back as {got[3]} m, nearer than the far plane");
        log.Info($"GpuGolden: E3 depth decode {got[0]}/{got[1]}/{got[2]} m, sky {got[3]} m");
    }

    /// <summary>E4: a world wall at 30 m, then a hak's quad over the right half at 29.5 m through the m_proj the frame
    /// hands content; the hak quad covers columns 32 to 63 exactly and stands in front.</summary>
    private static void ZHakCase(VkBackend vk, Checker check, ExtenderLog log, Matrix4x4 proj)
    {
        float[]? px = ZDraw(vk, log, [new ZQuad(-1, 1, 30), new ZQuad(0, 1, 29.5)], proj, "golden E4");
        check.Expect(px != null, "E4: the depth golden's draw was refused (logged above)");
        if (px == null) return;
        int wrong = 0;
        for (int y = 0; y < ZSide; y++)
            for (int x = 0; x < ZSide; x++)
                if (px[(y * ZSide + x) * 2] != (x < ZSide / 2 ? 1f : 2f)) wrong++;
        check.Expect(wrong == 0, $"E4: {wrong} pixels are not the hak quad right of column 32 and the wall left of it");
    }

    /// <summary>E5: the GUI's and the casters' stages keep GL's window depth: clip z 0.5 and -0.5 at w 1 land at 0.75
    /// and 0.25, as the device's own GL depth mapping put them.</summary>
    private static void ZGlDepthCase(VkBackend vk, Checker check, ExtenderLog log)
    {
        const string vs = Version + "flat out float vId;\nvoid main()\n{\n    int q = gl_VertexID / 6, k = gl_VertexID % 6;\n"
                        + "    vec2 c = vec2(k == 1 || k == 2 || k == 4 ? 1.0 : 0.0, k == 2 || k == 4 || k == 5 ? 1.0 : -1.0);\n"
                        + "    vId = float(q + 1);\n    gl_Position = vec4(c.x - float(1 - q), c.y, q == 0 ? 0.5 : -0.5, 1.0);\n}\n";
        GuiVkProg? gui = GuiVkProg.Build(vk, vs, ZFs, new Dictionary<string, uint>(), "golden E5 gui", _ => { }, log);
        CasterPass.VkCastProg? cast = CasterPass.VkCastBuild(vk, vs, ZFs, "golden E5 caster", log);
        check.Expect(gui != null && cast != null, "E5: the GUI or caster stage did not build (logged above)");
        foreach ((string who, ShaderId v, ShaderId f) in (ReadOnlySpan<(string, ShaderId, ShaderId)>)
                 [("GUI", gui?.Vs ?? default, gui?.Fs ?? default), ("caster", cast?.Vs ?? default, cast?.Fs ?? default)])
        {
            if (v.IsNull) continue;
            float[]? px = ZRun(vk, log, v, f, 2, $"golden E5 {who}");
            check.Expect(px != null, $"E5: the {who} draw was refused (logged above)");
            if (px == null) continue;
            int row = ZSide / 2;
            float left = px[(row * ZSide + ZSide / 4) * 2 + 1], right = px[(row * ZSide + ZSide * 3 / 4) * 2 + 1];
            check.Expect(left == 0.75f && right == 0.25f, $"E5: the {who} stage's clip z 0.5 / -0.5 landed at {left} / {right}, not 0.75 / 0.25");
        }
        if (gui != null) { vk.Destroy(gui.Vs); vk.Destroy(gui.Fs); }
        if (cast != null) { vk.Destroy(cast.Vs); vk.Destroy(cast.Fs); }
    }

    /// <summary>The fixture's fragment stage: the quad's id and its window depth.</summary>
    private const string ZFs = Version + "flat in float vId;\nout vec2 cxOut;\nvoid main() { cxOut = vec2(vId, gl_FragCoord.z); }\n";

    /// <summary><paramref name="quads"/> in view space through <paramref name="proj"/>, as a world program draws them:
    /// translated as the world's are, under the world's depth clear and test. Every pixel's id and depth, or null.</summary>
    private static float[]? ZDraw(VkBackend vk, ExtenderLog log, ZQuad[] quads, in Matrix4x4 proj, string label)
    {
        var at = new List<string>();
        foreach (ZQuad z in quads)
            foreach ((double x, double y) in (ReadOnlySpan<(double, double)>)[(z.X0, z.Y0), (z.X1, z.Y0), (z.X1, 1), (z.X0, z.Y0), (z.X1, 1), (z.X0, 1)])
                at.Add($"vec3({G(x * z.Dist / proj.M11)}, {G(y * z.Dist / proj.M22)}, {G(-z.Dist)})");
        string vs = Version + $"const vec3 cxPos[{at.Count}] = vec3[]({string.Join(", ", at)});\nconst mat4 cxM = {Mat(proj)};\n"
                  + "flat out float vId;\nvoid main() { vId = float(gl_VertexID / 6 + 1); gl_Position = cxM * vec4(cxPos[gl_VertexID], 1.0); }\n";
        VkProgramText t = WorldDepth.Translate(vs, ZFs, new Dictionary<string, uint>());
        if (t.Error != null) { log.Always($"GpuGolden: {label} did not translate -- {t.Error}"); return null; }
        byte[]? vb = VulkanToolchain.Compile(VkBackend.Translate(t.Vs), $"{label} vs", ShaderKind.VertexShader, out string ve);
        byte[]? fb = VulkanToolchain.Compile(VkBackend.Translate(t.Fs), $"{label} fs", ShaderKind.FragmentShader, out string fe);
        if (vb == null || fb == null) { log.Always($"GpuGolden: {label} did not compile -- {(vb == null ? ve : fe)}"); return null; }
        ShaderId v = vk.CreateShader(ShaderStage.Vertex, vb, $"{label} vs"), f = vk.CreateShader(ShaderStage.Fragment, fb, $"{label} fs");
        float[]? px = v.IsNull || f.IsNull ? null : ZRun(vk, log, v, f, quads.Length, label, true);
        vk.Destroy(v); vk.Destroy(f);
        return px;
    }

    /// <summary><paramref name="quads"/> quads of stages <paramref name="v"/> and <paramref name="f"/> into an RG32F
    /// target and a D32F depth, under the world's clear and test when <paramref name="world"/>, else always written.</summary>
    private static float[]? ZRun(VkBackend vk, ExtenderLog log, ShaderId v, ShaderId f, int quads, string label, bool world = false)
    {
        var d = new PipelineDesc
        {
            Vs = v, Fs = f, Topo = Topology.Triangles, Raster = new RasterState(CullMode.None),
            Depth = world ? new DepthState(true, true, WorldDepth.NearerOrEqual) : new DepthState(true, true, CompareOp.Always),
            Label = label,
        };
        d.Targets.ColorCount = 1; d.Targets.Colors[0] = Format.Rg32F; d.Targets.Depth = Format.D32F; d.Targets.Samples = 1;
        PipelineId pipe = vk.CreatePipeline(d);
        ImageId col = PostImage(vk, Format.Rg32F, ZSide, ZSide, ImageUse.ColorTarget | ImageUse.CopySrc, label);
        ImageId dep = PostImage(vk, Format.D32F, ZSide, ZSide, ImageUse.DepthTarget, label + " depth");
        BufferId rb = vk.CreateBuffer(new BufferDesc(ZSide * ZSide * 8, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, label + " read"));
        float[]? px = null;
        if (!pipe.IsNull && !col.IsNull && !dep.IsNull && !rb.IsNull)
        {
            var cl = new CommandList(label, 1 << 12, log);
            ReadOnlySpan<ColorAttachment> at = [new ColorAttachment(col, 0, 0, LoadOp.Clear, StoreOp.Store, Vector4.Zero)];
            cl.BeginRenderPass(new RenderPassDesc(at, new DepthAttachment(dep, 0, 0, LoadOp.Clear, StoreOp.DontCare,
                                                                          world ? WorldDepth.Clear : 1f)));
            cl.SetViewport(0, 0, ZSide, ZSide);
            cl.SetScissor(0, 0, ZSide, ZSide);
            cl.SetPipeline(pipe);
            cl.Draw(quads * 6);
            cl.EndRenderPass();
            if (vk.Submit(cl) && PostRead(vk, log, col, ZSide, ZSide, 8, rb) is { } bytes)
                px = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        }
        vk.Destroy(rb); vk.Destroy(dep); vk.Destroy(col); vk.Destroy(pipe);
        return px;
    }

    /// <summary><paramref name="m"/> as a GLSL constructor: the row-vector bytes read column-major, as every upload does.</summary>
    private static string Mat(in Matrix4x4 m)
        => $"mat4({G(m.M11)}, {G(m.M12)}, {G(m.M13)}, {G(m.M14)}, {G(m.M21)}, {G(m.M22)}, {G(m.M23)}, {G(m.M24)}, "
         + $"{G(m.M31)}, {G(m.M32)}, {G(m.M33)}, {G(m.M34)}, {G(m.M41)}, {G(m.M42)}, {G(m.M43)}, {G(m.M44)})";

    /// <summary>A GLSL float literal that round-trips.</summary>
    private static string G(double x) => ((float)x).ToString("0.0########E+0", CultureInfo.InvariantCulture);
}
