using System;
using System.Collections.Generic;
using Vortice.Vulkan;

namespace CX.Graphics.Rhi;

internal sealed unsafe partial class VkBackend
{
    /// <summary>The pool every bind set comes from, and the sets it holds by the bindings they were written with. It
    /// outlives submits, so a frame that repeats the last one's binds writes no set; a destroyed buffer, image or
    /// sampler marks it <see cref="Stale"/>, since a handle the cache names may be reused once freed.</summary>
    private sealed class SetPoolState
    {
        public VkDescriptorPool Pool;
        public readonly SetCache Cache = new();
        public uint Left = SetsPerPool;
        public bool Stale;
        /// <summary>Pools retired since the last take: by a destroy, or by filling up.</summary>
        public int Retired;
        /// <summary>Retired pools and the timeline value each waits out, reset and reused once no submit in flight can
        /// bind their sets.</summary>
        public readonly List<(long At, VkDescriptorPool Pool)> Spare = new();
    }

    /// <summary>What one descriptor of a bind set was written with: a buffer's handle, offset and range, or an image
    /// view's handle, its sampler and the layout it is read in.</summary>
    private readonly record struct BindingKey(uint Binding, VkDescriptorType Type, VkImageLayout Layout, ulong Object,
                                              ulong Sampler, ulong Offset, ulong Range);

    private readonly SetPoolState _sets = new();

    private VkResult MakeSetPool(out VkDescriptorPool pool)
    {
        VkDescriptorPoolSize* sizes = stackalloc VkDescriptorPoolSize[5]
        {
            new() { type = VkDescriptorType.CombinedImageSampler, descriptorCount = SetsPerPool * SampledSlots },
            new() { type = VkDescriptorType.UniformBuffer, descriptorCount = SetsPerPool * Math.Max(UniformSlots - _dynUbos, 1) },
            new() { type = VkDescriptorType.UniformBufferDynamic, descriptorCount = SetsPerPool * Math.Max(_dynUbos, 1) },
            new() { type = VkDescriptorType.StorageBuffer, descriptorCount = SetsPerPool * StorageSlots },
            new() { type = VkDescriptorType.StorageImage, descriptorCount = SetsPerPool * ImageSlots },
        };
        var ci = new VkDescriptorPoolCreateInfo { maxSets = SetsPerPool, poolSizeCount = 5, pPoolSizes = sizes };
        VkDescriptorPool p;
        VkResult r = _vkd.vkCreateDescriptorPool(&ci, &p);
        pool = p;
        return r;
    }

    /// <summary>An empty pool -- a spare past every submit in flight, reset, or a new one -- and an empty cache; the
    /// old pool becomes a spare.</summary>
    private bool RetireSets()
    {
        List<(long At, VkDescriptorPool Pool)> spare = _sets.Spare;
        int i = spare.Count - 1;
        while (i >= 0 && !Passed(spare[i].At)) i--;
        VkDescriptorPool fresh;
        if (i >= 0)
        {
            fresh = spare[i].Pool;
            spare.RemoveAt(i);
            _vkd.vkResetDescriptorPool(fresh, 0);
        }
        else if (MakeSetPool(out fresh) != VkResult.Success) return Fault("a new descriptor pool was refused");
        spare.Add((Next, _sets.Pool));
        _sets.Pool = fresh;
        _sets.Cache.Clear();
        _sets.Left = SetsPerPool;
        _sets.Stale = false;
        _sets.Retired++;
        return true;
    }

    /// <summary>The bind sets one pool has written, by the bindings they were written with, so a bind that repeats one
    /// rebinds it instead of allocating and writing another.</summary>
    private sealed class SetCache
    {
        private readonly record struct Entry(int At, int Len, ulong Set, int Next);

        /// <summary>The newest entry per hash; an entry's <see cref="Entry.Next"/> chains the older ones on it.</summary>
        private readonly Dictionary<int, int> _head = new();
        private readonly List<Entry> _entries = new();
        private BindingKey[] _keys = new BindingKey[1024];
        private int _used;

        public bool TryGet(ReadOnlySpan<BindingKey> key, out ulong set) => TryGetHashed(key, Hash(key), out set);

        public void Add(ReadOnlySpan<BindingKey> key, ulong set) => AddHashed(key, Hash(key), set);

        public bool TryGetHashed(ReadOnlySpan<BindingKey> key, int hash, out ulong set)
        {
            for (int i = _head.GetValueOrDefault(hash, -1); i >= 0; i = _entries[i].Next)
            {
                Entry e = _entries[i];
                if (_keys.AsSpan(e.At, e.Len).SequenceEqual(key)) { set = e.Set; return true; }
            }
            set = 0;
            return false;
        }

        public void AddHashed(ReadOnlySpan<BindingKey> key, int hash, ulong set)
        {
            if (_used + key.Length > _keys.Length) Array.Resize(ref _keys, Math.Max(_keys.Length * 2, _used + key.Length));
            key.CopyTo(_keys.AsSpan(_used));
            _entries.Add(new Entry(_used, key.Length, set, _head.GetValueOrDefault(hash, -1)));
            _head[hash] = _entries.Count - 1;
            _used += key.Length;
        }

        public void Clear()
        {
            _head.Clear();
            _entries.Clear();
            _used = 0;
        }

        private static int Hash(ReadOnlySpan<BindingKey> key)
        {
            var h = new HashCode();
            foreach (BindingKey b in key) h.Add(b);
            return h.ToHashCode();
        }
    }
}
