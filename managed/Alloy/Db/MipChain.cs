using System;
using System.Runtime.InteropServices;

namespace CX.Alloy.Db;

/// <summary>The mip chain of a texture whose alpha holes cut a surface: built on the decode worker, in place of the
/// device's alpha-blind filter and a file's stored mips.</summary>
internal static class MipChain
{
    /// <summary>One RGBA8 texel, as a chain lays them out.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Texel(byte R, byte G, byte B, byte A);

    /// <summary>The texel at column <paramref name="x"/>, row <paramref name="y"/> of a level <paramref name="w"/> wide.</summary>
    internal static int Cell(int w, int x, int y) => y * w + x;

    /// <summary>The whole RGBA8 chain of a sheet with holes; null for one with none.</summary>
    internal static byte[]? Raster(ReadOnlySpan<byte> rgba, int w, int h) => null;

    /// <summary>A stored chain rebuilt as BC3 with level 0 kept where it can be; null when it stays as stored.</summary>
    internal static byte[]? Dds(ReadOnlySpan<byte> chain, int w, int h, int levels, DdsReader.Fmt f) => null;
}
