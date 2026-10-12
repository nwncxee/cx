using System;
using System.Collections.Generic;
using CX.Engine;
using CX.Framework;
using CX.Services.Nui;
using CX.Services.Radial;

namespace CX.Services.Cards;

/// <summary>
/// The throwaway items behind the <c>@look:</c> images the declared cards use: one per distinct look, made when a declaration that
/// names it arrives and ended once no declared card names it. <see cref="Sync"/> runs on the game thread, where the engine may be asked
/// to make an item; the draw only reads.
/// </summary>
internal static class CardLooks
{
    /// <summary>The minted items by look, and the registry generation they were last reconciled with.</summary>
    internal sealed class LooksState
    {
        public readonly Dictionary<string, IntPtr> Minted = new();
        public int Gen = -1;
        public int Notices;
    }

    private static LooksState _s = new();

    /// <summary>The prefix of an image name that describes an item by its appearance.</summary>
    public const string Prefix = "@look:";

    /// <summary>Is <paramref name="src"/> an <c>@look:</c> image name?</summary>
    public static bool IsLook(string src) => src.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The minted item for <paramref name="look"/>, or zero when none was made. Allocation-free.</summary>
    public static IntPtr Item(string look) => _s.Minted.TryGetValue(look, out IntPtr item) ? item : IntPtr.Zero;

    /// <summary>How many looks have an item right now.</summary>
    public static int Count => _s.Minted.Count;

    /// <summary>Brings the minted items in line with the declared cards when the registry has changed.</summary>
    public static void Sync(ExtenderLog? log)
    {
        var s = _s;
        int gen = CardRegistry.Gen;
        if (gen == s.Gen) return;
        s.Gen = gen;
        var wanted = new HashSet<string>();
        foreach (CardSet set in CardRegistry.Sets())
            foreach (CardDecl c in set.Cards) Collect(c, wanted);
        Reconcile(s, wanted, NuiItemIcons.MintLook, NuiItemIcons.Destroy, log);
    }

    /// <summary>Ends the items of looks not in <paramref name="wanted"/> and makes one for each new look, through <paramref name="mint"/>
    /// and <paramref name="destroy"/> so a check can supply its own. A look that made no item is remembered as none, so it is not tried every frame.</summary>
    internal static void Reconcile(LooksState s, HashSet<string> wanted, Func<string, IntPtr> mint, Action<IntPtr> destroy, ExtenderLog? log)
    {
        foreach (string k in new List<string>(s.Minted.Keys))
        {
            if (wanted.Contains(k)) continue;
            if (s.Minted[k] != IntPtr.Zero) destroy(s.Minted[k]);
            s.Minted.Remove(k);
        }
        foreach (string look in wanted)
        {
            if (s.Minted.ContainsKey(look)) continue;
            IntPtr item = mint(look);
            s.Minted[look] = item;
            if (item == IntPtr.Zero && s.Notices++ < 5) log?.Always($"Cards: '{look}' made no item -- that image draws nothing");
        }
    }

    /// <summary>Every <c>@look:</c> image name the card names, in its images, icon rows, inline icons and sections.</summary>
    internal static void Collect(CardDecl card, HashSet<string> into)
    {
        foreach (CardImage i in card.Images) Add(i.Src, into);
        foreach (Block b in card.Blocks) Collect(b, into);
    }

    private static void Collect(Block b, HashSet<string> into)
    {
        Add(b.Icon, into);
        foreach (string i in b.Icons) Add(i, into);
        foreach (Run r in b.Runs) Add(r.Icon, into);
        foreach (Run r in b.Right) Add(r.Icon, into);
        foreach (Block k in b.Kids) Collect(k, into);
    }

    private static void Add(string src, HashSet<string> into)
    {
        if (IsLook(src)) into.Add(src);
    }

    /// <summary>The item's icon into the rectangle, cropped to its base item's cells as the held image's is. Nothing when the look made none.</summary>
    public static unsafe void Draw(string look, float x, float y, float w, float h, float alpha)
    {
        IntPtr item = Item(look);
        if (item == IntPtr.Zero) return;
        NuiItemIcons.Cells(item, out int cw, out int ch);
        IntPtr icon = new CNWCItem(item).GuiIcon;
        if (icon != IntPtr.Zero) RadialIcons.DrawGuiIcon(icon, x, y, w, h, alpha, cw, ch);
    }
}
