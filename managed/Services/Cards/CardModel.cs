using System;

namespace CX.Services.Cards;

/// <summary>The kinds of block a card is stacked from. An unknown <c>type</c> in a declaration parses as <see cref="Text"/>.</summary>
internal enum BlockKind { Header, Subtitle, Status, Headline, Text, Flavour, Divider, Icons, Section, Footer, Loading }

/// <summary>Which corner of a card an image is anchored to.</summary>
internal enum Anchor { TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>
/// One styled piece of a text line. <see cref="Icon"/> makes it an inline icon, <see cref="Break"/> a line break. A
/// <see cref="Rgba"/> of 0 and a <see cref="Size"/> of 0 inherit the block's colour and size.
/// </summary>
internal readonly struct Run
{
    public readonly string Text, Icon;
    public readonly uint Rgba;
    public readonly float Size;
    public readonly bool Italic, Break;

    public Run(string text, string icon, uint rgba, float size, bool italic, bool brk)
    {
        Text = text; Icon = icon; Rgba = rgba; Size = size; Italic = italic; Break = brk;
    }
}

/// <summary>One block of a card: the fields each <see cref="BlockKind"/> reads are listed in <c>docs/ui-policy-contract.md</c>.</summary>
internal sealed class Block
{
    public BlockKind Kind;
    public Run[] Runs = [], Right = [];
    public string Icon = "";
    public bool Frame;
    public uint Color, Tint;
    public float Size;
    public string[] Icons = [];
    public float IconPx;
    public Block[] Kids = [];
}

/// <summary>An image placed against a card corner; an offset that pushes it out of the card makes it overflow.</summary>
internal struct CardImage
{
    public string Src;
    public float W, H, Dx, Dy;
    public Anchor At;
}

/// <summary>One declared card.</summary>
internal sealed class CardDecl
{
    public float Width = 300f;
    public uint Border, BgFrom, BgTo;
    public bool Horizontal;
    public string Tab = "";
    public uint TabRgba;
    public string Item = "";
    public Block[] Blocks = [];
    public CardImage[] Images = [];
}

/// <summary>
/// The cards one key holds, in the order they draw. <see cref="Views"/> is the render thread's cache of their layout: it
/// is rebuilt when the card is shown, and when the font atlas or the UI scale changes under it.
/// </summary>
internal sealed class CardSet
{
    public readonly string Key;
    public readonly CardDecl[] Cards;
    public CardView[]? Views;
    public int ViewEpoch;
    public float ViewScale;

    public CardSet(string key, CardDecl[] cards) { Key = key; Cards = cards; }
}

/// <summary>The limits on a declaration. A declaration over any of them is refused whole.</summary>
internal static class CardLimits
{
    public const int MaxKeyBytes = 96, MaxJsonBytes = 12_000, MaxCards = 4, MaxBlocks = 48, MaxDepth = 2, MaxRuns = 48;
    public const int MaxText = 400, MaxIcons = 16, MaxImages = 4, MaxKeys = 256, MaxIconName = 120;
    public const float MinWidth = 160f, MaxWidth = 480f, MinIconPx = 12f, MaxIconPx = 64f, MaxImagePx = 256f;
    /// <summary>The only text sizes: a multiple of the player's text size. Each one is a baked face, so the set is fixed.</summary>
    public static readonly float[] SizeLadder = [0.7f, 0.85f, 1f, 1.15f, 1.3f, 1.6f, 2f];

    /// <summary>The ladder step nearest <paramref name="v"/>.</summary>
    public static float Snap(float v)
    {
        float best = SizeLadder[0];
        foreach (float s in SizeLadder) if (Math.Abs(s - v) < Math.Abs(best - v)) best = s;
        return best;
    }
}
