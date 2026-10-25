using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// <see cref="CasterGather"/> on Vulkan: the same two compute stages over the same staged records and the same
/// bounds, recorded into the shadow bracket's command list. Both dispatches read the <c>CxGather</c> block
/// <see cref="GatherBlock"/> stages, and both sphere buffers are this port's own -- the shadow bracket runs ahead
/// of the colour build, so nothing is shared with the Vulkan cull's.
/// </summary>
internal static unsafe partial class CasterGatherVk
{
    /// <summary>Everything on one device, replaced whole when the device is, plus the frame's staged sizes.</summary>
    private sealed class GatherVkState
    {
        public VkBackend? Dev;
        /// <summary>The on-device check's own list; the frame's is the shadow bracket's.</summary>
        public CommandList? Cmds;
        public PipelineId Prog, LayerProg;
        /// <summary>The pipelines did not build or the on-device check disagreed: the bake stands down, loudly.</summary>
        public bool Failed;
        public BufferId Ubo, Src, DynSrc, ViewTbl, Cull, Cast;
        public long SrcCap, DynSrcCap, ViewCap, CullCap, CastCap;
        /// <summary>Command/count pairs rotated per view, <see cref="CasterGather.Rotation"/>'s argument.</summary>
        public readonly BufferId[] Cmd = new BufferId[Rotation], Cnt = new BufferId[Rotation],
                                   LyCmd = new BufferId[Rotation], LyCnt = new BufferId[Rotation];
        public long CmdCap, LyCmdCap;
        public int CntCap, LyCntCap, At, LyAt;
        public int DrawN, Groups, DynDrawN;
        /// <summary>A caller reported changed source bytes that have not reached the buffer yet.</summary>
        public bool SrcDirty = true;
        public GatherBlock Block;
    }

    private static GatherVkState _s = new();

    private const int Rotation = CasterGather.Rotation, GroupSize = 64;

    /// <summary>Room for the fixture's uploads and dispatches; the frame's own list is the bracket's.</summary>
    private const int ArenaBytes = 8192;

    /// <summary>The staged source is non-empty and the dispatches are armed.</summary>
    internal static bool Active => !_s.Prog.IsNull && _s.DrawN > 0;

    /// <summary>The dynamic source is standing for this frame.</summary>
    internal static bool DynamicReady => _s.DynDrawN > 0 && !_s.DynSrc.IsNull;

    /// <summary>Forget the frame's staging, as <see cref="CasterGather.Stand"/> does: a frame that stages nothing
    /// bakes empty views rather than the previous frame's world.</summary>
    internal static void Stand() { _s.DrawN = 0; _s.DynDrawN = 0; }

    internal static void StandDynamic() => _s.DynDrawN = 0;

    /// <summary>The per-view set's five-word commands and per-batch counts, the pair the last
    /// <see cref="Dispatch"/> wrote.</summary>
    internal static BufferId Commands => _s.Cmd[_s.At];
    internal static BufferId Counts => _s.Cnt[_s.At];

    /// <summary>...and the layered set's, the pair the last <see cref="DispatchLayer"/> wrote.</summary>
    internal static BufferId LayerCommands => _s.LyCmd[_s.LyAt];
    internal static BufferId LayerCounts => _s.LyCnt[_s.LyAt];

    /// <summary>The view table the layered depth stages read their fit matrix and layer out of.</summary>
    internal static BufferId Views => _s.ViewTbl;

