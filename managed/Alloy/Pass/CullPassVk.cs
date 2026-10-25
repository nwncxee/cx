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
/// into a list <see cref="VkBackend"/> runs at the frame's cull site. The prepass and colour lists are both cut, as
/// GL cuts them; until the Vulkan prepass exists there is no depth to reduce, so both are frustum, LOD and fog only.
/// </summary>
internal static unsafe class CullPassVk
{
    /// <summary>Everything on one device, replaced whole when the device is (context loss drops it).</summary>
    private sealed class CullVkState
    {
        public VkBackend? Dev;
        public CommandList? Cmds;
        public PipelineId Prog, ProgSort;
        /// <summary>The pipelines did not build or the on-device check disagreed: Alloy has already failed, loudly.</summary>
        public bool Failed, Live;
        public BufferId Ubo, Src, Sph, Key, Rej;
        public long SrcCap, SphCap;
        public int CmdCap, CntCap;
        /// <summary>Indexed by <see cref="CullPass.PhasePrepass"/> and <see cref="CullPass.PhaseColour"/>.</summary>
        public readonly BufferId[] Cmd = new BufferId[Phases], Slot = new BufferId[Phases], Cnt = new BufferId[Phases];
        /// <summary>A 1x1 stand-in on the pyramid's slot: the cull stage names the sampler whether or not it
        /// tests occlusion, and Vulkan wants every descriptor a pipeline uses written.</summary>
        public ImageId Pyr;
    }

    private static CullVkState _s = new();

    /// <summary>The output sets this port cuts: the prepass's and the colour pass's.</summary>
    private const int Phases = 2;
    /// <summary>Room for the blocks and binds of both phases; the source and sphere uploads grow it once, to their high water.</summary>
    private const int ArenaBytes = 64 * 1024;

    /// <summary>Cull this frame's draw list into the prepass and colour sets. A refusal fails Alloy loudly: the
    /// frame has no other visibility test, and a Vulkan frame that drew unculled would hide the bug.</summary>
    internal static void Run(ExtenderLog log, int groups, int drawN, CullSrcRec* src, Sphere* sph, int sphereSlots,
                             float lodScale, int compactBelow, Vector3 eye, Matrix4x4* vp)
    {
        if (CullPass.Mode <= CullPass.ModeOff || drawN <= 0 || groups <= 0) return;
        if (AlloyDevice.Vulkan(log) is not VkBackend vk || !Ensure(vk, log)) return;
        CullBlock b = Block(drawN, vp, eye, lodScale, compactBelow);
        if (!Cull(vk, groups, drawN, src, sph, sphereSlots, ref b))
        {
            _s.Failed = true;
            AlloyGate.Fail("the Vulkan cull's submit was refused (logged above) -- Alloy has no visibility test");
            return;
        }
        if (_s.Live) return;
        _s.Live = true;
        log.Always($"CullPassVk: LIVE -- {drawN} draws in {groups} batches culled on Vulkan (frustum, LOD, fog; "
                 + "hi-Z waits for the Vulkan prepass depth)");
    }

    /// <summary>The terms this frame's dispatch reads, as the GL pass stages them minus the pyramid's.</summary>
    private static CullBlock Block(int drawN, Matrix4x4* vp, Vector3 eye, float lodScale, int compactBelow)
    {
        var b = new CullBlock
        {
            DrawN = drawN, Vp = *vp, PyrVp = *vp, LodPx = MathF.Max(CullPass.LodPx, 0f), LodScale = lodScale,
            ClipZ = -1e30f, CompactBelow = compactBelow, Eye = new Vector4(eye, 0f),
        };
        CullPass.Planes(in *vp, b.Planes, eye);
        float opaque = CullPass.FogCull ? SceneFog.OpaqueDistance() : float.PositiveInfinity;
        b.FogCull = float.IsInfinity(opaque) ? 0f : opaque;
        Vector3 origin = SceneFog.Origin(out bool radial);
        b.FogOrigin = new Vector4(origin, radial ? 1f : 0f);
        return b;
    }

