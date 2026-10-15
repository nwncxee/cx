namespace CX.Engine;

/// <summary>A native block that holds CX's own header and then the shell the engine is handed: the shell address
/// is the block address plus the header size, and the header is read back by stepping the same distance down.</summary>
public static unsafe class ShellBlock
{
    /// <summary>The shell inside <paramref name="block"/>, whose header is <paramref name="headerBytes"/> long.</summary>
    public static nint ShellOf(void* block, int headerBytes) => (nint)block + headerBytes;

    /// <summary>The block a shell lives in.</summary>
    public static void* BlockOf(nint shell, int headerBytes) => (void*)(shell - headerBytes);

    /// <summary>The header in front of a shell.</summary>
    public static T* Header<T>(nint shell, int headerBytes) where T : unmanaged => (T*)(shell - headerBytes);
}
