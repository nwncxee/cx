using CX.Engine;
using System;
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
    internal static float* DbBoneStage(int slot)
    {
        EnsureDbStage(slot + 1);
        return ((BoneRec*)_dbStage)[slot].F;
    }

    private static float* _dbStage, _dbPrev;
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
        nuint bytes = (nuint)cap * BoneFloats * sizeof(float);
        _dbStage = (float*)NativeMemory.Realloc(_dbStage, bytes);
        _dbPrev = (float*)NativeMemory.Realloc(_dbPrev, bytes);
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
            var now = new ReadOnlySpan<float>(((BoneRec*)_dbStage)[slot].F, BoneFloats);
            var prev = new Span<float>(((BoneRec*)_dbPrev)[slot].F, BoneFloats);
            // A FRESH slot's history is its own pose, or a creature's first frame smears from whatever the
            // slot held before it.
            if (_dbFresh.Remove(slot)) now.CopyTo(prev);
            prev.CopyTo(new Span<float>(((BoneRec*)_bonePrevStage)[slot].F, BoneFloats));
            now.CopyTo(new Span<float>(((BoneRec*)_boneStage)[slot].F, BoneFloats));
            now.CopyTo(prev);
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
