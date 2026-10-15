namespace CX.Engine;

/// <summary>A native block that holds a CX header of type <typeparamref name="T"/> and then the shell the engine is
/// handed. The header takes <see cref="Size"/> bytes, 16-aligned, so the shell keeps the block's alignment.</summary>
public static unsafe class ShellBlock<T> where T : unmanaged
{
    /// <summary>Bytes from the block start to the shell.</summary>
    public static readonly int Size = (sizeof(T) + 15) & ~15;

    /// <summary>The shell inside <paramref name="block"/>.</summary>
    public static nint ShellOf(void* block) => (nint)block + Size;

    /// <summary>The block a shell lives in.</summary>
    public static void* BlockOf(nint shell) => (void*)(shell - Size);

    /// <summary>The header in front of a shell.</summary>
    public static T* Header(nint shell) => (T*)(shell - Size);
}
