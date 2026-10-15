using System.Numerics;
using CX.Alloy;

namespace CX.Engine;

/// <summary>A pass's uniform block, one lane per member. The lane offsets are <c>AlloyRenderer.Pb*</c>; a lane
/// moves by changing that constant, never here.</summary>
public readonly unsafe struct PassParamBlock
{
    private readonly byte* _b;

    /// <summary>Views the block at <paramref name="block"/>.</summary>
    public PassParamBlock(byte* block) => _b = block;

    /// <summary>Cluster tile: viewport origin and the tile size in pixels.</summary>
    public ref Vector4 Tile => ref *(Vector4*)(_b + AlloyRenderer.PbTile);
    /// <summary>Cluster grid dimensions, three ints.</summary>
    public int* Grid => (int*)(_b + AlloyRenderer.PbGrid);
    /// <summary>Light reach.</summary>
    public ref float Reach => ref *(float*)(_b + AlloyRenderer.PbReach);
    /// <summary>Light curve falloff terms.</summary>
    public ref Vector3 Falloff => ref *(Vector3*)(_b + AlloyRenderer.PbFalloff);
    /// <summary>Wrap lighting.</summary>
    public ref float Wrap => ref *(float*)(_b + AlloyRenderer.PbWrap);
    /// <summary>Near and far depth.</summary>
    public ref Vector2 Z => ref *(Vector2*)(_b + AlloyRenderer.PbZ);
    /// <summary>Attenuation terms.</summary>
    public ref Vector2 Atten => ref *(Vector2*)(_b + AlloyRenderer.PbAtten);
    /// <summary>Hotspot terms.</summary>
    public ref Vector2 Hot => ref *(Vector2*)(_b + AlloyRenderer.PbHot);
    /// <summary>Subsurface terms.</summary>
    public ref Vector2 Sss => ref *(Vector2*)(_b + AlloyRenderer.PbSss);
    /// <summary>Normal-wrap lighting.</summary>
    public ref float WrapN => ref *(float*)(_b + AlloyRenderer.PbWrapN);
    /// <summary>Day dimming.</summary>
    public ref float DayDim => ref *(float*)(_b + AlloyRenderer.PbDayDim);
    /// <summary>Relevance terms.</summary>
    public ref Vector2 Relevance => ref *(Vector2*)(_b + AlloyRenderer.PbRelevance);
    /// <summary>Whether the pass draws the view the cluster table was built for.</summary>
    public ref float Bounds => ref *(float*)(_b + AlloyRenderer.PbBounds);
    /// <summary>Local-cube plane terms.</summary>
    public ref Vector2 LocalPlane => ref *(Vector2*)(_b + AlloyRenderer.PbLocalPlane);
    /// <summary>Local-cube shadow floor.</summary>
    public ref float LocalFloor => ref *(float*)(_b + AlloyRenderer.PbLocalFloor);
    /// <summary>Fade.</summary>
    public ref float Fade => ref *(float*)(_b + AlloyRenderer.PbFade);
}
