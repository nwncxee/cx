using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The gather's staging and submit: the per-view source words the compute shader reads, the dynamic-source
/// copy and its audit, and the indirect draws each batch issues.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary>The compute gather's per-frame staging. Parallel to the live list but in BATCH order,
    /// which is what lets one dispatch feed one glMultiDrawElementsIndirectCount per batch.</summary>
    private static readonly uint[] _gSrc = new uint[MaxCasters * CasterGather.SrcUints];
    /// <summary>Batch key per staged draw, and the live index it names. The key orders masked draws last (one
    /// program switch instead of one per VAO), then by VAO, primitive and mask texture -- the (VAO, prim,
    /// maskTex) key the multidraws cut on.</summary>
    private static readonly long[] _gKey = new long[MaxCasters];
    private static readonly int[] _gLive = new int[MaxCasters];
    private static readonly int[] _gVao = new int[MaxCasters], _gPrim = new int[MaxCasters],
                                 _gTex = new int[MaxCasters], _gStart = new int[MaxCasters],
                                 _gLen = new int[MaxCasters];
    /// <summary>The batch's cutout threshold KIND, resolved to a number at the upload. Part of the sort key,
    /// so a batch carries one -- the same shape the colour path's <c>uAlphaCut</c> has.</summary>
    private static readonly CasterCut[] _gCut = new CasterCut[MaxCasters];
    private static int _gDrawN, _gGroups, _gCreatures;
    /// <summary>SL1: per LIVE entry, its slot in the standing staging, or -1 when the staging does not hold it
    /// (no VAO, a blended caster the mask stage cannot take, <c>/shadow mask -1</c>). The reverse of
    /// <see cref="_gLive"/>, which is what lets the per-frame word rewrite visit the CHANGED entries instead of
    /// all 37k staged ones.</summary>
    private static readonly int[] _gAt = new int[MaxCasters];
    /// <summary>SL1: live entries whose two per-frame staged words may differ from the staging. Fed by the
    /// refresh's visited set -- the only walk that can move them -- and ACCUMULATED, because a frame with no
    /// light to bake for refreshes without re-staging. Deduped by <see cref="_gDirtyMark"/>, so it cannot
    /// exceed the live count and has no overflow case.</summary>
    private static readonly int[] _gDirty = new int[MaxCasters];
    private static readonly bool[] _gDirtyMark = new bool[MaxCasters];
    private static int _gDirtyN;
    /// <summary>SL1's audit: 1 frame in <see cref="StageAuditEvery"/> re-reads every staged entry and counts
    /// the ones the changed-set walk left out of date (and fixes them). MUST read 0 -- a miss is a caster
    /// baking at another frame's index count, or in the wrong half of the static/dynamic split.
    /// <c>/shadow refreshall 1</c> marks every entry, which is the whole-list A/B over the same machinery.</summary>
    private static long _gStaleWords, _gAudits;
    private const int StageAuditEvery = 64;

    /// <summary>SL1: note that this live entry's staged words may have moved. Hot (per visited caster per
    /// frame), allocates nothing.</summary>
    private static void NoteStagedWord(int i)
    {
        if (_gDirtyMark[i]) return;
        _gDirtyMark[i] = true;
        _gDirty[_gDirtyN++] = i;
    }

    /// <summary>The two staged words that can move without a re-staging -- the index count (an entry turned
    /// off draws nothing) and the set flag. True = they differed, i.e. the source buffer needs re-uploading.
    /// One function so the changed-set walk and its audit cannot diverge.</summary>
    private static bool WriteStagedWords(uint* src, int d, int i)
    {
        uint* r = src + (long)d * CasterGather.SrcUints;
        uint cnt = _liveOff[i] ? 0u : (uint)_liveIdxCount[i], set = _liveDyn[i] ? 1u : 0u;
        if (r[0] == cnt && r[CasterGather.SetFlagWord] == set) return false;
        // The batch's dynamic population follows the set flag, and the batch is the word the staging wrote
        // beside it -- no second lookup.
        if (r[CasterGather.SetFlagWord] != set) _gDynN[(int)r[4]] += set != 0 ? 1 : -1;
        r[0] = cnt; r[CasterGather.SetFlagWord] = set;
        return true;
    }

    /// <summary>The DYNAMIC source -- a verbatim copy of the staged records whose set flag is 1. Each record
    /// carries its own batch id and output base, so a survivor lands in exactly the region and batch it would
    /// have from the full source, and the command buffer, count buffer, batch table and draw loop are
    /// untouched. What changes is the thread count, and dynamic faces are most of the frame's views.</summary>
    private const int MaxDynSrc = 8192;
    private static readonly uint[] _dSrc = new uint[MaxDynSrc * CasterGather.SrcUints];
    /// <summary>Frames the dynamic source stood, the draws it covered, frames past <see cref="MaxDynSrc"/>
    /// (which fall back to the full source -- the same set by a longer road), and the audit's misses.</summary>
    private static long _dSrcFrames, _dSrcDraws, _dSrcOver, _dSrcAudits, _dSrcMiss;
    /// <summary>Frames the movers were more than half the staging, so the short source would have saved
    /// nothing. Its own number: a steady value here means the split has stopped being worth having.</summary>
    private static long _dSrcHalf;
    /// <summary>Records the copy took that are NOT in the dynamic set -- a still caster in the moving layer.
    /// The half of the audit that <c>want != dyn</c> cannot see, so it carries its own number.</summary>
    private static long _dSrcExtra;

    /// <summary>Build and publish this frame's dynamic source. Inside the bracket, after the staging stands:
    /// the membership is <c>_dMoverIdx</c>, which Collect and Refresh rebuild whole every frame.</summary>
    private static void StageDynamicSource()
    {
        if (!Shadows.DynSrc.V || _gN <= 0 || _dMoverCount <= 0) { CasterGather.StandDynamic(); return; }
        fixed (uint* src = _gSrc)
        fixed (uint* dst = _dSrc)
        {
            int m = CopyDynamicSource(src, dst);
            if (m < 0) { _dSrcOver++; CasterGather.StandDynamic(); return; }
            // Past half the staging there is nothing left to save, and the upload would cost more than the
            // threads: the full source IS the dynamic source at that point.
            if (m == 0 || m * 2 > _gN) { if (m > 0) _dSrcHalf++; CasterGather.StandDynamic(); return; }
            if ((_frameSeq & (StageAuditEvery - 1)) == 0) AuditDynamicSource(src, dst, m);
            if (CasterGather.DynamicSource(m, dst)) { _dSrcFrames++; _dSrcDraws += m; }
        }
    }

    /// <summary>The copy itself, apart from the GL upload so the self-check can drive it: every staged mover's
    /// record, verbatim. -1 = more movers than <see cref="MaxDynSrc"/> holds.</summary>
    private static int CopyDynamicSource(uint* src, uint* dst)
    {
        const long RecBytes = CasterGather.SrcUints * sizeof(uint);
        int m = 0;
        for (int q = 0; q < _dMoverCount; q++)
        {
            int d = _gAt[_dMoverIdx[q]];
            if (d < 0) continue;   // this mover is not in the staging at all (no VAO, blended, mask -1)
            if (m >= MaxDynSrc) return -1;
            Buffer.MemoryCopy(src + (long)d * CasterGather.SrcUints,
                              dst + (long)m * CasterGather.SrcUints, RecBytes, RecBytes);
            m++;
        }
        return m;
    }

    /// <summary>1 frame in <see cref="StageAuditEvery"/>: the dynamic source has to be EXACTLY the staged
    /// draws a dynamic view would DRAW -- a missing one is a mover with no shadow in its own page, an extra
    /// one a still caster baked into the moving layer. "Would draw" and not "has the set flag", since an
    /// entry that turns off keeps its flag; both directions are counted, and both must read 0.</summary>
    private static void AuditDynamicSource(uint* src, uint* dst, int m)
    {
        _dSrcAudits++;
        int want = 0;
        for (int d = 0; d < _gN; d++)
        {
            uint* r = src + (long)d * CasterGather.SrcUints;
            if (r[CasterGather.SetFlagWord] != 0u && r[0] != 0u) want++;
        }
        int dyn = 0, extra = 0;
        for (int k = 0; k < m; k++)
        {
            uint* r = dst + (long)k * CasterGather.SrcUints;
            if (r[CasterGather.SetFlagWord] == 0u) { extra++; continue; }
            if (r[0] != 0u) dyn++;
        }
        if (want != dyn) _dSrcMiss++;
        if (extra > 0) { _dSrcExtra += extra; _dSrcMiss++; }
    }

    /// <summary>SL2/SL6: dynamic casters per batch. A view baking the DYNAMIC set can skip a batch holding
    /// none of them, and one baking the STATIC set can skip a batch holding nothing else -- exactly, from the
    /// CPU, with no readback: the set flag is the CPU's own word. At 438 movers over 37k staged draws most
    /// batches hold no mover at all, and the dynamic pool's faces are most of the frame's views.</summary>
    private static readonly int[] _gDynN = new int[MaxCasters];
    /// <summary>Indirect calls the set-occupancy skip did not issue -- the period line's witness for it.</summary>
    private static long _mdiSkipped;
    /// <summary>Per batch: the chunk's view sets, as a bit per <c>CasterGather.Set*</c>. A batch may be
    /// skipped only when NO view in the chunk could admit one of its casters.</summary>
    private static bool BatchIdle(int g, int sets)
        => sets == 1 << CasterGather.SetDynamic ? _gDynN[g] == 0
         : sets == 1 << CasterGather.SetStatic ? _gDynN[g] >= _gLen[g]
         : false;

    /// <summary>The GL primitive one batch draws. An ARENA batch is TRIANGLES by construction -- the mesh
    /// arena holds one topology, and a DB slot carries no engine primitive enum to translate. Putting it
    /// through the engine's <c>PrimTypeTranslationTable</c> anyway read index 0, which that table answers
    /// <c>GL_POINTS</c>: the bake issued its draws and left a map with nothing in it to shadow with.</summary>
    private static uint BatchMode(int g)
        => AlloyRenderer.IsArenaVao(_gVao[g]) ? Gl.TRIANGLES : _primTable[_gPrim[g]];

    /// <summary>Per batch: the mask is sampled through the record's handle (no texture in the key).</summary>
    private static readonly bool[] _gH = new bool[MaxCasters];
    /// <summary>Batches that bind a mask texture (one indirect call per flush each) and batches that sample
    /// it through the handle block (merged per VAO and primitive).</summary>
    private static string MaskedBatches()
    {
        int b = 0, h = 0;
        for (int g = 0; g < _gGroups; g++) { if (_gH[g]) h++; else if (_gTex[g] != 0) b++; }
        return $"{b} masked bound, {h} by handle";
    }
    /// <summary>The frame produced a coherent staging -- a non-empty one the gather accepted, or a
    /// genuinely empty world. False = the gather refused a non-empty staging (no program, no sphere buffer
    /// yet), and the views must NOT bake -- baking would clear live shadows out of the maps.</summary>
    private static bool _gStaged;

    /// <summary>One staged draw, written into the source record. Its own function because
    /// <see cref="SelfCheck"/> runs it: a field landing one slot over is a caster baking at its neighbour's
    /// matrix, and none of it needs a GL context to be wrong. Word 3 is the OWNERSHIP SLOT -- the dispatch
    /// culls <c>CxCullSphere[slot]</c> and writes it as <c>baseInstance</c> for the depth stages.</summary>
    private static void StageOne(uint* src, int d, int i, int g, int start)
    {
        uint* r = src + (long)d * CasterGather.SrcUints;
        r[0] = _liveOff[i] ? 0u : (uint)_liveIdxCount[i];   // an entry turned off draws nothing
        // in INDICES already, unlike the colour path's byte offsets -- the indirect command takes elements
        r[1] = _liveFirst[i];
        r[2] = _liveBaseVert[i];
        r[3] = (uint)_liveSlot[i];
        r[4] = (uint)g;
        r[5] = (uint)start;
        // #305: the set flag the dispatch filter reads -- which of the two arrays this caster belongs in.
        r[CasterGather.SetFlagWord] = _liveDyn[i] ? 1u : 0u;
        // CX-50: ...and the same mesh's two coarse runs, out of which each view's dispatch picks one. Read
        // HERE rather than carried in the live list: the staging is rebuilt whenever the list moves, and a
        // level is a property of the range for its whole life (the simplifier runs inside Acquire), so this
        // is no staler than the base run beside it -- which the same walk reads the same way.
        AlloyRenderer.DbCasterLevels(_liveSlot[i], out uint c1, out uint f1, out uint c2, out uint f2);
        r[CasterGather.Cnt1Word] = c1; r[CasterGather.First1Word] = f1;
        r[CasterGather.Cnt2Word] = c2; r[CasterGather.First2Word] = f2;
    }

    /// <summary>Stage the whole frame once, in batch order, and publish it. Everything per-view that is not
    /// the cull itself happens here exactly once -- the VAO lookup, the mask-texture resolve, the record.
    /// Inside the bracket, because the mask resolve issues Alloy's own texture copy. False = a non-empty
    /// staging was REFUSED, so the views must idle; an empty world stages true and its views bake clears.</summary>
    private static bool GatherStage()
    {
        if (_gCollectSeq == _collectSeq && _gListVer == _listVer)
        {   // #328: the list stood, so the staging stands. The set flag is the one word that moves per frame
            // (a caster starting or stopping); the gather skips the upload when nothing did.
            fixed (uint* src = _gSrc)
            {
                bool changed = false;
                // SL1: the CHANGED entries, not all of them. r[0] follows _liveOff and _liveIdxCount and
                // r[SetFlagWord] follows _liveDyn; all three move only for an entry the refresh visited (a
                // collect or an append bumps the list version and re-stages instead), and the visited set is
                // ~1100 of 37k. The pre-SL1 walk read four scattered arrays per staged draw every frame.
                for (int q = 0; q < _gDirtyN; q++)
                {
                    int i = _gDirty[q];
                    _gDirtyMark[i] = false;
                    int d = _gAt[i];
                    if (d < 0) continue;   // this entry is not in the staging at all
                    if (WriteStagedWords(src, d, i)) changed = true;
                }
                _gDirtyN = 0;
                if (_refreshAll || (_frameSeq & (StageAuditEvery - 1)) == 0)
                {   // ...and the audit: a missed word bakes a caster at another frame's index count or in the
                    // wrong half of the split, so the full walk still runs -- one frame in 64.
                    _gAudits++;
                    for (int d = 0; d < _gN; d++)
                        if (WriteStagedWords(src, d, _gLive[d])) { _gStaleWords++; changed = true; }
                }
                bool stood = _gN == 0 || CasterGather.Begin(_gN, _gGroups, src, changed);
                _gDrawN = stood ? _gN : 0;
                if (stood) { _submitMasked += _gMasked; StageDynamicSource(); }
                else CasterGather.StandDynamic();
                return stood;
            }
        }
        int n = 0, creatures = 0, maskedN = 0;
        _gCollectSeq = _collectSeq; _gListVer = _listVer; _gN = 0; _gMasked = 0;
        // SL1: the staging is being rebuilt, so every entry's words are about to be correct and the standing
        // changed set is spent. Live indices mean something different after a collect, so the marks go too.
        for (int q = 0; q < _gDirtyN; q++) _gDirtyMark[_gDirty[q]] = false;
        _gDirtyN = 0;
        Array.Fill(_gAt, -1, 0, _liveCount);
        for (int i = 0; i < _liveCount; i++)
        {
            if (AlloyRenderer.CasterVao(_liveVao[i]) == 0) continue;
            // #265: resolve the OWNED name first and put the coverage question to IT -- the mask and colour
            // passes sample the owned copy, which may be a BCn transcode whose block-quantized alpha sits on
            // the other side of the cutout from the engine's raw original.
            // CX-120: a DB caster's diffuse lives in the scene database's own texture registry -- another id
            // space and another coverage memo -- so BOTH questions go there instead (DbCasterMask). Asked
            // through OwnedTextures they came back as the registry slot NUMBER to bind as a GL name and
            // "never scanned" for every texture, which is how 813 arena casters alpha-tested against
            // someone else's texture and the sun cascades baked empty.
            bool db = AlloyRenderer.DbSlot(_liveSlot[i]);
            int dbTex = 0; float dbHoles = 0f, dbGraded = 0f;
            if (db) AlloyRenderer.DbCasterMask(_liveSlot[i], out dbTex, out dbHoles, out dbGraded);
            int drawTex = db ? dbTex : _liveTex[i] != 0 ? OwnedTextures.Draw(_liveTex[i]) : 0;
            // the scan is a glGetTexImage of the owned name in the 4.6 context; /shadow mask skip is the A/B for it
            // #363: a translucent diffuse (glass) has no holes at all; it qualifies on its graded fraction
            // instead and skips the mode-2 hole gate, which would hand it straight back to the solid stage.
            bool glass = drawTex != 0 && _maskDither > 0
                         && (_liveBlend[i] || (db ? dbGraded : TexGraded(drawTex)) >= _maskGraded);
            // CX-227: a declared coverage part is admitted on the DECLARATION, not on what its diffuse
            // measures -- the author already said the alpha is coverage, so no hole or graded fraction has
            // to vouch for it. Carried on the cut kind, which the collect set from the facts.
            // CX-218: and so is a slot-1 coverage part -- its diffuse measures nothing relevant (a DXT1 skin
            // with no alpha at all, or an env reflectivity mask), so a hole fraction could never vouch for it.
            bool cover = drawTex != 0 && (_liveCut[i] == CasterCut.Coverage || _liveCut[i] == CasterCut.CoverNrm);
            int masked = drawTex != 0 && _gl.ProgMask != 0 && _maskMode != 0
                         && (cover || glass || (db ? dbHoles > 0f : TexHasAlpha(drawTex))) ? drawTex : 0;
            if (masked == 0 && _liveTex[i] != 0 && _gl.ProgMask == 0) _maskNoProg++;
            // The mask MODE is decided here, in the key, not at the draw: a masked texture the mode draws
            // solid merges into its (VAO, prim) batch instead of costing one indirect call per view per
            // texture. The knobs bump _listVer so a change re-stages.
            if (masked != 0 && (_maskMode == 0
                                || (_maskMode == 2 && !cover && !glass && (db ? dbHoles : TexHoles(masked)) < _maskHoles))) masked = 0;
            if (masked != 0 && _maskMode < 0) continue;
            // a blended caster the mask stage cannot take (no diffuse, no UVs, mask off) casts nothing, as before #363
            if (_liveBlend[i] && masked == 0) continue;
            if (masked != 0) maskedN++;
            // A record with a resident handle samples its mask through the handle block, so the texture
            // leaves the key. Handle 0 is a BLACK texture (every fragment discards), hence the guard: such a
            // record keeps the bound-texture batch.
            bool hmask = masked != 0 && HandleMaskReady && AlloyRenderer.RecordHandle0(_liveSlot[i]) != 0;
            // CX-218: slot 1 reaches a stage only as a HANDLE, so without the handle stage this caster has no
            // readable coverage source -- and the bound-texture stage would test its DIFFUSE alpha, which on
            // these materials is the env level. It casts solid instead of casting a reflectivity mask.
            if (masked != 0 && !hmask && _liveCut[i] == CasterCut.CoverNrm) { masked = 0; maskedN--; }
            _gLive[n] = i;
            // #302: the cutout threshold is a per-BATCH uniform now, exactly as the colour path spells it
            // (uAlphaCut), so it joins the key rather than riding in a per-draw record. The batch takes the
            // kind of its first entry, so the KIND is a key field: two bits at 60, with masked above it,
            // the (VAO, prim) pair below, the handle flag at 32 and the texture name in the low 32.
            _gKey[n] = ((masked != 0 ? 1L : 0L) << 62) | ((long)_liveCut[i] << 60)
                       | ((long)_liveVao[i] << 44) | ((long)_livePrim[i] << 36)
                       | (hmask ? 1L << 32 : 0L) | (hmask ? 0u : (uint)masked);
            n++;
            if (i < _liveCreatures) creatures++;
        }
        _gDrawN = 0;
        if (n == 0) return true;   // an empty world is a coherent staging: every view bakes a clear
        Array.Sort(_gKey, _gLive, 0, n);
        int g = -1;
        fixed (uint* src = _gSrc)
            for (int d = 0; d < n; d++)
            {
                int i = _gLive[d];
                if (g < 0 || _gKey[d] != _gKey[d - 1])
                {
                    g++;
                    _gStart[g] = d; _gVao[g] = _liveVao[i]; _gPrim[g] = _livePrim[i];
                    _gTex[g] = (int)(_gKey[d] & 0xffffffffL);
                    _gH[g] = (_gKey[d] & (1L << 32)) != 0;
                    _gCut[g] = _liveCut[i];
                    _gDynN[g] = 0;
                }
                _gLen[g] = d - _gStart[g] + 1;
                _gAt[i] = d;
                if (_liveDyn[i]) _gDynN[g]++;
                StageOne(src, d, i, g, _gStart[g]);
            }
        _gGroups = g + 1; _gCreatures = creatures; _gN = n; _gMasked = maskedN;
        bool ok;
        fixed (uint* src = _gSrc)
            ok = CasterGather.Begin(n, _gGroups, src, true);
        if (ok) { _gDrawN = n; _submitMasked += maskedN; StageDynamicSource(); }
        else CasterGather.StandDynamic();
        return ok;
    }

    /// <summary>Bake one view without a per-view walk. The dispatch selects out of this frame's staging
    /// and writes the commands; the CPU issues one indirect-count draw per batch and never learns how many
    /// survived.</summary>
    private static bool GatherSubmit(uint tex, int size, int layer, Matrix4x4* vp,
                                     Vector3 lightDir, Vector3 focus, float reach, float margin, bool point,
                                     float lodR1, float lodR2, int set, bool receiver)
    {
        // PRE-cull figures: the survivors are the GPU's, and asking would be the readback this pass exists
        // to not do -- the latent sample on the period line is what reports them.
        _submitCreatures = _gCreatures;
        if (!BeginTarget(tex, size, layer)) return false;
        CasterGather.Dispatch(focus, lightDir, reach, reach + margin, point, lodR1, lodR2, set, receiver,
                              dynSrc: set == CasterGather.SetDynamic);
        // unit 0 is where the cutout stage samples; nothing else in this bracket moves the active unit
        Gl.ActiveTexture(Gl.TEXTURE0);
        int boundVao = -1, boundMode = -1, boundTex = -1;
        int boundCut = -1;
        // ponytail: every view issues one indirect-count draw per GLOBAL batch. SL2 removes the batches the
        // view's SET provably empties -- the set flag is the CPU's own word, so that much needs no readback --
        // and what is left is the batches whose count the CULL may have left at zero, which the CPU cannot
        // know without the readback this pass exists to not do. Fewer batches (the staging key) is the lever
        // for those.
        int sets = 1 << set;
        for (int g = 0; g < _gGroups; g++)
        {
            if (BatchIdle(g, sets)) { _mdiSkipped++; continue; }
            int cut0 = _gTex[g], mode = _gH[g] ? 2 : cut0 != 0 ? 1 : 0;
            if (mode != boundMode)
            {
                boundMode = mode;
                Gl.UseProgram(mode == 0 ? _gl.Prog : mode == 1 ? _gl.ProgMask : _gl.ProgMaskH);
                Gl.UniformMatrix4fv(mode == 0 ? _gl.LocVp : mode == 1 ? _gl.LocMaskVp : _gl.LocMaskHVp, 1, 0, (float*)vp);
                if (mode == 1) Gl.Uniform1i(_gl.LocMaskTex, 0);
                boundCut = -1;
            }
            if (mode != 0 && (int)_gCut[g] != boundCut)
            {
                CasterCut kind = _gCut[g]; boundCut = (int)kind;
                Gl.Uniform1f(mode == 1 ? _gl.LocMaskCut : _gl.LocMaskHCut, Threshold(kind));
                Gl.Uniform1f(mode == 1 ? _gl.LocMaskMip : _gl.LocMaskHMip, Renderer.CutMip.V);
                Gl.Uniform1i(mode == 1 ? _gl.LocMaskDither : _gl.LocMaskHDither, DitherFor(kind));
                Gl.Uniform1i(mode == 1 ? _gl.LocMaskEnv : _gl.LocMaskHEnv, kind == CasterCut.EnvAlpha ? 1 : 0);
            }
            if (mode == 1 && cut0 != boundTex) { Gl.BindTexture(Gl.TEXTURE_2D, (uint)cut0); boundTex = cut0; }
            if (_gVao[g] != boundVao) { Gl.BindVertexArray(AlloyRenderer.CasterVao(_gVao[g])); boundVao = _gVao[g]; }
            CasterGather.Draw(BatchMode(g), g, _gStart[g], _gLen[g]);
            _mdiCalls++;
        }
        CasterGather.End();
        Gl.BindVertexArray(0);
        Gl.BindFramebuffer(Gl.FRAMEBUFFER, 0);
        return true;
    }

    /// <summary>The period's triangles baked per detail level, per sampled view -- the bake's twin of
    /// <c>MeshLod.Drew</c>, off the gather's own latent count sample. Empty where no view was sampled, so a
    /// driver without the readback entry points loses the figure and nothing else.</summary>
    private static string SampledTriLine()
    {
        long n = CasterGather.Samples + CasterGather.SamplesDyn;
        if (n <= 0) return "";
        long[] t = CasterGather.SampledTris;
        if (t[0] + t[1] + t[2] == 0) return ", no triangles baked in the sampled views";
        return $", baked {t[0] / n}/{t[1] / n}/{t[2] / n} tris per sampled view at detail 0/1/2"
             + (Shadows.Detail.V ? "" : " (per-view detail OFF: /shadow detail 1)");
    }

    /// <summary>Is a caster sphere outside this cube face's 90deg pyramid? <paramref name="t"/> is its depth
    /// along the face forward and <paramref name="dSq"/> its squared distance from the light, so the
    /// perpendicular offset is the third side of that right triangle -- no per-face basis needed, and a cone
    /// cannot clip a corner a mis-rotated square would. Conservative by construction, which SelfCheck pins.</summary>
    private static bool FaceCulls(float dSq, float t, float r)
    {
        if (t < -r) return true;                          // wholly behind the face
        float lim = t * FaceConeSlope + 2f * r;
        return lim < 0f || dSq - t * t > lim * lim;
    }

    /// <summary>The faces of a cube at <paramref name="pos"/> a sphere sits in, as a bit per face in
    /// <c>CubeFaceForward</c> order; 0 when it is out of the light's reach. The same cone test the caster
    /// submission culls by, so a face this says is empty is one the gather would have found empty.</summary>
    private static int FacesOf(Vector3 pos, float far, float cx, float cy, float cz, float r)
    {
        float dx = cx - pos.X, dy = cy - pos.Y, dz = cz - pos.Z;
        float dd = dx * dx + dy * dy + dz * dz, mr = far + r;
        if (dd > mr * mr) return 0;
        int m = 0;
        if (!FaceCulls(dd, dx, r)) m |= 1; if (!FaceCulls(dd, -dx, r)) m |= 2;
        if (!FaceCulls(dd, dy, r)) m |= 4; if (!FaceCulls(dd, -dy, r)) m |= 8;
        if (!FaceCulls(dd, dz, r)) m |= 16; if (!FaceCulls(dd, -dz, r)) m |= 32;
        return m;
    }

    /// <summary>The faces of this light's cube that hold an EVENT this frame -- a mover, a static-set flip, a
    /// ghost -- which is what a dynamic page's content can differ by from last frame. It saturates, because
    /// the movers really are everywhere: nearly every dangly caster of a city reports motion every frame, and
    /// raising that threshold would freeze a swinging banner's shadow. What is cut instead is the COST.</summary>
    private static int EventFaceMask(Vector3 pos, float far)
    {
        int mask = 0;
        for (int q = 0; q < _moverCount && mask != AllFaces; q++)
        { int m = _moverIdx[q]; mask |= FacesOf(pos, far, _liveCx[m], _liveCy[m], _liveCz[m], _liveR[m]); }
        for (int q = 0; q < _sMoverCount && mask != AllFaces; q++)
        { int m = _sMoverIdx[q]; mask |= FacesOf(pos, far, _liveCx[m], _liveCy[m], _liveCz[m], _liveR[m]); }
        for (int q = 0; q < _ghostCount && mask != AllFaces; q++)
            mask |= FacesOf(pos, far, _ghostCx[q], _ghostCy[q], _ghostCz[q], _ghostR[q]);
        return mask;
    }

    /// <summary>Is an event (a mover, a static-set flip, a ghost) inside this cascade's fit?</summary>
    private static bool EventInFit(Vector3 c, Vector3 dir, float radius, float margin)
    {
        for (int q = 0; q < _moverCount; q++)
        { int m = _moverIdx[q]; if (InFit(_liveCx[m] - c.X, _liveCy[m] - c.Y, _liveCz[m] - c.Z, _liveR[m], dir, radius, radius + margin)) return true; }
        for (int q = 0; q < _sMoverCount; q++)
        { int m = _sMoverIdx[q]; if (InFit(_liveCx[m] - c.X, _liveCy[m] - c.Y, _liveCz[m] - c.Z, _liveR[m], dir, radius, radius + margin)) return true; }
        for (int q = 0; q < _ghostCount; q++)
            if (InFit(_ghostCx[q] - c.X, _ghostCy[q] - c.Y, _ghostCz[q] - c.Z, _ghostR[q], dir, radius, radius + margin)) return true;
        return false;
    }
}
