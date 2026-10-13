using System;
using System.Collections.Generic;
using CX.Ui;

namespace CX.Services.Panels;

/// <summary>
/// The text faces a panel draws with. A size other than the player's own is a multiple from <see cref="PanelLimits.SizeLadder"/>;
/// its face is baked when a declaration that uses it arrives (baking rebuilds the glyph sheet, so never while drawing). A face
/// that is not baked yet draws as the player's face stretched, which is blurrier and never wrong.
/// </summary>
internal static class PanelFaces
{
    private sealed class FacesState
    {
        public readonly HashSet<float> Wanted = [];
    }

    private static readonly FacesState _f = new();

    /// <summary>Asks for the face of <paramref name="mult"/> times the player's text size. Cold.</summary>
    public static void Want(float mult)
    {
        lock (_f.Wanted) _f.Wanted.Add(mult);
        TextRig.Face(PxOf(mult));
    }

    /// <summary>Asks again for every wanted face after the player's text size or the UI scale changed. Cold.</summary>
    public static void WantAll()
    {
        float[] all;
        lock (_f.Wanted) all = [.. _f.Wanted];
        foreach (float m in all) TextRig.Face(PxOf(m));
    }

    private static float PxOf(float mult) => MathF.Round(TextRig.FacePx(0) * mult);

    /// <summary>The atlas for <paramref name="mult"/> and the stretch it still needs (1 when that size is baked). Allocation-free.</summary>
    public static (GlyphAtlas A, float K) Resolve(float mult)
    {
        GlyphAtlas def = TextRig.Atlas!;
        if (mult == 1f) return (def, 1f);
        GlyphAtlas? baked = TextRig.BakedFace(PxOf(mult));
        return baked != null ? (baked, 1f) : (def, mult);
    }
}
