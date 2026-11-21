using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CX.Ui;

namespace CX.Services.Panels;

/// <summary>
/// The text faces a panel draws with. A size other than the player's own is a multiple a style's <c>sizes</c> declares; its face
/// is baked on the thread pool when a declaration that uses it arrives (baking rebuilds the glyph sheet), and released when no
/// held card uses it. A face that is not baked yet draws as the player's face stretched, which is blurrier and never wrong.
/// </summary>
internal static class PanelFonts
{
    /// <summary>The most faces the panels hold at once, one per distinct size; a declaration that would hold more is refused.</summary>
    public const int MaxFaces = 24;
    /// <summary>The owner name the panels' faces are held under.</summary>
    private const string Owner = "panels";

    private sealed class FacesState
    {
        /// <summary>Each size the panels hold and the px its face was asked at.</summary>
        public readonly Dictionary<float, float> Held = [];
        /// <summary>Each named face a run asked a bake for, so it is asked once.</summary>
        public readonly HashSet<(string Name, float Px)> Asked = [];
    }

    private static readonly FacesState _f = new();

    /// <summary>Holds the faces of exactly <paramref name="mults"/> times the player's text size: a new one is baked on the thread
    /// pool, and one no longer listed is released. False, changing nothing, when that is more than <see cref="MaxFaces"/>.</summary>
    public static bool TryHold(IReadOnlySet<float> mults)
    {
        var fresh = new List<float>();
        var gone = new List<float>();
        lock (_f)
        {
            if (mults.Count > MaxFaces) return false;
            foreach (float m in _f.Held.Keys.ToList())
                if (!mults.Contains(m)) { gone.Add(_f.Held[m]); _f.Held.Remove(m); }
            foreach (float m in mults)
                if (!_f.Held.ContainsKey(m)) { float px = PxOf(m); _f.Held[m] = px; fresh.Add(px); }
            gone.RemoveAll(px => _f.Held.ContainsValue(px));
        }
        foreach (float px in gone) TextRig.ReleaseFace(px, Owner);
        foreach (float px in fresh) ThreadPool.QueueUserWorkItem(static p => Bake(p), px, preferLocal: false);
        return true;
    }

    /// <summary>The number of sizes held now.</summary>
    internal static int Count { get { lock (_f) return _f.Held.Count; } }

    /// <summary>Releases every face. For the self-check only.</summary>
    internal static void Reset() => TryHold(new HashSet<float>());

    /// <summary>Bakes the face at <paramref name="px"/>, then lets it go again if the size was released while it baked.</summary>
    private static void Bake(float px)
    {
        TextRig.Face(px, Owner);
        bool held;
        lock (_f) held = _f.Held.ContainsValue(px);
        if (!held) TextRig.ReleaseFace(px, Owner);
    }

    /// <summary>The player's text size or the UI scale changed: each held size is asked for at its new px and the old face
    /// released. Cold; runs on the baking thread.</summary>
    public static void WantAll()
    {
        var moved = new List<(float Old, float New)>();
        lock (_f)
            foreach (float m in _f.Held.Keys.ToList())
            {
                float px = PxOf(m);
                if (px != _f.Held[m]) { moved.Add((_f.Held[m], px)); _f.Held[m] = px; }
            }
        foreach (var (old, px) in moved)
        {
            TextRig.Face(px, Owner);
            bool still;
            lock (_f) still = _f.Held.ContainsValue(old);
            if (!still) TextRig.ReleaseFace(old, Owner);
        }
    }

    /// <summary>The registry name <paramref name="r"/> draws with over the UI's rows, and whether its letters are
    /// slanted (<paramref name="slant"/>): an italic run on an upright face.</summary>
    public static string RunFace(in Run r, out bool slant)
        => FontRegistry.RunFace(UiKnobs.Typeface.V, UiKnobs.FontWeight.V, UiKnobs.FontStyle.V, r.Weight, r.Style, r.Italic, out slant);

    /// <summary>The atlas of face <paramref name="name"/> at <paramref name="mult"/> times the player's size, when it is baked;
    /// a miss asks its bake on the thread pool once and is null until it lands, and null when there is no UI face (headless).
    /// ponytail: a named face stays in the sheet for the session; releasing one no card uses is the upgrade.</summary>
    public static (GlyphAtlas A, float K)? Named(string name, float mult)
    {
        if (TextRig.Atlas == null) return null;
        float px = PxOf(mult);
        if (TextRig.BakedFace(name, px) is GlyphAtlas f) return (f, 1f);
        bool fresh;
        lock (_f) fresh = _f.Asked.Add((name, px));
        if (fresh) ThreadPool.QueueUserWorkItem(static a => TextRig.Face(a.Name, a.Px, Owner), (Name: name, Px: px), preferLocal: false);
        return null;
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
