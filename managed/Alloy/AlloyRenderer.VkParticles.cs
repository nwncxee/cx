using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The emitter pass (<see cref="PassId.ParticlesOwned"/>) on Vulkan: <see cref="Particles"/>' own records and
/// draw-order list, drawn through the same stages GL compiles, six vertices a particle expanded from the vertex
/// index, over the scene colour after the water and additive classes.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The pass's resources on one device, replaced whole with the device.</summary>
    private sealed class VkPartState
    {
        public VkBackend? Dev;
        public VkProg? Prog;
        public bool Tried, Live;
        public BufferId Recs, Idx;
        public long RecCap, IdxCap;
        /// <summary>A pipeline per class: punch-through writes depth, blended and additive blend over.</summary>
        public readonly PipelineId[] Pipes = new PipelineId[3];
        public SamplerId Samp;
        public CommandList? Side;
        public int View = -1, Proj = -1, Eye = -1, Cut = -1, Add = -1, Lit = -1, Emis = -1, Viz = -1, Soft = -1;
        public int SunMedia = -1, LocalMedia = -1, Cubes = -1;
        /// <summary>The velocity arm: the same stages with the motion define, re-issuing this frame's groups.</summary>
        public VkProg? Motion;
        public PipelineId MotionPipe;
        public CommandList? MotionSide;
        public bool MotionTried, MotionLive;
        public int MView = -1, MProj = -1, MEye = -1, MAdd = -1, MPrevVp = -1, MJitter = -1, MSize = -1;
        /// <summary>The frame the pass last drew in, its group count, and the camera it drew with.</summary>
        public int DrewSeq = -1, DrewGroups;
        public Matrix4x4 DrewView, DrewProj;
        public Vector3 DrewEye;
        public long NoTex;
        /// <summary>The first texture names that drew on the stand-in, each said once.</summary>
        public readonly uint[] NoTexSaid = new uint[8];
        public int NoTexSaidN;
    }

    private static VkPartState _vkPart = new();

    /// <summary>Room for the pass's binds and draws: a bind and a draw a group.</summary>
    private const int PartArenaBytes = 1 << 16;

    /// <summary>Class <paramref name="cls"/>'s depth: every class tests the scene's target and only punch-through
    /// writes it. The pipeline, the per-group state and the flush all read it here.</summary>
    private static DepthState VkPartDepth(int cls) => new(true, cls == Particles.VkPunch, WorldDepth.NearerOrEqual);

    /// <summary>The emitter pass into <paramref name="cl"/>. Nothing when no emitter placed a particle; false only when
    /// the device refused an upload (the frame then refuses, as every other Vulkan pass does).</summary>
    private static bool VkParticlePass(VkBackend vk, CommandList cl, VkColourState c, in Matrix4x4 view, in Matrix4x4 proj,
                                       int* viewport, float near, float far, bool lightingOn, ExtenderLog log)
    {
        VkPartState s = _vkPart;
        if (s.Dev != vk) _vkPart = s = new VkPartState { Dev = vk };
        if (_capturing || !Particles.VkOn || !VkPartEnsure(vk, s, log)) return true;
        Matrix4x4 v = view, p = proj;
        int at = Particles.StageVk(&v, lightingOn, out Vector3 eye, out bool lit, out int groups);
        if (at == 0) return true;
        ReadOnlySpan<byte> recs = MemoryMarshal.AsBytes(Particles.VkRecords);
        ReadOnlySpan<byte> idx = MemoryMarshal.AsBytes(Particles.VkIndices(at));
        if (!VkGrowBuf(vk, ref s.Recs, ref s.RecCap, recs.Length, "vk particle records")
            || !VkGrowBuf(vk, ref s.Idx, ref s.IdxCap, idx.Length, "vk particle order")) return false;
        cl.UpdateBuffer(s.Recs, 0, recs);
        cl.UpdateBuffer(s.Idx, 0, idx);
        // the soft fade reads the depth as everything before this pass left it, the blended and water classes included
        bool soft = Particles.SoftOn && !c.DepthRead.IsNull;
        if (soft)
        {
            VkCopyDepth(cl, c);
            ReadOnlySpan<ImageId> read = [c.DepthRead];
            cl.ReadImages(read);
        }
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, viewport[2], viewport[3]);
        int cls = -1;
        bool punched = false;
        bool? cull = null;
        for (int gi = 0; gi < groups; gi++)
        {
            Particles.VkGroup g = Particles.VkGroupAt(gi);
            if (g.Parts == 0) continue;
            PipelineId pipe = s.Pipes[g.Cls];
            if (pipe.IsNull) continue;
            if (g.Cls != cls)
            {
                cls = g.Cls;
                side.SetPipeline(pipe);
                side.SetDepthState(VkPartDepth(cls));
            }
            if (cull != g.Cull)
            {
                cull = g.Cull;
                side.SetRasterState(new RasterState(g.Cull ? CullMode.Back : CullMode.None));
            }
            bool groupLit = lit && cls == Particles.VkBlend;
            int loose = VkPartBlock(s, &v, &p, eye, g, groupLit, soft, viewport, near, far);
            int pass = groupLit ? VkArenaPass() : -1;
            if (loose < 0 || (groupLit && pass < 0)) return false;
            VkPartBind(side, s, s.Prog!, c, loose, pass, g.Tex, soft);
            side.Draw(g.Parts * 6, 1, g.Start * 6, 0);
            punched |= VkPartDepth(cls).Write;
        }
        ReadOnlySpan<ColorAttachment> colors = [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default), VkReactiveAttach(c)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear)),
                    writesDepth: punched);
        s.DrewSeq = FrameSeq; s.DrewGroups = groups; s.DrewView = v; s.DrewProj = p; s.DrewEye = eye;
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan particle pass is LIVE -- {groups} group(s), {at} particle(s), blended groups "
                     + "sorted back to front on the CPU" + (soft ? ", soft against the scene depth" : ""));
        }
        return true;
    }

    /// <summary>One group's loose block, as GL's pass sets its terms: the camera, the fog, the class's cut and blend
    /// switch, the emissive gain and, for a lit group, the pass block's receivers with the media offsets.</summary>
    private static int VkPartBlock(VkPartState s, Matrix4x4* view, Matrix4x4* proj, Vector3 eye, in Particles.VkGroup g,
                                   bool lit, bool soft, int* viewport, float near, float far)
    {
        VkProg p = s.Prog!;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            LooseBlock.Set(s.View, *view);
            LooseBlock.Set(s.Proj, *proj);
            LooseBlock.Set(s.Eye, [eye.X, eye.Y, eye.Z]);
            UploadFog(p.F);
            LooseBlock.Set(s.Cut, g.Cls == Particles.VkPunch ? Particles.VkPunchCut : Particles.VkNoCut);
            LooseBlock.Set(s.Add, g.Cls == Particles.VkAdd ? 1 : 0);
            LooseBlock.Set(s.Lit, lit ? 1 : 0);
            LooseBlock.Set(s.Emis, g.Emissive);
            LooseBlock.Set(s.Viz, Particles.VizMode);
            LooseBlock.Set(s.Soft, soft ? 1 : 0);
            if (lit)
            {
                BindPass(s.Cubes, viewport, near, far);
                if (!Particles.ShadowsOn) LooseBlock.Set(s.Cubes, 0);
                LooseBlock.Set(s.SunMedia, 1f);
                LooseBlock.Set(s.LocalMedia, 1f);
            }
        }
        finally { LooseBlock.EndCapture(); }
        return VkArena(p.Block);
    }

    /// <summary>Say, once per texture and for the first few, that a group's texture has no Vulkan copy: its sprites
    /// draw on the white stand-in, as flat quads.</summary>
    private static void VkPartNoTex(VkPartState s, uint tex)
    {
        if (s.NoTexSaidN >= s.NoTexSaid.Length || Array.IndexOf(s.NoTexSaid, tex, 0, s.NoTexSaidN) >= 0) return;
        s.NoTexSaid[s.NoTexSaidN++] = tex;
        _log?.Always($"AlloyRenderer: particle texture {tex} has NO Vulkan copy -- its sprites draw on the white stand-in, as flat "
                   + $"untextured quads ({s.NoTex} such bind(s) so far)");
    }

    /// <summary>The pass's bind set: the loose and pass blocks, the record and order buffers, the group's texture (the
    /// white stand-in, counted, for one Vulkan has no copy of), the depth copy, and the stand-ins for the rest.</summary>
    private static void VkPartBind(CommandList side, VkPartState s, VkProg p, VkColourState c, int loose, int pass, uint tex, bool soft)
    {
        Span<BufferBinding> ubo = stackalloc BufferBinding[12];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[16];
        Span<ImageBinding> sampled = stackalloc ImageBinding[48];
        int nu = 0, ns = 0, nt = 0;
        foreach (VkOpaque o in p.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock:
                    ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, loose, p.Block.Length)
                              : o.Binding == UboSlots.Pass && pass >= 0 ? new BufferBinding(o.Binding, c.Arena, pass, PassBytes)
                              : new BufferBinding(o.Binding, c.ZeroUbo);
                    break;
                case VkBindKind.StorageBlock:
                    ssbo[ns++] = new BufferBinding(o.Binding, VkPartStorage(o.Binding, _capturing, s.Recs, s.Idx, VkDeferredLights(),
                                                                            VkClusterCounts, VkClusterIdx, VkMovers) ?? c.ZeroSsbo);
                    break;
                case VkBindKind.Sampled when o.Name == "uTex":
                    ImageId img = tex != 0 ? VkTextures.Image((int)tex) : default;
                    SamplerId own = img.IsNull ? default : VkTextures.SamplerId((int)tex);
                    if (img.IsNull) { s.NoTex++; VkPartNoTex(s, tex); img = VkTextures.Image(OwnedTextures.WhiteTid()); }
                    sampled[nt++] = new ImageBinding(o.Binding, img.IsNull ? c.Tex2D : img, VkPartSampler(Particles.OwnedSampler, s.Samp, own));
                    break;
                case VkBindKind.Sampled when o.Name == "uSceneDepth" && soft:
                    sampled[nt++] = new ImageBinding(o.Binding, c.DepthRead);
                    break;
                case VkBindKind.Sampled:
                    sampled[nt++] = VkSampled(o, c, 0);
                    break;
            }
        }
        VkBindTally.Count(VkBindBy.Effects);
        side.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], default));
    }

    /// <summary>The particle stages as the colour pass draws them, translated for Vulkan.</summary>
    internal static VkProgramText VkPartText()
        => WorldDepth.Translate(ShaderResource.Tokens(ShaderSource.Get("cx_part_v.glsl")),
                                ShaderResource.Tokens(ShaderSource.Get("cx_part_f.glsl")), VkPrograms.Units);

    /// <summary>The buffer the particle stages read at storage <paramref name="binding"/>: the records, the draw order, and
    /// the frame's light table and froxel lists the lit vertex stage walks; null leaves the stand-in.</summary>
    internal static BufferId? VkPartStorage(uint binding, bool capturing, BufferId recs, BufferId idx, BufferId lights,
                                            BufferId counts, BufferId clIdx, BufferId movers)
        => binding switch
        {
            Particles.BindRecs => recs,
            Particles.BindIdx => idx,
            _ => VkLitTable(binding, capturing, lights, counts, clIdx, movers),
        };

    /// <summary>The sampler a group's texture is read through: its own (its levels and its wrap), or the pass's
    /// un-mipmapped clamp one under <c>particles.sampler</c> or for a texture that has none (the stand-in).</summary>
    internal static SamplerId VkPartSampler(bool owned, SamplerId pass, SamplerId texture)
        => owned || texture.IsNull ? pass : texture;

    /// <summary>The program, its three pipelines and the un-mipmapped clamp sampler, made once per device. A refusal is
    /// said once and the pass sits the session out (GL's own refusal stands it down the same way).</summary>
    private static bool VkPartEnsure(VkBackend vk, VkPartState s, ExtenderLog log)
    {
        if (s.Tried) return s.Prog != null;
        s.Tried = true;
        s.Side = new CommandList("vulkan particles", PartArenaBytes, log);
        s.Samp = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.None, Wrap.Clamp));
        VkProg? p = VkBuild(vk, VkPartText(), "particles", false, log);
        if (p != null)
            LooseBlock.Resolve(p.Names, () =>
            {
                s.View = LooseBlock.Loc("uView"); s.Proj = LooseBlock.Loc("uProj"); s.Eye = LooseBlock.Loc("uEye");
                s.Cut = LooseBlock.Loc("uAlphaCut"); s.Add = LooseBlock.Loc("uAdditive"); s.Lit = LooseBlock.Loc("uLit");
                s.Emis = LooseBlock.Loc("uEmissive"); s.Viz = LooseBlock.Loc("uViz"); s.Soft = LooseBlock.Loc("uSoftOn");
                s.SunMedia = LooseBlock.Loc("uSunMedia"); s.LocalMedia = LooseBlock.Loc("uLocalMedia");
                s.Cubes = LooseBlock.Loc("uLocalCubes");
            });
        for (int cls = 0; p != null && cls < s.Pipes.Length; cls++)
        {
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Topo = Topology.Triangles, Raster = new RasterState(CullMode.None),
                Depth = VkPartDepth(cls), Label = $"{PipeFeatures.Particles}class {cls}",
            };
            d.Targets.ColorCount = 2;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Targets.Colors[1] = Format.R8;
            d.Targets.Depth = Format.D32F;
            d.Targets.Samples = 1;
            if (cls != Particles.VkPunch)
            {
                BlendFactor dst = cls == Particles.VkAdd ? BlendFactor.One : BlendFactor.OneMinusSrcAlpha;
                d.Blend[0] = new AttachmentBlend(true, BlendFactor.SrcAlpha, dst, BlendFactor.SrcAlpha, dst);
            }
            d.Blend[1] = ReactiveBlend(p);
            s.Pipes[cls] = vk.CreatePipeline(in d);
            if (s.Pipes[cls].IsNull) p = null;
        }
        s.Prog = p;
        if (p == null)
            log.Always("AlloyRenderer: the Vulkan particle program or a pipeline was REFUSED (logged above) -- no particles are "
                     + "drawn on Vulkan this session");
        return p != null;
    }

    /// <summary>A storage buffer of at least <paramref name="bytes"/>, grown by doubling.</summary>
    private static bool VkGrowBuf(VkBackend vk, ref BufferId id, ref long cap, long bytes, string label)
        => VkGrow(vk, ref id, ref cap, Math.Max(bytes, 64), label);
}
