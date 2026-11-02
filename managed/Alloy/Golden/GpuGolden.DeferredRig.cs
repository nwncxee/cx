using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>One small deferred frame for the goldens: the surfaces, the visibility record and the lights written
/// by hand, then the shipped froxel build, walk and film dispatches over them. Every block term is a literal set
/// here, never a knob, so the user's own <c>cx.cfg</c> cannot move the answer.</summary>
internal sealed class DeferredRigScene
{
    /// <summary>Floats per light in <see cref="Lights"/>: position and radius, then colour and the slot word.</summary>
    internal const int LightFloats = 8;

    internal int W, H;
    internal Matrix4x4 View, Proj;
    internal Vector3 Eye;
    internal float Near, Far;
    internal float[] Lights = [];
    internal RigPixel[] Pixels = [];
    /// <summary>One <c>ext1</c> per ownership slot: rgb the sheen colour, w its roughness.</summary>
    internal Vector4[] Sheen = [];
    /// <summary>Shadow-casting cube slots in use, each at its light's position.</summary>
    internal Vector4[] Cubes = [];
    /// <summary>A content author's <c>cx_deferred_cs</c> text standing in for the shipped walk; null = the shipped one.</summary>
    internal string? WalkOverride;
    /// <summary>Set by the run: the walk it drew with was <see cref="WalkOverride"/>'s, not the shipped stage it falls back to.</summary>
    internal bool WalkFromContent;
    /// <summary>The walk built without wave lockstep, as a device whose subgroups offer no ballot builds it.</summary>
    internal bool NoWave;
    /// <summary>Set by the run: the period line's counters -- pixels the walk lit, the entries they walked, the most one walked.</summary>
    internal (uint Pixels, uint Sum, uint Most) Walked;
}

