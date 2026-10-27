using System;

namespace CX.Rhi;

/// <summary>The limits a pass may plan against, read once when the device is made, so recording never queries.</summary>
public readonly record struct DeviceCaps(
    int MaxTextureSize, int MaxArrayLayers, int MaxColorAttachments, int MaxSamples,
    int MaxFragmentTextureUnits, int MaxCombinedTextureUnits, int MaxStorageBindings, int MaxVertexStorageBlocks,
    int UniformAlign, int StorageAlign, float MaxAniso);

/// <summary>A rendering backend: resources made cold, one <see cref="Submit"/> per recorded list (dispatch per
/// pass, not per draw). Made inside the draw of the published frame and bound to that thread (the game thread),
/// and refuses calls from any other.</summary>
public interface IDevice
{
    DeviceCaps Caps { get; }

    /// <summary>Each <c>Create*</c> returns a null id after logging why when the backend refuses.</summary>
    BufferId CreateBuffer(in BufferDesc d);
    ImageId CreateImage(in ImageDesc d);
    SamplerId CreateSampler(in SamplerDesc d);
    ShaderId CreateShader(in ShaderDesc d);
    PipelineId CreatePipeline(in PipelineDesc d);

    void Destroy(BufferId b);
    void Destroy(ImageId i);
    void Destroy(SamplerId s);
    void Destroy(ShaderId s);
    void Destroy(PipelineId p);

    /// <summary>Execute what <paramref name="list"/> recorded since its last submit. False when a command was
    /// refused (a stale handle, a missing pipeline) and logged: the pass sits out this frame.</summary>
    bool Submit(CommandList list);
}
