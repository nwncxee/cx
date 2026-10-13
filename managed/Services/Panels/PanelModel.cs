using System;

namespace CX.Services.Panels;

/// <summary>The kinds of block a panel is stacked from. An unknown <c>type</c> in a declaration parses as <see cref="Text"/>.</summary>
internal enum BlockKind { Status, Text, Divider, Icons, Section, Footer }

/// <summary>Which corner of a panel an image is anchored to.</summary>
internal enum Anchor { TopLeft, TopRight, BottomLeft, BottomRight, Top, Bottom, Left, Right, Center }

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
    public uint Color, Tint, FrameFill, FrameLine;
    public float Size;
    /// <summary>Text alignment: 0 left, 1 right, 2 centre.</summary>
    public int Align;
    /// <summary>Per-block overrides of the card's spacing; null takes the card's.</summary>
    public float? After, Pad, Margin, Thick, Spacing, Inset;
    public string[] Icons = [];
    public float IconPx;
    public Block[] Kids = [];
}

/// <summary>An image placed against a panel corner; an offset that pushes it out of the panel makes it overflow.</summary>
internal struct PanelImage
{
    public string Src;
    public float W, H, Dx, Dy;
    public float Alpha;
    public Anchor At;
}

/// <summary>One declared panel: its content, and the <see cref="PanelStyle"/> it was resolved against.</summary>
internal sealed class PanelDecl
{
    public PanelStyle St = new();
    public string Tab = "";
    public string Item = "";
    public Block[] Blocks = [];
    public PanelImage[] Images = [];
}

/// <summary>The limits on a declaration. A declaration over any of them is refused whole.</summary>
internal static class PanelLimits
{
    public const int MaxJsonBytes = 12_000, MaxPanels = 4, MaxBlocks = 48, MaxDepth = 2, MaxRuns = 48;
    public const int MaxText = 400, MaxIcons = 16, MaxImages = 4, MaxIconName = 120;
    /// <summary>Outer bounds on lengths a style sets; the style's own ranges sit inside them.</summary>
    public const float AbsMinWidth = 32f, AbsMaxWidth = 2000f, MinIconPx = 4f, MaxIconPx = 256f, MaxImagePx = 1024f, MaxLen = 256f;
    public const int MaxSizes = 12;
    public const float MinSize = 0.25f, MaxSize = 6f;
}
