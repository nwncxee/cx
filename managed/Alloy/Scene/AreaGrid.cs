using CX.Engine;
using System;
using CX.Framework;

namespace CX.Alloy;

/// <summary>Alloy's spatial index of the area: a tile-aligned uniform grid holding every STATIC slot (no gob,
/// world box filled by <c>ComputePartBBox</c> @0x561370 at tile spawn), filed once per enrolment. Parts on a gob
/// are movers Alloy routes every pass anyway; one with no world box is UNBOUNDED and always visible. The grid
/// WRAPS at <see cref="Dim"/> cells -- a candidate, never a miss, since each cell's AABB is real.</summary>
internal static unsafe class AreaGrid
{
    /// <summary>One NWN tile per cell, and a 64 x 64 grid -- 640 units, comfortably past the largest shipped
    /// area (32 x 32 tiles) and past any camera far plane.</summary>
    internal const float CellSize = 10f;
    internal const int Bits = 6, Dim = 1 << Bits, Mask = Dim - 1, Cells = Dim * Dim;

    /// <summary>Per cell: the head of its slot list (-1 empty) and the real world AABB of everything in it.</summary>
    private static readonly int[] _head = new int[Cells];
    private static readonly float[] _box = new float[Cells * 6];
    /// <summary>Per slot: the next slot in its cell's list. Sized by the ownership table, like every other
    /// per-slot column, and written only by <see cref="Add"/>.</summary>
    private static readonly int[] _next = new int[AlloyRenderer.MaxOwned];
    /// <summary>Diagnostic: the cell each slot was FILED under, -1 for never. <c>_oBox</c> has a second writer
    /// (<c>DbWriteWorldBox</c>, on every record rewrite), so a box that moved after its filing leaves the grid
    /// describing where the part WAS, and a cell whose union no longer covers it drops it from every query.</summary>
    private static readonly int[] _cellOf = ResetCells(new int[AlloyRenderer.MaxOwned]);
    private static int[] ResetCells(int[] a) { for (int i = 0; i < a.Length; i++) a[i] = -1; return a; }
    private static int _filed, _occupied;

    /// <summary>Slots with no world box of their own: always visible, exactly as their <c>NeverCull</c> sphere
    /// already made them.</summary>
    private static readonly int[] _unboxed = new int[AlloyRenderer.MaxOwned];
    private static int _unboxedN;

    internal static int Filed => _filed;
    internal static int Unboxed => _unboxedN;
    internal static int Occupied => _occupied;

    /// <summary>Start a fresh index. Called once per enrolment, which is the only edge that can change which
    /// slot holds which part.</summary>
    internal static void Begin()
    {
        for (int c = 0; c < Cells; c++) _head[c] = -1;
        for (int i = 0; i < _cellOf.Length; i++) _cellOf[i] = -1;
        _filed = 0; _unboxedN = 0; _occupied = 0;
    }

    /// <summary>File <paramref name="slot"/> under its world AABB (six floats, min then max).</summary>
    internal static void Add(int slot, float* box)
    {
        int cell = Cell(box[0], box[1]);
        if (_head[cell] < 0)
        {
            _occupied++;
            int o = cell * 6;
            for (int k = 0; k < 6; k++) _box[o + k] = box[k];
        }
        else
        {
            int o = cell * 6;
            for (int k = 0; k < 3; k++) if (box[k] < _box[o + k]) _box[o + k] = box[k];
            for (int k = 3; k < 6; k++) if (box[k] > _box[o + k]) _box[o + k] = box[k];
        }
        _next[slot] = _head[cell];
        _head[cell] = slot;
        _cellOf[slot] = cell;
        _filed++;
    }

    /// <summary>File <paramref name="slot"/> as unboundable: no world box, so every pass sees it.</summary>
    internal static void AddUnboxed(int slot)
    {
        if (_unboxedN < _unboxed.Length) _unboxed[_unboxedN++] = slot;
    }

    /// <summary>The cell a world XY falls in. Wraps, so any coordinate is a valid cell.</summary>
    internal static int Cell(float x, float y)
    {
        int ix = (int)MathF.Floor(x / CellSize) & Mask;
        int iy = (int)MathF.Floor(y / CellSize) & Mask;
        return (iy << Bits) | ix;
    }

    /// <summary>Append every filed slot whose cell survives <paramref name="planes"/> (six vec4s, the
    /// <see cref="CullPass.Planes"/> convention: a point is inside when <c>dot(n, p) + d &gt;= 0</c>) into
    /// <paramref name="into"/> from <paramref name="n"/>, plus every unboundable slot. <paramref name="slack"/>
    /// grows each cell box, so a cell about to enter the view is yielded the frame before. Allocation-free.</summary>
    internal static int Query(float* planes, float slack, int[] into, int n)
    {
        for (int k = 0; k < _unboxedN && n < into.Length; k++) into[n++] = _unboxed[k];
        fixed (float* boxes = _box)
            for (int c = 0; c < Cells; c++)
            {
                int head = _head[c];
                if (head < 0) continue;
                if (!BoxInFrustum(planes, ((Stride6f*)boxes)[c].F, slack)) continue;
                for (int i = head; i >= 0 && n < into.Length; i = _next[i]) into[n++] = i;
            }
        return n;
    }

