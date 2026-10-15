namespace CX.Engine;

/// <summary>A rectangle of 16-bit values as nuklear's draw commands store it: x, y signed, w, h unsigned.</summary>
public struct NuiShortRect
{
    /// <summary>Left edge.</summary>
    public short X;
    /// <summary>Top edge.</summary>
    public short Y;
    /// <summary>Width.</summary>
    public ushort W;
    /// <summary>Height.</summary>
    public ushort H;
}

/// <summary>A colour as four bytes, in the order nuklear stores it.</summary>
public struct NuiRgba
{
    /// <summary>Red.</summary>
    public byte R;
    /// <summary>Green.</summary>
    public byte G;
    /// <summary>Blue.</summary>
    public byte B;
    /// <summary>Alpha.</summary>
    public byte A;
}

/// <summary>One command in nuklear's queue: a type word, the offset of the next command, then fields whose place
/// depends on the type.</summary>
public readonly unsafe struct NuiCmd
{
    /// <summary>The command's first byte.</summary>
    public readonly byte* P;

    /// <summary>Views the command that starts at <paramref name="p"/>.</summary>
    public NuiCmd(byte* p) => P = p;

    /// <summary>The command's type word.</summary>
    public ref int Type => ref *(int*)P;
    /// <summary>The queue offset of the following command.</summary>
    public ref ulong Next => ref *(ulong*)(P + 8);
    /// <summary>The rectangle stored <paramref name="at"/> bytes into the command.</summary>
    public ref NuiShortRect Rect(int at) => ref *(NuiShortRect*)(P + at);
    /// <summary>The colour stored <paramref name="at"/> bytes into the command.</summary>
    public ref NuiRgba Colour(int at) => ref *(NuiRgba*)(P + at);
}
