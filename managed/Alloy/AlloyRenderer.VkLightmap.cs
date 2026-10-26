using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;
using Vortice.ShaderCompiler;

namespace CX.Alloy;

/// <summary>
/// The lightmap bake's GPU half on Vulkan: the bake stage drawing charted rects into the scratch planes (coverage, then
/// one light step a pass against its slot of the scratch cubes), the encoder folding a band of them into page blocks,
/// and the blocks' copies into the resident pages. <see cref="LightmapBake"/> owns the flow and every image.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The bake and encoder programs on one device, with the bake's two pipelines.</summary>
    private sealed class VkLmState
    {
        public VkBackend? Dev;
        public bool Tried;
        public VkProg? Bake;
        public ShaderId Gs;
        /// <summary>The coverage and light-step pipelines per arena format, made at a format's first draw.</summary>
        public readonly PipelineId[] Cover = new PipelineId[MeshArena.Formats], Light = new PipelineId[MeshArena.Formats];
        public VkDefStage? Enc;
        public int BLight = -1, BColor = -1, BMode = -1, BCube = -1, BCubes = -1, ERegion = -1, ELayer = -1;
        public CommandList? Side;
    }

    private static VkLmState _vkLm = new();

    /// <summary>One rect of a bake pass: its record slot, its origin and scale on the page, the shadow disc's world
    /// floor, the mover channel this light writes, and its chart range in the arena format <see cref="Fmt"/>.</summary>
    internal readonly record struct VkLmDraw(int Slot, int X, int Y, float S, float SoftWorld, int Chan, int Fmt,
                                             int BaseVertex, int FirstIndex, int Count);

    /// <summary>The bake's shim name; the encoder reads its terms under the base name, as the deferred stages do.</summary>
    private const uint VkLmShim = VkUniformShim.ProgramBase + 62;   // not-an-offset: the bake program's shim name

    /// <summary>Both programs and the bake's pipelines, once per device; a refusal is logged and stays.</summary>
    internal static bool VkLmReady(VkBackend vk, ExtenderLog log)
    {
        VkLmState s = _vkLm;
        if (s.Dev != vk) _vkLm = s = new VkLmState { Dev = vk };
        if (s.Tried) return s.Bake != null;
        s.Tried = true;
        s.Side = new CommandList("vulkan lightmap bake", 1 << 16, log);
        VkProg? p = VkBuild(vk, VkLmText(log, out string gs), "lightmap bake", false, VkLmShim, log);
        byte[]? g = VulkanToolchain.Compile(VkBackend.Translate(gs), "vk lightmap bake gs", ShaderKind.GeometryShader, out string ge);
        if (g == null) log.Always($"AlloyRenderer: the Vulkan lightmap bake's geometry stage did not compile -- {ge}");
        ShaderId gid = g == null ? default : vk.CreateShader(ShaderStage.Geometry, g, "vk lightmap bake gs");
        s.Enc = VkDefBuild(vk, "cx_bakeenc_c", "CX_BAKEENC_LAYER 1", "vk lightmap encoder", log);
        if (p != null)
            VkUniformShim.Resolve(p.Names, () =>
            {
                s.BLight = GlProgram.Loc(VkLmShim, "uBakeLight"); s.BColor = GlProgram.Loc(VkLmShim, "uBakeColor");
                s.BMode = GlProgram.Loc(VkLmShim, "uBakeMode"); s.BCube = GlProgram.Loc(VkLmShim, "uBakeCube");
                s.BCubes = GlProgram.Loc(VkLmShim, "uLocalCubes");
            });
        if (s.Enc != null)
            VkUniformShim.Resolve(s.Enc.Names, () =>
            {
                s.ERegion = GlProgram.Loc(VkUniformShim.ProgramBase, "uRegion");
                s.ELayer = GlProgram.Loc(VkUniformShim.ProgramBase, "uPageLayer");
            });
        s.Gs = gid;
        int st = (int)MeshArena.Fmt.Static;
        if (p != null && !gid.IsNull)
        {
            s.Cover[st] = VkLmPipeline(vk, p, gid, st, false);
            s.Light[st] = VkLmPipeline(vk, p, gid, st, true);
        }
        s.Bake = p != null && !gid.IsNull && s.Enc != null && !s.Cover[st].IsNull && !s.Light[st].IsNull ? p : null;
        if (s.Bake == null)
            log.Always("AlloyRenderer: the Vulkan lightmap bake or encoder was REFUSED (logged above) -- nothing bakes on Vulkan "
                     + "this session; surfaces with a cached bake still read it");
        return s.Bake != null;
    }

    /// <summary>The bake's three stages as GL compiles them, its per-rect terms moved onto the push block (a light step
    /// is hundreds of draws, past what a loose block a draw would leave of the arena): slot, rect origin and scale,
    /// disc floor, channel and depth step. Returns the program's pair translated; <paramref name="gs"/> the geometry stage.</summary>
    private static VkProgramText VkLmText(ExtenderLog log, out string gs)
    {
        const string what = "LightmapBake vk bake";
        string v = VkLmFinal(LightmapBake.BakeVs, log, what), g = VkLmFinal(LightmapBake.BakeGs, log, what);
        string f = VkLmFinal(LightmapBake.BakeFs, log, what);
        v = v.Replace("uniform int uSlot;", "uniform int uDrawBase;\n#define uSlot uDrawBase\n", StringComparison.Ordinal)
             .Replace("uniform vec4 uRect;", "uniform int uDecal;\nuniform int uSolid;\nuniform float uAlphaCut;\n"
                                           + "#define uRect vec4(float(uDecal), float(uSolid), uAlphaCut, 0.0)\n", StringComparison.Ordinal);
        f = f.Replace("uniform int uBakeChan;", "uniform int uOitGob;\n#define uBakeChan uOitGob\n", StringComparison.Ordinal)
             .Replace("uniform float uLocalSoftWorld;", "uniform float uAoDirect;\n#define uLocalSoftWorld uAoDirect\n", StringComparison.Ordinal);
        var push = new System.Text.StringBuilder("layout(push_constant) uniform CxBatch {");
        foreach (var (t, n) in VkGlsl.Push) push.Append(' ').Append(t).Append(' ').Append(n).Append(';');
        push.Append(" };\n#define uBakeDepth (float(uSsaoOn) / ").Append(LightmapBake.VkDepthSteps).Append(".0 - 1.0)\n");
        gs = ShaderStages.AfterVersion(g.Replace("uniform float uBakeDepth;", "", StringComparison.Ordinal), push.ToString());
        return VkGlsl.Translate(v, f, VkPrograms.Units, glDepth: false);
    }

    private static string VkLmFinal(string src, ExtenderLog log, string what)
        => ShaderResource.Tokens(VanillaShader.Splice(GlProgram.Lib(src, log, what), log, what, true, true));

    /// <summary>A bake pipeline over arena format <paramref name="fmt"/>: three RGBA16F planes added into, the D16
    /// scratch depth tested GREATER and written for a light step and untouched for coverage, no cull.</summary>
    private static PipelineId VkLmPipeline(VkBackend vk, VkProg p, ShaderId gs, int fmt, bool light)
    {
        var d = new PipelineDesc
        {
            Vs = p.Vs, Gs = gs, Fs = p.Fs, Layout = _vkc.Layouts[fmt], Topo = Topology.Triangles, Raster = new RasterState(CullMode.None),
            Depth = light ? new DepthState(true, true, CompareOp.Greater) : new DepthState(false, false, CompareOp.Always),
            Label = $"vk lightmap bake {(MeshArena.Fmt)fmt} {(light ? "light" : "coverage")}",
        };
        d.Targets.ColorCount = 3;
        d.Targets.Colors[0] = d.Targets.Colors[1] = d.Targets.Colors[2] = Format.Rgba16F;
        d.Targets.Depth = Format.D16;
        d.Targets.Samples = 1;
        for (int i = 0; i < 3; i++) d.Blend[i] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One);
        return vk.CreatePipeline(in d);
    }

    /// <summary>One bake pass over <paramref name="draws"/> into the scratch planes: coverage (<paramref name="step"/> 0,
    /// clearing the planes first) or light step <paramref name="step"/> of the page, its shadow in slot
    /// <paramref name="cube"/> of <paramref name="cubes"/>. False only when the uniform arena is full.</summary>
    internal static bool VkLmBake(CommandList cl, ImageId a, ImageId b, ImageId c, ImageId z, int step, Vector4 light,
                                  Vector3 colour, int cube, ImageId cubes, int cubeSize, ReadOnlySpan<VkLmDraw> draws)
    {
        VkLmState s = _vkLm;
        VkColourState vc = _vkc;
        VkProg p = s.Bake!;
        bool cover = step == 0;
        Array.Clear(p.Block);
        VkUniformShim.Capture(p.Members, p.Block);
        VkUniformShim.Set(s.BMode, cover ? 0 : 1);
        VkUniformShim.Set(s.BLight, [light.X, light.Y, light.Z, light.W]);
        VkUniformShim.Set(s.BColor, [colour.X, colour.Y, colour.Z]);
        VkUniformShim.Set(s.BCube, cube);
        VkUniformShim.Set(s.BCubes, cover ? 0 : cube + 1);
        VkUniformShim.EndCapture();
        int arena0 = vc.ArenaUsed;
        int loose = VkArena(p.Block);
        byte* pb = stackalloc byte[PassBytes];
        new Span<byte>(pb, PassBytes).Clear();
        int* vp = stackalloc int[4] { 0, 0, LightmapBake.PageSize, LightmapBake.PageSize };
        AlloyLights.FillDraw(pb, vp, 1f, 100f);
        var blk = (PassBlock*)pb;
        if (!cover)
        {
            // the scratch cube as this step's slot, filtered as GL's bake filters its one cube (LightmapBake.BakeLight)
            blk->Light[cube] = light;
            blk->LocalPlane = new Vector2(MathF.Max(AlloyLights.ShadowNearWorld, CasterPass.CubeNearPlane), 1f / cubeSize);
            blk->LocalSoft = new Vector4(AlloyLights.ShadowSoft, AlloyLights.ShadowNearWorld, 0f, 0f);
            blk->LocalFloor = AlloyLights.ShadowFloor;
            blk->LocalPcss = new Vector4(0f, 0f, Shadows.Taps.V, 0f);
            blk->Fade[cube].Value = 1f;
        }
        int pass = VkArena(new ReadOnlySpan<byte>(pb, PassBytes));
        if (loose < 0 || pass < 0) return false;
        CommandList side = s.Side!;
        side.Reset();
        side.SetViewport(0, 0, LightmapBake.PageSize, LightmapBake.PageSize);
        if (!MeshArena.VkEbo.IsNull) side.BindIndexBuffer(MeshArena.VkEbo, IndexType.U16, 0);
        PipelineId[] pipes = cover ? s.Cover : s.Light;
        side.SetPipeline(pipes[(int)MeshArena.Fmt.Static]);
        ReadOnlySpan<ImageBinding> own = cover ? default
            : [new(CubeUnit, cubes, vc.Shadow), new(TextureSlots.LocalShadowRaw, cubes, vc.Raw)];
        VkBindSet(side, p, vc, loose, pass, vc.Slots, 0, default, default, own);
        var push = new VkBatchPush { SsaoOn = cover ? 0 : step };
        int fmt = -1;
        foreach (VkLmDraw d in draws)
        {
            if (d.Fmt != fmt)
            {
                if ((uint)d.Fmt >= MeshArena.Formats || MeshArena.VkVbo((MeshArena.Fmt)d.Fmt).IsNull) continue;
                if (pipes[d.Fmt].IsNull) pipes[d.Fmt] = VkLmPipeline(s.Dev!, p, s.Gs, d.Fmt, !cover);
                if (pipes[d.Fmt].IsNull) continue;
                fmt = d.Fmt;
                side.SetPipeline(pipes[fmt]);
                side.BindVertexBuffer(MeshArena.VkVbo((MeshArena.Fmt)fmt));
            }
            push.DrawBase = d.Slot; push.Decal = d.X; push.Solid = d.Y; push.AlphaCut = d.S;
            push.AoDirect = d.SoftWorld; push.OitGob = d.Chan;
            side.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<VkBatchPush>(in push)));
            side.DrawIndexed(d.Count, 1, d.FirstIndex, d.BaseVertex);
        }
        LoadOp load = cover ? LoadOp.Clear : LoadOp.Load;
        ReadOnlySpan<ColorAttachment> colors =
        [
            new(a, 0, 0, load, StoreOp.Store, default), new(b, 0, 0, load, StoreOp.Store, default), new(c, 0, 0, load, StoreOp.Store, default),
        ];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(z, 0, 0, load, StoreOp.Store, 0f)));
        return true;
    }

    /// <summary>One band of the page's finish: blocks (<paramref name="bx"/>, <paramref name="by"/>) + <paramref name="bw"/> x
    /// <paramref name="bh"/> of the scratch planes encoded into the block images, plane C written into
    /// <paramref name="layer"/> of <paramref name="pageC"/>. False only when the uniform arena is full.</summary>
    internal static bool VkLmEncode(CommandList cl, ImageId a, ImageId b, ImageId c, ImageId blkA, ImageId blkB, ImageId pageC,
                                    int layer, int bx, int by, int bw, int bh)
    {
        VkLmState s = _vkLm;
        VkColourState vc = _vkc;
        VkDefStage st = s.Enc!;
        Array.Clear(st.Block);
        VkUniformShim.Capture(st.Members, st.Block);
        VkUniformShim.Set(s.ERegion, (ReadOnlySpan<int>)[bx, by, bw, bh]);
        VkUniformShim.Set(s.ELayer, layer);
        VkUniformShim.EndCapture();
        int arena0 = vc.ArenaUsed;
        int loose = VkArena(st.Block);
        if (loose < 0) return false;
        cl.UpdateBuffer(vc.Arena, arena0, vc.ArenaBytes.AsSpan(arena0, Math.Min((vc.ArenaUsed - arena0 + 3) & ~3, ArenaCap - arena0)));
        ReadOnlySpan<ImageId> read = [a, b, c];
        cl.ReadImages(read);
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<ImageBinding> tex = stackalloc ImageBinding[4], img = stackalloc ImageBinding[4];
        int nu = 0, nt = 0, ni = 0;
        foreach (VkOpaque o in st.Opaques)
        {
            if (o.Set != 0) continue;
            if (o.Kind == VkBindKind.UniformBlock && nu < ubo.Length)
                ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, vc.Arena, loose, st.Block.Length) : new BufferBinding(o.Binding, vc.ZeroUbo);
            else if (o.Kind == VkBindKind.Sampled && nt < tex.Length)
                tex[nt++] = new ImageBinding(o.Binding, o.Binding == 0 ? a : o.Binding == 1 ? b : c);
            else if (o.Kind == VkBindKind.StorageImage && ni < img.Length)
                img[ni++] = new ImageBinding(o.Binding, o.Binding == 0 ? blkA : o.Binding == 1 ? blkB : pageC);
        }
        cl.SetPipeline(st.Pipe);
        cl.BindSet(0, new BindSet(ubo[..nu], default, tex[..nt], img[..ni]));
        cl.Dispatch((bw + LightmapBake.EncGroup - 1) / LightmapBake.EncGroup, (bh + LightmapBake.EncGroup - 1) / LightmapBake.EncGroup);
        return true;
    }
}
