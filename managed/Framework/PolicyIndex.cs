using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;

namespace CX.Framework;

/// <summary>
/// The values a policy family holds by key, case-insensitive, with up to <see cref="MaxKeys"/> keys of at most
/// <see cref="MaxKeyBytes"/> UTF-8 bytes. A write copies the map and publishes the copy whole, so a reader holds one
/// reference and never sees a half-built one. Reads allocate nothing; there is one writer.
/// </summary>
internal sealed class PolicyIndex<T> where T : class
{
    /// <summary>The longest key in UTF-8 bytes.</summary>
    public const int MaxKeyBytes = 96;
    /// <summary>The most keys held at once.</summary>
    public const int MaxKeys = 256;

    private volatile Dictionary<string, T> _map = new(StringComparer.OrdinalIgnoreCase);
    private volatile PrefixTable<T> _prefixes = PrefixTable<T>.Empty;
    private int _gen;

    /// <summary>Bumps whenever a key is put, replaced or released; a reader that cached a lookup compares it.</summary>
    public int Gen => Volatile.Read(ref _gen);

    public int Count => _map.Count;

    /// <summary>Every value held. Cold: it walks the map.</summary>
    public IEnumerable<T> Values => _map.Values;

    /// <summary>True when <paramref name="key"/> is 1-<see cref="MaxKeyBytes"/> bytes. Trim it first.</summary>
    public static bool KeyOk(string key) => key.Length != 0 && Encoding.UTF8.GetByteCount(key) <= MaxKeyBytes;

    /// <summary>True when <paramref name="key"/> is new and <see cref="MaxKeys"/> keys are held already.</summary>
    public bool IsFull(string key) => _map.Count >= MaxKeys && !_map.ContainsKey(key);

    /// <summary>The value held under <paramref name="key"/>, or null. Allocation-free.</summary>
    public T? Find(ReadOnlySpan<char> key)
        => _map.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out T? v) ? v : null;

    /// <summary>The value of the longest declared <c>prefix*</c> key that starts <paramref name="id"/>, or null. Allocation-free.</summary>
    public T? FindPrefix(ReadOnlySpan<char> id) => _prefixes.Find(id);

    /// <summary>Holds <paramref name="value"/> under <paramref name="key"/>; null removes the key.</summary>
    public void Put(string key, T? value)
    {
        var next = new Dictionary<string, T>(_map, StringComparer.OrdinalIgnoreCase);
        if (value == null) next.Remove(key); else next[key] = value;
        Publish(next);
    }

    /// <summary>Drops every key <paramref name="drop"/> accepts.</summary>
    public void Release(Func<string, bool> drop)
    {
        Dictionary<string, T> old = _map;
        var next = new Dictionary<string, T>(old, StringComparer.OrdinalIgnoreCase);
        foreach (string k in old.Keys)
            if (drop(k)) next.Remove(k);
        Publish(next);
    }

    /// <summary>Drops every key whose full policy key (<paramref name="family"/> then the key) starts with <paramref name="prefix"/>; an empty prefix drops all.</summary>
    public void Release(string family, string prefix)
        => Release(k => string.Concat(family, k).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Replaces every value with <paramref name="update"/> of it; a null result drops the key.</summary>
    public void Rebuild(Func<T, T?> update)
    {
        var next = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in _map)
            if (update(kv.Value) is { } v) next[kv.Key] = v;
        Publish(next);
    }

    /// <summary>Holds nothing, and counts from 0 again.</summary>
    public void Reset()
    {
        _prefixes = PrefixTable<T>.Empty;
        _map = new(StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _gen, 0);
    }

    private void Publish(Dictionary<string, T> next)
    {
        _prefixes = PrefixTable<T>.Build(next);
        _map = next;
        Interlocked.Increment(ref _gen);
    }
}
