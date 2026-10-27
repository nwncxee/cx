using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// <see cref="CullPass"/> on Vulkan: the same compute stages over the same source records and spheres, in GL's order --
/// the prepass set against last frame's pyramid (marking what it rejected), the pyramid rebuilt from the prepass
/// depth, the marked draws retested against it, then the colour set against it.
/// </summary>
internal static class CullPassVk
{
    /// <summary>Everything on one device, replaced whole when the device is (context loss drops it), plus the
    /// inputs the cull site staged for this frame's recording.</summary>
    private sealed class CullVkState
    {
        public VkBackend? Dev;
        public CommandList? Cmds;
        public PipelineId Prog, ProgSort, ProgHiZFirst, ProgHiZ;
        /// <summary>The pipelines did not build or the on-device check disagreed: Alloy has already failed, loudly.</summary>
        public bool Failed, Live;
        /// <summary>This frame's lists were recorded: the passes draw from them rather than from the staged order.</summary>
        public bool Ran;
        public BufferId Ubo, Src, Sph, Key, Rej;
        public long SrcCap, SphCap;
        public int CmdCap, CntCap;
        /// <summary>Indexed by <see cref="Prepass"/>, <see cref="Colour"/> and <see cref="Retest"/>.</summary>
        public readonly BufferId[] Cmd = new BufferId[Phases], Slot = new BufferId[Phases], Cnt = new BufferId[Phases];
        /// <summary>A 1x1 stand-in on the pyramid's slot while no pyramid is built: the cull stage names the sampler
        /// whether or not it tests occlusion, and Vulkan wants every descriptor a pipeline uses written.</summary>
        public ImageId Stand;
        /// <summary>The max-depth pyramid (R32F, half the depth's size, down to 1x1), the view-projection its depth
        /// was drawn under, and whether it is this frame's (<see cref="Fresh"/>) or a prior frame's (<see cref="Stale"/>).</summary>
        public ImageId Pyr;
        /// <summary>The pyramid a resize replaced: this frame's prepass cull bound it before the rebuild, so its id
        /// must resolve until this frame's list has run. Destroyed at the next build.</summary>
        public ImageId Retired;
        public int PyrW, PyrH, PyrLevels;
        public Matrix4x4 PyrVp;
        public bool Stale, Fresh, RetestPending;
        /// <summary>This frame's list, staged at the cull site; <see cref="Groups"/> 0 means nothing to cut.</summary>
        public CullSrcRec[]? Records;
        public Sphere[]? Spheres;
        public int Groups, DrawN, SphereSlots, RunGroups;
        public CullBlock Block;
    }

    private static CullVkState _s = new();

    /// <summary>This port's output sets: the prepass's (which writes the hi-Z verdicts), the colour pass's, and the
    /// retest's (the prepass's stale rejections the fresh pyramid clears). GL's numbering.</summary>
    private const int Prepass = CullPass.PhasePrepass, Colour = CullPass.PhaseColour, Retest = CullPass.PhaseRetest, Phases = 3;

    /// <summary>The sets a pass draws.</summary>
    internal const int PhasePrepass = Prepass, PhaseColour = Colour, PhaseRetest = Retest;

    /// <summary>The prepass cull marked draws the stale pyramid rejected: the frame retests them once it is rebuilt.</summary>
    internal static bool RetestPending => _s.RetestPending;

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

