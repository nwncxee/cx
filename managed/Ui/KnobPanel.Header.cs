using static CX.Ui.PanelChrome;

namespace CX.Ui;

/// <summary>The header's controls: the close ×, the collapse button left of it -- the panel folds to its title bar
/// (still movable), so what a setting changed can be seen behind it; a modal panel has none -- and the header button.</summary>
public sealed partial class KnobPanel
{
    public const string CollapseGlyph = "▾", ExpandGlyph = "▸";

    /// <summary>Folded to the title bar; a fresh open unfolds.</summary>
    public bool Collapsed { get; private set; }

    /// <summary>The height drawn and hit: the title bar alone while collapsed.</summary>
    public float ShownH => Collapsed ? HeaderH : H;

    /// <summary>Header width the collapse button takes, left of the ×.</summary>
    private float CollapseSpan => Modal ? 0f : CloseW;

    /// <summary>The × box as drawn, so it does not reach into the collapse box left of it.</summary>
    private bool OverClose(float mx, float my)
    {
        float x1 = X + W - Pad + 0.2f * Em;
        return mx >= x1 - CloseW && mx < x1 && my >= Y && my < Y + HeaderH;
    }

    /// <summary>The header button's box (right of the title, left of the ×).</summary>
    public void HeaderButtonRect(out float x0, out float x1, out float y0, out float y1)
    {
        x1 = X + W - Pad - CloseW - CollapseSpan - 0.6f * Em; x0 = x1 - HeaderBtnW;
        float mid = Y + HeaderH * 0.5f;
        y0 = mid - HeaderBtnH * 0.5f; y1 = mid + HeaderBtnH * 0.5f;
    }

    /// <summary>A labelled dropdown in the header, left of the header button; empty = none. A pick runs
    /// <see cref="HeaderChoicePicked"/> with its index after setting <see cref="HeaderChoice"/>.</summary>
    public string[] HeaderChoices = System.Array.Empty<string>();
    public string HeaderChoiceLabel = "";
    public int HeaderChoice;
    public System.Action<int>? HeaderChoicePicked;
    /// <summary>The open list's tag while it is the header dropdown's, so a pick is told from a row's.</summary>
    private static readonly object HeaderChoiceTag = new();

    /// <summary>The header dropdown's box: as wide as its widest choice, left of the header button.</summary>
    public void HeaderChoiceRect(out float x0, out float x1, out float y0, out float y1)
    {
        HeaderButtonRect(out float bx0, out _, out y0, out y1);
        x1 = (HeaderAction.Length > 0 ? bx0 : X + W - Pad - CloseW - CollapseSpan) - 0.4f * Em;
        float widest = 0f;
        foreach (string c in HeaderChoices) widest = System.Math.Max(widest, Body != null ? UiText.Measure(Body, c) : 6f * Em);
        x0 = x1 - widest - 1.8f * Em;
    }

    private bool OverHeaderChoice(float mx, float my)
    {
        if (HeaderChoices.Length == 0) return false;
        HeaderChoiceRect(out float x0, out float x1, out float y0, out float y1);
        return In(mx, my, x0, x1, y0, y1);
    }

    /// <summary>Open the header dropdown under its box.</summary>
    private void OpenHeaderChoice()
    {
        HeaderChoiceRect(out float x0, out float x1, out float y0, out float y1);
        Drop.OpenAt(HeaderChoices, HeaderChoice, HeaderChoiceTag, x0, y0, y1, x1 - x0, CtlH, VpH);
    }

    /// <summary>Draw the header button; returns the x the title must clip at.</summary>
    private float DrawHeaderButton(VertexBatch b, GlyphAtlas body, Skin? skin, float hbase, float titleClip)
    {
        if (HeaderAction.Length == 0) return titleClip;
        HeaderButtonRect(out float hx0, out float hx1, out float hy0, out float hy1);
        if (!Art(skin, NativeChrome.Button, hx0, hy0, hx1, hy1))
        {
            Fill(b, hx0, hy0, hx1, hy1, PanelChrome.NavOn);
            Outline(b, hx0, hy0, hx1, hy1, PanelChrome.Frame2);
        }
        UiText.Draw(b, body, HeaderAction, hx0 + 0.9f * Em, hbase, PanelChrome.Tx, hx1 - 0.4f * Em);
        return hx0 - 0.6f * Em;
    }

    /// <summary>Draw the header dropdown and its label; returns the x the title must clip at.</summary>
    private float DrawHeaderChoice(VertexBatch b, GlyphAtlas body, float hbase, float titleClip)
    {
        if (HeaderChoices.Length == 0) return titleClip;
        HeaderChoiceRect(out float x0, out float x1, out float y0, out float y1);
        string cur = (uint)HeaderChoice < (uint)HeaderChoices.Length ? HeaderChoices[HeaderChoice] : "";
        Dropdown.DrawBox(b, body, x0, y0, x1, y1, hbase, cur, PanelChrome.C(PanelChrome.Input), PanelChrome.Tx);
        Outline(b, x0, y0, x1, y1, Drop.Open && ReferenceEquals(Drop.Tag, HeaderChoiceTag) ? PanelChrome.Accent2 : PanelChrome.Frame2);
        float lx = x0 - 0.4f * Em - UiText.Measure(body, HeaderChoiceLabel);
        UiText.Draw(b, body, HeaderChoiceLabel, lx, hbase, PanelChrome.Tx2, x0);
        return lx - 0.6f * Em;
    }

    private bool OverHeaderButton(float mx, float my)
    {
        if (HeaderAction.Length == 0) return false;
        HeaderButtonRect(out float x0, out float x1, out float y0, out float y1);
        return In(mx, my, x0, x1, y0, y1);
    }

    private bool OverCollapse(float mx, float my)
    {
        if (Modal) return false;
        float x1 = X + W - Pad + 0.2f * Em - CloseW;
        return mx >= x1 - CloseW && mx < x1 && my >= Y && my < Y + HeaderH;
    }

    /// <summary>Fold or unfold; unfolding pulls the panel back on screen if it was parked low.</summary>
    public void ToggleCollapse()
    {
        Collapsed = !Collapsed;
        Drop.Close(); Tip.Cut(); _grab = null; _hoverRow = null;
        if (!Collapsed) { Y = Window.ClampAxis(Y, H, VpH); Place(); }
    }

    /// <summary>The collapse glyph left of the ×.</summary>
    private void DrawCollapse(VertexBatch b, GlyphAtlas body, float closeX0, float hbase)
    {
        if (Modal) return;
        string g = Collapsed ? ExpandGlyph : CollapseGlyph;
        UiText.Draw(b, body, g, closeX0 - CloseW + (CloseW - UiText.Measure(body, g)) * 0.5f, hbase, PanelChrome.Grip, closeX0);
    }
}
