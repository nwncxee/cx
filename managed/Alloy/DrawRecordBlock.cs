using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>One persistent draw record as the GPU reads it: <see cref="AlloyRenderer.DrawFloats"/> floats, the
/// model matrix first, last frame's model at <see cref="AlloyRenderer.PrevFloat"/> and the extension tail at
/// <see cref="AlloyRenderer.ExtFloat"/>. Field order is the shader's record layout.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DrawRecordBlock
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
    public fixed float Ext[12];
}
