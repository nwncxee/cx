using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace CX.Rhi;

/// <summary>One vertex attribute's component type, as the shader reads it: a normalized type arrives scaled
/// into -1..1 or 0..1, the others at face value.</summary>
public enum VertexType : byte
{
    Float,
    /// <summary>Four bytes at face value -- 0.0..255.0, not 0..1.</summary>
    U8,
    U8Norm,
    /// <summary>Four lanes in one 32-bit word, normalized to -1..1: a packed tangent.</summary>
    Packed1010102Norm,
}

/// <summary>One attribute of a vertex layout: the shader location it feeds, how many components of which type,
/// and its byte offset inside the stream's element.</summary>
public readonly record struct VertexAttrib(uint Location, int Components, VertexType Type, int Offset);

/// <summary>A vertex input layout to resolve once: one interleaved stream of <see cref="Stride"/> bytes per vertex.
/// Cold: the array is read at creation and kept, never walked in a frame.</summary>
public readonly record struct VertexLayoutDesc(VertexAttrib[] Attribs, int Stride, string Label);

/// <summary>The per-draw constant block's limits. 128 bytes is Vulkan's guaranteed push-constant size, so anything
/// larger belongs in a buffer the draw indexes.</summary>
public static class PushConstants
{
    /// <summary>Bytes a pass may push. A write past it is refused.</summary>
    public const int Bytes = 128;
}

/// <summary>Primitive assembly. Every arena mesh is a triangle list; the engine's other primitive modes reach only
/// the engine's own draws.</summary>
public enum Topology : byte { Triangles, TriangleStrip }

/// <summary>Which faces are culled.</summary>
public enum CullMode : byte { None, Back, Front }

/// <summary>Rasterizer state. The bias constants are dynamic (<c>SetDepthBias</c>); only the switch is here.</summary>
public readonly record struct RasterState(CullMode Cull = CullMode.None, bool FrontCw = false, bool DepthBias = false,
                                          bool DepthClamp = false);

/// <summary>Depth test and write. ponytail: no stencil; no Alloy pass uses it.</summary>
public readonly record struct DepthState(bool Test = false, bool Write = false, CompareOp Compare = CompareOp.LessEqual);

/// <summary>A blend factor: the set the engine's own blend-mode table resolves to, plus the inverse source colour
/// the order-free transparency pass reveals with.</summary>
public enum BlendFactor : byte { Zero, One, SrcAlpha, OneMinusSrcAlpha, SrcColor, OneMinusSrcColor, DstColor, OneMinusDstAlpha }

/// <summary>A blend equation, shared by colour and alpha.</summary>
public enum BlendOp : byte { Add, Min }

/// <summary>A set of colour channels.</summary>
[Flags]
public enum ColorMask : byte { None = 0, R = 1, G = 2, B = 4, A = 8, All = 15 }

/// <summary>One colour attachment's blend. <see cref="Masked"/> names the channels NOT written, so the zero value
/// -- <c>default</c>, or an element of a fresh <see cref="BlendSet"/>, which no constructor default reaches --
/// writes every channel unblended.</summary>
public readonly record struct AttachmentBlend(bool Enable = false, BlendFactor SrcRgb = BlendFactor.One,
    BlendFactor DstRgb = BlendFactor.Zero, BlendFactor SrcA = BlendFactor.One, BlendFactor DstA = BlendFactor.Zero,
    BlendOp Op = BlendOp.Add, ColorMask Masked = ColorMask.None)
{
    /// <summary>Is every channel of <paramref name="c"/> written?</summary>
    public bool Writes(ColorMask c) => (Masked & c) == 0;
}

/// <summary>Up to <see cref="RenderPassDesc.MaxColors"/> attachments' blends.</summary>
[InlineArray(RenderPassDesc.MaxColors)]
public struct BlendSet { private AttachmentBlend _e; }

/// <summary>Up to <see cref="RenderPassDesc.MaxColors"/> attachments' formats.</summary>
[InlineArray(RenderPassDesc.MaxColors)]
public struct FormatSet { private Format _e; }

/// <summary>The target shape a pipeline draws into: Vulkan bakes it, GL ignores it.</summary>
public struct AttachmentFormats
{
    public FormatSet Colors;
    public int ColorCount;
    public Format Depth;
    public int Samples;
}

