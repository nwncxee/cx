using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>An engine <c>CExoString</c> as it sits in memory: the character pointer, then the length. Sixteen
/// bytes, so a run of them is an array of strings.</summary>
[StructLayout(LayoutKind.Explicit, Size = 16)]
public unsafe struct ExoStringRec
{
    /// <summary>The characters.</summary>
    [FieldOffset(0)] public byte* Buf;
    /// <summary>The length in bytes.</summary>
    [FieldOffset(8)] public uint Len;
}
