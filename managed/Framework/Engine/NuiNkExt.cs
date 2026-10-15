using System;
using System.Runtime.InteropServices;

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

/// <summary>One nuklear <c>nk_color</c>: four bytes, alpha last.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct NkColor
{
    /// <summary>Channels.</summary>
    public byte R, G, B, A;
}

/// <inheritdoc cref="NkCommand"/>
public readonly unsafe partial struct NkCommand
{
    /// <summary>The highest <c>nk_command_type</c> this layout knows (<c>NK_COMMAND_CUSTOM</c>).</summary>
    public const int LastType = 18;
    private const int MultiColor = 6, Text = 16;

    /// <summary>Per <c>nk_command_type</c> 0..18: the byte offset of the command's first <c>nk_color</c>, 0 = none.</summary>
    private static ReadOnlySpan<byte> FirstColour => [0, 0, 0x1a, 0x22, 0x1c, 0x1a, 0x18, 0x1a, 0x18, 0x20, 0x20, 0x1e, 0x1c, 0x10, 0x10, 0x10, 0x18, 0x30, 0];

    /// <summary>The command's colours: four for a multi-colour rect, two for text (background, foreground), one for
    /// any other drawing command, none for a scissor, a custom command or a type past <see cref="LastType"/>.</summary>
    public Span<NkColor> Colours
    {
        get
        {
            int type = Type;
            if ((uint)type > LastType || FirstColour[type] == 0) return default;
            int n = type == MultiColor ? 4 : type == Text ? 2 : 1;
            return new Span<NkColor>((void*)(Ptr + FirstColour[type]), n);
        }
    }
}
