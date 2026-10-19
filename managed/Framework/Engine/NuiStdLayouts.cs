using System;
using CX.Framework;

namespace CX.Engine;

/// <summary>Where one compiler keeps the parts of a <c>std::string</c>. libstdc++: a data pointer, the length at +8 and a
/// 16-byte inline buffer at +16 that also holds the capacity of a heap string; the pointer is always valid. MSVC: the inline
/// buffer first, then the length at +16 and the capacity at +24; the pointer is valid only past 15 characters.</summary>
internal readonly struct StdStringShape(int sizeAt, int capAt, int inlineAt)
{
    /// <summary>Offset of the length.</summary>
    public readonly int SizeAt = sizeAt;
    /// <summary>Offset of the capacity word, or -1 when the capacity lives in the inline buffer.</summary>
    public readonly int CapAt = capAt;
    /// <summary>Offset of the inline buffer.</summary>
    public readonly int InlineAt = inlineAt;
}

/// <summary>One compiler's container layouts: the <c>std::string</c> and the <c>std::map</c> tree nodes. Chosen once, when a
/// view is made: the running compiler's (<see cref="Native"/>), or the other one for a check that builds both. libstdc++'s
/// tree keeps its root in the map header and ends a branch with null; MSVC's keeps a head node whose parent is the root and
/// ends a branch with that head, whose <c>_Isnil</c> byte is set.</summary>
public readonly struct StdAbi
{
    internal readonly StdStringShape String;
    internal readonly bool HeadNode;
    internal readonly int RootAt, HeadParentAt, LeftAt, ParentAt, RightAt, NilAt;
    /// <summary>The compiler's name, for a check's messages.</summary>
    public readonly string Name;

    private StdAbi(string name, StdStringShape str, bool headNode, int rootAt, int headParentAt, int leftAt, int parentAt, int rightAt, int nilAt)
    {
        Name = name; String = str; HeadNode = headNode; RootAt = rootAt; HeadParentAt = headParentAt;
        LeftAt = leftAt; ParentAt = parentAt; RightAt = rightAt; NilAt = nilAt;
    }

    /// <summary>libstdc++'s layouts.</summary>
    public static readonly StdAbi Gcc = new("libstdc++", new(8, -1, 16), false, 16, 0, 16, 8, 24, 0);
    /// <summary>MSVC's layouts.</summary>
    public static readonly StdAbi Msvc = new("MSVC", new(16, 24, 0), true, 0, 8, 0, 8, 16, 25);
    /// <summary>The running compiler's.</summary>
    public static StdAbi Native => OperatingSystem.IsWindows() ? Msvc : Gcc;
}

/// <summary>A <c>std::string</c> in one compiler's shape, 32 bytes on both. The shape is chosen when the view is made: the
/// running compiler's, or the other one for a check that builds both.</summary>
public readonly unsafe struct NuiStdString
{
    /// <summary>The address of the string object.</summary>
    public readonly byte* Ptr;
    private readonly StdStringShape _shape;

    /// <summary>The object's size.</summary>
    public const int Bytes = 32;
    /// <summary>The most characters that fit inline.</summary>
    public const int InlineMax = 15;

    /// <summary>Views the string at <paramref name="ptr"/> in the running compiler's shape.</summary>
    public NuiStdString(byte* ptr) : this(ptr, StdAbi.Native) { }

    /// <summary>Views the string at <paramref name="ptr"/> in <paramref name="abi"/>'s shape.</summary>
    public NuiStdString(byte* ptr, StdAbi abi)
    {
        Ptr = ptr;
        _shape = abi.String;
    }

    /// <summary>The character count.</summary>
    public ulong Length => *(ulong*)(Ptr + _shape.SizeAt);

    /// <summary>The characters: MSVC keeps up to 15 inline, libstdc++ always points at them.</summary>
    public byte* Chars => _shape.CapAt >= 0 && *(ulong*)(Ptr + _shape.CapAt) < InlineMax + 1 ? Ptr : *(byte**)Ptr;

    /// <summary>Where a string of up to <see cref="InlineMax"/> characters keeps them.</summary>
    public byte* Inline => Ptr + _shape.InlineAt;

    /// <summary>Points the string at <paramref name="n"/> characters that something else owns: pointer and length, and MSVC's
    /// capacity.</summary>
    public void Borrow(byte* chars, ulong n)
    {
        *(byte**)Ptr = chars;
        *(ulong*)(Ptr + _shape.SizeAt) = n;
        if (_shape.CapAt >= 0) *(ulong*)(Ptr + _shape.CapAt) = n;
    }

    /// <summary>Points the string at <paramref name="chars"/> (<see cref="Inline"/> when <paramref name="n"/> fits, else a heap
    /// block of <c>n + 1</c> bytes) and sets its length and capacity.</summary>
    public void Set(byte* chars, ulong n)
    {
        bool heap = n > InlineMax;
        if (heap || _shape.CapAt < 0) *(byte**)Ptr = chars;
        *(ulong*)(Ptr + _shape.SizeAt) = n;
        if (_shape.CapAt >= 0) *(ulong*)(Ptr + _shape.CapAt) = heap ? n : InlineMax;
        else if (heap) *(ulong*)(Ptr + _shape.InlineAt) = n;
    }
}

