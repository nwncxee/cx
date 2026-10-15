using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>A <c>std::string</c> in the running compiler's shape, 32 bytes on both: libstdc++ keeps a data pointer, a length and
/// a 16-byte inline buffer; MSVC keeps the inline buffer first, then the length and the capacity, and up to 15 characters
/// live inline.</summary>
public readonly unsafe struct NuiStdString(byte* ptr)
{
    /// <summary>The address of the string object.</summary>
    public readonly byte* Ptr = ptr;
    /// <summary>The object's size.</summary>
    public const int Bytes = 32;
    /// <summary>The most characters that fit inline.</summary>
    public const int InlineMax = 15;
    private const int GccSize = 8, GccBuf = 16, MsvcSize = 16, MsvcCap = 24;

    /// <summary>The character count.</summary>
    public ulong Length(bool msvc) => *(ulong*)(Ptr + (msvc ? MsvcSize : GccSize));

    /// <summary>The characters: MSVC keeps up to 15 inline, libstdc++ always points at them.</summary>
    public byte* Chars(bool msvc) => msvc && *(ulong*)(Ptr + MsvcCap) < 16 ? Ptr : *(byte**)Ptr;

    /// <summary>Where a string of up to <see cref="InlineMax"/> characters keeps them.</summary>
    public byte* Inline(bool msvc) => msvc ? Ptr : Ptr + GccBuf;

    /// <summary>Points the string at <paramref name="n"/> characters that something else owns: pointer and length, and MSVC's
    /// capacity.</summary>
    public void Borrow(bool msvc, byte* chars, ulong n)
    {
        *(byte**)Ptr = chars;
        if (msvc)
        {
            *(ulong*)(Ptr + MsvcSize) = n;
            *(ulong*)(Ptr + MsvcCap) = n;
        }
        else *(ulong*)(Ptr + GccSize) = n;
    }

    /// <summary>Points the string at <paramref name="chars"/> (<see cref="Inline"/> when <paramref name="n"/> fits, else a heap
    /// block of <c>n + 1</c> bytes) and sets its length and capacity.</summary>
    public void Set(bool msvc, byte* chars, ulong n)
    {
        bool heap = n > InlineMax;
        if (msvc)
        {
            if (heap) *(byte**)Ptr = chars;
            *(ulong*)(Ptr + MsvcSize) = n;
            *(ulong*)(Ptr + MsvcCap) = heap ? n : InlineMax;
        }
        else
        {
            *(byte**)Ptr = chars;
            *(ulong*)(Ptr + GccSize) = n;
            if (heap) *(ulong*)(Ptr + GccBuf) = n;
        }
    }
}

/// <summary>An <c>nlohmann::json</c> value: 16 bytes on both builds, a type byte and then the payload at +8. Values of an array
/// lie end to end.</summary>
public readonly unsafe struct NuiJsonValue(byte* ptr)
{
    /// <summary>The address of the value.</summary>
    public readonly byte* Ptr = ptr;
    /// <summary>The value's size.</summary>
    public const int Bytes = 16;
    private const int PayloadAt = 8;

    /// <summary>The value's type byte.</summary>
    public ref byte Type => ref *Ptr;
    /// <summary>The payload read as a pointer: the object's map, the array's vector or the string.</summary>
    public ref byte* Pointer => ref *(byte**)(Ptr + PayloadAt);
    /// <summary>The payload read as a boolean byte.</summary>
    public ref byte Bool => ref Ptr[PayloadAt];
    /// <summary>The payload read as a signed integer.</summary>
    public ref long Int => ref *(long*)(Ptr + PayloadAt);
    /// <summary>The payload read as an unsigned integer.</summary>
    public ref ulong Uint => ref *(ulong*)(Ptr + PayloadAt);
    /// <summary>The payload read as a double.</summary>
    public ref double Float => ref *(double*)(Ptr + PayloadAt);
    /// <summary>The payload's address.</summary>
    public byte* PayloadPtr => Ptr + PayloadAt;
    /// <summary>The value <paramref name="i"/> places after this one in an array.</summary>
    public NuiJsonValue At(int i) => new(Ptr + i * Bytes);
    /// <summary>The next value in an array.</summary>
    public NuiJsonValue Next() => new(Ptr + Bytes);
    /// <summary>Copies another value's 16 bytes over this one.</summary>
    public void CopyFrom(NuiJsonValue other) => Buffer.MemoryCopy(other.Ptr, Ptr, Bytes, Bytes);
}

