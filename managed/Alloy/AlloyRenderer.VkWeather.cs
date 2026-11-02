using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Falling rain and snow (<see cref="PassId.WeatherOwned"/>) on Vulkan: a compute cull places, tests and lights every
/// drop of <see cref="Weather"/>'s lattice once, keeps those on screen and under open sky, and the streaks and flakes
/// draw indirect from its records, premultiplied over the scene colour, faded against a depth copy. The roof drips are
/// rain's program again, kept only under a lip.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The rain, snow and splash programs on one device.</summary>
    private sealed class VkWxState
    {
        public VkBackend? Dev;
        public readonly VkProg?[] Prog = new VkProg?[3];
        public readonly PipelineId[] Pipe = new PipelineId[3];
        public readonly Weather.Loc[] L = new Weather.Loc[2];
        public Weather.SpLoc Sp;
        public readonly bool[] Tried = new bool[3];
        public CommandList? Side;
        public bool Live;
        public VkDefStage? Cull;
        public Weather.Loc CullL;
        public bool CullTried;
        /// <summary>The cull's survivors, three vec4 each, and its two draw commands: the fall, then the drips.</summary>
        public BufferId Drops, Cmd;
        /// <summary>This frame's cull kept a fall of <see cref="Kind"/> and drips, so the velocity can draw them again.</summary>
        public bool Fell, Dripped;
        public int Kind;
        /// <summary>The phase the last cull saw, and whether a cull ran then: a frame after none takes no fall step.</summary>
        public double LastPhase;
        public bool CulledLast;
        public VkProg? Motion;
        public PipelineId MotionPipe;
        public Weather.Loc ML;
        public int MVp, MPrevVp, MJitter, MSize;
        public bool MotionTried, MotionLive;
        public CommandList? MotionSide;
    }

    /// <summary>The bytes of one cull survivor's record.</summary>
    private const int VkWxRecordBytes = 3 * 16;

    /// <summary>The most survivors one command keeps: a ceiling on the drops drawn, not on the field. ponytail: a frame
    /// that would keep more draws an arbitrary subset; raise it, or cap by distance, if a real area reaches it.</summary>
    private const int VkWxRecCap = 1 << 17;

    /// <summary>The first record the roof drips take: past the most the fall can keep.</summary>
    private const int VkWxDripBase = VkWxRecCap;

    private static VkWxState _vkWx = new();

    /// <summary>The precipitation into <paramref name="cl"/> as its own pass; false only when the arena is full.</summary>
    private static bool VkWeatherPass(VkBackend vk, CommandList cl, VkColourState c, in Matrix4x4 view, in Matrix4x4 proj,
                                      int* viewport, float near, float far, bool lightingOn, ExtenderLog log)
    {
        VkWxState s = _vkWx;
        if (s.Dev != vk) _vkWx = s = new VkWxState { Dev = vk };
        s.Fell = s.Dripped = false;
        if (_capturing || c.DepthRead.IsNull) { s.CulledLast = false; return true; }
        int count = Weather.StageVk(AreaWind(), out bool snow, out int drips);
        int k = snow ? 1 : 0;
        if (count < 0) { s.CulledLast = false; return true; }
        bool culls = (count > 0 || drips > 0) && VkWxCullEnsure(vk, s, log);
        bool fall = culls && count > 0 && VkWxEnsure(vk, s, k, log), drip = culls && drips > 0 && VkWxEnsure(vk, s, 0, log);
        bool lit = lightingOn && AlloyLights.Ready;
        float step = VkWxFallStep(s, fall || drip);
        if ((fall || drip) && !VkWxCull(cl, s, c, snow ? 2 : 1, fall ? count : 0, drip ? drips : 0, step, view, proj, viewport, near, far, lit))
            return false;
        s.Fell = fall; s.Dripped = drip; s.Kind = snow ? 2 : 1;
        cl.CopyImage(new CopyImageCmd(c.Depth, new ImageRegion(0, 0, 0, 0), c.DepthRead, new ImageRegion(0, 0, 0, 0), c.W, c.H, 1));
        ReadOnlySpan<ImageId> read = [c.DepthRead];
        cl.ReadImages(read);
        CommandList side = s.Side!;
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, viewport[2], viewport[3]);
        if (fall && !VkWxDraw(side, s, c, k, snow ? 2 : 1, 0, view, proj, viewport, near, far, lit)) return false;
        if (drip && !VkWxDraw(side, s, c, 0, 3, 1, view, proj, viewport, near, far, lit)) return false;
        if (VkWxEnsure(vk, s, VkSplashProg, log) && !VkSplashDraw(side, s, c, view, proj, viewport, near, far, lit)) return false;
        ReadOnlySpan<ColorAttachment> colors = [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear)));
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan {(snow ? "snow" : "rain")} is LIVE -- {count} lattice point(s) and {drips} drip(s) "
                     + "culled and lit once each in compute, stopped under the cover map, drawn indirect");
        }
        return true;
    }

    /// <summary>The draw of <paramref name="kind"/> (1 rain, 2 snow, 3 drips) through program <paramref name="k"/>, its own
    /// loose block, indirect from the cull's command <paramref name="slot"/>; false only when the arena is full.</summary>
    private static bool VkWxDraw(CommandList side, VkWxState s, VkColourState c, int k, int kind, int slot, in Matrix4x4 view,
                                 in Matrix4x4 proj, int* viewport, float near, float far, bool lit)
    {
        VkProg p = s.Prog[k]!;
        side.SetPipeline(s.Pipe[k]);
        Matrix4x4 v = view, pr = proj;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, viewport, near, far);
            Weather.WriteVk(in s.L[k], kind, &v, &pr, _eyePos, lit);
        }
        finally { LooseBlock.EndCapture(); }
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        ReadOnlySpan<BufferBinding> drops = [new(SsboSlots.WxDrops, s.Drops)];
        VkBindSet(side, p, c, loose, pass, c.Slots, 0, default, drops);
        side.DrawIndirect(s.Cmd, slot * 4 * sizeof(uint), 1);
        c.Effects++;
        return true;
    }

    /// <summary>The cull into <paramref name="cl"/>: both commands reset, then one dispatch for the fall's
    /// <paramref name="count"/> lattice points and one for the <paramref name="drips"/>; false only when the arena is full.</summary>
    private static bool VkWxCull(CommandList cl, VkWxState s, VkColourState c, int kind, int count, int drips, float step, in Matrix4x4 view,
                                 in Matrix4x4 proj, int* viewport, float near, float far, bool lit)
    {
        ReadOnlySpan<uint> cmd = [4, 0, 0, 0, 4, 0, 0, VkWxDripBase];
        cl.UpdateBuffer(s.Cmd, 0, MemoryMarshal.AsBytes(cmd));
        int arena0 = c.ArenaUsed;
        int fall = count > 0 ? VkWxCullBlock(s, kind, count, 0, step, view, proj, viewport, near, far, lit) : 0;
        int fallPass = count > 0 ? VkArenaPass() : 0;
        int drip = drips > 0 ? VkWxCullBlock(s, 3, drips, 1, step, view, proj, viewport, near, far, lit) : 0;
        int dripPass = drips > 0 ? VkArenaPass() : 0;
        if (fall < 0 || fallPass < 0 || drip < 0 || dripPass < 0) return false;
        int len = (c.ArenaUsed - arena0 + 3) & ~3;
        cl.UpdateBuffer(c.Arena, arena0, c.ArenaBytes.AsSpan(arena0, Math.Min(len, ArenaCap - arena0)));
        cl.SetPipeline(s.Cull!.Pipe);
        if (count > 0) { VkWxCullBind(cl, s, c, fall, fallPass); cl.Dispatch((count + 63) / 64); }
        if (drips > 0) { VkWxCullBind(cl, s, c, drip, dripPass); cl.Dispatch((drips + 63) / 64); }
        return true;
    }

    /// <summary>One cull dispatch's loose block, the pass block staged inside it: <paramref name="kind"/>'s terms, the
    /// <paramref name="count"/> it walks and the command <paramref name="slot"/> its survivors count into.</summary>
    private static int VkWxCullBlock(VkWxState s, int kind, int count, int slot, float step, in Matrix4x4 view, in Matrix4x4 proj,
                                     int* viewport, float near, float far, bool lit)
    {
        VkDefStage st = s.Cull!;
        Matrix4x4 v = view, pr = proj;
        Array.Clear(st.Block);
        LooseBlock.Capture(st.Members, st.Block);
        try
        {
            BindPass(st.L.Cubes, viewport, near, far);
            Weather.WriteVk(in s.CullL, kind, &v, &pr, _eyePos, lit);
            Weather.WriteCullVk(in s.CullL, count, slot, slot == 0 ? 0 : VkWxDripBase, step);
        }
        finally { LooseBlock.EndCapture(); }
        return VkArena(st.Block);
    }

    /// <summary>Metres the field fell since the last frame's cull at speed 1, across the phase's wrap; 0 when
    /// <paramref name="culls"/> is false or no cull ran last frame, so a drop that just appeared does not streak.</summary>
    private static float VkWxFallStep(VkWxState s, bool culls)
    {
        double now = Weather.Phase, d = now - s.LastPhase;
        if (d < 0) d += Weather.PhaseWrap;
        bool had = s.CulledLast;
        s.LastPhase = now;
        s.CulledLast = culls;
        return had && culls && d < Weather.PhaseWrap * 0.5 ? (float)d : 0f;
    }

    /// <summary>The cull's set: its two blocks, the probe's SH where it is live, the light rig, the records and commands
    /// it writes, and each sampler by name as the draws bind it.</summary>
    private static void VkWxCullBind(CommandList cl, VkWxState s, VkColourState c, int loose, int pass)
    {
        VkDefStage st = s.Cull!;
        Span<BufferBinding> ubo = stackalloc BufferBinding[12];
        Span<BufferBinding> ssbo = stackalloc BufferBinding[16];
        Span<ImageBinding> sampled = stackalloc ImageBinding[48];
        int nu = 0, ns = 0, nt = 0;
        BufferId lights = VkDeferredLights();
        foreach (VkOpaque o in st.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock:
                    ubo[nu++] = VkProbeSh(o.Binding) is { } sh ? sh
                              : o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, loose, st.Block.Length)
                              : o.Binding == UboSlots.Pass ? new BufferBinding(o.Binding, c.Arena, pass, PassBytes)
                              : new BufferBinding(o.Binding, c.ZeroUbo);
                    break;
                case VkBindKind.StorageBlock:
                    ssbo[ns++] = new BufferBinding(o.Binding, o.Binding switch
                    {
                        SsboSlots.WxCmd => s.Cmd,
                        SsboSlots.WxDrops => s.Drops,
                        SsboSlots.Lights when !lights.IsNull => lights,
                        SsboSlots.ClusterCount when !VkClusterCounts.IsNull => VkClusterCounts,
                        SsboSlots.ClusterIdx when !VkClusterIdx.IsNull => VkClusterIdx,
                        SsboSlots.Movers when !VkMovers.IsNull => VkMovers,
                        _ => c.ZeroSsbo,
                    });
                    break;
                case VkBindKind.StorageImage:
                    break;
                default:
                    sampled[nt++] = VkSampled(o, c, 0);
                    break;
            }
        }
        cl.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], default));
    }

    /// <summary>The cull stage and its two buffers, made once per device; refused, nothing falls this session, said once.
    /// A content override that does not build falls back, logged, to the embedded stage.</summary>
    private static bool VkWxCullEnsure(VkBackend vk, VkWxState s, ExtenderLog log)
    {
        if (s.CullTried) return s.Cull != null;
        s.CullTried = true;
        var units = new System.Collections.Generic.Dictionary<string, uint>(VkPrograms.Units) { ["uCover"] = TextureSlots.CoverEsm };
        string label = "vk weather cull";
        string? content = ShaderResource.Override(Weather.ShdWeatherCull, log);
        if (content != null) s.Cull = VkWxCullBuild(vk, content, units, label, log);
        if (content != null && s.Cull == null) log.Always($"AlloyRenderer: {label}'s override did not build (logged above) -- building the embedded stage");
        s.Cull ??= VkWxCullBuild(vk, ShaderStages.Source(Weather.ShdWeatherCull), units, label, log);
        s.Drops = vk.CreateBuffer(new BufferDesc(2L * VkWxRecCap * VkWxRecordBytes, BufferUse.Storage, MemoryKind.Device, "vk weather drops"));
        s.Cmd = vk.CreateBuffer(new BufferDesc(8 * sizeof(uint), BufferUse.Storage | BufferUse.Indirect | BufferUse.CopyDst,
                                               MemoryKind.Device, "vk weather commands"));
        if (s.Cull == null || s.Drops.IsNull || s.Cmd.IsNull)
        {
            s.Cull = null;
            log.Always("AlloyRenderer: the Vulkan weather cull or its buffers were REFUSED (logged above) -- nothing falls on Vulkan this session");
            return false;
        }
        VkWxState w = s;
        LooseBlock.Resolve(s.Cull.Names, () => w.CullL = Weather.VkLocs());
        return true;
    }

    /// <summary>The cull stage from <paramref name="text"/>, translated with the cover map on its unit; null when refused.</summary>
    private static VkDefStage? VkWxCullBuild(VkBackend vk, string text, System.Collections.Generic.Dictionary<string, uint> units,
                                            string label, ExtenderLog log)
    {
        string src = ShaderStages.AfterVersion(ShaderResource.Tokens(ShaderSource.Content(text, log)), $"#define CX_WX_REC_CAP {VkWxRecCap}u\n");
        return VkDefCompile(vk, VkGlsl.TranslateCompute(src, units), src, label, log);
    }

    /// <summary>The splash program's slot in <see cref="VkWxState"/>, past rain's and snow's.</summary>
    private const int VkSplashProg = 2;

    /// <summary>The rain and footfall crowns, through the splash program; false only when the arena is full.</summary>
    private static bool VkSplashDraw(CommandList side, VkWxState s, VkColourState c, in Matrix4x4 view, in Matrix4x4 proj,
                                     int* viewport, float near, float far, bool lit)
    {
        VkProg p = s.Prog[VkSplashProg]!;
        Matrix4x4 v = view, pr = proj;
        int n;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, viewport, near, far);
            n = Weather.WriteSplashVk(in s.Sp, &v, &pr, viewport, _eyePos, lit);
        }
        finally { LooseBlock.EndCapture(); }
        if (n == 0) return true;
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        side.SetPipeline(s.Pipe[VkSplashProg]);
        VkBindSet(side, p, c, loose, pass, c.Slots, 0);
        side.Draw(4, n, 0, 0);
        c.Effects++;
        return true;
    }

    /// <summary>Program <paramref name="k"/> (0 rain, 1 snow, 2 splash) and its premultiplied pipeline, made once per device.</summary>
    private static bool VkWxEnsure(VkBackend vk, VkWxState s, int k, ExtenderLog log)
    {
        if (s.Tried[k]) return s.Prog[k] != null;
        s.Tried[k] = true;
        s.Side ??= new CommandList("vulkan weather", 1 << 14, log);
        (string vs, string fs) = k == VkSplashProg ? Weather.VkSplashTexts(log) : Weather.VkTexts(k == 1, log);
        string label = k == VkSplashProg ? "splash" : k == 1 ? "snow" : "rain";
        var units = new System.Collections.Generic.Dictionary<string, uint>(VkPrograms.Units) { ["uCover"] = TextureSlots.CoverEsm };
        VkProgramText t = WorldDepth.Translate(ShaderResource.Tokens(vs), ShaderResource.Tokens(fs), units);
        VkProg? p = VkBuild(vk, t, label, true, log);
        if (p != null)
        {
            VkWxState w = s;
            if (k == VkSplashProg) LooseBlock.Resolve(p.Names, () => w.Sp = Weather.VkSplashLocs());
            else LooseBlock.Resolve(p.Names, () => w.L[k] = Weather.VkLocs());
            var d = new PipelineDesc
            {
                Vs = p.Vs, Fs = p.Fs, Topo = Topology.TriangleStrip, Raster = new RasterState(CullMode.None),
                Depth = new DepthState(true, false, WorldDepth.NearerOrEqual), Label = $"vk {label}",
            };
            d.Targets.ColorCount = 1;
            d.Targets.Colors[0] = Format.Rgba16F;
            d.Targets.Depth = Format.D32F;
            d.Targets.Samples = 1;
            d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha);
            s.Pipe[k] = vk.CreatePipeline(in d);
            if (s.Pipe[k].IsNull) p = null;
        }
        s.Prog[k] = p;
        if (p == null)
            log.Always(k == VkSplashProg ? "AlloyRenderer: the Vulkan splash program was REFUSED (logged above) -- no rain impacts on Vulkan this session"
                                         : "AlloyRenderer: the Vulkan weather program was REFUSED (logged above) -- nothing falls on Vulkan this session");
        return p != null;
    }
}
