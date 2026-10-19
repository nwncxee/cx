using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary><b>The visible set, as an index over the scene database</b>: one <see cref="UniformGrid"/> keyed on
/// the OBJECT, not the part. INCREMENTAL -- a <see cref="ObjFlag.Static"/> object is filed once and never
/// re-examined, everything else goes on the MOVER list, and a static with unknown bounds is UNBOUNDED; the last
/// two are in every query.</summary>
internal static unsafe class DbGrid
{
    /// <inheritdoc cref="UniformGrid.CellSize"/>
    internal const float CellSize = UniformGrid.CellSize;
    /// <inheritdoc cref="UniformGrid.CellSize"/>
    internal const int Dim = UniformGrid.Dim;

    private static readonly UniformGrid G = new("DbGrid", SceneDb.MaxObjects, movers: true);

    internal static int Filed => G.Filed;
    internal static int Unboxed => G.Unboxed;
    internal static int Movers => G.Movers;
    internal static int Occupied => G.Occupied;
    /// <inheritdoc cref="UniformGrid.Refused"/>
    internal static int Refused => G.Refused;

    /// <summary>Empty the index. A scene destroy, a session edge, or a self-check.</summary>
    internal static void Reset() => G.Begin();

    /// <inheritdoc cref="UniformGrid.File"/>
    internal static void File(int o, float cx, float cy, float cz, float r, bool stat)
        => G.File(o, cx, cy, cz, r, stat);

    /// <inheritdoc cref="UniformGrid.Remove"/>
    internal static void Remove(int o) => G.Remove(o);

    /// <inheritdoc cref="UniformGrid.Cell"/>
    internal static int Cell(float x, float y) => UniformGrid.Cell(x, y);

    /// <inheritdoc cref="UniformGrid.Query"/>
    internal static int Query(ReadOnlySpan<Vector4> planes, float slack, int[] into, int n)
        => G.Query(planes, slack, into, n);

    /// <summary>Off-device: a static comes back from a query containing its cell and not from one that does
    /// not; a mover and an unbounded object come back from every query; an unfiled object comes back from
    /// none. A wrong answer here is geometry that vanishes at a viewing angle.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbGrid.SelfCheck");
        Reset();
        File(1, 5f, 5f, 0f, 1f, stat: true);        // cell (0,0)
        File(2, 305f, 305f, 0f, 1f, stat: true);    // cell (30,30)
        File(3, 0f, 0f, 0f, 0f, stat: true);        // no bounds -> unbounded
        File(4, 5f, 5f, 0f, 1f, stat: false);       // a mover
        check.Expect(Filed == 2 && Unboxed == 1 && Movers == 1 && Occupied == 2,
                     "two cells filed, one unbounded, one mover");
        check.Expect(Cell(0f, 0f) != Cell(300f, 300f), "two tiles 30 apart are different cells");
        check.Expect(Cell(0f, 0f) == Cell(Dim * CellSize, 0f), "the grid wraps at its width");

        Span<Vector4> planes = stackalloc Vector4[6];
        planes.Fill(new Vector4(0f, 0f, 0f, 1f));
        planes[0] = new Vector4(1f, 0f, 0f, -100f);          // keep x >= 100
        int[] into = new int[16];
        int n = Query(planes, 0f, into, 0);
        bool near = false, far = false, unb = false, mov = false;
        for (int k = 0; k < n; k++)
        {
            if (into[k] == 1) near = true;
            if (into[k] == 2) far = true;
            if (into[k] == 3) unb = true;
            if (into[k] == 4) mov = true;
        }
        check.Expect(!near && far && unb && mov,
                     "the query yields the cell in the half-space, the movers and the unbounded, not the cell behind it");
        n = Query(planes, 200f, into, 0);
        near = false;
        for (int k = 0; k < n; k++) if (into[k] == 1) near = true;
        check.Expect(near, "the slack yields a cell about to enter the view");

        // A destroy unfiles, and nothing stale comes back.
        Remove(2);
        n = Query(planes, 0f, into, 0);
        far = false;
        for (int k = 0; k < n; k++) if (into[k] == 2) far = true;
        check.Expect(!far && Filed == 1, "an unfiled object is in no query");
        // A static that moves to another cell is in the new one and not in the old one.
        File(1, 305f, 305f, 0f, 1f, stat: true);
        n = Query(planes, 0f, into, 0);
        near = false;
        for (int k = 0; k < n; k++) if (into[k] == 1) near = true;
        check.Expect(near && Filed == 1, "a re-filed static moves cells rather than occupying both");
        // Re-filing with the same placement is idempotent.
        File(1, 305f, 305f, 0f, 1f, stat: true);
        check.Expect(Filed == 1, "re-filing the same placement does not duplicate the entry");
        Remove(1); Remove(3); Remove(4);
        check.Expect(Filed == 0 && Unboxed == 0 && Movers == 0, "the index empties completely");

        Reset();
        log.Info(check.Ok ? "DbGrid.SelfCheck: passed"
                          : "DbGrid.SelfCheck: FAILED -- the visible set would hide or duplicate geometry");
        return check.Ok;
    }
}
