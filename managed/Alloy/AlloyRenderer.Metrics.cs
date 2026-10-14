using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>What the period measured and how: the GPU query objects and the per-pass CPU partition that fill the
/// counters, and the one period line that prints every tally and resets it.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The fragments the COLOUR pass actually shaded. samples/screen-pixel is the overdraw figure and
    /// its move with <c>/alloy prepass</c> is what the prepass is worth: on a vsync-capped display frame time
    /// cannot answer that, because both modes sit on the cap and the saving becomes headroom.</summary>
    private static GpuQuery _qSamples;
    /// <summary>SAMPLES_PASSED over the particle pass alone -- its overdraw against the viewport.</summary>
    private static GpuQuery _qPartSamples;
    /// <summary>Samples the blended class's OIT accumulation wrote -- its overdraw, beside its GPU term.</summary>
    private static GpuQuery _qBlendSamples;
    /// <summary>The blended accumulation's triangles (PRIMITIVES_GENERATED) and its draw groups, per frame, for the period line.</summary>
    private static GpuQuery _qBlendPrims;
    /// <summary>The accumulation's fragment-shader invocations: oit 2 tests depth in the shader, so SAMPLES_PASSED
    /// counts only the survivors and this is what the pass actually shades.</summary>
    private static GpuQuery _qBlendFrags;
    private static long _blendGroups;
    private static int _blendFrames;
    /// <summary>The timed GPU terms of the colour bracket, in pass order. Beams and the probe capture are
    /// untimed; the shadow bracket and the light build run their own elapsed queries OUTSIDE every term by
    /// pass order, which is the premise the guard rests on. The <c>*Key</c> terms are the keyhole-dissolving
    /// tails, and the deferred pass is three dispatches timed apart, so each knob behind them has a number.</summary>
    internal enum GpuTerm { Prepass, PrepassKey, PrepassFeat, PrepassRetest, Ao, Opaque, OpaqueKey, Cutout, CutoutKey, Grass, GrassDepth, Sky, Blended, BlendedComposite, BlendedHosted, BlendedDepth, Water, Additive, Particles, Motion, Post, Cull, HiZ, Beams, WaterCopy, Weather, Ssr, Deferred, DeferredShadow, PairDebt, Coverage, Count }
    /// <summary>One elapsed query per term. TIME_ELAPSED cannot nest: a colour timer wrapping the grass and
    /// water ones has its inner End close it early, wedging both inner meters on a stale sample.</summary>
    private static readonly GpuQuery[] _qTerm = new GpuQuery[(int)GpuTerm.Count];
    /// <summary>The term whose query is open, -1 for none. Closed at frame start if a pass left it open.</summary>
    private static int _qOpen = -1;
    /// <summary>Pixels per world radius at unit depth for THIS pass (0 without perspective), kept for the cube
    /// receiver's disc gate.</summary>
    private static float _lodScale;

    /// <summary>Open a term's elapsed query unless another is already open -- the guard that keeps the
    /// brackets serial. A term whose query declined the frame (result not ready) is simply not measured.</summary>
    /// <summary>Every term's period average summed, shadows excluded (the bake has its own query).</summary>
    private static double GpuTermsTotal()
    { double s = 0; for (int t = 0; t < (int)GpuTerm.Count; t++) s += GpuMs((GpuTerm)t); return s; }

    /// <summary>Is a timed term's query open? TIME_ELAPSED cannot nest, so a second timer waits.</summary>
    internal static bool GpuTermOpen => _qOpen >= 0;

    internal static void GpuBegin(GpuTerm t)
    { if (_qOpen < 0 && _qTerm[(int)t].Begin(Gl.TIME_ELAPSED)) _qOpen = (int)t; }

    /// <summary>Frame start: a term still open from the last frame (an early return between its Begin and
    /// End) is closed here, or every later Begin is refused and the line reads zero for the session.</summary>
    private static void GpuFrameStart()
    {
        if (_qOpen >= 0) GpuEnd((GpuTerm)_qOpen);
        GpuSpanStamp(0);
    }

    /// <summary>Whole-bracket GPU span: TIMESTAMP at frame start and after the pass loop, two frames in flight.
    /// The terms are serial elapsed queries and only cover what is bracketed; this is the number they are
    /// measured against, so the unmeasured remainder has a size.</summary>
    private static readonly uint[] _qSpan = new uint[4];
    private static readonly bool[] _qSpanPending = new bool[2];
    private static int _qSpanAt;
    private static double _spanSum; private static int _spanN;
    internal static double GpuSpanMs => _spanN == 0 ? 0.0 : _spanSum / _spanN / 1e6;

    private static void GpuSpanStamp(int which)
    {
        if (!GpuQuery.Enabled || Gl.QueryCounter == null || Gl.GenQueries == null || Gl.GetQueryObjectui64v == null) return;
        if (_qSpan[0] == 0) fixed (uint* q = _qSpan) Gl.GenQueries(4, q);
        if (which == 0)
        {
            // Both slots, not just the one closed last: with the GPU two frames behind, both are pending, and
            // harvesting only the newer one wedged the meter at 0 for the session.
            for (int s = 0; s < 2; s++)
            {
                if (!_qSpanPending[s]) continue;
                int ready; Gl.GetQueryObjectiv(_qSpan[s * 2 + 1], Gl.QUERY_RESULT_AVAILABLE, &ready);
                if (ready == 0) continue;
                ulong t0, t1;
                Gl.GetQueryObjectui64v(_qSpan[s * 2], Gl.QUERY_RESULT, &t0);
                Gl.GetQueryObjectui64v(_qSpan[s * 2 + 1], Gl.QUERY_RESULT, &t1);
                _qSpanPending[s] = false;
                if (t1 > t0 && t1 - t0 < 10_000_000_000UL) { _spanSum += t1 - t0; _spanN++; }
            }
            if (_qSpanPending[_qSpanAt]) return;
            Gl.QueryCounter(_qSpan[_qSpanAt * 2], Gl.TIMESTAMP);
        }
        else if (!_qSpanPending[_qSpanAt])
        {
            Gl.QueryCounter(_qSpan[_qSpanAt * 2 + 1], Gl.TIMESTAMP);
            _qSpanPending[_qSpanAt] = true;
            _qSpanAt = 1 - _qSpanAt;
        }
    }
    internal static void GpuSpanEnd() => GpuSpanStamp(1);

    /// <summary>Close the term if it is the open one; a no-op for a term that never opened.</summary>
    internal static void GpuEnd(GpuTerm t)
    { if (_qOpen == (int)t) { _qTerm[(int)t].End(Gl.TIME_ELAPSED); _qOpen = -1; } }

    private static double GpuMs(GpuTerm t) => _qTerm[(int)t].Avg / 1e6;

    /// <summary>The colour-bracket loop's SELF CPU ticks per <see cref="PassId"/> this period -- the five
    /// child zones <see cref="FrameZones.Zone.Passes"/> subtracts come off each row too, so the rows partition
    /// that zone rather than double-counting Issue, Cull, Post, Probe and Lightmap inside it. The rows plus
    /// <see cref="_passTail"/> account for the zone bar the <c>foreach</c>'s own unbracketed advance.</summary>
    private static readonly long[] _passCpu = new long[(int)PassId.PostStack + 1];
    /// <summary>The colour loop's tail (<c>PaintStreamFailures</c> + the GPU span close): inside the Passes
    /// bracket and inside no pass, so it is reported rather than left as an unexplained remainder.</summary>
    private static long _passTail;
    /// <summary>Rows that measured NEGATIVE self time, which can only mean a child zone was credited to a pass
    /// it did not run inside. Must read 0.</summary>
    private static int _passSelfNeg;

    /// <summary>The five passes with the most SELF CPU per frame this period, clearing the tally. These five
    /// are a partition of <see cref="FrameZones.Zone.Passes"/>, not a parent-and-child mix. Cold (period
    /// line).</summary>
    private static string PassCpuNote(int frames)
    {
        var sb = new System.Text.StringBuilder(", pass cpu self ms/frame:");
        double k = 1000.0 / System.Diagnostics.Stopwatch.Frequency / Math.Max(1, frames);
        for (int n = 0; n < 5; n++)
        {
            int best = -1;
            for (int i = 0; i < _passCpu.Length; i++) if (_passCpu[i] > 0 && (best < 0 || _passCpu[i] > _passCpu[best])) best = i;
            if (best < 0) break;
            sb.Append(' ').Append((PassId)best).Append(' ').Append((_passCpu[best] * k).ToString("F2"));
            _passCpu[best] = 0;
        }
        Array.Clear(_passCpu);
        if (_passTail > 0) sb.Append(" tail ").Append((_passTail * k).ToString("F2"));
        if (_passSelfNeg > 0) sb.Append(" (").Append(_passSelfNeg).Append(" NEGATIVE SELF ROWS -- the partition is broken)");
        _passTail = 0; _passSelfNeg = 0;
        return sb.ToString();
    }

    /// <summary>The [alloy] period line: every number the period tallied, in one statement, in its own method
    /// so <see cref="EndFrame"/> reads as the gate, the census and the reset it is. Pure reporting: a closed log
    /// returns before any of it is built.</summary>
    private static void PeriodReport()
    {
        if (!ExtenderLog.Enabled) return;
        // distinct textures is the BATCH FLOOR: one program + one VDO cannot batch below one call per texture,
        // so batches/frame ~= textures tells you the grouping is optimal and the ratio is scene-bound, while
        // batches >> textures says the sort is failing. Without it the ratio alone cannot distinguish the two.
        // #120 made the floor a SOFT one: slots 1..5 joined the batch key but not the radix digit (which still
        // sorts on the diffuse alone), so two materials sharing a diffuse and differing in their normal map
        // now cut a batch the count does not predict. A small excess over the floor is that, not a regression.
        // #264: the gap between these two is a HOLE now, not a ratio. Every candidate the enrollment turned
        // away used to fall through to the engine's walk and draw vanilla; there is no walk, so it is simply
        // not drawn. Named in the line rather than left to subtraction -- "6944/7144" reads as coverage, and
        // "200 NOT DRAWN" reads as the missing geometry it actually is. The reject breakdown at the end of
        // this line is what it decomposes into.
        int holes = Counters._lastCandidates - Counters._lastEnrolled;
        // #325: the old single colour timer's span, opaque through additive (water = copy + draw).
        double colourMs = 0; for (int t = (int)GpuTerm.Opaque; t <= (int)GpuTerm.Additive; t++) colourMs += GpuMs((GpuTerm)t);
        _log?.Info($"AlloyRenderer: [alloy] {DbFeed.Stats()} | {DbTableNote()} | {DbPassNote()} "
                     + $"| {DbInputs.Stats()} | {DbGui.Stats()}");
        _log?.Info($"AlloyRenderer: [alloy] {Counters._lastEnrolled}/{Counters._lastCandidates} parts owned{CapacityNote()}" +
                   (holes <= 0 ? "" : $" ({holes} NOT DRAWN)") + " [" + OwnedTextures.TranscodeNote() + "; " + OwnedTextures.CacheNote() + "; " + OwnedTextures.PrefetchNote() + TexStream.Note() + TexLoad.Note() + ModelLoad.Note() + "; " + DdsReader.Note() + "; " + RasterReader.Note() + "]" +
                   $" drawn by {OwnedClasses()} " +
                   $"/ {Counters._lastTextures} textures " +
                   $"({_litCount} lit, {Counters._litUnownable} lit left to the engine" +
                   // #128: what the creature class costs, split. "Creatures are still vanilla" has three
                   // different fixes behind it -- a skinning stage, an instance-state term, or nothing at all
                   // if the sweep is simply doing its job -- and only the split says which.
                   // #137: and what the skinmesh class itself did. A zero everywhere with the class armed
                   // means no cloak was on screen, which is a different statement from "the pose failed" --
                   // so the failures are named, not merely absent.
                   (!_skinning ? ", skinmeshes not owned"
                      : Counters._skinDrawn == 0 && Counters._skinNoPose == 0 ? ""
                      : $", {(float)Counters._skinDrawn / Counters._frames:F1} skinmeshes posed/frame" +
                        (Counters._skinNoPose == 0 ? "" : $", {Counters._skinNoPose} drawn rigid (no pose yet)") +
                        $", {Counters._boneHigh} bone slots peak of {_boneCap}") +
                   // #284: session totals, so a creature that is STILL a hole three periods later is
                   // visible as one rather than being reset out of the line every period.
                   "), " +
                   $"Alloy draws {(float)Counters._draws / Counters._frames:F0}/frame in {(float)Counters._batches / Counters._frames:F0} batches " +
                   // #126: the transparency model, as numbers. Cutout should dwarf blended on NWN content;
                   // a blended count that reads 0 while the scene plainly has transparents means the class
                   // was dropped whole rather than never classified -- which the next term names.
                   $"({(float)Counters._drawsByClass[1] / Counters._frames:F0} cutout, {(float)Counters._drawsByClass[2] / Counters._frames:F0} blended, " +
                   // #129: indexed off the enum, not off literals -- inserting Water at 3 renumbered
                   // Additive, and the line went on labelling water draws "additive" while additive itself
                   // stopped being printed at all. This line is the acceptance instrument; it must not lie.
                   $"{(float)Counters._drawsByClass[(int)AlloyClass.Water] / Counters._frames:F0} water, " +
                   $"{(float)Counters._drawsByClass[(int)AlloyClass.Additive] / Counters._frames:F0} additive" +
                   (Counters._blendedDropped == 0 ? "" : $"; {Counters._blendedDropped} BLENDED PARTS HANDED BACK -- the class " +
                    "could not be Alloy whole, so none of it is") + ") " +
                   $"({(Counters._batches == 0 ? 0f : (float)Counters._draws / Counters._batches):F1} draws/batch), " +
                   // #292: the face-culling term's own cost. A two-sided count of 0 with a live scene means
                   // no shipped material here says `twosided`, which is a finding about the content, not a
                   // broken read; cuts above zero is what the term actually charged in batches.
                   $"({(float)Counters._twoSidedDraws / Counters._frames:F0} two-sided draws/frame, " +
                   $"{(float)Counters._cullCuts / Counters._frames:F1} batch cuts/frame on culling; " +
                   // #327: the opaque cut census by term. A term that dominates here is the next sort digit.
                   $"opaque cuts/frame by term: {CutCensus()}), " +
                   // #243 (AC5): what the GPU cull kept, and what it dropped. Taken off the count buffer
                   // three frames behind the dispatch, so nothing on the submission path waits for it. The
                   // "not running" reading is deliberately distinct from a cull that kept everything: one is
                   // "this instrument has nothing to say", the other is a finding about the area.
                   (CullPass.Mode <= CullPass.ModeOff ? "cull OFF (/alloy cull 2 arms it), "
                      : !CullPass.Ready ? "cull unavailable -- no compute or no indirect-count, "
                      : CullPass.CullFrames == 0 ? "cull armed, no counts read back yet, "
                      : $"cull keeps {(float)CullPass.KeptDraws / CullPass.CullFrames:F0}/frame and drops " +
                        $"{(float)CullPass.CulledDraws / CullPass.CullFrames:F0} " +
                        $"({(CullPass.KeptDraws + CullPass.CulledDraws == 0 ? 0f : 100f * CullPass.CulledDraws / (CullPass.KeptDraws + CullPass.CulledDraws)):F0}% of the colour range, " +
                        $"{(CullPass.Mode >= CullPass.ModeHiZ ? "frustum+hi-Z" : "frustum only")}; bounds span " +
                        $"{CullPass.SpreadX:F0}x{CullPass.SpreadY:F0}x{CullPass.SpreadZ:F0}u, largest radius " +
                        $"{CullPass.SpreadMaxR:F0}u), ") +
                   (Renderer.CullWatch.V ? $"cullwatch {CullPass.WatchCollapses} collapse(s) over {CullPass.WatchReads} reads ({CullPass.WatchLate} late), " : "") +
                   // #285: every reason on this list is a HOLE -- geometry that nothing renders this frame.
                   $"NOT DRAWN this frame: unlit-pass {(float)Counters._skipNoLight / Counters._frames:F0} identity {(float)Counters._skipIdentity / Counters._frames:F0} " +
                   $"inactive-LOD {(float)Counters._skipLod / Counters._frames:F0} " +
                   (Counters._skipForeignScene > 0
                       ? $"other-scene {(float)Counters._skipForeignScene / Counters._frames:F0} (routine: one draw order, many scenes) "
                       : "") +
                   $"dissolve {(float)Counters._ownDissolve / Counters._frames:F0} owned, fade {(float)Counters._ownFade / Counters._frames:F0} owned " +
                   $"fog-out {(float)Counters._skipFogTile / Counters._frames:F0} tile + {(float)Counters._skipFogGob / Counters._frames:F0} gob " +
                   $"no-mdl {(float)Counters._skipNoMdl / Counters._frames:F0} tex {(float)Counters._skipTex / Counters._frames:F0}" +
                   (Counters._skipTex == 0 ? " " : $" (no-gl-id {(float)Counters._skipTexNoId / Counters._frames:F0}" +
                       $" not-owned {(float)Counters._skipTexNotOwned / Counters._frames:F0}" +
                       $" no-handle {(float)Counters._skipTexNoHandle / Counters._frames:F0}) ") +
                   $"range {(float)Counters._skipRange / Counters._frames:F0} vao {(float)Counters._skipVao / Counters._frames:F0}), " +
                   // Drawn, not dropped: the divergence-A population. It sits outside the NOT DRAWN list on
                   // purpose -- a part on the white stand-in is on screen exactly as vanilla puts it there.
                   (Counters._nullTexDraws == 0 ? ""
                      : $"null-tex {(float)Counters._nullTexDraws / Counters._frames:F0} drawn white/frame, ") +
                   (Counters._noDiffuseDraws == 0 ? ""
                      : $"no-diffuse {(float)Counters._noDiffuseDraws / Counters._frames:F0} drawn untextured/frame, ") +
                   // The one split that answers "are characters Alloy's" without inference. Drawn ~0 with
                   // owned nonzero means every creature and placeable in view is a HOLE (#285): since #264
                   // there is no engine walk behind the skip, so nothing renders them at all.
                   $"instanced {(float)Counters._drawsGob / Counters._frames:F0} drawn/frame, " +
                   // #244 (AC1): the mesh-sharing census. "1,00x" says every owned part has its own item
                   // range and there is nothing for instancing to collapse; the object-space figure is the
                   // only part of the shared count that could become instances, for the reason MeshCensus
                   // states. Reported from the live area rather than assumed from the VDO count.
                   $"mesh sharing {(Counters._msMeshes == 0 ? 0f : (float)Counters._msParts / Counters._msMeshes):F2}x " +
                   $"({Counters._msMeshes} meshes, max {Counters._msMaxInstances} instances, {Counters._msShared} parts share a mesh " +
                   $"of which {Counters._msSharedMx} object-space" +
                   (Counters._msUnread == 0 ? "" : $"; {Counters._msUnread} PARTS UNREAD -- their index range did not resolve, " +
                    "so they are outside this count") + "), " +
                   // #244: the SHAPE tier, which is the one that matters on placeable-heavy content -- fifty
                   // identical barrels are fifty placements of one model, and if the engine copies its
                   // vertices per placement then every RANGE is distinct while the geometry repeats fifty
                   // times. A signature, not a proof (same texture + same index count collides), so 1,00x is
                   // the conclusive reading and anything above it is a population to confirm, not design on.
                   $"shape repeats {(Counters._msShapes == 0 ? 0f : (float)Counters._msParts / Counters._msShapes):F2}x " +
                   $"({Counters._msShapes} shapes, max {Counters._msShapeMax}, {Counters._msShapeRepeat} parts on a repeated shape " +
                   $"of which {Counters._msShapeRepeatGob} placeable/creature and {Counters._msShapeRepeatMx} object-space; " +
                   $"{Counters._msGobParts} gobs and {Counters._msMxParts} object-space of {Counters._msParts} censused parts" +
                   // #244: a SIGNATURE, never a proof -- same texture + same index count collides. ADR 0017
                   // settled the reading (1,00x at record identity, 2,5x at content level); #258 deleted the
                   // content pass that measured the second, because it could only reach the bytes by mapping
                   // a buffer back. Sharing is residency's question now (#254), keyed on content at load.
                   "), " +
                   $"normal maps {(float)Counters._nrmBound / Counters._frames:F0} bound of " +
                   $"{(float)Counters._nrmDeclared / Counters._frames:F0} declared/frame, " +
                   // #242 (AC3): the whole point of the persistent records, as one number. "written" should
                   // be the MOVER count -- creatures, doors, animated textures, fading tiles -- and not the
                   // owned count. If the two are the same the split has a permanent mover in it and step 2
                   // of ADR 0017 bought nothing, which no frame-time average would ever say out loud.
                   $"records {(float)Counters._recWritten / Counters._frames:F0}/frame written, " +
                   $"{(float)Counters._recStable / Counters._frames:F0} stable ({_recOwnCap} slots), " +
                   // CX-43: program switches that took the pass terms, and how many of them had to send the block
                   $"pass block {(float)PassBinds / Counters._frames:F1} binds/frame ({(float)PassBindsBorrowed / Counters._frames:F1} through another view's cluster table), {(float)PassUploads / Counters._frames:F1} sends/frame, " +
                   $"record table {Issue.WholeUploads} whole upload(s) ({Issue.WholeBytes >> 20} MiB) over {Counters._frames} frames, " +
                   // #330: how much of the engine chase the view bounded away, and the poll audit.
                   ChaseNote() + ", " +
                   (Counters._watchEnrolled == 0 ? "" :
                       $"dumptex {Counters._watchEnrolled} enrolled, {(float)Counters._watchSubmitted / Counters._frames:F1} submitted/frame, " +
                       "") +
                   // #125: "masked" is the criterion, not "captured" -- it counts the frames the engine's own
                   // sky wrote nothing because the Alloy one had already drawn. Captured but never masked
                   // means the pass is staging nothing, which is a different bug from never seeing the sky.
                   // #175: every registered pass's clause, in declaration order. #139's "post 0 resolves"
                   // while the world renders is the whole reason this is a roll-call and not a fixed list --
                   // the frame looks fine, just 8-bit and un-bloomed, and only the clause says so.
                   PassRegistry.Global.Stats(Counters._frames) + ", " +
                   $"enrolls={Counters._enrolls} revokes={Counters._revokes}; cycle={CycleTextures.Count} steps={CycleTextures.Steps}" + (CycleTextures.Overflow == 0 ? "" : $" overflow={CycleTextures.Overflow}") + (CycleTextures.OtherClass == 0 ? "" : $" otherctl={CycleTextures.OtherClass}") + "; " +
                   // #119: the prepass's own cost, and what it bought. samples/px is the overdraw figure --
                   // run the same scene with /alloy prepass 0 and the difference IS the pass's worth.
                   (!_depthPrepass ? "prepass OFF (/alloy prepass 1 restores it)"
                    : Programs._progDepth == 0 ? "prepass unavailable"
                    : $"prepass {(float)Counters._prepassDraws / Counters._frames:F0} draws/frame" +
                      (Counters._prepassKeyed == 0 ? "" : $" ({(float)Counters._prepassKeyed / Counters._frames:F0} of them dissolving, keyhole stage)") +
                      (Counters._prepassSkipped == 0 ? "" : $" (+{(float)Counters._prepassSkipped / Counters._frames:F0} excluded, displacing/skinned or cutout)") +
                      // #489 (AC2): the GPU's kept figures under the staged ceiling -- phase one plus phase
                      // two, and phase two's own share -- with phase two's submission as its own term.
                      (CullPass.CullFrames == 0 ? "" : $" (cull kept {(float)CullPass.PrepassKept / CullPass.CullFrames:F0}, of which retest {(float)CullPass.RetestKept / CullPass.CullFrames:F0})") +
                      (_qTerm[(int)GpuTerm.Prepass].Measured ? $" in {GpuMs(GpuTerm.Prepass):F3}ms" : "") +
                      (_qTerm[(int)GpuTerm.PrepassRetest].Measured ? $" + retest {GpuMs(GpuTerm.PrepassRetest):F3}ms" : "") +
                      (_qTerm[(int)GpuTerm.PrepassKey].Measured ? $" + keyhole sweep {GpuMs(GpuTerm.PrepassKey):F3}ms" : "") +
                      (_qTerm[(int)GpuTerm.PrepassFeat].Measured ? $" + cutout/displaced sweep {GpuMs(GpuTerm.PrepassFeat):F3}ms ({(float)Counters._prepassFeatDraws / Counters._frames:F0} draws)" : "") +
                      (_qTerm[(int)GpuTerm.Ao].Measured ? $" + ao {GpuMs(GpuTerm.Ao):F3}ms" : "") +
                      (!_prepassKey ? " (keyhole sweep OFF -- /alloy prepasskey 1)" : "")) +
                   // CX-120: the sample query reads 0 on the DB path while the frame visibly shades, so the
                   // counter is not measuring this path's draws. A measured-looking zero is worse than
                   // silence -- it reads as "nothing rasterised", which is exactly the fault we spent a day
                   // chasing -- so the clause says it is unmeasured rather than reporting a number it has
                   // not got. ponytail: the query bracket needs a DB-mode home; it is a diagnostic, not a
                   // pass, so it waits behind the consumers.
                   ", shaded (not measured)" +
                   (_qTerm[(int)GpuTerm.Opaque].Measured
                       ? $", gpu ms: opaque {GpuMs(GpuTerm.Opaque):F2}" +
                         (_qTerm[(int)GpuTerm.OpaqueKey].Measured ? $" (+ keyhole {GpuMs(GpuTerm.OpaqueKey):F2})" : "") +
                         $" / cutout {GpuMs(GpuTerm.Cutout):F2}" +
                         (_qTerm[(int)GpuTerm.CutoutKey].Measured ? $" (+ keyhole {GpuMs(GpuTerm.CutoutKey):F2})" : "") + " / " +
                         $"grass {GpuMs(GpuTerm.Grass):F2} (+ depth {GpuMs(GpuTerm.GrassDepth):F2}) / sky {GpuMs(GpuTerm.Sky):F2} / blended {GpuMs(GpuTerm.Blended):F2}" +
                         (_qTerm[(int)GpuTerm.BlendedComposite].Measured
                             ? $" (accumulate" + (_qBlendSamples.Measured && Counters._lastPixels > 0 ? $", overdraw x{_qBlendSamples.Avg / Counters._lastPixels:F2}" : "")
                               + (_blendFrames > 0 ? $", {_blendGroups / _blendFrames} groups" : "")
                               + (_qBlendPrims.Measured ? $", {_qBlendPrims.Avg / 1000.0:F0}k tris" : "")
                               + (_qBlendFrags.Measured && Counters._lastPixels > 0 ? $", shaded x{_qBlendFrags.Avg / Counters._lastPixels:F2}" : "")
                               + $"; + composite {GpuMs(GpuTerm.BlendedComposite):F2}, hosted {GpuMs(GpuTerm.BlendedHosted):F2}, depth sweep {GpuMs(GpuTerm.BlendedDepth):F2})"
                             : "") + " / " +
                         $"water {GpuMs(GpuTerm.Water):F2} / additive {GpuMs(GpuTerm.Additive):F2} (colour {colourMs:F2}) " +
                         $"/ particles {GpuMs(GpuTerm.Particles):F2}" +
                         (_qPartSamples.Measured && Counters._lastPixels > 0 ? $" (overdraw x{_qPartSamples.Avg / Counters._lastPixels:F2})" : "") +
                         (_qTerm[(int)GpuTerm.Motion].Measured ? $" / motion {GpuMs(GpuTerm.Motion):F2}" : "") +
                         $" / post {GpuMs(GpuTerm.Post):F2}" +
                         // The terms no pass owned: the cull dispatches, the hi-Z build, beams, the water
                         // copy -- and the total, to read against the [gl46] gpu-span. Span minus total is
                         // idle-or-unbracketed; it is idle if it shrinks when the frame goes GPU-bound.
                         $" / cull {GpuMs(GpuTerm.Cull):F2} / hiz {GpuMs(GpuTerm.HiZ):F2} / beams {GpuMs(GpuTerm.Beams):F2}" +
                         $" / water copy {GpuMs(GpuTerm.WaterCopy):F2}" +
                         // CX-51: the history copy alone; the march itself is inside opaque (A/B with /light ssr 0)
                         (_qTerm[(int)GpuTerm.Ssr].Measured ? $" / ssr {GpuMs(GpuTerm.Ssr):F2}" : "") +
                         // CX-188: the deferred point-light dispatch. Read it against the lit colour terms
                         // above: this is what light.deferred moved OUT of them (A/B with /light deferred 0).
                         (_qTerm[(int)GpuTerm.Deferred].Measured ? $" / deferred {GpuMs(GpuTerm.Deferred):F2}" : "") +
                         // CX-189: the coverage layer -- seed, the forward-lit A2C draws and the resolve, together
                         (_qTerm[(int)GpuTerm.Coverage].Measured ? $" / a2c {GpuMs(GpuTerm.Coverage):F2}" : "") +
                         // CX-188 step 3: the per-pixel shadow record the walk above reads, apart from it
                         // (A/B with /shadow deferredres and /shadow cap 0).
                         (_qTerm[(int)GpuTerm.DeferredShadow].Measured
                             ? $" / deferred shadows {GpuMs(GpuTerm.DeferredShadow):F2}" : "") +
                         // CX-188 step 4: the hot-light debt -- what the deferred stage's CX_NO_HOT gave up
                         // on the baked receivers, taken back once per pixel (A/B with /lightmap moverlights 0).
                         (_qTerm[(int)GpuTerm.PairDebt].Measured ? $" / pair debt {GpuMs(GpuTerm.PairDebt):F3}" : "") +
                         (_qTerm[(int)GpuTerm.Weather].Measured ? $" / weather {GpuMs(GpuTerm.Weather):F3}" : "") +
                         $" (gpu terms total {GpuTermsTotal() + CasterPass.ShadowGpuMs:F2} incl. shadows {CasterPass.ShadowGpuMs:F2}; colour bracket span {GpuSpanMs:F2})" : "") +
                   // #121: captures per SESSION against frames per PERIOD. A number that tracks the frame
                   // count is the acceptance criterion ("probe capture does not run per frame") failing.
                   PassCpuNote(Counters._frames) +
                   $", {StreamRing.Diag()}" +
                   $", probe {(EnvProbe.Ready ? $"ready ({EnvProbe.Captures} captures)" : "none yet")}" +
                   "; " +
                   AlloyLights.Diag(Counters._clusterOverflow) +
                   // The coverage question -- "what is the engine still drawing, and why?" -- was only
                   // answerable from the one-shot enroll diag, which a healthy session prints once at startup
                   // and never again. It belongs where the numbers it explains are.
                   // Nothing lit means nothing samples the shadow maps -- the whole Alloy lit stage, cascades
                   // and cubes included, is dead code for the session. Loud, because it looks like working
                   // output: parts are owned, cubes render, and the frame is merely fullbright.
                   (_litCount == 0 && Counters._lastEnrolled > 0
                       ? "; NOTHING LIT -- no Alloy draw samples the shadow maps"
                       : "") +
                   // Dangling texture pointers AurTex refused. A handful around an area transition is the
                   // engine freeing textures a material still names; a number that climbs every period means
                   // something upstream is holding a dead MATERIAL, which is a lifetime bug rather than a
                   // texture one -- and this line is the only place it would ever be visible.
                   (Framework.AurTex.Refused == 0 ? ""
                       : $"; {Framework.AurTex.Refused} dangling texture pointers refused (session total)"));
    }

    /// <summary>The period tallies: every count the [alloy] line prints and its reset zeros, wherever it is
    /// bumped. Grouped so a partial's reader can tell a tally from state without knowing all 400 names.
    /// Constant initialisers only: a runtime cctor would put an init check on every hot-path bump.</summary>
    internal static class Counters
    {
        internal static int _frames, _revokes, _enrolls, _lastEnrolled, _lastCandidates;
        /// <summary>Distinct diffuse textures interned at the last enrollment -- the batch floor (see EndFrame).</summary>
        internal static int _lastTextures;
        internal static int _lastPixels;
        /// <summary>The Alloy draw log counter, counterpart to M0's Alloy cast.</summary>
        internal static long _draws;
        /// <summary>Submitted multidraw batches -- the measurable form of "draw count decoupled from part
        /// count". draws/batches IS the batching ratio, straight out of the log.</summary>
        internal static long _batches;
        /// <summary>Alloy draws per class this pass, and the batches each class's pass submitted. The
        /// cutout/blended split is the whole model, so it is reported rather than inferred.</summary>
        internal static readonly long[] _drawsByClass = new long[AlloyClasses.Count];
        /// <summary>The INSTANCED half of the draw accounting: gob parts Alloy staged this pass, where every
        /// other number on the [alloy] line mixes tiles, placeables and creatures into one total. Read it
        /// against <c>{_gobOwned} owned</c> -- a gap between them is instanced geometry NOT DRAWN, nothing
        /// being left behind an unstaged part.</summary>
        internal static long _drawsGob;
        /// <summary>Staged draws whose material said <c>twosided</c>, and the batches the culling term alone
        /// opened. Together they say what reproducing the engine's per-material cull decision cost the batch
        /// count -- measured, not assumed.</summary>
        internal static long _twoSidedDraws, _cullCuts;
        /// <summary>Opaque-range batch cuts per cut-key term, in <see cref="CutTerms"/> order. Sized by a
        /// const, not by the label table: a string array cannot be preinitialised and would give this class a
        /// cctor.</summary>
        internal static readonly long[] _cutBy = new long[CutTermCount];
        internal const int CutTermCount = 8;
        /// <summary>Why an Alloy part still did not draw this pass, by term. The single fallback count could not
        /// separate a camera-dependent bail-out (keyhole, tile fade) from a content one, which is exactly the
        /// distinction a part that is Alloy head-on and vanilla at the screen edge turns on.</summary>
        internal static long _skipLod;
        /// <summary>Parts the pass skipped because they belong to ANOTHER SCENE. Its own term, not a share of
        /// the LOD one: the draw order is a single table over every slot, so a world pass routes past every
        /// GUI panel's controls and the count is large, routine and says nothing is wrong -- where
        /// <see cref="_skipLod"/> is content state and a large one is a question.</summary>
        internal static long _skipForeignScene;
        internal static long _skipNoLight, _skipIdentity, _skipNoMdl, _skipTex,
                             _skipRange, _skipVao;
        /// <summary>...and the causes behind the <c>tex</c> fallback, which are different bugs wearing one
        /// number. "No GL id" is the copy path; "no owned handle" is the BINDLESS layer, which a part can fail
        /// every frame forever -- a steady count there, roughly one creature's worth of parts, is a specific
        /// model whose texture Alloy declined to own.</summary>
        internal static long _skipTexNoId, _skipTexNoHandle, _skipTexNotOwned;
        /// <summary>Draws whose material declares NO diffuse slot. Like <see cref="_nullTexDraws"/> these are
        /// DRAWN, on the white stand-in, because vanilla draws them with texture stage 0 disabled
        /// (<c>Material::BindAllStandardTextures</c> @0x4c6e30).</summary>
        internal static long _noDiffuseDraws;
        /// <summary>Draws that took the white null-texture stand-in because the diffuse resolved to GL id 0.
        /// These are DRAWN, not skipped -- vanilla renders such a part blank white and so does Alloy now -- so
        /// they are counted apart from the tex fallback rather than inside it. A steady count names content
        /// whose texture resource is genuinely absent; `no-gl-id` beside it can only be a mint that failed.</summary>
        internal static long _nullTexDraws;
        /// <summary>Parts whose tile fog ramp (or a gob part's filed tile) read 0, dropped outright at route.
        /// Split by gob/tile because a creature part here is a HOLE the fog-of-war never explains.</summary>
        internal static long _skipFogTile, _skipFogGob;
        /// <summary>Alloy parts the camera dissolves this pass -- drawn by the Alloy program with the dissolve
        /// folded in, where the keyhole used to drop them from the frame.</summary>
        internal static long _ownDissolve;
        /// <summary>Its counterpart: Alloy parts the tile fade moves into the blended run this pass, where the
        /// tilefade used to drop them from the frame.</summary>
        internal static long _ownFade;
        /// <summary>Parts whose material declared a normal map, and the subset whose map survived the per-slot
        /// re-fetch into the mask the lit stage samples on.</summary>
        internal static long _nrmDeclared, _nrmBound;
        /// <summary>Lit parts refused at enrollment (no normal stream / lit program down) -- they stay vanilla.</summary>
        internal static int _litUnownable;
        /// <summary>Parts refused for carrying bone streams, and what the per-instance sweep then cost --
        /// already-accepted parts revoked because a SIBLING on the same instance was refused. With
        /// <c>AlloyCensus.RefusedGobs</c> on the same line, the honest answer to "why is that creature still
        /// vanilla". Counted as PARTS, which is the figure the sweep actually produces.</summary>
        internal static int _skinUnownable, _gobParts;
        /// <summary>Blended parts dropped from this enrollment because the class could not be Alloy whole --
        /// dropped means NOT DRAWN, there being no engine walk behind the class. Reported: a class that never
        /// lands is the one number separating "no transparent content here" from "one part keeps refusing and
        /// takes the class down with it".</summary>
        internal static int _blendedDropped;
        /// <summary>Record writes should scale with the MOVER count. If "written" reads as the owned count, the
        /// per-frame/stable split has a permanent mover in it.</summary>
        internal static long _recWritten, _recStable;
        /// <summary>The per-report bone-slot high-water mark, which keeps the ceiling counted rather than silent.</summary>
        internal static int _boneHigh;
        internal static long _skinDrawn, _skinNoPose;
        internal static uint _clusterOverflow;
        internal static long _prepassDraws;
        /// <summary>Prepass draws that went through the keyhole depth stage -- the number that says the
        /// dissolve prepass is doing its job, since each of these would otherwise be in
        /// <see cref="_prepassSkipped"/>.</summary>
        internal static long _prepassKeyed;
        /// <summary>Draws the prepass had to leave out because their feature moves vertices, plus the ones
        /// that dissolve and therefore discard. Reported, not hidden: it is the overdraw the prepass no longer
        /// removes, and the number that says whether a displacing prepass stage would be worth building.</summary>
        internal static long _prepassSkipped;
        internal static long _prepassFeatDraws;
        /// <summary>How much of the <c>dumptex</c> population we enrolled, and how much of it we actually
        /// submit each frame. The gap between them, and between both and the engine's own draw count, is the
        /// only honest answer to "is that thing on screen ours".</summary>
        internal static int _watchEnrolled;
        internal static long _watchSubmitted;
        /// <summary>Parts the last census actually keyed, and parts it had to leave out because their index
        /// range would not resolve. The second number is what stops the first being read as the population
        /// when the dereference went blind.</summary>
        internal static int _msParts, _msUnread;
        internal static int _msMeshes, _msMaxInstances, _msShared, _msSharedMx;
        /// <summary>The shape tier -- distinct meshes by signature, the largest repeat, how many parts sit on a
        /// repeated shape and how many of those are gob (placeable/creature) parts. The tier the range key is
        /// blind to: repeated geometry the engine copied per placement.</summary>
        internal static int _msShapes, _msShapeMax, _msShapeRepeat, _msShapeRepeatGob;
        /// <summary>How many censused parts belong to a gob at all -- the denominator that says whether a
        /// placeable-heavy area was even in the population.</summary>
        internal static int _msGobParts;
        /// <summary>Censused parts that are matrix-class, and the repeated ones among them. If the first is
        /// near zero the area's geometry is world-space and nothing repeated in it is collapsible whatever the
        /// shape tier says -- the reading that decides whether a repeat count means anything.</summary>
        internal static int _msMxParts, _msShapeRepeatMx;
    }

    /// <summary>One-shot diagnostic budgets and "said it once" latches. Cold by construction; grouped for the
    /// same reason as <see cref="Counters"/>.</summary>
    internal static class Diag
    {
        /// <summary>A small budget of NAMED failures. The counters say which fault; only the resref says which
        /// CONTENT, and that is what separates "one model's texture" from "a whole class of them". Budgeted
        /// rather than rate-limited: the population is what matters and it is small, so a handful of distinct
        /// names is the whole answer.</summary>
        internal static int _texSkipDiagLeft = 12;
        internal static readonly System.Collections.Generic.HashSet<string> _texSkipSeen = new();
        /// <summary>Diagnostic budget for <see cref="HandleDiag"/>. Cold by construction -- the miss path runs
        /// about once per distinct texture per enrollment, not per draw -- and it stops speaking after this many
        /// reports, so it costs one branch on a warm cache.</summary>
        internal static int _handleDiagLeft = HandleDiagBudget;
        internal static bool _ambientFallbackLogged, _loggedOnce;
    }

    /// <summary>A per-bucket counter as "bucket:per-frame", only the buckets that contributed.</summary>
    internal static string ByBucket(long[] counts)
    {
        string s = "";
        for (int b = 0; b < counts.Length; b++)
            if (counts[b] != 0) s += $" {b}:{(float)counts[b] / Counters._frames:F0}";
        return s.Length == 0 ? " none" : s;
    }
}