    /// <summary>The device's pipelines and constant resources, then the on-device check, once per device. False
    /// (logged) when the device refused one: the caller stands the whole bake down.</summary>
    internal static bool Ensure(VkBackend vk, ExtenderLog log)
    {
        if (_s.Dev != vk) _s = new GatherVkState { Dev = vk };
        var s = _s;
        if (s.Failed) return false;
        if (!s.Prog.IsNull) return true;
        s.Cmds = new CommandList("vulkan caster gather check", ArenaBytes, log);
        s.Prog = Compute(vk, CasterGather.GatherCs, "CasterGatherVk caster cull");
        s.LayerProg = Compute(vk, CasterGather.LayerCs, "CasterGatherVk layered caster cull");
        s.Ubo = vk.CreateBuffer(new BufferDesc(Std140.BlockBytes<GatherBlock>(), BufferUse.Uniform, MemoryKind.Upload, "gather block"));
        if (s.Prog.IsNull || s.LayerProg.IsNull || s.Ubo.IsNull)
        {
            s.Failed = true;
            log.Always("CasterGatherVk: the device REFUSED the caster cull's pipelines or constants (logged above)");
            return false;
        }
        if (!DeviceCheck(vk, log))
        {
            s.Failed = true;
            log.Always("CasterGatherVk: the device's caster cull DISAGREES with the reference list (DeviceCheck above)");
            return false;
        }
        return true;
    }

    private static PipelineId Compute(VkBackend vk, string src, string label)
    {
        var d = new PipelineDesc { Cs = vk.CreateShader(new ShaderDesc(ShaderStage.Compute, null, src, label)), Label = label };
        return vk.CreatePipeline(in d);
    }

    /// <summary>The frame's bounds into this port's own pair of sphere buffers: the colour cull's spheres, which
    /// the cull test reads, and the caster pass's geometric ones, which stand in wherever the colour cull exempted
    /// a bound. Recorded by the colour frame, ahead of the bake that reads them.</summary>
    internal static void RecordSpheres(VkBackend vk, CommandList cl, Sphere* cull, Sphere* cast, int slots)
    {
        if (slots <= 0) return;
        var s = _s;
        long bytes = (long)slots * sizeof(Sphere);
        Grow(vk, ref s.Cull, ref s.CullCap, bytes, BufferUse.Storage, "gather cull bounds");
        Grow(vk, ref s.Cast, ref s.CastCap, bytes, BufferUse.Storage, "gather caster bounds");
        if (s.Cull.IsNull || s.Cast.IsNull) return;
        cl.UpdateBuffer(s.Cull, 0, new ReadOnlySpan<byte>(cull, (int)bytes));
        cl.UpdateBuffer(s.Cast, 0, new ReadOnlySpan<byte>(cast, (int)bytes));
    }

    /// <summary>Publish one frame's staging into <paramref name="cl"/>, as <see cref="CasterGather.Begin"/> does:
    /// the source buffer is persistent and a frame whose caller reports no changed bytes sends nothing.</summary>
    internal static bool Begin(VkBackend vk, CommandList cl, int drawN, int groups, CasterSrcRec* src, bool changed)
    {
        var s = _s;
        s.DrawN = 0;
        s.SrcDirty |= changed;
        if (drawN <= 0 || groups <= 0) return false;
        if (s.Cull.IsNull || s.Cast.IsNull) return false;   // no colour build yet: no bounds to cull against
        long bytes = (long)drawN * sizeof(CasterSrcRec);
        bool grow = bytes > s.SrcCap;
        Grow(vk, ref s.Src, ref s.SrcCap, bytes, BufferUse.Storage, "gather source");
        if (s.Src.IsNull) return false;
        if (grow || s.SrcDirty)
        {
            cl.UpdateBuffer(s.Src, 0, new ReadOnlySpan<byte>(src, (int)bytes));
            s.SrcDirty = false;
            CasterGather.Uploads++;
        }
        else CasterGather.UploadsSkipped++;
        bool growCmd = drawN > s.CmdCap, growCnt = groups > s.CntCap;
        const BufferUse Out = BufferUse.Storage | BufferUse.Indirect | BufferUse.CopySrc;
        for (int i = 0; i < Rotation; i++)
        {
            Make(vk, cl, ref s.Cmd[i], growCmd, (long)drawN * CasterGather.CmdBytes, Out, "gather commands");
            Make(vk, cl, ref s.Cnt[i], growCnt, CountBytes(groups), Out, "gather counts");
            if (s.Cmd[i].IsNull || s.Cnt[i].IsNull) return false;
        }
        if (growCmd) s.CmdCap = drawN;
        if (growCnt) s.CntCap = groups;
        s.DrawN = drawN; s.Groups = groups;
        return true;
    }

