using System;
using System.Collections.Generic;
using CX.Alloy.Db;
using CX.Framework;
using CX.Graphics;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>
/// Every owned texture on Vulkan too, under <c>alloy.backend vk</c>: the bytes each of <see cref="OwnedTextures"/>'
/// upload sites hands GL go up again as a Vulkan image with the same levels and sampler, and the image takes a
/// bindless index. Keyed by the owned GL name, which is what the batch tables carry; freed beside the GL name.
/// ponytail: every texture is held twice in VRAM until GL Alloy is deleted.
/// </summary>
internal static class VkTextures
{
    private readonly record struct Entry(ImageId Image, BindlessHandle Handle, SamplerDesc Sampler);

    /// <summary>One device's mirror, replaced whole with the device (context loss drops both).</summary>
    private sealed class VkTexState
    {
        public VkBackend? Dev;
        public readonly Dictionary<uint, Entry> ByName = new();
        public readonly Dictionary<SamplerDesc, SamplerId> Samplers = new();
        public long Mirrored, Refused;
    }

    private static VkTexState _s = new();

    /// <summary>Distinct refusals logged before they are only counted.</summary>
    private const int RefusalLines = 8;

    /// <summary>The device the mirror writes to, or null: not on Vulkan, Alloy not owning textures, or no device.
    /// Made here when it does not exist yet, which an area load reaches before the first Vulkan frame.</summary>
    internal static VkBackend? Device(ExtenderLog? log)
    {
        if (!Renderer.OnVulkan || log == null || !OwnedTextures.Armed) return null;
        if (AlloyDevice.Vulkan(log) is not VkBackend vk) return null;
        if (_s.Dev != vk) _s = new VkTexState { Dev = vk };
        return vk;
    }

    /// <summary>A new Vulkan image for owned name <paramref name="gl"/>, replacing whatever the name held; null id
    /// (counted, logged) when the device refuses it. The caller uploads its levels next.</summary>
    internal static ImageId Create(VkBackend vk, uint gl, Format f, int w, int h, int mips, bool grey, in SamplerDesc s, string label)
    {
        Forget(gl);
        ImageUse use = ImageUse.Sampled | ImageUse.CopyDst | ImageUse.CopySrc | (grey ? ImageUse.Grey : ImageUse.None);
        ImageId img = vk.CreateImage(new ImageDesc(ImageKind.Tex2D, f, w, h, 1, mips, 1, 1, use, label));
        if (img.IsNull) { Refuse($"{label}: the device refused a {f} {w}x{h} image"); return default; }
        if (!_s.Samplers.TryGetValue(s, out SamplerId sid)) _s.Samplers[s] = sid = vk.CreateSampler(s);
        _s.ByName[gl] = new Entry(img, vk.Bindless(img, sid), s);
        _s.Mirrored++;
        return img;
    }

    /// <summary>The sampler <paramref name="gl"/>'s mirror was made with, for a replacement that keeps it.</summary>
    internal static SamplerDesc? SamplerOf(uint gl) => _s.ByName.TryGetValue(gl, out Entry e) ? e.Sampler : null;

    /// <summary>The bindless index of <paramref name="gl"/>'s mirror; 0 when it has none.</summary>
    internal static uint Index(int gl) => _s.ByName.TryGetValue((uint)gl, out Entry e) ? (uint)e.Handle.Value : 0;

    /// <summary>The mirror's image, for a bound (not bindless) read; null id when it has none.</summary>
    internal static ImageId Image(int gl) => _s.ByName.TryGetValue((uint)gl, out Entry e) ? e.Image : default;

    /// <summary>The GL name died: its mirror and its index go after the frames in flight.</summary>
    internal static void Forget(uint gl)
    {
        VkTexState s = _s;
        if (s.Dev == null || !s.ByName.Remove(gl, out Entry e)) return;
        s.Dev.Unbind(e.Handle);
        s.Dev.Destroy(e.Image);
    }

    /// <summary>A texture whose mirror could not be made draws with the null index; logged a few times, then counted.</summary>
    internal static void Refuse(string why)
    {
        if (++_s.Refused <= RefusalLines)
            KnobGroup.Log?.Always($"VkTextures: REFUSED {why} -- the texture draws as the stand-in on Vulkan"
                                + (_s.Refused == RefusalLines ? "; further refusals are only counted" : ""));
    }

