using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The grass class (<see cref="PassId.GrassOwned"/>) on Vulkan: <see cref="GrassField"/>'s own bake and plan, the
/// scatter GL dispatches regenerating every visible blade into the command and record buffers, then the procedural
/// blades and the atlas cards drawn indirect from them, after the deferred lights as GL's graph has it.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The class's resources on one device, replaced whole with the device.</summary>
    private sealed class VkGrassState
    {
        public VkBackend? Dev;
        public bool Tried, Live;
        public VkProg? Cards, Blades;
        public readonly GrassField.VkLocs LC = new(), LB = new();
        public VkDefStage? Scatter;
        public GrassField.ScatterLoc LS;
        public PipelineId CardPipe, BladePipe;
        public BufferId Faces, Patches, Mats, Out, Cmd;
        public long PatchCap, MatCap;
        public ImageId Push, Ground;
        public int GroundW, GroundH;
        /// <summary>The push field's generation the image holds; -1 = never sent.</summary>
        public long PushSent = -1;
        public SamplerId Clamp;
        public CommandList? Side;
    }

    private static VkGrassState _vkGrass = new();


    /// <summary>Room for the class's binds and draws: a bind and a draw per blade strip and card slot.</summary>
    private const int GrassArenaBytes = 1 << 15;

    /// <summary>The grass class into <paramref name="cl"/>: the tables sent, the scatter's two dispatches, the ground
    /// copy, then one pass of blades and cards. False only when the uniform arena is full.</summary>
    private static bool VkGrassPass(VkBackend vk, CommandList cl, VkColourState c, in Matrix4x4 vp, int* viewport,
                                    float near, float far, bool lightingOn, ExtenderLog log)
    {
        VkGrassState g = _vkGrass;
        if (g.Dev != vk) _vkGrass = g = new VkGrassState { Dev = vk };
        if (_capturing || !VkGrassEnsure(vk, g, log)) return true;
        Matrix4x4 m = vp;
        if (!GrassField.StageVk(&m, g.Blades != null, out GrassField.VkFrame f) || f.Patches == 0) return true;
        ReadOnlySpan<byte> faces = GrassField.VkNewFaces(out long faceAt);
        if (!faces.IsEmpty) cl.UpdateBuffer(g.Faces, faceAt, faces);
        ReadOnlySpan<byte> patches = MemoryMarshal.AsBytes(GrassField.VkPatches(f.Patches));
        ReadOnlySpan<byte> mats = MemoryMarshal.AsBytes(GrassField.VkMats(f.Mats));
        if (!VkGrowBuf(vk, ref g.Patches, ref g.PatchCap, patches.Length, "vk grass patches")
            || !VkGrowBuf(vk, ref g.Mats, ref g.MatCap, mats.Length, "vk grass materials")) return true;
        cl.UpdateBuffer(g.Patches, 0, patches);
        cl.UpdateBuffer(g.Mats, 0, mats);
        Span<uint> cmd = stackalloc uint[GrassField.VkCmds * 4];
        GrassField.VkCommands(cmd);
        cl.UpdateBuffer(g.Cmd, 0, MemoryMarshal.AsBytes(cmd));
        ReadOnlySpan<byte> push = GrassField.VkPush(ref g.PushSent);
        if (!push.IsEmpty) vk.Upload(g.Push, 0, 0, push);

        // both scatter blocks staged, then the arena range they took sent ahead of the dispatches reading it
        int arena0 = c.ArenaUsed;
        VkDefStage sc = g.Scatter!;
        int scatter = VkGrassScatterBlock(g, &m, 0), clamp = VkGrassScatterBlock(g, &m, f.Mats);
        if (scatter < 0 || clamp < 0) return false;
        int len = (c.ArenaUsed - arena0 + 3) & ~3;
        cl.UpdateBuffer(c.Arena, arena0, c.ArenaBytes.AsSpan(arena0, Math.Min(len, ArenaCap - arena0)));
        cl.SetPipeline(sc.Pipe);
        VkGrassScatterBind(cl, g, c, scatter);
        cl.Dispatch(f.Patches, f.Split);
        VkGrassScatterBind(cl, g, c, clamp);
        cl.Dispatch(1);

        // the frame as it stands before the grass, for the root blend and the blades' fade into the ground
        bool ground = GrassField.VkWantsGround(in f) && VkGrassGround(vk, g, c.W, c.H);
        if (ground)
        {
            cl.CopyImage(new CopyImageCmd(c.Hdr, new ImageRegion(0, 0, 0, 0), g.Ground, new ImageRegion(0, 0, 0, 0), c.W, c.H, 1));
            ReadOnlySpan<ImageId> read = [g.Ground];
            cl.ReadImages(read);
        }
        ReadOnlySpan<ImageBinding> own = [new(TextureSlots.GrassPush, g.Push, g.Clamp),
                                          new(TextureSlots.GrassGround, ground ? g.Ground : c.Tex2D, g.Clamp)];
        Span<BufferBinding> storage = stackalloc BufferBinding[6];
        int ns = 0;
        storage[ns++] = new BufferBinding(SsboSlots.GrassMats, g.Mats);
        storage[ns++] = new BufferBinding(SsboSlots.GrassInst, g.Out);
        if (lightingOn && !VkDeferredLights().IsNull)
        {
            storage[ns++] = new BufferBinding(SsboSlots.Lights, VkDeferredLights());
            if (!VkClusterCounts.IsNull) storage[ns++] = new BufferBinding(SsboSlots.ClusterCount, VkClusterCounts);
            if (!VkClusterIdx.IsNull) storage[ns++] = new BufferBinding(SsboSlots.ClusterIdx, VkClusterIdx);
            if (!VkMovers.IsNull) storage[ns++] = new BufferBinding(SsboSlots.Movers, VkMovers);
        }

        CommandList side = g.Side!;
        side.Reset();
        arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, viewport[2], viewport[3]);
        side.SetRasterState(new RasterState(CullMode.None));
        if (f.BladeNear + f.BladeFar > 0 && g.Blades != null)
        {
            side.SetPipeline(g.BladePipe);
            side.SetDepthState(new DepthState(true, true, CompareOp.LessEqual));
            for (int k = 0; k < 2; k++)
            {
                if ((k == 0 ? f.BladeNear : f.BladeFar) <= 0) continue;
                int at = VkGrassColourBlock(g.Blades, g.LB, &m, viewport, near, far, lightingOn, ground, false, k == 1, default, default);
                int pass = VkArenaPass();
                if (at < 0 || pass < 0) return false;
                VkBindSet(side, g.Blades, c, at, pass, c.Slots, 0, default, storage[..ns], own);
                side.DrawIndirect(g.Cmd, k * 4 * sizeof(uint), 1);
            }
        }
        if (f.Slots > 0 && g.Cards != null)
        {
            side.SetPipeline(g.CardPipe);
            side.SetDepthState(new DepthState(true, true, CompareOp.LessEqual));
            if (!VkGrassSlots(side, g, c, g.Cards, g.LC, f, &m, viewport, near, far, lightingOn, ground, own, storage[..ns]))
                return false;
        }
        ReadOnlySpan<ColorAttachment> colors = [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));
        if (!g.Live)
        {
            g.Live = true;
            log.Always($"AlloyRenderer: the Vulkan grass is LIVE -- {f.Patches} patch(es) scattered, {f.BladeNear}+{f.BladeFar} "
                     + $"blade record(s) reserved, {f.Slots} card slot(s){(ground ? ", the ground copy taken" : "")}. Not on Vulkan "
                     + "yet: grass shadows in the sun cascades, grass velocity (TAA takes the camera's vectors over it), and the "
                     + "card prepass with its soft fringe (NVIDIA's pipeline compiler crashes on that stage; the cards write their own depth)");
        }
        return true;
    }

    /// <summary>Every card slot's indirect draw, its atlas bound and its material's colours.</summary>
    private static bool VkGrassSlots(CommandList side, VkGrassState g, VkColourState c, VkProg p, GrassField.VkLocs v,
                                     in GrassField.VkFrame f, Matrix4x4* vp, int* viewport, float near, float far, bool lightingOn,
                                     bool ground, ReadOnlySpan<ImageBinding> own, ReadOnlySpan<BufferBinding> storage)
    {
        for (int slot = 0; slot < f.Slots; slot++)
        {
            int tid = GrassField.VkSlot(slot, out Vector3 dif, out Vector3 amb);
            if (tid == 0) continue;
            int at = VkGrassColourBlock(p, v, vp, viewport, near, far, lightingOn, ground, true, false, dif, amb);
            int pass = VkArenaPass();
            if (at < 0 || pass < 0) return false;
            VkBindSet(side, p, c, at, pass, c.Slots, tid, default, storage, own);
            side.DrawIndirect(g.Cmd, (2 + slot) * 4 * sizeof(uint), 1);
        }
        return true;
    }

    /// <summary>A colour stage's loose block, as GL's <c>Issue</c> sets it: the shared and colour terms, then the blade
    /// strip's segments or the card material's colours. Its arena offset; the pass block is staged inside.</summary>
    private static int VkGrassColourBlock(VkProg p, GrassField.VkLocs v, Matrix4x4* vp, int* viewport, float near, float far,
                                          bool lightingOn, bool ground, bool cards, bool farBlades, Vector3 dif, Vector3 amb)
    {
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            GrassField.VkColourTerms(v, vp, viewport, near, far, lightingOn, ground, cards);
            if (cards)
            {
                LooseBlock.Set(v.L.MatDif, [dif.X, dif.Y, dif.Z]);
                LooseBlock.Set(v.L.MatAmb, [amb.X, amb.Y, amb.Z]);
            }
            else LooseBlock.Set(v.L.Segments, GrassField.VkSegments(farBlades));
        }
        finally { LooseBlock.EndCapture(); }
        return VkArena(p.Block);
    }

    /// <summary>One scatter dispatch's loose block, <paramref name="clamp"/> its pass switch; its arena offset.</summary>
    private static int VkGrassScatterBlock(VkGrassState g, Matrix4x4* vp, int clamp)
    {
        VkDefStage sc = g.Scatter!;
        Array.Clear(sc.Block);
        LooseBlock.Capture(sc.Members, sc.Block);
        try { GrassField.VkScatterTerms(in g.LS, vp, clamp); }
        finally { LooseBlock.EndCapture(); }
        return VkArena(sc.Block);
    }

    /// <summary>The scatter's set: its block, the five buffers it reads and writes, the push field.</summary>
    private static void VkGrassScatterBind(CommandList cl, VkGrassState g, VkColourState c, int loose)
    {
        VkDefStage sc = g.Scatter!;
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[8];
        Span<ImageBinding> sampled = stackalloc ImageBinding[4];
        int nu = 0, ns = 0, nt = 0;
        foreach (VkOpaque o in sc.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock:
                    ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, loose, sc.Block.Length)
                              : new BufferBinding(o.Binding, c.ZeroUbo);
                    break;
                case VkBindKind.StorageBlock:
                    ssbo[ns++] = new BufferBinding(o.Binding, o.Binding switch
                    {
                        SsboSlots.GrassFaces => g.Faces, SsboSlots.GrassPatches => g.Patches, SsboSlots.GrassMats => g.Mats,
                        SsboSlots.GrassOut => g.Out, SsboSlots.GrassCmd => g.Cmd, _ => c.ZeroSsbo,
                    });
                    break;
                case VkBindKind.Sampled:
                    sampled[nt++] = new ImageBinding(o.Binding, g.Push, g.Clamp);
                    break;
            }
        }
        cl.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], default));
    }

    /// <summary>The ground copy at the scene's size, made on first ask and resized with the view.</summary>
    private static bool VkGrassGround(VkBackend vk, VkGrassState g, int w, int h)
    {
        if (!g.Ground.IsNull && g.GroundW == w && g.GroundH == h) return true;
        vk.Destroy(g.Ground);
        g.Ground = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, w, h, 1, 1, 1, 1,
                                                ImageUse.Sampled | ImageUse.CopyDst, "vk grass ground copy"));
        g.GroundW = w; g.GroundH = h;
        return !g.Ground.IsNull;
    }

    /// <summary>The scatter, the three stages, their pipelines and the class's buffers, made once per device. Without the
    /// scatter or the card stage the class sits the session out, said once; without the blades every atlas draws as
    /// cards, as GL's build falls back.</summary>
    private static bool VkGrassEnsure(VkBackend vk, VkGrassState g, ExtenderLog log)
    {
        if (g.Tried) return g.Scatter != null && g.Cards != null;
        g.Tried = true;
        g.Side = new CommandList("vulkan grass", GrassArenaBytes, log);
        g.Clamp = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
        string src = ShaderResource.Tokens(GrassField.ScatterCs);
        g.Scatter = VkDefCompile(vk, VkGlsl.TranslateCompute(src, GrassField.VkUnits), src, "vk grass scatter", log);
        if (g.Scatter != null)
        {
            VkGrassState s = g;
            LooseBlock.Resolve(g.Scatter.Names, () => s.LS = GrassField.ScatterLocs());
        }
        g.Cards = VkGrassProgram(vk, VkPrograms.Id.Grass, g.LC, log);
        g.Blades = VkGrassProgram(vk, VkPrograms.Id.GrassBlade, g.LB, log);
        if (g.Cards != null)
        {
            g.CardPipe = VkGrassPipe(vk, g.Cards, Topology.Triangles, "vk grass cards");
            if (g.CardPipe.IsNull) g.Cards = null;
        }
        if (g.Blades != null && (g.BladePipe = VkGrassPipe(vk, g.Blades, Topology.TriangleStrip, "vk grass blades")).IsNull)
            g.Blades = null;
        g.Faces = vk.CreateBuffer(new BufferDesc(GrassField.VkFaceBytes, BufferUse.Storage | BufferUse.CopyDst, MemoryKind.Device, "vk grass faces"));
        g.Out = vk.CreateBuffer(new BufferDesc(GrassField.VkRecordBytes, BufferUse.Storage, MemoryKind.Device, "vk grass records"));
        g.Cmd = vk.CreateBuffer(new BufferDesc(GrassField.VkCmds * 4 * sizeof(uint), BufferUse.Storage | BufferUse.Indirect | BufferUse.CopyDst,
                                               MemoryKind.Device, "vk grass commands"));
        g.Push = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, GrassField.VkPushRes, GrassField.VkPushRes, 1, 1, 1, 1,
                                              ImageUse.Sampled | ImageUse.CopyDst, "vk grass push field"));
        if (g.Scatter == null || g.Cards == null || g.Faces.IsNull || g.Out.IsNull || g.Cmd.IsNull || g.Push.IsNull || g.Clamp.IsNull)
        {
            g.Scatter = null;
            log.Always("AlloyRenderer: the Vulkan grass scatter, card stage or a buffer was REFUSED (logged above) -- NO grass is "
                     + "drawn on Vulkan this session");
            return false;
        }
        if (g.Blades == null) log.Always("AlloyRenderer: the Vulkan blade stage was REFUSED (logged above) -- every grass atlas draws as cards");
        return true;
    }

    /// <summary>Program <paramref name="id"/> and its locations, or null (logged by the build).</summary>
    private static VkProg? VkGrassProgram(VkBackend vk, VkPrograms.Id id, GrassField.VkLocs v, ExtenderLog log)
    {
        VkProg? p = VkBuild(vk, id, log);
        if (p == null) return null;
        LooseBlock.Resolve(p.Names, () => { v.L = GrassField.Locate(); v.F = Programs.FeatLocs(); });
        return p;
    }

    /// <summary>A grass pipeline into the scene colour and depth: no vertex input, both faces, no blend. Depth is set
    /// per draw.</summary>
    private static PipelineId VkGrassPipe(VkBackend vk, VkProg p, Topology topo, string label)
    {
        var d = new PipelineDesc
        {
            Vs = p.Vs, Fs = p.Fs, Topo = topo, Raster = new RasterState(CullMode.None),
            Depth = new DepthState(true, true, CompareOp.LessEqual), Label = label,
        };
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = Format.Rgba16F;
        d.Targets.Depth = Format.D32F;
        d.Targets.Samples = 1;
        return vk.CreatePipeline(in d);
    }
}
