using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>The display block <c>StringGob+0x528</c>: scroll, box, pixel-scroll mode, hit margin and font height, read
/// and written in place by the game. One layout on both builds: on Windows <c>SetText</c> @rva 0x1fd690 reads +0x28,
/// <c>EnablePixelScrolling</c> @0x1401e92a0 stores the +0x20 byte and <c>StringGob::HitCheck</c> @0x140048620 reads +0x24.</summary>
[StructLayout(LayoutKind.Explicit)]
public struct TextDisplayInfo
{
    /// <summary>First visible line.</summary>
    [FieldOffset(0x0)] public int TopLine;
    /// <summary>Lines that fit.</summary>
    [FieldOffset(0x4)] public int VisibleLines;
    /// <summary>Scroll range in lines.</summary>
    [FieldOffset(0x8)] public int ScrollBudget;
    /// <summary>Box width.</summary>
    [FieldOffset(0xc)] public float Width;
    /// <summary>Box height.</summary>
    [FieldOffset(0x10)] public float Height;
    /// <summary>Height of all lines.</summary>
    [FieldOffset(0x14)] public float TotalHeight;
    /// <summary>Height of the visible lines.</summary>
    [FieldOffset(0x18)] public float VisibleHeight;
    /// <summary>Scroll within a line.</summary>
    [FieldOffset(0x1c)] public float SubLineScroll;
    /// <summary>Non-zero when scrolling is per pixel. One byte: the engine stores and tests only <c>+0x20</c>.</summary>
    [FieldOffset(0x20)] public byte PixelScroll;
    /// <summary>How far the text's hit quad reaches past the box on each side horizontally; only the hit
    /// test reads it. The chat window sets it so a click on a line's portrait, left of the text, lands.</summary>
    [FieldOffset(0x24)] public float HitMargin;
    /// <summary>Font height in design pixels.</summary>
    [FieldOffset(0x28)] public float FontHeight;
}
