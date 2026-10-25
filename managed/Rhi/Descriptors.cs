using System;

namespace CX.Rhi;

/// <summary>A texel format, the seam's own: a backend translates it, and sRGB encoding is a property of the
/// format rather than a context flag.</summary>
public enum Format : byte
{
    Unknown, R8, Rg8, Rgba8, Rgba8Srgb, R16F, Rg16F, Rgba16F, R32F, Rg32F, Rgba32F, R32Ui, Rg32Ui, Rgba32Ui, R11G11B10F,
    D16, D24, D24S8, D32F, D32FS8,
    /// <summary>The block-compressed formats an engine or DDS texture arrives in, each sampled as UNORM (or SNORM/float).</summary>
    Bc1, Bc1A, Bc2, Bc3, Bc4, Bc4S, Bc5, Bc5S, Bc6U, Bc6S, Bc7,
}

/// <summary>What a format is, asked without a backend.</summary>
public static class Formats
{
    /// <summary>A depth (or depth-stencil) format, attached at the depth point and cleared as depth.</summary>
    public static bool IsDepth(this Format f) => f is Format.D16 or Format.D24 or Format.D24S8 or Format.D32F or Format.D32FS8;

    /// <summary>Holds unnormalized integers: cleared and read as integers, never through a float path.</summary>
    public static bool IsInteger(this Format f) => f is Format.R32Ui or Format.Rg32Ui or Format.Rgba32Ui;

    /// <summary>Carries stencil too, so it attaches at the depth-stencil point.</summary>
    public static bool HasStencil(this Format f) => f is Format.D24S8 or Format.D32FS8;

    /// <summary>Stored in 4x4 blocks: a level's bytes are its block count times <see cref="BlockBytes"/>.</summary>
    public static bool IsCompressed(this Format f) => f >= Format.Bc1;

    /// <summary>Bytes in one 4x4 block of a compressed format: 8 for BC1 and BC4, 16 for the rest.</summary>
    public static int BlockBytes(this Format f) => f is Format.Bc1 or Format.Bc1A or Format.Bc4 or Format.Bc4S ? 8 : 16;

    /// <summary>Writes encode linear to sRGB and reads decode, so a target in it needs no context-wide switch.</summary>
    public static bool IsSrgb(this Format f) => f == Format.Rgba8Srgb;
}

/// <summary>What a buffer is bound as. Vulkan needs all of it at creation; GL ignores it.</summary>
[Flags]
public enum BufferUse : byte { None = 0, Vertex = 1, Index = 2, Storage = 4, Uniform = 8, Indirect = 16, CopySrc = 32, CopyDst = 64 }

/// <summary>Where a buffer lives: device-only, or written from the CPU with <c>UpdateBuffer</c>.</summary>
public enum MemoryKind : byte { Device, Upload }

/// <summary>A buffer to create.</summary>
public readonly record struct BufferDesc(long Bytes, BufferUse Use, MemoryKind Memory, string Label);

/// <summary>The dimensionality of an image.</summary>
public enum ImageKind : byte { Tex2D, Tex2DArray, Cube, CubeArray, Tex3D }

/// <summary>What an image is used as. Vulkan needs all of it at creation; GL ignores it.</summary>
[Flags]
public enum ImageUse : byte
{
    None = 0, Sampled = 1, ColorTarget = 2, DepthTarget = 4, Storage = 8, CopySrc = 16, CopyDst = 32,
    /// <summary>Sampled as grey: red repeated into green and blue, alpha 1 -- a single-channel engine texture.</summary>
    Grey = 64,
}

/// <summary>An image to create. <see cref="Layers"/> counts whole cubes for the cube kinds; <see cref="D"/> is
/// the depth of a 3D image and 1 otherwise.</summary>
public readonly record struct ImageDesc(ImageKind Kind, Format Fmt, int W, int H, int D, int Mips, int Layers,
                                        int Samples, ImageUse Use, string Label);

/// <summary>A texel filter.</summary>
public enum Filter : byte { Nearest, Linear }

/// <summary>Mip selection: <see cref="None"/> samples the base level only.</summary>
public enum MipMode : byte { None, Nearest, Linear }

/// <summary>Addressing past the edge.</summary>
public enum Wrap : byte { Repeat, Clamp }

/// <summary>A depth or shadow comparison.</summary>
public enum CompareOp : byte { Less, LessEqual, Equal, Greater, Always }

/// <summary>A sampler to create. A non-null <see cref="Compare"/> makes it a shadow sampler, and a null
/// <see cref="WrapT"/> wraps T (and R) as <see cref="Wrap"/> does S.</summary>
public readonly record struct SamplerDesc(Filter Min, Filter Mag, MipMode Mip, Wrap Wrap,
                                          CompareOp? Compare = null, float Aniso = 1, float LodBias = 0, Wrap? WrapT = null);

/// <summary>A programmable stage.</summary>
public enum ShaderStage : byte { Vertex, Geometry, Fragment, Compute }

/// <summary>One stage's GLSL. <see cref="Ref"/> is the <c>cx_*</c> resref content may override it by, or null for
/// a stage that is not overridable.</summary>
public readonly record struct ShaderDesc(ShaderStage Stage, string? Ref, string Source, string Label);
