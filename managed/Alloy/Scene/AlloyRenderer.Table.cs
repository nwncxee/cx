using CX.Engine;
using System.Collections.Generic;
using System;
using CX.Alloy.Db;
using System.Numerics;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The OWNERSHIP TABLE and the staging tables built from it: fixed-capacity parallel arrays indexed
/// by Alloy slot (or VDO slot, or batch group), allocated once at type init and never after, which is what
/// makes the per-frame path allocation-free. Struct-of-arrays on purpose -- the hot walks touch two or three
/// columns of a wide row, and the sorts permute an index array rather than the rows.</summary>
public sealed unsafe partial class AlloyRenderer
{
    // the ownership table (parallel arrays, index = Alloy slot; _order walks them grouped by VAO)
    private static readonly int[] _oEntry = new int[MaxOwned];      // PartTable slot
    private static readonly IntPtr[] _oTri = new IntPtr[MaxOwned];  // Zero = revoked, harvested at enrollment
    private static readonly IntPtr[] _oNode = new IntPtr[MaxOwned];
    private static readonly IntPtr[] _oMat = new IntPtr[MaxOwned];
    private static readonly IntPtr[] _oVdo = new IntPtr[MaxOwned];
    private static readonly int[] _oItem = new int[MaxOwned];
    private static readonly int[] _oPrim = new int[MaxOwned];
    private static readonly int[] _oVao = new int[MaxOwned];        // slot in the per-VDO VAO cache
    /// <summary><see cref="LodVaoBit"/> (or <see cref="ChartVaoBit"/>) when the last staging drew this slot
    /// from an Alloy arena, 0 otherwise. Carried per SLOT rather than per draw because the velocity pass redraws a mover off the
    /// ownership table and has to bind the element buffer the colour draw used, not the other one.</summary>
    private static readonly int[] _oLodBit = new int[MaxOwned];
    /// <summary>Each part's last <see cref="MeshChart.Range"/> answer, re-probed on a table epoch or LOD change.</summary>
    private static readonly MeshChart.RangeMemo[] _oChart = new MeshChart.RangeMemo[MaxOwned];
    private static readonly int[] _oRotBits = new int[MaxOwned];    // part+0xb0 at enrollment (revalidation)
    private static readonly float[] _oCos = new float[MaxOwned];
    private static readonly float[] _oSin = new float[MaxOwned];
    private static readonly bool[] _oMx = new bool[MaxOwned];       // matrix-class: needs uMVP = model*view*proj
    private static readonly bool[] _oLit = new bool[MaxOwned];      // #83: shade it, don't paint it fullbright
    private static readonly int[] _oTexKey = new int[MaxOwned];     // #84: interned diffuse-texture sort key
    /// <summary>The Gob this part instances off, zero for static geometry. The key the all-owned-or-none
    /// sweep groups by -- never dereferenced here, only compared.</summary>
    internal static readonly IntPtr[] _oGob = new IntPtr[MaxOwned];
    /// <summary>Is this part's gob a TILE gob (<see cref="EngineLayout.GobTileData"/>)? Sampled at
    /// enrolment because that is the one place the gob is known live. See <c>Dissolves</c>.</summary>
    private static readonly bool[] _oTileGob = new bool[MaxOwned];
    /// <summary>The root <c>Part*</c> of the LOD level this slot was enrolled from, zero for a slot with
    /// none. Only compared, never dereferenced.</summary>
    private static readonly IntPtr[] _oLodRoot = new IntPtr[MaxOwned];
    /// <summary>This enrolled part's bone slot for THIS pass, or -1 for a rigid one. Rewritten every
    /// frame in <c>PoseSkinned</c>, never carried across one -- a pose is per-frame by definition.</summary>
    private static readonly int[] _oBone = new int[MaxOwned];
    /// <summary>The two PBR vec4s of this part's draw record, staged verbatim -- (Specularity, Roughness,
    /// Metallicness, _) then (CustomSpecularColor.rgb, declared?). Read once at enrollment off the material's
    /// parsed .mtr list; 0 means "not declared" and the shader synthesises it, as the stock shaders do. The
    /// spare w carries the live slot mask per frame, so index 3 of each pair is never written here.</summary>
    private static readonly ParamRow[] _oParam = new ParamRow[MaxOwned];
    /// <summary>Which of slots 1..5 this material had a texture in at enrollment (bit s-1), plus slot 6, the
    /// flow map, at bit 5 -- above the five, below <see cref="EnvSlot"/>. The live tid is still re-fetched
    /// per frame like slot 0's; this only says which slots are worth asking for.</summary>
    private static readonly int[] _oSlots = new int[MaxOwned];
    /// <summary>This part's <see cref="ShaderFeature"/> and the three .mtr parameters that drive it,
    /// staged verbatim into the record's <c>feat</c> vec4. Which three depends on the feature -- sway
    /// speed/strength/tileset-Z for wind, roof offset/clip/interior for the toggles, slide speed for the
    /// flow map. Read once at enrollment: a .mtr does not animate.</summary>
    private static readonly float[] _oFeat = new float[MaxOwned * 4];
    /// <summary>This part's <see cref="AlloyClass"/> -- the pass it draws in. The most significant
    /// radix digit, so each class occupies a contiguous run of <see cref="_order"/> and each pass owns a
    /// contiguous run of batches.</summary>
    private static readonly byte[] _oCls = new byte[MaxOwned];
    /// <summary>A cutout part whose material asks for alpha-to-coverage. Routed to the tail of the
    /// cutout run so <see cref="CoverageLayer"/> can take it as one batch range.</summary>
    private static readonly bool[] _oA2c = new bool[MaxOwned];
    /// <summary>The bucket this part was captured in, for the one-owner audit alone: an UNMATCHED part is
    /// one we drew that the engine's iterator never walked, and "which bucket did it come from" is the only
    /// question that separates a suppression gap from geometry the engine simply culled this frame.</summary>
    private static readonly byte[] _oBucket = new byte[MaxOwned];
    /// <summary>The material's Aurora blend pair, translated to GL at bind time. Only the blending
    /// classes read it.</summary>
    private static readonly byte[] _oSrc = new byte[MaxOwned], _oDst = new byte[MaxOwned];
    /// <summary>A wash (<see cref="DbWashMode"/>): a see-through sheet lying flat, drawn under a polygon offset so it
    /// cannot z-fight the surface it lies on; <see cref="WashSplit"/> draws its solid interior apart. Batch key term.
    /// Its DEPTH is the class's own -- see <c>BlendLaysDepth</c>.</summary>
    internal static readonly byte[] _oWash = new byte[MaxOwned], _gWash = new byte[MaxOwned];
    /// <summary>A ground decal (<see cref="DbDecal"/>): drawn into <see cref="DecalLayer"/> and folded into the
    /// surface under it. The blended run puts these first, and it is a batch key term.</summary>
    internal static readonly byte[] _oDecal = new byte[MaxOwned];
    internal static readonly bool[] _gDecal = new bool[MaxOwned];
    /// <summary>This part's material said <c>twosided</c>, so it draws with face culling OFF. The
    /// engine's default -- and what a part with no material gets -- is culling ON, which is why this is the
    /// exception flag rather than a cull flag.</summary>
    private static readonly bool[] _oNoCull = new bool[MaxOwned];
    /// <summary>The part's LOCAL bbox centre (part+0x64/+0x70, the pair
    /// <c>Camera::GetCanKeyholeDissolve</c> and the caster cull both read). Run through the live model matrix
    /// each pass it gives the world-space sort key for the back-to-front blended order; captured at
    /// enrollment because a bbox does not move under a part.</summary>
    private static readonly float[] _oCx = new float[MaxOwned], _oCy = new float[MaxOwned],
                                    _oCz = new float[MaxOwned];
    /// <summary>The part's bbox RADIUS (half the diagonal), the colour LOD's size input. 0 = bbox
    /// degenerate or unread -- such a part is never LOD-culled, unknown bounds are not "small".</summary>
    private static readonly float[] _oR = new float[MaxOwned];
    /// <summary>The caster's bound in the part's OWN space -- the box of the vertices its model matrix draws,
    /// as centre and half-diagonal, read off the VDO at enrollment. The engine's part bbox cannot be it: only a
    /// static tile part's is filled (<c>ComputePartBBox</c> @0x461370, in parent space), and a farthest-vertex
    /// fallback swallows offset geometry -- a ship's mast reads 12.2 m where its own box is 4.9 m. 0 r = unread.</summary>
    private static readonly float[] _oBx = new float[MaxOwned], _oBy = new float[MaxOwned],
                                    _oBz = new float[MaxOwned], _oBr = new float[MaxOwned];
    private static readonly int[] _order = new int[MaxOwned];
    private static readonly int[] _orderTmp = new int[MaxOwned];    // #84: LSD radix intermediate
    /// <summary>Every live skinned slot, rebuilt with <see cref="_order"/> -- skin-ness is
    /// enrollment-static, so <c>PoseSkinned</c> walks this instead of the whole table.</summary>
    private static readonly int[] _skinOrder = new int[MaxOwned];
    private static int _skinCount;
    /// <summary>The third radix digit's buckets, and the [start, end) range each class occupies in
    /// <see cref="_order"/> afterwards. A pass draws its class's range and nothing else.</summary>
    private static readonly int[] _clsCount = new int[AlloyClasses.Count],
                                  _clsStart = new int[AlloyClasses.Count],
                                  _clsEnd = new int[AlloyClasses.Count];
    private static int _ownCount, _litCount;
    /// <summary>Entries in <see cref="_order"/>: <see cref="_ownCount"/> minus whatever the all-owned-or-none
    /// sweep dropped. The draw walks this, never the slot count.</summary>
    private static int _orderCount;
    /// <summary>Counting-sort buckets. The draw order groups by (lit, no-cull, VDO) so the VAO, the PROGRAM
    /// and the face-culling state each settle once per group -- lit slots are the upper half, and the
    /// two-sided slots the upper half of each of those. <see cref="SortKeyOf"/> says why lit outranks cull.
    /// </summary>
    private static readonly int[] _sortCount = new int[MaxVdos * 4];
    /// <summary>The second radix digit's counting-sort buckets, over the interned texture key. Sorting by it
    /// FIRST and then stably by (lit, VDO) leaves the order grouped by (lit, VDO) with equal textures
    /// contiguous inside each group -- i.e. exactly the batch key, in one pass over the table.</summary>
    private static readonly int[] _texSortCount = new int[MaxMats];
    /// <summary>The third radix digit's buckets, over <see cref="FeatPrimKey"/>. The feature and the
    /// primitive mode are both cut-key terms and neither was sorted, so a VDO's run cut on every interleave
    /// of them -- under bindless, most of what a lit batch could still cut on.</summary>
    private static readonly int[] _featPrimCount = new int[FeatPrimKeys];
    /// <summary>The slot draws a part of a tile, not a placeable.</summary>
    internal static bool TileSlot(int i) => (uint)i < (uint)MaxOwned && _oTileGob[i];

