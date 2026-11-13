using System;
using CX.Framework;

namespace CX.Services.NuiItems;

/// <summary>
/// The NUI elements a server declared draggable (<c>drag.&lt;id&gt;=1</c> policy lines), by element id. A key ending in
/// <c>*</c> is a prefix. The index is replaced whole, so the input thread reads one reference.
/// </summary>
internal static class ItemDragPolicy
{
    /// <summary>The <see cref="ServerPolicy"/> prefix every drag key carries.</summary>
    public const string Prefix = "drag.";

    private static readonly PolicyIndex<string> Index = new();

    public static int Count => Index.Count;

    /// <summary>Hooks the declarations into <see cref="ServerPolicy"/>.</summary>
    public static void Init() => ServerPolicy.Handle(Prefix, Set, Release, static () => $"drag: {Count} element(s) declared");

    /// <summary>One <c>drag.&lt;key&gt;</c> line; an empty value releases it. False when refused.</summary>
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
            PolicyRefusals.Add(RefusalKind.Held, Prefix + key, value, $"{PolicyIndex<string>.MaxKeys} drag keys are declared already");
            return false;
        }
        Index.Put(key, value.Length == 0 ? null : value);
        return true;
    }

    /// <summary>Drops every key whose full policy key starts with <paramref name="prefix"/>.</summary>
    public static void Release(string prefix) => Index.Release(Prefix, prefix);

    /// <summary>May the element with id <paramref name="id"/> be dragged: its own key, else the longest declared prefix. Allocation-free.</summary>
    public static bool Draggable(ReadOnlySpan<char> id)
        => id.Length != 0 && (Index.Find(id) ?? Index.FindPrefix(id)) != null;

    /// <summary>Forgets everything. For the self-check only.</summary>
    internal static void Reset() => Index.Reset();
}
