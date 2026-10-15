namespace CX.Engine;

/// <summary>The words the Itanium C++ ABI keeps ahead of a vtable's first slot.</summary>
public static unsafe class Itanium
{
    /// <summary>The offset-to-top word of the table <paramref name="vptr"/> points into: what a secondary base's
    /// pointer adds to reach the full object. It sits two words before the first slot.</summary>
    public static nint OffsetToTop(nint vptr) => ((nint*)vptr)[-2];
}
