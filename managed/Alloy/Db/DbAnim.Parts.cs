using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The part walk: what <c>Part::Animate</c> @0x49ad00 and its one real override are for. The engine
/// spends one indirect call per part to find the parts whose <c>Animate</c> is not the plain recursion, and
/// only <c>PartDanglyMesh::Animate</c> @0x49bbb0 overrides it meaningfully. Here the node's kind answers that
/// off a column, so an object with no dangly part costs one pass over one byte array.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Animate <paramref name="obj"/>'s parts: the dangly solve where a part has one, and nothing
    /// else. The REAL frame time, never a rate-reduced one -- a cloak sampled every other frame stutters where
    /// a limb pose does not.</summary>
    private static void PartWalk(DbId obj)
    {
        int i = obj.Index;
        // The tree fact, scanned once per part range instead of per step.
        if (!_hasDangly[i]) return;
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        // Past the far animation band the same thing, and counted: the joint solve costs what it SIMULATES, so
        // no rate can make it cheaper, and a whole swing that far off is a pixel of motion.
        bool far = RateRestsDangly(i);
        if (!Dangly.Enabled.V || far)
        {
            // Off is rigid: every dangly part goes back to its rest pose, not frozen in its last swing. Only
            // dangly ones -- an animmesh rides the same dynamic buffers and is not this switch's.
            for (int k = 0; k < n; k++)
            {
                if (!IsDanglyPart(first + k)) continue;
                DanglyDropRig(first + k);
                DanglyDropStock(first + k);
                if (far) _rate.Rigid++;
            }
            return;
        }
        if (n <= 0 || _danglyDt <= 0f) return;
        DbId sc = SceneDb.ObjScene[i];
        bool windy = SceneDb.Scenes.Valid(sc) && SceneDb.SceneIsWorld[sc.Index];
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            if (!SceneDb.PartEnabled[p] || SceneDb.PartMerged[p] || !IsDanglyPart(p)) continue;
            DanglyPart(p, _danglyDt, windy);
        }
        _dangly.Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
    }

    /// <summary>Is part <paramref name="p"/> a danglymesh node's?</summary>
    private static bool IsDanglyPart(int p)
    {
        ModelDef? m = SceneDb.PartModel[p];
        int node = SceneDb.PartNode[p];
        return m != null && (uint)node < (uint)m.Nodes.Length && m.Nodes[node].Kind == NodeKind.Dangly;
    }

    /// <summary>The frame time the part walk hands the dangly solve. Latched per scene animate, because the
    /// walk is reached from the per-object step and the solve must see one value for the whole frame.</summary>
    private static float _danglyDt;
}
