using CX.Alloy.Db;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The ownership slot table's plumbing: the stable slot allocator the database seat and release use,
/// the world edge's GL-side teardown, and the radix order the batch cut walks. Cold: each runs on an edge.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Retire a slot WITHOUT touching its entry's OwnFlag or the revoke counter. The enrollment
    /// sweeps need this variant: a PartTable entry can change hands between enrollments, so by sweep time
    /// <c>_oEntry[i]</c> may name an entry a DIFFERENT slot claimed this scan, and clearing its flag would
    /// un-suppress a part Alloy is about to draw.</summary>
    private static void FreeSlot(int i)
    {
        // a vacated slot carries no scene-database tenant either, whichever mode freed it.
        _oDbPart[i] = -1; _oDbRecipe[i] = -1;
        _oTri[i] = IntPtr.Zero;
        // PoseSkinned only walks the skinned list, which drops this slot at the next rebuild
        int b = _oBone[i];
        if ((uint)b < (uint)_boneTop && _boneOwner[b] == i) FreeBoneSlot(b);
        _oBone[i] = -1;
        SetCastOk(i, false);   // #328: a vacated slot casts nothing, and the shadow side re-collects on it
        LightmapBake.Vacate(i);   // #674: its bake record reads unbaked
        _ownLive--;
        _freeSlots[_freeCount++] = i;
    }

    /// <summary>Stable ownership slots: a part KEEPS its slot while it lives, because the slot index is the
    /// identity every persistent slot-keyed table hangs off (per-draw records, sphere tables, shadow tenancy
    /// stamps, cull buffers).</summary>
    private static readonly int[] _freeSlots = new int[MaxOwned];
    private static int _freeCount, _ownLive;

    /// <summary>A newcomer's slot: a vacated one if any sweep left one, the next high-water slot otherwise.
    /// Never overflows past MaxOwned -- the capacity gate refuses on the LIVE count, and every slot below the
    /// high-water mark is either live or on the free list. Only slots below <see cref="_freeAvail"/> are
    /// offered, so a slot freed mid-walk is not re-tenanted by the same walk; the hole is filled from the top.</summary>
    private static int AllocSlot()
    {
        if (_freeAvail == 0) return _ownCount++;
        int s = _freeSlots[--_freeAvail];
        _freeSlots[_freeAvail] = _freeSlots[--_freeCount];
        return s;
    }

    /// <summary>The free-list floor a walk snapshots at its begin -- see <see cref="AllocSlot"/>.</summary>
    private static int _freeAvail;

    /// <summary>The free-list floor, checked once at Initialize on the empty table: a walk begins over two
    /// vacated slots, a death mid-walk frees a third, and only the first two may be handed out.</summary>
    internal static void EnrollSelfCheck(ExtenderLog log)
    {
        _freeSlots[0] = 7; _freeSlots[1] = 8; _freeCount = 2; _freeAvail = 2;
        _freeSlots[_freeCount++] = 9;
        bool ok = AllocSlot() == 8 && AllocSlot() == 7 && AllocSlot() == _ownCount - 1
                  && _freeCount == 1 && _freeSlots[0] == 9 && _freeAvail == 0;
        log.Info(ok ? "AlloyRenderer.EnrollSelfCheck: passed" : "AlloyRenderer.EnrollSelfCheck: FAILED -- a mid-walk vacated slot was re-offered inside the walk");
        _freeCount = 0; _freeAvail = 0; _ownCount = 0;

    }

    private static void RevokeAll(string why)
    {
        // above the empty-table guard, because the ownership count says what ALLOY took and nothing
        // about what the passes captured -- and for beams, particles and grass this walk is the only
        // teardown there is. See docs/owned-resource-lifetimes.md.
        Issue.WipeHandles();
        // above the guard for the same reason the registry walk is. The registry's Revoke deletes the
        // owned buffer copies, and the VAOs built over them are only invalidated here -- an enrollment that
        // took nothing (_ownCount == 0) with slots left from an earlier one would otherwise keep binding a
        // VAO whose buffers were just freed, permanently, with the map empty so no upload can re-copy them.
        PassRegistry.Global.Revoke();
        // The slot space belongs to the FEED: every slot is joined to a live part through SceneDb.PartSlot and is
        // released when the game destroys the part, never here. The device-side teardown above is all a revoke is.
        if (_ownCount > 0) _dbRevokeAll++;
    }

    /// <summary>The four-pass radix over every live slot: texture key, feature/primitive, VAO sort key, class.
    /// O(live) integer passes -- cheap enough to re-run after an incremental enrolment too.</summary>
    private static void RebuildOrder()
    {
        _litCount = 0; _skinCount = 0;
        Array.Clear(_sortCount, 0, MaxVdos * 4);
        Array.Clear(_texSortCount, 0, MaxMats);
        Array.Clear(_featPrimCount);
        Array.Clear(_clsCount); Array.Clear(_clsStart); Array.Clear(_clsEnd);
        for (int i = 0; i < _ownCount; i++)
        {
            if (_oTri[i] == IntPtr.Zero) continue;
            _sortCount[SortKey(i)]++;
            _texSortCount[_oTexKey[i]]++;
            _featPrimCount[FeatPrimKey(i)]++;
            _clsCount[_oCls[i]]++;
            if (_oLit[i]) _litCount++;
            if (DbSlot(i) && (DrawRecipe.Flags(_oDbRecipe[i]) & RecipeFlag.Skinned) != 0)
                _skinOrder[_skinCount++] = i;
        }
        // Four-digit LSD radix into _order, every digit counting-sorted (stable): texture, then
        // (feature, primitive), then (lit, no-cull bit, VDO), then class. The result walks
        // grouped by CLASS -- so each pass owns a contiguous batch range -- and inside a class by (lit, cull,
        // VDO, feature, primitive) with equal textures contiguous, which is the whole enrollment-static half
        // of the batch cut key. The draw loop then cuts a batch on a key change instead of searching for one;
        // the one per-frame term left, the dissolve bit, is RouteParts's partition.
        int at = 0;
        for (int k = 0; k < MaxMats; k++) { int n = _texSortCount[k]; _texSortCount[k] = at; at += n; }
        for (int i = 0; i < _ownCount; i++) if (_oTri[i] != IntPtr.Zero) _orderTmp[_texSortCount[_oTexKey[i]]++] = i;
        int live = at;
        at = 0;
        for (int k = 0; k < FeatPrimKeys; k++) { int n = _featPrimCount[k]; _featPrimCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _orderTmp[k]; _order[_featPrimCount[FeatPrimKey(i)]++] = i; }
        at = 0;
        for (int k = 0; k < MaxVdos * 4; k++) { int n = _sortCount[k]; _sortCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _order[k]; _orderTmp[_sortCount[SortKey(i)]++] = i; }
        at = 0;
        for (int k = 0; k < AlloyClasses.Count; k++) { int n = _clsCount[k]; _clsStart[k] = at; _clsCount[k] = at; at += n; }
        for (int k = 0; k < live; k++) { int i = _orderTmp[k]; _order[_clsCount[_oCls[i]]++] = i; }
        // ...and the cursor each class's fill left behind IS its end index.
        for (int k = 0; k < AlloyClasses.Count; k++) _clsEnd[k] = _clsCount[k];
        _orderCount = live;
        long tIdx = FrameZones.Now;
        // the area index rides the same edge. Enrolment is the only thing that changes which slot holds
        // which part, so the grid is rebuilt here and never maintained incrementally.
        BuildIndex();
        _churn.IndexTicks += FrameZones.Now - tIdx;
    }

    /// <summary>The middle radix digit: (lit, no-cull, VDO). The cull bit sits UNDER the lit one -- see
    /// <see cref="SortKeyOf"/> -- so inside a class the two-sided parts of each lighting mode run contiguously
    /// and <c>BuildBatches</c>' cut flips face culling at most twice without splitting a program's run.</summary>
    private static int SortKey(int i)
        => SortKeyOf(_oNoCull[i], _oLit[i], _oDbPart[i] >= 0 ? DbVaoKey(i) : _oVao[i]);

    /// <summary>The (feature, primitive) radix digit, under the same pose override the record takes
    /// (<see cref="ShaderFeatures.WithPose"/>). A fresh tenant is unposed at enrollment (its bone slot lands
    /// on the first pose after), so a skinmesh sorts under its content feature until the next enrollment --
    /// one extra cut, briefly.</summary>
    private static int FeatPrimKey(int i)
        => FeatPrimKeyOf(ShaderFeatures.WithPose((int)_oFeat[i * 4], _oBone[i] >= 0), _oPrim[i]);

    /// <summary>Mask bit of map slot <paramref name="s"/>: its index, except for the AO slot -- see
    /// <see cref="AoBit"/> for why that one is out of line.</summary>
    internal static int SlotBit(int s) => s == AoSlot ? AoBit : 1 << s;

    /// <summary>Case-insensitive ASCII compare of a MaterialParameter's name field against a literal, over the
    /// 0x40 bytes the engine's own <c>strncasecmp</c> compares. Allocation-free, and cold anyway. Internal
    /// because <see cref="Particles"/> reads its own <c>.mtr</c> parameter off the same record layout, and two
    /// spellings of "compare a material parameter name" is two answers to a case-folding question.</summary>
    internal static bool NameIs(byte* rec, string name)
    {
        for (int i = 0; i < name.Length; i++)
        {
            byte a = rec[i], b = (byte)name[i];
            if (a >= 'A' && a <= 'Z') a += 32;
            if (b >= 'A' && b <= 'Z') b += 32;
            if (a != b) return false;
        }
        return rec[name.Length] == 0;
    }


}
