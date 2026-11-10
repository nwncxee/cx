using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>One triangle's three corner indices, laid out exactly as a triangle list is, so an index run
/// reinterprets into a span of these with <c>MemoryMarshal.Cast</c> and no copy.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct TriCorners(ushort A, ushort B, ushort C);
