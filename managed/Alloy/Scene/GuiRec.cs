using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>The leading lanes of one GUI draw record, as the shader reads them: the two matrices, the texture
/// rotation, and the colour terms.</summary>
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
}
