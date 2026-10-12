using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The part columns a <c>.mtr</c> seeds, and the parts still waiting for one. A material can land in
/// the middle of gameplay -- a creature spawns and its armour names a file no pump had reached -- so the
/// re-seed visits the parts that MISSED and never the part table.</summary>
public static partial class PartTree
{
    /// <summary>Parts built while the resource layer had not answered for their mesh's material: a drained
    /// list, so a landing edge costs the waiting parts and nothing else.</summary>
    private sealed class MtrWaitState
    {
        /// <summary>The waiting part slots, each with the handle it held when it was enqueued: a slot
        /// RECYCLED since names another tenant, whose material must not be written from this entry.</summary>
        public readonly int[] Part = new int[Cap];
        public readonly uint[] Id = new uint[Cap];
        /// <summary>On the list already, so a second <see cref="InitMaterial"/> for the same slot (a rebuilt
        /// tree) does not enqueue it twice.</summary>
        public readonly bool[] Queued = new bool[SceneDb.MaxParts];
        public int N, Seeded, Stale, Overflow;
        /// <summary>The list overflowed, so the next edge sweeps the whole part table ONCE instead. Slower,
        /// and never a part left holding provisional material columns.</summary>
        public bool Sweep;
    }

    /// <summary>Parts that may wait at once. Only a part whose material is UNANSWERED gets here, which in
    /// practice is a reference sub-model's or one built between a queued ask and the next pump.</summary>
    private const int Cap = 1 << 13;

    private static MtrWaitState _mtrWait = new();

    /// <summary>Parts waiting on a <c>.mtr</c> right now. The self-check's witness that an answered-absent
    /// material enqueues nothing.</summary>
    internal static int MaterialWaiting => _mtrWait.N;

    /// <summary>Note that part <paramref name="p"/>'s material columns are PROVISIONAL: its mesh names a
    /// material the layer has not answered for, so the answer may still arrive.</summary>
    private static void WaitForMaterial(int p)
    {
        MtrWaitState w = _mtrWait;
        if (w.Queued[p]) return;
        if (w.N >= Cap)
        {
            w.Overflow++;
            if (!w.Sweep)
                _log?.Always($"PartTree: more than {Cap} parts are waiting on a .mtr -- the next material "
                           + "read sweeps the whole part table instead of their list");
            w.Sweep = true;
            return;
        }
        w.Part[w.N] = p;
        w.Id[w.N] = SceneDb.Parts.IdAt(p).Value;
        w.N++;
        w.Queued[p] = true;
    }

    /// <summary>A <c>.mtr</c> the resource layer read after a part was built: <see cref="InitMaterial"/> took
    /// the part's material pointer and its render hint from a file that was not there, and nothing else
    /// re-asks. Re-seeds exactly those two over the WAITING parts and marks each, which is what rewrites its
    /// record and re-resolves its texture slots. Driven by the landing edge, from the frame's fold.</summary>
    internal static int ReseedMaterials()
    {
        if (!DrawRecipe.TakePartMaterialEdge()) return 0;
        MtrWaitState w = _mtrWait;
        int n = w.Sweep ? SweepAll(w) : Drain(w);
        w.Seeded += n;
        if (n > 0) _log?.Info($"PartTree: {n} part(s) folded in a .mtr that was read after they were built"
                            + $" ({w.N} still waiting)");
        return n;
    }

    /// <summary>The waiting list, visited once: a part that took its material leaves, one whose material is
    /// still unanswered stays, and one whose slot was recycled or whose answer was "no such file" is dropped
    /// -- there is nothing left to come for either.</summary>
    private static int Drain(MtrWaitState w)
    {
        int n = 0, keep = 0;
        for (int k = 0; k < w.N; k++)
        {
            int p = w.Part[k];
            uint id = w.Id[k];
            w.Queued[p] = false;
            if (!SceneDb.Parts.LiveAt(p) || SceneDb.Parts.IdAt(p).Value != id) { w.Stale++; continue; }
            MeshDef? mesh = SceneDb.PartMeshDef[p];
            if (mesh == null) continue;
            if (Fold(p, mesh)) { n++; continue; }
            if (SceneDb.PartMtr[p] != null || !MtrDef.Pending(mesh)) continue;
            w.Part[keep] = p;
            w.Id[keep] = id;
            w.Queued[p] = true;
            keep++;
        }
        w.N = keep;
        return n;
    }

    /// <summary>The overflow path: every live part whose material is still unfolded, once, and the ones still
    /// waiting go back on the list -- which is authoritative again from here, or overflows again and says so.</summary>
    private static int SweepAll(MtrWaitState w)
    {
        int n = 0, high = SceneDb.Parts.High;
        w.N = 0;
        w.Sweep = false;
        System.Array.Clear(w.Queued);
        for (int p = 0; p < high; p++)
        {
            if (SceneDb.PartMtr[p] != null || !SceneDb.Parts.LiveAt(p)) continue;
            MeshDef? mesh = SceneDb.PartMeshDef[p];
            if (mesh == null) continue;
            if (Fold(p, mesh)) { n++; continue; }
            if (MtrDef.Pending(mesh)) WaitForMaterial(p);
        }
        return n;
    }

    /// <summary>Fold the material into part <paramref name="p"/>'s columns, if there is one to fold now.
    /// False leaves the part exactly as it was.</summary>
    private static bool Fold(int p, MeshDef mesh)
    {
        if (SceneDb.PartMtr[p] != null) return false;
        MtrDef? mtr = MtrDef.Of(mesh);
        if (mtr == null) return false;
        SceneDb.PartMtr[p] = mtr;
        if (mtr.RenderHint != 0)
        {
            PartMaterial m = SceneDb.PartMtl[p];
            m.RenderHint = mtr.RenderHint;
            SceneDb.PartMtl[p] = m;
        }
        // Dirty.Material is what rewrites the record AND marks the slot's texture memo, so the slots resolve
        // against the file's names rather than the mesh bitmap's.
        SceneDb.TouchPart(p, Dirty.Material);
        return true;
    }

    /// <summary>One clause for the period line: what the late-material path has had to do. Silent until a
    /// material has actually landed late, and loud about anything it could not hold.</summary>
    internal static string MaterialNote()
    {
        MtrWaitState w = _mtrWait;
        if (w.Seeded == 0 && w.N == 0 && w.Overflow == 0) return "";
        return $"late .mtr: {w.Seeded} part(s) folded, {w.N} waiting"
             + (w.Stale > 0 ? $", {w.Stale} slot(s) recycled first" : "")
             + (w.Overflow > 0 ? $", {w.Overflow} PAST THE WAIT LIST (swept)" : "");
    }
}
