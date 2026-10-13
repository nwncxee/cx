using System;
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

    private static readonly PolicyIndex<string> Index = new();

    public static int Count => Index.Count;

    /// <summary>Hooks the picks into <see cref="ServerPolicy"/>.</summary>
    public static void Init() => ServerPolicy.Handle(Prefix, Set, Release, static () => $"held picks: {Count} key(s)");

    /// <summary>One <c>held.pick.&lt;key&gt;</c> line; an empty value releases it. False when refused.</summary>
    public static bool Set(string key, string value)
    {
        key = key.Trim();
        if (!PolicyIndex<string>.KeyOk(key))
        {
            PolicyRefusals.Add(RefusalKind.Held, Prefix + key, value, $"the key must be 1-{PolicyIndex<string>.MaxKeyBytes} bytes");
            return false;
        }
        if (value.Length != 0 && Index.IsFull(key))
        {
            PolicyRefusals.Add(RefusalKind.Held, Prefix + key, value, $"{PolicyIndex<string>.MaxKeys} pick keys are held already");
            return false;
        }
        Index.Put(key, value.Length == 0 ? null : value);
        return true;
    }

    /// <summary>Drops every key whose full policy key starts with <paramref name="prefix"/>.</summary>
    public static void Release(string prefix) => Index.Release(Prefix, prefix);

    /// <summary>The image a press on the element with id <paramref name="id"/> picks up: its own key, else the longest declared prefix. Allocation-free.</summary>
    public static string? Find(ReadOnlySpan<char> id)
        => id.Length == 0 ? null : Index.Find(id) ?? Index.FindPrefix(id);

    /// <summary>Forgets everything. For the self-check only.</summary>
    internal static void Reset() => Index.Reset();
}
