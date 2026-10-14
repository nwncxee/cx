using System;
using CX.Ui;

namespace CX.Services.Panels;

/// <summary>Which way a panel's background gradient runs.</summary>
internal enum BgDir { Vertical, Horizontal, Diagonal }

/// <summary>Which edge of the element a card group sits against.</summary>
internal enum Side { Right, Left, Above, Below }

/// <summary>How the card group lines up along the element's edge.</summary>
internal enum Align { Start, Center, End }

/// <summary>Which chrome a panel wears: <see cref="Auto"/> follows the player's <c>ui.look</c>, the other two decide.</summary>
internal enum CardLook { Auto, Native, Flat }

/// <summary>
/// Every value that decides how a panel looks or where it sits. The field initialisers are the defaults and the only place
/// they are written; the server's <c>cardstyle</c> line replaces them for every card, and a card's own fields replace them
/// for that card. Lengths are design px; a colour is a <see cref="Paint"/>, so a default of it follows the player's look.
/// </summary>
internal sealed class PanelStyle
{
    public float Width = 300f, MinWidth = 160f, MaxWidth = 480f;
    public float[] Sizes = [0.7f, 0.85f, 1f, 1.15f, 1.3f, 1.6f, 2f];

    public float Pad = 10f, Gap = 4f, TextGap = 2f, StatusGap = 6f, IconGap = 4f, FrameInset = 2f;
    public float DividerMargin = 3f, DividerThick = 1f, SectionPad = 6f, FooterPad = 5f, FooterSplit = 0.62f;
    public float StatusPx = 22f, IconsPx = 28f, ImageSize = 64f, Leading = 1f, Slant = 0.2f;
    public float TabX = 8f, TabPad = 6f, TabExtra = 4f, TabDrop = 2f;

    /// <summary>The colours that follow the player's look, by role; a declared colour replaces one outright.</summary>
    public Paint Text = Paint.Of(Role.Tx), BgFrom = Paint.Of(Role.PanelBg, 0xF2), Border = Paint.Of(Role.Border);
    /// <summary>The frame of an icon and the tab behind a card's name, by role; the translucent black and white
    /// washes sit over whatever the look draws, so they stay as written.</summary>
    public Paint FrameLine = Paint.Of(Role.Grip, 0xC0), TabBg = Paint.Of(Role.MasterBg, 0xB0);
    // not-an-offset: RRGGBBAA colours
    public Paint Divider = Paint.Literal(0xFFFFFF30), FooterBand = Paint.Literal(0x00000066), FrameFill = Paint.Literal(0x00000066);
    public Paint TabText, Shadow, Border2, Glow, Corners;
    /// <summary>The gradient's far colour; null is the near colour, a flat fill.</summary>
    public Paint? BgTo;
    /// <summary>The colour a <c>dim</c> block takes when it names none; only its RGB is used.</summary>
    public Paint DimColor = Paint.Literal(0xFFFFFFFF);  // not-an-offset: an RRGGBBAA colour
    public byte DimAlpha = 0x99;  // not-an-offset: an alpha byte
    public BgDir Dir = BgDir.Vertical;
    /// <summary>Which chrome the panel wears; the owner resolves <see cref="CardLook.Auto"/> and stands native down when
    /// the art is missing.</summary>
    public CardLook Look = CardLook.Auto;
    public float BorderW = 1f, FrameW = 1f, Border2W = 1f, Border2Inset = 3f, ShadowDx = 1f, ShadowDy = 1f, GlowSpread = 8f, GlowAlpha = 0.5f, CornerLen = 12f, CornerW = 2f;
    /// <summary>Which edges the frame draws: any of <c>t r b l</c>.</summary>
    public string Sides = "trbl";

    public float PulseMin = 0.35f, PulsePeriodMs = 1200f;

    public Side Side = Side.Right;
    public Align Align = Align.Start;
    public bool Flip = true;
    public float Offset = 8f, CardGap = 8f, Dx, Dy;
    /// <summary>Seconds a card waits before it shows, and its fades; below 0 follows the player's own tooltip timing.</summary>
    public float Delay = -1f, FadeIn = -1f, FadeOut = -1f;

    /// <summary>True when this panel asks for the game's GUI art: its own choice, or the player's under
    /// <see cref="CardLook.Auto"/>. The owner still stands it down when the skin is not whole.</summary>
    public bool WantsNative(bool playerNative) => Look switch { CardLook.Native => true, CardLook.Flat => false, _ => playerNative };

    /// <summary>The nearest step of <see cref="Sizes"/> to <paramref name="v"/>.</summary>
    public float Snap(float v)
    {
        float best = Sizes[0];
        foreach (float s in Sizes) if (Math.Abs(s - v) < Math.Abs(best - v)) best = s;
        return best;
    }

    /// <summary>A copy a card's own fields can then change.</summary>
    public PanelStyle Clone() => (PanelStyle)MemberwiseClone();
}