/// <summary>A <c>std::vector</c>: begin, end and capacity pointers, the same on both compilers.</summary>
public readonly unsafe struct NuiJsonVector(byte* ptr)
{
    /// <summary>The address of the vector object.</summary>
    public readonly byte* Ptr = ptr;
    /// <summary>The object's size.</summary>
    public const int Bytes = 24;

    /// <summary>The first element.</summary>
    public ref byte* Begin => ref ((byte**)Ptr)[0];
    /// <summary>One past the last element.</summary>
    public ref byte* End => ref ((byte**)Ptr)[1];
    /// <summary>One past the storage.</summary>
    public ref byte* Cap => ref ((byte**)Ptr)[2];
}

/// <summary>A <c>std::map&lt;std::string, json&gt;</c>: libstdc++'s red-black tree (the root in the header at +16) or MSVC's (a
/// head node whose parent is the root).</summary>
public readonly unsafe struct NuiJsonMap(byte* ptr)
{
    /// <summary>The address of the map object.</summary>
    public readonly byte* Ptr = ptr;
    /// <summary>The object's size.</summary>
    public const int Bytes = 48;
    private const int GccRoot = 16, MsvcParent = 8;

    /// <summary>The root node, or null for an empty tree. <paramref name="head"/> is MSVC's head node (the end marker), null on libstdc++.</summary>
    public NuiJsonMapNode Root(bool msvc, out NuiJsonMapNode head)
    {
        if (!msvc)
        {
            head = default;
            return new(*(byte**)(Ptr + GccRoot));
        }
        head = new(*(byte**)Ptr);
        return head.Ptr == null ? default : new(*(byte**)(head.Ptr + MsvcParent));
    }

    /// <summary>Points the map at <paramref name="head"/> (MSVC) with <paramref name="root"/> as its root, or at the root alone (libstdc++).</summary>
    public void SetRoot(bool msvc, NuiJsonMapNode root, NuiJsonMapNode head)
    {
        if (msvc)
        {
            *(byte**)Ptr = head.Ptr;
            *(byte**)(head.Ptr + MsvcParent) = root.Ptr == null ? head.Ptr : root.Ptr;
        }
        else *(byte**)(Ptr + GccRoot) = root.Ptr;
    }
}

/// <summary>A node of <see cref="NuiJsonMap"/>: the compiler's 32-byte tree header, then the key string and the value.</summary>
public readonly unsafe struct NuiJsonMapNode(byte* ptr)
{
    /// <summary>The address of the node.</summary>
    public readonly byte* Ptr = ptr;
    /// <summary>The node's size.</summary>
    public const int Bytes = 80;
    private const int GccParent = 8, GccLeft = 16, GccRight = 24;
    private const int MsvcLeft = 0, MsvcParent = 8, MsvcRight = 16, MsvcIsNil = 25;
    private const int KeyAt = 32, ValueAt = 64;

    /// <summary>The key string.</summary>
    public NuiStdString Key => new(Ptr + KeyAt);
    /// <summary>The value.</summary>
    public NuiJsonValue Value => new(Ptr + ValueAt);
    /// <summary>The left child.</summary>
    public NuiJsonMapNode Left(bool msvc) => new(*(byte**)(Ptr + (msvc ? MsvcLeft : GccLeft)));
    /// <summary>The right child.</summary>
    public NuiJsonMapNode Right(bool msvc) => new(*(byte**)(Ptr + (msvc ? MsvcRight : GccRight)));

    /// <summary>Whether a branch ends here: libstdc++ ends one with a null, MSVC with the head node, whose <c>_Isnil</c> byte is set.</summary>
    public bool IsEnd(NuiJsonMapNode head, bool msvc) => Ptr == null || (msvc && (Ptr == head.Ptr || Ptr[MsvcIsNil] != 0));

    /// <summary>Marks this node as MSVC's head (end marker).</summary>
    public void MarkHead() => Ptr[MsvcIsNil] = 1;

    /// <summary>Links the node into a tree; a missing link is null on libstdc++ and the head node on MSVC.</summary>
    public void Link(bool msvc, NuiJsonMapNode left, NuiJsonMapNode parent, NuiJsonMapNode right, NuiJsonMapNode head)
    {
        if (msvc)
        {
            *(byte**)(Ptr + MsvcLeft) = left.Ptr == null ? head.Ptr : left.Ptr;
            *(byte**)(Ptr + MsvcParent) = parent.Ptr == null ? head.Ptr : parent.Ptr;
            *(byte**)(Ptr + MsvcRight) = right.Ptr == null ? head.Ptr : right.Ptr;
        }
        else
        {
            *(byte**)(Ptr + GccParent) = parent.Ptr;
            *(byte**)(Ptr + GccLeft) = left.Ptr;
            *(byte**)(Ptr + GccRight) = right.Ptr;
        }
    }
}