    /// <summary>What the lightmap bake keys an instance on -- the slot's VDO slot and item (the chart
    /// lookup) and its record's model matrix (the placement; identity for a world-space tile part). The
    /// bounds come from the chart, not from here: the engine's part bbox is unpopulated on tile parts.
    /// False for an empty slot.</summary>
    internal static bool SlotPlacement(int i, out int vao, out int item, out Matrix4x4 model)
    {
        vao = item = 0; model = Matrix4x4.Identity;
        if ((uint)i >= (uint)MaxOwned || _oTri[i] == IntPtr.Zero) return false;
        // A DB slot's chart key is (MeshChart.DbSlot, arena range) -- its _oVao/_oItem are 0/-1, and a
        // slot the bake may not take (a mover, an unlit mesh) has no placement to offer it at all
        if (DbSlot(i))
        {
            if (!DbBakeable(i)) return false;
            vao = MeshChart.DbSlot; item = Db.DrawRecipe.Range(_oDbRecipe[i]);
        }
        else { vao = _oVao[i]; item = _oItem[i]; }
        model = _dData[i].Model;
        return true;
    }

    // Multidraw submission: one entry per DRAW in _dCount/_dOff/_dBase + one staged SSBO record per draw,
    // sliced into batches by the _g* table. All static -- the hot path stages into them and allocates nothing.
    internal static readonly int[] _dCount = new int[MaxOwned];
    internal static readonly nint[] _dOff = new nint[MaxOwned];
    internal static readonly int[] _dBase = new int[MaxOwned];
    internal static readonly DrawRecordBlock[] _dData = new DrawRecordBlock[MaxRecords];
    /// <summary>The world bounding sphere of each RECORD, parallel to <see cref="_dData"/> -- centre in xyz,
    /// radius in w, written on the same edges the record is because it is a function of the same model matrix.
    /// A radius of <see cref="NeverCull"/> means "do not cull me": the vertices move under a matrix that does
    /// not (skinned, dangly, a displacing feature), or the bbox is degenerate. Unknown bounds mean VISIBLE.</summary>
    private static readonly Sphere[] _dSph = new Sphere[MaxRecords];
    /// <summary>The radius that exempts a record from the cull. Large enough to fail every plane test and
    /// finite, so it survives a matrix multiply without becoming a NaN.</summary>
    internal const float NeverCull = 1e30f;
    /// <summary>Per-draw bindless handles, 8 per record (diffuse + material slots 1..7), parallel to
    /// <see cref="_dData"/>. 0 = slot empty; the record's slot mask already forbids sampling those.</summary>
    internal static readonly TexHandles[] _dTexH = new TexHandles[MaxRecords];
    /// <summary>The per-draw index array -- for draw <c>n</c> of this pass, the RECORD slot it reads. Records
    /// are indexed by ownership slot and persist across frames, while draw order is per pass (a fading tile
    /// joins the blended run, sorted from this pass's eye), so the two cannot be the same number. The only
    /// per-draw array the frame uploads whole: four bytes each rather than a hundred and seventy-six.</summary>
    internal static readonly int[] _dSlot = new int[MaxOwned];
    /// <summary>Which record slots were rewritten this frame, i.e. the movers. Uploaded one
    /// <c>BufferSubData</c> each; past a quarter of the table the whole range goes up in one call instead.
    /// </summary>
    internal static readonly int[] _recDirty = new int[MaxRecords];
    internal static int _recDirtyN;
    // The per-slot record stamps (epoch, verified frame, tenancy, texture staleness) are RouteGate's rows.
    /// <summary>The blanket invalidation counter. Bumped by anything that can make every persistent
    /// record wrong at once -- an enrollment (the slots reshuffle), a bindless handle wipe, a lost context,
    /// a debug toggle, an armed frame dump. See ADR 0017's "Step 2, as built".</summary>
    private static int _recEpoch = 1;
    /// <summary>The resolved diffuse tid per slot -- still needed every frame by the batch key (a
    /// bind-path batch cuts on its textures) even when the record itself does not move.</summary>
    private static readonly int[] _recTid = new int[MaxOwned];
    /// <summary>The <see cref="TexRegistry.TableSerial"/> this slot's textures were last resolved under; 0 is
    /// "never, or ask again", which an unsettled resolve (a white stand-in, a slot still loading) stamps. While
    /// it matches, <c>StageDbPart</c> serves the slot's eight handles and its tids from the memos. The registry
    /// bumps the serial to bust every slot; this slot's own override column busts it via <see cref="DbTexDirty"/>.</summary>
    private static readonly int[] _recTexStamp = new int[MaxOwned];
    /// <summary>The registry serial the whole stage phase resolves against, read ONCE per phase --
    /// the registry is quiet while the walk runs (its writers are the game thread and the bracket's Drain,
    /// and the pool's workers run with the game thread parked in <c>StagePool.Run</c>), so a per-part read
    /// would be 12,7k reads of one unchanging int.</summary>
    private static int _stageTexSerial;
    /// <summary>Frames this period whose <see cref="_stageTexSerial"/> differed from the previous
    /// frame's, and the value it is compared against. The skip's own instrument: the serial is meant to move
    /// on a texture EVENT (a transcode drip, an area load), so a count near the frame count means something
    /// is bumping it per frame and every slot re-resolves anyway.</summary>
    private static long _texSerialMoves;
    private static int _texSerialWas;
    /// <summary>This slot's texture answers may have changed for a reason the registry serial cannot
    /// see -- its part's own override column. Called from the record fold for every part carrying
    /// <c>Dirty.Texture</c> (or a create/tree/material mark, which can re-point the recipe under it).</summary>
    internal static void DbTexDirty(int slot)
    { if ((uint)slot < (uint)_recTexStamp.Length) _recTexStamp[slot] = 0; }
    /// <summary>Only the records that minted on the retired name go stale -- the global generation this
    /// replaced re-staged the whole world per drip frame.</summary>
    internal static void SelfCheckNameRetired(ExtenderLog log)
    {
        int sv = _ownCount; _ownCount = 4;
        var svTid = new int[4]; Array.Copy(_recTid, svTid, 4);
        var svS = new int[4 * MapSlots]; Array.Copy(_recTidS, svS, 4 * MapSlots);
        var svStale = new bool[4];
        var svStamp = new int[4]; Array.Copy(_recTexStamp, svStamp, 4);
        for (int i = 0; i < 4; i++) { svStale[i] = _gate[i].TexStale; _gate[i].TexStale = false; }
        Array.Clear(_recTid, 0, 4); Array.Clear(_recTidS, 0, 4 * MapSlots);
        _recTid[1] = 77; _recTidS[3 * MapSlots + 2] = 77; _recTid[2] = 78;
        NoteOwnedNameRetired(77);
        bool ok = !_gate[0].TexStale && _gate[1].TexStale && !_gate[2].TexStale && _gate[3].TexStale;
        // CX-188: the stage's CLEAR (DbStage, beside the tid memos) is sound only because the memo moves with
        // it. Clear with the memo still on the retired name and the next retire finds the slot again; clear
        // after the memo moved and there is nothing left to find, which is what lets the gate go back down.
        _gate[1].TexStale = false;
        NoteOwnedNameRetired(77);
        ok &= _gate[1].TexStale;
        _recTid[1] = 81; _gate[1].TexStale = false;
        NoteOwnedNameRetired(77);
        ok &= !_gate[1].TexStale;
        _recTid[1] = 77; _gate[1].TexStale = true;

        // CX-188 (review): ...and the mark must RESET THE MEMO. A slot whose texture stamp still holds the
        // current serial skips the whole resolve block in StageDbPart -- the handle block, the tid and the
        // clear with them -- so a retire that marks without resetting the stamp binds the retired name,
        // keeps the zeroed handle and force-routes the slot for the rest of the session. None of these three
        // paths is a registry event, so the serial cannot catch it: MarkTexStale is the one funnel.
        _stageTexSerial = TexRegistry.TableSerial;
        for (int i = 0; i < 4; i++) { _gate[i].TexStale = false; _recTexStamp[i] = _stageTexSerial; }
        NoteOwnedNameRetired(77);
        ok &= _gate[1].TexStale && _recTexStamp[1] == 0 && _gate[3].TexStale && _recTexStamp[3] == 0
              && !_gate[0].TexStale && _recTexStamp[0] == _stageTexSerial
              && !_gate[2].TexStale && _recTexStamp[2] == _stageTexSerial;
        for (int i = 0; i < 4; i++) { _gate[i].TexStale = false; _recTexStamp[i] = _stageTexSerial; }
        var retNames = new List<uint> { 78 };
        NoteOwnedNamesRetired(retNames, new HashSet<uint>());
        ok &= _gate[2].TexStale && _recTexStamp[2] == 0 && !_gate[1].TexStale && _recTexStamp[1] == _stageTexSerial;
        // ZeroHandle's own funnel: the dying VALUE is matched in the handle block, so the slot it sits in is
        // marked AND un-memoised -- a zeroed handle that is never re-resolved is the radeonsi page fault.
        const ulong fakeDying = 0xD1E1A5E0u;   // not-an-offset: a stand-in handle value, never dereferenced
        for (int i = 0; i < 4; i++) { _gate[i].TexStale = false; _recTexStamp[i] = _stageTexSerial; }
        ulong svH = _dTexH[2][3]; long svZeroed = HandlesZeroed;
        _dTexH[2][3] = fakeDying;
        ZeroHandle(fakeDying);
        ok &= _gate[2].TexStale && _recTexStamp[2] == 0 && _dTexH[2][3] == 0
              && !_gate[1].TexStale && _recTexStamp[1] == _stageTexSerial;
        _dTexH[2][3] = svH; HandlesZeroed = svZeroed;
        _recTid[1] = 77; _recTidS[3 * MapSlots + 2] = 77; _recTid[2] = 78;
        for (int i = 0; i < 4; i++) _gate[i].TexStale = i == 1 || i == 3;

        // #386: a swap renames in place and queues the two records, once each
        int svSwapN = _swapN; _swapN = 0;
        NoteOwnedNameSwapped(77, 79); NoteOwnedNameSwapped(79, 80);
        ok &= _recTid[1] == 80 && _recTidS[3 * MapSlots + 2] == 80 && _recTid[2] == 78 && _swapN == 2
              && _recSwapped[1] && _recSwapped[3] && !_recSwapped[0];
        _recSwapped[1] = _recSwapped[3] = false; _swapN = svSwapN;
        Array.Copy(svTid, _recTid, 4); Array.Copy(svS, _recTidS, 4 * MapSlots); _ownCount = sv;
        for (int i = 0; i < 4; i++) _gate[i].TexStale = svStale[i];
        Array.Copy(svStamp, _recTexStamp, 4);
        log.Info(ok ? "AlloyRenderer.SelfCheckNameRetired: passed" : "AlloyRenderer.SelfCheckNameRetired: FAILED -- a retired name did not mark exactly its records, or marked one without resetting its texture memo");
    }

