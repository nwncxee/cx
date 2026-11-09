using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>One RGBA8 texel in upload order, which is the order every raster the engine hands over is in,
/// so a byte buffer reinterprets into a span of these with <c>MemoryMarshal.Cast</c> and no copy.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct Rgba32(byte R, byte G, byte B, byte A)
{
    /// <summary>Opaque: a source with no alpha channel widens to this.</summary>
    internal const byte Opaque = 255;

    /// <summary>The same colour at full alpha, for a source that carries none.</summary>
    internal Rgba32(byte r, byte g, byte b) : this(r, g, b, Opaque) { }
}

/// <summary>One RGB8 texel: a three-byte raster's element, and a BCn colour endpoint once widened.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct Rgb24(byte R, byte G, byte B);