    /// <summary>Diagnostic: does the grid still describe where <paramref name="slot"/>'s box is? A query yields a
    /// slot only through its filed cell and tests that cell's UNION box, so a box that has left the union is
    /// invisible to every query. Reports the filed cell, the cell it would be filed under now, and whether the
    /// union still covers it. Off the hot path -- asked only once the audit has found a miss.</summary>
    internal static bool StillFiled(int slot, float* box, out int filed, out int now, out bool covered)
    {
        filed = (uint)slot < (uint)_cellOf.Length ? _cellOf[slot] : -1;
        now = Cell(box[0], box[1]);
        covered = false;
        if (filed < 0) return false;
        fixed (float* b = _box)
        {
            float* u = ((Stride6f*)b)[filed].F;
            covered = box[0] >= u[0] && box[1] >= u[1] && box[2] >= u[2]
                   && box[3] <= u[3] && box[4] <= u[4] && box[5] <= u[5];
        }
        return filed == now && covered;
    }

    /// <summary>The standard n-vertex box test: for each plane, the box corner FARTHEST along the plane normal.
    /// Behind every one of the six and the box cannot be in view.</summary>
    internal static bool BoxInFrustum(float* planes, float* box, float slack)
    {
        for (int f = 0; f < 6; f++)
        {
            float* p = ((Stride4f*)planes)[f].F;
            float x = p[0] >= 0f ? box[3] + slack : box[0] - slack;
            float y = p[1] >= 0f ? box[4] + slack : box[1] - slack;
            float z = p[2] >= 0f ? box[5] + slack : box[2] - slack;
            if (p[0] * x + p[1] * y + p[2] * z + p[3] < 0f) return false;
        }
        return true;
    }

    /// <summary>Off-device: the two properties the index lives by -- a slot comes back from a query that
    /// contains its cell and does not from one that does not, and an unboundable slot comes back from every
    /// query. A wrong answer here is geometry that vanishes at a viewing angle, which is the hardest kind of
    /// bug to catch live.</summary>
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AreaGrid.SelfCheck");
        Begin();
        float* near = stackalloc float[6] { 0f, 0f, 0f, 1f, 1f, 1f };            // cell (0,0)
        float* far = stackalloc float[6] { 300f, 300f, 0f, 301f, 301f, 1f };     // cell (30,30)
        Add(0, near);
        Add(1, far);
        AddUnboxed(2);
        check.Expect(Filed == 2 && Unboxed == 1 && Occupied == 2, "two cells filed, one slot unboundable");
        check.Expect(Cell(0f, 0f) != Cell(300f, 300f), "two tiles 30 apart are different cells");
        check.Expect(Cell(0f, 0f) == Cell(Dim * CellSize, 0f), "the grid wraps at its width");
        check.Expect(Cell(-1f, -1f) == Cell(Dim * CellSize - 1f, Dim * CellSize - 1f),
                     "...and a negative coordinate wraps into the far corner rather than out of the table");

        // A half-space keeping x >= 100: the near cell is out, the far one is in, the unboundable one always in.
        float* planes = stackalloc float[24];
        for (int f = 0; f < 6; f++) { planes[f * 4] = 0f; planes[f * 4 + 1] = 0f; planes[f * 4 + 2] = 0f; planes[f * 4 + 3] = 1f; }
        planes[0] = 1f; planes[3] = -100f;
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
        planes[0] = 1f; planes[3] = -1e9f;
        n = Query(planes, 0f, into, 0);
        check.Expect(n == 1 && into[0] == 2, "a frustum that reaches nothing still yields the unboundable slots");

        // CX-188: and the filing's own staleness test, which is what the [vischeck] miss log now reports.
        // A query reaches a slot ONLY through the cell it was filed under, so a box that moved after the
        // filing is invisible to every query -- the property this asserts is that the test says so.
        check.Expect(StillFiled(0, near, out int cellWas, out int cellNow, out bool covered)
                     && cellWas == cellNow && covered, "a slot whose box has not moved still reads as filed where it is");
        float* moved = stackalloc float[6] { 300f, 300f, 0f, 301f, 301f, 1f };   // the far cell, not slot 0's
        check.Expect(!StillFiled(0, moved, out cellWas, out cellNow, out covered)
                     && cellWas != cellNow && !covered,
                     "a slot whose box left its filed cell no longer reads as filed, and names both cells");
        float* grown = stackalloc float[6] { 0f, 0f, 0f, 40f, 1f, 1f };          // same cell, past its union
        check.Expect(!StillFiled(0, grown, out cellWas, out cellNow, out covered) && !covered,
                     "a box that outgrew its cell's union no longer reads as covered by it");
        check.Expect(!StillFiled(7, near, out cellWas, out _, out _) && cellWas < 0,
                     "an unfiled slot reads as unfiled and names no cell");

        Begin();
        log.Info(check.Ok ? "AreaGrid.SelfCheck: passed"
                          : "AreaGrid.SelfCheck: FAILED -- the area index would hide geometry from a pass");
        return check.Ok;
    }
}
