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
        _log?.Info($"AlloyRenderer: [alloy] {DbFeed.Stats()} | {DbTableNote()} | {DbPassNote()} "
                     + $"| {DbInputs.Stats()} | {DbEmitterChannels.Stats()} | {DbGui.Stats()}");
        _log?.Info($"AlloyRenderer: [alloy] {_ownLive} parts owned{CapacityNote()}" + " [" + OwnedTextures.TranscodeNote() + "; " + OwnedTextures.CacheNote() + "; " + OwnedTextures.UploadNote() + TexStream.Note() + TexLoad.Note() + ModelLoad.Note() + "; " + DdsReader.Note() + "; " + RasterReader.Note() + "] " +
                   $"({_litCount} lit" +
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
                        $", {Counters._boneHigh} bone slots peak of {_boneStage.Length}") +
                   // #284: session totals, so a creature that is STILL a hole three periods later is
                   // visible as one rather than being reset out of the line every period.
                   "), " +
                   $"Alloy draws {(float)Counters._draws / Counters._frames:F0}/frame in {(float)Counters._batches / Counters._frames:F0} batches " +
                   // #126: the transparency model, as numbers. Cutout should dwarf blended on NWN content.
                   $"({(float)Counters._drawsByClass[1] / Counters._frames:F0} cutout, {(float)Counters._drawsByClass[2] / Counters._frames:F0} blended, " +
                   // #129: indexed off the enum, not off literals -- inserting Water at 3 renumbered
                   // Additive, and the line went on labelling water draws "additive" while additive itself
                   // stopped being printed at all. This line is the acceptance instrument; it must not lie.
                   $"{(float)Counters._drawsByClass[(int)AlloyClass.Water] / Counters._frames:F0} water, " +
                   $"{(float)Counters._drawsByClass[(int)AlloyClass.Additive] / Counters._frames:F0} additive" +
                   ") " +
                   $"({(Counters._batches == 0 ? 0f : (float)Counters._draws / Counters._batches):F1} draws/batch), " +
                   // #292: the face-culling term's own cost. A two-sided count of 0 with a live scene means
                   // no shipped material here says `twosided`, which is a finding about the content, not a
                   // broken read; cuts above zero is what the term actually charged in batches.
                   $"({(float)Counters._twoSidedDraws / Counters._frames:F0} two-sided draws/frame, " +
                   $"{(float)Counters._cullCuts / Counters._frames:F1} batch cuts/frame on culling; " +
                   // #327: the opaque cut census by term. A term that dominates here is the next sort digit.
                   $"opaque cuts/frame by term: {CutCensus(Counters._cutBy)}; " +
                   $"blended: {CutCensus(Counters._cutByBlend)}), " +
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
                   $"no-mdl {(float)Counters._skipNoMdl / Counters._frames:F0} tex {(float)Counters._skipTex / Counters._frames:F0} " +
                   $"range {(float)Counters._skipRange / Counters._frames:F0} vao {(float)Counters._skipVao / Counters._frames:F0}), " +
                   // The one split that answers "are characters Alloy's" without inference. Drawn ~0 with
                   // owned nonzero means every creature and placeable in view is a HOLE (#285): since #264
                   // there is no engine walk behind the skip, so nothing renders them at all.
                   $"instanced {(float)Counters._drawsGob / Counters._frames:F0} drawn/frame, " +
                   $"normal maps {(float)Counters._nrmBound / Counters._frames:F0} bound of " +
                   $"{(float)Counters._nrmDeclared / Counters._frames:F0} declared/frame, " +
                   // #242 (AC3): the whole point of the persistent records, as one number. "written" should
                   // be the MOVER count -- creatures, doors, animated textures, fading tiles -- and not the
                   // owned count. If the two are the same the split has a permanent mover in it and step 2
                   // of ADR 0017 bought nothing, which no frame-time average would ever say out loud.
                   $"records {(float)Counters._recWritten / Counters._frames:F0}/frame written, " +
                   $"{(float)Counters._recStable / Counters._frames:F0} stable ({_recOwnCap} slots, {(float)Counters._recSettled / Counters._frames:F0} settled, {_settledAt.Misses} settled misses), " +
                   // CX-302: a whole upload sends the table's whole capacity, so the reason one fired is the
                   // number that tells a real table-wide edge from a hand-over ceiling set too low.
                   RecordUploads.Stats(Counters._frames, VkHandoverSeats) + ", " +
                   // CX-43: program switches that took the pass terms, and which of them borrowed a cluster table
                   PassNote() + ", " +
                   $"over {Counters._frames} frames, " +
                   // #330: how much of the engine chase the view bounded away, and the poll audit.
                   ChaseNote() + ", " +
                   // #125: "masked" is the criterion, not "captured" -- it counts the frames the engine's own
                   // sky wrote nothing because the Alloy one had already drawn. Captured but never masked
                   // means the pass is staging nothing, which is a different bug from never seeing the sky.
                   // #175: every registered pass's clause, in declaration order. #139's "post 0 resolves"
                   // while the world renders is the whole reason this is a roll-call and not a fixed list --
                   // the frame looks fine, just 8-bit and un-bloomed, and only the clause says so.
                   PassRegistry.Global.Stats(Counters._frames) + "; " +
                   (!_depthPrepass ? "prepass OFF (/alloy prepass 1 restores it)"
                    : "prepass on" + (!_prepassKey ? " (keyhole sweep OFF -- /alloy prepasskey 1)" : "")) +
                   ", shaded (gpu ms on the [vkgpu] line)" +
                   // #121: captures per SESSION against frames per PERIOD. A number that tracks the frame
                   // count is the acceptance criterion ("probe capture does not run per frame") failing.
                   $", probe {(EnvProbe.Ready ? $"ready ({EnvProbe.Captures} captures)" : "none yet")}" +
                   "; " +
                   AlloyLights.Diag(Counters._clusterOverflow) +
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
        internal static int _frames;
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
        /// <summary>Record writes should scale with the MOVER count. If "written" reads as the owned count, the
        /// per-frame/stable split has a permanent mover in it.</summary>
        internal static long _recWritten, _recStable, _recSettled;
        /// <summary>The per-report bone-slot high-water mark, which keeps the ceiling counted rather than silent.</summary>
        internal static int _boneHigh;
        internal static long _skinDrawn, _skinNoPose;
        internal static uint _clusterOverflow;
    }

    /// <summary>One-shot diagnostic budgets and "said it once" latches. Cold by construction; grouped for the
    /// same reason as <see cref="Counters"/>.</summary>
    internal static class Diag
    {
        internal static bool _ambientFallbackLogged, _loggedOnce;
    }
}
