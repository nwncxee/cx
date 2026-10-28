using System;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// The sky-visibility bake (<see cref="PassId.SkyMap"/>) on Vulkan: the overhead depth view lands in the caster flush
/// like any other, then <see cref="SkyView"/>'s one dispatch turns it into the map the lit stage reads as
/// <c>uSkyMap</c>, once per area and again when the static set changes.
/// </summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The bake's stage and map on one device, replaced whole with the device.</summary>
    private sealed class VkSkyViewState
    {
        public VkBackend? Dev;
        public VkDefStage? Bake;
        public bool Tried, Live, Valid;
        public ImageId Map;
        public int Size = -1, Z = -1, Fit = -1;
    }

    private static VkSkyViewState _vkSkyView = new();

    /// <summary>The baked map, or the null id until a bake landed for the area being drawn.</summary>
    internal static ImageId VkSkyMap => _vkSkyView.Valid ? _vkSkyView.Map : default;

    /// <summary>The map describes an area that has gone; the next exterior frame bakes again.</summary>
    internal static void VkSkyRevoke() => _vkSkyView.Valid = false;

    /// <summary>The bake into <paramref name="cl"/>, after the flush that rendered <paramref name="depth"/>. A refusal
    /// is said once and the term stays at 1 everywhere this session.</summary>
    private static void VkSkyBake(VkBackend vk, CommandList cl, ImageId depth, in SkyFit fit, ExtenderLog log)
    {
        VkSkyViewState s = _vkSkyView;
        if (s.Dev != vk) _vkSkyView = s = new VkSkyViewState { Dev = vk };
        s.Valid = false;
        if (!VkSkyEnsure(vk, s, log)) return;
        VkColourState c = _vkc;
        VkDefStage st = s.Bake!;
        int arena0 = c.ArenaUsed;
        Array.Clear(st.Block);
        LooseBlock.Capture(st.Members, st.Block);
        LooseBlock.Set(s.Size, (ReadOnlySpan<int>)[SkyView.MapSize, SkyView.MapSize]);
        LooseBlock.Set(s.Z, [fit.ZAt0, fit.ZPerDepth]);
        LooseBlock.Set(s.Fit, [fit.UvPerMetre, SkyMath.RangeM]);
        LooseBlock.EndCapture();
        int at = VkArena(st.Block);
        if (at < 0) return;
        cl.UpdateBuffer(c.Arena, arena0, c.ArenaBytes.AsSpan(arena0, Math.Min((c.ArenaUsed - arena0 + 3) & ~3, ArenaCap - arena0)));
        Span<BufferBinding> ubo = stackalloc BufferBinding[4];
        Span<ImageBinding> sampled = stackalloc ImageBinding[2];
        Span<ImageBinding> images = stackalloc ImageBinding[1];
        int nu = 0, nt = 0, ni = 0;
        foreach (VkOpaque o in st.Opaques)
        {
            if (o.Set != 0) continue;
            if (o.Kind == VkBindKind.UniformBlock)
                ubo[nu++] = o.Binding == UboSlots.Loose ? new BufferBinding(o.Binding, c.Arena, at, st.Block.Length)
                                                        : new BufferBinding(o.Binding, c.ZeroUbo);
            else if (o.Kind == VkBindKind.StorageImage && ni < images.Length) images[ni++] = new ImageBinding(o.Binding, s.Map);
            else if (o.Kind == VkBindKind.Sampled && nt < sampled.Length) sampled[nt++] = new ImageBinding(o.Binding, depth, c.Raw);
        }
        cl.SetPipeline(st.Pipe);
        cl.BindSet(0, new BindSet(ubo[..nu], default, sampled[..nt], images[..ni]));
        int g = (SkyView.MapSize + SkyView.Group - 1) / SkyView.Group;
        cl.Dispatch(g, g, 1);
        ReadOnlySpan<ImageId> map = [s.Map];
        cl.ReadImages(map);
        s.Valid = true;
        SkyView.Baked(in fit);
        if (!s.Live)
        {
            s.Live = true;
            log.Always($"AlloyRenderer: the Vulkan sky-visibility bake is LIVE -- {SkyView.MapSize}^2 off the overhead view, "
                     + "read by the lit stage as the area ambient's scale");
        }
    }

    /// <summary>The stage and the RG16F map, once per device.</summary>
    private static bool VkSkyEnsure(VkBackend vk, VkSkyViewState s, ExtenderLog log)
    {
        if (s.Tried) return s.Bake != null;
        s.Tried = true;
        s.Bake = VkDefBuild(vk, SkyView.Shd, null, "vk sky bake", log);
        s.Map = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rg16F, SkyView.MapSize, SkyView.MapSize, 1, 1, 1, 1,
                                             ImageUse.Sampled | ImageUse.Storage, "vk sky map"));
        if (s.Bake == null || s.Map.IsNull)
        {
            s.Bake = null;
            log.Always("AlloyRenderer: the Vulkan sky-visibility bake or its map was REFUSED (logged above) -- the area "
                     + "ambient reaches everywhere on Vulkan this session");
            return false;
        }
        VkSkyViewState k = s;
        LooseBlock.Resolve(s.Bake.Names, () =>
        {
            k.Size = LooseBlock.Loc("uSize");
            k.Z = LooseBlock.Loc("uSkyZ");
            k.Fit = LooseBlock.Loc("uSkyFit");
        });
        return true;
    }
}
