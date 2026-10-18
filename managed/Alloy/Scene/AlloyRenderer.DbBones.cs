using CX.Engine;
using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy;

/// <summary>The bone-slot claim for database parts: the palette lives in the renderer's own bone stage, the
/// block <c>Issue.UploadBones</c> sends and <c>feat.y</c> indexes, with <c>SceneDb.PartBoneBase</c> carrying the
/// slot number. The owner is <see cref="DbBoneOwner"/>, below zero, so <c>FreeBoneSlot</c> and
/// <c>PoseSkinned</c>'s orphan sweep both skip it: sticky while the part holds it, as the shadow bake needs.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>The owner marker a database bone slot carries.</summary>
    private const int DbBoneOwner = -2;

    /// <summary>Claim a bone slot for a database part, or -1 when the stage cannot grow (the caller counts it
    /// and the part draws rigid, which is its rest pose).</summary>
    internal static int DbAllocBone()
    {
        int s = AllocBoneSlot(DbBoneOwner);
        if (s >= 0)
        {
            EnsureDbStage(s + 1);
            _dbLive.Add(s);
            _dbFresh.Add(s);
        }
        return s;
    }

    /// <summary>
    /// Where slot <paramref name="slot"/>'s 64 matrices live: a stage of this file's OWN, not the pair
    /// <c>PoseSkinned</c> ping-pongs, because the two run on different clocks -- a palette is solved on the
    /// GAME thread at the end of the animate step, and <c>PoseSkinned</c> swaps the pair once per frame after
    /// that. <see cref="DbBonePublish"/> copies from here into whichever buffer the frame is about to upload.
    /// </summary>
    internal static ref BonePalette DbBoneStage(int slot)
    {
        EnsureDbStage(slot + 1);
        return ref _dbStage[slot];
    }

    /// <summary>The <paramref name="count"/> matrices at ABSOLUTE bone index <paramref name="at"/> in the DB
    /// stage, empty when the stage will not grow that far. A rigged dangly part's cell is one run inside a
    /// slot; a whole skin's base is its slot's bone 0.</summary>
    internal static Span<Matrix4x4> DbBoneCell(int at, int count)
    {
        if (at < 0 || count <= 0) return default;
        EnsureDbStage(at / BoneCount + 1);
        return Cell(_dbStage, at, count);
    }

    /// <summary>Make the <paramref name="count"/> bones at <paramref name="at"/> their own history. The first
    /// frame a part publishes into a slot already in use must not smear from whatever that cell last held, and
    /// <see cref="_dbFresh"/> can only say that about a whole slot.</summary>
    internal static void DbBoneSeedPrev(int at, int count)
    {
        Span<Matrix4x4> now = DbBoneCell(at, count);
        Span<Matrix4x4> prev = Cell(_dbPrev, at, count);
        if (!now.IsEmpty && prev.Length == now.Length) now.CopyTo(prev);
    }

    /// <summary>One run of matrices inside <paramref name="stage"/>, bounded by the slot it starts in.</summary>
    private static Span<Matrix4x4> Cell(BonePalette* stage, int at, int count)
    {
        if (stage == null || at < 0 || count <= 0) return default;
        ref BonePalette p = ref stage[at / BoneCount];
        Span<Matrix4x4> all = p;
        int off = at % BoneCount;
        return all.Slice(off, Math.Min(count, BoneCount - off));
    }

    private static BonePalette* _dbStage, _dbPrev;
    private static int _dbStageSlots;
    private static readonly System.Collections.Generic.HashSet<int> _dbLive = new();
    /// <summary>Slots claimed but not yet published, whose motion-vector history is their own first pose.</summary>
    private static readonly System.Collections.Generic.HashSet<int> _dbFresh = new();

    /// <summary>Room for <paramref name="want"/> slots in the DB stage, doubling as the engine path's does.</summary>
    private static void EnsureDbStage(int want)
    {
        if (want <= _dbStageSlots) return;
        int cap = _dbStageSlots == 0 ? 16 : _dbStageSlots;
        while (cap < want) cap *= 2;
        nuint bytes = (nuint)cap * (nuint)sizeof(BonePalette);
        _dbStage = (BonePalette*)NativeMemory.Realloc(_dbStage, bytes);
        _dbPrev = (BonePalette*)NativeMemory.Realloc(_dbPrev, bytes);
        _dbStageSlots = cap;
    }

    /// <summary>
    /// Copy every live palette into the frame's upload buffer, and last frame's into the history one.
    /// Called from <c>PoseSkinned</c> immediately after its swap, which is the one moment both buffers mean
    /// what their names say.
    /// </summary>
    internal static void DbBonePublish()
    {
        if (_dbStage == null || _dbLive.Count == 0) return;
        foreach (int slot in _dbLive)
        {
            if (slot < 0 || slot >= _boneTop || !EnsureBoneSlots(slot + 1)) continue;
            ref BonePalette now = ref _dbStage[slot];
            ref BonePalette prev = ref _dbPrev[slot];
            // A FRESH slot's history is its own pose, or a creature's first frame smears from whatever the
            // slot held before it.
            if (_dbFresh.Remove(slot)) prev = now;
            _bonePrevStage[slot] = prev;
            _boneStage[slot] = now;
            prev = now;
        }
    }

    /// <summary>Give a database bone slot back. Does what <c>FreeBoneSlot</c> does, which refuses a negative
    /// owner on purpose: such a slot is only ever released from here.</summary>
    internal static void DbFreeBone(int slot)
    {
        if (slot < 0 || slot >= _boneTop || _boneOwner[slot] != DbBoneOwner) return;
        _dbLive.Remove(slot);
        _dbFresh.Remove(slot);
        _boneOwner[slot] = -1;
        if (_boneFreeN < _boneFree.Length) _boneFree[_boneFreeN++] = slot;
    }
}