    /// <summary>An owned name was REPLACED by another for the same content (the transcode swap, an
    /// engine re-upload). Re-staging every record on it was Restage 3,3-4,9ms against 1,1 in the hitch rows
    /// (a tileset texture sits on hundreds of parts); instead the records take the new name here and their
    /// handle block is rewritten in the next bracket by <see cref="ApplySwaps"/> -- no chase, no re-stage.</summary>
    internal static void NoteOwnedNameSwapped(uint oldName, uint newName)
    {
        int o = (int)oldName, nw = (int)newName;
        for (int i = 0; i < _ownCount; i++)
        {
            bool hit = false;
            if (_recTid[i] == o) { _recTid[i] = nw; hit = true; }
            for (int s = 0; s < MapSlots; s++)
                if (_recTidS[i * MapSlots + s] == o) { _recTidS[i * MapSlots + s] = nw; hit = true; }
            if (hit && !_recSwapped[i]) { _recSwapped[i] = true; if (_swapN < _swapSlots.Length) _swapSlots[_swapN++] = i; else _swapOverflow = true; }
        }
    }
    private static readonly bool[] _recSwapped = new bool[MaxOwned];
    private static readonly int[] _swapSlots = new int[4096];
    private static int _swapN;
    private static bool _swapOverflow;

