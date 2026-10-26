using System;
using System.IO;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

internal static unsafe partial class FrameTap
{
    /// <summary>Words a texel the scene colour reads back as: two halves to a word, as <c>cx_parity_c.glsl</c> packs
    /// <see cref="Format.Rgba16F"/> and <see cref="VkParity.WriteColour"/> unpacks it.</summary>
    private const int ColourWords = 2;

    /// <summary>The Vulkan frame's finished scene colour -- past the batches and the sky, before the encode into the
    /// composite -- as this set's <c>{seq}_vkscene.pfm</c>. The Vulkan frame records one list and submits it once, so
    /// there is no mid-frame tap to take: this reads the target back after that submit, the way the A/B does.</summary>
    internal static void TakeVk(VkBackend vk, ImageId hdr, int w, int h)
    {
        if (!Armed || _log == null || hdr.IsNull || w <= 0 || h <= 0) return;
        uint[]? colour = VkParity.Read(vk, hdr, Format.Rgba16F, ColourWords, w, h);
        if (colour == null)
        {
            _log.Always("[snap] the Vulkan scene colour did NOT read back (logged above) -- the set has no vkscene.pfm");
            return;
        }
        string path = Path.Combine(Dir, $"{_seq}_vkscene.pfm");
        try
        {
            Directory.CreateDirectory(Dir);
            VkParity.WriteColour(path, colour, w, h);
            _log.Always($"[snap] wrote the Vulkan scene colour to {path} ({w}x{h})");
        }
        catch (IOException e) { _log.Always($"[snap] {path} was not written -- {e.Message}"); }
        catch (UnauthorizedAccessException e) { _log.Always($"[snap] {path} was not written -- {e.Message}"); }
    }
}
