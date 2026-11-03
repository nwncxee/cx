using System;
using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>A native allocation and the element count it was taken at, in one place: every span comes from the
/// allocation, so no caller supplies a length. Zeroed when taken and when <see cref="Clear"/> runs.</summary>
internal unsafe struct NativeBuffer<T> where T : unmanaged
{
    private T* _at;
    private int _n;

    /// <summary>Elements the allocation holds, 0 before the first <see cref="Take"/>.</summary>
    internal int Length => _n;

    /// <summary>The whole allocation, empty before the first <see cref="Take"/>.</summary>
    internal Span<T> Span => new(_at, _n);   // the one place a length comes from the allocation itself

    /// <summary>The allocation's address, for a native call that takes a pointer; null before the first
    /// <see cref="Take"/>.</summary>
    internal T* Pointer => _at;

    /// <summary>Hold at least <paramref name="count"/> zeroed elements, reallocating only when the count grew.
    /// False, and the buffer left as it was, when the count is not positive or the allocator refused.</summary>
    internal bool Take(int count)
    {
        if (count <= 0) return false;
        if (_n >= count) { Span.Clear(); return true; }
        var at = (T*)NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(T));
        if (at == null) return false;
        NativeMemory.Free(_at);
        _at = at; _n = count;
        return true;
    }

    /// <summary>Zero every element.</summary>
    internal void Clear() => Span.Clear();

    /// <summary>Release the allocation; the buffer reads empty afterwards and may be taken again.</summary>
    internal void Free()
    {
        NativeMemory.Free(_at);
        _at = null; _n = 0;
    }
}

/// <summary>Proof of <see cref="NativeBuffer{T}"/>'s contract.</summary>
internal static unsafe class NativeBufferCheck
{
    /// <summary>A buffer answers for its own length, keeps an allocation already big enough, grows to one that is
    /// not, and reads empty once freed. Oracle: the literal counts asked for and the values written.</summary>
    internal static void SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "NativeBuffer.SelfCheck");
        var buf = default(NativeBuffer<int>);
        check.Expect(buf.Length == 0 && buf.Span.IsEmpty && buf.Pointer == null, "an untaken buffer is empty and has no address");
        check.Expect(!buf.Take(0) && buf.Length == 0, "a count of zero is refused and takes nothing");
        check.Expect(buf.Take(4) && buf.Length == 4 && buf.Span.Length == 4, $"a taken buffer's span is as long as the count ({buf.Length})");
        buf.Span[3] = 1781;
        int* kept = buf.Pointer;
        check.Expect(buf.Take(4) && buf.Pointer == kept && buf.Span[3] == 0,
                     "a second take of the same count keeps the allocation and zeroes what it held");
        buf.Span[3] = 965;
        check.Expect(buf.Take(9) && buf.Length == 9 && buf.Span[3] == 0 && buf.Span[8] == 0,
                     $"growing takes a bigger zeroed allocation ({buf.Length})");
        buf.Span[0] = 42;
        buf.Clear();
        check.Expect(buf.Span[0] == 0, "Clear zeroes the allocation without resizing it");
        buf.Free();
        check.Expect(buf.Length == 0 && buf.Pointer == null && buf.Span.IsEmpty, "a freed buffer reads empty");
        check.Expect(buf.Take(2) && buf.Length == 2, $"...and can be taken again ({buf.Length})");
        buf.Free();
        log.Info(check.Ok ? "NativeBuffer.SelfCheck: passed" : "NativeBuffer.SelfCheck: FAILED");
    }
}
