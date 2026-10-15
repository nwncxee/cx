using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>The three material-extension vec4s that close a draw record.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MaterialExt
{
    /// <summary>The first extension vec4.</summary>
    public Vector4 Ext0;
    /// <summary>The second extension vec4.</summary>
    public Vector4 Ext1;
    /// <summary>The third extension vec4; its w is the tile's hidden term, 0 meaning explored.</summary>
    public Vector4 Ext2;
}

/// <summary>One bone palette: a matrix per bone, as the pose stage writes them.</summary>
[InlineArray(AlloyRenderer.BoneCount)]
internal struct BonePalette { private Matrix4x4 _e; }

/// <summary>One persistent draw record as the GPU reads it: <see cref="AlloyRenderer.DrawFloats"/> floats, the
/// model matrix first, last frame's model at <see cref="AlloyRenderer.PrevFloat"/> and the extension tail at
/// <see cref="AlloyRenderer.ExtFloat"/>. Field order is the shader's record layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DrawRecordBlock
{
    public Matrix4x4 Model;
    public float Cos, Sin, Dissolve, Alpha;
    public float AmbR, AmbG, AmbB, AmbW;
    public float DifR, DifG, DifB, DifW;
    public float EmiR, EmiG, EmiB, EmiW;
    public float PbrR, PbrG, PbrB, Slots;
    public float PbrX, PbrY, PbrZ, PbrW;
    public float FeatX, FeatY, FeatZ, FeatW;
    public Matrix4x4 Prev;
    public MaterialExt Ext;
}
