using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace CX.Rhi;

/// <summary>Vertex input. ponytail: only the attribute-less layout (full-screen triangles, vertex pulling);
/// the mesh arena's four layouts join when a mesh pass moves onto the seam.</summary>
public enum VertexLayout : byte { None }

/// <summary>Primitive assembly.</summary>
public enum Topology : byte { Triangles, TriangleStrip }

/// <summary>Which faces are culled.</summary>
public enum CullMode : byte { None, Back, Front }

/// <summary>Rasterizer state. The bias constants are dynamic (<c>SetDepthBias</c>); only the switch is here.</summary>
public readonly record struct RasterState(CullMode Cull = CullMode.None, bool FrontCw = false, bool DepthBias = false,
                                          bool DepthClamp = false);

/// <summary>Depth test and write. ponytail: no stencil; no Alloy pass uses it.</summary>
public readonly record struct DepthState(bool Test = false, bool Write = false, CompareOp Compare = CompareOp.LessEqual);

/// <summary>A blend factor.</summary>
public enum BlendFactor : byte { Zero, One, SrcAlpha, OneMinusSrcAlpha, OneMinusSrcColor }

/// <summary>A blend equation, shared by colour and alpha.</summary>
public enum BlendOp : byte { Add, Min }

/// <summary>Colour channels written.</summary>
[Flags]
public enum ColorMask : byte { None = 0, R = 1, G = 2, B = 4, A = 8, All = 15 }

/// <summary>One colour attachment's blend. The default writes every channel unblended.</summary>
public readonly record struct AttachmentBlend(bool Enable = false, BlendFactor SrcRgb = BlendFactor.One,
    BlendFactor DstRgb = BlendFactor.Zero, BlendFactor SrcA = BlendFactor.One, BlendFactor DstA = BlendFactor.Zero,
    BlendOp Op = BlendOp.Add, ColorMask Mask = ColorMask.All);

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
    public VertexLayout Layout;
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

/// <summary>The depth attachment, cleared to <see cref="Depth"/> on <see cref="LoadOp.Clear"/>.</summary>
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
