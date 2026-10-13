using System;
using System.Collections.Generic;
using System.Text;
using CX.Framework;

namespace CX.Services.Nui;

/// <summary>
/// The images a server declared for a press on a NUI element (<c>held.pick.&lt;id&gt;=&lt;image&gt;</c> policy lines), by element id.
/// A key ending in <c>*</c> is a prefix. The index is replaced whole, so the input thread reads one reference.
/// </summary>
internal static class HeldPicks
{
    /// <summary>The <see cref="ServerPolicy"/> prefix every pick key carries.</summary>
    public const string Prefix = "held.pick.";
    private const int MaxKeys = 256, MaxKeyBytes = 96;

    private sealed class PickState
    {
        public volatile Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);
        public volatile PrefixTable<string> Prefixes = PrefixTable<string>.Empty;
    }

    private static PickState _s = new();

    public static int Count => _s.Map.Count;

    /// <summary>Hooks the picks into <see cref="ServerPolicy"/>.</summary>
    public static void Init() => ServerPolicy.Handle(Prefix, Set, Release, static () => $"held picks: {Count} key(s)");

    /// <summary>One <c>held.pick.&lt;key&gt;</c> line; an empty value releases it. False when refused.</summary>
    public static bool Set(string key, string value)
    {
        key = key.Trim();
        if (key.Length == 0 || Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
        {
            PolicyRefusals.Add(RefusalKind.Held, Prefix + key, value, $"the key must be 1-{MaxKeyBytes} bytes");
            return false;
        }
        var next = new Dictionary<string, string>(_s.Map, StringComparer.OrdinalIgnoreCase);
        if (value.Length == 0) next.Remove(key);
        else if (next.Count >= MaxKeys && !next.ContainsKey(key))
        {
            PolicyRefusals.Add(RefusalKind.Held, Prefix + key, value, $"{MaxKeys} pick keys are held already");
            return false;
        }
        else next[key] = value;
        Publish(next);
        return true;
    }

    /// <summary>Drops every key whose full policy key starts with <paramref name="prefix"/>.</summary>
    public static void Release(string prefix)
    {
        var next = new Dictionary<string, string>(_s.Map, StringComparer.OrdinalIgnoreCase);
        foreach (string k in new List<string>(next.Keys))
            if ((Prefix + k).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) next.Remove(k);
        Publish(next);
    }

    private static void Publish(Dictionary<string, string> next)
    {
        _s.Prefixes = PrefixTable<string>.Build(next);
        _s.Map = next;
    }

    /// <summary>The image a press on the element with id <paramref name="id"/> picks up: its own key, else the longest declared prefix. Allocation-free.</summary>
    public static string? Find(ReadOnlySpan<char> id)
        => id.Length == 0 ? null
            : _s.Map.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(id, out string? image) ? image : _s.Prefixes.Find(id);

    /// <summary>Forgets everything. For the self-check only.</summary>
    internal static void Reset() => _s = new();
}
