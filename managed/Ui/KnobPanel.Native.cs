using System;
using CX.Framework;

using static CX.Ui.PanelChrome;

namespace CX.Ui;

/// <summary>The panel's half of the native look: its own <see cref="Skin"/> over the stock NUI art, armed per
/// policy, and the one-part helper every control draws through. A refused skin leaves every control flat.</summary>
public sealed partial class KnobPanel
{
    /// <summary>Where a refused skin is reported; an owner that has a log sets it.</summary>
    public ExtenderLog? Log;

    /// <summary>This window's art. One per panel: <see cref="Skin.Begin"/> clears the batches, so the body pass
    /// and the overlay pass flush between their begins rather than sharing one.</summary>
    private readonly Skin _chrome = NativeChrome.NewSkin();

    /// <summary>The art of everything inside the scrolling row column, which flushes scissored to it -- a skin of
    /// its own because a clip is per part and the window's own chrome must not be cut by the column.</summary>
    private readonly Skin _rowArt = NativeChrome.NewSkin();

    /// <summary>The armed skin, or null while the flat look is chosen, nothing has been presented yet (the
    /// resource system is the game's) or a part is missing.</summary>
    private Skin? Art() => Theme.IsNative && TextRig.ViewportW > 0 && _chrome.Arm(Log, "") ? _chrome : null;

    /// <summary>The row column's art, armed only behind <see cref="Art"/> so one refusal is one notice.</summary>
    private Skin? RowArt(Skin? chrome) => chrome != null && _rowArt.Arm(Log, "") ? _rowArt : null;

    /// <summary>Every row part scissored to the row column, so a half-scrolled control's art ends at the column
    /// exactly as its fill does.</summary>
    private void FlushRowArt(Skin rows)
    {
        int cx = (int)List.X, cy = (int)List.Y, cw = (int)MathF.Ceiling(List.W), ch = (int)MathF.Ceiling(List.H);
        for (int p = 0; p < NativeChrome.Count; p++) rows.FlushClipped(p, cx, cy, cw, ch);
    }

    /// <summary>The look the columns lay their gutters out for: the art as of the last arm, so the scrollbar the
    /// owner draws and the content width it reserves agree.</summary>
    private bool NativeArt => Theme.IsNative && _chrome.Armed;

    /// <summary>Part <paramref name="part"/> over the rect at <paramref name="alpha"/>; false when there is no
    /// skin, so the caller draws its flat look instead.</summary>
    private static bool Art(Skin? skin, int part, float x0, float y0, float x1, float y1, float alpha = 1f)
    {
        if (skin == null) return false;
        skin.Draw(part, x0, y0, x1, y1, NativeChrome.White(alpha));
        return true;
    }

    /// <summary>A nav column's ground and its right-hand divider. Under the art the ground is the frame's own black
    /// interior: a fill here would land over the column's scrollbar art, which flushes first. The divider sits where
    /// that bar is, so it is left out while one shows.</summary>
    private static void ColumnBg(VertexBatch b, Skin? skin, ScrollColumn col, uint rgb)
    {
        if (skin == null) Fill(b, col.X, col.Y, col.X + col.W, col.Y + col.H, rgb);
        if (skin == null || !col.Overflows) Fill(b, col.X + col.W - 1f, col.Y, col.X + col.W, col.Y + col.H, ColBorder);
    }

    /// <summary>Hand the three columns the look their gutters are drawn and hit-tested at. Called from the layout
    /// and from every draw, so geometry never disagrees with the art within a frame.</summary>
    private void SyncColumnLook()
    {
        bool n = NativeArt;
        Cats.Native = n; SubCol.Native = n; List.Native = n;
    }
}
