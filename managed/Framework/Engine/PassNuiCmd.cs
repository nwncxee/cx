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

/// <summary>A command-sized slot of memory, for building a queue by hand.</summary>
public unsafe struct NuiCmdBlock
{
    /// <summary>Bytes per slot, enough for the largest command's fields.</summary>
    public const int Size = 0x50;
    /// <summary>The slot's bytes.</summary>
    public fixed byte B[Size];
}

/// <summary>One command in nuklear's queue: a type word, the offset of the next command, then fields whose place
/// depends on the type. A colour or rectangle member is named for the command types that keep it there.</summary>
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

    /// <summary>A scissor's clip, and an image's rectangle.</summary>
    public ref NuiShortRect ScissorRect => ref *(NuiShortRect*)(P + 0x10);
    /// <summary>The same slot read as the image's rectangle.</summary>
    public ref NuiShortRect ImageRect => ref *(NuiShortRect*)(P + 0x10);
    /// <summary>A filled rectangle's bounds.</summary>
    public ref NuiShortRect FilledRect => ref *(NuiShortRect*)(P + 0x12);
    /// <summary>A stroked rectangle's bounds.</summary>
    public ref NuiShortRect StrokedRect => ref *(NuiShortRect*)(P + 0x14);

    /// <summary>Line, filled rectangle and circle colour.</summary>
    public ref NuiRgba LineColour => ref *(NuiRgba*)(P + 0x1a);
    /// <summary>Curve colour.</summary>
    public ref NuiRgba CurveColour => ref *(NuiRgba*)(P + 0x22);
    /// <summary>Stroked rectangle and filled triangle colour.</summary>
    public ref NuiRgba RectColour => ref *(NuiRgba*)(P + 0x1c);
    /// <summary>Multi-colour rectangle, left corner.</summary>
    public ref NuiRgba MultiLeft => ref *(NuiRgba*)(P + 0x18);
    /// <summary>Multi-colour rectangle, top corner.</summary>
    public ref NuiRgba MultiTop => ref *(NuiRgba*)(P + 0x1c);
    /// <summary>Multi-colour rectangle, bottom corner.</summary>
    public ref NuiRgba MultiBottom => ref *(NuiRgba*)(P + 0x20);
    /// <summary>Multi-colour rectangle, right corner.</summary>
    public ref NuiRgba MultiRight => ref *(NuiRgba*)(P + 0x24);
    /// <summary>Filled circle colour.</summary>
    public ref NuiRgba CircleFilledColour => ref *(NuiRgba*)(P + 0x18);
    /// <summary>Arc and filled arc colour.</summary>
    public ref NuiRgba ArcColour => ref *(NuiRgba*)(P + 0x20);
    /// <summary>Stroked triangle colour.</summary>
    public ref NuiRgba TriangleColour => ref *(NuiRgba*)(P + 0x1e);
    /// <summary>Polygon, filled polygon and polyline colour.</summary>
    public ref NuiRgba PolygonColour => ref *(NuiRgba*)(P + 0x10);
    /// <summary>Text background colour.</summary>
    public ref NuiRgba TextBackground => ref *(NuiRgba*)(P + 0x18);
    /// <summary>Text foreground colour.</summary>
    public ref NuiRgba TextForeground => ref *(NuiRgba*)(P + 0x1c);
    /// <summary>Image tint.</summary>
    public ref NuiRgba ImageColour => ref *(NuiRgba*)(P + 0x30);
}

/// <summary>Nuklear's command queue: commands are reached by the offsets the queue itself stores.</summary>
public readonly unsafe struct NuiQueue
{
    private readonly byte* _base;

    /// <summary>Views the queue that starts at <paramref name="queueBase"/>.</summary>
    public NuiQueue(byte* queueBase) => _base = queueBase;

    /// <summary>The command at the queue's base.</summary>
    public NuiCmd Head => new(_base);

    /// <summary>The first command of <paramref name="window"/>.</summary>
    public NuiCmd Begin(NkWindow window) => new(_base + window.BufBegin);

    /// <summary>The command <paramref name="cmd"/> links to.</summary>
    public NuiCmd After(NuiCmd cmd) => new(_base + cmd.Next);

    /// <summary>The first draw command of the buffer whose memory is <paramref name="memoryPtr"/>: the context's
    /// <c>drawCmdOffset</c> back from the memory's end.</summary>
    public static byte* FirstDrawCommand(System.IntPtr memoryPtr, long memorySize, NkContext ctx)
        => (byte*)memoryPtr + memorySize - ctx.DrawCmdOffset;
}
