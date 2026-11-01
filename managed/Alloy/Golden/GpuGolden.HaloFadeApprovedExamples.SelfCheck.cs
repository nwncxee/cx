using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The halo fade golden (approved-spec, E1-E5): the shipped <c>cx_haloocc_c</c> over a one-texel depth copy, a light
/// 10 m ahead with a 1 m soft range, each case's fade slot read back. Oracle: the soft test and the 3-per-second walk
/// worked by hand -- a clear light climbs by the step, a covered one falls by it, a 40% visible one settles at 0.4.
/// </summary>
internal static partial class GpuGolden
{
    /// <summary>One case: the scene's view distance in the depth copy, the light, its lead flag, the slot's fade before
    /// the dispatch, the step, and the slot's fade after it.</summary>
    private readonly record struct HaloFadeCase(string Name, float Scene, Vector3 Light, bool Lead, float Was, float Step, float Want);

    /// <summary>The fixture's near plane: reversed depth stores near / distance.</summary>
    private const float HaloNear = 0.1f;

    private static readonly HaloFadeCase[] HaloFadeCases =
    [
        new("E1 a clear light climbs by the step", 100f, new Vector3(0f, 0f, -10f), true, 0f, 0.3f, 0.3f),
        new("E2 a covered light falls by it", 5f, new Vector3(0f, 0f, -10f), true, 1f, 0.3f, 0.7f),
        new("E3 a 40% visible light settles at 0.4", 9.4f, new Vector3(0f, 0f, -10f), true, 0f, 3f, 0.4f),
        new("E4 a light off screen holds", 100f, new Vector3(20f, 0f, -10f), true, 0.6f, 0.3f, 0.6f),
        new("E4 a light behind the camera holds", 100f, new Vector3(0f, 0f, 10f), true, 0.6f, 0.3f, 0.6f),
        new("E5 a trailing element leaves its slot alone", 100f, new Vector3(0f, 0f, -10f), false, 0.25f, 0.3f, 0.25f),
    ];

    private static void HaloFadeSelfCheck(VkBackend vk, Checker check, ExtenderLog log)
    {
        PipelineId pipe = vk.CreateCompute(ShaderSource.Get(Halos.OccCs + ".glsl"), "golden halo fade");
        check.Expect(!pipe.IsNull, "the halo fade golden's stage builds for the device");
        if (pipe.IsNull) return;
        // identity view; a reversed-depth projection, GL's -1 in (3,4), near 0.1 in (4,3)
        var proj = new Matrix4x4(1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, -1f, 0f, 0f, HaloNear, 0f);
        foreach (HaloFadeCase c in HaloFadeCases)
        {
            float? got = HaloFadeRun(vk, log, pipe, c, Halos.OccPush(Matrix4x4.Identity, proj, 1, c.Step));
            check.Expect(got != null, $"halo fade, {c.Name}: the fixture, dispatch or readback was refused (logged above)");
            if (got != null) Near(check, $"halo fade, {c.Name}", got.Value, c.Want, 1e-4);
        }
        log.Info($"GpuGolden: halo fade, {HaloFadeCases.Length} cases");
        vk.Destroy(pipe);
    }

    /// <summary>One case through <paramref name="pipe"/>: its record on slot 1 (slot 0 a decoy at 0.5), the depth copy
    /// one texel at the case's scene distance; slot 1's fade after the dispatch, null when a step was refused.</summary>
    private static float? HaloFadeRun(VkBackend vk, ExtenderLog log, PipelineId pipe, in HaloFadeCase c, Halos.OccConsts push)
    {
        var rec = new HaloRec { Pos = c.Light, Softness = 1f, Probe = new Vector2(c.Lead ? 1f : 0f, 1f) };
        ReadOnlySpan<float> fades = [0.5f, c.Was];
        ReadOnlySpan<float> depth = [HaloNear / c.Scene];
        BufferId recs = vk.CreateBuffer(new BufferDesc(64, BufferUse.Storage | BufferUse.CopyDst, MemoryKind.Device, "golden halo recs"));
        BufferId fade = vk.CreateBuffer(new BufferDesc(16, BufferUse.Storage | BufferUse.CopyDst | BufferUse.CopySrc,
                                                       MemoryKind.Device, "golden halo fades"));
        ImageId img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.R32F, 1, 1, 1, 1, 1, 1,
                                                   ImageUse.Sampled | ImageUse.CopyDst, "golden halo depth"));
        float? got = null;
        if (!recs.IsNull && !fade.IsNull && !img.IsNull && vk.Upload(img, 0, 0, MemoryMarshal.AsBytes(depth)))
        {
            var cl = new CommandList("golden halo fade", 1 << 12, log);
            cl.UpdateBuffer(recs, 0, MemoryMarshal.AsBytes(new ReadOnlySpan<HaloRec>(in rec)));
            cl.UpdateBuffer(fade, 0, MemoryMarshal.AsBytes(fades));
            cl.ReadImages([img]);
            cl.SetPipeline(pipe);
            ReadOnlySpan<BufferBinding> ssbo = [new(SsboSlots.Halos, recs), new(SsboSlots.HaloFade, fade)];
            cl.BindSet(0, new BindSet(default, ssbo, [new ImageBinding(0, img)], default));
            cl.PushConstants(0, MemoryMarshal.AsBytes(new ReadOnlySpan<Halos.OccConsts>(in push)));
            cl.Dispatch(1);
            var back = new float[4];
            if (vk.Submit(cl) && vk.ReadBack(fade, MemoryMarshal.AsBytes(back.AsSpan())))
            {
                got = back[1];
                if (back[0] != 0.5f) log.Always($"GpuGolden: halo fade, {c.Name}: the decoy slot 0 moved to {back[0]:G6}");
                got = back[0] == 0.5f ? got : float.NaN;
            }
        }
        vk.Destroy(img);
        vk.Destroy(fade);
        vk.Destroy(recs);
        return got;
    }
}
