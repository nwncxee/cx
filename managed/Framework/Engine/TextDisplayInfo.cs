using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>The words of the display block a text edit caches: scroll position, visible lines and budget,
/// the box, and the font height. The edit reads and writes them in place.</summary>
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
    /// <summary>Font height in layout units.</summary>
    [FieldOffset(0x28)] public float FontHeight;
}
