using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>One draw-table record: <see cref="AlloyRenderer.DrawFloats"/> floats, with the two matrices it opens
/// with named. <c>((DrawRec*)table)[i].F</c> is the i-th record's address.</summary>
[StructLayout(LayoutKind.Explicit, Size = AlloyRenderer.DrawFloats * sizeof(float))]
internal unsafe struct DrawRec
{
    /// <summary>All the lanes.</summary>
    [FieldOffset(0)] public fixed float F[AlloyRenderer.DrawFloats];
    /// <summary>This frame's model matrix.</summary>
    [FieldOffset(0)] public Matrix4x4 Model;
    /// <summary>Last frame's model matrix.</summary>
    [FieldOffset(AlloyRenderer.PrevFloat * sizeof(float))] public Matrix4x4 Prev;
}

/// <summary>One bone palette: <see cref="AlloyRenderer.BoneFloats"/> floats.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BoneRec { public fixed float F[AlloyRenderer.BoneFloats]; }
