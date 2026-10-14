using System;
using System.Collections.Generic;
using System.Threading;
using CX.Ui;

namespace CX.Services.Panels;

/// <summary>
/// The text faces a panel draws with. A size other than the player's own is a multiple a style's <c>sizes</c> declares; its face
/// is baked on the thread pool when a declaration that uses it arrives (baking rebuilds the glyph sheet). A face that is not
/// baked yet draws as the player's face stretched, which is blurrier and never wrong.
/// </summary>
internal static class PanelFonts
{
    /// <summary>The most distinct sizes the panels bake in one run; a declaration that would bake past it is refused.</summary>
    public const int MaxFaces = 24;

    private sealed class FacesState
    {
        public readonly HashSet<float> Wanted = [];
        /// <summary>Every size asked for this run. Baked faces stay in the glyph sheet, so this one is never emptied.</summary>
        public readonly HashSet<float> Ever = [];
    }

    private static readonly FacesState _f = new();

    /// <summary>Asks for the faces of <paramref name="mults"/> times the player's text size; a new one is baked on the thread pool.
    /// False, asking for none, when the new ones would take the run past <see cref="MaxFaces"/>.</summary>
    public static bool TryWant(IEnumerable<float> mults)
    {
        var fresh = new List<float>();
        lock (_f.Wanted)
        {
            int added = 0;
            foreach (float m in mults)
                if (!_f.Ever.Contains(m) && !fresh.Contains(m)) { fresh.Add(m); added++; }
            if (_f.Ever.Count + added > MaxFaces) return false;
            foreach (float m in mults) { _f.Wanted.Add(m); _f.Ever.Add(m); }
        }
        foreach (float m in fresh)
        {
            float px = PxOf(m);
            if (TextRig.BakedFace(px) == null) ThreadPool.QueueUserWorkItem(static p => TextRig.Face(p), px, preferLocal: false);
        }
        return true;
    }

    /// <summary>Forgets every size asked for. For the self-check only.</summary>
    internal static void Reset()
    {
        lock (_f.Wanted) { _f.Wanted.Clear(); _f.Ever.Clear(); }
    }

    /// <summary>Every card is gone: a font change stops asking for their faces. ponytail: the baked faces stay in the sheet for the
    /// session, since a release by size would also drop another feature's face of that size; a per-owner release is the upgrade.</summary>
    public static void Forget()
    {
        lock (_f.Wanted) _f.Wanted.Clear();
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
