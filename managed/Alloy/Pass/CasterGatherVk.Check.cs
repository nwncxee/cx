using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static unsafe partial class CasterGatherVk
{
    /// <summary>Casters the fixture stages, and the batches they fall in.</summary>
    private const int FixtureDraws = 6, FixtureGroups = 2;

    /// <summary>The fixture view's reach and extent along the light, chosen so the cull's two directional tests
    /// each reject exactly one staged caster.</summary>
    private const float FixtureReach = 5f, FixtureAlong = 20f;

    /// <summary>Both dispatches on this device over a fixed six-caster list, read back and held against counts,
    /// commands and tallies worked out by hand. Oracle: the hand-counted survivors below -- one caster off the
    /// light axis, one past its extent, one admitted only through the caster-sphere exemption, one only by the
    /// dynamic half of the set filter -- with the detail level and the layered command packing counted the same way.</summary>
    private static bool DeviceCheck(VkBackend vk, ExtenderLog log)
    {
        var check = new Checker(log, "CasterGatherVk.DeviceCheck");
        var src = new CasterSrcRec[FixtureDraws];
        var cull = new Sphere[FixtureDraws];
        var cast = new Sphere[FixtureDraws];
        for (int n = 0; n < FixtureDraws; n++)
        {
            src[n] = new CasterSrcRec
            {
                IndexCount = (uint)(30 + 3 * n), FirstIndex = (uint)n, BaseVertex = (uint)(2 * n), Slot = (uint)n,
                Batch = (uint)(n / 3), OutBase = (uint)(n / 3 * 3),
            };
            cull[n] = new Sphere { Center = new Vector3(0f, 0f, -10f), Radius = 1f };
            cast[n] = cull[n];
        }
        // caster 1 is far off the light axis; caster 5 is past the fit's extent along it
        cull[1].Center.X = 500f; cast[1].Center.X = 500f;
        cull[5].Center.Z = -40f; cast[5].Center.Z = -40f;
        // caster 2's colour-cull bound is exempt, so the cull has to fall back to its geometric caster sphere
        cull[2].Radius = AlloyRenderer.NeverCull;
        // caster 4 is this frame's mover: the static half of the set filter drops it and the dynamic half keeps it
        src[4].SetFlag = 1u;
        // ...and caster 0 has two coarse runs, so the detail pick has something to choose
        src[0].Count1 = 12u; src[0].First1 = 100u;
        src[0].Count2 = 6u; src[0].First2 = 200u;

        var cnt = new uint[FixtureGroups + CasterGather.CountExtra];
        var cmd = new DrawCmdRec[FixtureDraws * 2];
        bool ok = true;

        ok &= Fixture(vk, check, src, cull, cast, CasterGather.SetStatic, 1, 0f, 0f, cnt, cmd);
        check.Expect(!ok || cnt[0] == 2 && cnt[1] == 1,
            $"the static half admits casters 0 and 2 in batch 0 and caster 3 in batch 1 -- 1 is off axis, 5 past the "
          + $"extent, 4 a mover (got {cnt[0]}, {cnt[1]})");
        check.Expect(!ok || cmd[0].InstanceCount == 1u && cmd[3].Count == src[3].IndexCount
                     && cmd[3].FirstIndex == src[3].FirstIndex && cmd[3].BaseVertex == src[3].BaseVertex
                     && cmd[3].BaseInstance == 3u,
            "batch 1's one command is caster 3's own range, one instance, with its ownership slot as the base instance");
        check.Expect(!ok || cnt[CasterGather.TriWord(FixtureGroups, 0)] == (30u + 36u + 39u) / 3u,
            $"every survivor bakes its base run, so the level-0 triangle tally is their index counts over three "
          + $"(got {cnt[CasterGather.TriWord(FixtureGroups, 0)]})");

        ok &= Fixture(vk, check, src, cull, cast, CasterGather.SetDynamic, 1, 0f, 0f, cnt, cmd);
        check.Expect(!ok || cnt[0] == 0 && cnt[1] == 1 && cmd[3].BaseInstance == 4u,
            $"the dynamic half admits caster 4 alone (got {cnt[0]}, {cnt[1]})");

        ok &= Fixture(vk, check, src, cull, cast, CasterGather.SetAll, 0, 0f, 0f, cnt, cmd);
        check.Expect(!ok || cnt[0] == 3 && cnt[1] == 3,
            $"with the cull waived every staged caster of every batch is admitted (got {cnt[0]}, {cnt[1]})");

        // the detail pick: caster 0's bound of 1 clears the first threshold and not the second, so it bakes its
        // middle run -- 12 indices at index 100, four triangles
        ok &= Fixture(vk, check, src, cull, cast, CasterGather.SetStatic, 1, 2f, 0.5f, cnt, cmd);
        check.Expect(!ok || cmd[0].Count == 12u && cmd[0].FirstIndex == 100u || cmd[1].Count == 12u && cmd[1].FirstIndex == 100u,
            "the caster whose bound clears the first threshold bakes its level-1 run, at that run's own first index");
        check.Expect(!ok || cnt[CasterGather.TriWord(FixtureGroups, 1)] == 4u,
            $"...and its four triangles tally at level 1 (got {cnt[CasterGather.TriWord(FixtureGroups, 1)]})");

        ok &= Layered(vk, check, src, cull, cast, cnt, cmd);
        log.Info(check.Ok ? "CasterGatherVk.DeviceCheck: passed" : "CasterGatherVk.DeviceCheck: FAILED -- see above");
        return check.Ok;
    }

    /// <summary>One per-view fixture submit: the list staged, one dispatch run with <paramref name="cullOn"/>
    /// overriding the live dial, and its counts and commands read back.</summary>
    private static bool Fixture(VkBackend vk, Checker check, CasterSrcRec[] src, Sphere[] cull, Sphere[] cast,
                                int set, int cullOn, float lodR1, float lodR2, uint[] cnt, DrawCmdRec[] cmd)
    {
        CommandList cl = _s.Cmds!;
        cl.Reset();
        if (!Stage(vk, cl, src, cull, cast)) { check.Expect(false, "the fixture's list stages"); return false; }
        _s.At = (_s.At + 1) % Rotation;
        CasterGather.View(ref _s.Block, FixtureDraws, FixtureGroups, Vector3.Zero, new Vector3(0f, 0f, -1f),
                          FixtureReach, FixtureAlong, false, lodR1, lodR2, set, false, cullOn);
        cl.ZeroBuffer(_s.Cnt[_s.At], 0, CountBytes(FixtureGroups));
        Block(cl);
        cl.SetPipeline(_s.Prog);
        Bind(cl, _s.Src, _s.Cmd[_s.At], _s.Cnt[_s.At], default);
        cl.Dispatch((FixtureDraws + GroupSize - 1) / GroupSize);
        return Read(vk, check, _s.Cnt[_s.At], _s.Cmd[_s.At], cnt, cmd, FixtureDraws);
    }

    /// <summary>The layered dispatch over two copies of the same view: the batch regions scale by the view count
    /// and each survivor's command carries its view index above the ownership slot.</summary>
    private static bool Layered(VkBackend vk, Checker check, CasterSrcRec[] src, Sphere[] cull, Sphere[] cast,
                                uint[] cnt, DrawCmdRec[] cmd)
    {
        const int viewN = 2;
        var views = new CasterViewRec[viewN];
        for (int v = 0; v < viewN; v++)
            views[v] = new CasterViewRec
            {
                Vp = Matrix4x4.Identity, Focus = Vector3.Zero, Reach = FixtureReach,
                Dir = new Vector3(0f, 0f, -1f), Along = FixtureAlong, Set = CasterGather.SetStatic, Layer = v,
            };
        CommandList cl = _s.Cmds!;
        cl.Reset();
        if (!Stage(vk, cl, src, cull, cast)) { check.Expect(false, "the layered fixture's list stages"); return false; }
        bool ran;
        fixed (CasterViewRec* vs = views) ran = DispatchLayer(vk, cl, vs, viewN, false, viewN, cull: 1);
        check.Expect(ran, "the layered fixture's dispatch records");
        if (!ran) return false;
        if (!Read(vk, check, _s.LyCnt[_s.LyAt], _s.LyCmd[_s.LyAt], cnt, cmd, FixtureDraws * viewN)) return false;
        check.Expect(cnt[0] == 4 && cnt[1] == 2,
            $"two views of the static half admit the same three casters each, counted over both views (got {cnt[0]}, {cnt[1]})");
        int tagged = 0, plain = 0;
        for (int i = 0; i < 4; i++)
            if ((cmd[i].BaseInstance >> CasterGather.ViewShift) == 1u) tagged++;
            else if (cmd[i].BaseInstance >> CasterGather.ViewShift == 0u) plain++;
        check.Expect(tagged == 2 && plain == 2,
            $"batch 0's four commands are two per view, each tagged with its own view index (got {tagged} of view 1, {plain} of view 0)");
        check.Expect((cmd[6].BaseInstance & ((1u << CasterGather.ViewShift) - 1u)) == 3u,
            "batch 1's region starts at its staged base scaled by the view count, and its command still names caster 3");
        return true;
    }

    /// <summary>The fixture's bounds and source into <paramref name="cl"/>, sized for it.</summary>
    private static bool Stage(VkBackend vk, CommandList cl, CasterSrcRec[] src, Sphere[] cull, Sphere[] cast)
    {
        fixed (Sphere* c = cull) fixed (Sphere* g = cast) RecordSpheres(vk, cl, c, g, cull.Length);
        _s.SrcDirty = true;
        bool ok;
        fixed (CasterSrcRec* s = src) ok = Begin(vk, cl, src.Length, FixtureGroups, s, true);
        return ok;
    }

    /// <summary>Submit the fixture's list and read its counts and commands back.</summary>
    private static bool Read(VkBackend vk, Checker check, BufferId cntBuf, BufferId cmdBuf, uint[] cnt,
                             DrawCmdRec[] cmd, int cmds)
    {
        bool ran = vk.Submit(_s.Cmds!);
        check.Expect(ran, "the fixture's submit runs");
        Array.Clear(cmd);
        bool read = ran && vk.ReadBack(cntBuf, MemoryMarshal.AsBytes(cnt.AsSpan()))
                        && vk.ReadBack(cmdBuf, MemoryMarshal.AsBytes(cmd.AsSpan(0, cmds)));
        check.Expect(!ran || read, "the fixture's counts and commands read back");
        return read;
    }

}
