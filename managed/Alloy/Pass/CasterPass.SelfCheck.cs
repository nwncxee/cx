using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy;
using CX.Graphics;

namespace CX.Alloy;

public sealed unsafe partial class CasterPass
{
    /// <summary>Headless check of the contracts that fail SILENTLY -- a wrong SSBO binding or record stride
    /// reads a neighbour's matrix and misplaces geometry rather than erroring, and swapping
    /// <c>gl_BaseInstance</c> for <c>gl_DrawID</c> would look correct today and break the moment M4's culling
    /// compacts the command buffer. False = do not arm, per the house rule.</summary>
    private static bool SelfCheck(ExtenderLog log)
    {
        // ...and the cutout stage really discards, at the engine's own threshold. A masked batch drawn by a
        // stage that lost the test writes the whole quad into the shadow map, which is the artefact the
        // masked path exists to remove and looks exactly like the pass working.
        bool ok = CastMaskFs.Contains("CxAlphaMipAt(t, dFdx(vUV), dFdy(vUV), tsize, 0.0, tmip) <= uAlphaCut) discard;")
                  && CastMaskFs.Contains("uniform float uAlphaCut;")
                  && CastMaskFs.Contains("uniform int uDither;") && CastMaskHFs.Contains("uniform int uDither;")
                  // #363: the stochastic discard, on the SAME sampled alpha, in both mask stages
                  && CastMaskFs.Contains("if (uDither > 0 && cxHash(uvec2(gl_FragCoord.xy) / uDither) > a) discard;")
                  && CastMaskHFs.Contains("if (uDither > 0 && cxHash(uvec2(gl_FragCoord.xy) / uDither) > a) discard;")
                  // The handle-masked stages: the same discard, the mask off the record's handle block at
                  // slot 0, the record index from gl_BaseInstance in both packings.
                  && CastMaskHFs != CastMaskHFsSrc
                  && CastMaskHFs.Contains("CxAlphaMipAt(t, dFdx(vUV), dFdy(vUV), tsize, 0.0, tmip) <= uAlphaCut) discard;")
                  // ...the handle stage samples the record's own diffuse; a content pair whose coverage is its own casts
                  // through the pair's caster twin instead (CasterPass.Content), whose cut is the colour program's
                  && CastMaskHFs.Contains("#define CX_CAST_HANDLE 1")
                  && AlloyRenderer.OwnedLitVs.Contains("#elif defined(CX_CAST)\n    int id = gl_BaseInstance;")
                  && AlloyRenderer.OwnedLitVs.Contains($"int vi = gl_BaseInstance >> {CasterGather.ViewShift};\n    int id = gl_BaseInstance & 0x3ffff;")
                  && !CastMaskFs.Contains("#define CX_CAST_HANDLE")
                  && CastMaskFs.Contains("float t = texture(uTex, vUV).a;")
                  // ...and both dither on the colour pass's blend weight, the record alpha included
                  && CastMaskFs.Contains("float a = (uEnvAlpha != 0 ? 1.0 : t) * vAlpha;")
                  && CastMaskHFs.Contains("float a = (uEnvAlpha != 0 ? 1.0 : t) * vAlpha;")
                  && CastMaskVs.Contains("vAlpha = d.rot.w;") && CastLayerMaskVs.Contains("vAlpha = d.rot.w;")
                  && CastMaskHFs.Contains($"binding = {AlloyRenderer.BindTexH})")
                  && CastMaskHFs.Contains("(vDraw << 3) + 0]")
                  && CastMaskHVs.Contains("vDraw = gl_BaseInstance;")
                  && CastMaskHVs.Contains("CxDraw d = uDraw[gl_BaseInstance];")
                  && CastLayerMaskHVs.Contains("vDraw = gl_BaseInstance & 0x3ffff;")
                  && CastLayerMaskHVs.Contains("uDraw[gl_BaseInstance & 0x3ffff]")
                  && CastVs.Contains("gl_BaseInstance") && !CastVs.Contains("gl_DrawID")
                  // ...and the masked stage indexes its record the same way, for the same M4 reason
                  && CastMaskVs.Contains("gl_BaseInstance") && !CastMaskVs.Contains("gl_DrawID")
                  // #303: BOTH depth stages read the model matrix out of ALLOY's record, at the ownership
                  // slot baseInstance carries -- no shadow-side record block exists at all. A stage that went back to a matrix of its own would bake a world one data
                  // plane out of step with the one being drawn, and nothing would say so.
                  && CastVs.Contains("CxDraw d = uDraw[gl_BaseInstance];")
                  && CastMaskVs.Contains("CxDraw d = uDraw[gl_BaseInstance];")
                  && !CastVs.Contains("c.model") && !CastMaskVs.Contains("c.model")
                  && AlloyRenderer.DrawBufDecl.Contains($"binding = {BindDraw})")
                  // #96 through #302: the bone base is the RECORD's, at the colour path's binding, and the
                  // stage must still treat a negative base as rigid and guard every weighted lane.
                  && AlloyRenderer.BoneVs.Contains($"binding = {BindBones})")
                  && CastVs.Contains("int bb = CxBoneBase(d);")
                  && CastVs.Contains("if (bb >= 0)") && AlloyRenderer.BoneVs.Contains("aBoneWeight.w > 0.0")
                  // #119's standing constraint: the rigid arm is the prepass's clip expression verbatim,
                  // under the invariant. A caster whose depth disagrees with the colour pass's by a bit is
                  // acne no reviewer would trace to a re-spelled multiply.
                  && ShaderContract.DepthSafe(CastVs) && ShaderContract.DepthSafe(CastMaskVs)
                  && ShdCastVs.Length <= ShaderResource.ResRefBytes
                  && ShdCastFs.Length <= ShaderResource.ResRefBytes
                  // #161: the embedded caster stages satisfy the override guards they are checked against --
                  // a guard its own default trips rebuilds from that same default every session, which reads
                  // as the override silently not applying.
                  && CastVsSafe(CastVs) && CastFsSafe(CastFs)
                  // ...and the guards REFUSE each failure they exist for: a record short one vec4 (every
                  // caster bakes at its neighbour's matrix), a vertex stage that lost #96's skinning (every
                  // creature casts its bind pose), a fragment stage that discards (no shadow at all) or
                  // writes gl_FragDepth (acne). All four link cleanly and none says anything.
                  && !CastVsSafe(CastVs.Replace("uDraw[gl_BaseInstance]", "uDraw[0]"))
                  && !CastVsSafe(CastVs.Replace("uBone", "uNothing"))
                  && !CastFsSafe("#version 460 core\nvoid main() { discard; }")
                  && !CastFsSafe("#version 460 core\nvoid main() { gl_FragDepth = 0.5; }")
                  // the receiver's uSunVp array must be exactly as long as the cascade ceiling: a shorter one
                  // reads out of bounds on the outermost cascade, a longer one silently never fills
                  && AlloyLights.SunShadowLib.Contains($"uSunVp[{MaxCascades}]")
                  // same pin on the cube side: uLocalLight is indexed by a slot the CPU hands out, so a
                  // shorter array reads out of bounds the moment the count passes it
                  && AlloyLights.LocalShadowLib.Contains($"uLocalLight[{MaxCubes}]")
                  // #305: the dynamic half. The page array is slot-indexed like uLocalLight; the compose is
                  // min() (depth is a min-of-blockers -- any other operator double-counts or hides one
                  // array); and the page guard reads "none" from the ZERO a program that never sets the
                  // uniform leaves behind, so a stale content override degrades to static-only.
                  && AlloyLights.LocalShadowLib.Contains($"uLocalDynPage[{MaxCubes}]")
                  && AlloyLights.LocalShadowLib.Contains("float v = min(vs, vd);")
                  && AlloyLights.LocalShadowLib.Contains("return mix(1.0, min(vs, vd), uLocalFade[slot]);")
                  && AlloyLights.LocalShadowLib.Contains("vec4 uLocalPcss;")
                  && AlloyLights.LocalShadowLib.Contains("if (src <= 0.0) return uLocalSoft.x;")
                  && AlloyLights.LocalShadowLib.Contains($"uLocalFade[{MaxCubes}]")
                  && AlloyLights.LocalShadowLib.Contains("pg > 0.5")
                  // #306: the sun's half of the split, in the cube's exact shape -- one min() compose, a
                  // per-layer freshness flag that reads "static only" off the zero an unset uniform leaves.
                  && AlloyLights.SunShadowLib.Contains("uniform sampler2DArrayShadow uSunDynShadow;")
                  && AlloyLights.SunShadowLib.Contains("if (uSunDyn[i] > 0.5)")
                  && AlloyLights.SunShadowLib.Contains("return min(vs, vd);")
                  // #307: the layered stages. The pack is pinned on BOTH sides (the dispatch writes
                  // slot | (view << shift), the stage unpacks with the same shift and its mask), the layer
                  // routing exists in both stages, the skinning branch survives, and the clip expression is
                  // still #119's under the invariant. Any of these drifting is a caster baked into the wrong
                  // layer or at the wrong matrix, with nothing in the log.
                  && CastLayerVs.Contains("#extension GL_ARB_shader_viewport_layer_array : require")
                  && CastLayerMaskVs.Contains("#extension GL_ARB_shader_viewport_layer_array : require")
                  && CastLayerVs.Contains("gl_Layer = int(uViewTbl[vi].c.w);")
                  && CastLayerMaskVs.Contains("gl_Layer = int(uViewTbl[vi].c.w);")
                  && CastLayerVs.Contains($"int vi = gl_BaseInstance >> {CasterGather.ViewShift};")
                  && CastLayerMaskVs.Contains($"int vi = gl_BaseInstance >> {CasterGather.ViewShift};")
                  && CastLayerVs.Contains("uDraw[gl_BaseInstance & 0x3ffff]")
                  && CastLayerMaskVs.Contains("uDraw[gl_BaseInstance & 0x3ffff]")
                  && (1 << CasterGather.ViewShift) - 1 == 0x3ffff
                  && (1 << CasterGather.ViewShift) >= AlloyRenderer.MaxOwned
                  && MaxBakeViews <= 1 << (30 - CasterGather.ViewShift)
                  && CastLayerVs.Contains($"binding = {CasterGather.BindView})")
                  && CastLayerMaskVs.Contains($"binding = {CasterGather.BindView})")
                  && CastLayerVs.Contains("int bb = CxBoneBase(d);")
                  // CX-227: the MASKED stages carry the solid one's skinning arm too -- a posed coverage
                  // part is alpha-tested, so a lost branch would punch its holes out of a bind pose and
                  // its shadow would stand where the creature was exported, not where it walks. Spelled
                  // character for character as the solid stages spell it, so the two cannot drift.
                  && CastMaskVs.Contains("int bb = CxBoneBase(d);")
                  && CastLayerMaskVs.Contains("int bb = CxBoneBase(d);")
                  && CastMaskVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * (CxSkinMat(bb) * vec4(aPos, 1.0));")
                  && CastLayerMaskVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * (CxSkinMat(bb) * vec4(aPos, 1.0));")
                  && CastVs.Contains("if (bb >= 0) gl_Position = uVP * d.model * (CxSkinMat(bb) * vec4(aPos, 1.0));")
                  // ...and the three threshold kinds pick the three numbers, each written out by hand: the
                  // layer's own cut for a declared coverage material (so the shadow drops the texels the
                  // draw drops), the env-alpha floor where the diffuse alpha is reflectivity, 0.2 otherwise.
                  && CutFor(coverage: true, envAlpha: false) == CasterCut.Coverage
                  && CutFor(coverage: true, envAlpha: true) == CasterCut.Coverage
                  && CutFor(coverage: false, envAlpha: true) == CasterCut.EnvAlpha
                  && CutFor(coverage: false, envAlpha: false) == CasterCut.Cutout
                  && Threshold(CasterCut.Coverage) == 0.01f
                  && Threshold(CasterCut.EnvAlpha) == 0.02f
                  && Threshold(CasterCut.Cutout) == 0.2f
                  && Threshold(CasterCut.Coverage) == CoverageLayer.Cut
                  // the kind is a 2-bit key field at 60, so the three must fit under it and leave 62 alone
                  && (int)CasterCut.Coverage <= 3
                  // ...and a coverage batch always dithers: at 0 its cut would stamp a near-solid silhouette
                  && DitherFor(CasterCut.Coverage) >= 1
                  // the coverage source is ONE answer for the draw and the caster: the diffuse alpha, except under a
                  // content pair that makes its own, which casts through its own caster twin instead
                  && CoverageLayer.HasCoverageSource((int)ShaderFeature.None)
                  && CoverageLayer.HasCoverageSource((int)ShaderFeature.Skin)
                  && ShaderContract.DepthSafe(CastLayerVs) && ShaderContract.DepthSafe(CastLayerMaskVs)
                  && !CastLayerVs.Contains("discard")
                  && CasterGather.LayerSafe()
                  // #303: the gather dispatch culls the CPU reference's tests over the SHARED CxCullSphere
                  // buffer, conservative in the direction that costs a draw -- a cull that rejects a visible
                  // caster deletes a shadow; one that admits an invisible one costs a draw. GLSL cannot be
                  // run headless, so what is pinned is that every term is present and spelled from these same
                  // constants -- the cone slope is interpolated from FaceConeSlope rather than typed twice,
                  // and the NeverCull exemption gates the reach tests.
                  && CasterGather.SourceSafe();
        // #241: the re-bake predicate, each of its four invalidation sources verified RED on its own -- a
        // term silently dropped from ViewStale is a shadow that stops tracking, which reads as correct until
        // something moves.
        if (ok)
        {   // #282: the sun's staleness volume against the sun's CULL volume. The invariant is one-directional
            // -- nothing the cull would ADMIT may test as outside the fit, or a mover the cascade draws never
            // dirties it and its shadow freezes mid-walk. Up-light of the fit is the case a sphere test gets
            // wrong, so it is probed explicitly.
            Vector3 d = Vector3.Normalize(new Vector3(0.3f, -0.5f, -0.8f));
            bool fit = InFit(0f, 0f, 0f, 0f, d, 10f, 20f)                        // at the focus
                       && InFit(d.X * 19f, d.Y * 19f, d.Z * 19f, 0f, d, 10f, 20f)  // up-light, inside the extent
                       && InFit(-d.X * 19f, -d.Y * 19f, -d.Z * 19f, 0f, d, 10f, 20f)
                       && !InFit(d.X * 30f, d.Y * 30f, d.Z * 30f, 0f, d, 10f, 20f) // ...and past it
                       && InFit(d.X * 30f, d.Y * 30f, d.Z * 30f, 11f, d, 10f, 20f) // ...unless its radius reaches
                       && !InFit(0f, 40f, 0f, 0f, d, 10f, 20f);                    // off the axis entirely
            if (!fit)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the sun's staleness fit rejects a caster its cull admits (frozen cascades)"); }
        }
        if (ok && !(ViewStale(true, false, false, false) && ViewStale(false, true, false, false)
                    && ViewStale(false, false, true, false) && ViewStale(false, false, false, true)
                    && !ViewStale(false, false, false, false)))
        { ok = false; log.Info("CasterPass.SelfCheck FAIL: the view re-bake predicate lost an invalidation source"); }
        if (ok && !(DegradeCounts(false, false, true) && DegradeCounts(false, true, false) && !DegradeCounts(false, false, false)))
        { ok = false; log.Info("CasterPass.SelfCheck FAIL: the sun degrade window counts a walking creature as a stale static half (far cascades stay combined)"); }
        if (ok)
        {   // #302: the vanish detector, which is the ONLY thing standing between a caster that stops casting
            // and the views it stood in keeping its shadow forever. It is a frame STAMP compared against a
            // previous-frame list -- no hash, no bucket, no eviction -- so what it has to get right is that a
            // slot present in both frames does not ghost and a slot present in only the first one does.
            int ghosts0 = _ghostCount; bool inv0 = _invalidate;
            _frameSeq = 7;
            _prevSlots[0] = 3; _prevSlots[1] = 9; _prevCount = 2;
            _liveStamp[3] = 7; _liveStamp[9] = 6;      // slot 3 collected this frame, slot 9 not
            AlloyRenderer.SelfCheckCasterSphere(3, 1f, 2f, 3f, 4f);
            AlloyRenderer.SelfCheckCasterSphere(9, 5f, 6f, 7f, 8f);
            _ghostCount = 0; _invalidate = false;
            VanishSweep();
            bool sweep = _ghostCount == 1 && _ghostCx[0] == 5f && _ghostR[0] == 8f && !_invalidate;
            // ...and one whose stored sphere was never written takes the blanket rather than ghosting a
            // zero-radius sphere that would invalidate nothing.
            AlloyRenderer.SelfCheckCasterSphere(9, 0f, 0f, 0f, 0f);
            _ghostCount = 0;
            VanishSweep();
            sweep &= _ghostCount == 0 && _invalidate;
            _frameSeq = 0; _prevCount = 0; _liveStamp[3] = 0; _liveStamp[9] = 0;
            AlloyRenderer.SelfCheckCasterSphere(3, 0f, 0f, 0f, 0f);
            _ghostCount = ghosts0; _invalidate = inv0;
            if (!sweep)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the vanish sweep misses a caster that stopped casting (frozen shadows)"); }
        }
        if (ok)
        {   // #328: the predicate is ONE function for the full collect and the standing-list refresh. Pin
            // what it must say: a still caster is live but not a mover; one that starts moving is dynamic
            // AND a static-set event; one that keeps moving is dynamic only; an appearance is an event.
            int hold = _dynHold; Shadows.DynHold.V = 0;   // plain knob writes: nothing runs, nothing is written
            _frameSeq = 9; _moverCount = _dMoverCount = _sMoverCount = 0;
            _dynStamp[5] = 0; _dynStamp[6] = 0; _dynStamp[7] = 8;
            Admit(0, 5, skinned: false, appeared: false, recMoved: false, dangly: false, creature: false);
            Admit(1, 6, skinned: false, appeared: false, recMoved: true, dangly: false, creature: false);
            Admit(2, 7, skinned: false, appeared: false, recMoved: true, dangly: false, creature: false);
            Admit(3, 8, skinned: false, appeared: true, recMoved: false, dangly: false, creature: false);
            bool pred = _moverCount == 3 && _moverIdx[0] == 1 && _moverIdx[1] == 2 && _moverIdx[2] == 3
                        && _dMoverCount == 2 && _dMoverIdx[0] == 1 && _dMoverIdx[1] == 2
                        && _sMoverCount == 2 && _sMoverIdx[0] == 1 && _sMoverIdx[1] == 3
                        && !_liveDyn[0] && _liveDyn[1] && _liveStamp[8] == 9;
            // #336: the hold. At 4 frames a caster last moved 3 frames ago is still dynamic and no event;
            // one that last moved 5 frames ago flips back -- once -- and is a static event; a flip that would
            // repeat every frame under vertex-dirty flapping is the churn this exists to stop.
            Shadows.DynHold.V = 4; _moverCount = _dMoverCount = _sMoverCount = 0;
            _dynStamp[5] = 6; _dynStamp[6] = 4;
            Admit(0, 5, skinned: false, appeared: false, recMoved: false, dangly: false, creature: false);
            Admit(1, 6, skinned: false, appeared: false, recMoved: false, dangly: false, creature: false);
            pred &= _moverCount == 0 && _dMoverCount == 1 && _dMoverIdx[0] == 0 && _sMoverCount == 1 && _sMoverIdx[0] == 1
                    && _liveDyn[0] && !_liveDyn[1];
            Shadows.DynHold.V = hold;
            _frameSeq = 0; _moverCount = _dMoverCount = _sMoverCount = 0; _mvMatrix = 0; _mvAppear = 0;
            for (int q = 5; q <= 8; q++) { _dynStamp[q] = 0; _liveStamp[q] = 0; }
            _liveDyn[0] = _liveDyn[1] = _liveDyn[2] = false;
            if (!pred)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the mover predicate misclassifies a caster (a shadow that stops tracking, or a static page that never re-bakes)"); }
        }
        if (ok)
        {   // #336: the refresh's visit set. Three still casters; slot 21's record changed
            // in the colour build, slot 22 was dynamic last frame, slot 20 nothing named -- so exactly two are
            // visited and the third keeps its verdict, and the refreshall A/B visits all three.
            // Off-device every ownership slot reads skinned (an unenrolled bone slot is 0), so the three
            // sit in the creature half; the visit set is the same question either way.
            _frameSeq = 12; _collectSeq = 3; _liveCount = 3; _liveCreatures = 3;
            for (int q = 0; q < 3; q++)
            { _liveSlot[q] = 20 + q; _liveVao[q] = 0; _liveIdx[20 + q] = q; _liveIdxSeq[20 + q] = 3; _liveVisit[q] = 0; _liveDyn[q] = false; _liveOff[q] = false; _liveCreature[q] = true; AlloyRenderer.SelfCheckCastOk(20 + q, true); }
            AlloyRenderer.ResetCasterTouched(); AlloyRenderer.NoteCasterTouched(21);
            _dPrevIdx[0] = 2; _dPrevCount = 1; _dynStamp[22] = 11;
            long visits = _visitSum, touched = _touchedSum; _visitSum = 0;
            bool part = Refresh() && _visitSum == 2 && _liveVisit[0] != 12 && _liveVisit[1] == 12 && _liveVisit[2] == 12
                        && !_liveDyn[0] && _moverCount == 2;   // two visited, both skinned = both movers
            Shadows.RefreshAll.V = true; _frameSeq = 13; _visitSum = 0;
            bool all = Refresh() && _visitSum == 3;
            // A flip off is a ghost and a zero-count entry, not a re-collect; a flip back on is an appearance.
            AlloyRenderer.SelfCheckCastOk(21, false); _frameSeq = 14; _ghostCount = 0;
            bool off = Refresh() && _liveOff[1] && _ghostCount == 1 && _moverCount == 2 && InList(20) && InList(21);
            AlloyRenderer.SelfCheckCastOk(21, true); _frameSeq = 15;
            bool on = Refresh() && !_liveOff[1] && _moverCount == 3;
            // ...and an off-flip outside the stage (a freed slot) reaches the list through the off queue.
            // ...queued as the LIVE index: the slot is re-pointed at a new entry before the refresh runs, and
            // it is the old entry that must go off.
            AlloyRenderer.SelfCheckCastOk(20, false); NoteSlotOff(20); _liveIdx[20] = 2; _frameSeq = 16; Shadows.RefreshAll.V = false;
            bool queued = Refresh() && _liveOff[0] && !_liveOff[2] && _offN == 0;
            _liveIdx[20] = 0;
            Shadows.RefreshAll.V = true;
            all &= off && on && queued;
            _touchedSum = touched;
            Shadows.RefreshAll.V = false; _frameSeq = 0; _collectSeq = 0; _liveCount = 0; _liveCreatures = 0; _collectedAt = -1;
            _dPrevCount = 0; _moverCount = _dMoverCount = _sMoverCount = 0; _mvMatrix = 0; _mvBone = 0; _liveSum = 0; _moverSum = 0; _degenSum = 0;
            _visitSum = visits;
            for (int q = 0; q < 3; q++) { _dynStamp[20 + q] = 0; _liveStamp[20 + q] = 0; _liveDyn[q] = false; _liveVisit[q] = 0; _liveIdxSeq[20 + q] = 0; _liveOff[q] = false; _liveCreature[q] = false; AlloyRenderer.SelfCheckCastOk(20 + q, false); }
            _ghostCount = 0;
            AlloyRenderer.ResetCasterTouched();
            if (!part || !all)
            { ok = false; log.Info($"CasterPass.SelfCheck FAIL: the refresh visit set is wrong (partial {part}, all {all}) -- a caster's motion goes unseen, or the walk is O(world) again"); }
        }
        if (ok)
        {   // #286's staging, against what the dispatch and the draw stages read back out of it. Not a GL
            // call in sight: this is a memory layout shared with GLSL, and a field one slot over is a caster
            // baking at its neighbour's geometry.
            // #303: the source is an index range, the OWNERSHIP slot (word 3 -- the sphere the dispatch
            // culls and the baseInstance the depth stages read uDraw through) and the batch pair. A slot off
            // by one is a caster baking at another part's matrix, with nothing in the log.
            _liveCreatures = 1; _liveCount = 2;
            for (int i = 0; i < 2; i++)
            {
                _liveIdxCount[i] = 30 + i; _liveFirst[i] = (uint)(100 + i); _liveBaseVert[i] = (uint)(7 + i);
                _liveCut[i] = CasterCut.Cutout; _liveSlot[i] = 4100 + i;
                _liveDyn[i] = i == 1;   // #305: the set flag, one of each, so a swap fails
            }
            CasterSrcRec* recs = stackalloc CasterSrcRec[2];
            uint* src = (uint*)recs;
            // CX-50: the WHOLE record, every word of it. Filled with a sentinel first, because stackalloc is
            // not zeroed and the staging array is not either: a word StageOne leaves alone is whatever the
            // last staging left there, which for a level's (count, first) is another mesh's geometry.
            for (int w = 0; w < 2 * CasterGather.SrcUints; w++) src[w] = 2989u;
            StageOne(recs, 0, 0, 0, 0); StageOne(recs, 1, 1, 1, 1);
            bool whole = sizeof(CasterSrcRec) == 11 * sizeof(uint);
            for (int w = 0; w < 2 * CasterGather.SrcUints; w++) whole &= src[w] != 2989u;
            StageOne(recs, 0, 0, 0, 0);
            _liveOff[1] = true; StageOne(recs, 1, 1, 1, 1);
            bool off = src[CasterGather.SrcUints] == 0;
            _liveOff[1] = false; StageOne(recs, 1, 1, 1, 1);
            bool pack = src[0] == 30 && src[1] == 100 && src[2] == 7 && src[3] == 4100 && src[4] == 0
                        && src[5] == 0 && src[6] == 0
                        && src[CasterGather.SrcUints] == 31
                        && src[CasterGather.SrcUints + 3] == 4101 && src[CasterGather.SrcUints + 4] == 1
                        && src[CasterGather.SrcUints + 5] == 1 && src[CasterGather.SrcUints + CasterGather.SetFlagWord] == 1
                        && off && whole
                        // CX-50: a caster with no coarse run of its own stages zeros, which is what makes
                        // every view's pick fall back to its base run rather than to a stale pair.
                        && src[CasterGather.Cnt1Word] == 0 && src[CasterGather.First1Word] == 0
                        && src[CasterGather.Cnt2Word] == 0 && src[CasterGather.First2Word] == 0;
            // SL1/SL2: the per-frame word rewrite, and the per-batch dynamic population it maintains. The
            // rewrite is what a frame does INSTEAD of re-reading all 37k staged draws, and the population is
            // what lets a view's set skip a batch outright -- both silent when wrong (a caster at another
            // frame's index count, a mover whose batch is never issued into its own page).
            _liveCreatures = 0; _liveCount = 2;
            _gLen[0] = 1; _gLen[1] = 1; _gDynN[0] = 0; _gDynN[1] = 1;
            _liveOff[0] = false; _liveOff[1] = false;
            _liveDyn[0] = false; _liveDyn[1] = true;
            src[4] = 0; src[CasterGather.SrcUints + 4] = 1;              // each entry its own batch
            bool words = !WriteStagedWords(recs, 0, 0) && !WriteStagedWords(recs, 1, 1);   // already in step
            _liveDyn[0] = true;                                         // a still caster starts moving
            words &= WriteStagedWords(recs, 0, 0) && src[CasterGather.SetFlagWord] == 1 && _gDynN[0] == 1;
            _liveOff[0] = true;
            words &= WriteStagedWords(recs, 0, 0) && src[0] == 0 && _gDynN[0] == 1;   // off, still dynamic
            _liveDyn[0] = false; _liveOff[0] = false;
            words &= WriteStagedWords(recs, 0, 0) && src[0] == 30 && _gDynN[0] == 0;
            // ...and the skip: a batch with no dynamic caster is nothing to a dynamic view, a batch with
            // nothing else is nothing to a static view, and a mixed chunk skips neither.
            // batch 0 holds the still caster, batch 1 the mover.
            bool idle = BatchIdle(0, 1 << CasterGather.SetDynamic) && !BatchIdle(1, 1 << CasterGather.SetDynamic)
                        && !BatchIdle(0, 1 << CasterGather.SetStatic) && BatchIdle(1, 1 << CasterGather.SetStatic)
                        && !BatchIdle(0, 1 << CasterGather.SetAll) && !BatchIdle(1, 1 << CasterGather.SetAll)
                        && !BatchIdle(1, (1 << CasterGather.SetDynamic) | (1 << CasterGather.SetStatic));
            _gLen[0] = 0; _gLen[1] = 0; _gDynN[0] = 0; _gDynN[1] = 0;
            _liveCreatures = 0; _liveCount = 0; _liveDyn[0] = false; _liveDyn[1] = false;
            if (!pack)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the compute gather's staging drifted from the source the dispatch reads"); }
            if (!words)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the per-frame staged-word rewrite does not track the live entry"); }
            if (!idle)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: a batch the view's set empties is not the one the skip names"); }

            // CX-50: the per-level bake tally's readout, over hand-set counts against a hand-written line.
            // The figure is per SAMPLED VIEW, so the period's totals divide by the samples that produced
            // them -- a tally that stopped dividing reads as a bake that grew with the period length.
            long[] tris = CasterGather.SampledTris;
            long kTris0 = tris[0], kTris1 = tris[1], kTris2 = tris[2];
            long kSamples = CasterGather.Samples, kSamplesDyn = CasterGather.SamplesDyn;
            tris[0] = 300; tris[1] = 200; tris[2] = 100;
            CasterGather.Samples = 1; CasterGather.SamplesDyn = 1;
            string tline = SampledTriLine();
            tris[0] = 0; tris[1] = 0; tris[2] = 0;
            string tzero = SampledTriLine();
            CasterGather.Samples = 0; CasterGather.SamplesDyn = 0;
            string tnone = SampledTriLine();
            tris[0] = kTris0; tris[1] = kTris1; tris[2] = kTris2;
            CasterGather.Samples = kSamples; CasterGather.SamplesDyn = kSamplesDyn;
            if (tline != ", baked 150/100/50 tris per sampled view at detail 0/1/2"
                || tzero != ", no triangles baked in the sampled views" || tnone.Length != 0)
            { ok = false; log.Info($"CasterPass.SelfCheck FAIL: the per-level bake tally reads '{tline}'"); }

            // S8: the dynamic source is a VERBATIM copy of the staged movers -- same batch id, same output
            // base, same index range -- because that is the whole argument for dispatching over it: a survivor
            // lands in the region and the batch it would have landed in from the full source. A word of drift
            // here is a mover's shadow baked at another caster's geometry, and only its own page shows it.
            _liveCount = 2; _gN = 2;
            _liveDyn[0] = false; _liveDyn[1] = true;
            _liveOff[0] = false; _liveOff[1] = false;
            src[CasterGather.SetFlagWord] = 0u; src[CasterGather.SrcUints + CasterGather.SetFlagWord] = 1u;
            _gAt[0] = 0; _gAt[1] = 1;
            _dMoverIdx[0] = 1; _dMoverCount = 1;
            CasterSrcRec* dynRecs = stackalloc CasterSrcRec[2];
            uint* dyn = (uint*)dynRecs;
            for (int i = 0; i < 2 * CasterGather.SrcUints; i++) dyn[i] = 0xdeadu;
            int dm = CopyDynamicSource(recs, dynRecs);
            bool copy = dm == 1;
            for (int w = 0; w < CasterGather.SrcUints; w++)
                copy &= dyn[w] == src[CasterGather.SrcUints + w];   // the MOVER's record, word for word
            long missWas = _dSrcMiss;
            AuditDynamicSource(recs, dynRecs, dm);
            copy &= _dSrcMiss == missWas;   // one staged mover, one copied: no miss
            // ...and both directions of the audit actually fire. Missing: the copy drops the mover. Extra: it
            // takes a still caster, which the shader's set filter would reject but which means the copy walked
            // the wrong set -- the direction "would draw" on both sides cannot see.
            AuditDynamicSource(recs, dynRecs, 0);
            copy &= _dSrcMiss == missWas + 1;
            dyn[CasterGather.SetFlagWord] = 0u;
            AuditDynamicSource(recs, dynRecs, dm);
            copy &= _dSrcMiss == missWas + 3 && _dSrcExtra == 1;   // one for want != dyn, one for the extra
            dyn[CasterGather.SetFlagWord] = 1u;
            _dSrcExtra = 0;
            // ...and a mover the staging does not hold is skipped rather than copied as slot -1.
            _gAt[1] = -1;
            copy &= CopyDynamicSource(recs, dynRecs) == 0;
            _gAt[0] = 0; _gAt[1] = 0; _dMoverCount = 0; _gN = 0; _liveCount = 0;
            _liveDyn[1] = false;
            _dSrcMiss = missWas; _dSrcAudits = 0;
            if (!copy)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the dynamic source is not a verbatim copy of the staged movers"); }
            // The rotation's write-after-read argument holds only above a floor of 2: at 1 no barrier stands
            // between a view's indirect reads and the next view's dispatch rewriting the same pair.
            if (CasterGather.Rotation < 2)
            { ok = false; log.Info("CasterPass.SelfCheck FAIL: the gather's buffer rotation is below 2, so a view's commands can be rewritten while its draws still source them"); }
        }
        // #100's face cull, brute-forced over the +X face (forward = +X, so t = dx and the pyramid is
        // |dy| <= dx, |dz| <= dx). The invariant that matters is one-directional: NOTHING inside the pyramid
        // may be culled. Admitting a little extra is the deliberate cost of a rotation-invariant test.
        for (int a = 1; a <= 12 && ok; a++)
            for (int b = -12; b <= 12 && ok; b++)
                for (int c = -12; c <= 12 && ok; c++)
                {
                    float dx = a, dy = b * a / 12f, dz = c * a / 12f;   // strictly inside: |dy|,|dz| <= dx
                    if (!FaceCulls(dx * dx + dy * dy + dz * dz, dx, 0f)) continue;
                    ok = false;
                    log.Info($"CasterPass.SelfCheck FAIL: face cull rejects ({dx},{dy},{dz}), inside the 90deg face");
                }
        // and it must still reject the obvious misses, else it is culling nothing at all
        if (ok && (!FaceCulls(100f, -10f, 0f) || !FaceCulls(101f, 1f, 0f)))
        { ok = false; log.Info("CasterPass.SelfCheck FAIL: face cull admits geometry behind or beside the face"); }
        // #336: the per-face event mask. Out of reach = no face; straight down +X = face 0 alone; a sphere
        // around the light = every face. A mask that drops a face is a mover shadow that never updates.
        if (ok && (FacesOf(Vector3.Zero, 10f, 20f, 0f, 0f, 1f) != 0 || FacesOf(Vector3.Zero, 10f, 5f, 0f, 0f, 0.1f) != 1
                   || FacesOf(Vector3.Zero, 10f, 0f, -5f, 0f, 0.1f) != 8 || FacesOf(Vector3.Zero, 10f, 0f, 0f, 0f, 1f) != AllFaces))
        { ok = false; log.Info("CasterPass.SelfCheck FAIL: the per-face event mask names the wrong faces"); }
        // the hot path's mover spheres: six movers folded into DynSpheres, every mover inside one of them. A
        // mover outside every sphere is a creature shadow missing from the baked ground.
        if (ok)
        {
            const int movers = DynSpheres + 6;
            const float big = 12f;   // the one mover the tightness bound below is measured against
            MoverBox* box = stackalloc MoverBox[DynSpheres];
            Vector4* mv = stackalloc Vector4[movers];
            for (int i = 0; i < movers; i++)
            {   // a ring of creatures round the light, and a 12 m mover among them
                mv[i] = new Vector4(25f * MathF.Cos(i * 0.9f), 25f * MathF.Sin(i * 0.9f), i % 3, i == 5 ? big : 0.5f + (i % 4) * 0.25f);
            }
            int n = 0;
            for (int i = 0; i < movers; i++) n = AddMoverBox(box, n, mv[i].X, mv[i].Y, mv[i].Z, mv[i].W);
            WriteDynSpheres(0, n, box, Vector3.Zero, 40f);
            bool held = n == DynSpheres && CubeDynSphere[(DynSpheres - 1) * 4 + 3] >= 0f;
            for (int i = 0; i < movers && held; i++)
            {
                bool inOne = false;
                for (int s = 0; s < DynSpheres && !inOne; s++)
                {
                    float sx = CubeDynSphere[s * 4], sy = CubeDynSphere[s * 4 + 1], sz = CubeDynSphere[s * 4 + 2], sr = CubeDynSphere[s * 4 + 3];
                    float dx = mv[i].X - sx, dy = mv[i].Y - sy, dz = mv[i].Z - sz;
                    inOne = sr >= 0f && MathF.Sqrt(dx * dx + dy * dy + dz * dz) + mv[i].W <= sr + 1e-3f;
                }
                held = inOne;
            }
            // ...and the fold is a fold, not a union: no sphere outgrows the 12 m mover's own (a fold into one box
            // reads 41,5 m here, a single sphere per slot 40)
            for (int s = 0; s < DynSpheres && held; s++) held = CubeDynSphere[s * 4 + 3] <= big * MathF.Sqrt(3f) * 1.001f + HotPad(40f);
            // a lone mover publishes its own sphere, not its box's corner sphere
            n = AddMoverBox(box, 0, 3f, 4f, 1f, 0.5f);
            WriteDynSpheres(0, n, box, Vector3.Zero, 40f);
            held &= MathF.Abs(CubeDynSphere[3] - 0.5f - HotPad(MathF.Sqrt(26f))) < 1e-5f && CubeDynSphere[4 + 3] < 0f;
            // the pad is the shadow read's own reach: an angle, so a mover twice as far from its light gets twice the share
            held &= MathF.Abs(HotPad(10f) - HotPad(0f) - 2f * (HotPad(5f) - HotPad(0f))) < 1e-5f && HotPad(0f) > 0f
                    && ShaderSource.Get("cx_localshadow.glsl").Contains("const float CX_LOCAL_NORMAL_OFFSET = 1.0;")
                    && ShaderSource.Get("cx_localshadow.glsl").Contains("const float CX_LOCAL_SLOPE_OFFSET = 0.75;")
                    && ShaderSource.Get("cx_localshadow.glsl").Contains("const float CX_LOCAL_SLOPE_MAX = 2.0;");
            WriteDynSpheres(0, 0, box, Vector3.Zero, 40f);
            if (!held) { ok = false; log.Info("CasterPass.SelfCheck FAIL: a mover falls outside every hot-path sphere, or the fold swallows the square"); }
        }

        // CX-49: run unconditionally and last -- it names the pin that drifted, which the && chain above
        // would swallow.
        ok &= PcssCheck(log);

        log.Info(ok
            ? "CasterPass.SelfCheck: passed"
            : $"CasterPass.SelfCheck: FAILED -- indirect/per-draw contract drifted (command {CmdBytes}B, " +
              "gl_BaseInstance-as-ownership-slot indexing); standing down");
        return ok;
    }
}
