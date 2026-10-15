using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>The words of an <c>SDL_Event</c> the extender reads, by event kind: the kinds overlap, so a field is
/// only meaningful for the events that carry it.</summary>
[StructLayout(LayoutKind.Explicit, Size = 56)]
public unsafe struct SdlEvent
{
    /// <summary>The event type.</summary>
    [FieldOffset(0x0)] public uint Type;
    /// <summary>A window event's kind.</summary>
    [FieldOffset(0xc)] public byte WindowKind;
    /// <summary>A key event's repeat flag.</summary>
    [FieldOffset(0xd)] public byte Repeat;
    /// <summary>A key event's scancode.</summary>
    [FieldOffset(0x10)] public int Scancode;
    /// <summary>A key event's keysym.</summary>
    [FieldOffset(0x14)] public int Sym;
    /// <summary>A key event's keysym, unsigned.</summary>
    [FieldOffset(0x14)] public uint SymU;
    /// <summary>A mouse button event's button.</summary>
    [FieldOffset(0x10)] public byte Button;
    /// <summary>A mouse event's x.</summary>
    [FieldOffset(0x14)] public int X;
    /// <summary>A mouse event's y.</summary>
    [FieldOffset(0x18)] public int Y;
    /// <summary>A mouse motion's x delta.</summary>
    [FieldOffset(0x1c)] public int XRel;
    /// <summary>A mouse motion's y delta.</summary>
    [FieldOffset(0x20)] public int YRel;
    /// <summary>A wheel event's vertical scroll.</summary>
    [FieldOffset(0x14)] public int WheelY;
    /// <summary>A text-input event's UTF-8 text.</summary>
    [FieldOffset(0xc)] public SdlText Text;
}

/// <summary>The 32 bytes an <c>SDL_TextInputEvent</c> carries its UTF-8 text in.</summary>
[InlineArray(32)]
public struct SdlText
{
    private byte _first;
}
