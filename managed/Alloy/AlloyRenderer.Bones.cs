using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Db;
using CX.Engine;
using CX.Framework;
using CX.Graphics;

namespace CX.Alloy;

/// <summary>The bone block: one slot per skinmesh, allocated from a free list, with every pose published once at
/// the shadow bracket so the caster bake and the colour pass read the same frame's skin.</summary>
public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Publish every skinmesh's pose for THIS frame once, at the shadow bracket (CPU half here,
    /// upload half in <see cref="ShadowPoseUpload"/>), so the colour preamble reuses it. A bake sampling the
    /// block the LAST colour build uploaded -- pose N-1 under a pass drawing pose N -- flips the self-shadow
    /// compare's sign every frame on a cloak lit by its own carried torch, which is the flicker.</summary>
    internal static bool PosedByShadow;

    internal static void ShadowPose()
    {
        if (!AlloyGate.Alloy) return;
        long tPose = FrameZones.Now;
        PoseSkinned();
        FrameZones.Add(FrameZones.Zone.Pose, tPose);
        PosedByShadow = true;
    }

    /// <summary>The GL half of <see cref="ShadowPose"/>: push the solved block. Must run inside the 4.6
    /// bracket.</summary>
    internal static void ShadowPoseUpload()
    {
        if (PosedByShadow) Issue.UploadBones();
    }

    /// <summary>Bone slots are STICKY -- a part keeps its slot in the bone block across frames, mirroring the
    /// ownership table's stable-slot scheme. Load-bearing for the shadow-bracket pose: the bake pairs the LAST
    /// colour build's records (whose feat.y bone bases name last frame's layout) with THIS frame's block, so a
    /// slot that shifted under a survivor would bake it with another creature's bones.</summary>
    private static readonly int[] _boneFree = new int[MaxBoneSlots];
    private static int _boneFreeN, _boneTop;
    private static int[] _boneOwner = new int[MaxBoneSlots];   // ownership slot holding each bone slot, -1 free

    private static int AllocBoneSlot(int owner)
    {
        int s;
        if (_boneFreeN > 0) s = _boneFree[--_boneFreeN];
        else
        {
            if (!EnsureBoneSlots(_boneTop + 1)) return -1;
            s = _boneTop++;
            if (s >= _boneOwner.Length) Array.Resize(ref _boneOwner, _boneCap);
        }
        _boneOwner[s] = owner;
        return s;
    }

    private static void FreeBoneSlot(int s)
    {
        if (s < 0 || s >= _boneTop || _boneOwner[s] < 0) return;
        _boneOwner[s] = -1;
        if (_boneFreeN < _boneFree.Length) _boneFree[_boneFreeN++] = s;
    }

    private static void PoseSkinned()
    {
        // Nothing to pose for if the pass cannot draw: DrawOwned bails on these two without stamping a
        // single part, so the engine walks every one of them itself -- and would then solve each pose a
        // second time.
        if (!_skinning || Programs._progFailed || _batchUnavailable) { _boneSlots = 0; return; }
        // CX-26: once a FRAME -- a second pose in the same frame (a reflection subframe) re-solves the same
        // animation state and must not make it its own history.
        bool roll = _poseRollSeq != _frameSeq;
        if (roll) { _poseRollSeq = _frameSeq; BonePalette* t = _boneStage; _boneStage = _bonePrevStage; _bonePrevStage = t; }
        // CX-120 seam-anim: a DB palette is solved on the game thread, before this swap, so it lives in the
        // DB stage and is copied into whichever buffer THIS frame uploads -- here, the one moment both
        // buffers mean what their names say (Scene/AlloyRenderer.DbBones.cs).
        DbBonePublish();
        for (int k = 0; k < _skinCount; k++)
        {
            int i = _skinOrder[k];
            int prev = _oBone[i];
            _oBone[i] = -1;
            // A slot's palette is a column seam-anim's pose step wrote on the game thread, so there is no part
            // chain to chase -- the slot carries the bone base it was given. A part whose pose step has not run
            // yet draws rigid, which is its rest pose, and `skin-nopose` on the period line says how often.
            int given = SceneDb.PartBoneBase[_oDbPart[i]];
            // Both are ABSOLUTE bone indices: the slot they name is what a reclaim could free, and a DB slot
            // refuses that by owner anyway (it is seam-anim's to release).
            if (given < 0) { Counters._skinNoPose++; if (prev >= 0) FreeBoneSlot(prev / BoneCount); continue; }
            if (prev >= 0 && prev != given) FreeBoneSlot(prev / BoneCount);
            _oBone[i] = given;
            Counters._skinDrawn++;
        }
        // Reclaim orphans: a table wipe can re-tenant an ownership slot without a FreeSlot
        // (a fresh tenant starts at _oBone -1), leaving a bone slot whose owner no longer
        // points back. _boneTop is the peak skinned population -- tiny -- so the sweep is a few compares.
        for (int s = 0; s < _boneTop; s++)
            if (_boneOwner[s] >= 0 && (_oBone[_boneOwner[s]] < 0 || _oBone[_boneOwner[s]] / BoneCount != s))
                FreeBoneSlot(s);
        // The block uploads the whole (holey) high water: stale floats in freed slots are never indexed,
        // and a contiguous re-pack is exactly the layout shift stickiness exists to prevent.
        _boneSlots = _boneTop;
        if (_boneTop > Counters._boneHigh) Counters._boneHigh = _boneTop;
    }

    /// <summary>Room for <paramref name="want"/> bone slots, doubling the stage rather than dropping a
    /// creature past a fixed budget. Native memory on purpose: the growth is rare but happens on the render
    /// thread, where a managed array of this size would hand the collector a gen-2 allocation. The catch is
    /// the point -- <c>NativeMemory.Realloc</c> THROWS rather than returning null, inside the present hook.</summary>
    private static bool EnsureBoneSlots(int want)
    {
        if (want <= _boneCap) return true;
        int cap = _boneCap == 0 ? MaxBoneSlots : _boneCap;
        while (cap < want) cap *= 2;
        BonePalette* next, prev;
        nuint bytes = (nuint)cap * (nuint)sizeof(BonePalette);
        try { next = (BonePalette*)NativeMemory.Realloc(_boneStage, bytes); }
        catch (OutOfMemoryException) { return false; }
        if (next == null) return false;
        _boneStage = next;
        try { prev = (BonePalette*)NativeMemory.Realloc(_bonePrevStage, bytes); }
        catch (OutOfMemoryException) { return false; }
        if (prev == null) return false;
        _bonePrevStage = prev; _boneCap = cap;
        return true;
    }
}
