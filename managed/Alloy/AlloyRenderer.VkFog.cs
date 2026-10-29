using System;
using System.Numerics;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The volumetric fog (<see cref="PassId.FogVolume"/>) on Vulkan: <see cref="FogVolume"/>'s two compute stages over
/// the froxel grid, the sum bound wherever a stage declares <c>uVFogVol</c>. Built on the async queue with the froxel
/// build it reads, beside the prepass through the G-buffer pass, which fogs through last frame's sum: the two sums take turns.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The fog's stages and volumes on one device, replaced whole with the device.</summary>
    private sealed class VkFogState
    {
        public VkBackend? Dev;
        public VkDefStage? Light, Sum;
        public FogVolume.LightLocs L;
        public FogVolume.GridLocs S;
        public int SunMedia = -1, LocalMedia = -1;
        public bool Tried, Live;
        public ImageId MedA, MedB, SumA, SumB;
        /// <summary>The sum this frame's pass wrote, or last frame's until it runs.</summary>
        public ImageId Total;
        public int W, H, D;
        /// <summary>This frame's volume is built, so its readers may sample it.</summary>
        public bool Built;
    }

    private static VkFogState _vkFog = new();

    /// <summary>This frame's summed volume, or the null id where none was built.</summary>
    internal static ImageId VkFogVolume => _vkFog.Built ? _vkFog.Total : default;

    /// <summary>The volume into <paramref name="cl"/>, ahead of the first stage that fogs. Nothing (every reader on the
    /// ramp) when the mode is off, the view is a capture, or a stage was refused.</summary>
    private static void VkFogPass(VkBackend vk, CommandList cl, in Matrix4x4 view, in Matrix4x4 vpClean, int* viewport,
                                  float near, float far, bool main, bool lightingOn, ExtenderLog log)
    {
        VkFogState s = _vkFog;
        if (s.Dev != vk) _vkFog = s = new VkFogState { Dev = vk };
        s.Built = false;
        FogVolume.NotBuilt();
        if (!VolumetricFog.Enabled.V || !VkFogEnsure(vk, s, log)) return;
        if (!FogVolume.Prepare(view, vpClean, viewport, far, main && !_capturing, lightingOn, out FogVolume.Terms t)) return;
        if (!VkFogVolumes(vk, s, t.W, t.H, t.D, log)) return;
        bool a = FogVolume.CurrentIsA;
        ImageId cur = a ? s.MedA : s.MedB, prev = a ? s.MedB : s.MedA;
        s.Total = a ? s.SumA : s.SumB;
        int gx = (t.W + FogVolume.VkGroup - 1) / FogVolume.VkGroup, gy = (t.H + FogVolume.VkGroup - 1) / FogVolume.VkGroup;

        // both stages' blocks staged, then the arena range they took sent ahead of the first dispatch reading it
        VkColourState c = _vkc;
        int arena0 = c.ArenaUsed;
        VkDefStage l = s.Light!, m = s.Sum!;
        Array.Clear(l.Block);
        LooseBlock.Capture(l.Members, l.Block);
        try
        {
            FogVolume.WriteLight(in t, in s.L, viewport, near, far);
            LooseBlock.Set(s.SunMedia, 1f);
            LooseBlock.Set(s.LocalMedia, 1f);
        }
        finally { LooseBlock.EndCapture(); }
        int loose = VkArena(l.Block), pass = VkArenaPass();
        Array.Clear(m.Block);
        LooseBlock.Capture(m.Members, m.Block);
        try { FogVolume.WriteSum(in t, in s.S); }
        finally { LooseBlock.EndCapture(); }
        int looseSum = VkArena(m.Block);
        if (loose < 0 || pass < 0 || looseSum < 0) return;
        int len = (c.ArenaUsed - arena0 + 3) & ~3;
        cl.UpdateBuffer(c.Arena, arena0, c.ArenaBytes.AsSpan(arena0, Math.Min(len, ArenaCap - arena0)));

        ReadOnlySpan<ImageId> hist = [prev];
        cl.ReadImages(hist);
        cl.SetPipeline(l.Pipe);
        VkDefBindSet(cl, l, cur, default, loose, pass, c.Slots, prev);
        cl.Dispatch(gx, gy, t.D);
        cl.SetPipeline(m.Pipe);
        VkDefBindSet(cl, m, cur, s.Total, looseSum, pass, c.Slots);
        cl.Dispatch(gx, gy, 1);
        ReadOnlySpan<ImageId> sum = [s.Total];
        cl.ReadImages(sum);

        FogVolume.Finish(in t, vpClean);
        s.Built = true;
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan volumetric fog is LIVE -- {t.W}x{t.H}x{t.D} froxels, in-scatter then the integral");
        }
    }

    /// <summary>Both stages, made once per device; a refusal is said once and every reader stays on the ramp.</summary>
    private static bool VkFogEnsure(VkBackend vk, VkFogState s, ExtenderLog log)
    {
        if (s.Tried) return s.Light != null && s.Sum != null;
        s.Tried = true;
        s.Light = VkDefBuild(vk, FogVolume.ShdLightCs, null, "vk vfog light", log);
        s.Sum = VkDefBuild(vk, FogVolume.ShdSumCs, null, "vk vfog integrate", log);
        if (s.Light == null || s.Sum == null)
        {
            log.Always("AlloyRenderer: a Vulkan volumetric fog stage was REFUSED (logged above) -- the fog stays the ramp this session");
            return false;
        }
        VkFogState f = s;
        LooseBlock.Resolve(s.Light.Names, () =>
        {
            f.L = FogVolume.LightLocsOf();
            f.SunMedia = LooseBlock.Loc("uSunMedia");
            f.LocalMedia = LooseBlock.Loc("uLocalMedia");
        });
        LooseBlock.Resolve(s.Sum.Names, () => f.S = FogVolume.GridLocsOf());
        return true;
    }

    /// <summary>The three volumes at this grid; a new grid is new storage and starts without history.</summary>
    private static bool VkFogVolumes(VkBackend vk, VkFogState s, int w, int h, int d, ExtenderLog log)
    {
        if (!s.SumA.IsNull && s.W == w && s.H == h && s.D == d) return true;
        vk.Destroy(s.MedA); vk.Destroy(s.MedB); vk.Destroy(s.SumA); vk.Destroy(s.SumB);
        s.MedA = Volume(vk, w, h, d, "vk vfog medium a");
        s.MedB = Volume(vk, w, h, d, "vk vfog medium b");
        s.SumA = Volume(vk, w, h, d, "vk vfog sum a");
        s.SumB = Volume(vk, w, h, d, "vk vfog sum b");
        s.Total = default;
        s.W = w; s.H = h; s.D = d;
        FogVolume.Revoke();
        if (!s.MedA.IsNull && !s.MedB.IsNull && !s.SumA.IsNull && !s.SumB.IsNull) return true;
        log.Always($"AlloyRenderer: a {w}x{h}x{d} Vulkan fog volume was REFUSED (logged above) -- the fog stays the ramp");
        s.SumA = default;
        return false;

        static ImageId Volume(VkBackend vk, int w, int h, int d, string label)
            => vk.CreateImage(new ImageDesc(ImageKind.Tex3D, Format.Rgba16F, w, h, d, 1, 1, 1,
                                            ImageUse.Sampled | ImageUse.Storage | ImageUse.Async, label));
    }

    /// <summary>Every image the async froxel and fog stages sample that a graphics pass may too, handed over for sampling
    /// in the submit they wait on: the async queue may not move one that the G-buffer pass beside it reads.</summary>
    private static void VkHandOverAsync(CommandList cl)
    {
        VkColourState s = _vkc;
        ReadOnlySpan<ImageId> shared =
        [
            CasterPass.VkCascadeMap, CasterPass.VkSunDynMap, CasterPass.VkCubeMap, CasterPass.VkCubeDynMap,
            s.Tex2D, s.TexU2D, s.Tex2DArray, s.Tex3D, s.TexCube, s.TexCubeArray, s.Depth2DArray, s.DepthCubeArray,
        ];
        cl.ReadImages(shared);
    }
}
