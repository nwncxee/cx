using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using CX.Framework;
using CX.Ui;

namespace CX.Services.Cards;

/// <summary>
/// The cards the server has declared this session, by key (<c>card.&lt;key&gt;</c> policy lines). A declaration parses when its
/// line arrives, and the index is replaced whole, so the render thread reads one reference and never a half-built one. A
/// refused declaration leaves its key with no card. Keys compare without regard to case, as every policy key does.
/// </summary>
internal static class CardRegistry
{
    /// <summary>The <see cref="ServerPolicy"/> prefix every card key carries.</summary>
    public const string Prefix = "card.";

    private sealed class RegistryState
    {
        public volatile Dictionary<string, CardSet> Map = new(StringComparer.OrdinalIgnoreCase);
        public int Gen;
        public ExtenderLog? Log;
    }

    private static RegistryState _s = new();

    /// <summary>Bumps whenever a key is set, replaced or released; a reader that cached a lookup compares it.</summary>
    public static int Gen => Volatile.Read(ref _s.Gen);
    public static int Count => _s.Map.Count;

    /// <summary>Hooks the registry into <see cref="ServerPolicy"/> and keeps the log for refusals.</summary>
    public static void Init(ExtenderLog log)
    {
        _s.Log = log;
        ServerPolicy.Handle(Prefix, Set, Release, () => $"cards: {Count} key(s)");
    }

    /// <summary>One <c>card.&lt;key&gt;</c> line: <paramref name="key"/> is what follows the prefix. An empty value releases it. False when refused.</summary>
    public static bool Set(string key, string value)
    {
        key = key.Trim();
        if (key.Length == 0 || Encoding.UTF8.GetByteCount(key) > CardLimits.MaxKeyBytes)
        {
            _s.Log?.Info($"Cards: refusing a key of {key.Length} characters (1-{CardLimits.MaxKeyBytes} bytes allowed)");
            return false;
        }
        if (value.Length == 0) { Swap(key, null); return true; }
        if (!CardParser.TryParse(value, out CardDecl[] cards, out string why))
        {
            Swap(key, null);
            _s.Log?.Info($"Cards: refused '{key}': {why}; it keeps no card");
            return false;
        }
        if (_s.Map.Count >= CardLimits.MaxKeys && !_s.Map.ContainsKey(key))
        {
            _s.Log?.Info($"Cards: refused '{key}': {CardLimits.MaxKeys} keys held already");
            return false;
        }
        foreach (float m in SizesUsed(cards)) CardFaces.Want(m);
        Swap(key, new CardSet(key, cards));
        return true;
    }

    /// <summary>Drops every key whose full policy key (<c>card.&lt;key&gt;</c>) starts with <paramref name="prefix"/>; an empty prefix drops all.</summary>
    public static void Release(string prefix)
    {
        var next = new Dictionary<string, CardSet>(_s.Map, StringComparer.OrdinalIgnoreCase);
        foreach (string k in new List<string>(next.Keys))
            if ((Prefix + k).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) next.Remove(k);
        _s.Map = next;
        Interlocked.Increment(ref _s.Gen);
    }

    private static void Swap(string key, CardSet? set)
    {
        var next = new Dictionary<string, CardSet>(_s.Map, StringComparer.OrdinalIgnoreCase);
        if (set == null) next.Remove(key); else next[key] = set;
        _s.Map = next;
        Interlocked.Increment(ref _s.Gen);
    }

    /// <summary>The cards held under <paramref name="key"/>, or null. Allocation-free.</summary>
    public static CardSet? Find(ReadOnlySpan<char> key)
        => _s.Map.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(key, out CardSet? set) ? set : null;

    /// <summary>Forgets everything and the log. For the self-check only.</summary>
    internal static void Reset() => _s = new();

    /// <summary>Every distinct non-1 text size the cards use, so their faces are baked when the declaration arrives and never while one is drawn.</summary>
    internal static IEnumerable<float> SizesUsed(CardDecl[] cards)
    {
        var seen = new HashSet<float>();
        void Walk(Block[] blocks)
        {
            foreach (Block b in blocks)
            {
                seen.Add(b.Size);
                foreach (Run r in b.Runs) if (r.Size > 0f) seen.Add(r.Size);
                foreach (Run r in b.Right) if (r.Size > 0f) seen.Add(r.Size);
                Walk(b.Kids);
            }
        }
        foreach (CardDecl c in cards) Walk(c.Blocks);
        seen.Remove(1f);
        return seen;
    }
}
