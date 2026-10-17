using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Alloy.Db;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The standing caster list: one collect per frame selects out of Alloy's ownership table, a refresh
/// revalidates it while its generation holds, and the per-slot records say what each entry owns.</summary>
public sealed unsafe partial class CasterPass
{
    /// <summary><c>/shadow flipcollect 1</c>: the A/B -- every stage cast-admission flip re-collects the caster
    /// list, as before the membership-neutral entries.</summary>
    internal static bool FlipCollect => Shadows.FlipCollect.V;

    /// <summary>Set by anything that makes every recorded face meaningless at once -- a tuning change, a
    /// release, a frame whose scan never reached every slot. Consumed only by a COMPLETE scan, so the
    /// evidence outlives any frame that could not carry it.</summary>
    private static bool _invalidate = true;
    /// <summary>Live indices of this frame's movers -- what every slot's staleness scan runs over. Small by
    /// construction in a still area, which is the whole point.</summary>
    private static readonly int[] _moverIdx = new int[MaxCasters];
    private static int _moverCount;
    /// <summary>The frame's DYNAMIC casters (live indices) -- skinned, matrix-moved, dangly, vertex-dirty.
    /// What the dynamic pool bakes, and what marks a light mover-bearing. A subset of
    /// <see cref="_moverIdx"/>, which adds appearances for the sun's unsplit predicate.</summary>
    private static readonly int[] _dMoverIdx = new int[MaxCasters];
    private static int _dMoverCount;
    /// <summary>The frame's STATIC-SET EVENTS (live indices) -- a caster that appeared, or whose dynamic
    /// membership flipped. The only live movers that may invalidate a static page; vanishes ride the ghost
    /// list.</summary>
    private static readonly int[] _sMoverIdx = new int[MaxCasters];
    private static int _sMoverCount;
    /// <summary>Is this live caster dynamic this frame? Staged into the gather source as the set flag its
    /// dispatch filter reads.</summary>
    private static readonly bool[] _liveDyn = new bool[MaxCasters];
    /// <summary>Per live caster: a skinmesh, or any part rooted on a creature gob. Always a mover and never
    /// under the <c>dynmin</c> floor: a still character is rigid parts a few decimetres across whose matrices
    /// stop, and run 17 lost its shadow to both rules (dyn pool 0/32, 0 posed casters, 4 dangly movers).</summary>
    private static readonly bool[] _liveCreature = new bool[MaxCasters];
    /// <summary>Per live caster: the creature object it is a part of (<see cref="SceneDb"/> index), -1 for none.
    /// Every part of one body folds into one pair-plan mover.</summary>
    private static readonly int[] _liveObj = new int[MaxCasters];
    /// <summary>The pair plan's per-creature fold, indexed by object: the box round this frame's dynamic parts of
    /// that body (<see cref="_aggSeq"/> = the frame it was opened), and the bodies opened, in walk order.</summary>
    private static readonly Vector3[] _aggLo = new Vector3[SceneDb.MaxObjects], _aggHi = new Vector3[SceneDb.MaxObjects];
    private static readonly int[] _aggSeq = new int[SceneDb.MaxObjects];
    private static readonly int[] _aggObj = new int[MaxCasters];
    private static int _aggN;
    /// <summary>The entry's slot stopped casting since the collect (a tile fade, a skip): it stays in the list
    /// with a zero-count draw until it turns back on or the next collect drops it.</summary>
    private static readonly bool[] _liveOff = new bool[MaxCasters];
    /// <summary>The tenant the entry was collected under; a slot re-seated since is not "in the list".</summary>
    private static readonly int[] _liveGenOf = new int[MaxCasters];
    /// <summary>Does the standing list hold this slot under its current tenant? Read by the stage (workers);
    /// every field is written only by the caster pass, which never overlaps the stage.</summary>
    internal static bool InList(int slot)
        => _liveIdxSeq[slot] == _collectSeq && _liveGenOf[_liveIdx[slot]] == AlloyRenderer.SlotEnrollGen(slot);

    /// <summary>Slots whose admission went off outside the stage (a freed slot, an inactive LOD): the refresh
    /// visits them so their entries turn off. Main thread only. Past the cap the next refresh re-collects.</summary>
    private static readonly int[] _offQ = new int[4096];
    private static int _offN;
    private static bool _offOverflow;
    internal static void NoteSlotOff(int slot)
    {
        // The LIVE INDEX, resolved now: a freed slot is commonly re-seated in the same pass, and by the time
        // the refresh reads the queue _liveIdx[slot] names the new tenant's entry, not the one going off.
        if (_liveIdxSeq[slot] != _collectSeq) return;
        if (_offN < _offQ.Length) _offQ[_offN++] = _liveIdx[slot]; else _offOverflow = true;
    }
    /// <summary>Entries appended since the collect. They are outside the per-VAO buckets, so the refresh
    /// visits them every frame until the next collect folds them in.</summary>
    private static readonly int[] _appended = new int[4096];
    private static int _appN;
    /// <summary>Bumped by an append: the gather's staging must be rebuilt even though the collect stood.</summary>
    private static int _listVer, _gListVer;