    /// <summary>This frame's dynamic source, uploaded whole: a few hundred records whose membership moves every
    /// frame by construction. False = there is none, and a dynamic view falls back to the full source.</summary>
    internal static bool DynamicSource(VkBackend vk, CommandList cl, int drawN, CasterSrcRec* src)
    {
        var s = _s;
        s.DynDrawN = 0;
        if (s.Prog.IsNull || drawN <= 0) return false;
        long bytes = (long)drawN * sizeof(CasterSrcRec);
        Grow(vk, ref s.DynSrc, ref s.DynSrcCap, bytes, BufferUse.Storage, "gather dynamic source");
        if (s.DynSrc.IsNull) return false;
        cl.UpdateBuffer(s.DynSrc, 0, new ReadOnlySpan<byte>(src, (int)bytes));
        s.DynDrawN = drawN;
        return true;
    }

    /// <summary>Count-buffer bytes for <paramref name="groups"/> batches: the per-batch counts, the receiver tally
    /// and the per-detail-level triangle tallies.</summary>
    private static long CountBytes(int groups) => (long)(groups + CasterGather.CountExtra) * sizeof(uint);

    /// <summary>Select one view's casters into <paramref name="cl"/>, as <see cref="CasterGather.Dispatch"/> does.</summary>
    internal static void Dispatch(CommandList cl, Vector3 focus, Vector3 dir, float reach, float along, bool point,
                                 float lodR1, float lodR2, int set, bool receiver, bool dynSrc)
    {
        var s = _s;
        s.At = (s.At + 1) % Rotation;
        bool dyn = dynSrc && DynamicReady;
        int drawN = dyn ? s.DynDrawN : s.DrawN;
        if (dyn) CasterGather.DynDispatches++;
        CasterGather.View(ref s.Block, drawN, s.Groups, focus, dir, reach, along, point, lodR1, lodR2, set, receiver,
                          CasterGather.Mode != 0 ? 1 : 0);
        cl.ZeroBuffer(s.Cnt[s.At], 0, CountBytes(s.Groups));
        Block(cl);
        cl.SetPipeline(s.Prog);
        Bind(cl, dyn ? s.DynSrc : s.Src, s.Cmd[s.At], s.Cnt[s.At], default);
        cl.Dispatch((drawN + GroupSize - 1) / GroupSize);
        CasterGather.Views++;
    }

    /// <summary>Select every queued view's casters in ONE dispatch into <paramref name="cl"/>, as
    /// <see cref="CasterGather.DispatchLayer"/> does: the chunk's view table goes up, the command buffer is sized
    /// draws x views, and a survivor's <c>baseInstance</c> packs its view index above the ownership slot.</summary>
    internal static bool DispatchLayer(VkBackend vk, CommandList cl, CasterViewRec* views, int viewN, bool dynSrc,
                                      int cull = -1)
    {
        var s = _s;
        if (s.LayerProg.IsNull || s.DrawN <= 0 || viewN <= 0 || s.Cull.IsNull || s.Cast.IsNull) return false;
        bool dyn = dynSrc && DynamicReady;
        int srcN = dyn ? s.DynDrawN : s.DrawN;
        if (dyn) CasterGather.DynDispatches++;
        long viewBytes = (long)viewN * sizeof(CasterViewRec);
        Grow(vk, ref s.ViewTbl, ref s.ViewCap, viewBytes, BufferUse.Storage, "gather view table");
        if (s.ViewTbl.IsNull) return false;
        cl.UpdateBuffer(s.ViewTbl, 0, new ReadOnlySpan<byte>(views, (int)viewBytes));

        s.LyAt = (s.LyAt + 1) % Rotation;
        long cmds = (long)s.DrawN * viewN;
        bool growCmd = cmds > s.LyCmdCap, growCnt = s.Groups > s.LyCntCap;
        for (int i = 0; i < Rotation; i++)
        {
            Make(vk, cl, ref s.LyCmd[i], growCmd, cmds * CasterGather.CmdBytes,
                 BufferUse.Storage | BufferUse.Indirect | BufferUse.CopySrc, "gather layered commands");
            Make(vk, cl, ref s.LyCnt[i], growCnt, CountBytes(s.Groups),
                 BufferUse.Storage | BufferUse.Indirect | BufferUse.CopySrc, "gather layered counts");
            if (s.LyCmd[i].IsNull || s.LyCnt[i].IsNull) return false;
        }
        if (growCmd) s.LyCmdCap = cmds;
        if (growCnt) s.LyCntCap = s.Groups;
        CasterGather.Layer(ref s.Block, srcN, viewN, s.Groups, cull >= 0 ? cull : CasterGather.Mode != 0 ? 1 : 0);
        cl.ZeroBuffer(s.LyCnt[s.LyAt], 0, CountBytes(s.Groups));
        Block(cl);
        cl.SetPipeline(s.LayerProg);
        Bind(cl, dyn ? s.DynSrc : s.Src, s.LyCmd[s.LyAt], s.LyCnt[s.LyAt], s.ViewTbl);
        cl.Dispatch((srcN + GroupSize - 1) / GroupSize, viewN);
        CasterGather.LayerDispatches++; CasterGather.LayerViews += viewN;
        return true;
    }

