using System.Runtime.CompilerServices;

namespace CX.Rhi;

/// <summary>Whether a range lies inside a size, tested by subtracting from the size so that no start, however large,
/// wraps a sum back inside. The one way the backend tests a range (CX0010 refuses <c>at + n &gt; size</c>).</summary>
public static class Bounds
{
    /// <summary><paramref name="n"/> items from <paramref name="at"/> lie inside <paramref name="size"/>; a negative
    /// start or count lies nowhere.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Inside(long at, long n, long size) => at >= 0 && n >= 0 && at <= size && n <= size - at;

    /// <inheritdoc cref="Inside(long, long, long)"/>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Inside(ulong at, ulong n, ulong size) => at <= size && n <= size - at;

    /// <summary><paramref name="bytes"/> from <paramref name="at"/> lie inside <paramref name="size"/> and start and
    /// end on a word, as Vulkan asks of an indirect, update, fill or buffer-to-buffer copy range.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool InWords(long at, long bytes, long size) => ((at | bytes) & 3) == 0 && Inside(at, bytes, size);
}
