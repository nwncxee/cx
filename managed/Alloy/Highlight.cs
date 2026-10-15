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
internal static unsafe class Highlight
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

    private static uint _progHl, _progHlComp, _hlMaskTex, _hlMaskFbo, _hlVao;
    private static int _hlVp, _hlBase, _hlColor, _hlCompMask, _hlCompFill, _hlCompRadius, _hlCompUnjitter;
    private static int _hlW, _hlH;
    private static uint _hlDepthAttached;
    private static bool _hlFailed, _hlLogged;

    internal static void HighlightContextLost()
    {
        _progHl = 0; _progHlComp = 0; _hlMaskTex = 0; _hlMaskFbo = 0; _hlVao = 0;
        _hlW = 0; _hlH = 0; _hlDepthAttached = 0;
    }

    /// <summary>The root's highlight colour at full coverage, over <see cref="DepthVs"/> -- the vertex
    /// stage is reused verbatim so the mask's biased LEQUAL test sits against exactly the positions the
    /// colour pass wrote depth from.</summary>
    private static string HighlightMaskFs => ShaderSource.Get("cx_hilitemask_f.glsl");

    /// <summary>The composite: fill where the mask has coverage (its bilinear edge antialiases the rim, and
    /// rgb rides the same fetch so per-object colours cost nothing), outline where a ring-tap dilation of
    /// the coverage lands OUTSIDE it, coloured by the taps' own coverage-weighted rgb. Coverage through
    /// smoothstep is what makes the outline read as one thick soft stroke rather than an aliased wireframe.</summary>
    private static string HighlightCompFs => ShaderSource.Get("cx_hilitecomp_f.glsl");

    private static bool EnsureHighlightPrograms()
    {
        if (_progHl != 0 && _progHlComp != 0) return true;
        if (_hlFailed) return false;
        if (Gl.GenFramebuffers == null || Gl.FramebufferTexture2D == null || Gl.PolygonOffset == null)
        { _hlFailed = true; return false; }
        uint mask = GlProgram.Graphics(DepthVs, HighlightMaskFs, _log, "AlloyRenderer highlight mask program");
        uint comp = GlProgram.Graphics(PostChain.FullscreenVs, HighlightCompFs, _log,
                                       "AlloyRenderer highlight composite program");
        if (mask == 0 || comp == 0) { _hlFailed = true; return false; }
        _progHl = mask;
        _hlVp = GlProgram.Loc(mask, "uVP");
        _hlBase = GlProgram.Loc(mask, "uDrawBase");
        _hlColor = GlProgram.Loc(mask, "uColor");
        _progHlComp = comp;
        _hlCompMask = GlProgram.Loc(comp, "uMask"); _hlCompUnjitter = GlProgram.Loc(comp, "uUnjitter");
        _hlCompFill = GlProgram.Loc(comp, "uFill");
        _hlCompRadius = GlProgram.Loc(comp, "uRadius");
        if (_hlVao == 0) { uint v; Gl.CreateVertexArrays(1, &v); _hlVao = v; }
        return true;
    }

    /// <summary>The RGBA8 mask target, viewport-sized, recreated on resize. The frame's depth texture is
    /// attached fresh whenever <see cref="PostChain"/> recreates it on its own schedule.</summary>
    private static bool EnsureHighlightMask(int w, int h)
    {
        if (_hlMaskTex == 0 || w != _hlW || h != _hlH)
        {
            if (_hlMaskFbo != 0) { uint f = _hlMaskFbo; Gl.DeleteFramebuffers(1, &f); _hlMaskFbo = 0; }
            uint tex = _hlMaskTex;
            RenderTarget.AllocColor(ref tex, false, w, h, Gl.LINEAR);
            uint fbo; Gl.GenFramebuffers(1, &fbo);
            Gl.BindFramebuffer(Gl.FRAMEBUFFER, fbo);
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.COLOR_ATTACHMENT0, Gl.TEXTURE_2D, tex, 0);
            _hlMaskTex = tex; _hlMaskFbo = fbo; _hlW = w; _hlH = h; _hlDepthAttached = 0;
        }
        else Gl.BindFramebuffer(Gl.FRAMEBUFFER, _hlMaskFbo);
        if (_hlDepthAttached != PostChain.DepthTex)
        {
            Gl.FramebufferTexture2D(Gl.FRAMEBUFFER, Gl.DEPTH_ATTACHMENT, Gl.TEXTURE_2D,
                                    PostChain.DepthTex, 0);
            if (Gl.CheckFramebufferStatus(Gl.FRAMEBUFFER) != Gl.FRAMEBUFFER_COMPLETE)
            {
                _hlFailed = true;
                _log?.Info("AlloyRenderer: highlight mask framebuffer incomplete -- outline highlight disarmed");
                return false;
            }
            _hlDepthAttached = PostChain.DepthTex;
        }
        return true;
    }

    /// <summary>The pass, called from the frame walk right after the post-process composite with the engine
    /// target bound. Every highlighted root is covered: one the outline claims and Alloy drew takes the mask,
    /// the rest are staged into <see cref="HighlightGlow"/>. The mask draws at the RENDER size
    /// (<paramref name="viewport"/>) to depth-test the frame depth, the composite at <paramref name="display"/>.</summary>
    internal static void OutlineHighlightPass(int groups, Matrix4x4* vp, Matrix4x4* view, Matrix4x4* proj,
                                             float jx, float jy, int* viewport, int* display)
    {
        // The highlighted set is the scene database's: the seam records every AddHighlightGob in SceneDb.
        DbId dbScene = AlloyRenderer.DbPassScene;
        int nGobs = SceneFeed.HighlightCount(dbScene);
        if (nGobs == 0) return;
        if (PostChain.DepthTex == 0) return;
        if (nGobs > MaxHlRoots) nGobs = MaxHlRoots;
        HighlightGlow.Begin(dbScene, EyePos);

        // The target set for EVERY highlighted root at once: the gob plus its visible attached children
        // (weapons, shields -- exactly the set the engine glow covers), each carrying its root's index so
        // the mask draw picks the right colour and the hit lands on the right root. Read per frame off the
        // live Scene; nothing cached across frames (borrowed pointers).
        IntPtr* targets = stackalloc IntPtr[MaxHlTargets];
        byte* targetRoot = stackalloc byte[MaxHlTargets];
        byte* rootHit = stackalloc byte[MaxHlRoots];
        IntPtr* kids = stackalloc IntPtr[MaxHlTargets];
        byte* kidRoot = stackalloc byte[MaxHlTargets];
        // A root KEY per entry (the DB record's handle value) and its colour.
        IntPtr* arr = stackalloc IntPtr[MaxHlRoots];
        float* cols = stackalloc float[MaxHlRoots * 3];
        for (int i = 0; i < nGobs; i++)
        {
            SceneFeed.HighlightAt(dbScene, i, out DbId o, out Vector3 c);
            arr[i] = (IntPtr)o.Value;
            cols[i * 3] = c.X; cols[i * 3 + 1] = c.Y; cols[i * 3 + 2] = c.Z;
        }
        if (HlDiag) DiagRecords(nGobs, arr);
        int nT = 0;
        bool outline = Renderer.OutlineHighlight && EnsureHighlightPrograms();
        for (int i = 0; i < nGobs; i++)
        {
            rootHit[i] = 0;
            if (!outline || nT >= MaxHlTargets) continue;
            IntPtr gob = arr[i];
            // The list is source-agnostic, so only what the source hook claimed is ours.
            if (!OwnedHighlightDb(new DbId((uint)gob))) continue;
            targetRoot[nT] = (byte)i;
            targets[nT++] = gob;
            nT = DbAttached(new DbId((uint)gob), (byte)i, targets, targetRoot, nT);
        }

        int prevFbo;
        Gl.GetIntegerv(Gl.FRAMEBUFFER_BINDING, &prevFbo);

        if (nT > 0 && EnsureHighlightMask(viewport[2], viewport[3]))
        {
            // Step 1: ONE visibility mask for the whole set. Biased LEQUAL against the frame's own depth --
            // visible surface takes its root's colour at full coverage, everything else stays the cleared zero.
            Gl.Viewport(0, 0, viewport[2], viewport[3]);
            Gl.ClearColor(0f, 0f, 0f, 0f);
            Gl.Clear(Gl.COLOR_BUFFER_BIT);
            Gl.UseProgram(_progHl);
            Gl.UniformMatrix4fv(_hlVp, 1, 0, (float*)vp);
            Gl.Enable(Gl.DEPTH_TEST);
            Gl.DepthFunc(Gl.LEQUAL);
            Gl.DepthMask(0);
            Gl.Disable(Gl.BLEND);
            Gl.PolygonOffset(MaskOffsetFactor, MaskOffsetUnits);
            DrawTargets(groups, targets, targetRoot, rootHit, nT, cols);
            Gl.Disable(Gl.POLYGON_OFFSET_FILL);
            Gl.DepthFunc(Gl.LEQUAL);   // the cutout run leaves EQUAL bound; every later pass assumes LEQUAL
        }
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, (uint)prevFbo);
        Gl.Viewport(display[0], display[1], display[2], display[3]);

        // Whatever the outline could not cover -- an unclaimed source channel, or a root with no Alloy draw
        // records at all -- is the glow's, in the slot the engine's DoHighlightGob had.
        int hit = 0;
        for (int i = 0; i < nGobs; i++)
        {
            if (rootHit[i] != 0) { hit++; continue; }
            var root = new DbId((uint)arr[i]);
            fixed (float* c3 = new Span<float>(cols, MaxHlRoots * 3).Slice(i * 3, 3))
                HighlightGlow.Stage(root, c3, kids, DbAttached(root, 0, kids, kidRoot, 0));
        }
        HighlightGlow.Draw(view, proj, display);
        if (hit == 0) return;

        // Step 2: ONE fullscreen triangle over the finished frame -- fill + dilated outline, blended.
        Gl.UseProgram(_progHlComp);
        Gl.BindVertexArray(_hlVao);
        Gl.Disable(Gl.DEPTH_TEST);
        Gl.Enable(Gl.BLEND);
        Gl.BlendFunc(Gl.SRC_ALPHA, Gl.ONE_MINUS_SRC_ALPHA);
        Gl.ActiveTexture(Gl.TEXTURE0);
        Gl.BindTexture(Gl.TEXTURE_2D, _hlMaskTex);
        Gl.Uniform1i(_hlCompMask, 0);
        Gl.Uniform1f(_hlCompFill, FillAlpha);
        Gl.Uniform1f(_hlCompRadius, OutlinePx);
        // NDC offset j is uv offset j/2; the mask holds clean + j, so clean reads at uv + j/2
        Gl.Uniform2f(_hlCompUnjitter, jx * 0.5f, jy * 0.5f);
        Gl.DrawArrays(Gl.TRIANGLES, 0, 3);
        Gl.BindVertexArray(0);

        // Hand the state back the way Composite leaves it: depth on, mask on, blend off.
        Gl.Disable(Gl.BLEND);
        Gl.Enable(Gl.DEPTH_TEST);
        Gl.DepthMask(1);

        if (_hlLogged) return;
        _hlLogged = true;
        _log?.Info($"AlloyRenderer: outline highlight LIVE -- {hit}/{nGobs} root gob(s) outlined "
                 + "(screen-space mask fill + dilated outline, one pass for the set); every other "
                 + "highlighted root takes the glow");
    }

    private static long _diagKey;

    /// <summary><c>/alloy hldiag 1</c>, once per change of the highlighted set: what each root's colour slots
    /// left in the database (<c>RefreshHilite</c> calls <c>SetColorShifting</c> and <c>SetIllumination</c> with
    /// the highlight colour) and what reached its first drawn part's record (diffuse lanes 24..26, emissive
    /// 28..30) -- the two ends of the vanilla recolour, so a missing tint names its own break.</summary>
    private static void DiagRecords(int n, IntPtr* roots)
    {
        long key = n;
        for (int i = 0; i < n; i++) key = key * 31 + (long)roots[i];
        if (key == _diagKey) return;
        _diagKey = key;
        for (int i = 0; i < n; i++)
        {
            var id = new DbId((uint)roots[i]);
            if (!SceneDb.Objects.Valid(id)) continue;
            int o = id.Index, first = SceneDb.ObjPartFirst[o], slot = -1;
            for (int p = first; p < first + SceneDb.ObjPartCount[o] && slot < 0; p++) slot = SceneDb.PartSlot[p];
            string rec = "no drawn part";
            if (slot >= 0)
            {
                ref DrawRecordBlock r = ref _dData[slot];
                rec = $"record dif=({r.DifR:F2},{r.DifG:F2},{r.DifB:F2}) emi=({r.EmiR:F2},{r.EmiG:F2},{r.EmiB:F2})";
            }
            _log?.Info($"[hldiag] root o={o} \"{SceneDb.ObjModel[o]?.Name ?? "-"}\" tint={SceneDb.ObjTint[o]} "
                       + $"illum={SceneDb.ObjEmissive[o]} flags={SceneDb.ObjSeamFlags[o]:x} {rec}");
        }
    }

    /// <summary>One walk over the staged batches, a single-draw multidraw per record whose owning gob is in the
    /// target set, the root's colour bound and the root marked hit. Cutout batches are IN, at <c>GL_EQUAL</c>
    /// with no bias, so only fragments that survived their colour-pass discard match. Out: displacing batches,
    /// and everything past <see cref="_gBlendEnd"/>, which writes no depth to match against.</summary>
    private static void DrawTargets(int groups, IntPtr* targets, byte* targetRoot, byte* rootHit, int nT,
                                    float* cols)
    {
        uint boundVao = 0;
        int boundCull = -1, boundColor = -1, boundCut = -1;
        // The blended class writes depth too now (Issue.TransparentPass depthWrite), so a translucent creature matches.
        int end = groups < _gBlendEnd ? groups : _gBlendEnd;
        for (int g = 0; g < end; g++)
        {
            if (ShaderFeatures.OutOfPrepass(_gFeat[g])) continue;
            int s = _gStart[g], len = _gLen[g];
            for (int j = s; j < s + len; j++)
            {
                // The slot's OWNER, in whichever shape this mode's target keys are: the engine's gob pointer,
                // or the DB record handle of the object the part belongs to.
                int slot = _dSlot[j];
                IntPtr gob = DbSlot(slot) ? (IntPtr)SceneDb.PartObj[_oDbPart[slot]].Value : _oGob[slot];
                if (gob == IntPtr.Zero) continue;
                // An alpha-0 or untextured part (a door's `sam` doorway sheen) is no silhouette to outline.
                if (DbSlot(slot) && (SceneDb.PartAlpha[_oDbPart[slot]] <= 0f || AlloyRenderer.DbUntextured(slot))) continue;
                int hit = -1;
                for (int k = 0; k < nT; k++)
                    if (targets[k] == gob) { hit = k; break; }
                if (hit < 0) continue;
                int root = targetRoot[hit];
                rootHit[root] = 1;
                if (root != boundColor)
                {
                    boundColor = root;
                    Gl.Uniform3f(_hlColor, cols[root * 3], cols[root * 3 + 1], cols[root * 3 + 2]);
                }
                // The two depth regimes: opaque took its depth from the prepass, so the mask is biased toward
                // the viewer and tested LEQUAL against exact-equality speckle; cutout wrote its own,
                // discard-applied, in the colour pass, so the mask tests EQUAL with no bias -- a bias there
                // would let the alpha holes through.
                int wantCut = _gCls[g] == (byte)AlloyClass.Cutout ? 1 : 0;
                if (wantCut != boundCut)
                {
                    boundCut = wantCut;
                    if (wantCut != 0)
                    { Gl.Disable(Gl.POLYGON_OFFSET_FILL); Gl.DepthFunc(Gl.EQUAL); }
                    else
                    { Gl.Enable(Gl.POLYGON_OFFSET_FILL); Gl.DepthFunc(Gl.LEQUAL); }
                }
                BindBatchState(g, ref boundCull, ref boundVao);
                Gl.Uniform1i(_hlBase, j);
                AlloyRenderer.MultiDrawRange(_gMode[g], j, 1);
            }
        }
    }
}
