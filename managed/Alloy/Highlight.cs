using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

using static CX.Alloy.AlloyRenderer;

namespace CX.Alloy;

/// <summary>The OWNED highlight, <c>alloy.highlight</c>'s default: instead of the engine's
/// <c>Scene::DoHighlightGob</c> glow quads, the highlighted object is redrawn flat -- a <see cref="FillAlpha"/>
/// fill plus a thick, smooth silhouette outline -- right after <see cref="PostChain"/>'s composite, and two
/// draws deep whatever the highlight count: one RGBA mask, then one fullscreen dilation blend over it.</summary>
internal static unsafe partial class Highlight
{

    /// <summary>Ceilings: highlighted ROOT gobs per frame, and (roots + attached children) in total.
    /// Roots beyond the cap take the glow instead of silently vanishing.
    /// ponytail: the per-draw match below is a linear scan over these targets; if a census ever shows
    /// hundreds of simultaneous highlights mattering, swap it for a fixed-size open-addressed table.</summary>
    private const int MaxHlRoots = 128, MaxHlTargets = 256;

    /// <summary>The highlight CHANNELS as <c>CNWCAnimBase</c> lays them out: active flag at +0x9c[ch], colour
    /// at +0x60[ch] (12 bytes), "also add a glow gob" at +0xb0[ch], the winner's gob at +0xc8, and
    /// <c>RefreshHilite</c> takes the LOWEST active slot. The channel is the SOURCE the Scene highlight list
    /// does not carry: Tab and mouseover arrive on 2, every VFX that highlights on 3.</summary>
    private const int HiliteChannels = 5;

    /// <summary>Channels the owned outline claims, as a bitmask (<c>/alloy hlchan</c>). 3 is the VFX channel
    /// and is NOT claimed: a spell glow drawn as a crisp outline reads as a Tab highlight. 4, the tab-target
    /// lock, is a Tab highlight in all but source, so it is. Unclaimed falls through to the glow.</summary>
    internal static int HlChannels => Renderer.HlChan.V;
    internal const int DefHlChannels = 0b10111;

    /// <summary>Gobs whose winning channel the outline claims, refreshed from the engine's own verdict at
    /// every <c>RefreshHilite</c>. Small and churn-free: an entry per highlighted object, not per part.</summary>
    private static readonly IntPtr[] _ownedHl = new IntPtr[MaxHlRoots];
    private static int _ownedHlN;

    /// <summary><c>CNWCAnimBase::RefreshHilite</c>, after the original. The engine has just recomputed the
    /// winning channel and published (or withdrawn) the gob, so this reads the result instead of predicting
    /// it -- no duplicated priority rule to drift.</summary>
    [UnmanagedCallersOnly]
    internal static void OnRefreshHilite(IntPtr animBase)
    {
        _origRefreshHilite(animBase);
        if (animBase == IntPtr.Zero) return;
        var anim = new CNWCAnimBase(animBase);
        IntPtr gob = anim.Gob.Ptr;
        // The trace goes AHEAD of the early return: a null gob is a legitimate state (the engine's own body
        // takes the same branch), so "fired, published no gob" has to be distinguishable from "never fired".
        if (HlDiag) _log?.Info($"[hldiag] enter animBase={animBase:x} gob={gob:x}");
        if (gob == IntPtr.Zero) return;
        int win = -1;
        Span<int> active = anim.HiliteActive, glow = anim.HiliteGlow;
        for (int c = 0; c < HiliteChannels && win < 0; c++)
            if (active[c] != 0) win = c;
        // Claimed only when the winner is ours AND the engine would publish a gob for it at all.
        bool claim = win >= 0 && (HlChannels & (1 << win)) != 0 && glow[win] != 0;
        SetClaim(gob, claim);
        if (HlDiag)
            _log?.Info($"[hldiag] gob={gob:x} channel={win} glow={(win >= 0 ? glow[win] : 0)}"
                       + $" -> {(claim ? "OWNED outline" : "engine glow")}");
    }

