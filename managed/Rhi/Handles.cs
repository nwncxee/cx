using System;

namespace CX.Rhi;

/// <summary>A buffer the device owns. Value 0 is null; a stale id is refused by its generation, never misdrawn.</summary>
public readonly record struct BufferId(uint Value) { public bool IsNull => Value == 0; }

/// <summary>An image (texture or target) the device owns.</summary>
public readonly record struct ImageId(uint Value) { public bool IsNull => Value == 0; }

/// <summary>A sampler object the device owns.</summary>
public readonly record struct SamplerId(uint Value) { public bool IsNull => Value == 0; }

/// <summary>One shader stage's source, held until a pipeline links it.</summary>
public readonly record struct ShaderId(uint Value) { public bool IsNull => Value == 0; }

/// <summary>A linked program plus its fixed-function state, resolved at build time so a frame binds it by index.</summary>
public readonly record struct PipelineId(uint Value) { public bool IsNull => Value == 0; }

/// <summary>A bindless texture reference, 64 bits on every backend: GL's resident handle, or a Vulkan descriptor
/// index zero-extended.</summary>
public readonly record struct BindlessHandle(ulong Value);

/// <summary>Handle-indexed storage for one resource kind: a 20-bit slot index under a 12-bit generation, so an id
/// whose slot was freed and reused no longer resolves. Allocates only when the slot array grows.</summary>
public sealed class HandleTable<T>
{
    /// <summary>Bits of a handle that index the slot; the rest carry its generation.</summary>
    public const int IndexBits = 20;
    private const uint IndexMask = (1u << IndexBits) - 1;
    private const uint GenMax = (1u << (32 - IndexBits)) - 1;

    private T[] _items = new T[16];
    private uint[] _gen = new uint[16];
    private int[] _free = new int[16];
    private int _freeCount, _next = 1;

    /// <summary>Live entries.</summary>
    public int Count { get; private set; }

    /// <summary>Store <paramref name="item"/> and return its handle value (never 0).</summary>
    public uint Add(T item)
    {
        int i;
        if (_freeCount > 0) i = _free[--_freeCount];
        else
        {
            if (_next > IndexMask) throw new InvalidOperationException($"HandleTable<{typeof(T).Name}>: {IndexMask} slots in use");
            i = _next++;
            if (i >= _items.Length)
            {
                Array.Resize(ref _items, _items.Length * 2);
                Array.Resize(ref _gen, _gen.Length * 2);
            }
        }
        if (_gen[i] == 0) _gen[i] = 1;
        _items[i] = item;
        Count++;
        return (_gen[i] << IndexBits) | (uint)i;
    }

    /// <summary>The entry <paramref name="h"/> names, or false when it is null, out of range or stale.</summary>
    public bool TryGet(uint h, out T item)
    {
        int i = (int)(h & IndexMask);
        if (h == 0 || i >= _next || _gen[i] != h >> IndexBits) { item = default!; return false; }
        item = _items[i];
        return true;
    }

    /// <summary>Every live entry, for a device's teardown. Cold: it allocates.</summary>
    public System.Collections.Generic.List<T> Live()
    {
        var freed = new bool[_next];
        for (int f = 0; f < _freeCount; f++) freed[_free[f]] = true;
        var live = new System.Collections.Generic.List<T>(Count);
        for (int i = 1; i < _next; i++) if (!freed[i]) live.Add(_items[i]);
        return live;
    }

    /// <summary>Free <paramref name="h"/>'s slot and bump its generation, so every copy of the handle goes stale.</summary>
    public bool Remove(uint h, out T item)
    {
        if (!TryGet(h, out item)) return false;
        int i = (int)(h & IndexMask);
        _items[i] = default!;
        _gen[i] = _gen[i] == GenMax ? 1 : _gen[i] + 1;
        if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
        _free[_freeCount++] = i;
        Count--;
        return true;
    }
}