    /// <summary>Inside the 4.6 bracket (handles are minted and made resident there): rewrite the handle
    /// block of every swapped record from its tids and mark it for upload. A record that never had handles
    /// (unlit) is left alone -- minting for it would spend residency on a texture nothing samples.</summary>
    private static void ApplySwaps()
    {
        if (_swapN == 0 && !_swapOverflow) return;
        int n = _swapOverflow ? _ownCount : _swapN;
        for (int k = 0; k < n; k++)
        {
            int i = _swapOverflow ? k : _swapSlots[k];
            _recSwapped[i] = false;
            if (_oTri[i] == IntPtr.Zero) continue;
            ref TexHandles hh = ref _dTexH[i];
            if (hh[0] == 0) continue;
            hh[0] = HandleFor(_recTid[i]);
            for (int s = 0; s < MapSlots; s++) hh[1 + s] = _recTidS[i * MapSlots + s] != 0 ? HandleFor(_recTidS[i * MapSlots + s]) : 0;
            _texHDirty = true;
            MarkRecordDirty(i);
        }
        _swapN = 0; _swapOverflow = false;
    }

    /// <summary>Raise the next visible-set rebuild to a FULL walk. <c>RouteWanted</c> re-resolves a slot
    /// marked <see cref="RouteGate.TexStale"/> only once the visible set VISITS it, and the index-driven set
    /// holds just the gob slots and the grid's frustum hits -- so an off-frustum static slot would keep a
    /// zeroed handle block the caster bake then samples as fully opaque. One O(live) scan per retire event.</summary>
    internal static void NoteTexStalePending() => _texStalePending = true;
    private static volatile bool _texStalePending;

