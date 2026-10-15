using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Ui;

/// <summary>One vertex of a <see cref="VertexBatch"/> stream: position, texture coordinates, colour and alpha.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ShadedVertex
{
    /// <summary>Position.</summary>
    public float X, Y;
    /// <summary>Texture coordinates.</summary>
    public float U, V;
    /// <summary>Colour.</summary>
    public float R, G, B;
    /// <summary>Alpha.</summary>
    public float A;
}

/// <summary>The six vertices of one quad in a <see cref="VertexBatch"/> stream.</summary>
[InlineArray(6)]
public struct QuadVerts
{
    private ShadedVertex _vertex;
}
