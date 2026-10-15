using CX.Engine;
using System;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary><b>The visible set, as an index over the scene database</b>, in the engine's <c>AreaGrid</c> shape:
/// one NWN tile per cell, a wrapping 64 x 64 grid, a cell carrying the real AABB of what it holds, keyed on the
/// OBJECT not the part. INCREMENTAL: a <see cref="ObjFlag.Static"/> object is filed once and never re-examined,
/// everything else walks the MOVER list whole, and a static with unknown bounds is UNBOUNDED -- in every query.</summary>
internal static unsafe class DbGrid
{
    internal const float CellSize = 10f;
    internal const int Bits = 6, Dim = 1 << Bits, Mask = Dim - 1, Cells = Dim * Dim;

    /// <summary>Per cell: the head of its object list (-1 empty) and the real world AABB of everything in it.
    /// The box only ever GROWS, which costs a candidate when a static leaves and never a miss.</summary>
    private static readonly int[] _head = new int[Cells];
    private static readonly float[] _box = new float[Cells * 6];
    /// <summary>Per object: the next object in its cell's list, and which cell it is in (-1 not filed,
    /// <see cref="CellUnboxed"/> unbounded, <see cref="CellMover"/> on the mover list).</summary>
    private static readonly int[] _next = new int[SceneDb.MaxObjects];
    private static readonly int[] _cell = new int[SceneDb.MaxObjects];
    private static readonly int[] _unboxed = new int[SceneDb.MaxObjects];
    private static readonly int[] _movers = new int[SceneDb.MaxObjects];
    private static int _unboxedN, _moverN, _filed, _occupied;
    private static bool _ready;

    private const int CellNone = -1, CellUnboxed = -2, CellMover = -3;

    internal static int Filed => _filed;
    internal static int Unboxed => _unboxedN;
    internal static int Movers => _moverN;
    internal static int Occupied => _occupied;

    /// <summary>Empty the index. A scene destroy, a session edge, or a self-check.</summary>
    internal static void Reset()
    {
        for (int c = 0; c < Cells; c++) _head[c] = CellNone;
        Array.Fill(_cell, CellNone);
        _unboxedN = 0; _moverN = 0; _filed = 0; _occupied = 0;
        _ready = true;
    }

    /// <summary>File object <paramref name="o"/>, or re-file it if its placement changed. Idempotent: calling
    /// it twice with the same sphere is one filing. Allocation-free.</summary>
    internal static void File(int o, float cx, float cy, float cz, float r, bool stat)
    {
        if (!_ready) Reset();
        if ((uint)o >= (uint)_cell.Length) return;
        if (!stat)
        {
            if (_cell[o] == CellMover) return;
            Remove(o);
            if (_moverN < _movers.Length) { _movers[_moverN++] = o; _cell[o] = CellMover; }
            return;
        }
        if (!(r > 0f))
        {
            if (_cell[o] == CellUnboxed) return;
            Remove(o);
            if (_unboxedN < _unboxed.Length) { _unboxed[_unboxedN++] = o; _cell[o] = CellUnboxed; }
            return;
        }
        int cell = Cell(cx, cy);
        if (_cell[o] == cell) { Grow(cell, cx, cy, cz, r); return; }
        Remove(o);
        Grow(cell, cx, cy, cz, r);
        _next[o] = _head[cell];
        _head[cell] = o;
        _cell[o] = cell;
        _filed++;
    }

    /// <summary>Unfile object <paramref name="o"/> (its record was destroyed, or it stopped being static).</summary>
    internal static void Remove(int o)
    {
        if (!_ready || (uint)o >= (uint)_cell.Length) return;
        int c = _cell[o];
        if (c == CellNone) return;
        if (c == CellUnboxed)
        {
            for (int k = 0; k < _unboxedN; k++)
                if (_unboxed[k] == o) { _unboxed[k] = _unboxed[--_unboxedN]; break; }
        }
        else if (c == CellMover)
        {
            for (int k = 0; k < _moverN; k++)
                if (_movers[k] == o) { _movers[k] = _movers[--_moverN]; break; }
        }
        else
        {
            int prev = -1;
            for (int i = _head[c]; i >= 0; i = _next[i])
            {
                if (i == o)
                {
                    if (prev < 0) _head[c] = _next[i]; else _next[prev] = _next[i];
                    _filed--;
                    if (_head[c] < 0) _occupied--;
                    break;
                }
                prev = i;
            }
        }
        _cell[o] = CellNone;
    }

    private static void Grow(int cell, float cx, float cy, float cz, float r)
    {
        int b = cell * 6;
        // The first entry SETS the box; every later one only grows it. An empty cell's stale box would
        // otherwise swallow the whole area once anything was filed back into it.
        if (_head[cell] < 0)
        {
            _occupied++;
            _box[b + 0] = cx - r; _box[b + 1] = cy - r; _box[b + 2] = cz - r;
            _box[b + 3] = cx + r; _box[b + 4] = cy + r; _box[b + 5] = cz + r;
            return;
        }
        if (cx - r < _box[b + 0]) _box[b + 0] = cx - r;
        if (cy - r < _box[b + 1]) _box[b + 1] = cy - r;
        if (cz - r < _box[b + 2]) _box[b + 2] = cz - r;
        if (cx + r > _box[b + 3]) _box[b + 3] = cx + r;
        if (cy + r > _box[b + 4]) _box[b + 4] = cy + r;
        if (cz + r > _box[b + 5]) _box[b + 5] = cz + r;
    }

    /// <summary>The cell a world XY falls in. Wraps, so any coordinate is a valid cell.</summary>
    internal static int Cell(float x, float y)
    {
        int ix = (int)MathF.Floor(x / CellSize) & Mask;
        int iy = (int)MathF.Floor(y / CellSize) & Mask;
        return (iy << Bits) | ix;
    }

    /// <summary>Append every object whose cell survives <paramref name="planes"/> (six vec4s, the
    /// <c>CullPass.Planes</c> convention: inside when <c>dot(n, p) + d &gt;= 0</c>), plus every mover and every
    /// unbounded object, into <paramref name="into"/> from <paramref name="n"/>; <paramref name="slack"/> grows
    /// each cell box so a cell about to enter view is yielded a frame early. Returns the new count, allocation-free.</summary>
    internal static int Query(float* planes, float slack, int[] into, int n)
    {
        if (!_ready) return n;
        for (int k = 0; k < _unboxedN && n < into.Length; k++) into[n++] = _unboxed[k];
        for (int k = 0; k < _moverN && n < into.Length; k++) into[n++] = _movers[k];
        fixed (float* boxes = _box)
            for (int c = 0; c < Cells; c++)
            {
                int head = _head[c];
                if (head < 0) continue;
                if (!AreaGrid.BoxInFrustum(planes, ((Stride6f*)boxes)[c].F, slack)) continue;
                for (int i = head; i >= 0 && n < into.Length; i = _next[i]) into[n++] = i;
            }
        return n;
    }

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

        float* planes = stackalloc float[24];
        for (int f = 0; f < 6; f++) { planes[f * 4] = 0f; planes[f * 4 + 1] = 0f; planes[f * 4 + 2] = 0f; planes[f * 4 + 3] = 1f; }
        planes[0] = 1f; planes[3] = -100f;          // keep x >= 100
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
