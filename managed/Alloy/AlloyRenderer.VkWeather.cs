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
/// drop of <see cref="Weather"/>'s lattice once, keeps those on screen and under open sky, and the drops and flakes
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
        /// <summary>The mesh path's programs, made the first time the device offers it.</summary>
        public VkWxMeshState? MeshState;
        /// <summary>This frame drew through the mesh path, and the lattice points and drips it walked and the fall step it
        /// took, for the late draw to walk again.</summary>
        public bool Mesh;
        public int Count, Drips;
        public float Step;
        /// <summary>The path the last line named; null until one was said.</summary>
        public Weather.WxPath? SaidPath;
        /// <summary>A splash drew with no standing water recorded, and the line saying so is out.</summary>
        public bool BareSaid;
        public VkProg? Motion;
        public PipelineId MotionPipe;
        public Weather.Loc ML;
        public int MVp, MPrevVp, MJitter, MSize;
        public bool MotionTried, MotionLive;
        public CommandList? MotionSide;
        /// <summary>The camera the weather pass drew with, for the velocity's contact fade and the late draw.</summary>
        public Matrix4x4 View, Proj;
        /// <summary>This frame's fall, drips and crowns wait for the late draw after the temporal resolve, lit or flat; a late
        /// pipeline or the overlay was refused once, so they draw in the scene for the rest of the session.</summary>
        public bool Late, Lit, LateFailed;
        /// <summary>The splash program is ready this frame: with <see cref="Late"/>, the crowns wait for the late draw.</summary>
        public bool Splashed;
        /// <summary>The rain, snow and splash pipelines without depth, for the frame after the resolve.</summary>
        public readonly PipelineId[] LatePipe = new PipelineId[3];
        public CommandList? LateSide;
        /// <summary>Where the late draw last said it drew; the scene until it first draws.</summary>
        public Weather.Site LateSaid;
        /// <summary>The display-size overlay the taa stage composites the precipitation from, and its size. It aliases
        /// nothing: it lives from the late draw to the taa stage, inside the post pair's lanes.</summary>
        public ImageId Over;
        public int OverW, OverH;
        /// <summary>The commands' readback copy, the frame whose copy is in flight (0 = none), and whether the
        /// record cap has been reported.</summary>
        public BufferId CmdRead;
        public int SampleGen, Frames;
        public bool CapLogged;
        /// <summary>The last sampled frame's drops kept by the fall's command and the lattice points it walked; the walk
        /// the copy in flight was taken over. Both clear when nothing falls.</summary>
        public uint Kept, Walked, SampleWalked;
    }

    /// <summary>The command buffer's words: two four-word draw commands, then what each wanted before the cap.</summary>
    private const int VkWxCmdWords = 10;

    /// <summary>How often, in frames that culled, the commands are read back to see whether the cap bound.</summary>
    private const int VkWxSamplePeriod = 30;

    /// <summary>The bytes of one cull survivor's record: five vec4s (<c>CxWxPlace</c>).</summary>
    private const int VkWxRecordBytes = 5 * 16;

    /// <summary>The most survivors one command keeps: a ceiling on the drops drawn, not on the field. ponytail: a frame
    /// that would keep more draws an arbitrary subset; raise it, or cap by distance, if a real area reaches it.</summary>
    private const int VkWxRecCap = 1 << 17;

    /// <summary>The first record the roof drips take: past the most the fall can keep.</summary>
    private const int VkWxDripBase = VkWxRecCap;

    /// <summary>Every precipitation pipeline's depth: tested against the scene's target, never written. The pass
    /// hands <see cref="DepthState.Write"/> to the flush, so this is the one place it is decided.</summary>
    private static readonly DepthState VkWxDepth = new(true, false, WorldDepth.NearerOrEqual);

    private static VkWxState _vkWx = new();

    /// <summary>The precipitation into <paramref name="cl"/> as its own pass; false only when the arena is full.</summary>
    private static bool VkWeatherPass(VkBackend vk, CommandList cl, VkColourState c, in Matrix4x4 view, in Matrix4x4 proj,
                                      int* viewport, float near, float far, bool lightingOn, ExtenderLog log)
    {
        VkWxState s = _vkWx;
        if (s.Dev != vk) _vkWx = s = new VkWxState { Dev = vk };
        s.Fell = s.Dripped = s.Splashed = false;
        if (_capturing || c.DepthRead.IsNull) { s.CulledLast = false; return true; }
        int count = Weather.StageVk(AreaWind(), out bool snow, out int drips);
        int k = snow ? 1 : 0;
        if (count < 0) { s.CulledLast = false; s.Walked = 0; return true; }
        // FSR and TAA reconstruct from history, and a drop or a crown in it smears over the ground: hold them for after the resolve
        bool late = Weather.DrawsWhere(Fsr.On, PostChain.TaaOn) != Weather.Site.Scene && !s.LateFailed;
        bool any = count > 0 || drips > 0;
        // rain's own motion is drawn again from the cull's records, which only the compute path keeps
        bool mesh = any && VkWxMeshPath(vk, s, k, drips, Weather.MotionWanted(Fsr.On, PostChain.TaaOn, late), log);
        bool culls = !mesh && any && VkWxCullEnsure(vk, s, log);
        bool fall = count > 0 && (mesh || culls && VkWxEnsure(vk, s, k, log));
        bool drip = drips > 0 && (mesh || culls && VkWxEnsure(vk, s, 0, log));
        bool lit = lightingOn && AlloyLights.Ready;
        float step = VkWxFallStep(s, fall || drip);
        if (!mesh && (fall || drip) && !VkWxCull(cl, s, c, snow ? 2 : 1, fall ? count : 0, drip ? drips : 0, step, view, proj, viewport, near, far, lit))
            return false;
        s.Fell = fall; s.Dripped = drip; s.Kind = snow ? 2 : 1;
        s.Mesh = mesh; s.Count = count; s.Drips = drips; s.Step = step;
        s.View = view; s.Proj = proj;
        VkCopyDepth(cl, c);
        ReadOnlySpan<ImageId> read = [c.DepthRead, c.G3];
        cl.ReadImages(c.GbufWritten ? read : read[..1]);
        CommandList side = s.Side ??= new CommandList("vulkan weather", 1 << 14, log);
        side.Reset();
        int arena0 = c.ArenaUsed;
        side.SetViewport(0, 0, viewport[2], viewport[3]);
        s.Late = late; s.Lit = lit;
        Matrix4x4 mv = view, mp = proj;
        if (!late && fall && !(mesh ? VkWxMeshDraw(vk, side, s, c, k, s.Kind, count, false, &mv, &mp, viewport, near, far, lit, log)
                                    : VkWxDraw(side, s, c, k, s.Kind, 0, view, proj, viewport, near, far, lit))) return false;
        if (!late && drip && !(mesh ? VkWxMeshDraw(vk, side, s, c, 0, 3, drips, false, &mv, &mp, viewport, near, far, lit, log)
                                    : VkWxDraw(side, s, c, 0, 3, 1, view, proj, viewport, near, far, lit))) return false;
        bool splash = VkWxEnsure(vk, s, VkSplashProg, log);
        s.Splashed = splash;
        if (!late && splash && !VkSplashDraw(side, s, c, view, proj, viewport, near, far, lit, s.Pipe[VkSplashProg], false)) return false;
        ReadOnlySpan<ColorAttachment> colors = [new(c.Hdr, 0, 0, LoadOp.Load, StoreOp.Store, default)];
        VkFlushPass(cl, side, arena0, new RenderPassDesc(colors, new DepthAttachment(c.Depth, 0, 0, LoadOp.Load, StoreOp.Store, WorldDepth.Clear)),
                    writesDepth: VkWxDepth.Write);
        if ((fall || drip) && VkWxPathLine(s, vk, mesh ? Weather.WxPath.Mesh : Weather.WxPath.Compute) is { } said)
            log.Always($"{said} -- the {(snow ? "snow" : "rain")} is LIVE, {count} lattice point(s) and {drips} drip(s)");
        return true;
    }

    /// <summary>The draw of <paramref name="kind"/> (1 rain, 2 snow, 3 drips) through program <paramref name="k"/>, its own
    /// loose block, indirect from the cull's command <paramref name="slot"/>; false only when the arena is full.</summary>
    private static bool VkWxDraw(CommandList side, VkWxState s, VkColourState c, int k, int kind, int slot, in Matrix4x4 view,
                                 in Matrix4x4 proj, int* viewport, float near, float far, bool lit, ReadOnlySpan<ImageBinding> own = default)
    {
        VkProg p = s.Prog[k]!;
        side.SetPipeline(s.Pipe[k]);
        Matrix4x4 v = view, pr = proj;
        Array.Clear(p.Block);
        LooseBlock.Capture(p.Members, p.Block);
        try
        {
            BindPass(p.L.CubeCount, viewport, near, far);
            Weather.WriteVk(in s.L[k], kind, &v, &pr, _eyePos, lit, viewport[3]);
            LooseBlock.Set(s.L[k].Screen, [(float)viewport[2], viewport[3]]);
        }
        finally { LooseBlock.EndCapture(); }
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        ReadOnlySpan<BufferBinding> drops = [new(SsboSlots.WxDrops, s.Drops)];
        VkBindSet(side, p, c, loose, pass, c.Slots, 0, default, drops, own);
        side.DrawIndirect(s.Cmd, slot * 4 * sizeof(uint), 1);
        c.Effects = true;
        return true;
    }

    /// <summary>The cull into <paramref name="cl"/>: both commands reset, then one dispatch for the fall's
    /// <paramref name="count"/> lattice points and one for the <paramref name="drips"/>; false only when the arena is full.</summary>
    private static bool VkWxCull(CommandList cl, VkWxState s, VkColourState c, int kind, int count, int drips, float step, in Matrix4x4 view,
                                 in Matrix4x4 proj, int* viewport, float near, float far, bool lit, ReadOnlySpan<ImageBinding> own = default)
    {
        ReadOnlySpan<uint> cmd = [4, 0, 0, 0, 4, 0, 0, VkWxDripBase, 0, 0];
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
        if (count > 0) { VkWxCullBind(cl, s, c, fall, fallPass, own); cl.Dispatch((count + 63) / 64); }
        if (drips > 0) { VkWxCullBind(cl, s, c, drip, dripPass, own); cl.Dispatch((drips + 63) / 64); }
        if (++s.Frames % VkWxSamplePeriod == 0 && s.SampleGen == 0)
        {
            s.SampleWalked = (uint)count;
            cl.CopyBuffer(new CopyBufferCmd(s.Cmd, 0, s.CmdRead, 0, VkWxCmdWords * sizeof(uint)));
            s.SampleGen = s.Frames;
            cl.WhenDone(VkWxSampleLanded, s.SampleGen);
        }
        return true;
    }

    private static readonly Action<int, bool> VkWxSampleLanded = OnVkWxSample;

    /// <summary>The period line's cull figure: of the lattice points the fall walked, how many drops it kept.</summary>
    internal static string VkWxKept() => _vkWx.Walked == 0 ? "" : $", the cull kept {_vkWx.Kept} of {_vkWx.Walked}";

    /// <summary>A sampled frame's commands landed: when a command wanted more survivors than the records keep, say so
    /// once, loudly, since the drops past the cap are simply not drawn.</summary>
    private static void OnVkWxSample(int gen, bool ran)
    {
        VkWxState s = _vkWx;
        if (s.SampleGen != gen) return;
        s.SampleGen = 0;
        Span<uint> words = stackalloc uint[VkWxCmdWords];
        if (!ran || s.Dev == null || !s.Dev.ReadMapped(s.CmdRead, 0, MemoryMarshal.AsBytes(words))) return;
        s.Kept = words[1];
        s.Walked = s.SampleWalked;
        uint wanted = Math.Max(words[8], words[9]);
        if (s.CapLogged || wanted <= VkWxRecCap) return;
        s.CapLogged = true;
        _log?.Always($"AlloyRenderer: the precipitation kept {VkWxRecCap} of {wanted} visible drops in one draw -- the rest are "
                   + "NOT drawn; lower weather.count or weather.density, or raise the record cap");
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
            Weather.WriteVk(in s.CullL, kind, &v, &pr, _eyePos, lit, viewport[3]);
            Weather.WriteCullVk(in s.CullL, count, slot, step);
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
    private static void VkWxCullBind(CommandList cl, VkWxState s, VkColourState c, int loose, int pass,
                                     ReadOnlySpan<ImageBinding> own = default)
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
                    sampled[nt++] = Own(own, o.Binding) ?? VkSampled(o, c, 0);
                    break;
            }
        }
        VkBindTally.Count(VkBindBy.Effects);
        cl.BindSet(0, new BindSet(ubo[..nu], ssbo[..ns], sampled[..nt], default));
    }

    /// <summary>The cull stage and its buffers, made once per device through the compute path every resref takes, so a
    /// content override gets its guards and its fallback to the embedded stage; refused, nothing falls this session,
    /// said once, and nothing it made is kept.</summary>
    private static bool VkWxCullEnsure(VkBackend vk, VkWxState s, ExtenderLog log)
    {
        if (s.CullTried) return s.Cull != null;
        s.CullTried = true;
        string define = $"CX_WX_REC_CAP {VkWxRecCap}u" + (vk.WaveOps ? "\nCX_WX_WAVE 1" : "");
        s.Cull = VkDefBuild(vk, Weather.ShdWeatherCull, define, "vk weather cull", log);
        s.Drops = vk.CreateBuffer(new BufferDesc(2L * VkWxRecCap * VkWxRecordBytes, BufferUse.Storage, MemoryKind.Device, "vk weather drops"));
        s.Cmd = vk.CreateBuffer(new BufferDesc(VkWxCmdWords * sizeof(uint), BufferUse.Storage | BufferUse.Indirect | BufferUse.CopyDst
                                               | BufferUse.CopySrc, MemoryKind.Device, "vk weather commands"));
        s.CmdRead = vk.CreateBuffer(new BufferDesc(VkWxCmdWords * sizeof(uint), BufferUse.CopyDst, MemoryKind.Readback,
                                                   "vk weather commands readback"));
        if (s.Cull == null || s.Drops.IsNull || s.Cmd.IsNull || s.CmdRead.IsNull)
        {
            if (s.Cull != null) vk.Destroy(s.Cull.Pipe);
            vk.Destroy(s.Drops); vk.Destroy(s.Cmd); vk.Destroy(s.CmdRead);
            s.Cull = null; s.Drops = default; s.Cmd = default; s.CmdRead = default;
            log.Always("AlloyRenderer: the Vulkan weather cull or its buffers were REFUSED (logged above) -- nothing falls on Vulkan this session");
            return false;
        }
        VkWxState w = s;
        LooseBlock.Resolve(s.Cull.Names, () => w.CullL = Weather.VkLocs());
        return true;
    }

    /// <summary>The splash program's slot in <see cref="VkWxState"/>, past rain's and snow's.</summary>
    private const int VkSplashProg = 2;

    /// <summary>The rain and footfall crowns, through the splash program on <paramref name="pipe"/>: in the scene, or
    /// <paramref name="late"/> after the resolve, where each part tests the depth copy itself. False only when the arena is full.</summary>
    private static bool VkSplashDraw(CommandList side, VkWxState s, VkColourState c, in Matrix4x4 view, in Matrix4x4 proj,
                                     int* viewport, float near, float far, bool lit, PipelineId pipe, bool late)
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
            LooseBlock.Set(s.Sp.Late, late ? 1 : 0);
            LooseBlock.Set(s.Sp.StandOn, c.GbufWritten ? 1 : 0);
            LooseBlock.Set(s.Sp.Viz, Weather.Viz.V);
            if (!c.GbufWritten && !s.BareSaid)
            {
                s.BareSaid = true;
                _log?.Always("AlloyRenderer: the rain crowns read every landing point as bare ground -- this frame recorded no standing water (the deferred G-buffer is off), so puddles throw full crowns");
            }
            LooseBlock.Set(s.Sp.Screen, [(float)viewport[2], viewport[3]]);
        }
        finally { LooseBlock.EndCapture(); }
        if (n == 0) return true;
        int loose = VkArena(p.Block), pass = VkArenaPass();
        if (loose < 0 || pass < 0) return false;
        side.SetPipeline(pipe);
        VkBindSet(side, p, c, loose, pass, c.Slots, 0);
        side.Draw(4, n, 0, 0);
        c.Effects |= !late;
        return true;
    }

    /// <summary>Program <paramref name="k"/>'s name in logs and pipeline labels.</summary>
    private static string VkWxLabel(int k) => k == VkSplashProg ? "splash" : k == 1 ? "snow" : "rain";

    /// <summary>The precipitation's premultiplied pipeline over <paramref name="p"/>: depth-tested against the scene's target,
    /// or with no depth target for the frame after the resolve.</summary>
    private static PipelineDesc VkWxPipeDesc(VkProg p, string label, bool depth)
    {
        var d = new PipelineDesc
        {
            Vs = p.Vs, Fs = p.Fs, Topo = Topology.TriangleStrip, Raster = new RasterState(CullMode.None), Label = label,
        };
        if (depth) d.Depth = VkWxDepth;
        d.Targets.ColorCount = 1;
        d.Targets.Colors[0] = Format.Rgba16F;
        if (depth) d.Targets.Depth = Format.D32F;
        d.Targets.Samples = 1;
        d.Blend[0] = new AttachmentBlend(true, BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha);
        return d;
    }

    /// <summary>Program <paramref name="k"/> (0 rain, 1 snow, 2 splash) and its premultiplied pipeline, made once per device.</summary>
    private static bool VkWxEnsure(VkBackend vk, VkWxState s, int k, ExtenderLog log)
    {
        if (s.Tried[k]) return s.Prog[k] != null;
        s.Tried[k] = true;
        (string vs, string fs) = k == VkSplashProg ? Weather.VkSplashTexts(log) : Weather.VkTexts(k == 1, log);
        string label = VkWxLabel(k);
        VkProgramText t = WorldDepth.Translate(ShaderResource.Tokens(vs), ShaderResource.Tokens(fs), VkPrograms.Units);
        VkProg? p = VkBuild(vk, t, label, true, log);
        if (p != null)
        {
            VkWxState w = s;
            if (k == VkSplashProg) LooseBlock.Resolve(p.Names, () => w.Sp = Weather.VkSplashLocs());
            else LooseBlock.Resolve(p.Names, () => w.L[k] = Weather.VkLocs());
            PipelineDesc d = VkWxPipeDesc(p, $"vk {label}", true);
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
