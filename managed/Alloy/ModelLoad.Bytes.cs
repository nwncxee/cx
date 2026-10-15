using System;
using System.Runtime.InteropServices;

namespace CX.Alloy;

internal static unsafe partial class ModelLoad
{
    /// <summary>A little-endian 16-bit read at byte <paramref name="at"/> of the peeked message.</summary>
    private static ushort U16(ReadOnlySpan<byte> b, long at) => MemoryMarshal.Read<ushort>(b.Slice((int)at));

    private static int I32(ReadOnlySpan<byte> b, long at) => MemoryMarshal.Read<int>(b.Slice((int)at));

    private static float F32(ReadOnlySpan<byte> b, long at) => MemoryMarshal.Read<float>(b.Slice((int)at));

    private static void Put(Span<byte> b, long at, ushort v) => MemoryMarshal.Write(b.Slice((int)at), in v);

    private static void Put(Span<byte> b, long at, int v) => MemoryMarshal.Write(b.Slice((int)at), in v);

    private static void Put(Span<byte> b, long at, float v) => MemoryMarshal.Write(b.Slice((int)at), in v);
}