    /// <summary>The engine's sampler state (GL enums, <c>OwnedTextures.EngineSampler</c>) as the seam's.</summary>
    internal static SamplerDesc Sampler(int min, int mag, int wrapS, int wrapT, bool aniso)
    {
        Filter Of(int f) => f is (int)Gl.LINEAR or (int)Gl.LINEAR_MIPMAP_LINEAR or (int)Gl.LINEAR_MIPMAP_NEAREST ? Filter.Linear : Filter.Nearest;
        MipMode mip = min switch
        {
            (int)Gl.LINEAR_MIPMAP_LINEAR or (int)Gl.NEAREST_MIPMAP_LINEAR => MipMode.Linear,
            (int)Gl.NEAREST_MIPMAP_NEAREST or (int)Gl.LINEAR_MIPMAP_NEAREST => MipMode.Nearest,
            _ => MipMode.None,
        };
        Wrap W(int w) => w == (int)Gl.CLAMP_TO_EDGE ? Wrap.Clamp : Wrap.Repeat;
        return new SamplerDesc(Of(min), Of(mag), mip, W(wrapS), Aniso: aniso ? TexRegistry.Aniso() : 1, WrapT: W(wrapT));
    }

    /// <summary>The seam's twin of a GL compressed internal format an owned texture is stored in; Unknown for
    /// one Vulkan's desktop devices do not sample (ETC2).</summary>
    internal static Format Compressed(uint gl) => gl switch
    {
        0x83F0 => Format.Bc1, 0x83F1 => Format.Bc1A, 0x83F2 => Format.Bc2, 0x83F3 => Format.Bc3,   // not-an-offset: S3TC DXT1 / DXT1A / DXT3 / DXT5
        0x8DBB => Format.Bc4, 0x8DBC => Format.Bc4S, 0x8DBD => Format.Bc5, 0x8DBE => Format.Bc5S,   // not-an-offset: RGTC1/2, unsigned and signed
        0x8E8F => Format.Bc6U, 0x8E8E => Format.Bc6S, 0x8E8C => Format.Bc7,                        // not-an-offset: BPTC float and unorm
        _ => Format.Unknown,
    };

    /// <summary>One line for the period report.</summary>
    internal static string Line() => _s.Dev == null ? "" : $"VkTextures: {_s.ByName.Count} mirrored ({_s.Mirrored} made, {_s.Refused} refused)";

    /// <summary>Oracle: the GL tokens glcorearb.h assigns, hand-written, against the seam formats they are.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "VkTextures.SelfCheck");
        check.Expect(Compressed(0x83F0) == Format.Bc1 && Compressed(0x83F3) == Format.Bc3 && Compressed(0x8DBD) == Format.Bc5
                  && Compressed(0x8E8C) == Format.Bc7 && Compressed(0x9274) == Format.Unknown,
            "DXT1 0x83F0 is BC1, DXT5 0x83F3 BC3, RG_RGTC2 0x8DBD BC5, RGBA_BPTC 0x8E8C BC7; ETC2 0x9274 has no Vulkan twin");
        SamplerDesc s = Sampler(0x2703, 0x2601, 0x812F, 0x2901, aniso: false);
        check.Expect(s.Min == Filter.Linear && s.Mip == MipMode.Linear && s.Mag == Filter.Linear && s.Wrap == Wrap.Clamp && s.WrapT == Wrap.Repeat,
            "LINEAR_MIPMAP_LINEAR 0x2703 / LINEAR 0x2601 / CLAMP_TO_EDGE 0x812F on S / REPEAT 0x2901 on T read as trilinear, clamped on S only");
        check.Expect(Sampler(0x2700, 0x2600, 0x2901, 0x2901, false).Mip == MipMode.Nearest && Sampler(0x2600, 0x2600, 0x2901, 0x2901, false).Mip == MipMode.None,
            "NEAREST_MIPMAP_NEAREST 0x2700 picks the nearest mip; plain NEAREST 0x2600 reads the base level only");
        log.Info(check.Ok ? "VkTextures.SelfCheck: passed" : "VkTextures.SelfCheck: FAILED");
        return check.Ok;
    }
}
