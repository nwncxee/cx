using CX.Framework;
using CX.Graphics.Rhi;
using CX.Rhi;

namespace CX.Alloy;

/// <summary>The format an alpha-free colour target takes. Three packed floats hold a never-negative colour in half
/// the bytes four half floats would, but the packed format is optional for most of the ways a target is written, so
/// one a device does not offer every way its target is reached takes the half floats instead.</summary>
internal static class PackedColour
{
    /// <summary>Three floats in one word: five exponent bits each, six mantissa bits in red and green and five in
    /// blue, no sign and no alpha.</summary>
    internal const Format Packed = Format.R11G11B10F;

    /// <summary>What a target takes where the device falls short of <see cref="Packed"/>: four half floats.</summary>
    internal const Format Wide = Format.Rgba16F;

    /// <summary>The format for <paramref name="what"/>, which is reached every way <paramref name="use"/> names:
    /// <see cref="Packed"/>, or <see cref="Wide"/> where the device falls short of what
    /// <see cref="VkBackend.FormatMissing"/> asks of it. Said either way, so a log names the device's answer rather
    /// than leaving the bytes a guess. Cold: once a target.</summary>
    internal static Format Or16F(VkBackend vk, ImageUse use, string what, ExtenderLog log)
    {
        if (vk.FormatMissing(Packed, use) is not string why)
        {
            log.Info($"Alloy: {Packed} for {what}, which this device offers for {use}");
            return Packed;
        }
        log.Always($"Alloy: {Wide} for {what}, at twice the bytes a texel -- {why}");
        return Wide;
    }
}
