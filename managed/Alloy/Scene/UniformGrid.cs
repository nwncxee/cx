using CX.Engine;
using System;
using System.Numerics;

namespace CX.Alloy;

/// <summary><b>One wrapping, tile-aligned uniform grid</b> over the world XY plane: an id per entry, a cell
/// carrying the REAL AABB of everything filed in it, queried against a frustum. Two instances -- Alloy's owned
/// slots (<see cref="AreaGrid"/>) and the scene database's objects (<c>Db.DbGrid</c>) -- because the capacity
/// and the filing edge differ and nothing else does. Allocation-free after construction.</summary>
internal sealed class UniformGrid
{
    /// <summary>One NWN tile per cell, and a 64 x 64 grid -- 640 units, comfortably past the largest shipped
    /// area (32 x 32 tiles) and past any camera far plane. The grid WRAPS, so any coordinate is a valid cell:
    /// a candidate, never a miss, since each cell's AABB is real.</summary>
    internal const float CellSize = 10f;
    /// <inheritdoc cref="CellSize"/>
    internal const int Bits = 6, Dim = 1 << Bits, Mask = Dim - 1, Cells = Dim * Dim;

    /// <summary>The <see cref="_cellOf"/> values that are not a cell: never filed, filed as unbounded (in
    /// every query), and filed as a mover (in every query, and re-routed by its owner each pass).</summary>
    private const int CellNone = -1, CellUnboxed = -2, CellMover = -3;

    /// <summary>Per cell: the head of its id list (-1 empty) and the real world AABB of everything in it. The
    /// box only ever GROWS while the cell is occupied, which costs a candidate and never a miss.</summary>
    private readonly int[] _head = new int[Cells];
    private readonly Box[] _box = new Box[Cells];
    /// <summary>Per id: the next id in its cell's list, and which cell it is in. <see cref="_cellOf"/> is also
    /// the diagnostic <see cref="StillFiled"/> reads -- a box that moved after its filing leaves the grid
    /// describing where the entry WAS, and a cell whose union no longer covers it drops it from every query.</summary>
    private readonly int[] _next, _cellOf, _unboxed, _movers;
    private int _unboxedN, _moverN, _filed, _occupied;

    internal UniformGrid(int capacity)
    {
        _next = new int[capacity];
        _cellOf = new int[capacity];
        _unboxed = new int[capacity];
        _movers = new int[capacity];
        Begin();
    }

    internal int Filed => _filed;
    internal int Unboxed => _unboxedN;
    internal int Movers => _moverN;
    internal int Occupied => _occupied;

    /// <summary>Empty the index: a fresh enrolment, a scene destroy, a session edge, or a self-check.</summary>
    internal void Begin()
    {
        for (int c = 0; c < Cells; c++) _head[c] = CellNone;
        Array.Fill(_cellOf, CellNone);
        _unboxedN = 0; _moverN = 0; _filed = 0; _occupied = 0;
    }

    /// <summary>File <paramref name="id"/> under its world AABB, at the cell its MIN corner falls in.</summary>
    internal void Add(int id, in Box box)
    {
        if ((uint)id >= (uint)_cellOf.Length) return;
        Link(id, Cell(box.Min.X, box.Min.Y), in box);
    }

    /// <summary>File <paramref name="id"/> as unboundable: no world box, so every query yields it.</summary>
    internal void AddUnboxed(int id)
    {
        if ((uint)id >= (uint)_cellOf.Length || _cellOf[id] == CellUnboxed) return;
        Remove(id);
        if (_unboxedN < _unboxed.Length) { _unboxed[_unboxedN++] = id; _cellOf[id] = CellUnboxed; }
    }

    /// <summary>File <paramref name="id"/> by its world SPHERE, at the cell its centre falls in, or re-file it
    /// when its placement changed. A non-static entry goes on the mover list and a radius-less one on the
    /// unbounded list; both are in every query. Idempotent: the same placement twice is one filing.</summary>
    internal void File(int id, float cx, float cy, float cz, float r, bool stat)
    {
        if ((uint)id >= (uint)_cellOf.Length) return;
        if (!stat)
        {
            if (_cellOf[id] == CellMover) return;
            Remove(id);
            if (_moverN < _movers.Length) { _movers[_moverN++] = id; _cellOf[id] = CellMover; }
            return;
        }
        if (!(r > 0f)) { AddUnboxed(id); return; }
        var box = new Box { Min = new Vector3(cx - r, cy - r, cz - r), Max = new Vector3(cx + r, cy + r, cz + r) };
        Link(id, Cell(cx, cy), in box);
    }

