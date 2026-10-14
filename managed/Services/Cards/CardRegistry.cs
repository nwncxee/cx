using System;
using System.Collections.Generic;
using CX.Framework;
using CX.Ui;
using CX.Services.Panels;

namespace CX.Services.Cards;

/// <summary>
/// The cards one key holds, in the order they draw. <see cref="Views"/> is the render thread's cache of their layout: it
/// is rebuilt when the card is shown, and when the font atlas, the UI scale or the player's look changes under it.
/// </summary>
internal sealed class CardSet
{
    public readonly string Key;
    public readonly PanelDecl[] Cards;
    /// <summary>The line as sent, kept so a new <c>cardstyle</c> can lay it out again.</summary>
    public readonly string Raw;
    public PanelView[]? Views;
    public int ViewEpoch;
    public float ViewScale;
    /// <summary>The <see cref="Theme.Epoch"/> the cached layout baked its role colours at.</summary>
    public int ViewTheme;

    public CardSet(string key, PanelDecl[] cards, string raw = "") { Key = key; Cards = cards; Raw = raw; }
}

/// <summary>The limits on the keys a client holds.</summary>
internal static class CardLimits
{
    public const int MaxKeyBytes = PolicyIndex<CardSet>.MaxKeyBytes, MaxKeys = PolicyIndex<CardSet>.MaxKeys;
}

/// <summary>
/// The cards the server has declared this session, by key (<c>card.&lt;key&gt;</c> policy lines). A declaration parses when its
/// line arrives, and the index is replaced whole, so the render thread reads one reference and never a half-built one. A
/// refused declaration leaves its key with no card. Keys compare without regard to case, as every policy key does.
/// </summary>
internal static class CardRegistry
{
    /// <summary>The <see cref="ServerPolicy"/> prefix every card key carries.</summary>
    public const string Prefix = "card.";
    /// <summary>The policy key whose value is the style every card starts from.</summary>
    public const string StyleKey = "cardstyle";

    private sealed class RegistryState
    {
        public volatile PanelStyle Style = new();
        public string StyleRaw = "";
        public ExtenderLog? Log;
    }

    private static RegistryState _s = new();
    private static readonly PolicyIndex<CardSet> Index = new();

    /// <summary>Bumps whenever a key is set, replaced or released; a reader that cached a lookup compares it.</summary>
    public static int Gen => Index.Gen;
    public static int Count => Index.Count;

    /// <summary>Hooks the registry into <see cref="ServerPolicy"/> and keeps the log for refusals.</summary>
    public static void Init(ExtenderLog log)
    {
        _s.Log = log;
        ServerPolicy.Handle(Prefix, Set, Release, () => $"cards: {Count} key(s)");
        ServerPolicy.Handle(StyleKey, SetStyle, ReleaseStyle, () => $"card style: {(_s.StyleRaw.Length == 0 ? "default" : "set")}");
    }

    /// <summary>The <c>cardstyle</c> line: the style every card starts from. An empty value restores the defaults. Cards already held are laid out again against it. False when refused (the defaults then apply).</summary>
    public static bool SetStyle(string rest, string value)
    {
        if (rest.Length != 0) { PolicyRefusals.Add(RefusalKind.Style, StyleKey + rest, value, "the style line takes no key after it"); return false; }
        PanelStyle style = new();
        bool ok = value.Length == 0 || PanelParser.TryParseStyle(value, out style, out string why) || Refused(value, why);
        _s.Style = ok ? style : new PanelStyle();
        _s.StyleRaw = ok ? value : "";
        Restyle();
        return ok;
    }

    private static bool Refused(string value, string why)
    {
        PolicyRefusals.Add(RefusalKind.Style, StyleKey, value, why);
        _s.Log?.Info($"Cards: refused the card style: {why}; the defaults apply");
        return false;
    }

    private static void ReleaseStyle(string prefix)
    {
        if (StyleKey.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) SetStyle("", "");
    }

    /// <summary>Parses every held card line again against the current style.</summary>
    private static void Restyle()
    {
        Index.Rebuild(old =>
        {
            if (!PanelParser.TryParse(old.Raw, out PanelDecl[] cards, out _, _s.Style)) return null;
            foreach (float m in SizesUsed(cards)) PanelFonts.Want(m);
            return new CardSet(old.Key, cards, old.Raw);
        });
    }

    /// <summary>One <c>card.&lt;key&gt;</c> line: <paramref name="key"/> is what follows the prefix. An empty value releases it. False when refused.</summary>
    public static bool Set(string key, string value)
    {
        key = key.Trim();
        if (!PolicyIndex<CardSet>.KeyOk(key))
        {
            PolicyRefusals.Add(RefusalKind.Card, Prefix + key, value, $"the key must be 1-{CardLimits.MaxKeyBytes} bytes");
            _s.Log?.Info($"Cards: refusing a key of {key.Length} characters (1-{CardLimits.MaxKeyBytes} bytes allowed)");
            return false;
        }
        if (value.Length == 0) { Index.Put(key, null); return true; }
        if (!PanelParser.TryParse(value, out PanelDecl[] cards, out string why, _s.Style))
        {
            Index.Put(key, null);
            PolicyRefusals.Add(RefusalKind.Card, Prefix + key, value, why);
            _s.Log?.Info($"Cards: refused '{key}': {why}; it keeps no card");
            return false;
        }
        if (Index.IsFull(key))
        {
            PolicyRefusals.Add(RefusalKind.Card, Prefix + key, value, $"{CardLimits.MaxKeys} card keys are held already");
            _s.Log?.Info($"Cards: refused '{key}': {CardLimits.MaxKeys} keys held already");
            return false;
        }
        foreach (float m in SizesUsed(cards)) PanelFonts.Want(m);
        Index.Put(key, new CardSet(key, cards, value));
        return true;
    }

    /// <summary>Drops every key whose full policy key (<c>card.&lt;key&gt;</c>) starts with <paramref name="prefix"/>; an empty prefix drops all.</summary>
    public static void Release(string prefix)
    {
        Index.Release(Prefix, prefix);
        if (Index.Count == 0) PanelFonts.Forget();
    }

    /// <summary>The cards held under <paramref name="key"/>, or null. Allocation-free.</summary>
    public static CardSet? Find(ReadOnlySpan<char> key)
        => Index.Find(key);

    /// <summary>The cards of the longest declared <c>prefix*</c> key that starts <paramref name="id"/>, or null. Allocation-free.</summary>
    public static CardSet? FindPrefix(ReadOnlySpan<char> id) => Index.FindPrefix(id);

    /// <summary>Every card set held now. Cold: it walks the index.</summary>
    internal static IEnumerable<CardSet> Sets() => Index.Values;

    /// <summary>Forgets everything and the log. For the self-check only.</summary>
    internal static void Reset() { _s = new(); Index.Reset(); }

    /// <summary>Every distinct non-1 text size the cards use, so their faces are baked when the declaration arrives and never while one is drawn.</summary>
    internal static IEnumerable<float> SizesUsed(PanelDecl[] cards)
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
        foreach (PanelDecl c in cards) Walk(c.Blocks);
        seen.Remove(1f);
        return seen;
    }
}
