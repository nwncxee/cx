using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>The engine's <c>CallBack</c> block as CX lays it out for its own event registrations: 0x30 bytes,
/// a 0x20-byte name, the handler, the user data.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct DbCallbackRec
{
    /// <summary>The event name, NUL-terminated.</summary>
    public fixed byte Name[0x20];
    /// <summary>The handler.</summary>
    public nint Fn;
    /// <summary>The registration's user data.</summary>
    public nint Data;
}
