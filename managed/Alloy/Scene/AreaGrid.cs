using CX.Engine;
using System;
using System.Numerics;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Alloy's spatial index of the area: a tile-aligned uniform grid holding every STATIC slot (no gob,
/// world box filled by <c>ComputePartBBox</c> @0x561370 at tile spawn), filed once per enrolment. Parts on a gob
/// are movers Alloy routes every pass anyway; one with no world box is UNBOUNDED and always visible. The grid
/// itself is <see cref="UniformGrid"/>, which the scene database indexes its objects with too.</summary>
internal static unsafe class AreaGrid
{
    /// <inheritdoc cref="UniformGrid.CellSize"/>
    internal const float CellSize = UniformGrid.CellSize;
    /// <inheritdoc cref="UniformGrid.CellSize"/>
    internal const int Dim = UniformGrid.Dim;

    private static readonly UniformGrid G = new(AlloyRenderer.MaxOwned);

    internal static int Filed => G.Filed;
    internal static int Unboxed => G.Unboxed;
    internal static int Occupied => G.Occupied;

    /// <summary>Start a fresh index. Called once per enrolment, which is the only edge that can change which
    /// slot holds which part.</summary>
    internal static void Begin() => G.Begin();

    /// <inheritdoc cref="UniformGrid.Add"/>
    internal static void Add(int slot, in Box box) => G.Add(slot, in box);

    /// <inheritdoc cref="UniformGrid.AddUnboxed"/>
    internal static void AddUnboxed(int slot) => G.AddUnboxed(slot);

    /// <inheritdoc cref="UniformGrid.Cell"/>
    internal static int Cell(float x, float y) => UniformGrid.Cell(x, y);

    /// <inheritdoc cref="UniformGrid.Query"/>
    internal static int Query(ReadOnlySpan<Vector4> planes, float slack, int[] into, int n)
        => G.Query(planes, slack, into, n);

    /// <inheritdoc cref="UniformGrid.StillFiled"/>
    internal static bool StillFiled(int slot, in Box box, out int filed, out int now, out bool covered)
        => G.StillFiled(slot, in box, out filed, out now, out covered);

    /// <inheritdoc cref="UniformGrid.BoxInFrustum"/>
    internal static bool BoxInFrustum(ReadOnlySpan<Vector4> planes, in Box box, float slack)
        => UniformGrid.BoxInFrustum(planes, in box, slack);

    /// <summary>Off-device: the two properties the index lives by -- a slot comes back from a query that
    /// contains its cell and does not from one that does not, and an unboundable slot comes back from every
    /// query. A wrong answer here is geometry that vanishes at a viewing angle, which is the hardest kind of
    /// bug to catch live.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AreaGrid.SelfCheck");
        Begin();
        var near = new Box { Min = new Vector3(0f, 0f, 0f), Max = new Vector3(1f, 1f, 1f) };            // cell (0,0)
        var far = new Box { Min = new Vector3(300f, 300f, 0f), Max = new Vector3(301f, 301f, 1f) };     // cell (30,30)
        Add(0, in near);
        Add(1, in far);
        AddUnboxed(2);
        check.Expect(Filed == 2 && Unboxed == 1 && Occupied == 2, "two cells filed, one slot unboundable");
        check.Expect(Cell(0f, 0f) != Cell(300f, 300f), "two tiles 30 apart are different cells");
        check.Expect(Cell(0f, 0f) == Cell(Dim * CellSize, 0f), "the grid wraps at its width");
        check.Expect(Cell(-1f, -1f) == Cell(Dim * CellSize - 1f, Dim * CellSize - 1f),
                     "...and a negative coordinate wraps into the far corner rather than out of the table");

        // A half-space keeping x >= 100: the near cell is out, the far one is in, the unboundable one always in.
        Span<Vector4> planes = stackalloc Vector4[6];
        planes.Fill(new Vector4(0f, 0f, 0f, 1f));
        planes[0] = new Vector4(1f, 0f, 0f, -100f);
        int[] into = new int[8];
        int n = Query(planes, 0f, into, 0);
        bool sawNear = false, sawFar = false, sawUnboxed = false;
        for (int k = 0; k < n; k++) { if (into[k] == 0) sawNear = true; if (into[k] == 1) sawFar = true; if (into[k] == 2) sawUnboxed = true; }
        check.Expect(!sawNear && sawFar && sawUnboxed, "the query yields the cell in the half-space, not the one behind it");
        // ...and the slack pulls a cell in: 100 units of it reaches the near cell's far corner at x = 1.
        n = Query(planes, 100f, into, 0);
        sawNear = false;
        for (int k = 0; k < n; k++) if (into[k] == 0) sawNear = true;
        check.Expect(sawNear, "the route slack yields a cell about to enter the view");
        // A plane nothing can satisfy empties the query but keeps the unboundable slots.
        planes[0] = new Vector4(1f, 0f, 0f, -1e9f);
        n = Query(planes, 0f, into, 0);
        check.Expect(n == 1 && into[0] == 2, "a frustum that reaches nothing still yields the unboundable slots");

        // CX-188: and the filing's own staleness test, which is what the [vischeck] miss log now reports.
        // A query reaches a slot ONLY through the cell it was filed under, so a box that moved after the
        // filing is invisible to every query -- the property this asserts is that the test says so.
        check.Expect(StillFiled(0, in near, out int cellWas, out int cellNow, out bool covered)
                     && cellWas == cellNow && covered, "a slot whose box has not moved still reads as filed where it is");
        var moved = new Box { Min = new Vector3(300f, 300f, 0f), Max = new Vector3(301f, 301f, 1f) };   // the far cell, not slot 0's
        check.Expect(!StillFiled(0, in moved, out cellWas, out cellNow, out covered)
                     && cellWas != cellNow && !covered,
                     "a slot whose box left its filed cell no longer reads as filed, and names both cells");
        var grown = new Box { Min = new Vector3(0f, 0f, 0f), Max = new Vector3(40f, 1f, 1f) };          // same cell, past its union
        check.Expect(!StillFiled(0, in grown, out cellWas, out cellNow, out covered) && !covered,
                     "a box that outgrew its cell's union no longer reads as covered by it");
        check.Expect(!StillFiled(7, in near, out cellWas, out _, out _) && cellWas < 0,
                     "an unfiled slot reads as unfiled and names no cell");

        Begin();
        log.Info(check.Ok ? "AreaGrid.SelfCheck: passed"
                          : "AreaGrid.SelfCheck: FAILED -- the area index would hide geometry from a pass");
        return check.Ok;
    }
}
