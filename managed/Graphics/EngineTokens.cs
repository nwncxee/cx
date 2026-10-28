namespace CX.Graphics;

/// <summary>
/// The numbering the engine's own renderer speaks, and the one a texture format, a blend factor, a sampler
/// filter or a primitive topology travels as wherever CX reads it out of engine data or carries it between
/// its own tables. Alloy maps these to the Vulkan seam's own enums; nothing here is an API CX calls, so a
/// file that needs only these needs no GL binding.
/// </summary>
internal static class EngineTokens
{
    /// <summary>Blend factors, as the engine's own draw state and Alloy's blend profiles name them.</summary>
    public const uint ONE = 1, SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303, ONE_MINUS_SRC_COLOR = 0x0301;

    /// <summary>Primitive topologies: the two every CX draw list records.</summary>
    public const uint TRIANGLES = 0x0004, TRIANGLE_STRIP = 0x0005;

    /// <summary>Pixel types an engine upload arrives in: one byte per channel, and one packed short per RGB
    /// pixel -- what the engine's own upload funnel pairs with an unsized RGB at a 16-bit colour depth.</summary>
    public const uint UNSIGNED_BYTE = 0x1401, UNSIGNED_SHORT_5_6_5 = 0x8363;

    /// <summary>Sampler minification/magnification filters and wrap modes, as the engine's sampler state
    /// carries them. The four mip pairings are all named, for a seam sampler that may ask for any of them.</summary>
    public const int NEAREST = 0x2600, LINEAR = 0x2601,
                     NEAREST_MIPMAP_NEAREST = 0x2700, LINEAR_MIPMAP_NEAREST = 0x2701,
                     NEAREST_MIPMAP_LINEAR = 0x2702, LINEAR_MIPMAP_LINEAR = 0x2703,
                     CLAMP_TO_EDGE = 0x812F, REPEAT = 0x2901;

    /// <summary>The sized colour formats CX names: the grass displacement field (RGBA8, RG the signed XY push
    /// and B the press-down), the lightmap's irradiance page (R11F_G11F_B10F), the probe's half-float cube
    /// (RGBA16F -- it stores linear radiance, where 8 bits would band the sky and clip a torch), and the rest
    /// a native stage may ask its own images for (<c>docs/shader-overrides.md</c>).</summary>
    public const uint R8 = 0x8229, R16F = 0x822D, R32F = 0x822E, RG8 = 0x822B, RG16F = 0x822F,
                      RGBA8 = 0x8058, RGBA16F = 0x881A, RGBA32F = 0x8814, R11F_G11F_B10F = 0x8C3A;

    /// <summary>The unsized base formats an engine upload names, and the sized RGB8 it has to be promoted to
    /// before a storage allocation will take it.</summary>
    public const uint RG = 0x8227, RGB = 0x1907, RGBA = 0x1908, RGB8 = 0x8051;

    /// <summary>BPTC, 16 bytes per 4x4 block: BC7 for the lightmap's baked colour pages, BC6H unsigned for
    /// its HDR ones.</summary>
    public const uint COMPRESSED_RGBA_BPTC_UNORM = 0x8E8C, COMPRESSED_RGB_BPTC_UNSIGNED_FLOAT = 0x8E8F;
}
