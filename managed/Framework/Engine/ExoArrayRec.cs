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
}
