using System;

namespace CX.Services.Panels;

/// <summary>The kinds of block a panel is stacked from. An unknown <c>type</c> in a declaration parses as <see cref="Text"/>.</summary>
internal enum BlockKind { Status, Text, Divider, Icons, Section, Footer }

/// <summary>Which corner of a panel an image is anchored to.</summary>
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

/// <summary>One block of a panel: the fields each <see cref="BlockKind"/> reads are listed in <c>docs/ui-policy-contract.md</c>.</summary>
internal sealed class Block
{
    public BlockKind Kind;
    public Run[] Runs = [], Right = [];
    public string Icon = "";
    public bool Frame, Pulse;
    public uint Color, Tint;
    public float Size;
    public string[] Icons = [];
    public float IconPx;
    public Block[] Kids = [];
}

/// <summary>An image placed against a panel corner; an offset that pushes it out of the panel makes it overflow.</summary>
internal struct PanelImage
{
    public string Src;
    public float W, H, Dx, Dy;
    public Anchor At;
}

/// <summary>One declared panel: a card when it shows beside a NUI element.</summary>
internal sealed class PanelDecl
{
    public float Width = 300f;
    public uint Border, BgFrom, BgTo;
    public bool Horizontal;
    public string Tab = "";
    public uint TabRgba;
    public string Item = "";
    public Block[] Blocks = [];
    public PanelImage[] Images = [];
}

/// <summary>The limits on a declaration. A declaration over any of them is refused whole.</summary>
internal static class PanelLimits
{
    public const int MaxJsonBytes = 12_000, MaxCards = 4, MaxBlocks = 48, MaxDepth = 2, MaxRuns = 48;
    public const int MaxText = 400, MaxIcons = 16, MaxImages = 4, MaxIconName = 120;
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