    /// <summary>The one funnel every setter of <see cref="RouteGate.TexStale"/> goes through: the texture MARK
    /// and the texture MEMO move together, always. None of the marking paths is a registry event, so marking
    /// without zeroing <see cref="_recTexStamp"/> leaves <c>StageDbPart</c>'s resolve skipped -- the mark never
    /// clears, the retired name or zeroed handle stays bound and the slot force-routes for the session.</summary>
    private static void MarkTexStale(int i) { _gate[i].TexStale = true; _recTexStamp[i] = 0; }

    /// <summary>Mark every record that minted a handle on <paramref name="name"/>. Cold (once per retired
    /// name), MaxOwned x 8 int compares.</summary>
    internal static void NoteOwnedNameRetired(uint name)
    {
        NoteTexStalePending();
        int n = (int)name;
        for (int i = 0; i < _ownCount; i++)
        {
            if (_recTid[i] == n) { MarkTexStale(i); continue; }
            for (int s = 0; s < MapSlots; s++)
                if (_recTidS[i * MapSlots + s] == n) { MarkTexStale(i); break; }
        }
    }
    /// <summary>The Drain-time form: every name about to lose its handle, marked in one scan. A swapped
    /// record already holds its new name, so it matches nothing here.
    /// ponytail: names past the mark table fall back to the epoch bump; GL names are small ints in practice.</summary>
    internal static void NoteOwnedNamesRetired(List<uint> names, HashSet<uint> handles)
    {
        if (names.Count == 0 && handles.Count == 0) return;
        NoteTexStalePending();
        foreach (uint n in names) if (n >= _nameMark.Length) { InvalidateRecords(); return; }
        foreach (uint n in handles) if (n >= _nameMark.Length) { InvalidateRecords(); return; }
        foreach (uint n in names) _nameMark[n] = 1;
        foreach (uint n in handles) _nameMark[n] = 1;
        for (int i = 0; i < _ownCount; i++)
            {
                int t = _recTid[i];
                if ((uint)t < _nameMark.Length && _nameMark[t] != 0) { MarkTexStale(i); continue; }
                for (int k = 0; k < MapSlots; k++)
                {
                    t = _recTidS[i * MapSlots + k];
                    if ((uint)t < _nameMark.Length && _nameMark[t] != 0) { MarkTexStale(i); break; }
                }
            }
        foreach (uint n in names) _nameMark[n] = 0;
        foreach (uint n in handles) _nameMark[n] = 0;
    }
    /// <summary>A handle that just died must leave every record block NOW, not at the next colour build: the
    /// caster bake samples the GPU handle table off a STANDING staging, and a sample through a dead handle is
    /// a GPU page fault and a hard reset on radeonsi, silent garbage on NVIDIA. Reached from the engine-name
    /// cache's <see cref="DropHandle(uint, bool)"/> and from <c>TexRegistry.Drain</c>, inside the bracket.</summary>
    internal static void ZeroHandle(ulong v)
    {
        if (v == 0) return;
        NoteTexStalePending();
        for (int j = 0; j < _ownCount; j++)
            for (int s = 0; s < TexHandles.MaterialSlots; s++)
            {
                if (_dTexH[j][s] != v) continue;
                _dTexH[j][s] = 0;
                // the full re-resolve, not ApplySwaps: that one skips a block whose slot 0 reads 0 as "unlit"
                MarkTexStale(j);
                MarkRecordDirty(j);
                _texHDirty = true;
                HandlesZeroed++;
            }
    }
    /// <summary>Handle table entries zeroed under a dying handle (the hazard count), and bakes that found
    /// handle changes the colour bracket had not uploaded yet (the window count). Both on the [casters] line.</summary>
    internal static long HandlesZeroed, BakeHandleUploads;
    /// <summary>The handle table changed since its last upload. Apart from the shared record dirty list,
    /// which movers touch every frame -- the bake must not pay a handle upload for a matrix write.</summary>
    internal static bool _texHDirty;
    private static readonly byte[] _nameMark = new byte[1 << 20];
    private static readonly int[] _recTidS = new int[MaxOwned * MapSlots];
    private static readonly int[] _recSlotMask = new int[MaxOwned];
    /// <summary>How many record slots the persistent GPU buffer holds for OWNED parts, the sky's
    /// reserved block sitting immediately above them. Grows to the high-water ownership count and never
    /// shrinks; a growth reallocates, so the whole range is re-sent on that frame.</summary>
    internal static int _recOwnCap;
    /// <summary>The dirty list overflowed (or the buffer was just reallocated) -- send the whole
    /// range instead of naming slots. Correct, and merely more expensive than the incremental path.</summary>
    internal static bool _recDirtyAll = true;
    /// <summary>GL texture NAME -&gt; resident bindless handle, open-addressed. These are Alloy-OWNED names and
    /// nothing but <c>OwnedTextures</c> creates or destroys one, so the cache is invalidated EXACTLY when its
    /// texture is retired (<c>OwnedTextures.Drain</c> -&gt; <c>DropHandle</c>) and there is no enrollment wipe
    /// while ownedtex is armed. Unarmed, engine names populate it again and the wipe is the only heal.</summary>
    internal const int HandleHashBits = 15;
    internal static readonly int[] _hTid = new int[1 << HandleHashBits];
    internal static readonly ulong[] _hVal = new ulong[1 << HandleHashBits];
    /// <summary>Did WE make <see cref="_hVal"/>[i] resident? The driver cannot be asked, and guessing
    /// produces GL_INVALID_OPERATION in both directions -- residency is only ever ours to know.</summary>
    internal static readonly bool[] _hRes = new bool[1 << HandleHashBits];
    /// <summary>Is the bindless path live? Extension present, entry points resolved, not vetoed by
    /// alloy.bindless=off, and the LIT program's actual sources carry the handle block. When false everything
    /// draws exactly as before -- per-batch texture binds.</summary>
    internal static bool _bindless;
    internal static bool _bindlessProbed, _limitsProbed;
    /// <summary>This pass's draw order, rebuilt every pass from <see cref="_order"/> -- the opaque set
    /// first, then the blended members sorted back-to-front, then additive. It is a separate array from
    /// _order because the membership is per FRAME (a fading tile joins the blended run for the frames it
    /// fades) and the blended order is per PASS (it depends on the camera).</summary>
    private static readonly int[] _pass = new int[MaxOwned];
    /// <summary>...and each entry's class + alpha multiplier for this pass, since neither is
    /// <c>_oCls[i]</c> alone once the fade overlay applies.</summary>
    private static readonly byte[] _passCls = new byte[MaxOwned];
    private static readonly float[] _passAlpha = new float[MaxOwned];
    /// <summary>...and, for the opaque classes only, whether the keyhole dissolves the entry this
    /// frame -- chased at route so the class run can be partitioned on it (see <see cref="RouteParts"/>).</summary>
    private static readonly bool[] _passDis = new bool[MaxOwned];
    /// <summary>The route's alpha-to-coverage cutouts and their dissolve bit, held until the cutout run is out.</summary>
    private static readonly int[] _a2cTmp = new int[MaxOwned];
    private static readonly bool[] _a2cDis = new bool[MaxOwned];
    /// <summary>The draw-arg cache -- index count, byte offset and base vertex as read off the VDO item
    /// tables the last time this slot staged. Valid while stamped with the live epoch AND the stamp survives
    /// <c>UnstampRecord</c>, which fires on every re-tenant and drifted keep -- the only edges that re-point
    /// <c>_oVdo</c>/<c>_oItem</c>. Repack revokes before the tables move; a freed VDO kills its parts first.</summary>
    private static readonly int[] _cIdx = new int[MaxOwned];
    private static readonly nint[] _cOff = new nint[MaxOwned];
    private static readonly int[] _cBase = new int[MaxOwned];
    /// <summary>Blended candidates of this pass and their squared distance from the eye, before the sort.</summary>
    private static readonly int[] _blend = new int[MaxOwned];
    private static readonly float[] _blendKey = new float[MaxOwned];
    private static readonly float[] _blendAlpha = new float[MaxOwned];
    internal static readonly int[] _gStart = new int[MaxOwned];
    internal static readonly int[] _gLen = new int[MaxOwned];
    internal static readonly int[] _gVao = new int[MaxOwned];
    internal static readonly int[] _gTex = new int[MaxOwned];
    /// <summary>The live tids of slots 1..5 for this batch (0 = nothing bound there). Part of the batch
    /// KEY, not just of the bind set -- a batch that grouped on the diffuse alone would render every member
    /// under the first member's normal and specular maps.</summary>
    internal static readonly int[] _gTexN = new int[MaxOwned * MapSlots];
    internal static readonly uint[] _gMode = new uint[MaxOwned];
    internal static readonly bool[] _gLit = new bool[MaxOwned];
    /// <summary>The batch's <see cref="ShaderFeature"/>. Read by the depth prepass, which skips a batch
    /// whose feature displaces vertices.</summary>
    internal static readonly int[] _gFeat = new int[MaxOwned];
    /// <summary>Does this batch camera-dissolve? A key term for one reason only -- the keyhole
    /// fragment stage <c>discard</c>s, and the depth prepass has no per-draw granularity, so a dissolving
    /// batch has to be droppable whole. Prepass depth for a wall the colour pass punches through would cull
    /// the room behind it.</summary>
    internal static readonly bool[] _gDis = new bool[MaxOwned];
    /// <summary>The batch is an alpha-to-coverage cutout (<see cref="_oA2c"/>); these close the cutout range.</summary>
    internal static readonly bool[] _gA2c = new bool[MaxOwned];
    /// <summary>Did the depth prepass ACTUALLY draw this batch? Set by
    /// <c>DepthPrepassPass</c> as it walks, not predicted from the class -- the SSAO consumer gate was a
    /// second copy of the prepass's skip conditions and drifted from it (a door read "in the depth" while
    /// the buffer plainly saw through it). The prepass is the only thing that knows.</summary>
    internal static readonly bool[] _gPrepassed = new bool[MaxOwned];
    /// <summary>The batch's class and blend pair. In the key because they ARE the GL state a multidraw
    /// cannot vary inside: depth mask, blend function and the alpha-discard threshold.</summary>
    internal static readonly byte[] _gCls = new byte[MaxOwned];
    internal static readonly byte[] _gSrc = new byte[MaxOwned], _gDst = new byte[MaxOwned];
    /// <summary>This batch draws with face culling off. A batch key term rather than a per-draw one:
    /// culling is fixed-function GL state and one multidraw cannot vary it.</summary>
    internal static readonly bool[] _gNoCull = new bool[MaxOwned];
    /// <summary>The run gob of a blended batch (zero for gobless geometry): consecutive batches with the same
    /// non-zero tag are one creature, prepassed and coloured together so its parts read at one alpha while
    /// two creatures still composite through each other.</summary>
    internal static readonly IntPtr[] _gGob = new IntPtr[MaxOwned];
    /// <summary>Where the opaque set ends and where the blended run ends, in batch indices. Each pass
    /// draws exactly its own range -- the depth prepass and the probe capture take the opaque prefix.</summary>
    internal static int _gOpaqueEnd, _gBlendEnd, _gWaterEnd;
    /// <summary>Where the ground decals that open the blended run end: <see cref="_gOpaqueEnd"/> when there are none.</summary>
    internal static int DecalEnd()
    {
        int g = _gOpaqueEnd;
        while (g < _gBlendEnd && _gDecal[g]) g++;
        return g;
    }
    /// <summary>Where the cutout run begins inside the opaque set, so the opaque pass can time the two
    /// classes apart. Equals <see cref="_gOpaqueEnd"/> when nothing staged as cutout.</summary>
    private static int _gCutoutStart;
    /// <summary>Where the alpha-to-coverage batches begin inside the cutout run; <see cref="_gOpaqueEnd"/> when none.</summary>
    internal static int _gA2cStart;
    internal static int _drawN;
    // #125: the sky's own draw list, staged after the batch records and issued one draw at a time -- a
    // skybox is a handful of parts and each carries its own captured matrix, so there is nothing to group.
    internal const int MaxSkyDraws = 1024;
    private static readonly int[] _skVao = new int[MaxSkyDraws];
    /// <summary>The GL VAO NAME a DB sky draw binds, 0 when the draw is the engine path's and
    /// <see cref="_skVao"/>'s index into the harvest cache is the answer. The arena hands out a name
    /// directly, exactly as <c>Beams.EnrollDb</c> takes one.</summary>
    private static readonly uint[] _skVaoName = new uint[MaxSkyDraws];
    private static readonly uint[] _skMode = new uint[MaxSkyDraws];
    private static readonly int[] _skSrc = new int[MaxSkyDraws], _skDst = new int[MaxSkyDraws];
    /// <summary>The constant colour <c>PartTriMesh::Mat</c> would have set for this sky part -- white,
    /// or the scene's fog colour where the gob carries the +0x19 tint byte. Three floats per draw.</summary>
    private static readonly Vector3[] _skTint = new Vector3[MaxSkyDraws];
    /// <summary>The role's alpha, as the engine's <c>SetColor4f</c> fourth component: the plain unlit stage
    /// reads it off the draw record, a hosted stage from <c>globalColor</c>, so it is carried here once for
    /// both.</summary>
    private static readonly float[] _skAlpha = new float[MaxSkyDraws];
    /// <summary>The program this sky draw is issued with -- 0 for the plain unlit stage, else the Alloy
    /// program <see cref="SkyStage"/> compiled from the material's own custom fragment stage.</summary>
    private static readonly uint[] _skProg = new uint[MaxSkyDraws];
    /// <summary>The material's texture objects on units 0..<see cref="MaxEngTex"/>-1: what a stock fragment
    /// stage declares before the maps an unlit sky cannot use. Unit 0 always, the rest only for a hosted
    /// stage -- and the ENGINE's own objects there, never an owned copy (see <see cref="SkyStage"/>).</summary>
    internal const int MaxEngTex = 4;
    private static readonly SkyTexUnits[] _skTex = new SkyTexUnits[MaxSkyDraws];
    private static int _skyFirst, _skyN;
    internal static uint _drawBuf;
    /// <summary>The per-draw texture-handle SSBO (binding <see cref="BindTexH"/>).</summary>
    internal static uint _texHBuf;
    /// <summary>How many records the GPU side of <see cref="_drawBuf"/>/<see cref="_texHBuf"/> currently
    /// holds. A capacity growth reallocates both, which is why it forces a wholesale re-send.</summary>
    internal static int _recCapGpu;
    /// <summary>The bone block and the CPU stage behind it. One region, rewritten every colour pass. The
    /// stage is native and GROWS (<c>EnsureBoneSlots</c>) rather than dropping a crowd past the budget;
    /// <c>Counters._boneHigh</c> is the per-report high-water mark that keeps the ceiling counted.</summary>
    internal static uint _boneBuf;
    internal static BonePalette* _boneStage;
    internal static int _boneSlots, _boneCap;
    /// <summary>Last frame's palette, slot for slot beside <see cref="_boneStage"/> (the two swap
    /// once a frame), for the mover sweep's previous clip position. Streamed only when a skinned mover draws.</summary>
    internal static BonePalette* _bonePrevStage;
    /// <summary>The frame a posed record's palette last differed from the one before it -- the
    /// skinned twin of <c>_recMovedAt</c>, read by the mover pick.</summary>
    private static readonly int[] _poseMovedAt = new int[MaxOwned];
    private static int _poseRollSeq = -1;
    /// <summary><c>ownskin</c> not 0. False refuses the skinmesh class at enrollment, which means every
    /// cloak, robe and tail is NOT DRAWN -- not drawn by the engine either.</summary>
    internal static bool _skinning;
    /// <summary>Is the skinning path armed at all? A feature that needs a bone palette asks this and stands
    /// itself down loudly when it is off, rather than publishing into a block nothing uploads.</summary>
    internal static bool Skinning => _skinning;
    /// <summary>Off-device only: a self-check that exercises a palette path arms it, since no renderer
    /// Initialize has run to read <c>ownskin</c>. Returns what it was, for the restore.</summary>
    internal static bool SelfCheckSkinning(bool on)
    {
        bool was = _skinning;
        _skinning = on;
        return was;
    }
}
