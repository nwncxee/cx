using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>The one [alloy] period line: every tally the period took, printed and reset.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Pixels per world radius at unit depth for THIS pass (0 without perspective), kept for the cube
    /// receiver's disc gate.</summary>
    private static float _lodScale;

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
        _log?.Info($"AlloyRenderer: [alloy] {DbFeed.Stats()} | {DbTableNote()} | {DbPassNote()} "
                     + $"| {DbInputs.Stats()} | {DbEmitterChannels.Stats()} | {DbGui.Stats()}");
        _log?.Info($"AlloyRenderer: [alloy] {Counters._lastEnrolled}/{Counters._lastCandidates} parts owned{CapacityNote()}" +
                   (holes <= 0 ? "" : $" ({holes} NOT DRAWN)") + " [" + OwnedTextures.TranscodeNote() + "; " + OwnedTextures.CacheNote() + "; " + OwnedTextures.UploadNote() + TexStream.Note() + TexLoad.Note() + ModelLoad.Note() + "; " + DdsReader.Note() + "; " + RasterReader.Note() + "]" +
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
                   $"opaque cuts/frame by term: {CutCensus(Counters._cutBy)}; " +
                   $"blended: {CutCensus(Counters._cutByBlend)}), " +
                   // CX-329: what the DRIVER is asked to do. The batch count above is the colour pass alone;
                   // the driver's CPU cost is every pass's call count, and a stage whose calls track its
                   // commands one for one is a stage submitting a draw at a time.
                   $"{Submit.Line(Counters._frames)}, " +
                   // #243 (AC5): the cull's mode and the bounds it tested, measured by the staging walk.
                   // The "not running" reading is deliberately distinct from a cull that kept everything:
                   // one is "this instrument has nothing to say", the other is a finding about the area.
                   (CullPass.Mode <= CullPass.ModeOff ? "cull OFF (/alloy cull 2 arms it), "
                      : $"cull {(CullPass.Mode >= CullPass.ModeHiZ ? "frustum+hi-Z" : "frustum only")}; bounds span " +
                        $"{CullPass.SpreadX:F0}x{CullPass.SpreadY:F0}x{CullPass.SpreadZ:F0}u, largest radius " +
                        $"{CullPass.SpreadMaxR:F0}u, ") +
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
                   $"record table {Issue.Uploads.Whole} whole upload(s) ({Issue.Uploads.RecordBytes >> 20} MiB, handle table {Issue.Uploads.HandleBytes >> 20} MiB: " +
                   $"{Issue.Uploads.Growth} growth, {Issue.Uploads.Overflow} overflow, {Issue.Uploads.Restage} restage, {Issue.Uploads.OverBudget} past {Issue.RunBudget} runs) " +
                   $"and {Issue.Uploads.Deltas} delta(s) of {(float)Issue.Uploads.DeltaRuns / Math.Max(1, Issue.Uploads.Deltas):F1} runs, max {Issue.Uploads.MaxRuns} runs, over {Counters._frames} frames, " +
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
                   $"enrolls={Counters._enrolls} revokes={Counters._revokes}; " +
                   // #119: the prepass's own cost, and what it bought. samples/px is the overdraw figure --
                   // run the same scene with /alloy prepass 0 and the difference IS the pass's worth.
                   (!_depthPrepass ? "prepass OFF (/alloy prepass 1 restores it)"
                    : $"prepass {(float)Counters._prepassDraws / Counters._frames:F0} draws/frame" +
                      (Counters._prepassKeyed == 0 ? "" : $" ({(float)Counters._prepassKeyed / Counters._frames:F0} of them dissolving, keyhole stage)") +
                      (Counters._prepassSkipped == 0 ? "" : $" (+{(float)Counters._prepassSkipped / Counters._frames:F0} excluded, displacing/skinned or cutout)") +
                      (!_prepassKey ? " (keyhole sweep OFF -- /alloy prepasskey 1)" : "")) +
                   ", shaded (gpu ms on the [vkgpu] line)" +
                   // #121: captures per SESSION against frames per PERIOD. A number that tracks the frame
                   // count is the acceptance criterion ("probe capture does not run per frame") failing.
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
        /// <summary>...and the same census over the BLENDED range, which the opaque one is blind to and
        /// where every batch the frame submits more than once comes from.</summary>
        internal static readonly long[] _cutByBlend = new long[CutTermCount];
        internal const int CutTermCount = 11;
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
        internal static bool _ambientFallbackLogged, _loggedOnce;
    }
}
