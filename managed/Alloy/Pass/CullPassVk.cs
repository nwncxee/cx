using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// <see cref="CullPass"/> on Vulkan: the same two compute stages over the same source records and spheres, recorded
/// first into the Vulkan frame's list. The prepass and colour sets are both cut, as GL cuts them; until the Vulkan
/// prepass exists there is no depth to reduce, so both are frustum, LOD and fog only.
/// </summary>
internal static class CullPassVk
{
    /// <summary>Everything on one device, replaced whole when the device is (context loss drops it), plus the
    /// inputs the cull site staged for this frame's recording.</summary>
    private sealed class CullVkState
    {
        public VkBackend? Dev;
        public CommandList? Cmds;
        public PipelineId Prog, ProgSort;
        /// <summary>The pipelines did not build or the on-device check disagreed: Alloy has already failed, loudly.</summary>
        public bool Failed, Live;
        /// <summary>This frame's lists were recorded: the passes draw from them rather than from the staged order.</summary>
        public bool Ran;
        public BufferId Ubo, Src, Sph, Key, Rej;
        public long SrcCap, SphCap;
        public int CmdCap, CntCap;
        /// <summary>Indexed by <see cref="Prepass"/> and <see cref="Colour"/>.</summary>
        public readonly BufferId[] Cmd = new BufferId[Phases], Slot = new BufferId[Phases], Cnt = new BufferId[Phases];
        /// <summary>A 1x1 stand-in on the pyramid's slot: the cull stage names the sampler whether or not it
        /// tests occlusion, and Vulkan wants every descriptor a pipeline uses written.</summary>
        public ImageId Pyr;
        /// <summary>This frame's list, staged at the cull site; <see cref="Groups"/> 0 means nothing to cut.</summary>
        public CullSrcRec[]? Records;
        public Sphere[]? Spheres;
        public int Groups, DrawN, SphereSlots;
        public CullBlock Block;
    }

    private static CullVkState _s = new();

    /// <summary>This port's output sets: the prepass's (which writes the hi-Z verdicts) and the colour pass's.</summary>
    private const int Prepass = 0, Colour = 1, Phases = 2;

    /// <summary>The sets a pass draws: the prepass's, and the colour passes'.</summary>
    internal const int PhasePrepass = Prepass, PhaseColour = Colour;

    /// <summary>The cull recorded this frame's sets; read by the passes after it, false when it staged nothing.</summary>
    internal static bool Ran => _s.Ran;

    /// <summary>Phase <paramref name="p"/>'s five-word draw commands, one per staged draw.</summary>
    internal static BufferId Commands(int p) => _s.Cmd[p];

    /// <summary>Phase <paramref name="p"/>'s survivor count per batch, the count an indirect-count draw reads.</summary>
    internal static BufferId Counts(int p) => _s.Cnt[p];

    /// <summary>Phase <paramref name="p"/>'s compacted draw-to-record order, on the draw-index point.</summary>
    internal static BufferId Slots(int p) => _s.Slot[p];

    /// <summary>The first sorted batch: from here up a batch is dense, its dropped draws zero-instance commands.</summary>
    internal static int CompactBelow => _s.Block.CompactBelow;
    /// <summary>Room for the fixture's uploads, blocks and binds; the frame's own list is <see cref="VulkanFrame"/>'s.</summary>
    private const int ArenaBytes = 4096;
    /// <summary>The mirror-plane height that clips nothing: only the planar reflection's dispatch sets one.</summary>
    private const float NoClipPlane = -1e30f;

    /// <summary>Stage this frame's draw list for <see cref="Record"/>. Zero groups (a probe capture, or
    /// <c>/alloy cull 0</c>) stages nothing, so no list from an older pass stays armed.</summary>
    internal static unsafe void Stage(int groups, int drawN, CullSrcRec[] src, Sphere[] sph, int sphereSlots,
                                      float lodScale, int compactBelow, Vector3 eye, Matrix4x4* vp)
    {
        var s = _s;
        s.Groups = CullPass.Mode > CullPass.ModeOff && drawN > 0 ? groups : 0;
        if (s.Groups <= 0) return;
        s.Records = src; s.Spheres = sph; s.DrawN = drawN; s.SphereSlots = sphereSlots;
        s.Block = new CullBlock
        {
            DrawN = drawN, Vp = *vp, PyrVp = *vp, LodPx = MathF.Max(CullPass.LodPx, 0f), LodScale = lodScale,
            ClipZ = NoClipPlane, CompactBelow = Math.Clamp(compactBelow, 0, groups), Eye = new Vector4(eye, 0f),
        };
        CullPass.Planes(in *vp, s.Block.Planes, eye);
        float opaque = CullPass.FogCull ? SceneFog.OpaqueDistance() : float.PositiveInfinity;
        s.Block.FogCull = float.IsInfinity(opaque) ? 0f : opaque;
        Vector3 origin = SceneFog.Origin(out bool radial);
        s.Block.FogOrigin = new Vector4(origin, radial ? 1f : 0f);
    }

