using System;

namespace CX.Engine;

/// <inheritdoc cref="NkBuffer"/>
public readonly unsafe partial struct NkBuffer
{
    /// <summary>The command at nuklear's buffer offset <paramref name="off"/> (the value a window's begin or a command's next holds).</summary>
    public NkCommand CommandAt(ulong off) => new(Memory + (nint)off);
}

/// <inheritdoc cref="NkContext"/>
public readonly unsafe partial struct NkContext
{
    /// <summary>The command at nuklear's buffer offset <paramref name="off"/> in the context's own memory.</summary>
    public NkCommand CommandAt(ulong off) => new(MemPtr + (nint)off);
}

/// <inheritdoc cref="NkCommand"/>
public readonly unsafe partial struct NkCommand
{
    /// <summary>The alpha byte of colour <paramref name="i"/> in a run of 4-byte <c>nk_color</c>s starting <paramref name="firstAt"/> bytes into the command.</summary>
    public ref byte ColourAlpha(int firstAt, int i) => ref *(byte*)(Ptr + firstAt + 4 * i + 3);
}