/// <summary>The window's bind-update queue, a <c>std::deque</c> of <c>Nui::JSON::BindUpdate</c>: on Linux libstdc++'s (start and
/// finish iterators over 8-record node buffers), on Windows MSVC's (a map of pointers to records, a start offset and a size).
/// Enumerating yields each queued record that exists; it reads nothing else of the window and stops at 4096 entries.</summary>
public readonly unsafe ref struct NuiBindQueue(byte* queue)
{
    private const int GccStart = 16, GccFinish = 48, GccNode = 24, GccLast = 16;
    private const int MsvcMap = 8, MsvcMapSize = 16, MsvcOff = 24, MsvcSize = 32;
    private const int Cap = 4096, PerNode = 8;
    private readonly byte* _q = queue;

    /// <summary>The records, oldest first.</summary>
    public Enumerator GetEnumerator() => new(_q);

    /// <summary>Walks the queue without allocating.</summary>
    public ref struct Enumerator
    {
        private readonly bool _msvc;
        private readonly byte** _map;
        private readonly ulong _off, _mask, _size;
        private ulong _i;
        private byte* _p, _end, _last;
        private byte** _node;
        private int _n;

        internal Enumerator(byte* q)
        {
            _msvc = OperatingSystem.IsWindows();
            Current = default;
            if (_msvc)
            {
                _size = *(ulong*)(q + MsvcSize);
                _off = *(ulong*)(q + MsvcOff);
                _mask = *(ulong*)(q + MsvcMapSize) - 1;
                _map = *(byte***)(q + MsvcMap);
                if (_size == 0 || _map == null || _size > Cap) _size = 0;
                return;
            }
            _p = *(byte**)(q + GccStart);
            _end = *(byte**)(q + GccFinish);
            if (_p == _end) return;
            _last = *(byte**)(q + GccStart + GccLast);
            _node = *(byte***)(q + GccStart + GccNode);
        }

        /// <summary>The record just reached.</summary>
        public Nui__JSON__BindUpdate Current { get; private set; }

        /// <summary>Moves to the next record; false at the end.</summary>
        public bool MoveNext()
        {
            while (true)
            {
                byte* rec;
                if (_msvc)
                {
                    if (_i >= _size) return false;
                    rec = _map[(_off + _i++) & _mask];
                }
                else
                {
                    if (_p == _end || _n >= Cap) return false;
                    rec = _p;
                    _n++;
                    _p = (byte*)new Nui__JSON__BindUpdate((IntPtr)_p)[1].Ptr;
                    if (_p == _last)
                    {
                        _node++;
                        _p = *_node;
                        _last = (byte*)new Nui__JSON__BindUpdate((IntPtr)_p)[PerNode].Ptr;
                    }
                }
                if (rec == null) continue;
                Current = new Nui__JSON__BindUpdate((IntPtr)rec);
                return true;
            }
        }
    }
}