    /// <summary>The staged block into the device's copy.</summary>
    private static void Block(CommandList cl)
    {
        Span<byte> blk = stackalloc byte[Std140.BlockBytes<GatherBlock>()];
        blk.Clear();
        MemoryMarshal.Write(blk, in _s.Block);
        cl.UpdateBuffer(_s.Ubo, 0, blk);
    }

    /// <summary>One dispatch's set: the block, the source, the shared bounds and the output pair, plus the view
    /// table for the layered stage (a null id leaves the point on the layered dispatch's own table).</summary>
    private static void Bind(CommandList cl, BufferId src, BufferId cmd, BufferId cnt, BufferId views)
    {
        var s = _s;
        ReadOnlySpan<BufferBinding> ubo = [new(UboSlots.Gather, s.Ubo)];
        if (views.IsNull)
        {
            ReadOnlySpan<BufferBinding> plain =
            [
                new(CasterGather.BindSrc, src), new(CullPass.BindSphere, s.Cull), new(CasterGather.BindCast, s.Cast),
                new(CasterGather.BindCmd, cmd), new(CasterGather.BindCount, cnt),
            ];
            cl.BindSet(0, new BindSet(ubo, plain, default, default));
            return;
        }
        ReadOnlySpan<BufferBinding> layered =
        [
            new(CasterGather.BindSrc, src), new(CullPass.BindSphere, s.Cull), new(CasterGather.BindCast, s.Cast),
            new(CasterGather.BindCmd, cmd), new(CasterGather.BindCount, cnt), new(CasterGather.BindView, views),
        ];
        cl.BindSet(0, new BindSet(ubo, layered, default, default));
    }

    /// <summary>Hold <paramref name="id"/> at <paramref name="bytes"/>, remade when <paramref name="grow"/> asks and
    /// zeroed when new: an indirect command or count read out of uninitialised memory can hang the GPU.</summary>
    private static void Make(VkBackend vk, CommandList cl, ref BufferId id, bool grow, long bytes, BufferUse use, string label)
    {
        if (!id.IsNull && !grow) return;
        vk.Destroy(id);
        id = vk.CreateBuffer(new BufferDesc(Math.Max(bytes, 4), use, MemoryKind.Device, label));
        if (!id.IsNull) cl.ZeroBuffer(id);
    }

    /// <summary>An upload buffer at least <paramref name="bytes"/> long, remade larger when it is not.</summary>
    private static void Grow(VkBackend vk, ref BufferId id, ref long cap, long bytes, BufferUse use, string label)
    {
        if (!id.IsNull && bytes <= cap) return;
        vk.Destroy(id);
        cap = Math.Max(bytes, 4);
        id = vk.CreateBuffer(new BufferDesc(cap, use, MemoryKind.Upload, label));
        if (id.IsNull) cap = 0;
    }
}
