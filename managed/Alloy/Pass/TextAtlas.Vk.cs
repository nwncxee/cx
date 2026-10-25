using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static unsafe partial class TextAtlas
{
    /// <summary>The atlases on Vulkan, keyed by the GL name the laid-out quads carry: a CX font's atlas is built
    /// here rather than acquired from the texture registry, so <see cref="VkTextures"/> never sees it and the GUI
    /// pass has nowhere else to look. Replaced whole with the device.</summary>
    private sealed class VkAtlasState
    {
        public VkBackend? Dev;
        public readonly Dictionary<uint, ImageId> ByName = new(8);
        public SamplerId Nearest;
    }

    private static VkAtlasState _vk = new();

    /// <summary>The engine's own font sampling, which <see cref="Upload"/> gives GL: nearest on both filters, no
    /// mips, clamped. Anything else moves glyph pixels.</summary>
    private static SamplerDesc NearestClamp => new(Filter.Nearest, Filter.Nearest, MipMode.None, Wrap.Clamp);

    /// <summary>Font atlas <paramref name="gl"/> as a Vulkan image, null id when it has no mirror.</summary>
    internal static ImageId VkImage(int gl) => _vk.ByName.TryGetValue((uint)gl, out ImageId i) ? i : default;

    /// <summary>The sampler every mirrored atlas is read through; null id before the first mirror.</summary>
    internal static SamplerId VkSampler => _vk.Nearest;

    /// <summary>Mirror <paramref name="p"/>'s pixels, already uploaded to GL name <paramref name="gl"/>, as a Vulkan
    /// image under that name. A refusal is logged and counted by <see cref="VkTextures"/>: the glyphs then draw
    /// through the GUI pass's stand-in rather than blank.</summary>
    private static void MirrorVk(uint gl, TextFontBake.PendingAtlas p)
    {
        if (!Renderer.OnVulkan || _log == null || AlloyDevice.Vulkan(_log) is not VkBackend vk) return;
        if (_vk.Dev != vk) _vk = new VkAtlasState { Dev = vk };
        VkAtlasState s = _vk;
        if (s.Nearest.IsNull) s.Nearest = vk.CreateSampler(NearestClamp);
        ImageId img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, Format.Rgba8, p.Width, p.Height, 1, 1, 1, 1,
                                                   ImageUse.Sampled | ImageUse.CopyDst, $"font atlas {p.Font.Name}"));
        if (img.IsNull || s.Nearest.IsNull)
        {
            VkTextures.Refuse($"the font atlas \"{p.Font.Name}\" {p.Width}x{p.Height}");
            return;
        }
        vk.Upload(img, 0, 0, new ReadOnlySpan<byte>((void*)p.Pixels, p.Width * p.Height * 4));
        Forget(gl);
        s.ByName[gl] = img;
        _log.Info($"TextAtlas: \"{p.Font.Name}\" mirrored on Vulkan as a {p.Width}x{p.Height} RGBA8 image under GL name {gl}");
    }

    /// <summary>The mirror under GL name <paramref name="gl"/> goes after the frames in flight.</summary>
    private static void Forget(uint gl)
    {
        if (_vk.ByName.Remove(gl, out ImageId old)) _vk.Dev?.Destroy(old);
    }
}
