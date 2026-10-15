using System;

namespace CX.Engine;

/// <summary>Addresses inside a block of memory a self-check allocated to stand in for engine memory.</summary>
public static unsafe class NuiScratch
{
    /// <summary>The address <paramref name="off"/> bytes into <paramref name="block"/>.</summary>
    public static IntPtr At(byte* block, long off) => (IntPtr)(block + off);
}
