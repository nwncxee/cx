using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>One draw-table record: <see cref="AlloyRenderer.DrawFloats"/> floats. <c>((DrawRec*)table)[i].F</c> is the
/// i-th record's address.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DrawRec { public fixed float F[AlloyRenderer.DrawFloats]; }

/// <summary>One bone palette: <see cref="AlloyRenderer.BoneFloats"/> floats.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BoneRec { public fixed float F[AlloyRenderer.BoneFloats]; }