    /// <summary>Take a freshly seated slot into the standing list without a re-collect: an OFF entry (its
    /// record, and so its admission, has not landed) that the stage's on-flip turns into an appearance.
    /// False = the list cannot take it (no list standing, skinned -- the creature half is contiguous --, out
    /// of room, or no geometry), and the caller re-collects.</summary>
    internal static bool Append(int slot)
    {
        if (_collectedAt < 0 || _liveCount == 0 || AlloyRenderer.SlotSkinned(slot)) return false;
        if (_liveCount >= MaxCasters || _appN >= _appended.Length) return false;
        if (!AlloyRenderer.TryCasterGeom(slot, out var f)) return false;
        if (!CasterRange(in f, out int idxCount, out uint first, out uint baseVert)) return false;
        int live = _liveCount++;
        _liveSlot[live] = slot;
        _liveCx[live] = f.Cx; _liveCy[live] = f.Cy; _liveCz[live] = f.Cz; _liveR[live] = f.R;
        _liveObj[live] = AlloyRenderer.SlotCreatureObj(slot);
        _liveCreature[live] = _liveObj[live] >= 0;
        _liveIdxCount[live] = idxCount; _liveFirst[live] = first; _liveBaseVert[live] = baseVert;
        _liveVao[live] = f.Vao; _livePrim[live] = f.Prim; _liveItem[live] = f.Item;
        _liveTex[live] = AlloyRenderer.VdoHasUv(f.Vao) ? MaskTid(in f) : 0;
        _liveCut[live] = CutFor(f.Coverage, f.EnvAlpha); _liveBlend[live] = f.Blended; _liveContent[live] = f.Content;
        _liveDyn[live] = false; _liveVisit[live] = 0;
        _liveOff[live] = true; _liveGenOf[live] = AlloyRenderer.SlotEnrollGen(slot);
        _liveIdx[slot] = live; _liveIdxSeq[slot] = _collectSeq;
        _appended[_appN++] = live; _listVer++;
        FrameZones.Count(FrameZones.Ev.CasterAppend);
        return true;
    }
    /// <summary>Spheres of the casters that were live last frame and are not live now -- a part that died, a
    /// creature that walked out of the capture, a VDO item range that stopped resolving. A vanished caster
    /// leaves no live mover to name the slot it was standing in, so its LAST KNOWN sphere is the mover.
    /// Bounded; an overflow falls back to the blanket invalidate rather than silently dropping one.</summary>
    private const int MaxGhosts = 1024;
    private static readonly float[] _ghostCx = new float[MaxGhosts], _ghostCy = new float[MaxGhosts],
                                    _ghostCz = new float[MaxGhosts], _ghostR = new float[MaxGhosts];
    private static int _ghostCount;
    /// <summary>Per VDO slot: the engine wrote into its position buffer this frame, so every caster on it has
    /// moved without its matrix moving. Resolved once per slot per frame rather than per caster.</summary>
    private static readonly bool[] _vDirty = new bool[MaxVdos];
    /// <summary><c>/shadow rebake 0</c> turns the predicate off and bakes every slot every frame. The live
    /// half of the self-check -- and the escape hatch if a shadow is ever seen to freeze.</summary>
    private static bool _rebake => Shadows.Rebake.V;
    /// <summary>Did the last frame's slot scan reach every slot? Mover evidence lives in
    /// <see cref="_cubeHadMover"/>, which only the scan writes -- so a frame that collected movers and then
    /// did not scan leaves slots holding no record that anything moved near them. Consuming the evidence
    /// anyway is a shadow frozen mid-move on a rare stall frame; a blanket re-bake costs one frame instead.</summary>
    private static bool _cubeScanned;
    /// <summary>The sun's half of the same evidence, and which of the two passes actually ran. The blanket
    /// invalidate is consumed by both and may only be CLEARED once every pass that ran completed its scan --
    /// a pass that did not run cannot veto, and a frame where neither ran clears nothing. Otherwise a sun
    /// stall is swallowed by a complete cube scan, and a torchless exterior never clears it at all.</summary>
    private static bool _sunScanned, _sunRan, _cubeRan;
    /// <summary>This frame's blanket-invalidate verdict, snapshot before either pass runs.</summary>
    private static bool _frameInvalidate;

