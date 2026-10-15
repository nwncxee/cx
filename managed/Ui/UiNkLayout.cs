using System;
using System.Runtime.InteropServices;

namespace CX.Ui;

/// <summary>The words of the engine's Nuklear context the input service reads: the head of the window list and the
/// active window. The context is far larger; only these two sit at these places.</summary>
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct UiNkContext
{
    /// <summary>First window in the list.</summary>
    [FieldOffset(0x6720)] public IntPtr Begin;
    /// <summary>The window that has the focus.</summary>
    [FieldOffset(0x6730)] public IntPtr ActiveWindow;
}

/// <summary>The words of a Nuklear window the input service reads.</summary>
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct UiNkWindow
{
    /// <summary>Window flags.</summary>
    [FieldOffset(0x48)] public uint Flags;
    /// <summary>Bounds: x, y, width, height.</summary>
    [FieldOffset(0x4c)] public fixed float Bounds[4];
    /// <summary>Non-zero while a text edit in the window has the focus.</summary>
    [FieldOffset(0x18c)] public int EditActive;
    /// <summary>Next window in the list.</summary>
    [FieldOffset(0x1c0)] public IntPtr Next;
}
