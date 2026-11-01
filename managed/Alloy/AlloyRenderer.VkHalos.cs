using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The light halos (<see cref="PassId.HalosOwned"/>) on Vulkan: <see cref="Halos"/>' staged flare elements, one
/// instanced quad each, added over the scene colour with no depth test, each occluded per pixel against a depth copy
/// as GL's stage does. Each light's chain is faded by the occlusion probe, a compute over the same copy that walks the
/// light's slot in a GPU fade buffer toward what it can see; the draw reads the slot the same frame.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The pass's program, pipeline and buffer on one device.</summary>
    private sealed class VkHaloState
    {
        public VkBackend? Dev;
        public VkProg? Prog;
        public PipelineId Pipe;
        public bool Tried, Live;
        public BufferId Recs;
        public long RecCap;
        public SamplerId Samp;
        public CommandList? Side;
        public PipelineId Occ;
        public BufferId Fade;
        public long FadeCap;
        public int View = -1, Proj = -1, Viz = -1, Gain = -1, First = -1, Textured = -1;
    }

    private static VkHaloState _vkHalo = new();

    /// <summary>The probe's workgroup: <c>local_size_x</c> in <c>cx_haloocc_c</c>.</summary>
    private const int VkHaloOccGroup = 64;
    /// <summary>The fade buffer's first size, in slots. ponytail: growing it makes a new, zeroed buffer, so every flare
    /// fades in again once; copy the old slots across if an area ever outgrows this.</summary>
    private const int VkHaloFadeSlots = 1024;

    /// <summary>The halos into <paramref name="cl"/> as their own pass; false only when the device refused an upload or
    /// the arena is full.</summary>
    private static bool VkHaloPass(VkBackend vk, CommandList cl, VkColourState c, in Matrix4x4 view, in Matrix4x4 proj,
                                   int* viewport, ExtenderLog log)
    {
        VkHaloState s = _vkHalo;
        if (s.Dev != vk) _vkHalo = s = new VkHaloState { Dev = vk };
        ReadOnlySpan<HaloRec> recs = Halos.VkRecords;
        int n = recs.Length;
        if (_capturing || n == 0 || !Effects.HalosOn || c.DepthRead.IsNull || !VkHaloEnsure(vk, s, log)) return true;
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(recs);
        if (!VkGrow(vk, ref s.Recs, ref s.RecCap, Math.Max(bytes.Length, 64), "vk halos")) return false;
        cl.UpdateBuffer(s.Recs, 0, bytes);
        cl.CopyImage(new CopyImageCmd(c.Depth, new ImageRegion(0, 0, 0, 0), c.DepthRead, new ImageRegion(0, 0, 0, 0), c.W, c.H, 1));
        ReadOnlySpan<ImageId> read = [c.DepthRead];
        cl.ReadImages(read);
        if (!VkHaloOcclude(vk, cl, s, c, view, proj, n)) return false;
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, viewport[2], viewport[3]);
        side.SetPipeline(s.Pipe);
        VkProg p = s.Prog!;
        int groups = 0, bare = 0;
        for (int at = 0; at < n;)
        {
            uint tex = Halos.VkTex(at);
            int run = 1;
            while (at + run < n && Halos.VkTex(at + run) == tex) run++;
            ImageId img = tex != 0 ? VkTextures.Image((int)tex) : default;
            Array.Clear(p.Block);
            LooseBlock.Capture(p.Members, p.Block);
            LooseBlock.Set(s.View, view);
            LooseBlock.Set(s.Proj, proj);
            LooseBlock.Set(s.Viz, Effects.HaloViz.V);
            LooseBlock.Set(s.Gain, Effects.Halos.V);
            LooseBlock.Set(s.First, at);
            LooseBlock.Set(s.Textured, img.IsNull ? 0 : 1);
            LooseBlock.EndCapture();
            int loose = VkArena(p.Block);
            if (loose < 0) return false;
            Span<ImageBinding> sampled = stackalloc ImageBinding[8];
            Span<BufferBinding> ubo = stackalloc BufferBinding[4];
            Span<BufferBinding> ssbo = stackalloc BufferBinding[4];
            int nt = 0, nu = 0, ns = 0;
            foreach (VkOpaque o in p.Opaques)
            {
                if (o.Set != 0) continue;
                if (o.Kind == VkBindKind.UniformBlock && nu < ubo.Length)
                    ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, loose, p.Block.Length) : new BufferBinding(o.Binding, c.ZeroUbo);
                else if (o.Kind == VkBindKind.StorageBlock && ns < ssbo.Length)
                    ssbo[ns++] = new BufferBinding(o.Binding, o.Binding == Halos.VkBind ? s.Recs
                                                            : o.Binding == SsboSlots.HaloFade ? s.Fade : c.ZeroSsbo);
                else if (o.Kind == VkBindKind.Sampled && nt < sampled.Length)
                    sampled[nt++] = o.Name == "uSceneDepth" ? new ImageBinding(o.Binding, c.DepthRead)
                                  : o.Name == "uFlare" && !img.IsNull ? new ImageBinding(o.Binding, img, s.Samp)
                                  : VkSampled(o, c, 0);
            }
            side.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], default));
            side.Draw(4, run, 0, 0);
            if (img.IsNull) bare += run;
            groups++;
            at += run;
        }
        ReadOnlySpan<ColorAttachment> colors = [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors));
        Halos.VkDrawn(n, groups, bare);
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan halos are LIVE -- {n} flare element(s) in {groups} texture group(s), "
                     + $"occluded per pixel and faded by the occlusion probe over {Halos.FadeSlots} light slot(s)");
        }
        return true;
    }

    /// <summary>The occlusion probe over this frame's <paramref name="n"/> elements, ahead of the draw that reads its
    /// fades; false only when the device refused the fade buffer.</summary>
    private static bool VkHaloOcclude(VkBackend vk, CommandList cl, VkHaloState s, VkColourState c, in Matrix4x4 view,
                                      in Matrix4x4 proj, int n)
    {
        long bytes = (long)Math.Max(Halos.FadeSlots, VkHaloFadeSlots) * sizeof(float);
        if (s.Fade.IsNull || bytes > s.FadeCap)
        {
            vk.Destroy(s.Fade);
            s.FadeCap = Math.Max(bytes, s.FadeCap * 2);
            s.Fade = vk.CreateBuffer(new BufferDesc(s.FadeCap, BufferUse.Storage, MemoryKind.Device, "vk halo fades"));
            if (s.Fade.IsNull) { s.FadeCap = 0; return false; }
        }
        if (Halos.TakeFadeReset()) cl.ZeroBuffer(s.Fade);
        Halos.OccConsts push = Halos.OccPush(view, proj, n, Halos.FadeStep(FrameDelta.Delta(FrameDelta.DtFloor)));
        ReadOnlySpan<BufferBinding> ssbo = [new(SsboSlots.Halos, s.Recs), new(SsboSlots.HaloFade, s.Fade)];
        cl.SetPipeline(s.Occ);
        cl.BindSet(0, new BindSet(default, ssbo, [new ImageBinding(0, c.DepthRead)], default));
        cl.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<Halos.OccConsts>(in push)));
        cl.Dispatch((n + VkHaloOccGroup - 1) / VkHaloOccGroup);
        return true;
    }

    /// <summary>The program, its additive, depth-less pipeline and the occlusion probe, made once per device; a refusal
    /// of either sits the halos out.</summary>
    private static bool VkHaloEnsure(VkBackend vk, VkHaloState s, ExtenderLog log)
    {
        if (s.Tried) return s.Prog != null;
        s.Tried = true;
        s.Side = new CommandList("vulkan halos", 1 << 14, log);
        s.Samp = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Linear, Wrap.Clamp));
        (string vs, string fs) = Halos.VkTexts(log);
        var units = new System.Collections.Generic.Dictionary<string, uint>(VkPrograms.Units) { ["uFlare"] = TextureSlots.Diffuse };
        units.Remove("uTex");
        VkProgramText t = WorldDepth.Translate(ShaderResource.Tokens(vs), ShaderResource.Tokens(fs), units);
        VkProg? p = VkBuild(vk, t, "halos", false, log);
        if (p != null)
        {
            LooseBlock.Resolve(p.Names, () =>
            {
                s.View = LooseBlock.Loc("uView"); s.Proj = LooseBlock.Loc("uProj"); s.Viz = LooseBlock.Loc("uViz");
                s.Gain = LooseBlock.Loc("uGain"); s.First = LooseBlock.Loc("uFirst"); s.Textured = LooseBlock.Loc("uTextured");
            });
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Topo = Topology.TriangleStrip, Raster = new RasterState(CullMode.None),
                Depth = new DepthState(false, false, CompareOp.Always), Label = "vk halos",
            };
            d.Targets.ColorCount = 1;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Targets.Samples = 1;
            d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.One, BlendFactor.One, BlendFactor.One);
            s.Pipe = vk.CreatePipeline(in d);
            if (s.Pipe.IsNull) p = null;
        }
        if (p != null)
        {
            s.Occ = vk.CreateCompute(ShaderSource.Get(Halos.OccCs + ".glsl"), "vk halo occlusion");
            if (s.Occ.IsNull) p = null;
        }
        s.Prog = p;
        if (p == null) log.Always("AlloyRenderer: the Vulkan halo program or its occlusion probe was REFUSED (logged above) -- no halos on Vulkan this session");
        return p != null;
    }
}