    private static long _cubeBakedSlots, _cubeSkippedSlots, _cubeOffSlots, _moverSum, _ghostSum;
    /// <summary>Movers BY REASON. A skip count that stays at zero says the predicate is not paying; only this
    /// says why -- a world whose matrices all jitter, a shared vertex pool marked dirty by one dangly mesh,
    /// or simply a scene full of creatures. Each is a different fix.</summary>
    private static long _mvMatrix, _mvDangly, _mvVertex, _mvBone, _mvAppear;
    /// <summary>Matrix movers' sphere radii this period (sum, largest): what the still scene's movers ARE.</summary>
    private static float _mvMatrixR, _mvMatrixRMax;
    private static int _mvMatrixRMaxSlot = -1;
    private static long _degenSum;
    /// <summary>Mover samples this period with a non-finite sphere, refused before the fold (one would poison the box
    /// it joined and drop that light's hot answer with it).</summary>
    private static long _nanMovers;
    /// <summary>Casters REFUSED this frame because no record has been built for the current tenant of their
    /// slot. They cast nothing this frame and re-appear the next; the vanish sweep covers the views they were
    /// standing in. High and steady after an area settles means the colour build is skipping slots the bake
    /// wants -- which is a missing shadow, so it is on the period line rather than inferred.</summary>
    private static long _noRecordSum;

    private static readonly float[] _liveCx = new float[MaxCasters], _liveCy = new float[MaxCasters],
                                    _liveCz = new float[MaxCasters], _liveR = new float[MaxCasters];
    private static readonly int[] _liveIdxCount = new int[MaxCasters];
    private static readonly uint[] _liveFirst = new uint[MaxCasters], _liveBaseVert = new uint[MaxCasters];
    private static readonly int[] _liveVao = new int[MaxCasters], _livePrim = new int[MaxCasters], _liveItem = new int[MaxCasters];
    /// <summary>The ALLOY OWNERSHIP SLOT this caster draws from. The one field the bake stages per draw that
    /// is not geometry -- the model matrix, the bone base and the material live in <c>uDraw[slot]</c>, which
    /// the depth stage reads through exactly the indirection the colour path uses.</summary>
    private static readonly int[] _liveSlot = new int[MaxCasters];
    /// <summary>The frame each ownership slot was last collected on, and last frame's list. Together they are
    /// the vanish detector: a slot in the previous list whose stamp is not this frame's stopped casting, and
    /// its last known sphere is the mover that invalidates the views it stood in. A stamp rather than a
    /// bitmap so nothing has to be cleared.</summary>
    private static readonly int[] _liveStamp = new int[MaxCasters];
    private static readonly int[] _prevSlots = new int[MaxCasters];
    private static int _prevCount;
    /// <summary>The frame the standing list was COLLECTED on (<c>_collectedAt</c> is the frame it was last
    /// collected OR refreshed). With the refresh no longer stamping every caster per frame, "present last
    /// frame" is membership of a list that stood through last frame: a stamp at or after this collect.</summary>
    private static int _lastCollectFrame = -1;
    /// <summary>Ownership slot -&gt; live index, valid while its seq is the current collect's. The refresh
    /// resolves the colour build's touched slots through it without a walk.</summary>
    private static readonly int[] _liveIdx = new int[MaxCasters], _liveIdxSeq = new int[MaxCasters];
    /// <summary>The live list bucketed by VAO (CSR built at collect), so a dirty VDO names its casters
    /// without a walk over the rest. Indexed by the DENSE caster VAO id, so it spans the arena sections past
    /// <c>AlloyRenderer.DbVaoBase</c> as well as the VDO slots.</summary>
    private static readonly int[] _vaoStart = new int[AlloyRenderer.CasterVaoIds + 1], _vaoMembers = new int[MaxCasters];
    /// <summary>The frame each live index was visited by the refresh, so the three visit sources
    /// (dirty VDO, touched record, dynamic last frame) admit a caster once.</summary>
    private static readonly int[] _liveVisit = new int[MaxCasters];
    /// <summary>The frame's visit list, gathered (and bone-checked) BEFORE any admission so a refresh
    /// that has to bail leaves no half-written stamps for the same-frame collect to misread.</summary>
    private static readonly int[] _visitIdx = new int[MaxCasters];
    private static int _visitN;
    /// <summary>Last frame's dynamic set, copied at <see cref="Account"/> -- the refresh revisits it
    /// to see a caster STOP moving, which no colour-side edge reports.</summary>
    private static readonly int[] _dPrevIdx = new int[MaxCasters];
    private static int _dPrevCount;
    /// <summary>The A/B: 1 = the refresh walks every caster.</summary>
    private static bool _refreshAll => Shadows.RefreshAll.V;
    /// <summary>Casters the refresh visited, and records the colour build changed, summed over the period.</summary>
    private static long _visitSum, _touchedSum;
    /// <summary>Frames a caster stays in the DYNAMIC set after it last moved. The live read that named it:
    /// about 20 static cube bakes a frame charged to "mover", with vertex-dirty flapping hundreds of casters
    /// between the two sets each frame, every flip a static-set event re-baking the cube it stood in. A held
    /// caster costs nothing extra -- the dynamic layer re-bakes every frame anyway.</summary>
    private static int _dynHold => Shadows.DynHold.V;   // default 30, was 8: vertex-dirty items committing intermittently flipped sets and re-baked static cubes 15/frame
    /// <summary>This frame's diffuse texture id for a MASKED caster (0 = not masked, or the texture did not
    /// resolve). Re-fetched every collect: a <c>CAurTexture*</c> is borrowed and freed on area transitions,
    /// so it is never cached -- and the per-frame fetch is what keeps an animated diffuse cutting the right
    /// silhouette.</summary>
    private static readonly int[] _liveTex = new int[MaxCasters];
    /// <summary>...and which threshold goes with it, as the KIND the staging key groups on rather than the
    /// number the stage tests at (<see cref="Threshold"/>).</summary>
    private static readonly CasterCut[] _liveCut = new CasterCut[MaxCasters];
    /// <summary>The content pair each caster casts through (<see cref="AlloyRenderer.CasterFacts.Content"/>), or 0.</summary>
    private static readonly int[] _liveContent = new int[MaxCasters];
    /// <summary>A blended-class caster; routed to the masked stage so its record alpha dithers.</summary>
    private static readonly bool[] _liveBlend = new bool[MaxCasters];
    /// <summary>Coverage casters in the standing list, counted on the period line rather than tallied per
    /// frame: the kind the collect staged is already the flag, so no second column exists to go stale.</summary>
    private static int CoverCasters()
    {
        int n = 0;
        for (int i = 0; i < _liveCount; i++) if (_liveCut[i] == CasterCut.Coverage) n++;
        return n;
    }
    /// <summary>Creature casters occupy the head of the live list (they stage first); the static world
    /// follows. Submit uses the split to report what a view actually took.</summary>
    private static int _liveCreatures;
    private static int _liveCount;
    /// <summary>What the standing list was collected under -- Alloy's caster generation and record epoch, and
    /// the frame. While all three hold, a frame walks the list (<see cref="Refresh"/>) instead of the world;
    /// <c>_collectSeq</c> counts collects so the gather's staging knows when its copy went stale. -1 = no
    /// list (a repack or release dropped it).</summary>
    private static int _liveGen = -1, _liveEpoch = -1, _collectedAt = -1, _collectSeq;
    /// <summary>The collect the gather staging was built from, its staged count, and its masked count (a
    /// period counter the standing staging re-adds per frame).</summary>
    private static int _gCollectSeq = -1, _gN, _gMasked;
    /// <summary>Collects this period, for the [casters] line.</summary>
    private static int _collects;