/// <summary>An <c>nlohmann::json</c> value: 16 bytes on both builds, a type byte and then the payload at +8. Values of an array
/// lie end to end.</summary>
public readonly unsafe struct NuiJsonValue
{
    /// <summary>The address of the value.</summary>
    public readonly byte* Ptr;
    private readonly StdAbi _abi;

    /// <summary>Views the value at <paramref name="ptr"/> with the running compiler's containers.</summary>
    public NuiJsonValue(byte* ptr) : this(ptr, StdAbi.Native) { }

    /// <summary>Views the value at <paramref name="ptr"/> with <paramref name="abi"/>'s containers.</summary>
    public NuiJsonValue(byte* ptr, StdAbi abi)
    {
        Ptr = ptr;
        _abi = abi;
    }

    /// <summary>The value's size.</summary>
    public const int Bytes = 16;
    private const int PayloadAt = 8;

    /// <summary>The value's type byte.</summary>
    public ref byte Type => ref *Ptr;
    /// <summary>The payload read as a pointer: the object's map, the array's vector or the string.</summary>
    public ref byte* Pointer => ref *(byte**)(Ptr + PayloadAt);
    /// <summary>The payload as a string.</summary>
    public NuiStdString String => new(*(byte**)(Ptr + PayloadAt), _abi);
    /// <summary>The payload as an object's map.</summary>
    public NuiJsonMap Map => new(*(byte**)(Ptr + PayloadAt), _abi);
    /// <summary>The payload read as a boolean byte.</summary>
    public ref byte Bool => ref Ptr[PayloadAt];
    /// <summary>The payload read as a signed integer.</summary>
    public ref long Int => ref *(long*)(Ptr + PayloadAt);
    /// <summary>The payload read as an unsigned integer.</summary>
    public ref ulong Uint => ref *(ulong*)(Ptr + PayloadAt);
    /// <summary>The payload read as a double.</summary>
    public ref double Float => ref *(double*)(Ptr + PayloadAt);
    /// <summary>The value <paramref name="i"/> places after this one in an array.</summary>
    public NuiJsonValue At(int i) => new(Ptr + i * Bytes, _abi);
    /// <summary>The value at <paramref name="first"/>, an element of this value's array, in the same containers.</summary>
    public NuiJsonValue Elements(byte* first) => new(first, _abi);
    /// <summary>The next value in an array.</summary>
    public NuiJsonValue Next() => new(Ptr + Bytes, _abi);
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
public readonly unsafe struct NuiJsonMap
{
    /// <summary>The address of the map object.</summary>
    public readonly byte* Ptr;
    private readonly StdAbi _abi;
    /// <summary>The object's size.</summary>
    public const int Bytes = 48;

    /// <summary>Views the map at <paramref name="ptr"/> in <paramref name="abi"/>'s layout.</summary>
    public NuiJsonMap(byte* ptr, StdAbi abi)
    {
        Ptr = ptr;
        _abi = abi;
    }

    /// <summary>The root node, or null for an empty tree. <paramref name="head"/> is MSVC's head node (the end marker), null on libstdc++.</summary>
    public NuiJsonMapNode Root(out NuiJsonMapNode head)
    {
        if (!_abi.HeadNode)
        {
            head = default;
            return new(*(byte**)(Ptr + _abi.RootAt), _abi);
        }
        head = new(*(byte**)(Ptr + _abi.RootAt), _abi);
        return head.Ptr == null ? default : new(*(byte**)(head.Ptr + _abi.HeadParentAt), _abi);
    }

    /// <summary>Points the map at <paramref name="head"/> (MSVC) with <paramref name="root"/> as its root, or at the root alone (libstdc++).</summary>
    public void SetRoot(NuiJsonMapNode root, NuiJsonMapNode head)
    {
        if (_abi.HeadNode)
        {
            *(byte**)(Ptr + _abi.RootAt) = head.Ptr;
            *(byte**)(head.Ptr + _abi.HeadParentAt) = root.Ptr == null ? head.Ptr : root.Ptr;
        }
        else *(byte**)(Ptr + _abi.RootAt) = root.Ptr;
    }
}

/// <summary>A node of <see cref="NuiJsonMap"/>: the compiler's 32-byte tree header, then the key string and the value.</summary>
public readonly unsafe struct NuiJsonMapNode
{
    /// <summary>The address of the node.</summary>
    public readonly byte* Ptr;
    private readonly StdAbi _abi;
    /// <summary>The node's size.</summary>
    public const int Bytes = 80;
    private const int KeyAt = 32, ValueAt = 64;

    /// <summary>Views the node at <paramref name="ptr"/> in <paramref name="abi"/>'s layout.</summary>
    public NuiJsonMapNode(byte* ptr, StdAbi abi)
    {
        Ptr = ptr;
        _abi = abi;
    }

    /// <summary>The key string.</summary>
    public NuiStdString Key => new(Ptr + KeyAt, _abi);
    /// <summary>The value.</summary>
    public NuiJsonValue Value => new(Ptr + ValueAt, _abi);
    /// <summary>The left child.</summary>
    public NuiJsonMapNode Left => new(*(byte**)(Ptr + _abi.LeftAt), _abi);
    /// <summary>The right child.</summary>
    public NuiJsonMapNode Right => new(*(byte**)(Ptr + _abi.RightAt), _abi);

    /// <summary>Whether a branch ends here: libstdc++ ends one with a null, MSVC with the head node, whose <c>_Isnil</c> byte is set.</summary>
    public bool IsEnd(NuiJsonMapNode head) => Ptr == null || (_abi.HeadNode && (Ptr == head.Ptr || Ptr[_abi.NilAt] != 0));

    /// <summary>Marks this node as MSVC's head (end marker).</summary>
    public void MarkHead() { if (_abi.HeadNode) Ptr[_abi.NilAt] = 1; }

    /// <summary>Links the node into a tree; a missing link is null on libstdc++ and the head node on MSVC.</summary>
    public void Link(NuiJsonMapNode left, NuiJsonMapNode parent, NuiJsonMapNode right, NuiJsonMapNode head)
    {
        byte* none = _abi.HeadNode ? head.Ptr : null;
        *(byte**)(Ptr + _abi.LeftAt) = left.Ptr == null ? none : left.Ptr;
        *(byte**)(Ptr + _abi.ParentAt) = parent.Ptr == null ? none : parent.Ptr;
        *(byte**)(Ptr + _abi.RightAt) = right.Ptr == null ? none : right.Ptr;
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
        private readonly bool _windows;
        private readonly byte** _map;
        private readonly ulong _off, _mask, _size;
        private ulong _i;
        private byte* _p, _end, _last;
        private byte** _node;
        private int _n;

        internal Enumerator(byte* q)
        {
            _windows = OperatingSystem.IsWindows();
            Current = default;
            if (_windows)
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
                if (_windows)
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
