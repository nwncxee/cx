namespace CX.Alloy.Db;

/// <summary>The engine's part ENABLE gate, which the game never sets: <c>TreeSetGeometryFlags</c> @0x43e620,
/// run from <c>Gob::ProcessGob</c> @0x43e920, leaves <c>Part+0x34</c> as <c>old &amp;&amp; subtree-has-geometry</c>,
/// where only <c>PartTriMesh</c> and its subclasses count. Its five readers are draw, volume projection, draw
/// buckets, hit check and highlight -- the pose, light, emitter and bbox walks ignore it, and so must CX.</summary>
internal static class PartGate
{
    /// <summary>A composition's object count ceiling and the propagation's iteration ceiling. A humanoid is
    /// one base plus ~16 body parts; past this the gate leaves the composition alone rather than looping.</summary>
    private const int MaxObjects = 64, MaxPasses = 16;

    /// <summary>Scratch "this part's subtree holds geometry", one slot per part index. One table for the
    /// session: the gate runs on a tree edit, never per frame, and never re-enters itself.</summary>
    private static readonly bool[] _geom = new bool[SceneDb.MaxParts];
    private static readonly DbId[] _objs = new DbId[MaxObjects];

    /// <summary>Cumulative since boot: gate passes run, parts the pass turned off, compositions that hit a
    /// ceiling.</summary>
    internal static long Ran, Gated, Overflowed;

    /// <summary>Does this part instance geometry of its own -- the answer <c>Part::AsPartTriMesh</c> gives?
    /// <see cref="PartKind.Merged"/> is the swallowed node, which in the engine is still the
    /// <c>PartTriMesh</c> its triangles came from.</summary>
    private static bool HasGeometry(int p) => SceneDb.PartKindOf[p] switch
    {
        PartKind.Mesh or PartKind.Skin or PartKind.Dangly or PartKind.AnimMesh or PartKind.Aabb
            or PartKind.Merged => true,
        _ => false,
    };

    /// <summary>Re-run the engine's pass over <paramref name="root"/>'s whole composition: the object's own
    /// part range plus every piece grafted onto it. Call it where the engine sets its process flag -- a tree
    /// built, a graft appended, a subtree amputated -- not per frame.</summary>
    internal static void Reflow(DbId root)
    {
        if (!SceneDb.Objects.Valid(root)) return;
        Ran++;

        // A graft is a CHILD OBJECT here and a child SUBTREE in the engine, and Gob::Append hangs every piece
        // of a creature off the ROOT gob's list whatever part it attaches to -- so the composition is flat
        // and its geometry edges cross object ranges.
        int count = Gather(root);
        if (count <= 0) return;

        for (int i = 0; i < count; i++)
        {
            int first = SceneDb.ObjPartFirst[_objs[i].Index], n = SceneDb.ObjPartCount[_objs[i].Index];
            for (int k = 0; k < n; k++) _geom[first + k] = HasGeometry(first + k);
        }

        for (int pass = 0; pass < MaxPasses; pass++)
        {
            bool changed = false;
            // Deepest graft first, and within a range the pre-order means a child's index is always above its
            // parent's, so one backward sweep lifts every subtree's answer to its root.
            for (int i = count - 1; i >= 0; i--)
            {
                int o = _objs[i].Index;
                int first = SceneDb.ObjPartFirst[o], n = SceneDb.ObjPartCount[o];
                for (int k = n - 1; k >= 1; k--)
                {
                    int p = first + k, parent = SceneDb.PartParent[p];
                    if (!_geom[p] || (uint)parent >= (uint)SceneDb.MaxParts || _geom[parent]) continue;
                    _geom[parent] = true;
                    changed = true;
                }
                if (i == 0 || n <= 0) continue;
                int at = SceneDb.ObjAttachPart[o];
                if (!_geom[first] || (uint)at >= (uint)SceneDb.MaxParts || _geom[at]) continue;
                _geom[at] = true;
                changed = true;
            }
            if (!changed) break;
            if (pass == MaxPasses - 1) Overflowed++;
        }

        for (int i = 0; i < count; i++)
        {
            int first = SceneDb.ObjPartFirst[_objs[i].Index], n = SceneDb.ObjPartCount[_objs[i].Index];
            for (int k = 0; k < n; k++)
            {
                int p = first + k;
                if (_geom[p] || !SceneDb.PartEnabled[p]) continue;
                SceneDb.PartEnabled[p] = false;
                Gated++;
            }
        }
    }

    /// <summary>The composition: <paramref name="root"/> at [0], then every grafted piece, a piece's own
    /// grafts after it. Returns how many went in.</summary>
    private static int Gather(DbId root)
    {
        _objs[0] = root;
        int count = 1;
        for (int i = 0; i < count; i++)
            foreach (DbId c in SceneDb.Pieces(_objs[i]))
            {
                if (count >= MaxObjects) { Overflowed++; return count; }
                _objs[count++] = c;
            }
        return count;
    }

    /// <summary>One clause of the <c>[alloy]</c> period line; empty until the gate has run.</summary>
    internal static string Stats()
        => Ran == 0 ? "" : $"enable gate: {Gated} parts off in {Ran} passes"
                           + (Overflowed > 0 ? $", {Overflowed} compositions past the ceiling" : "");

    /// <summary>Off-device reset, for the self-check suite.</summary>
    internal static void ResetForCheck() => Ran = Gated = Overflowed = 0;
}