    /// <summary>Record the staged cull into <paramref name="cl"/>, ahead of anything that reads its sets. False
    /// (logged) when the device refused the pass: the frame has no other visibility test, so the caller fails Alloy.</summary>
    internal static bool Record(VkBackend vk, CommandList cl, ExtenderLog log)
    {
        if (!Ensure(vk, log)) return false;
        var s = _s;
        int groups = s.Groups;
        s.Groups = 0;
        s.Ran = false;
        if (groups <= 0 || s.Records == null || s.Spheres == null) return true;
        if (!Cull(vk, cl, groups, s.DrawN, s.Records, s.Spheres, s.SphereSlots, ref s.Block)) return false;
        s.Ran = true;
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"CullPassVk: LIVE -- {s.DrawN} draws in {groups} batches culled on Vulkan (frustum, LOD, fog; "
                     + "hi-Z waits for the Vulkan prepass depth)");
        }
        return true;
    }

    /// <summary>The device's pipelines and constant resources, then the on-device check, once per device.</summary>
    private static bool Ensure(VkBackend vk, ExtenderLog log)
    {
        if (_s.Dev != vk) _s = new CullVkState { Dev = vk };
        var s = _s;
        if (s.Failed) return false;
        if (!s.Prog.IsNull) return true;
        s.Cmds = new CommandList("vulkan cull check", ArenaBytes, log);
        s.Prog = Compute(vk, CullPass.CullCs, "CullPassVk frustum cull");
        s.ProgSort = Compute(vk, CullPass.SortCs, "CullPassVk back-to-front rank sort");
        s.Ubo = vk.CreateBuffer(new BufferDesc(Std140.BlockBytes<CullBlock>(), BufferUse.Uniform, MemoryKind.Upload, "cull block"));
        s.Pyr = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, 1, 1, 1, 1, 1, 1, ImageUse.Sampled, "cull pyramid stand-in"));
        if (s.Prog.IsNull || s.ProgSort.IsNull || s.Ubo.IsNull || s.Pyr.IsNull)
        {
            s.Failed = true;
            log.Always("CullPassVk: the device REFUSED the cull's pipelines or constants (logged above)");
            return false;
        }
        if (!DeviceCheck(vk, log))
        {
            s.Failed = true;
            log.Always("CullPassVk: the device's cull DISAGREES with the reference draw list (DeviceCheck above)");
            return false;
        }
        return true;
    }

    private static PipelineId Compute(VkBackend vk, string src, string label)
    {
        var d = new PipelineDesc { Cs = vk.CreateShader(new ShaderDesc(ShaderStage.Compute, null, src, label)), Label = label };
        return vk.CreatePipeline(in d);
    }

    /// <summary>Upload the draw list and spheres, then both phases' dispatches, into <paramref name="cl"/>. False
    /// when the device refused a buffer (logged).</summary>
    private static bool Cull(VkBackend vk, CommandList cl, int groups, int drawN, CullSrcRec[] src, Sphere[] sph,
                             int sphereSlots, ref CullBlock b)
    {
        var s = _s;
        ReadOnlySpan<byte> srcBytes = MemoryMarshal.AsBytes(src.AsSpan(0, drawN));
        ReadOnlySpan<byte> sphBytes = MemoryMarshal.AsBytes(sph.AsSpan(0, sphereSlots));
        bool growCmd = drawN > s.CmdCap, growCnt = groups > s.CntCap;
        Grow(vk, ref s.Src, ref s.SrcCap, srcBytes.Length, "cull source");
        Grow(vk, ref s.Sph, ref s.SphCap, sphBytes.Length, "cull bounds");
        const BufferUse Out = BufferUse.Storage | BufferUse.Indirect | BufferUse.CopySrc;
        for (int p = 0; p < Phases; p++)
        {
            Make(vk, cl, ref s.Cmd[p], growCmd, (long)drawN * CullPass.CmdUints * sizeof(uint), Out, "cull commands");
            Make(vk, cl, ref s.Slot[p], growCmd, (long)drawN * sizeof(uint), Out, "cull slots");
            Make(vk, cl, ref s.Cnt[p], growCnt, (long)groups * sizeof(uint), Out, "cull counts");
        }
        Make(vk, cl, ref s.Key, growCmd, (long)drawN * sizeof(float), BufferUse.Storage, "cull sort keys");
        Make(vk, cl, ref s.Rej, growCmd, (long)drawN * sizeof(uint), BufferUse.Storage, "cull hi-Z verdicts");
        if (s.Src.IsNull || s.Sph.IsNull || s.Key.IsNull || s.Rej.IsNull) return false;
        for (int p = 0; p < Phases; p++) if (s.Cmd[p].IsNull || s.Slot[p].IsNull || s.Cnt[p].IsNull) return false;
        if (growCmd) s.CmdCap = drawN;
        if (growCnt) s.CntCap = groups;
        cl.UpdateBuffer(s.Src, 0, srcBytes);
        cl.UpdateBuffer(s.Sph, 0, sphBytes);

        int groups1D = (drawN + CullPass.GroupSize - 1) / CullPass.GroupSize;
        Span<byte> blk = stackalloc byte[Std140.BlockBytes<CullBlock>()];
        blk.Clear();
        ReadOnlySpan<BufferBinding> ubo = [new(UboSlots.Cull, s.Ubo)];
        ReadOnlySpan<ImageBinding> pyr = [new(CullPass.PyramidUnit, s.Pyr)];
        for (int p = 0; p < Phases; p++)
        {
            b.Rej = p == Prepass ? CullPass.RejWrite : CullPass.RejOff;
            MemoryMarshal.Write(blk, in b);
            cl.ZeroBuffer(s.Cnt[p], 0, (long)groups * sizeof(uint));
            cl.UpdateBuffer(s.Ubo, 0, blk);
            ReadOnlySpan<BufferBinding> storage =
            [
                new(CullPass.BindSrc, s.Src), new(CullPass.BindSphere, s.Sph), new(CullPass.BindCmd, s.Cmd[p]),
                new(CullPass.BindOutSlot, s.Slot[p]), new(CullPass.BindCount, s.Cnt[p]), new(CullPass.BindKey, s.Key),
                new(CullPass.BindRej, s.Rej),
            ];
            cl.SetPipeline(s.Prog);
            cl.BindSet(0, new BindSet(ubo, storage, pyr, default));
            cl.Dispatch(groups1D);
            // the rank reads keys every invocation of the cull wrote: the backend's barrier after a dispatch orders it
            if (b.CompactBelow >= groups) continue;
            ReadOnlySpan<BufferBinding> sortStorage =
            [
                new(CullPass.BindSrc, s.Src), new(CullPass.BindCmd, s.Cmd[p]), new(CullPass.BindOutSlot, s.Slot[p]),
                new(CullPass.BindKey, s.Key),
            ];
            cl.SetPipeline(s.ProgSort);
            cl.BindSet(0, new BindSet(ubo, sortStorage, default, default));
            cl.Dispatch(groups1D);
        }
        return true;
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

    /// <summary>An upload storage buffer at least <paramref name="bytes"/> long, remade larger when it is not.</summary>
    private static void Grow(VkBackend vk, ref BufferId id, ref long cap, long bytes, string label)
    {
        if (!id.IsNull && bytes <= cap) return;
        vk.Destroy(id);
        cap = Math.Max(bytes, 4);
        id = vk.CreateBuffer(new BufferDesc(cap, BufferUse.Storage, MemoryKind.Upload, label));
        if (id.IsNull) cap = 0;
    }

    /// <summary>The cull on this device over a fixed ten-draw list, read back and held against <see cref="CullPass"/>'s
    /// parity fixture. Oracle: the hand-counted survivors, slots and order that fixture asserts, written out again here.</summary>
    private static bool DeviceCheck(VkBackend vk, ExtenderLog log)
    {
        var check = new Checker(log, "CullPassVk.DeviceCheck");
        const int drawN = 10, groups = 3;
        ReadOnlySpan<int> start = [0, 0, 0, 3, 3, 3, 6, 6, 6, 6];
        var src = new CullSrcRec[drawN];
        var sph = new Sphere[drawN];
        for (int n = 0; n < drawN; n++)
        {
            CullPass.Pack(ref src[n], 30 + n, 2 * n, n, n, Math.Min(n / 3, groups - 1), start[n]);
            sph[n] = new Sphere { Center = new Vector3(0f, 0f, -10f), Radius = 1f };
        }
        sph[1].Center.X = 500f;
        sph[4].Radius = AlloyRenderer.NeverCull;
        sph[5].Radius = 0.001f;
        sph[7].Center.Z = -30f;
        sph[8].Center.Z = -30f;
        sph[9].Center.X = 500f;
        Matrix4x4 vp = Matrix4x4.CreateLookAt(Vector3.Zero, new Vector3(0, 0, -10), Vector3.UnitY)
                     * Matrix4x4.CreatePerspectiveFieldOfView(1.0f, 1.0f, 1f, 100f);
        var b = new CullBlock { DrawN = drawN, Vp = vp, PyrVp = vp, ClipZ = NoClipPlane, CompactBelow = 2 };
        CullPass.Planes(in vp, b.Planes, Vector3.Zero);
        var cnt = new uint[groups];
        var slot = new int[drawN];
        var cmd = new DrawCmdRec[drawN];
        if (!Run(vk, check, src, sph, ref b, cnt, slot, cmd)) return false;

        check.Expect(cnt[0] == 2 && cnt[1] == 3 && cnt[2] == 3,
            $"survivors per batch are 2, 3, 3 (got {cnt[0]}, {cnt[1]}, {cnt[2]}): the off-frustum draws 1 and 9 are dropped, nothing else");
        check.Expect((slot[0], slot[1]) is (0, 2) or (2, 0), "batch 0 compacts draws 0 and 2, in either order");
        check.Expect(Same(slot.AsSpan(3, 3), [3, 4, 5]), "batch 1 keeps draws 3, 4 and 5, in any order");
        check.Expect(slot[6] == 7 && slot[7] == 8 && slot[8] == 6 && slot[9] == 9,
            $"the sorted batch is 7, 8, 6 farthest first with the tie on the lower index, and 9 pads it (got {slot[6]}, {slot[7]}, {slot[8]}, {slot[9]})");
        check.Expect(cmd[9].InstanceCount == 0u && cmd[6].InstanceCount == 1u && cmd[7].InstanceCount == 1u && cmd[8].InstanceCount == 1u,
            "the dropped sorted draw is a zero-instance command behind three single-instance survivors");
        bool ranges = true;
        foreach (int o in (ReadOnlySpan<int>)[0, 1, 3, 4, 5, 6, 7, 8, 9])
        {
            int d = slot[o];
            ranges &= d >= 0 && d < drawN && cmd[o].Count == (uint)(30 + d) && cmd[o].FirstIndex == (uint)d
                   && cmd[o].BaseVertex == (uint)d && cmd[o].BaseInstance == 0u;
        }
        check.Expect(ranges, "every command is its own draw's count 30+n, first index n, base vertex n");

        // LOD: 540 px per unit at unit depth (1080 rows, 90 degrees), 1 px threshold -- the speck goes, NeverCull stays
        b.LodPx = 1f; b.LodScale = 540f;
        if (!Run(vk, check, src, sph, ref b, cnt, slot, cmd)) return false;
        check.Expect(cnt[1] == 2 && Same(slot.AsSpan(3, 2), [3, 4]), $"a sub-pixel draw is LOD-culled and 3, 4 survive (got {cnt[1]})");
        check.Expect(cnt[2] == 3, "...and nothing in the sorted batch, which runs no LOD test");
        log.Info(check.Ok ? "CullPassVk.DeviceCheck: passed" : "CullPassVk.DeviceCheck: FAILED -- see above");
        return check.Ok;
    }

    /// <summary>One fixture submit, then the colour set read back into the three arrays.</summary>
    private static bool Run(VkBackend vk, Checker check, CullSrcRec[] src, Sphere[] sph, ref CullBlock b,
                            uint[] cnt, int[] slot, DrawCmdRec[] cmd)
    {
        CommandList cl = _s.Cmds!;
        cl.Reset();
        bool ran = Cull(vk, cl, cnt.Length, src.Length, src, sph, sph.Length, ref b) && vk.Submit(cl);
        check.Expect(ran, "the fixture's submit runs");
        bool read = ran
            && vk.ReadBack(_s.Cnt[Colour], MemoryMarshal.AsBytes(cnt.AsSpan()))
            && vk.ReadBack(_s.Slot[Colour], MemoryMarshal.AsBytes(slot.AsSpan()))
            && vk.ReadBack(_s.Cmd[Colour], MemoryMarshal.AsBytes(cmd.AsSpan()));
        check.Expect(!ran || read, "the colour set reads back");
        return read;
    }

    /// <summary>The same values in any order: a compacting batch's survivors land in atomic order.</summary>
    private static bool Same(Span<int> got, ReadOnlySpan<int> want)
    {
        Span<int> g = stackalloc int[got.Length];
        got.CopyTo(g);
        g.Sort();
        return g.SequenceEqual(want);
    }
}
