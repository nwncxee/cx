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

/// <summary>Nuklear's command queue: commands are reached by the offsets the queue itself stores.</summary>
public readonly unsafe struct NkQueue
{
    private readonly byte* _base;

    /// <summary>Views the queue that starts at <paramref name="queueBase"/>.</summary>
    public NkQueue(byte* queueBase) => _base = queueBase;

    /// <summary>The command at the queue's base.</summary>
    public NkCommand Head => new((IntPtr)_base);

    /// <summary>The first command of <paramref name="window"/>.</summary>
    public NkCommand Begin(NkWindow window) => new((IntPtr)(_base + window.BufBegin));

    /// <summary>The command <paramref name="cmd"/> links to.</summary>
    public NkCommand After(NkCommand cmd) => new((IntPtr)(_base + cmd.Next));

    /// <summary>The first draw command of the buffer whose memory is <paramref name="memoryPtr"/>: the context's
    /// <c>drawCmdOffset</c> back from the memory's end.</summary>
    public static byte* FirstDrawCommand(IntPtr memoryPtr, long memorySize, NkContext ctx)
        => (byte*)memoryPtr + memorySize - ctx.DrawCmdOffset;
}

/// <inheritdoc cref="NkCommand"/>
public readonly unsafe partial struct NkCommand
{
    /// <summary>The highest <c>nk_command_type</c> this layout knows (<c>NK_COMMAND_CUSTOM</c>).</summary>
    public const int LastType = 18;

    /// <summary>The command's colours: four for a multi-colour rect, two for text (background, foreground), one for
    /// any other drawing command, none for a scissor, a custom command or a type past <see cref="LastType"/>.</summary>
    public Span<NkColor> Colours
    {
        get
        {
            switch (Type)
            {
                case 2: return MemoryMarshal.CreateSpan(ref new NkCommandLine(Ptr).Color, 1);
                case 3: return MemoryMarshal.CreateSpan(ref new NkCommandCurve(Ptr).Color, 1);
                case 4: return MemoryMarshal.CreateSpan(ref new NkCommandRect(Ptr).Color, 1);
                case 5: return MemoryMarshal.CreateSpan(ref new NkCommandRectFilled(Ptr).Color, 1);
                case 6: return MemoryMarshal.CreateSpan(ref new NkCommandRectMultiColor(Ptr).Left, 4);
                case 7: return MemoryMarshal.CreateSpan(ref new NkCommandCircle(Ptr).Color, 1);
                case 8: return MemoryMarshal.CreateSpan(ref new NkCommandCircleFilled(Ptr).Color, 1);
                case 9: return MemoryMarshal.CreateSpan(ref new NkCommandArc(Ptr).Color, 1);
                case 10: return MemoryMarshal.CreateSpan(ref new NkCommandArcFilled(Ptr).Color, 1);
                case 11: return MemoryMarshal.CreateSpan(ref new NkCommandTriangle(Ptr).Color, 1);
                case 12: return MemoryMarshal.CreateSpan(ref new NkCommandTriangleFilled(Ptr).Color, 1);
                case 13: return MemoryMarshal.CreateSpan(ref new NkCommandPolygon(Ptr).Color, 1);
                case 14: return MemoryMarshal.CreateSpan(ref new NkCommandPolygonFilled(Ptr).Color, 1);
                case 15: return MemoryMarshal.CreateSpan(ref new NkCommandPolyline(Ptr).Color, 1);
                case 16: return MemoryMarshal.CreateSpan(ref new NkCommandText(Ptr).Background, 2);
                case 17: return MemoryMarshal.CreateSpan(ref new NkCommandImage(Ptr).Color, 1);
                default: return default;
            }
        }
    }
}
