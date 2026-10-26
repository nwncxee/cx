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
/// The weather's surface maps (<see cref="PassId.RainCover"/>'s consumers) on Vulkan, recorded after the caster bake
/// and ahead of every pass that reads them: the cover layer copied out of the sun array, its filtered moments, the
/// wetness map's latch and integrate, the shelter cache, the noise and ripple tiles and the print field -- the stages
/// GL runs, with <see cref="Weather"/> making every decision. Each map binds where a stage names it.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The surface stages and maps on one device, replaced whole with the device.</summary>
    private sealed class VkWxSurfState
    {
        public VkBackend? Dev;
        public bool Tried, Failed, Live;
        public VkDefStage? Scat, Int, Shel, Noise, Rip;
        public GuiVkProg? Esm;
        public int EsmStep = -1, EsmFirst = -1;
        public ImageId CoverDepth, Esm0, Esm1, Map, ShelTex, NoiseTex, RipTex, Track;
        public int CoverSize, EsmSize, MapSize, ShelSize;
        /// <summary>The noise tile was written last frame and owes its mips, which an upload runs ahead of the next submit.</summary>
        public bool NoiseMips;
        public SamplerId Clamp, Tile, Print;
    }

    private static VkWxSurfState _vkWxs = new();

    /// <summary>Workgroup side of every surface stage's <c>local_size</c>.</summary>
    private const int VkWxGroup = 8;

    /// <summary>The surface maps into <paramref name="cl"/>; nothing while the weather is off, interior or refused.</summary>
    private static void VkWeatherSurface(VkBackend vk, CommandList cl, ExtenderLog log)
    {
        VkWxSurfState s = _vkWxs;
        if (s.Dev != vk) { _vkWxs = s = new VkWxSurfState { Dev = vk }; Weather.VkReset(); }
        if (!Weather.SurfacesVk || !VkWxsEnsure(vk, s, log)) return;
        int bake = Weather.CoverToFilterVk(out int mapSize, out int layer);
        if (bake >= 0) VkWxFilter(vk, cl, s, bake, mapSize, layer, log);
        VkWxMap(vk, cl, s, bake >= 0, log);
        if (Weather.ShelterStaleVk) VkWxShelter(vk, cl, s, Weather.ShelterSizeVk(s.CoverSize));
        VkWxTiles(vk, cl, s);
        VkWxTrack(vk, s);
        if (!s.Live && bake >= 0)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan weather surfaces are LIVE -- the cover layer filtered at {s.EsmSize}px, "
                     + $"the wetness map at {s.MapSize}px, the shelter cache at {s.ShelSize}px");
        }
    }

    /// <summary>The stages, the filter program and the samplers, made once per device; a refusal is said once and
    /// the lit stage keeps its fallbacks (a hard cover compare, the per-fragment wetness, no ripples).</summary>
    private static bool VkWxsEnsure(VkBackend vk, VkWxSurfState s, ExtenderLog log)
    {
        if (s.Tried) return !s.Failed;
        s.Tried = true;
        s.Scat = VkDefBuild(vk, "cx_wx_scat_c", null, "vk weather map scatter", log);
        s.Int = VkDefBuild(vk, "cx_wx_int_c", null, "vk weather map integrate", log);
        s.Shel = VkDefBuild(vk, "cx_wx_shel_c", null, "vk weather shelter cache", log);
        s.Noise = VkDefBuild(vk, "cx_wx_noise_c", null, "vk weather noise tile", log);
        s.Rip = VkDefBuild(vk, "cx_wx_ripl_c", null, "vk weather ripple tile", log);
        if (PostChainVk.Ensure(vk, log))
            s.Esm = PostChainVk.Program(vk, PostChain.FullscreenVs, Weather.EsmFs, "weather cover filter", p =>
            {
                s.EsmStep = p.Loc("uStep"); s.EsmFirst = p.Loc("uFirst");
            }, log);
        s.Clamp = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Nearest, Wrap.Clamp));
        s.Tile = vk.CreateSampler(new SamplerDesc(Filter.Linear, Filter.Linear, MipMode.Linear, Wrap.Repeat));
        s.Print = vk.CreateSampler(new SamplerDesc(Filter.Nearest, Filter.Nearest, MipMode.Nearest, Wrap.Repeat));
        s.Failed = s.Scat == null || s.Int == null || s.Shel == null || s.Noise == null || s.Rip == null || s.Esm == null;
        if (s.Failed)
            log.Always("AlloyRenderer: a Vulkan weather surface stage was REFUSED (logged above) -- no filtered cover, wetness "
                     + "map, shelter cache or ripples on Vulkan this session; the lit stage keeps its per-fragment fallbacks");
        return !s.Failed;
    }

    /// <summary>The cover layer out of the sun array into a plain depth image, then the two blur axes of its moments --
    /// <see cref="Weather"/>'s filter -- and the bake latched into the wetness map.</summary>
    private static void VkWxFilter(VkBackend vk, CommandList cl, VkWxSurfState s, int bake, int mapSize, int layer, ExtenderLog log)
    {
        ImageId sun = CasterPass.VkCascadeMap;
        int es = Weather.EsmSizeFor(mapSize);
        if (s.CoverSize != mapSize || s.CoverDepth.IsNull)
        {
            vk.Destroy(s.CoverDepth);
            s.CoverDepth = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.D32F, mapSize, mapSize, 1, 1, 1, 1,
                                                        ImageUse.Sampled | ImageUse.CopyDst, "vk weather cover depth"));
            s.CoverSize = mapSize;
        }
        if (s.EsmSize != es || s.Esm0.IsNull)
        {
            vk.Destroy(s.Esm0); vk.Destroy(s.Esm1);
            s.Esm0 = VkWxImage(vk, Format.Rg32F, es, ImageUse.ColorTarget, "vk weather cover moments 0");
            s.Esm1 = VkWxImage(vk, Format.Rg32F, es, ImageUse.ColorTarget, "vk weather cover moments 1");
            s.EsmSize = es;
            log.Info($"AlloyRenderer: the Vulkan cover filter is {es}x{es} RG32F");
        }
        if (sun.IsNull || s.CoverDepth.IsNull || s.Esm0.IsNull || s.Esm1.IsNull) { Weather.FilteredVk(bake, false); return; }
        cl.CopyImage(new CopyImageCmd(sun, new ImageRegion(0, 0, 0, layer), s.CoverDepth, new ImageRegion(0, 0, 0, 0), mapSize, mapSize, 1));
        ReadOnlySpan<ImageId> read = [sun, s.CoverDepth];
        cl.ReadImages(read);
        PostChainVk.BeginFrame();
        Rect r = new(0, 0, es, es);
        PostChainVk.Open(s.Esm!);
        Weather.WriteEsmVk(s.EsmStep, s.EsmFirst, true);
        int at = PostChainVk.Close(cl, s.Esm!);
        PostChainVk.Draw(cl, s.Esm!, Format.Rg32F, PostChainVk.Blend.None, s.Esm0, LoadOp.DontCare, r, at, new PostChainVk.Inputs(s.CoverDepth), log);
        PostChainVk.Open(s.Esm!);
        Weather.WriteEsmVk(s.EsmStep, s.EsmFirst, false);
        at = PostChainVk.Close(cl, s.Esm!);
        PostChainVk.Draw(cl, s.Esm!, Format.Rg32F, PostChainVk.Blend.None, s.Esm1, LoadOp.DontCare, r, at, new PostChainVk.Inputs(s.Esm0), log);
        ReadOnlySpan<ImageId> esm = [s.Esm1];
        cl.ReadImages(esm);
        Weather.FilteredVk(bake, true);
    }

    /// <summary>The wetness map's step: made or cleared on an area edge, latched from a new cover bake, integrated on
    /// its cadence -- <see cref="Weather.MapStepVk"/>'s decisions, in GL's order.</summary>
    private static void VkWxMap(VkBackend vk, CommandList cl, VkWxSurfState s, bool latch, ExtenderLog log)
    {
        int step = Weather.MapStepVk(out int size, out float dt);
        if (size > 0 && (s.MapSize != size || s.Map.IsNull))
        {
            vk.Destroy(s.Map);
            s.Map = VkWxImage(vk, Format.Rgba32F, size, ImageUse.Storage, "vk weather wetness map");
            s.MapSize = size;
            if (s.Map.IsNull) { Weather.MapRefusedVk(); return; }
        }
        if (s.Map.IsNull) return;
        if (step != 0)
        {
            VkDefStage st = s.Int!;
            int loose;
            VkWxOpen(st);
            try { Weather.WriteIntegrateVk(st.Names, dt, step == 1); }
            finally { loose = VkWxClose(cl, st); }
            if (loose >= 0) VkWxDispatch(cl, st, loose, s.Map, default, (size + VkWxGroup - 1) / VkWxGroup, (size + VkWxGroup - 1) / VkWxGroup);
        }
        if (!latch || s.CoverDepth.IsNull) return;
        VkDefStage sc = s.Scat!;
        int x0, y0, w, h, at;
        bool ok;
        VkWxOpen(sc);
        try { ok = Weather.WriteScatterVk(sc.Names, out x0, out y0, out w, out h); }
        finally { at = VkWxClose(cl, sc); }
        if (at >= 0 && ok) VkWxDispatch(cl, sc, at, s.Map, s.CoverDepth, (w + VkWxGroup - 1) / VkWxGroup, (h + VkWxGroup - 1) / VkWxGroup);
    }

    /// <summary>The shelter disk and snow drift, baked once per cover bake (or knob move) from the plain cover depth.</summary>
    private static void VkWxShelter(VkBackend vk, CommandList cl, VkWxSurfState s, int size)
    {
        if (size <= 0 || s.CoverDepth.IsNull) return;
        if (s.ShelSize != size || s.ShelTex.IsNull)
        {
            vk.Destroy(s.ShelTex);
            s.ShelTex = VkWxImage(vk, Format.Rgba16F, size, ImageUse.Storage, "vk weather shelter cache");
            s.ShelSize = size;
            if (s.ShelTex.IsNull) return;
        }
        VkDefStage st = s.Shel!;
        bool ok;
        int at;
        VkWxOpen(st);
        try { ok = Weather.WriteShelterVk(st.Names, size); }
        finally { at = VkWxClose(cl, st); }
        if (at < 0 || !ok) return;
        VkWxDispatch(cl, st, at, s.ShelTex, s.CoverDepth, (size + VkWxGroup - 1) / VkWxGroup, (size + VkWxGroup - 1) / VkWxGroup);
        Weather.ShelteredVk();
    }

    /// <summary>The noise tile once per device (its mips the frame after), and this frame's ripple tile while it rains.</summary>
    private static void VkWxTiles(VkBackend vk, CommandList cl, VkWxSurfState s)
    {
        if (s.NoiseMips) { vk.GenerateMips(s.NoiseTex); s.NoiseMips = false; Weather.NoiseDoneVk(); }
        else if (Weather.NoiseOwedVk && s.NoiseTex.IsNull)
        {
            int n = Weather.NoiseSizeVk;
            s.NoiseTex = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba16F, n, n, 1, BitOperations.Log2((uint)n) + 1, 1, 1,
                                                      ImageUse.Sampled | ImageUse.Storage, "vk weather noise tile"));
            VkDefStage st = s.Noise!;
            int at = -1;
            if (!s.NoiseTex.IsNull)
            {
                VkWxOpen(st);
                try { Weather.WriteNoiseVk(st.Names); }
                finally { at = VkWxClose(cl, st); }
            }
            if (at >= 0)
            {
                VkWxDispatch(cl, st, at, s.NoiseTex, default, n / VkWxGroup, n / VkWxGroup);
                s.NoiseMips = true;
            }
        }
        float rate = Weather.RippleRateVk;
        if (rate <= 0f) return;
        int rs = Weather.RippleSizeVk;
        if (s.RipTex.IsNull) s.RipTex = VkWxImage(vk, Format.Rg16F, rs, ImageUse.Storage, "vk weather ripple tile");
        if (s.RipTex.IsNull) return;
        VkDefStage rp = s.Rip!;
        int r;
        VkWxOpen(rp);
        try { Weather.WriteRippleVk(rp.Names, rate); }
        finally { r = VkWxClose(cl, rp); }
        if (r >= 0)
            VkWxDispatch(cl, rp, r, s.RipTex, default, rs / VkWxGroup, rs / VkWxGroup);
    }

    /// <summary>The print field, uploaded whole whenever a print landed since the last frame.</summary>
    private static void VkWxTrack(VkBackend vk, VkWxSurfState s)
    {
        bool fresh = false;
        if (s.Track.IsNull)
        {
            int t = Weather.TrackSizeVk;
            if (Weather.TrackDirtyVk(false).IsEmpty) return;
            s.Track = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba32F, t, t, 1, 1, 1, 1, ImageUse.Sampled, "vk weather prints"));
            if (s.Track.IsNull) return;
            fresh = true;
        }
        ReadOnlySpan<TrackTexel> field = Weather.TrackDirtyVk(fresh);
        if (!field.IsEmpty) vk.Upload(s.Track, 0, 0, MemoryMarshal.AsBytes(field));
    }

    /// <summary>Open a capture into <paramref name="st"/>'s cleared loose block.</summary>
    private static void VkWxOpen(VkDefStage st)
    {
        Array.Clear(st.Block);
        VkUniformShim.Capture(st.Members, st.Block);
    }

    /// <summary>Close <see cref="VkWxOpen"/>'s capture, append the block to the arena and send it ahead of the
    /// dispatch that reads it (no pass flush uploads these); its offset, or -1 when the arena is full.</summary>
    private static int VkWxClose(CommandList cl, VkDefStage st)
    {
        VkUniformShim.EndCapture();
        int at = VkArena(st.Block);
        if (at >= 0) cl.UpdateBuffer(_vkc.Arena, at, st.Block);
        return at;
    }

    /// <summary>One dispatch writing <paramref name="target"/> (storage binding 0), sampling <paramref name="cover"/>
    /// where the stage names <c>uCoverDepth</c>, then handed over for every later read.</summary>
    private static void VkWxDispatch(CommandList cl, VkDefStage st, int loose, ImageId target, ImageId cover, int gx, int gy)
    {
        VkColourState c = _vkc;
        VkWxSurfState s = _vkWxs;
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<ImageBinding> sampled = stackalloc ImageBinding[4];
        Span<ImageBinding> images = stackalloc ImageBinding[2];
        int nu = 0, nt = 0, ni = 0;
        foreach (VkOpaque o in st.Opaques)
        {
            if (o.Set != 0) continue;
            switch (o.Kind)
            {
                case VkBindKind.UniformBlock:
                    if (nu < ubo.Length)
                        ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, loose, st.Block.Length)
                                                                : new BufferBinding(o.Binding, c.ZeroUbo);
                    break;
                case VkBindKind.StorageImage:
                    if (ni < images.Length) images[ni++] = new ImageBinding(o.Binding, target);
                    break;
                case VkBindKind.StorageBlock:
                    break;
                default:
                    if (nt < sampled.Length)
                        sampled[nt++] = o.Name == "uCoverDepth" && !cover.IsNull ? new ImageBinding(o.Binding, cover, s.Clamp)
                                                                                 : new ImageBinding(o.Binding, c.Tex2D, c.Linear);
                    break;
            }
        }
        cl.SetPipeline(st.Pipe);
        cl.BindSet(0, new BindSet(ubo[..nu], default, sampled[..nt], images[..ni]));
        cl.Dispatch(gx, gy);
        ReadOnlySpan<ImageId> done = [target];
        cl.ReadImages(done);
    }

    /// <summary>A square map image, sampled and written as <paramref name="use"/> says.</summary>
    private static ImageId VkWxImage(VkBackend vk, Format f, int size, ImageUse use, string label)
        => vk.CreateImage(new ImageDesc(ImageKind.Tex2D, f, size, size, 1, 1, 1, 1, ImageUse.Sampled | use, label));

    /// <summary>The surface map a stage's sampler names, with the sampler GL gives it; false for any other name or a
    /// map this frame does not hold -- the stage's own gate (a zero term) then keeps it from reading the stand-in.</summary>
    private static bool VkWxSampled(string name, out ImageId img, out SamplerId smp)
    {
        VkWxSurfState s = _vkWxs;
        (img, smp) = name switch
        {
            "uCoverEsm" => (s.Esm1, s.Clamp),
            "uWxMap" => (s.Map, s.Clamp),
            "uWxShelter" => (s.ShelTex, s.Clamp),
            "uWxNoise" => (Weather.NoiseOwedVk ? default : s.NoiseTex, s.Tile),
            "uWxRipple" => (s.RipTex, s.Tile),
            "uWxTrack" => (s.Track, s.Print),
            "uCoverDepth" => (s.CoverDepth, s.Clamp),
            _ => (default, default),
        };
        return !img.IsNull;
    }
}
