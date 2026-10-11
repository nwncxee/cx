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
