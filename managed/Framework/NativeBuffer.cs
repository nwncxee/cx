using System;
using System.Runtime.InteropServices;

namespace CX.Framework;

/// <summary>A native allocation and the element count it was taken at, in one place: every span comes from the
/// allocation, so no caller supplies a length. Zeroed when taken and when <see cref="Clear"/> runs.</summary>
internal unsafe struct NativeBuffer<T> where T : unmanaged
{
    private T* _at;
    private int _n;
    /// <summary>The boundary <see cref="TakeAligned"/> took the allocation on, 0 for a plain one. It decides
    /// which allocator every later take, grow and free of this buffer uses.</summary>
    private int _align;

    /// <summary>Elements the allocation holds, 0 before the first <see cref="Take"/>.</summary>
    internal int Length => _n;

    /// <summary>The whole allocation, empty before the first <see cref="Take"/>.</summary>
    internal Span<T> Span => new(_at, _n);   // the one place a length comes from the allocation itself

    /// <summary>The allocation's address, for a native call that takes a pointer; null before the first
    /// <see cref="Take"/>.</summary>
    internal T* Pointer => _at;

    /// <summary>Element <paramref name="i"/>, bounded by the allocation.</summary>
    internal ref T this[int i] => ref Span[i];

    /// <summary>Hold at least <paramref name="count"/> zeroed elements, reallocating only when the count grew.
    /// False, and the buffer left as it was, when the count is not positive; an allocation that cannot be met
    /// throws rather than answering false.</summary>
    internal bool Take(int count)
    {
        if (count <= 0) return false;
        if (_n >= count) { Span.Clear(); return true; }
        T* at = Fresh(count);
        if (at == null) return false;
        Adopt(at, count);
        return true;
    }

    /// <summary>Hold at least <paramref name="count"/> elements, KEEPING the ones already held and zeroing the
    /// rest. False, and the buffer left exactly as it was, when the count is not positive or the allocation
    /// cannot be met: a stage grows mid-frame on the render thread, where a throw is not survivable and the
    /// caller's recovery is to drop the one part it was staging.</summary>
    internal bool Grow(int count)
    {
        if (count <= 0) return false;
        if (_n >= count) return true;
        T* at;
        try { at = Fresh(count); }
        catch (OutOfMemoryException) { return false; }
        if (at == null) return false;
        Span.CopyTo(new Span<T>(at, count));
        Adopt(at, count);
        return true;
    }

    /// <summary>Hold <paramref name="count"/> zeroed elements whose address is a multiple of
    /// <paramref name="align"/> bytes, for the caller kind that needs the allocation itself on a boundary -- a
    /// vtable the engine calls through. The boundary is the buffer's from here on, so a later
    /// <see cref="Grow"/> stays on it and <see cref="Free"/> gives the block back the way it was taken.</summary>
    internal bool TakeAligned(int count, int align)
    {
        if (align <= 0 || (align & (align - 1)) != 0) return false;
        if (_align != align) Free();
        _align = align;
        return Take(count);
    }

    /// <summary>Zero every element.</summary>
    internal void Clear() => Span.Clear();

    /// <summary>Release the allocation; the buffer reads empty afterwards and may be taken again.</summary>
    internal void Free()
    {
        if (_align == 0) NativeMemory.Free(_at);
        else NativeMemory.AlignedFree(_at);
        _at = null; _n = 0; _align = 0;
    }

    /// <summary>A zeroed allocation of <paramref name="count"/> elements on this buffer's own boundary.</summary>
    private T* Fresh(int count)
    {
        if (_align == 0) return (T*)NativeMemory.AllocZeroed((nuint)count, (nuint)sizeof(T));
        var at = (T*)NativeMemory.AlignedAlloc(checked((nuint)count * (nuint)sizeof(T)), (nuint)_align);
        if (at != null) new Span<T>(at, count).Clear();
        return at;
    }

    /// <summary>Take <paramref name="at"/> over, releasing what the buffer held and keeping its boundary.</summary>
    private void Adopt(T* at, int count)
    {
        int align = _align;
        Free();
        _at = at; _n = count; _align = align;
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
        Grows(check);
        Aligns(check);
        log.Info(check.Ok ? "NativeBuffer.SelfCheck: passed" : "NativeBuffer.SelfCheck: FAILED");
    }

    /// <summary>A stage that grows mid-frame keeps what it already staged. Oracle: the values written before
    /// the grow, which a <see cref="NativeBuffer{T}.Take"/> would have zeroed.</summary>
    private static void Grows(Checker check)
    {
        var buf = default(NativeBuffer<int>);
        check.Expect(!buf.Grow(0) && !buf.Grow(-3) && buf.Length == 0, "a grow to nothing is refused and takes nothing");
        buf.Grow(4);
        for (int i = 0; i < buf.Length; i++) buf.Span[i] = 100 + i;
        int* kept = buf.Pointer;
        check.Expect(buf.Grow(4) && buf.Pointer == kept && buf.Span[0] == 100 && buf.Span[3] == 103,
                     "a grow to the length it already holds keeps the allocation and every element in it");
        check.Expect(buf.Grow(9) && buf.Length == 9, $"a grow past the length reallocates ({buf.Length})");
        bool prefix = true;
        for (int i = 0; i < 4; i++) prefix &= buf.Span[i] == 100 + i;
        bool tail = true;
        for (int i = 4; i < buf.Length; i++) tail &= buf.Span[i] == 0;
        check.Expect(prefix, "...keeping every element the frame had already staged");
        check.Expect(tail, "...and zeroing the elements past them");
        check.Expect(!buf.Grow(0) && buf.Length == 9 && buf.Span[0] == 100,
                     "a refused grow leaves the buffer its length and its elements");
        buf.Free();
        check.Expect(buf.Grow(2) && buf.Length == 2 && buf.Span[0] == 0 && buf.Span[1] == 0,
                     "a freed buffer grows from nothing into zeroed elements");
        buf.Free();
    }

    /// <summary>An aligned take puts the allocation on the boundary asked for and keeps it there across a
    /// grow. Oracle: the address modulo the boundary, read back.</summary>
    private static void Aligns(Checker check)
    {
        const int Boundary = 64;
        var buf = default(NativeBuffer<nint>);
        check.Expect(!buf.TakeAligned(4, 0) && !buf.TakeAligned(4, 48) && buf.Length == 0,
                     "a boundary that is not a power of two is refused and takes nothing");
        check.Expect(buf.TakeAligned(5, Boundary) && buf.Length == 5 && (nuint)buf.Pointer % Boundary == 0,
                     $"an aligned take lands on its boundary (address % {Boundary} = {(nuint)buf.Pointer % Boundary})");
        buf.Span[4] = 1781;
        check.Expect(buf.Grow(40) && (nuint)buf.Pointer % Boundary == 0 && buf.Span[4] == 1781,
                     "...and a grow stays on it, with what the buffer held");
        buf.Free();
        check.Expect(buf.Length == 0 && buf.Pointer == null, "an aligned buffer reads empty once freed");
        check.Expect(buf.Take(3) && buf.Length == 3, $"...and takes plainly afterwards ({buf.Length})");
        buf.Free();
    }
}