    /// <summary>Unfile <paramref name="id"/>: its record was destroyed, or it stopped being static.</summary>
    internal void Remove(int id)
    {
        if ((uint)id >= (uint)_cellOf.Length) return;
        int c = _cellOf[id];
        if (c == CellNone) return;
        if (c == CellUnboxed)
        {
            for (int k = 0; k < _unboxedN; k++)
                if (_unboxed[k] == id) { _unboxed[k] = _unboxed[--_unboxedN]; break; }
        }
        else if (c == CellMover)
        {
            for (int k = 0; k < _moverN; k++)
                if (_movers[k] == id) { _movers[k] = _movers[--_moverN]; break; }
        }
        else
        {
            int prev = -1;
            for (int i = _head[c]; i >= 0; i = _next[i])
            {
                if (i == id)
                {
                    if (prev < 0) _head[c] = _next[i]; else _next[prev] = _next[i];
                    _filed--;
                    if (_head[c] < 0) _occupied--;
                    break;
                }
                prev = i;
            }
        }
        _cellOf[id] = CellNone;
    }

    /// <summary>The cell a world XY falls in. Wraps, so any coordinate is a valid cell.</summary>
    internal static int Cell(float x, float y)
    {
        int ix = (int)MathF.Floor(x / CellSize) & Mask;
        int iy = (int)MathF.Floor(y / CellSize) & Mask;
        return (iy << Bits) | ix;
    }

    /// <summary>Append every entry whose cell survives <paramref name="planes"/> (six vec4s, the
    /// <see cref="CullPass.Planes"/> convention: inside when <c>dot(n, p) + d &gt;= 0</c>), plus every mover and
    /// every unbounded entry, into <paramref name="into"/> from <paramref name="n"/>; <paramref name="slack"/>
    /// grows each cell box so a cell about to enter view is yielded a frame early. Returns the new count.</summary>
    internal int Query(ReadOnlySpan<Vector4> planes, float slack, int[] into, int n)
    {
        for (int k = 0; k < _unboxedN && n < into.Length; k++) into[n++] = _unboxed[k];
        for (int k = 0; k < _moverN && n < into.Length; k++) into[n++] = _movers[k];
        for (int c = 0; c < Cells; c++)
        {
            int head = _head[c];
            if (head < 0) continue;
            if (!BoxInFrustum(planes, in _box[c], slack)) continue;
            for (int i = head; i >= 0 && n < into.Length; i = _next[i]) into[n++] = i;
        }
        return n;
    }

    /// <summary>Diagnostic: does the grid still describe where <paramref name="id"/>'s box is? A query reaches
    /// an entry only through its filed cell and tests that cell's UNION box, so a box that has left the union
    /// is invisible to every query. Reports the filed cell, the cell it would take now, and the cover.</summary>
    internal bool StillFiled(int id, in Box box, out int filed, out int now, out bool covered)
    {
        filed = (uint)id < (uint)_cellOf.Length ? _cellOf[id] : CellNone;
        now = Cell(box.Min.X, box.Min.Y);
        covered = false;
        if (filed < 0) return false;
        ref Box u = ref _box[filed];
        covered = box.Min.X >= u.Min.X && box.Min.Y >= u.Min.Y && box.Min.Z >= u.Min.Z
               && box.Max.X <= u.Max.X && box.Max.Y <= u.Max.Y && box.Max.Z <= u.Max.Z;
        return filed == now && covered;
    }

    /// <summary>The standard n-vertex box test: for each plane, the box corner FARTHEST along the plane normal.
    /// Behind every one of the six and the box cannot be in view.</summary>
    internal static bool BoxInFrustum(ReadOnlySpan<Vector4> planes, in Box box, float slack)
    {
        for (int f = 0; f < 6; f++)
        {
            Vector4 p = planes[f];
            float x = p.X >= 0f ? box.Max.X + slack : box.Min.X - slack;
            float y = p.Y >= 0f ? box.Max.Y + slack : box.Min.Y - slack;
            float z = p.Z >= 0f ? box.Max.Z + slack : box.Min.Z - slack;
            if (p.X * x + p.Y * y + p.Z * z + p.W < 0f) return false;
        }
        return true;
    }

    /// <summary>Link <paramref name="id"/> into <paramref name="cell"/>, growing that cell's union. An entry
    /// already in this cell only grows it; one in another cell is unfiled first, so an id is in exactly one.</summary>
    private void Link(int id, int cell, in Box box)
    {
        if (_cellOf[id] == cell) { Grow(cell, in box); return; }
        Remove(id);
        Grow(cell, in box);
        _next[id] = _head[cell];
        _head[cell] = id;
        _cellOf[id] = cell;
        _filed++;
    }

    private void Grow(int cell, in Box box)
    {
        ref Box u = ref _box[cell];
        // The first entry SETS the box; every later one only grows it. An empty cell's stale box would
        // otherwise swallow the whole area once anything was filed back into it.
        if (_head[cell] < 0) { _occupied++; u = box; return; }
        if (box.Min.X < u.Min.X) u.Min.X = box.Min.X;
        if (box.Min.Y < u.Min.Y) u.Min.Y = box.Min.Y;
        if (box.Min.Z < u.Min.Z) u.Min.Z = box.Min.Z;
        if (box.Max.X > u.Max.X) u.Max.X = box.Max.X;
        if (box.Max.Y > u.Max.Y) u.Max.Y = box.Max.Y;
        if (box.Max.Z > u.Max.Z) u.Max.Z = box.Max.Z;
    }
}
