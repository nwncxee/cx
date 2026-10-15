using System;

namespace CX.Engine;

/// <inheritdoc cref="NkBuffer"/>
public readonly unsafe partial struct NkBuffer
{
    /// <summary>The command <paramref name="off"/> bytes into a buffer whose memory block starts at <paramref name="memory"/>.</summary>
    public static NkCommand CommandAt(IntPtr memory, ulong off) => new(memory + (nint)off);
}

/// <inheritdoc cref="NkCommand"/>
public readonly unsafe partial struct NkCommand
{
    /// <summary>The alpha byte of colour <paramref name="i"/> in a run of 4-byte <c>nk_color</c>s starting <paramref name="firstAt"/> bytes into the command.</summary>
    public ref byte ColourAlpha(int firstAt, int i) => ref *(byte*)(Ptr + firstAt + 4 * i + 3);
}
