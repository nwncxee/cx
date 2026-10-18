using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Physics;

namespace CX.Alloy.Db;

/// <summary>Bone ranges for rigged dangly parts. A rig of two or three bones does not deserve a 64-matrix slot
/// of its own, so each slot is cut into power-of-two CELLS and a part names the ABSOLUTE bone index of its
/// own -- which is what <c>feat.y</c> is, an index into one flat palette. Owned here and released on every
/// edge the dynamic vertex buffers are.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Cell sizes, by log2: 2, 4, ... 64 bones.</summary>
    private const int DanglyCellClasses = 7;

    /// <summary>Which cell each part holds, the free cells per size class, the slots they were cut from, and
    /// the scratch one part's solver writes before the store. One owner, so a reset is one assignment.</summary>
    private sealed class DanglyBoneState
    {
        /// <summary>The part's absolute bone index, plus one; 0 = no range.</summary>
        internal readonly int[] At = new int[SceneDb.MaxParts];
        /// <summary>Bones the part's cell holds, 0 = none.</summary>
        internal readonly int[] Cell = new int[SceneDb.MaxParts];
        /// <summary>The part's largest excursion (<see cref="DanglyReach"/>), which the caster sphere's pose slack
        /// comes from.</summary>
        internal readonly float[] Reach = new float[SceneDb.MaxParts];
        /// <summary>Free cells, one list per size class.</summary>
        internal readonly List<int>[] Free = NewFree();
        /// <summary>The DB bone slots cut into cells, so a wholesale drop gives every one of them back.</summary>
        internal readonly List<int> Pages = new();
        /// <summary>Node-space bones, one part at a time, before the column-major store.</summary>
        internal readonly Matrix4x4[] Scratch = new Matrix4x4[BoneCount];
        internal int Live, High;
        /// <summary>The renderer asked for every range back (a lost context); read on the game thread, which
        /// is where they are owned.</summary>
        internal volatile bool Drop;

        private static List<int>[] NewFree()
        {
            var f = new List<int>[DanglyCellClasses];
            for (int i = 0; i < f.Length; i++) f[i] = new List<int>();
            return f;
        }
    }

    private static DanglyBoneState _danglyBones = new();

    /// <summary>The cell a rig of <paramref name="bones"/> bones takes: the smallest power of two of at least
    /// two that holds it, or 0 for a rig past a whole palette -- refused, never clipped.</summary>
    private static int DanglyCellFor(int bones)
    {
        if (bones <= 0 || bones > BoneCount) return 0;
        int cell = 2;
        while (cell < bones) cell <<= 1;
        return cell;
    }

    /// <summary>The rig's largest excursion in node units: the cage times <c>dangly.cage</c> for a rig whose
    /// bones translate, the chord its longest arm sweeps through the cone for one whose segments turn.</summary>
    private static float DanglyReach(DanglyRig rig)
    {
        if (!rig.Shear) return 2f * rig.Arm * MathF.Sin(0.5f * rig.Cone);
        float r = 0f;
        float[] cap = rig.JointCap;
        for (int j = 0; j < cap.Length; j++) if (cap[j] > r) r = cap[j];
        return r * Dangly.Cage.V;
    }

    /// <summary>The absolute bone index of <paramref name="part"/>'s cell, or -1 when the rig is bigger than a
    /// palette or the renderer's bone stage will not grow. Idempotent: a part already holding a cell of the
    /// right size keeps it, which is what makes this safe to call every frame.</summary>
    private static int DanglyBoneAlloc(int part, int bones, float reach)
    {
        DanglyBoneState st = _danglyBones;
        int cell = DanglyCellFor(bones);
        if (cell == 0) return -1;
        st.Reach[part] = reach;
        if (st.Cell[part] == cell) return st.At[part] - 1;
        if (st.Cell[part] != 0) DanglyBoneFree(part);

        List<int> free = st.Free[System.Numerics.BitOperations.Log2((uint)cell)];
        if (free.Count == 0)
        {
            int slot = AlloyRenderer.DbAllocBone();
            if (slot < 0) return -1;
            st.Pages.Add(slot);
            // Pushed DESCENDING, so the pops walk the page from its front: a fresh page's first cell is its
            // slot's bone 0, which is what the packing self-check reads.
            for (int off = BoneCount - cell; off >= 0; off -= cell) free.Add(slot * BoneCount + off);
        }
        int at = free[^1];
        Span<Matrix4x4> dst = AlloyRenderer.DbBoneCell(at, cell);
        if (dst.Length != cell) return -1;
        free.RemoveAt(free.Count - 1);
        st.At[part] = at + 1;
        st.Cell[part] = cell;
        if (++st.Live > st.High) st.High = st.Live;
        // A cell the last tenant posed still holds its matrices, and a bone the rig never writes has to mean
        // the rest pose.
        dst.Fill(Matrix4x4.Identity);
        return at;
    }

    /// <summary>Give <paramref name="part"/>'s cell back. The PAGE stays claimed: a DB bone slot is sticky by
    /// design -- the shadow bake pairs the last build's bone bases with this frame's block -- and the next rig
    /// of that size takes the cell.</summary>
    private static void DanglyBoneFree(int part)
    {
        DanglyBoneState st = _danglyBones;
        int cell = st.Cell[part];
        if (cell == 0) return;
        st.Free[System.Numerics.BitOperations.Log2((uint)cell)].Add(st.At[part] - 1);
        st.At[part] = 0;
        st.Cell[part] = 0;
        st.Reach[part] = 0f;
        st.Live--;
        SceneDb.PartBoneBase[part] = -1;
    }

    /// <summary>Write <paramref name="bones"/> (node space, row-vector) into the cell at absolute index
    /// <paramref name="at"/>, in the column-major form the vertex stage reads. Bones the rig does not name keep
    /// the identity the allocation left there.</summary>
    private static void DanglyWriteBones(int at, int cell, ReadOnlySpan<Matrix4x4> bones)
    {
        Span<Matrix4x4> dst = AlloyRenderer.DbBoneCell(at, cell);
        int n = Math.Min(dst.Length, bones.Length);
        for (int b = 0; b < n; b++) dst[b] = Store(bones[b]);
    }

    /// <summary>Release the bone range and the joint state of every part of <paramref name="first"/>'s range:
    /// the part records are about to be re-tenanted or freed, so nothing may hold a cell keyed on them.</summary>
    private static void DanglyBonesForget(int first, int n)
    {
        for (int p = first; p < first + n; p++)
        {
            DanglyBoneFree(p);
            _dangly.Joints[p] = null;
            _dangly.Walls[p] = null;
            _dangly.Stock[p] = null;
        }
    }

    /// <summary>A part record is being re-tenanted (<c>DbFeed.InitPart</c>), possibly while still seated: the bone
    /// cell, the dynamic buffer and the solver state it held were the old tenant's.</summary>
    internal static void DanglyPartRecycled(int p)
    {
        if ((uint)p >= (uint)SceneDb.MaxParts) return;
        DanglyDropRig(p);
        DanglyDropStock(p);
    }

    /// <summary>Drop one part's rigged state, if it has any. Guarded, because the dispatch asks every frame
    /// for every part on the other path.</summary>
    private static void DanglyDropRig(int part)
    {
        if (_danglyBones.Cell[part] == 0 && _dangly.Joints[part] == null && _dangly.Walls[part] == null) return;
        DanglyBoneFree(part);
        _dangly.Joints[part] = null;
        _dangly.Walls[part] = null;
    }

    /// <summary>...and one part's stock state: its dynamic vertex buffer, its private arena range and the
    /// private recipe that draws it.</summary>
    private static void DanglyDropStock(int part)
    {
        if (_dyn[part] == 0 && _dangly.Stock[part] == null) return;
        DynDrop(part);
        _dangly.Stock[part] = null;
    }

    /// <summary>The pose slack a rigged dangly part's caster sphere needs: its rig's reach, which is how far
    /// from the rest pose the solver may take it. 0 for a part with no bone range.</summary>
    internal static float DanglyCasterSlack(int part)
        => (uint)part < (uint)SceneDb.MaxParts && _danglyBones.Cell[part] != 0
         ? _danglyBones.Reach[part] : 0f;

    /// <summary>The renderer's context-loss slot: ask for every range back. Nothing is freed here -- this runs
    /// on the render thread and the ranges are the game thread's.</summary>
    internal static void DanglyBonesContextLost() => _danglyBones.Drop = true;

    /// <summary>Game thread: take the renderer up on it. The whole owner goes, so the next frame re-allocates
    /// rather than publishing into a cell nothing claims.</summary>
    private static void DanglyBonesPoll()
    {
        DanglyBoneState st = _danglyBones;
        if (!st.Drop) return;
        for (int k = 0; k < st.Pages.Count; k++) AlloyRenderer.DbFreeBone(st.Pages[k]);
        for (int p = 0; p < SceneDb.MaxParts; p++)
        {
            if (st.Cell[p] == 0) continue;
            SceneDb.PartBoneBase[p] = -1;
            _dangly.Joints[p] = null;
        }
        _danglyBones = new DanglyBoneState();
    }
}
