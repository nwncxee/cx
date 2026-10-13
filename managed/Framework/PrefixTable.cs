using System;
using System.Collections.Generic;

namespace CX.Framework;

/// <summary>
/// The wildcard keys of a policy family: a key ending in <c>*</c> declares a prefix, and a lookup takes the longest declared
/// prefix of an id. Immutable once built, so a reader holds one reference and never sees it half-made. Lookups allocate nothing.
/// </summary>
internal sealed class PrefixTable<T> where T : class
{
    private readonly string[] _prefixes;
    private readonly T[] _values;

    /// <summary>A table with no prefixes.</summary>
    public static readonly PrefixTable<T> Empty = new([], []);

    private PrefixTable(string[] prefixes, T[] values) { _prefixes = prefixes; _values = values; }

    /// <summary>The prefixes among <paramref name="map"/>'s keys (those ending in <c>*</c>, longer than the star alone), longest first.</summary>
    public static PrefixTable<T> Build(Dictionary<string, T> map)
    {
        var list = new List<(string P, T V)>();
        foreach (var kv in map)
            if (kv.Key.Length > 1 && kv.Key[^1] == '*') list.Add((kv.Key[..^1], kv.Value));
        if (list.Count == 0) return Empty;
        list.Sort(static (a, b) => b.P.Length != a.P.Length ? b.P.Length.CompareTo(a.P.Length) : string.CompareOrdinal(a.P, b.P));
        var p = new string[list.Count];
        var v = new T[list.Count];
        for (int i = 0; i < p.Length; i++) { p[i] = list[i].P; v[i] = list[i].V; }
        return new PrefixTable<T>(p, v);
    }

    /// <summary>The value of the longest declared prefix of <paramref name="id"/>, ignoring case; null when none is.</summary>
    public T? Find(ReadOnlySpan<char> id)
    {
        for (int i = 0; i < _prefixes.Length; i++)
            if (id.StartsWith(_prefixes[i], StringComparison.OrdinalIgnoreCase)) return _values[i];
        return null;
    }
}
