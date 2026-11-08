using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The engine's one walkmesh per cell: <c>SceneBSPAddPart</c> @0x47ca10 stores a static's AABB part in its
/// BSP leaf's single <c>+0x90</c> slot, so the last one filed in a tile cell is that cell's only walkmesh, and
/// <c>HitPartOnBSPNode</c> @0x47b790 tests it against the segment clipped to the cell.</summary>
public static class WalkCells
{
    private static WalkCellsState _state = new();

    /// <summary>Stamp static <paramref name="o"/>'s filing order, once per tenancy: <c>Scene::SpawnTile</c> files
    /// a tile's walkmesh, or a static placeable's, in the order the seam hands them over.</summary>
    public static void Filed(DbId o)
    {
        if (!_state.Order.ContainsKey(o.Value)) _state.Order[o.Value] = ++_state.Seq;
    }

    /// <summary>File static <paramref name="o"/>'s AABB parts under the cell holding each part's world pivot, the
    /// point <c>BSPFitSphere</c> descends with. Called once the part matrices are current.</summary>
    public static void Offer(int o)
    {
        DbId obj = SceneDb.Objects.IdAt(o);
        if (!_state.Order.TryGetValue(obj.Value, out long seq)) return;
        DbId scene = SceneDb.ObjScene[o];
        Sweep(scene);
        int first = SceneDb.ObjPartFirst[o];
        for (int p = first; p < first + SceneDb.ObjPartCount[o]; p++)
        {
            ModelDef? m = SceneDb.PartModel[p];
            int node = SceneDb.PartNode[p];
            if (m == null || (uint)node >= (uint)m.Nodes.Length || m.Nodes[node].Kind != NodeKind.Aabb) continue;
            DbId part = SceneDb.Parts.IdAt(p);
            Vector3 pivot = SceneDb.PartWorld[p].Translation;
            var key = (scene.Value, Leaf(pivot.X), Leaf(pivot.Y));
            if (_state.CellOf.TryGetValue(part.Value, out var was))
            {
                if (was == key) continue;
                if (_state.Cells.TryGetValue(was, out List<WalkEntry>? old)) old.RemoveAll(e => e.Part == part);
            }
            _state.CellOf[part.Value] = key;
            if (!_state.Cells.TryGetValue(key, out List<WalkEntry>? list)) _state.Cells[key] = list = new List<WalkEntry>(2);
            list.Add(new WalkEntry(part, seq));
        }
    }

    /// <summary>Narrow the world segment <paramref name="a"/>-<paramref name="b"/> to what static walkmesh part
    /// <paramref name="p"/> is tested on: nothing unless it is its cell's last filed, else the stretch over its
    /// cell. False = the part answers nothing on this segment.</summary>
    public static bool Clip(int p, ref Vector3 a, ref Vector3 b)
    {
        DbId part = SceneDb.Parts.IdAt(p);
        if (!_state.CellOf.TryGetValue(part.Value, out var key) || !_state.Cells.TryGetValue(key, out List<WalkEntry>? cell))
            return true;
        DbId owner = default;
        long best = -1;
        foreach (WalkEntry e in cell)
        {
            if (!SceneDb.Parts.Valid(e.Part)) continue;
            if (e.Seq > best || (e.Seq == best && e.Part.Index > owner.Index)) { best = e.Seq; owner = e.Part; }
        }
        if (owner != part) return false;
        float x0 = key.Item2 * TileBuild.TileSize - HitTest.EdgeEps, y0 = key.Item3 * TileBuild.TileSize - HitTest.EdgeEps;
        float x1 = x0 + TileBuild.TileSize + 2f * HitTest.EdgeEps, y1 = y0 + TileBuild.TileSize + 2f * HitTest.EdgeEps;
        Vector3 d = b - a;
        float t0 = 0f, t1 = 1f;
        if (!Slab(a.X, d.X, x0, x1, ref t0, ref t1) || !Slab(a.Y, d.Y, y0, y1, ref t0, ref t1)) return false;
        Vector3 start = a;
        a = start + d * t0;
        b = start + d * t1;
        return true;
    }

    /// <summary>The leaf index along one axis: a pivot on a boundary goes to the lower cell, as the BSP's
    /// <c>n.p + d &lt;= 0</c> branch sends it.</summary>
    internal static int Leaf(float v) => (int)MathF.Ceiling(v / TileBuild.TileSize) - 1;

    private static bool Slab(float p, float d, float lo, float hi, ref float t0, ref float t1)
    {
        if (MathF.Abs(d) < 1e-12f) return p >= lo && p <= hi;
        float ta = (lo - p) / d, tb = (hi - p) / d;
        if (ta > tb) (ta, tb) = (tb, ta);
        t0 = MathF.Max(t0, ta);
        t1 = MathF.Min(t1, tb);
        return t0 <= t1;
    }

    /// <summary>Drop what dead tenants left, when a new scene starts filing: cold, once per scene change.</summary>
    private static void Sweep(DbId scene)
    {
        if (scene.Value == _state.Scene) return;
        _state.Scene = scene.Value;
        foreach (uint k in _state.Order.Keys) if (!SceneDb.Objects.Valid(new DbId(k))) _state.Order.Remove(k);
        foreach (uint k in _state.CellOf.Keys) if (!SceneDb.Parts.Valid(new DbId(k))) _state.CellOf.Remove(k);
        foreach (var k in _state.Cells.Keys) if (!SceneDb.Scenes.Valid(new DbId(k.Item1))) _state.Cells.Remove(k);
    }
}

/// <summary>One static walkmesh part in a cell, with its object's filing order.</summary>
internal readonly record struct WalkEntry(DbId Part, long Seq);

/// <summary>The per-cell walkmesh books: filing order by object, cell by part, and the parts in each cell.</summary>
internal sealed class WalkCellsState
{
    public long Seq;
    public uint Scene;
    public readonly Dictionary<uint, long> Order = new();
    public readonly Dictionary<uint, (uint, int, int)> CellOf = new();
    public readonly Dictionary<(uint, int, int), List<WalkEntry>> Cells = new();
}