/// <summary>One pixel of a rig scene, in the order <c>CxRigPixel</c> reads it: the <c>CxGbuf</c> fields the twin packs,
/// the record texel's four slots and visibilities, then how far the frame depth sits past the record's (a share).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct RigPixel
{
    public float Flags;
    public Vector3 N;
    public float Rough, Spec, Fresnel, Coat, CoatRough;
    public Vector3 SpecC;
    public float Slot, SpecT;
    public Vector3 DiffT;
    public float LmLum;
    public Vector4 LmMover;
    public float ViewDepth;
    public Vector3 LmShare;
    public Vector4 SurfFade, ShSlots, ShVis;
    public float Skew;
}

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Froxel grid and group sizes the rig builds its table with, at <see cref="DeferredRigScene"/>'s size.</summary>
    private const int RigGridX = 2, RigGridY = 1, RigGridZ = 24;

    /// <summary>The fill stage: each pixel's <c>CxGbuf</c> packed by the library's own <c>CxGbufPack</c>, its frame
    /// depth from the projection's depth row, and at each even pixel the record texel it heads.</summary>
    private static string RigFillText()
        => "#version 460 core\nlayout(local_size_x = 8, local_size_y = 8) in;\n"
         + "struct CxRigPixel { float flags, nx, ny, nz, rough, spec, fresnel, coat, coatRough, specCr, specCg, specCb, slot, specT,"
         + " diffTr, diffTg, diffTb, lmLum, lmMover0, lmMover1, lmMover2, lmMover3, viewDepth, lmShareR, lmShareG, lmShareB,"
         + " fadeX, fadeY, fadeZ, fadeW, sl0, sl1, sl2, sl3, vis0, vis1, vis2, vis3, skew; };\n"
         // uLmPages gates the lightmap samplers, which the fill never reaches: a const 0 folds them out
         + ShaderSource.Get("cx_chartbuf.glsl").Replace("uniform int uLmPages;", "const int uLmPages = 0;", StringComparison.Ordinal)
         + ShaderSource.Get("cx_gbuf.glsl") + ShaderSource.Get("cx_shrec.glsl")
         + $"layout(std430, binding = {SsboSlots.GoldenIn}) readonly buffer CxRigPixels {{ CxRigPixel uPx[]; }};\n" + """
           layout(rgba32ui, binding = 0) uniform writeonly uimage2D uG0;
           layout(rgba32ui, binding = 1) uniform writeonly uimage2D uG1;
           layout(r32ui, binding = 2) uniform writeonly uimage2D uG2;
           layout(r32f, binding = 3) uniform writeonly image2D uDepthOut;
           layout(rgba8, binding = 4) uniform writeonly image2D uVisOut;
           layout(rg32ui, binding = 5) uniform writeonly uimage2D uKeyOut;
           layout(push_constant) uniform CxRigFill { int uW, uH; float uPzz, uPzw; };
           void main()
           {
               ivec2 p = ivec2(gl_GlobalInvocationID.xy);
               if (p.x >= uW || p.y >= uH) return;
               CxRigPixel r = uPx[p.y * uW + p.x];
               CxGbuf g;
               g.flags = uint(r.flags);
               g.N = vec3(r.nx, r.ny, r.nz);
               g.rough = r.rough; g.spec = r.spec; g.fresnel = r.fresnel;
               g.coat = r.coat; g.coatRough = r.coatRough;
               g.specC = vec3(r.specCr, r.specCg, r.specCb);
               g.slot = int(r.slot);
               g.specT = r.specT;
               g.diffT = vec3(r.diffTr, r.diffTg, r.diffTb);
               g.lmLum = r.lmLum;
               g.lmTexel = 0.0;
               g.lmMover = vec4(r.lmMover0, r.lmMover1, r.lmMover2, r.lmMover3);
               g.lmPick = vec4(-1.0);
               g.viewDepth = r.viewDepth;
               g.lmShare = vec3(r.lmShareR, r.lmShareG, r.lmShareB);
               g.surfFade = vec4(r.fadeX, r.fadeY, r.fadeZ, r.fadeW);
               uvec4 w0, w1; uint w2;
               CxGbufPack(g, w0, w1, w2);
               imageStore(uG0, p, w0);
               imageStore(uG1, p, w1);
               imageStore(uG2, p, uvec4(w2));
               float d = g.viewDepth > 0.0 ? uPzw / (g.viewDepth * (1.0 + r.skew)) - uPzz : 0.0;
               imageStore(uDepthOut, p, vec4(d));
               if ((p.x & 1) == 0 && (p.y & 1) == 0)
               {
                   ivec4 sl = ivec4(r.sl0, r.sl1, r.sl2, r.sl3);
                   imageStore(uVisOut, p >> 1, vec4(r.vis0, r.vis1, r.vis2, r.vis3));
                   imageStore(uKeyOut, p >> 1, uvec4(CxShPackSlots(sl), g.viewDepth > 0.0 ? CxShPackKey(g.viewDepth, g.N) : 0u, 0u, 0u));
               }
           }
           """;

    /// <summary>The device resources one rig run holds, destroyed together.</summary>
    private sealed class RigFrame
    {
        public ImageId G0, G1, G2, Depth, Vis, Key, Hdr, Rec;
        public BufferId Lights, Counts, Idx, Movers, Stats, Draws, Chart, Pass, Def, LooseWalk, LooseFilm, LooseBuild, ZeroUbo, Read;
        public ImageId Tex2D, TexU2D, Tex2DArray, Depth2DArray, TexCube, TexCubeArray, DepthCubeArray, Tex3D;
        public SamplerId Linear, Shadow;

        public void Destroy(VkBackend vk)
        {
            foreach (ImageId i in (ReadOnlySpan<ImageId>)[G0, G1, G2, Depth, Vis, Key, Hdr, Rec, Tex2D, TexU2D, Tex2DArray,
                                                          Depth2DArray, TexCube, TexCubeArray, DepthCubeArray, Tex3D]) vk.Destroy(i);
            foreach (BufferId b in (ReadOnlySpan<BufferId>)[Lights, Counts, Idx, Movers, Stats, Draws, Chart, Pass, Def,
                                                            LooseWalk, LooseFilm, LooseBuild, ZeroUbo, Read]) vk.Destroy(b);
        }
    }

    /// <summary>The scene's HDR target after the walk and the film pass, as RGBA halves widened to floats, row by
    /// row from the top; null (logged) when a stage or a resource was refused.</summary>
    internal static float[]? DeferredRig(VkBackend vk, DeferredRigScene sc, ExtenderLog log)
    {
        VkDefStage? walk = VkDefBuild(vk, DeferredLights.ShdCs, null, "rig walk", log, sc.WalkOverride, out sc.WalkFromContent);
        VkDefStage? film = VkDefBuild(vk, DeferredLights.ShdCs, "CX_FILM_PASS 1", "rig film", log, null, out _);
        VkDefStage? build = VkDefBuild(vk, AlloyLights.ShdClusterCs, null, "rig froxel build", log, null, out _);
        PipelineId fill = vk.CreateCompute(RigFillText(), "rig fill");
        var f = new RigFrame();
        float[]? got = null;
        try
        {
            if (walk == null || film == null || build == null || fill.IsNull || !RigMake(vk, sc, f)) return null;
            got = RigRun(vk, sc, f, walk, film, build, fill, log);
        }
        finally
        {
            f.Destroy(vk);
            vk.Destroy(fill);
            foreach (VkDefStage? s in (ReadOnlySpan<VkDefStage?>)[walk, film, build])
                if (s != null) { vk.Destroy(s.Pipe); vk.Destroy(s.Cs); }
        }
        return got;
    }

    /// <summary>Every image and buffer of one run, the light records and draw records uploaded. False when one
    /// was refused.</summary>
    private static bool RigMake(VkBackend vk, DeferredRigScene sc, RigFrame f)
    {
        int w = sc.W, h = sc.H, rw = (w + 1) / 2, rh = (h + 1) / 2;
        const ImageUse both = ImageUse.Storage | ImageUse.Sampled;
        f.G0 = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32Ui, w, h, 1, 1, 1, 1, both, "rig g0"));
        f.G1 = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32Ui, w, h, 1, 1, 1, 1, both, "rig g1"));
        f.G2 = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32Ui, w, h, 1, 1, 1, 1, both, "rig g2"));
        f.Depth = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, w, h, 1, 1, 1, 1, both, "rig depth"));
        f.Vis = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, rw, rh, 1, 1, 1, 1, both, "rig shadow record"));
        f.Key = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg32Ui, rw, rh, 1, 1, 1, 1, both, "rig shadow key"));
        f.Hdr = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1, both | ImageUse.CopySrc, "rig hdr"));
        f.Rec = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32Ui, w, h, 1, 1, 1, 1, both, "rig light record"));
        int clusters = RigGridX * RigGridY * RigGridZ;
        f.Lights = RigBuffer(vk, MemoryMarshal.AsBytes(sc.Lights.AsSpan()), BufferUse.Storage, "rig lights");
        f.Counts = vk.CreateBuffer(new BufferDesc(clusters * AlloyLights.ClusterHeadWords * sizeof(uint), BufferUse.Storage, MemoryKind.Device, "rig froxel heads"));
        f.Idx = vk.CreateBuffer(new BufferDesc((long)clusters * AlloyLights.ClusterCap * sizeof(uint), BufferUse.Storage, MemoryKind.Device, "rig froxel lists"));
        f.Movers = vk.CreateBuffer(new BufferDesc(CasterPass.MaxCubes * CasterPass.DynSpheres * 16, BufferUse.Storage, MemoryKind.Device, "rig movers"));
        f.Stats = vk.CreateBuffer(new BufferDesc(VkDefStatsWords * sizeof(uint), BufferUse.Storage, MemoryKind.Device, "rig stats"));
        int slots = Math.Max(sc.Sheen.Length, 1);
        var draws = new DrawRecordBlock[slots];
        for (int i = 0; i < sc.Sheen.Length; i++) draws[i].Ext.Ext1 = sc.Sheen[i];
        f.Draws = RigBuffer(vk, MemoryMarshal.AsBytes(draws.AsSpan()), BufferUse.Storage, "rig draws");
        f.Chart = vk.CreateBuffer(new BufferDesc(slots * LightmapBake.RecFloats * sizeof(float) * 2, BufferUse.Storage, MemoryKind.Device, "rig chart"));
        f.Pass = vk.CreateBuffer(new BufferDesc(PassBytes, BufferUse.Uniform, MemoryKind.Upload, "rig pass block"));
        f.Def = vk.CreateBuffer(new BufferDesc(Std140.BlockBytes<DeferredBlock>(), BufferUse.Uniform, MemoryKind.Upload, "rig deferred block"));
        f.ZeroUbo = vk.CreateBuffer(new BufferDesc(ZeroUboBytes, BufferUse.Uniform, MemoryKind.Device, "rig zero block"));
        f.Read = vk.CreateBuffer(new BufferDesc((long)w * h * 8, BufferUse.CopyDst | BufferUse.CopySrc, MemoryKind.Device, "rig hdr read"));
        f.Linear = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
        f.Shadow = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp, GlDepth.NearerOrEqual));
        f.Tex2D = VkStandIn(vk, ImageKind.Tex2D, Format.Rgba8, 1, "rig 2D");
        f.TexU2D = VkStandIn(vk, ImageKind.Tex2D, Format.Rgba32Ui, 1, "rig uint 2D");
        f.Tex2DArray = VkStandIn(vk, ImageKind.Tex2DArray, Format.Rgba8, 1, "rig 2D array");
        f.Tex3D = VkStandIn(vk, ImageKind.Tex3D, Format.Rgba8, 1, "rig 3D");
        f.TexCube = VkStandIn(vk, ImageKind.Cube, Format.Rgba8, 6, "rig cube");
        f.TexCubeArray = VkStandIn(vk, ImageKind.CubeArray, Format.Rgba8, 6, "rig cube array");
        f.Depth2DArray = VkStandIn(vk, ImageKind.Tex2DArray, Format.D32F, 1, "rig shadow array");
        f.DepthCubeArray = VkStandIn(vk, ImageKind.CubeArray, Format.D32F, 6, "rig shadow cube array");
        return !(f.G0.IsNull || f.G1.IsNull || f.G2.IsNull || f.Depth.IsNull || f.Vis.IsNull || f.Key.IsNull || f.Hdr.IsNull
                 || f.Rec.IsNull || f.Lights.IsNull || f.Counts.IsNull || f.Idx.IsNull || f.Movers.IsNull || f.Stats.IsNull
                 || f.Draws.IsNull || f.Chart.IsNull || f.Pass.IsNull || f.Def.IsNull || f.ZeroUbo.IsNull || f.Read.IsNull
                 || f.Linear.IsNull || f.Shadow.IsNull || f.Tex2D.IsNull || f.TexU2D.IsNull || f.Tex2DArray.IsNull
                 || f.Tex3D.IsNull || f.TexCube.IsNull || f.TexCubeArray.IsNull || f.Depth2DArray.IsNull || f.DepthCubeArray.IsNull);
    }

    /// <summary>A device buffer of <paramref name="bytes"/>, filled; at least one word so an empty scene still binds.</summary>
    private static BufferId RigBuffer(VkBackend vk, ReadOnlySpan<byte> bytes, BufferUse use, string label)
    {
        BufferId b = vk.CreateBuffer(new BufferDesc(Math.Max(bytes.Length, 16), use | BufferUse.CopyDst, MemoryKind.Device, label));
        if (b.IsNull) return b;
        var cl = new CommandList(label, Math.Max(bytes.Length, 16) + 4096);
        cl.ZeroBuffer(b);
        if (bytes.Length > 0) cl.UpdateBuffer(b, 0, bytes);
        if (vk.Submit(cl)) return b;
        vk.Destroy(b);
        return default;
    }

    /// <summary>The fill, the froxel build, the walk and the film pass in one submit, then the HDR target read back.</summary>
    private static float[]? RigRun(VkBackend vk, DeferredRigScene sc, RigFrame f, VkDefStage walk, VkDefStage film,
                                   VkDefStage build, PipelineId fill, ExtenderLog log)
    {
        int w = sc.W, h = sc.H, lights = sc.Lights.Length / DeferredRigScene.LightFloats;
        Matrix4x4 vp = sc.View * sc.Proj;
        if (!Matrix4x4.Invert(vp, out Matrix4x4 invVp)) return null;
        RigBlocks(vk, sc, f, walk, film, build, invVp, out byte[] pass, out byte[] def);
        BufferId cases = RigBuffer(vk, MemoryMarshal.AsBytes(sc.Pixels.AsSpan()), BufferUse.Storage, "rig pixels");
        if (cases.IsNull || f.LooseWalk.IsNull || f.LooseFilm.IsNull || f.LooseBuild.IsNull) { vk.Destroy(cases); return null; }
        var cl = new CommandList("rig frame", 1 << 20, log);
        cl.UpdateBuffer(f.Pass, 0, pass);
        cl.UpdateBuffer(f.Def, 0, def);
        cl.ClearImage(f.Hdr);

        cl.SetPipeline(fill);
        cl.BindSet(0, new BindSet(default, [new BufferBinding(SsboSlots.GoldenIn, cases), new BufferBinding(SsboSlots.Chart, f.Chart)],
            default, [new ImageBinding(0, f.G0), new ImageBinding(1, f.G1), new ImageBinding(2, f.G2), new ImageBinding(3, f.Depth),
                      new ImageBinding(4, f.Vis), new ImageBinding(5, f.Key)]));
        cl.PushConstants(0, MemoryMarshal.AsBytes((ReadOnlySpan<int>)[w, h, BitConverter.SingleToInt32Bits(sc.Proj.M33),
                                                                      BitConverter.SingleToInt32Bits(sc.Proj.M43)]));
        cl.Dispatch((w + 7) / 8, (h + 7) / 8);

        cl.SetPipeline(build.Pipe);
        cl.BindSet(0, new BindSet([new BufferBinding(VkGlsl.LooseBinding, f.LooseBuild)],
            [new BufferBinding(SsboSlots.Lights, f.Lights), new BufferBinding(SsboSlots.ClusterCount, f.Counts),
             new BufferBinding(SsboSlots.ClusterIdx, f.Idx), new BufferBinding(SsboSlots.Movers, f.Movers),
             new BufferBinding(SsboSlots.LightStats, f.Stats)], default, default));
        cl.Dispatch((RigGridX * RigGridY * RigGridZ + AlloyLights.GroupSize - 1) / AlloyLights.GroupSize);

        cl.ReadImages([f.G0, f.G1, f.G2, f.Depth, f.Vis, f.Key]);
        cl.SetPipeline(walk.Pipe);
        RigBind(cl, walk, f, f.LooseWalk);
        cl.Dispatch((w + VkDefGroup - 1) / VkDefGroup, (h + VkDefGroup - 1) / VkDefGroup);
        cl.SetPipeline(film.Pipe);
        RigBind(cl, film, f, f.LooseFilm);
        cl.Dispatch((w + VkDefGroup - 1) / VkDefGroup, (h + VkDefGroup - 1) / VkDefGroup);
        cl.CopyImageToBuffer(new CopyToBufferCmd(f.Hdr, new ImageRegion(0, 0, 0, 0), w, h, f.Read, 0));
        var halves = new Half[w * h * 4];
        bool ok = vk.Submit(cl) && vk.ReadBack(f.Read, MemoryMarshal.AsBytes(halves.AsSpan()));
        vk.Destroy(cases);
        if (!ok) return null;
        var px = new float[halves.Length];
        for (int i = 0; i < px.Length; i++) px[i] = (float)halves[i];
        log.Info($"AlloyRenderer: rig frame {w}x{h}, {lights} lights, {sc.Cubes.Length} shadow cubes"
               + (sc.WalkOverride != null ? ", the walk from a content override" : ""));
        return px;
    }

    /// <summary>The pass block, the deferred block and the three loose blocks, every term a literal of this rig.</summary>
    private static void RigBlocks(VkBackend vk, DeferredRigScene sc, RigFrame f, VkDefStage walk, VkDefStage film,
                                  VkDefStage build, in Matrix4x4 invVp, out byte[] pass, out byte[] def)
    {
        int w = sc.W, h = sc.H, lights = sc.Lights.Length / DeferredRigScene.LightFloats;
        var pb = default(PassBlock);
        for (int i = 0; i < sc.Cubes.Length; i++) { pb.Light[i] = sc.Cubes[i]; pb.Fade[i].Value = 1f; }
        pb.LocalSoft = new Vector4(2f, 0f, 0f, 4f);
        pb.LocalFloor = 0.1f;
        pb.Ambient = new Vector3(0.2f);
        pb.Tile = new Vector4(0f, 0f, (float)w / RigGridX, (float)h / RigGridY);
        pb.GridX = RigGridX; pb.GridY = RigGridY; pb.GridZ = RigGridZ;
        pb.Reach = 0.8f;
        pb.Falloff = new Vector3(1f, 0.15f, 1f);
        pb.Wrap = 1f;
        pb.Z = new Vector2(sc.Near, sc.Far);
        pb.Hot = new Vector2(0.3f, 0.2f);
        pb.Sss = new Vector2(0.2f, 0.25f);
        pb.WrapN = 0.05f;
        pb.DayDim = 1f;
        pb.Relevance = new Vector2(0.01f, 0.02f);
        pb.Bounds = 1f;
        pb.ShBudget = 4f;
        pb.LightFloor = 0.0003f;
        pass = new byte[PassBytes];
        MemoryMarshal.Write(pass, in pb);

        var b = new DeferredBlock
        {
            InvVp = invVp, Proj = sc.Proj, View = sc.View, EyePos = sc.Eye, Tiles = 0,
            Rect = new Int4(0, 0, w, h), ShSize = new Int2((w + 1) / 2, (h + 1) / 2), LightCount = lights, ShRes = 2,
        };
        def = new byte[Std140.BlockBytes<DeferredBlock>()];
        MemoryMarshal.Write(def, in b);

        f.LooseWalk = RigLoose(vk, walk, sc, lights, w);
        f.LooseFilm = RigLoose(vk, film, sc, lights, w);
        f.LooseBuild = RigLoose(vk, build, sc, lights, w);
    }

    /// <summary>Stage <paramref name="st"/>'s loose block filled with the rig's literals, on a buffer of its own.</summary>
    private static BufferId RigLoose(VkBackend vk, VkDefStage st, DeferredRigScene sc, int lights, int w)
    {
        Array.Clear(st.Block);
        LooseBlock.Capture(st.Members, st.Block);
        try
        {
            LooseBlock.Set(st.L.Cubes, sc.Cubes.Length);
            LooseBlock.Set(st.L.Material, 1f, 0.5f, 1f, 0f);
            LooseBlock.Set(st.L.LightOcc, [1f, GlossyRough]);
            LooseBlock.Set(st.L.LmMovers, 0);
            LooseBlock.Set(st.L.View, in sc.View);
            LooseBlock.Set(st.L.Grid, (ReadOnlySpan<int>)[RigGridX, RigGridY, RigGridZ]);
            LooseBlock.Set(st.L.ProjXY, (ReadOnlySpan<float>)[sc.Proj.M11, sc.Proj.M22]);
            LooseBlock.Set(st.L.Z, (ReadOnlySpan<float>)[sc.Near, sc.Far]);
            LooseBlock.Set(st.L.LightCount, lights);
            LooseBlock.Set(st.L.Reach, 0.8f);
            LooseBlock.Set(st.L.LightFloor, (ReadOnlySpan<float>)[0.0003f, 1f]);
            LooseBlock.Set(st.L.BinCap, 0);
            LooseBlock.Set(st.L.Atten, (ReadOnlySpan<float>)[0f, 0f]);
            LooseBlock.Set(st.L.Hot, (ReadOnlySpan<float>)[0f, 0f]);
            LooseBlock.Set(st.L.Rel, (ReadOnlySpan<float>)[0.01f, 0.8f]);
            LooseBlock.Set(st.L.Pad, (ReadOnlySpan<float>)[2f / w, 2f / sc.H]);
            LooseBlock.Set(st.L.Tally, 0);
        }
        finally { LooseBlock.EndCapture(); }
        return RigBuffer(vk, st.Block, BufferUse.Uniform, "rig loose");
    }

    /// <summary>A deferred stage's set 0 over the rig's frame: the same routing as <see cref="VkDefBindSet"/>, onto
    /// the rig's own resources and stand-ins.</summary>
    private static void RigBind(CommandList cl, VkDefStage st, RigFrame f, BufferId loose)
    {
        Span<BufferBinding> ubo = stackalloc BufferBinding[(int)UboSlots.Budget];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[(int)SsboSlots.Budget];
        Span<ImageBinding> sampled = stackalloc ImageBinding[48];
        Span<ImageBinding> images = stackalloc ImageBinding[8];
        int nu = 0, ns = 0, nt = 0, ni = 0;
        foreach (VkOpaque o in st.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock:
                    ubo[nu++] = new BufferBinding(o.Binding, o.Binding == VkGlsl.LooseBinding ? loose
                        : o.Binding == UboSlots.Pass ? f.Pass : o.Binding == UboSlots.Deferred ? f.Def : f.ZeroUbo);
                    break;
                case VkBindKind.StorageBlock:
                    ssbo[ns++] = new BufferBinding(o.Binding, o.Binding switch
                    {
                        SsboSlots.Draw => f.Draws,
                        SsboSlots.Lights => f.Lights,
                        SsboSlots.ClusterCount => f.Counts,
                        SsboSlots.ClusterIdx => f.Idx,
                        SsboSlots.Movers => f.Movers,
                        SsboSlots.Chart => f.Chart,
                        _ => f.Stats,
                    });
                    break;
                case VkBindKind.StorageImage:
                    images[ni++] = new ImageBinding(o.Binding, o.Binding == 0 ? f.Hdr : f.Rec, default, 0);
                    break;
                default:
                    sampled[nt++] = o.Binding switch
                    {
                        TextureSlots.Gbuf0 => new ImageBinding(o.Binding, f.G0),
                        TextureSlots.Gbuf1 => new ImageBinding(o.Binding, f.G1),
                        TextureSlots.Gbuf2 => new ImageBinding(o.Binding, f.G2),
                        TextureSlots.GbufDepth => new ImageBinding(o.Binding, f.Depth),
                        TextureSlots.ShadowVis => new ImageBinding(o.Binding, f.Vis),
                        TextureSlots.ShadowKey => new ImageBinding(o.Binding, f.Key),
                        _ => o.Type switch
                        {
                            "usampler2D" => new ImageBinding(o.Binding, f.TexU2D),
                            "sampler2DArray" => new ImageBinding(o.Binding, f.Tex2DArray, f.Linear),
                            "sampler2DArrayShadow" => new ImageBinding(o.Binding, f.Depth2DArray, f.Shadow),
                            "samplerCube" => new ImageBinding(o.Binding, f.TexCube, f.Linear),
                            "samplerCubeArray" => new ImageBinding(o.Binding, f.DepthCubeArray, f.Linear),
                            "samplerCubeArrayShadow" => new ImageBinding(o.Binding, f.DepthCubeArray, f.Shadow),
                            "sampler3D" => new ImageBinding(o.Binding, f.Tex3D, f.Linear),
                            _ => new ImageBinding(o.Binding, f.Tex2D, f.Linear),
                        },
                    };
                    break;
            }
        }
        cl.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], images[..ni]));
    }
}