/// <summary>A pipeline: the stages and every fixed-function bit a pass draws with. Viewport, scissor and the
/// depth-bias constants are dynamic and recorded instead, so a cascade or cube-face loop reuses one pipeline.</summary>
public struct PipelineDesc
{
    public ShaderId Vs, Gs, Fs, Cs;
    /// <summary>The vertex input the draws read; the null id is attribute-less.</summary>
    public VertexLayoutId Layout;
    public Topology Topo;
    public RasterState Raster;
    public DepthState Depth;
    public BlendSet Blend;
    public AttachmentFormats Targets;
    public string Label;
}

/// <summary>What happens to an attachment's contents at the start of a render pass.</summary>
public enum LoadOp : byte { Load, Clear, DontCare }

/// <summary>What happens to an attachment's contents at the end of a render pass.</summary>
public enum StoreOp : byte { Store, DontCare }

/// <summary>A colour attachment: one mip of one layer, or every layer when <see cref="Layer"/> is -1.</summary>
public readonly record struct ColorAttachment(ImageId Image, int Mip, int Layer, LoadOp Load, StoreOp Store, Vector4 Clear);

/// <summary>The depth attachment, cleared to <see cref="Depth"/> on <see cref="LoadOp.Clear"/>: one mip of one
/// layer, or every layer when <see cref="Layer"/> is -1, which is what a stage writing <c>gl_Layer</c> routes into.</summary>
public readonly record struct DepthAttachment(ImageId Image, int Mip, int Layer, LoadOp Load, StoreOp Store, float Depth);

/// <summary>A render pass's attachments. There is nothing to save or restore around one.</summary>
public readonly ref struct RenderPassDesc
{
    /// <summary>Colour attachments a pass may have, GL's guaranteed minimum.</summary>
    public const int MaxColors = 8;
    public readonly ReadOnlySpan<ColorAttachment> Colors;
    public readonly bool HasDepth;
    public readonly DepthAttachment Depth;

    public RenderPassDesc(ReadOnlySpan<ColorAttachment> colors) { Colors = colors; }
    public RenderPassDesc(ReadOnlySpan<ColorAttachment> colors, DepthAttachment depth)
    { Colors = colors; HasDepth = true; Depth = depth; }
}

/// <summary>A buffer range on a binding slot; <see cref="Bytes"/> 0 binds the whole buffer.</summary>
public readonly record struct BufferBinding(uint Slot, BufferId Buffer, long Offset = 0, long Bytes = 0);

/// <summary>An image on a binding slot: sampled through <see cref="Sampler"/>, or as a storage image at
/// <see cref="Mip"/>. The backend picks the unit a slot lands on.</summary>
public readonly record struct ImageBinding(uint Slot, ImageId Image, SamplerId Sampler = default, int Mip = 0);

/// <summary>A pass's resource bindings, recorded by copy into the command arena; nothing is allocated.</summary>
public readonly ref struct BindSet
{
    public readonly ReadOnlySpan<BufferBinding> Uniforms, Storage;
    public readonly ReadOnlySpan<ImageBinding> Sampled, StorageImages;

    public BindSet(ReadOnlySpan<BufferBinding> uniforms, ReadOnlySpan<BufferBinding> storage,
                   ReadOnlySpan<ImageBinding> sampled, ReadOnlySpan<ImageBinding> storageImages)
    { Uniforms = uniforms; Storage = storage; Sampled = sampled; StorageImages = storageImages; }
}

/// <summary>How a pass touches a resource; the transition between two passes is derived from the pair.</summary>
public enum Access : byte
{
    SampledRead, ColorWrite, DepthRead, DepthWrite, StorageRead, StorageWrite, IndirectRead, VertexRead,
    IndexRead, UniformRead, CopySrc, CopyDst, HostRead,
}

/// <summary>One resource a pass reads or writes: an image or a buffer, never both.</summary>
public readonly record struct ResourceUse(ImageId Image, BufferId Buffer, Access Access)
{
    public static ResourceUse Of(ImageId i, Access a) => new(i, default, a);
    public static ResourceUse Of(BufferId b, Access a) => new(default, b, a);
}

/// <summary>A pass's declared reads and writes, the input the frame graph derives barriers from.</summary>
public readonly ref struct PassIo
{
    public readonly ReadOnlySpan<ResourceUse> Reads, Writes;

    public PassIo(ReadOnlySpan<ResourceUse> reads, ReadOnlySpan<ResourceUse> writes) { Reads = reads; Writes = writes; }
}
