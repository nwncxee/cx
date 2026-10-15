namespace CX.Engine;

/// <summary>A native allocation that holds a header of type <typeparamref name="T"/> and then the payload the engine
/// is handed. The header takes <see cref="Size"/> bytes, 16-aligned, so the payload keeps the allocation's alignment.</summary>
public static unsafe class Headroom<T> where T : unmanaged
{
    /// <summary>Bytes from the allocation start to the payload.</summary>
    public static readonly int Size = (sizeof(T) + 15) & ~15;

    /// <summary>The payload inside <paramref name="allocation"/>.</summary>
    public static nint PayloadOf(void* allocation) => (nint)allocation + Size;

    /// <summary>The allocation a payload lives in.</summary>
    public static void* BlockOf(nint payload) => (void*)(payload - Size);

    /// <summary>The header in front of a payload.</summary>
    public static T* Header(nint payload) => (T*)(payload - Size);
}
