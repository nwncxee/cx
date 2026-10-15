using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>The public <c>stbtt_pack_range</c> record the engine's font packer reads: size, first codepoint,
/// codepoint array, count, output chars, oversampling.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct StbPackRange
{
    /// <summary>The font size in pixels.</summary>
    public float FontSize;
    /// <summary>Unused when <see cref="Codepoints"/> is set.</summary>
    public int FirstCodepoint;
    /// <summary>The codepoint array, one per glyph.</summary>
    public nint Codepoints;
    /// <summary>The glyph count.</summary>
    public int NumChars;
    /// <summary>Where the packer writes one <see cref="StbPackedChar"/> per glyph.</summary>
    public nint CharData;
    /// <summary>Horizontal oversampling the packer fills in.</summary>
    public byte HOversample;
    /// <summary>Vertical oversampling the packer fills in.</summary>
    public byte VOversample;
}

/// <summary>The public <c>stbtt_packedchar</c> record: the box in the atlas and the glyph's offsets and advance.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StbPackedChar
{
    /// <summary>Box in the atlas.</summary>
    public ushort X0, Y0, X1, Y1;
    /// <summary>Offsets and advance, in pixels.</summary>
    public float XOff, YOff, XAdvance, XOff2, YOff2;
}
