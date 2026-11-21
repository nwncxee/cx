using System.Collections.Generic;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The part columns a <c>.mtr</c> seeds, worked out again when the material changes: a server's override, or
/// a file that landed after the part was built (<see cref="OverrideReset"/> carries both).</summary>
public static partial class PartTree
{
    /// <summary>The materials in <paramref name="names"/> changed: every live part whose mesh draws one takes the
    /// material as it now reads -- or none, back to its mesh's own terms -- and is marked, which rewrites its record and
    /// re-resolves its texture slots. Answers how many parts moved. Game thread, cold.</summary>
    internal static int Refold(HashSet<string> names)
    {
        int n = 0, high = SceneDb.Parts.High;
        for (int p = 0; p < high; p++)
        {
            if (!SceneDb.Parts.LiveAt(p)) continue;
            MeshDef? mesh = SceneDb.PartMeshDef[p];
            if (mesh == null || !names.Contains(MtrDef.NameOf(mesh))) continue;
            MtrDef? mtr = MtrDef.Of(mesh);
            SceneDb.PartMtr[p] = mtr;
            PartMaterial m = SceneDb.PartMtl[p];
            m.RenderHint = mtr != null && mtr.RenderHint != 0 ? mtr.RenderHint : MtrDef.Hint(mesh.RenderHint);
            SceneDb.PartMtl[p] = m;
            SceneDb.TouchPart(p, Dirty.Material);
            n++;
        }
        return n;
    }
}