    /// <summary>Live casters summed over the period. Reported ALONGSIDE the mover count, because the two
    /// being equal is a bug with no other symptom: it means the still world is not in the list the bake
    /// reads, and every shadow of it vanishes the moment nothing moves.</summary>
    private static long _liveSum;
    /// <summary>Creature casters in the frame's staging (pre-cull; the survivors are the GPU's) -- the number
    /// that answers "why did the shadow vanish": a frame that stages zero creature casters cannot show one.</summary>
    private static int _submitCreatures;
    /// <summary>Cutout-caster forensics: draws that ran the 0.2 discard this period, and the three ways a
    /// masked caster can fail to reach the bake -- no UV stream to sample with, no diffuse this frame, or no
    /// cutout program at all. Each of them is a MISSING leaf shadow; none of them is the solid quad.</summary>
    private static long _submitMasked;

    /// <summary>The <c>shadow.movers</c> button: dump one frame's movers on the next bake.</summary>
    internal static void DumpMoversNext() { _dumpMovers = true; PairShadows.Dump.Next = true; }

    /// <summary>A mask dial moved: the caster list's per-record mask routing is stale.</summary>
    internal static void ListChanged() => _listVer++;

    /// <summary>Chain priority on Repack: invalidation order among observers doesn't matter.</summary>
    private const int RepackPriority = 20;
    private static VdoRepackFn* _repackSlot;

    /// <summary>Repack observer. The live list and the gather's uploaded staging are both copies of index
    /// ranges that have just moved, so both are dropped rather than submitted from -- and the drop is the
    /// re-collect edge, since the list otherwise stands across frames.</summary>
    [UnmanagedCallersOnly]
    private static void OnVdoRepack(IntPtr vdo)
    {
        VdoRepackFn next = *_repackSlot;
        if (next != null) next(vdo);
        _liveCount = 0; _liveCreatures = 0; _liveGen = -1;
        CasterGather.Stand();
    }

