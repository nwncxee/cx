using System.Runtime.InteropServices;

namespace CX.Ui;

/// <summary>The six vertices of one quad in a <see cref="VertexBatch"/> stream.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct QuadVerts { public fixed float F[VertexBatch.Floats * 6]; }
