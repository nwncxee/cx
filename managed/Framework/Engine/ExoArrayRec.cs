using System;
using System.Runtime.InteropServices;

namespace CX.Engine;

/// <summary>An engine <c>CExoArrayList</c> header as it sits in memory: the items pointer, the count, the
/// allocated size. Sixteen bytes, so the bucket tables that are arrays of these read as an array.</summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct ExoArrayRec
{
    /// <summary>The items.</summary>
    public IntPtr Items;
    /// <summary>Entries in use.</summary>
    public int Count;
    /// <summary>Entries allocated.</summary>
    public int Alloc;

    /// <summary>One <c>MaterialShaderParam</c>: its name and value are inline, so the size is one number on both builds.</summary>
    public const int MaterialParamBytes = 0x6c;   // not-an-offset: sizeof(MaterialShaderParam)

    /// <summary>The items in use as raw bytes, for records of <paramref name="stride"/> bytes.</summary>
    public readonly ReadOnlySpan<byte> Bytes(int stride)
        => Items == IntPtr.Zero || Count <= 0 ? default : new ReadOnlySpan<byte>((void*)Items, Count * stride);
}
