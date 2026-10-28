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
/// scatter regenerating every visible blade into the command and record buffers, then the cards' depth prepass and the
/// procedural blades and atlas cards drawn indirect from them, after the deferred lights. The same draws go again
/// into the sun cascades the next frame's bake re-baked, and into the velocity field after the camera's.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The class's resources on one device, replaced whole with the device.</summary>
    private sealed class VkGrassState
    {
        public VkBackend? Dev;
        public bool Tried, Live, CastLive, MotionLive, Prepass;
        public VkProg? Cards, Blades, CardDepth, BladeDepth, CardMotion, BladeMotion;
        public readonly GrassField.VkLocs LC = new(), LB = new(), LCD = new(), LBD = new(), LCM = new(), LBM = new();
        public VkDefStage? Scatter;
        public GrassField.ScatterLoc LS;
        public PipelineId CardPipe, BladePipe, CardSoftPipe, CardPrePipe, CardCastPipe, BladeCastPipe, CardMotionPipe, BladeMotionPipe;
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

    /// <summary>What one grass draw writes: the scene colour, the cards' prepass depth, a sun cascade's depth, or the
    /// velocity field.</summary>
    private enum GrassDraw : byte { Colour, Prepass, Cast, Motion }

    /// <summary>One grass draw's terms: its kind, the camera (and last frame's, for the velocity), the viewport and
    /// clip range, the light rig, the ground copy, and whether the cards' fringe blends over the prepass's core.</summary>
    private readonly struct VkGrassView(GrassDraw kind, Matrix4x4* vp, Matrix4x4* prevVp, int* viewport, float near, float far,
                                        bool lightingOn, bool ground, bool soft)
    {
        public readonly GrassDraw Kind = kind;
        public readonly Matrix4x4* Vp = vp, PrevVp = prevVp;
        public readonly int* Viewport = viewport;
        public readonly float Near = near, Far = far;
        public readonly bool LightingOn = lightingOn, Ground = ground, Soft = soft;
    }

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
        bool prepass = f.Slots > 0 && g.Prepass;
        if (prepass)
        {
            side.SetPipeline(g.CardPrePipe);
            side.SetDepthState(new DepthState(true, true, CompareOp.Less));
            var pre = new VkGrassView(GrassDraw.Prepass, &m, null, viewport, near, far, false, ground, true);
            if (!VkGrassDraws(side, g, c, g.CardDepth!, g.LCD, in f, in pre, true, own, storage[..ns])) return false;
        }
        var colour = new VkGrassView(GrassDraw.Colour, &m, null, viewport, near, far, lightingOn, ground, prepass);
        if (f.BladeNear + f.BladeFar > 0 && g.Blades != null)
        {
            side.SetPipeline(g.BladePipe);
            side.SetDepthState(new DepthState(true, true, CompareOp.LessEqual));
            if (!VkGrassDraws(side, g, c, g.Blades, g.LB, in f, in colour, false, own, storage[..ns])) return false;
        }
        if (f.Slots > 0 && g.Cards != null)
        {
            side.SetPipeline(prepass ? g.CardSoftPipe : g.CardPipe);
            side.SetDepthState(new DepthState(true, !prepass, CompareOp.LessEqual));
            if (!VkGrassDraws(side, g, c, g.Cards, g.LC, in f, in colour, true, own, storage[..ns])) return false;
        }
        ReadOnlySpan<ColorAttachment> colors = [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));
        GrassField.VkDrew(in f, ground);
        if (!g.Live)
        {
            g.Live = true;
            log.Always($"AlloyRenderer: the Vulkan grass is LIVE -- {f.Patches} patch(es) scattered, {f.BladeNear}+{f.BladeFar} "
                     + $"blade record(s) reserved, {f.Slots} card slot(s){(ground ? ", the ground copy taken" : "")}; the card prepass "
                     + $"{(g.Prepass ? "on, the fringe blended" : "REFUSED, the cards write their own depth")}, casting into the sun "
                     + $"cascades {VkGrassArmed(g.CardCastPipe, g.BladeCastPipe, g.Blades)}, velocity {VkGrassArmed(g.CardMotionPipe, g.BladeMotionPipe, g.Blades)}");
        }
        return true;
    }

    /// <summary>A late draw's state for the LIVE line: both paths armed, or which one was refused.</summary>
    private static string VkGrassArmed(PipelineId cards, PipelineId blades, VkProg? bladeStage)
        => cards.IsNull ? "REFUSED" : bladeStage != null && blades.IsNull ? "armed for cards, REFUSED for blades" : "armed";

    /// <summary>One stage's blades (both regions) or cards (every slot with its atlas) into <paramref name="side"/>, each
    /// draw with its own block and set. False only when the uniform arena is full.</summary>
    private static bool VkGrassDraws(CommandList side, VkGrassState g, VkColourState c, VkProg p, GrassField.VkLocs v,
                                     in GrassField.VkFrame f, in VkGrassView d, bool cards, ReadOnlySpan<ImageBinding> own,
                                     ReadOnlySpan<BufferBinding> storage)
    {
        int n = cards ? f.Slots : 2;
        for (int k = 0; k < n; k++)
        {
            int tid = 0;
            Vector3 dif = default, amb = default;
            if (cards) { if ((tid = GrassField.VkSlot(k, out dif, out amb)) == 0) continue; }
            else if ((k == 0 ? f.BladeNear : f.BladeFar) <= 0) continue;
            int at = VkGrassBlock(p, v, in d, cards, k == 1, dif, amb);
            int pass = d.Kind == GrassDraw.Colour ? VkArenaPass() : -1;
            if (at < 0 || d.Kind == GrassDraw.Colour && pass < 0) return false;
            VkBindSet(side, p, c, at, pass, c.Slots, tid, default, storage, own);
            side.DrawIndirect(g.Cmd, (cards ? 2 + k : k) * 4 * sizeof(uint), 1);
        }
        return true;
    }

    /// <summary>A draw's loose block: the colour terms (the pass block staged inside) or the depth ones, then the blade
    /// strip's segments or the card material's colours. Its arena offset.</summary>
    private static int VkGrassBlock(VkProg p, GrassField.VkLocs v, in VkGrassView d, bool cards, bool farBlades, Vector3 dif, Vector3 amb)
    {
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            if (d.Kind == GrassDraw.Colour)
                GrassField.VkColourTerms(v, d.Vp, d.Viewport, d.Near, d.Far, d.LightingOn, d.Ground, cards, d.Soft);
            else GrassField.VkDepthTerms(v, d.Vp, d.Viewport, d.Kind == GrassDraw.Cast, d.Soft, d.PrevVp, d.Ground);
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

    /// <summary>Last pass's blades and cards into the sun cascade layers the caster flush just re-baked -- the bits of
    /// <paramref name="statMask"/> in <paramref name="stat"/>, of <paramref name="dynMask"/> in <paramref name="dyn"/> --
    /// under each cascade's baked matrix. The flush runs ahead of this frame's scatter, so the records are last frame's.</summary>
    internal static void VkGrassShadows(VkBackend vk, CommandList cl, ImageId stat, int statMask, ImageId dyn, int dynMask, int size)
    {
        VkGrassState g = _vkGrass;
        if ((statMask | dynMask) == 0 || g.Dev != vk) return;
        GrassField.VkFrame f = GrassField.VkLast(out _);
        bool blades = f.BladeNear + f.BladeFar > 0 && !g.BladeCastPipe.IsNull, cards = f.Slots > 0 && !g.CardCastPipe.IsNull;
        if (f.Patches == 0 || !(blades || cards)) return;
        VkColourState c = _vkc;
        Span<ImageBinding> ownB = stackalloc ImageBinding[CastOwnMax], ownC = stackalloc ImageBinding[CastOwnMax];
        ownB = ownB[..(blades ? VkGrassCastOwn(g.BladeDepth!, g, c, ownB) : 0)];
        ownC = ownC[..(cards ? VkGrassCastOwn(g.CardDepth!, g, c, ownC) : 0)];
        ReadOnlySpan<BufferBinding> storage = [new(SsboSlots.GrassMats, g.Mats), new(SsboSlots.GrassInst, g.Out)];
        int* viewport = stackalloc int[4] { 0, 0, size, size };
        CommandList side = g.Side!;
        int drawn = 0;
        for (int t = 0; t < 2; t++)
        {
            int mask = t == 0 ? statMask : dynMask;
            ImageId img = t == 0 ? stat : dyn;
            for (int i = 0; !img.IsNull && mask >> i != 0; i++)
            {
                if ((mask >> i & 1) == 0) continue;
                Matrix4x4 vp = CasterPass.CascadeVpOf(i);
                var d = new VkGrassView(GrassDraw.Cast, &vp, null, viewport, 0f, 0f, false, false, false);
                side.Reset();
                int arena0 = c.ArenaUsed;
                side.SetViewport(0, 0, size, size);
                side.SetRasterState(new RasterState(CullMode.None));
                if (blades)
                {
                    side.SetPipeline(g.BladeCastPipe);
                    side.SetDepthState(new DepthState(true, true, CompareOp.LessEqual));
                    if (!VkGrassDraws(side, g, c, g.BladeDepth!, g.LBD, in f, in d, false, ownB, storage)) return;
                }
                if (cards)
                {
                    side.SetPipeline(g.CardCastPipe);
                    side.SetDepthState(new DepthState(true, true, CompareOp.LessEqual));
                    if (!VkGrassDraws(side, g, c, g.CardDepth!, g.LCD, in f, in d, true, ownC, storage)) return;
                }
                VkFlushPass(cl, side, arena0, new RenderPassDesc(default, new DepthAttachment(img, 0, i, LoadOp.Load, StoreOp.Store, 1f)));
                drawn++;
            }
        }
        if (!g.CastLive && drawn > 0)
        {
            g.CastLive = true;
            _log?.Always($"AlloyRenderer: the Vulkan grass shadows are LIVE -- {drawn} cascade layer(s) took last frame's "
                       + $"{f.BladeNear + f.BladeFar} blade record(s) and {f.Slots} card slot(s) at {size}px");
        }
    }

    /// <summary>Did the card cascade caster build? Without it the bake re-bakes no cascade for the grass.</summary>
    internal static bool VkGrassCasts => !_vkGrass.CardCastPipe.IsNull;

    /// <summary>Room for a depth stage's own images: the push field, the ground copy and each shadow map it declares.</summary>
    private const int CastOwnMax = 16;

    /// <summary>A cast program's own images into <paramref name="own"/>: the push field, and a stand-in for the ground
    /// copy and for every shadow map it declares -- the arrays the bake is writing may not be bound for reading.</summary>
    private static int VkGrassCastOwn(VkProg p, VkGrassState g, VkColourState c, Span<ImageBinding> own)
    {
        int n = 0;
        own[n++] = new ImageBinding(TextureSlots.GrassPush, g.Push, g.Clamp);
        own[n++] = new ImageBinding(TextureSlots.GrassGround, c.Tex2D, g.Clamp);
        foreach (VkOpaque o in p.Opaques)
            if (o.Set == 0 && o.Kind == VkBindKind.Sampled && !VkShadowMap(o.Name).IsNull && n < own.Length)
                own[n++] = VkStandInOf(in o, c);
        return n;
    }

    /// <summary>This frame's blades and cards into the velocity field <paramref name="vel"/> after the camera's and the
    /// movers', against the scene depth: each surviving fragment paints its own sway's vector, so TAA does not smear it.</summary>
    internal static void VkGrassMotion(VkBackend vk, CommandList cl, ImageId vel, int w, int h, Matrix4x4* vp, in Matrix4x4 prevVp)
    {
        VkGrassState g = _vkGrass;
        if (g.Dev != vk || _capturing) return;
        GrassField.VkFrame f = GrassField.VkLast(out bool ground);
        bool blades = f.BladeNear + f.BladeFar > 0 && !g.BladeMotionPipe.IsNull, cards = f.Slots > 0 && !g.CardMotionPipe.IsNull;
        if (f.Patches == 0 || !(blades || cards)) return;
        VkColourState c = _vkc;
        Matrix4x4 prev = prevVp;
        int* viewport = stackalloc int[4] { 0, 0, w, h };
        var d = new VkGrassView(GrassDraw.Motion, vp, &prev, viewport, 0f, 0f, false, ground, false);
        ReadOnlySpan<ImageBinding> own = [new(TextureSlots.GrassPush, g.Push, g.Clamp), new(TextureSlots.GrassGround, c.Tex2D, g.Clamp)];
        ReadOnlySpan<BufferBinding> storage = [new(SsboSlots.GrassMats, g.Mats), new(SsboSlots.GrassInst, g.Out)];
        CommandList side = g.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, w, h);
        side.SetRasterState(new RasterState(CullMode.None));
        if (blades)
        {
            side.SetPipeline(g.BladeMotionPipe);
            side.SetDepthState(new DepthState(true, false, CompareOp.LessEqual));
            if (!VkGrassDraws(side, g, c, g.BladeMotion!, g.LBM, in f, in d, false, own, storage)) return;
        }
        if (cards)
        {
            side.SetPipeline(g.CardMotionPipe);
            side.SetDepthState(new DepthState(true, false, CompareOp.LessEqual));
            if (!VkGrassDraws(side, g, c, g.CardMotion!, g.LCM, in f, in d, true, own, storage)) return;
        }
        ReadOnlySpan<ColorAttachment> colors = [new(vel, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, 1f)));
        if (!g.MotionLive)
        {
            g.MotionLive = true;
            _log?.Always($"AlloyRenderer: the Vulkan grass velocity is LIVE -- {f.BladeNear + f.BladeFar} blade record(s) and "
                       + $"{f.Slots} card slot(s) over the camera field");
        }
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

    /// <summary>The scatter, the stages, their pipelines and the class's buffers, made once per device. Without the
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
        VkGrassLateEnsure(vk, g, log);
        return true;
    }

    /// <summary>The depth and velocity programs and their pipelines: the cards' prepass with the blended-fringe colour
    /// pipeline beside it, both paths' cascade casters, both paths' velocity arms. Each refusal is said once and stands
    /// only its own draw down -- the prepass's leaves the cards writing their own depth.</summary>
    private static void VkGrassLateEnsure(VkBackend vk, VkGrassState g, ExtenderLog log)
    {
        g.CardDepth = VkGrassProgram(vk, VkPrograms.Id.GrassDepth, g.LCD, log);
        g.CardMotion = VkGrassProgram(vk, VkPrograms.Id.GrassMotion, g.LCM, log);
        if (g.Blades != null)
        {
            g.BladeDepth = VkGrassProgram(vk, VkPrograms.Id.GrassBladeDepth, g.LBD, log);
            g.BladeMotion = VkGrassProgram(vk, VkPrograms.Id.GrassBladeMotion, g.LBM, log);
        }
        if (g.CardDepth != null)
        {
            var fringe = new AttachmentBlend(true, BlendFactor.SrcAlpha, BlendFactor.OneMinusSrcAlpha, BlendFactor.SrcAlpha,
                                             BlendFactor.OneMinusSrcAlpha);
            g.CardPrePipe = VkGrassPipe(vk, g.CardDepth, Topology.Triangles, "vk grass card prepass", Format.Rgba16F,
                                        new AttachmentBlend(Masked: ColorMask.All));
            g.CardSoftPipe = VkGrassPipe(vk, g.Cards!, Topology.Triangles, "vk grass cards blended fringe", Format.Rgba16F, fringe);
            g.CardCastPipe = VkGrassPipe(vk, g.CardDepth, Topology.Triangles, "vk grass card cascades", Format.Unknown, clamp: true);
        }
        g.Prepass = !g.CardPrePipe.IsNull && !g.CardSoftPipe.IsNull;
        if (g.BladeDepth != null)
            g.BladeCastPipe = VkGrassPipe(vk, g.BladeDepth, Topology.TriangleStrip, "vk grass blade cascades", Format.Unknown, clamp: true);
        if (g.CardMotion != null)
            g.CardMotionPipe = VkGrassPipe(vk, g.CardMotion, Topology.Triangles, "vk grass card velocity", Format.Rg16F);
        if (g.BladeMotion != null)
            g.BladeMotionPipe = VkGrassPipe(vk, g.BladeMotion, Topology.TriangleStrip, "vk grass blade velocity", Format.Rg16F);
        if (!g.Prepass)
            log.Always("AlloyRenderer: the Vulkan grass card prepass was REFUSED (logged above) -- the cards write their own depth, "
                     + "with no blended fringe");
        if (g.CardCastPipe.IsNull || g.Blades != null && g.BladeCastPipe.IsNull)
            log.Always("AlloyRenderer: a Vulkan grass cascade caster was REFUSED (logged above) -- that path casts no sun shadow");
        if (g.CardMotionPipe.IsNull || g.Blades != null && g.BladeMotionPipe.IsNull)
            log.Always("AlloyRenderer: a Vulkan grass velocity program was REFUSED (logged above) -- TAA takes the camera's vectors "
                     + "over that path");
    }

    /// <summary>Program <paramref name="id"/> and its locations, or null (logged by the build).</summary>
    private static VkProg? VkGrassProgram(VkBackend vk, VkPrograms.Id id, GrassField.VkLocs v, ExtenderLog log)
    {
        VkProg? p = VkBuild(vk, id, log);
        if (p == null) return null;
        LooseBlock.Resolve(p.Names, () => { v.L = GrassField.Locate(); v.F = Programs.FeatLocs(); });
        return p;
    }

    /// <summary>A grass pipeline into one <paramref name="colour"/> target (<c>Unknown</c> for none) and the depth: no
    /// vertex input, both faces, <paramref name="blend"/>, and the depth clamp a sun cascade's view needs when
    /// <paramref name="clamp"/>. Depth is set per draw.</summary>
    private static PipelineId VkGrassPipe(VkBackend vk, VkProg p, Topology topo, string label, Format colour = Format.Rgba16F,
                                          AttachmentBlend blend = default, bool clamp = false)
    {
        var d = new PipelineDesc
        {
            Vs = p.Vs, Fs = p.Fs, Topo = topo, Raster = new RasterState(CullMode.None, DepthClamp: clamp),
            Depth = new DepthState(true, true, CompareOp.LessEqual), Label = label,
        };
        d.Targets.ColorCount = colour == Format.Unknown ? 0 : 1;
        d.Targets.Colors[0] = colour;
        d.Blend[0] = blend;
        d.Targets.Depth = Format.D32F;
        d.Targets.Samples = 1;
        return vk.CreatePipeline(in d);
    }
}