    /// <summary>The device's pipelines and constant resources, then the on-device check, once per device.</summary>
    private static bool Ensure(VkBackend vk, ExtenderLog log)
    {
        if (_s.Dev != vk) _s = new CullVkState { Dev = vk };
        var s = _s;
        if (s.Failed) return false;
        if (!s.Prog.IsNull) return true;
        s.Cmds = new CommandList("vulkan cull", ArenaBytes, log);
        s.Prog = Compute(vk, CullPass.CullCs, "CullPassVk frustum cull");
        s.ProgSort = Compute(vk, CullPass.SortCs, "CullPassVk back-to-front rank sort");
        s.Ubo = vk.CreateBuffer(new BufferDesc(Std140.BlockBytes<CullBlock>(), BufferUse.Uniform, MemoryKind.Upload, "cull block"));
        s.Pyr = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, 1, 1, 1, 1, 1, 1, ImageUse.Sampled, "cull pyramid stand-in"));
        if (s.Prog.IsNull || s.ProgSort.IsNull || s.Ubo.IsNull || s.Pyr.IsNull)
        {
            s.Failed = true;
            AlloyGate.Fail("the Vulkan cull's pipelines did not build (logged above) -- Alloy has no visibility test");
            return false;
        }
        if (!DeviceCheck(vk, log))
        {
            s.Failed = true;
            AlloyGate.Fail("the Vulkan cull disagrees with the reference draw list (CullPassVk.DeviceCheck above)");
            return false;
        }
        return true;
    }

    private static PipelineId Compute(VkBackend vk, string src, string label)
    {
        var d = new PipelineDesc { Cs = vk.CreateShader(new ShaderDesc(ShaderStage.Compute, null, src, label)), Label = label };
        return vk.CreatePipeline(in d);
    }

    /// <summary>Upload the draw list and spheres, then both phases' dispatches, in one submit. False when the
    /// device refused a buffer or the submit (logged).</summary>
    private static bool Cull(VkBackend vk, int groups, int drawN, CullSrcRec* src, Sphere* sph, int sphereSlots, ref CullBlock b)
    {
        var s = _s;
        CommandList cl = s.Cmds!;
        cl.Reset();
        long srcBytes = (long)drawN * sizeof(CullSrcRec), sphBytes = (long)sphereSlots * sizeof(Sphere);
        bool growCmd = drawN > s.CmdCap, growCnt = groups > s.CntCap;
        Grow(vk, ref s.Src, ref s.SrcCap, srcBytes, BufferUse.Storage, MemoryKind.Upload, "cull source");
        Grow(vk, ref s.Sph, ref s.SphCap, sphBytes, BufferUse.Storage, MemoryKind.Upload, "cull bounds");
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
        cl.UpdateBuffer(s.Src, 0, new ReadOnlySpan<byte>(src, (int)srcBytes));
        cl.UpdateBuffer(s.Sph, 0, new ReadOnlySpan<byte>(sph, (int)sphBytes));

        int groups1D = (drawN + CullPass.GroupSize - 1) / CullPass.GroupSize;
        Span<byte> blk = stackalloc byte[Std140.BlockBytes<CullBlock>()];
        ReadOnlySpan<BufferBinding> ubo = [new(UboSlots.Cull, s.Ubo)];
        ReadOnlySpan<ImageBinding> pyr = [new(CullPass.PyramidUnit, s.Pyr)];
        for (int p = 0; p < Phases; p++)
        {
            b.Rej = p == CullPass.PhasePrepass ? CullPass.RejWrite : CullPass.RejOff;
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
        return vk.Submit(cl);
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
    private static void Grow(VkBackend vk, ref BufferId id, ref long cap, long bytes, BufferUse use,
                             MemoryKind mem, string label)
    {
        if (!id.IsNull && bytes <= cap) return;
        vk.Destroy(id);
        cap = Math.Max(bytes, 4);
        id = vk.CreateBuffer(new BufferDesc(cap, use, mem, label));
        if (id.IsNull) cap = 0;
    }

    /// <summary>The cull on this device over a fixed ten-draw list, read back and held against <see cref="CullPass"/>'s
    /// parity fixture. Oracle: the hand-counted survivors, slots and order that fixture asserts, written out again here.</summary>
    private static bool DeviceCheck(VkBackend vk, ExtenderLog log)
    {
        var check = new Checker(log, "CullPassVk.DeviceCheck");
        const int drawN = 10, groups = 3, compactBelow = 2;
        ReadOnlySpan<int> start = [0, 0, 0, 3, 3, 3, 6, 6, 6, 6];
        CullSrcRec* src = stackalloc CullSrcRec[drawN];
        Sphere* sph = stackalloc Sphere[drawN];
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
        var b = new CullBlock { DrawN = drawN, Vp = vp, PyrVp = vp, ClipZ = -1e30f, CompactBelow = compactBelow };
        CullPass.Planes(in vp, b.Planes, Vector3.Zero);
        check.Expect(Cull(vk, groups, drawN, src, sph, drawN, ref b), "the fixture's submit runs");
        uint* cnt = stackalloc uint[groups];
        int* slot = stackalloc int[drawN];
        DrawCmdRec* cmd = stackalloc DrawCmdRec[drawN];
        int c = CullPass.PhaseColour;
        bool read = check.Ok
            && vk.ReadBack(_s.Cnt[c], new Span<byte>(cnt, groups * sizeof(uint)))
            && vk.ReadBack(_s.Slot[c], new Span<byte>(slot, drawN * sizeof(int)))
            && vk.ReadBack(_s.Cmd[c], new Span<byte>(cmd, drawN * sizeof(DrawCmdRec)));
        check.Expect(read, "the colour set reads back");
        if (!read) return false;
        check.Expect(cnt[0] == 2 && cnt[1] == 3 && cnt[2] == 3,
            $"survivors per batch are 2, 3, 3 (got {cnt[0]}, {cnt[1]}, {cnt[2]}): the off-frustum draws 1 and 9 are dropped, nothing else");
        check.Expect((slot[0] == 0 && slot[1] == 2) || (slot[0] == 2 && slot[1] == 0), "batch 0 compacts draws 0 and 2, in either order");
        check.Expect(slot[3] + slot[4] + slot[5] == 12 && slot[3] * slot[4] * slot[5] == 60, "batch 1 keeps draws 3, 4 and 5");
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
        log.Info(check.Ok ? "CullPassVk.DeviceCheck: passed" : "CullPassVk.DeviceCheck: FAILED -- see above");
        return check.Ok;
    }
}