    internal static RefreshHiliteFn _origRefreshHilite;

    /// <summary>Add <paramref name="gob"/> to the claim list, or drop it. The facade's retire drops it too, so an
    /// object the game destroys while highlighted leaves before its block can be handed to a new one.</summary>
    internal static void SetClaim(IntPtr gob, bool claim)
    {
        int at = Array.IndexOf(_ownedHl, gob, 0, _ownedHlN);
        if (claim)
        {
            if (at < 0 && _ownedHlN < _ownedHl.Length) _ownedHl[_ownedHlN++] = gob;
        }
        else if (at >= 0) _ownedHl[at] = _ownedHl[--_ownedHlN];
    }

    /// <summary>Is <paramref name="gob"/> in the claim list?</summary>
    internal static bool Claimed(IntPtr gob) => Array.IndexOf(_ownedHl, gob, 0, _ownedHlN) >= 0;

    /// <summary>Did the source hook claim this record? A gob it never saw takes the glow. The claims are the
    /// facade shells the game holds (<c>CNWCAnimBase+0xc8</c>), so the record's own shell is compared by
    /// pointer: resolving a claim would land on a retired shell once the game destroyed the object.</summary>
    internal static bool OwnedHighlightDb(DbId obj)
    {
        nint shell = Facade.ObjectShell(obj);
        return shell != 0 && Claimed(shell);
    }

    /// <summary>The highlighted root's attached children, in the shape <c>Gob::GetAttachedGobs</c> hands
    /// back. Attachment is a field ON THE CHILD, so this is one walk of the object table per highlighted
    /// root -- cold (only while something is highlighted) and allocation-free.</summary>
    private static int DbAttached(DbId root, byte rootIndex, IntPtr* targets, byte* targetRoot, int nT)
    {
        for (int o = 1; o < SceneDb.Objects.High && nT < MaxHlTargets; o++)
        {
            if (!SceneDb.Objects.LiveAt(o) || SceneDb.ObjAttachTo[o] != root) continue;
            if ((SceneDb.ObjSeamFlags[o] & (uint)SeamFlag.PropagateHighlight) == 0) continue;
            targetRoot[nT] = rootIndex;
            targets[nT++] = (IntPtr)SceneDb.Objects.IdAt(o).Value;
        }
        return nT;
    }

    /// <summary><c>/alloy hldiag 1</c>: name the winning channel and the verdict every time a highlight
    /// changes. The source is the question now, so the colour is not worth printing.</summary>
    internal static bool HlDiag => Renderer.HlDiag.V;

    /// <summary>Outline thickness in pixels: the dilation radius the composite's ring taps sample at.</summary>
    private const float OutlinePx = 5f;

    /// <summary>Fill opacity over the highlighted object's visible surface.</summary>
    private const float FillAlpha = 0.10f;

    /// <summary>The mask rasterisation's depth bias: pulled toward the viewer so the re-draw passes LEQUAL
    /// against its own colour-pass depth everywhere, instead of speckling on exact-equality precision.</summary>
    private const float MaskOffsetFactor = -1f, MaskOffsetUnits = -2f;

    /// <summary>The root's highlight colour at full coverage, over <see cref="DepthVs"/> -- the vertex
    /// stage is reused verbatim so the mask's biased LEQUAL test sits against exactly the positions the
    /// colour pass wrote depth from.</summary>
    internal static string HighlightMaskFs => ShaderSource.Get("cx_hilitemask_f.glsl");

    /// <summary>The composite: fill where the mask has coverage (its bilinear edge antialiases the rim, and
    /// rgb rides the same fetch so per-object colours cost nothing), outline where a ring-tap dilation of
    /// the coverage lands OUTSIDE it, coloured by the taps' own coverage-weighted rgb. Coverage through
    /// smoothstep is what makes the outline read as one thick soft stroke rather than an aliased wireframe.</summary>
    internal static string HighlightCompFs => ShaderSource.Get("cx_hilitecomp_f.glsl");
}