    /// <summary>Once per frame, before any view submits: revalidate every caster and resolve its live world
    /// sphere and index range into the per-frame live list. Everything here is view-independent by
    /// construction, so every view of the frame stages from the one answer. Hot and alloc-free.</summary>

    /// <summary>This frame's caster list, selected out of ALLOY's ownership table rather than a second table
    /// of our own: the model matrix, the bone base, the index range and the world sphere are all there and
    /// already current, so this walk SELECTS and LOCATES and recomputes nothing. Two passes over the draw
    /// order so the SKINNED casters head the list -- a view that fills its staging truncates the tail.</summary>
    private static void Collect()
    {
        int live = 0;
        _moverCount = 0; _ghostCount = 0; _dMoverCount = 0; _sMoverCount = 0;
        // Read BEFORE OwnedBuffers.ClearDirty: dangly and morph-animated nodes both commit through
        // CommitItemDynamicData, so both land in the owned-buffer dirty set. Per VDO slot, not per caster.
        for (int v = 0; v < MaxVdos; v++) _vDirty[v] = AlloyRenderer.VdoDirty(v);
        int n = AlloyRenderer.OrderCount;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int k = 0; k < n; k++)
            {
                int slot = AlloyRenderer.OrderSlot(k);
                if (AlloyRenderer.SlotSkinned(slot) != (pass == 0)) continue;
                if (!AlloyRenderer.TryCaster(slot, out var f)) continue;
                // Re-read live: Repack moves the item tables under everyone.
                if (!CasterRange(in f, out int idxCount, out uint first, out uint baseVert)) continue;
                _liveSlot[live] = slot;
                _liveCx[live] = f.Cx; _liveCy[live] = f.Cy; _liveCz[live] = f.Cz;
                _liveR[live] = f.R;
                _liveObj[live] = AlloyRenderer.SlotCreatureObj(slot);
                _liveCreature[live] = f.Skinned || _liveObj[live] >= 0;
                _liveIdxCount[live] = idxCount;
                _liveFirst[live] = first;
                _liveBaseVert[live] = baseVert;
                _liveVao[live] = f.Vao; _livePrim[live] = f.Prim; _liveItem[live] = f.Item;
                // A skinned caster is alpha-tested only where the stage can POSE it: the masked stages carry
                // the solid one's skinning branch for coverage parts (CX-227, the hair case -- without it a
                // card's shadow is its solid silhouette), and nothing else, so a masked skinmesh never
                // punches holes out of a BIND pose. Same set the capture cast solid.
                // CX-303: ...and a content pair's caster twin poses its parts itself, the colour program's own way.
                bool maskable = !f.Skinned || f.Coverage || f.Content != 0;
                _liveTex[live] = maskable && AlloyRenderer.VdoHasUv(f.Vao) ? MaskTid(in f) : 0;
                // Review: the triage counter lost its writer in the rework -- a masked-eligible caster whose
                // diffuse did not resolve THIS frame casts solid, and this is the number that says how often.
                if (_liveTex[live] == 0 && maskable && AlloyRenderer.VdoHasUv(f.Vao)) _maskNoTex++;
                _liveCut[live] = CutFor(f.Coverage, f.EnvAlpha); _liveBlend[live] = f.Blended; _liveContent[live] = f.Content;
                // Review: a caster ABSENT last frame is a mover on return, whatever its record says -- a
                // transient TryRange or VAO failure longer than the one-frame ghost+hadMover cover would
                // otherwise re-appear into views that never re-bake its shadow back.
                // #336: present last frame = in a list that stood through last frame (see _listFrame).
                bool appeared = _collectedAt != _frameSeq - 1 || _liveStamp[slot] < _lastCollectFrame;
                Admit(live, slot, f.Skinned, appeared, f.RecMoved, f.Dangly, VertexDirty(f.Vao, f.Item), _liveCreature[live]);
                _liveIdx[slot] = live; _liveIdxSeq[slot] = _collectSeq + 1;
                _liveOff[live] = false; _liveGenOf[live] = AlloyRenderer.SlotEnrollGen(slot);
                live++;
            }
            // ...and pass 0 is the whole creature population, so the split #100 needs falls out of the walk.
            if (pass == 0) _liveCreatures = live;
        }
        _liveCount = live;
        _visitSum += live;
        _appN = 0; _offN = 0; _offOverflow = false; _listVer++;
        BucketByVao();
        VanishSweep();
        _noRecordSum += AlloyRenderer.CasterNoRecord;
        AlloyRenderer.CasterNoRecord = 0;
        RecordProbe();
        _ghostSum += _ghostCount;
        Account();
        Array.Copy(_liveSlot, _prevSlots, live);
        _prevCount = live;
        _liveGen = AlloyRenderer.CasterGen; _liveEpoch = AlloyRenderer.RecordEpoch;
        _lastCollectFrame = _frameSeq; FrameZones.Count(FrameZones.Ev.Collect);
        _collectSeq++; _collects++;
    }

    /// <summary>Whatever stopped casting this frame -- revoked, re-enrolled away, its index range stopped
    /// resolving -- is a mover at its LAST KNOWN sphere, ghosted so it invalidates the views it was standing
    /// in and only those. A slot carried over from last frame and not stamped by this collect is the whole
    /// test: no hash, no bucket, no eviction.</summary>
    private static void VanishSweep()
    {
        for (int q = 0; q < _prevCount; q++)
        {
            int slot = _prevSlots[q];
            if (_liveStamp[slot] == _frameSeq) continue;
            if (_ghostCount >= MaxGhosts) { _invalidate = true; break; }
            AlloyRenderer.CasterSphere(slot, out float gx, out float gy, out float gz, out float gr);
            // A departing caster with a degenerate stored sphere cannot name the views it shadowed, so the
            // admission's direction is mirrored: unknown means the blanket, which for one frame cannot lie.
            Ghost(gx, gy, gz, gr);
        }
    }

    /// <summary>A mover at a last-known sphere: invalidates the views it stood in. Past the cap, or with a
    /// degenerate sphere, the blanket takes over for the frame.</summary>
    private static void Ghost(float gx, float gy, float gz, float gr)
    {
        if (_ghostCount >= MaxGhosts || !(gr > 0f)) { _invalidate = true; return; }
        _ghostCx[_ghostCount] = gx; _ghostCy[_ghostCount] = gy; _ghostCz[_ghostCount] = gz;
        _ghostR[_ghostCount] = gr;
        _ghostCount++;
    }

    /// <summary>Counting sort of the live indices by VAO -- three passes over the list, paid once per
    /// collect so a dirty VDO's casters are a range rather than a walk.</summary>
    private static void BucketByVao()
    {
        Array.Clear(_vaoStart, 0, _vaoStart.Length);
        for (int i = 0; i < _liveCount; i++) _vaoStart[_liveVao[i] + 1]++;
        for (int v = 0; v < MaxVdos; v++) _vaoStart[v + 1] += _vaoStart[v];
        for (int i = 0; i < _liveCount; i++) _vaoMembers[_vaoStart[_liveVao[i]]++] = i;
        for (int v = MaxVdos; v > 0; v--) _vaoStart[v] = _vaoStart[v - 1];
        _vaoStart[0] = 0;
    }

    /// <summary>The per-caster predicate, ONE function for the full collect and the standing-list refresh so
    /// the two cannot classify a caster differently, counted by REASON because which term saturates decides
    /// where the next fix goes. A still caster is STILL A CASTER: it stays in the list and bakes, it merely
    /// does not invalidate the views it stands in, and <paramref name="appeared"/> errs toward stale.</summary>
    private static void Admit(int live, int slot, bool skinned, bool appeared, bool recMoved, bool dangly,
                              bool vDirty)
        => Admit(live, slot, skinned, appeared, recMoved, dangly, vDirty, creature: skinned);
    /// <summary><paramref name="creature"/>: in the dynamic set whether or not it moved this frame (its page
    /// stays claimed, so a still character keeps its shadow on a baked floor), but a mover -- a face
    /// re-bake -- only on real motion: run 18 admitted every creature part as a mover every frame and the
    /// pool re-baked 20-40 faces a frame with nothing moving (shadows GPU 1.1-1.8 ms, 60 fps).</summary>
    private static void Admit(int live, int slot, bool skinned, bool appeared, bool recMoved, bool dangly,
                              bool vDirty, bool creature)
    {
        // A dangly's VDO is committed every frame the solve runs, so vertex-dirty says nothing about it;
        // its own measured verdict (dangly) does. Here, so both Collect and Refresh agree.
        if (AlloyRenderer.SlotIsDangly(slot)) vDirty = false;
        bool mover = true;
        if (skinned) _mvBone++;
        else if (appeared) { _mvAppear++; FrameZones.Count(FrameZones.Ev.Appeared); }
        else if (recMoved) { _mvMatrix++; _mvMatrixR += _liveR[live]; if (_liveR[live] > _mvMatrixRMax) { _mvMatrixRMax = _liveR[live]; _mvMatrixRMaxSlot = slot; } }
        else if (dangly) _mvDangly++;
        else if (vDirty) _mvVertex++;
        else mover = false;
        if (mover) { _moverIdx[_moverCount++] = live; MoverFrames++; }
        // #336: _dynStamp is the frame the caster last MOVED; it stays dynamic for _dynHold frames after.
        bool moving = skinned || dangly || recMoved || vDirty;
        int last = _dynStamp[slot];
        // the same rule as dyn below, or a creature part with a stale stamp reads as a set flip EVERY frame:
        // run 19 re-baked 4.8 static cube slots a frame off those events (shadows GPU 7.9 ms, 47 fps)
        bool wasDyn = !appeared && (creature || _frameSeq - 1 - last <= _dynHold);
        if (moving) { _dynStamp[slot] = _frameSeq; last = _frameSeq; }
        bool dyn = creature || _frameSeq - last <= _dynHold;
        _liveDyn[live] = dyn;
        if (dyn) _dMoverIdx[_dMoverCount++] = live;
        if (appeared || dyn != wasDyn) { _sMoverIdx[_sMoverCount++] = live; if (!appeared) FrameZones.Count(FrameZones.Ev.DynFlip); }
        _liveStamp[slot] = _frameSeq;
    }

    /// <summary><c>/shadow movers</c> -- one frame's movers and static-set events grouped by diffuse
    /// texture and reason, so "200 matrix movers a frame" has names. Cold and one-shot; allocates.</summary>
    private static bool _dumpMovers;
    private static void DumpMovers(bool refreshed)
    {
        var groups = new Dictionary<string, int>();
        void Count(string k) { groups[k] = groups.TryGetValue(k, out int c) ? c + 1 : 1; }
        for (int q = 0; q < _moverCount; q++)
        {
            int m = _moverIdx[q], slot = _liveSlot[m];
            string why = m < _liveCreatures ? "bones" : AlloyRenderer.SlotRecMoved(slot) ? "matrix"
                         : AlloyRenderer.SlotDangly(slot) ? "dangly" : AlloyRenderer.VdoDirty(_liveVao[m]) ? "vertex-dirty" : "appeared";
            Count($"{why} {AlloyRenderer.SlotTexNameDb(slot)}");
        }
        for (int q = 0; q < _sMoverCount; q++) Count($"STATIC-EVENT {AlloyRenderer.SlotTexNameDb(_liveSlot[_sMoverIdx[q]])}");
        var top = new List<KeyValuePair<string, int>>(groups);
        top.Sort((a, b) => b.Value.CompareTo(a.Value));
        var sb = new System.Text.StringBuilder();
        sb.Append($"CasterPass: [movers] frame {_frameSeq} ({(refreshed ? "refresh" : "collect")}): {_moverCount} movers, " +
                  $"{_sMoverCount} static-set events, {_dMoverCount} dynamic, {_ghostCount} ghosts; by texture:");
        for (int i = 0; i < top.Count && i < 24; i++) sb.Append($"\n    {top[i].Value,5}  {top[i].Key}");
        _log?.Info(sb.ToString());
    }

    /// <summary>The frame's collect when the world did not change -- the list stands, and only what can move
    /// WITHOUT a <see cref="AlloyRenderer.CasterGen"/> edge is re-read: the mover levels, the per-VDO dirty
    /// set and the spheres, over the casters whose answer CAN differ. False = a bone slot was assigned or
    /// reclaimed, which moves a caster between the halves of the list, so the caller re-collects.</summary>
    private static bool Refresh()
    {
        _visitN = 0;
        if (_refreshAll || AlloyRenderer.CasterTouchedOverflow)
        {
            for (int i = 0; i < _liveCount; i++) if (!Mark(i)) return false;
        }
        else
        {
            for (int v = 0; v < MaxVdos; v++)
                if (AlloyRenderer.VdoDirty(v))
                    for (int m = _vaoStart[v]; m < _vaoStart[v + 1]; m++) if (!Mark(_vaoMembers[m])) return false;
            for (int k = 0, n = AlloyRenderer.CasterTouchedCount; k < n; k++)
            {
                int slot = AlloyRenderer.CasterTouchedSlot(k);
                if (_liveIdxSeq[slot] == _collectSeq && !Mark(_liveIdx[slot])) return false;
            }
            for (int q = 0; q < _dPrevCount; q++) if (!Mark(_dPrevIdx[q])) return false;
        }
        if (_offOverflow) return false;
        for (int q = 0; q < _offN; q++) if (!Mark(_offQ[q])) return false;
        _offN = 0;
        // An appended entry is outside the VAO buckets, so the vertex-dirty visit is the one it would miss;
        // its on-flip and its dynamic set reach it through the touched list and last frame's set like any other.
        for (int q = 0; q < _appN; q++) { int i = _appended[q]; if (AlloyRenderer.VdoDirty(_liveVao[i]) && !Mark(i)) return false; }
        _moverCount = 0; _ghostCount = 0; _dMoverCount = 0; _sMoverCount = 0;
        for (int v = 0; v < MaxVdos; v++) _vDirty[v] = AlloyRenderer.VdoDirty(v);
        _visitSum += _visitN; _touchedSum += AlloyRenderer.CasterTouchedCount;
        for (int k = 0; k < _visitN; k++)
        {
            int i = _visitIdx[k], slot = _liveSlot[i];
            // SL1: the visited set IS the set whose staged words can move -- both of them are written below
            // and nowhere else on a standing list.
            NoteStagedWord(i);
            if (!AlloyRenderer.CastOk(slot) || _liveGenOf[i] != AlloyRenderer.SlotEnrollGen(slot))
            {   // turned off since the collect: a ghost at its last sphere, and the entry stays, drawing nothing
                if (!_liveOff[i]) { _liveOff[i] = true; Ghost(_liveCx[i], _liveCy[i], _liveCz[i], _liveR[i]); }
                continue;
            }
            bool back = _liveOff[i];
            _liveOff[i] = false;
            // The sphere is written on any record change -- and, for a matrix-less part, from bbox terms a
            // kept enrollment may rewrite without a generation bump.
            AlloyRenderer.MoverSphere(slot, out _liveCx[i], out _liveCy[i], out _liveCz[i], out _liveR[i]);
            Admit(i, slot, i < _liveCreatures, appeared: back, AlloyRenderer.SlotRecMoved(slot),
                  AlloyRenderer.SlotDangly(slot), VertexDirty(_liveVao[i], _liveItem[i]), _liveCreature[i]);
        }
        Account();
        return true;
    }

    /// <summary>Put one caster on the frame's visit list, once, whichever source named it. False = its bone
    /// slot was assigned or reclaimed since the collect, which moves it between the two halves of the list
    /// -- the one flip the touched list does report (feat.x changes), and a skinned caster is dynamic and so
    /// revisited every frame besides.</summary>
    private static bool Mark(int i)
    {
        if (_liveVisit[i] == _frameSeq) return true;
        _liveVisit[i] = _frameSeq;
        if (AlloyRenderer.SlotSkinned(_liveSlot[i]) != (i < _liveCreatures)) return false;
        _visitIdx[_visitN++] = i;
        return true;
    }

    /// <summary>The period sums a collect or a refresh leaves behind, and the frame stamp that says the
    /// list is current.</summary>
    private static void Account()
    {
        _liveSum += _liveCount;
        _moverSum += _moverCount;
        _degenSum += AlloyRenderer.CasterDegenerate;
        AlloyRenderer.CasterDegenerate = 0;
        _collectedAt = _frameSeq;
        Array.Copy(_dMoverIdx, _dPrevIdx, _dMoverCount);
        _dPrevCount = _dMoverCount;
    }

    /// <summary>Triage, cold and one-shot: the bake's ONE placement input, checked against the engine's own
    /// answer. A shadow that flickers or does not render can only be a view that bakes nothing, a view nobody
    /// samples, or geometry baked in the wrong place. Reports the matrix-class casters at the head of the
    /// list, the record's translation beside the node's, and how many casters have no record at all.</summary>
    private static int _probeLeft = 6;
    private static void RecordProbe()
    {
        if (_probeLeft <= 0 || _liveCount == 0) return;
        _probeLeft--;
        int shown = 0, mxSeen = 0, apart = 0, none = 0;
        for (int i = 0; i < _liveCount; i++)
        {
            AlloyRenderer.CasterProbe(_liveSlot[i], out var lag, out var live,
                                      out bool mx, out bool known, out int feat);
            if (!known) none++;
            if (!mx) continue;
            mxSeen++;
            float dx = lag.X - live.X, dy = lag.Y - live.Y, dz = lag.Z - live.Z;
            bool off = dx * dx + dy * dy + dz * dz > 1f;
            if (off) apart++;
            if (shown >= 4 || (!off && shown >= 2)) continue;
            shown++;
            _log?.Info($"CasterPass: [probe] slot {_liveSlot[i]} record=({lag.X:F2},{lag.Y:F2},{lag.Z:F2}) " +
                       $"node=({live.X:F2},{live.Y:F2},{live.Z:F2}) sphere=({_liveCx[i]:F2},{_liveCy[i]:F2}," +
                       $"{_liveCz[i]:F2}) r={_liveR[i]:F2} feat={feat} known={known}{(off ? "  <-- APART" : "")}");
        }
        _log?.Info($"CasterPass: [probe] {_liveCount} casters, {mxSeen} matrix-class, {apart} placed APART " +
                   $"from their node, {none} with NO RECORD at all");
    }
}
