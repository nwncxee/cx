using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>The PLT master-palette row of each of a texture's first eight layers.</summary>
[InlineArray(8)]
internal struct PltLayerRows { private float _e; }

/// <summary>One GUI draw record as the shader reads it, 16 vec4: the two matrices, the texture rotation, the colour
/// terms, the draw's flags and bone base, then the PLT rows.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GuiRec
{
    /// <summary>Model-view-projection.</summary>
    public Matrix4x4 Mvp;
    /// <summary>Model-view.</summary>
    public Matrix4x4 Mv;
    /// <summary>The 2x2 texture rotation, row major.</summary>
    public Vector4 TexRot;
    /// <summary>Global colour.</summary>
    public Vector4 Colour;
    /// <summary>Ambient term.</summary>
    public Vector4 Ambient;
    /// <summary>Diffuse term.</summary>
    public Vector4 Diffuse;
    /// <summary>Emissive term.</summary>
    public Vector4 Emissive;
    /// <summary>The draw's flag bits.</summary>
    public float Flags;
    /// <summary>The first matrix of this draw's palette, in vec4 units from the start of the palettes.</summary>
    public float BoneBase;
    /// <summary>The PLT master-palette row of layer 8.</summary>
    public float PltRow8;
    /// <summary>The PLT master-palette row of layer 9.</summary>
    public float PltRow9;
    /// <summary>The PLT master-palette rows of layers 0 to 7.</summary>
    public PltLayerRows PltRows;
}
