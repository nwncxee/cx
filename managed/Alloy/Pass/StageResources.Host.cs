using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary><b>The host-side half of <see cref="StageResources"/>:</b> what a stage's declared overlay and swap
/// images have to be for the pass order to hold, and the extent and budget arithmetic the backend sizes them by.</summary>
internal static unsafe partial class StageResources
{

    /// <summary>A <c>cx_overlay</c> image has to be one a FRAME pass writes as an image: the resolve adds it to every
    /// frame, and one nothing rewrites is the same picture whichever way the camera turns.</summary>
    private static bool OverlayWritten(Stage s, out string why)
    {
        why = "";
        if (s.OverlayImage < 0) return true;
        foreach (Pass p in s.Passes)
            if (!p.Once)
                foreach (ImageBind b in p.Binds)
                    if (!b.Prev && b.Image == s.OverlayImage) return true;
        why = $"the overlay image `{s.OverlayRef}` is not written as an image by any frame pass";
        return false;
    }

    /// <summary>Which images swap halves each frame, once every pass's image units are known: a <c>history</c> image
    /// a FRAME pass writes. A <c>history</c> image nothing rewrites each frame has no history to keep, and is
    /// refused with <paramref name="why"/>.</summary>
    private static bool MarkSwaps(Stage s, out string why)
    {
        why = "";
        foreach (Pass p in s.Passes)
            if (!p.Once)
                foreach (ImageBind b in p.Binds)
                    if (!b.Prev && s.Images[b.Image].History) s.Images[b.Image].Swap = true;
        foreach (Img im in s.Images)
            if (im.History && !im.Swap)
            {
                why = $"image `{im.Name}` is declared `history`, but no frame pass writes it as an image "
                    + "-- there is no history to keep";
                return false;
            }
        return true;
    }

    /// <summary>A declared image's extent in this view: its own for <c>2d</c>/<c>3d</c>, the main view's
    /// render size over the divisor (rounded up) for <c>view</c>.</summary>
    private static void Extent(Img im, int vw, int vh, out int w, out int h, out int d)
    {
        if (im.Shape == Shape.View)
        { w = (vw + im.Div - 1) / im.Div; h = (vh + im.Div - 1) / im.Div; d = 1; }
        else { w = im.W; h = im.H; d = im.D; }
        if (w < 1) w = 1;
        if (h < 1) h = 1;
        if (d < 1) d = 1;
    }

    /// <summary>One stage's images at this view, mips and history halves included. A view extent of 0 means "no main
    /// view measured yet", and the view-sized images are left out.</summary>
    private static long StageBytes(Stage s, int vw, int vh)
    {
        long bytes = 0;
        foreach (Img im in s.Images)
        {
            if (im.Shape == Shape.View && (vw <= 0 || vh <= 0)) continue;
            Extent(im, vw, vh, out int w, out int h, out int d);
            bytes += Bytes(im, w, h, d) * (im.History ? 2 : 1);
        }
        return bytes;
    }

    /// <summary>What <paramref name="s"/>'s images come to at this view, and whether every live stage's together stay
    /// inside <see cref="MaxImageBytes"/>. Past it, <paramref name="s"/> is the one refused.</summary>
    private static bool Budget(Stage s, int vw, int vh, out long bytes, out string why)
    {
        var g = _gl;
        why = "";
        bytes = StageBytes(s, vw, vh);
        long total = bytes;
        for (int i = 0; i < g.Count; i++)
            if (g.Stages[i] is { Failed: false } t && !ReferenceEquals(t, s)) total += StageBytes(t, vw, vh);
        if (total <= MaxImageBytes) return true;
        why = $"its images come to {bytes / (1024.0 * 1024.0):F1} MiB, and every native stage's together "
            + $"{total / (1024.0 * 1024.0):F1} MiB, past the budget of {MaxImageBytes / (1024 * 1024)} MiB";
        return false;
    }

    /// <summary>Levels of one image at an extent -- the mip chain counted rather than approximated, since the
    /// budget is taken against it.</summary>
    private static int Levels(Img im, int w, int h, int d)
    {
        if (!im.Mips) return 1;
        int side = Math.Max(w, Math.Max(h, im.Shape == Shape.Tex3d ? d : 1)), n = 1;
        while (side > 1) { side >>= 1; n++; }
        return n;
    }

    private static long Bytes(Img im, int w, int h, int d)
    {
        long total = 0;
        int levels = Levels(im, w, h, d);
        for (int l = 0; l < levels; l++)
        {
            total += (long)Math.Max(w >> l, 1) * Math.Max(h >> l, 1)
                   * (im.Shape == Shape.Tex3d ? Math.Max(d >> l, 1) : 1) * im.Bpp;
        }
        return total;
    }


    /// <summary>Work groups over one extent: the division rounded UP, so the texels of a last partial group
    /// still get an invocation (and the stage's own bounds test is what keeps it inside the image).</summary>
    private static uint Groups(int extent, int group) => (uint)((extent + group - 1) / Math.Max(group, 1));
}