    /// <summary>Record the staged list's upload and the prepass set into <paramref name="cl"/>, ahead of anything
    /// that reads them; the colour set waits for <see cref="RecordColour"/>. False (logged) when the device refused
    /// the pass: the frame has no other visibility test, so the caller fails Alloy.</summary>
    internal static bool Record(VkBackend vk, CommandList cl, ExtenderLog log)
    {
        if (!Ensure(vk, log)) return false;
        var s = _s;
        int groups = s.Groups;
        s.Groups = 0;
        s.Ran = s.Fresh = s.RetestPending = false;
        if (groups <= 0 || s.Records == null || s.Spheres == null) return true;
        if (!Upload(vk, cl, groups, s.DrawN, s.Records, s.Spheres, s.SphereSlots)) return false;
        s.RunGroups = groups;
        Phase(cl, Prepass, groups, ref s.Block, Occludes && CullPass.Retest);
        s.Ran = true;
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"CullPassVk: LIVE -- {s.DrawN} draws in {groups} batches culled on Vulkan (frustum, LOD, fog, "
                     + (CullPass.Mode >= CullPass.ModeHiZ ? "hi-Z off the Vulkan prepass depth with the retest)" : "hi-Z off by alloy.cull)"));
        }
        return true;
    }

    /// <summary>The retest set into <paramref name="cl"/>: what the stale pyramid rejected, against the one
    /// <see cref="BuildHiZ"/> just made. Only when <see cref="RetestPending"/>.</summary>
    internal static void RecordRetest(CommandList cl)
    {
        var s = _s;
        if (s.Ran && s.RetestPending) Phase(cl, Retest, s.RunGroups, ref s.Block, Occludes);
    }

    /// <summary>The colour set into <paramref name="cl"/>, against this frame's pyramid when one was built.</summary>
    internal static void RecordColour(CommandList cl)
    {
        var s = _s;
        if (s.Ran) Phase(cl, Colour, s.RunGroups, ref s.Block, Occludes);
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
        s.ProgHiZFirst = Compute(vk, HiZCs.Replace(VersionLine, VersionLine + "#define CX_HIZ_FIRST\n"), "CullPassVk hi-Z first level");
        s.ProgHiZ = Compute(vk, HiZCs, "CullPassVk hi-Z level");
        s.Stand = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, 1, 1, 1, 1, 1, 1, ImageUse.Sampled, "cull pyramid stand-in"));
        if (s.Prog.IsNull || s.ProgSort.IsNull || s.ProgHiZFirst.IsNull || s.ProgHiZ.IsNull || s.Ubo.IsNull || s.Stand.IsNull)
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

    private static string HiZCs => ShaderSource.Get("cx_hiz_c.glsl");

    /// <summary>The first line of every embedded stage; a variant's define goes right after it.</summary>
    private const string VersionLine = "#version 460 core\n";

    /// <summary>The pyramid's local size, as <c>cx_hiz_c.glsl</c> declares it.</summary>
    private const int HiZGroup = 8;

    private static PipelineId Compute(VkBackend vk, string src, string label)
    {
        var d = new PipelineDesc { Cs = vk.CreateShader(new ShaderDesc(ShaderStage.Compute, null, src, label)), Label = label };
        return vk.CreatePipeline(in d);
    }

    /// <summary>Upload the draw list and spheres into <paramref name="cl"/>, every phase's sets sized for it. False
    /// when the device refused a buffer (logged).</summary>
    private static bool Upload(VkBackend vk, CommandList cl, int groups, int drawN, CullSrcRec[] src, Sphere[] sph, int sphereSlots)
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
        return true;
    }

    /// <summary><c>alloy.cull</c> asks for the occlusion test.</summary>
    private static bool Occludes => CullPass.Mode >= CullPass.ModeHiZ;

    /// <summary>One phase's dispatches into <paramref name="cl"/>: the
    /// prepass tests the prior pyramid in its own clip space and marks its rejects, the retest reads those marks,
    /// and the retest and colour sets test this frame's pyramid when there is one and <paramref name="occlude"/>.</summary>
    private static void Phase(CommandList cl, int p, int groups, ref CullBlock b, bool occlude)
    {
        var s = _s;
        bool hiZ = occlude && !s.Pyr.IsNull && (p == Prepass ? s.Stale : s.Fresh);
        if (p == Prepass) s.RetestPending = hiZ;
        b.Rej = p == Prepass ? CullPass.RejWrite : p == Retest ? CullPass.RejRead : CullPass.RejOff;
        b.HiZ = hiZ ? 1 : 0;
        Matrix4x4 vp = b.Vp;
        b.PyrVp = hiZ && p == Prepass ? s.PyrVp : vp;
        b.PyrSize = new Vector2(s.PyrW, s.PyrH);
        b.MaxLod = Math.Max(s.PyrLevels - 1, 0);
        int groups1D = (b.DrawN + CullPass.GroupSize - 1) / CullPass.GroupSize;
        Span<byte> blk = stackalloc byte[Std140.BlockBytes<CullBlock>()];
        blk.Clear();
        MemoryMarshal.Write(blk, in b);
        ReadOnlySpan<BufferBinding> ubo = [new(UboSlots.Cull, s.Ubo)];
        ReadOnlySpan<ImageBinding> pyr = [new(CullPass.PyramidUnit, hiZ ? s.Pyr : s.Stand)];
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
        // the rank reads keys every invocation of the cull wrote: the backend's barrier after a dispatch orders it.
        // The retest places no sorted draw: the prepass never draws them and the stage returns early on each.
        if (b.CompactBelow >= groups || p == Retest) return;
        ReadOnlySpan<BufferBinding> sortStorage =
        [
            new(CullPass.BindSrc, s.Src), new(CullPass.BindCmd, s.Cmd[p]), new(CullPass.BindOutSlot, s.Slot[p]),
            new(CullPass.BindKey, s.Key),
        ];
        cl.SetPipeline(s.ProgSort);
        cl.BindSet(0, new BindSet(ubo, sortStorage, default, default));
        cl.Dispatch(groups1D);
    }

    /// <summary>The max-depth pyramid from <paramref name="depth"/> (<paramref name="w"/> x <paramref name="h"/>, drawn
    /// under the staged view-projection) into <paramref name="cl"/>: half
    /// size, each texel the max of the 3x3 under it. It is this frame's for the retest and colour sets and next frame's
    /// stale one for its prepass. Nothing when the cull did not run or hi-Z is off.</summary>
    internal static void BuildHiZ(VkBackend vk, CommandList cl, ImageId depth, int w, int h)
    {
        var s = _s;
        vk.Destroy(s.Retired);
        s.Retired = default;
        if (!s.Ran || !Occludes || !EnsurePyramid(vk, w / 2, h / 2)) return;
        Reduce(cl, depth);
        s.Fresh = true;
        s.Stale = true;
        s.PyrVp = s.Block.Vp;
    }

    /// <summary>Every level of the pyramid from <paramref name="src"/>: level 0 off its sampled view, each next level
    /// off the one above through their storage views.</summary>
    private static void Reduce(CommandList cl, ImageId src)
    {
        var s = _s;
        for (int i = 0; i < s.PyrLevels; i++)
        {
            int lw = Math.Max(s.PyrW >> i, 1), lh = Math.Max(s.PyrH >> i, 1);
            ReadOnlySpan<ImageBinding> sampled = i == 0 ? [new(0, src)] : default;
            ReadOnlySpan<ImageBinding> images = i == 0 ? [new(0, s.Pyr, default, 0)] : [new(0, s.Pyr, default, i), new(1, s.Pyr, default, i - 1)];
            cl.SetPipeline(i == 0 ? s.ProgHiZFirst : s.ProgHiZ);
            cl.BindSet(0, new BindSet(default, default, sampled, images));
            cl.Dispatch((lw + HiZGroup - 1) / HiZGroup, (lh + HiZGroup - 1) / HiZGroup);
        }
    }

    /// <summary>The pyramid at <paramref name="w"/> x <paramref name="h"/>, remade (and no longer stale) on a resize.</summary>
    private static bool EnsurePyramid(VkBackend vk, int w, int h)
    {
        var s = _s;
        w = Math.Max(w, 1); h = Math.Max(h, 1);
        if (!s.Pyr.IsNull && w == s.PyrW && h == s.PyrH) return true;
        s.Retired = s.Pyr;
        s.Stale = false;
        int levels = 1;
        while ((w >> levels) > 0 || (h >> levels) > 0) levels++;
        s.Pyr = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, w, h, 1, levels, 1, 1,
                                             ImageUse.Sampled | ImageUse.Storage, "cull hi-Z pyramid"));
        s.PyrW = s.Pyr.IsNull ? 0 : w; s.PyrH = s.Pyr.IsNull ? 0 : h; s.PyrLevels = s.Pyr.IsNull ? 0 : levels;
        return !s.Pyr.IsNull;
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
        b.LodPx = 0f; b.LodScale = 0f;
        if (!Occlusion(vk, check, src, sph, ref b)) return false;
        log.Info(check.Ok ? "CullPassVk.DeviceCheck: passed" : "CullPassVk.DeviceCheck: FAILED -- see above");
        return check.Ok;
    }

    /// <summary>One fixture submit, then the colour set read back into the three arrays.</summary>
    private static bool Run(VkBackend vk, Checker check, CullSrcRec[] src, Sphere[] sph, ref CullBlock b,
                            uint[] cnt, int[] slot, DrawCmdRec[] cmd)
    {
        CommandList cl = _s.Cmds!;
        cl.Reset();
        bool ran = Upload(vk, cl, cnt.Length, src.Length, src, sph, sph.Length);
        if (ran) { Phase(cl, Prepass, cnt.Length, ref b, false); Phase(cl, Colour, cnt.Length, ref b, false); }
        ran = ran && vk.Submit(cl);
        check.Expect(ran, "the fixture's submit runs");
        bool read = ran
            && vk.ReadBack(_s.Cnt[Colour], MemoryMarshal.AsBytes(cnt.AsSpan()))
            && vk.ReadBack(_s.Slot[Colour], MemoryMarshal.AsBytes(slot.AsSpan()))
            && vk.ReadBack(_s.Cmd[Colour], MemoryMarshal.AsBytes(cmd.AsSpan()));
        check.Expect(!ran || read, "the colour set reads back");
        return read;
    }

    /// <summary>The fixture's source depth: 64x64, reduced to a 32x32 pyramid.</summary>
    private const int FixtureDepth = 64;

    /// <summary>The pyramid and the retest on this device. Oracle: hand-worked verdicts for the fixture's spheres
    /// (every one at least 9 units out, so its nearest depth is past 0.5) against a depth of all 0 (a wall at the
    /// near plane hides all but the never-culled draw), and all 1 save one 0 texel (a max keeps the 1s: nothing hides).</summary>
    private static bool Occlusion(VkBackend vk, Checker check, CullSrcRec[] src, Sphere[] sph, ref CullBlock b)
    {
        var s = _s;
        ImageId depth = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, FixtureDepth, FixtureDepth, 1, 1, 1, 1,
                                                     ImageUse.Sampled | ImageUse.CopyDst, "cull hi-Z fixture depth"));
        check.Expect(!depth.IsNull && EnsurePyramid(vk, FixtureDepth / 2, FixtureDepth / 2), "the fixture's depth and pyramid exist");
        if (!check.Ok) { vk.Destroy(depth); return false; }
        var wall = new float[FixtureDepth * FixtureDepth];
        var open = new float[wall.Length];
        Array.Fill(open, 1f);
        open[FixtureDepth * FixtureDepth / 2 + FixtureDepth / 2] = 0f;
        int groups = 3;
        var cnt = new uint[groups];
        var slot = new int[src.Length];
        bool ok = true;

        ok &= Fixture(vk, check, depth, wall, src, sph, ref b, Colour, false, cnt, slot);
        check.Expect(!ok || cnt[0] == 0 && cnt[1] == 1 && slot[3] == 4 && cnt[2] == 0,
            $"behind a wall at the near plane only the never-culled draw 4 survives (got {cnt[0]}, {cnt[1]}, {cnt[2]})");
        ok &= Fixture(vk, check, depth, open, src, sph, ref b, Colour, false, cnt, slot);
        check.Expect(!ok || cnt[0] == 2 && cnt[1] == 3 && cnt[2] == 3,
            $"with one near texel in an open depth the max hides nothing: 2, 3, 3 survive (got {cnt[0]}, {cnt[1]}, {cnt[2]})");

        ok &= Fixture(vk, check, depth, wall, src, sph, ref b, Prepass, true, cnt, slot);
        check.Expect(!ok || cnt[0] == 0 && cnt[1] == 1 && cnt[2] == 3 && s.RetestPending,
            $"against a stale wall the prepass keeps draw 4 and the untested sorted batch, and asks for a retest (got {cnt[0]}, {cnt[1]}, {cnt[2]})");
        ok &= Fixture(vk, check, depth, open, src, sph, ref b, Retest, false, cnt, slot);
        check.Expect(!ok || cnt[0] == 2 && cnt[1] == 2 && cnt[2] == 0 && Same(slot.AsSpan(0, 2), [0, 2]) && Same(slot.AsSpan(3, 2), [3, 5]),
            $"the retest against the open pyramid draws exactly the stale rejects 0, 2, 3, 5 (got {cnt[0]}, {cnt[1]}, {cnt[2]})");

        s.Fresh = s.Stale = s.RetestPending = false;
        vk.Destroy(depth);
        return ok;
    }

    /// <summary>One occlusion fixture submit: <paramref name="texels"/> into the depth, reduced into the pyramid (as the
    /// prior frame's when <paramref name="stale"/>), phase <paramref name="p"/> run, its set read back.</summary>
    private static bool Fixture(VkBackend vk, Checker check, ImageId depth, float[] texels, CullSrcRec[] src, Sphere[] sph,
                                ref CullBlock b, int p, bool stale, uint[] cnt, int[] slot)
    {
        var s = _s;
        CommandList cl = _s.Cmds!;
        cl.Reset();
        bool ran = vk.Upload(depth, 0, 0, MemoryMarshal.AsBytes(texels.AsSpan())) && Upload(vk, cl, cnt.Length, src.Length, src, sph, sph.Length);
        if (ran)
        {
            Reduce(cl, depth);
            s.Fresh = !stale; s.Stale = stale; s.PyrVp = b.Vp;
            Phase(cl, p, cnt.Length, ref b, true);
        }
        ran = ran && vk.Submit(cl);
        check.Expect(ran, $"the occlusion fixture's phase {p} submit runs");
        bool read = ran && vk.ReadBack(s.Cnt[p], MemoryMarshal.AsBytes(cnt.AsSpan())) && vk.ReadBack(s.Slot[p], MemoryMarshal.AsBytes(slot.AsSpan()));
        check.Expect(!ran || read, $"phase {p}'s set reads back");
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
