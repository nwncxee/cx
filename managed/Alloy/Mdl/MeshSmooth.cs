using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CX.Alloy.Mdl;

/// <summary>
/// alloy.smooth: PN triangles at load. A triangle with a curved edge is split into a grid on the cubic patch its corner
/// normals describe. An edge curves only where every face on it agrees on its normals and they lie near each face, so
/// creases, open borders and card foliage stay straight and nothing cracks. Original vertices keep their indices.
/// </summary>
internal static partial class MeshSmooth
{
    /// <summary>Three indices of a triangle list, as one record.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Tri { public uint A, B, C; }

    /// <summary>A corner normal further than this from its face's (cos 40 deg) is a shading trick, not a surface.</summary>
    private const float NearFaceCos = 0.766f;
    /// <summary>Two corners' normals at one point agree past this (cos ~2.5 deg).</summary>
    private const float SameNormalCos = 0.999f;
    /// <summary>Two faces this close to coplanar (cos ~5 deg) meet on a flat surface: their edge stays straight, so a table
    /// top whose rim normals lean out is not domed over what sits on it.</summary>
    private const float FlatCos = 0.996f;
    /// <summary>On a true arc each end's normal tilts off the edge's perpendicular by half the bend between them; this slack
    /// over that, plus a floor, is what an edge may tilt and still curve. A long pole whose normals all lean one way is
    /// sheared, not curved, and PN triangles swell it by a third of its length.</summary>
    private const float ArcSlack = 1.5f, ArcFloor = 0.02f;
    /// <summary>An edge whose curve moves less than this share of its length is left straight.</summary>
    private const float MinBulge = 0.01f;
    /// <summary>Position weld step, model units: corners closer than this are one point.</summary>
    private const float WeldStep = 1e-4f;
    /// <summary>Triangles a smoothed mesh may end with; a split level that would pass it steps down.</summary>
    private const int MaxTris = 40000;
    private const int FineLevel = 3, CoarseLevel = 2;

    /// <summary>One welded edge: its ends' points and normals (from the first face met), and whether it curves.</summary>
    private sealed class Edge
    {
        public Vector3 PLo, PHi, NLo, NHi, Face;
        public int Faces;
        public bool Curved = true;
    }

    /// <summary>Smooth every eligible mesh of <paramref name="m"/>; how many were. Skinned models, heads and creatures
    /// (<c>c_</c>) are left alone; armour and clothing parts round off.</summary>
    internal static int Run(ModelDef m)
    {
        if (IsCharacter(m.Name)) return 0;
        foreach (NodeDef n in m.Nodes)
            if (n.Kind is NodeKind.Skin or NodeKind.AnimMesh) return 0;
        int done = 0;
        foreach (NodeDef n in m.Nodes)
            if (n.Kind == NodeKind.TriMesh && n.Mesh is { Render: true, WalkMesh: false } mesh && Smooth(mesh)) done++;
        return done;
    }

    private static bool IsCharacter(string name)
    {
        // creatures, and the heads of the player models; their armour and clothing parts may round off
        string s = name.ToLowerInvariant();
        return s.StartsWith("c_", StringComparison.Ordinal) || s.Contains("_head", StringComparison.Ordinal);
    }

    /// <summary>Recursion-free PN split of <paramref name="m"/> in place; false (untouched) where nothing curves, the
    /// arrays disagree, or the budget is passed.</summary>
    internal static bool Smooth(MeshDef m)
    {
        int nv = m.Positions.Length;
        if (nv == 0 || m.Normals.Length != nv || m.Indices.Length < 3 || m.Indices.Length % 3 != 0) return false;
        if (m.Faces.Length * 3 != m.Indices.Length) return false;
        if (m.Tangents.Length is not 0 && m.Tangents.Length != nv) return false;
        if (m.Colors.Length is not 0 && m.Colors.Length != nv) return false;
        foreach (Vector2[] uv in m.Uv) if (uv.Length is not 0 && uv.Length != nv) return false;
        ReadOnlySpan<Tri> tris = MemoryMarshal.Cast<uint, Tri>(m.Indices.AsSpan());
        foreach (Tri t in tris) if (t.A >= nv || t.B >= nv || t.C >= nv) return false;

        int[] pid = Weld(m.Positions);
        var edges = new Dictionary<long, Edge>();
        foreach (Tri t in tris)
        {
            Vector3 fn = FaceNormal(m, t);
            Note(edges, m, pid, t.A, t.B, fn);
            Note(edges, m, pid, t.B, t.C, fn);
            Note(edges, m, pid, t.C, t.A, fn);
        }
        foreach (Edge e in edges.Values)
            if (e.Curved && (e.Faces != 2 || Bulge(e) < MinBulge)) e.Curved = false;

        int curved = 0;
        foreach (Tri t in tris) if (Splits(edges, pid, t)) curved++;
        if (curved == 0) return false;
        int flat = tris.Length - curved;
        int level = curved * FineLevel * FineLevel + flat <= MaxTris ? FineLevel
                  : curved * CoarseLevel * CoarseLevel + flat <= MaxTris ? CoarseLevel : 0;
        if (level == 0) return false;

        var b = new Builder(m);
        var outTris = new List<Tri>(flat + curved * level * level);
        var faces = new List<FaceDef>(outTris.Capacity);
        Span<uint> prev = stackalloc uint[FineLevel + 1], row = stackalloc uint[FineLevel + 1];
        for (int f = 0; f < tris.Length; f++)
        {
            Tri t = tris[f];
            if (!Splits(edges, pid, t)) { outTris.Add(t); faces.Add(m.Faces[f]); continue; }
            Patch p = PatchOf(m, edges, pid, t);
            // the grid: row i walks from corner A, column j toward B; each cell is one or two triangles
            for (int i = 0; i <= level; i++)
            {
                for (int j = 0; j <= level - i; j++) row[j] = b.At(m, p, t, level - i - j, j, i, level, edges, pid);
                if (i > 0)
                    for (int j = 0; j < level - i + 1; j++)
                    {
                        outTris.Add(new Tri { A = prev[j], B = prev[j + 1], C = row[j] });
                        if (j < level - i) outTris.Add(new Tri { A = prev[j + 1], B = row[j + 1], C = row[j] });
                    }
                row.CopyTo(prev);
            }
            while (faces.Count < outTris.Count) faces.Add(m.Faces[f]);
        }
        b.Finish(m);
        m.Indices = MemoryMarshal.Cast<Tri, uint>(CollectionsMarshal.AsSpan(outTris)).ToArray();
        m.Faces = faces.ToArray();
        ReadOnlySpan<Tri> fine = MemoryMarshal.Cast<uint, Tri>(m.Indices.AsSpan());
        for (int f = 0; f < m.Faces.Length; f++) Replane(ref m.Faces[f], m, fine[f]);
        Rebound(m);
        return true;
    }

    /// <summary>Per vertex, the id of its welded point.</summary>
    private static int[] Weld(Vector3[] pos)
    {
        var ids = new Dictionary<(int, int, int), int>();
        int[] pid = new int[pos.Length];
        for (int v = 0; v < pos.Length; v++)
        {
            var k = ((int)MathF.Round(pos[v].X / WeldStep), (int)MathF.Round(pos[v].Y / WeldStep), (int)MathF.Round(pos[v].Z / WeldStep));
            if (!ids.TryGetValue(k, out int id)) ids[k] = id = ids.Count;
            pid[v] = id;
        }
        return pid;
    }

    private static long EdgeKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

    /// <summary>One face's side (<paramref name="va"/>, <paramref name="vb"/>) filed on its welded edge.</summary>
    private static void Note(Dictionary<long, Edge> edges, MeshDef m, int[] pid, uint va, uint vb, Vector3 fn)
    {
        int a = pid[va], b = pid[vb];
        if (a == b) return;
        bool lo = a < b;
        Vector3 nLo = m.Normals[lo ? va : vb], nHi = m.Normals[lo ? vb : va];
        bool near = fn != Vector3.Zero && Vector3.Dot(nLo, fn) >= NearFaceCos && Vector3.Dot(nHi, fn) >= NearFaceCos
                    && Arc(m.Positions[lo ? va : vb], m.Positions[lo ? vb : va], nLo, nHi);
        if (!edges.TryGetValue(EdgeKey(a, b), out Edge? e))
        {
            edges[EdgeKey(a, b)] = new Edge
            {
                PLo = m.Positions[lo ? va : vb], PHi = m.Positions[lo ? vb : va], NLo = nLo, NHi = nHi, Face = fn, Faces = 1, Curved = near,
            };
            return;
        }
        e.Faces++;
        if (!near || Vector3.Dot(e.NLo, nLo) < SameNormalCos || Vector3.Dot(e.NHi, nHi) < SameNormalCos
            || Vector3.Dot(e.Face, fn) >= FlatCos) e.Curved = false;
    }

    /// <summary>Do the end normals bend round the edge as an arc does: each tilting away from the other end, by no more
    /// than the bend between them allows?</summary>
    private static bool Arc(Vector3 pLo, Vector3 pHi, Vector3 nLo, Vector3 nHi)
    {
        Vector3 d = pHi - pLo;
        float len = d.Length();
        if (len < 1e-6f) return false;
        float wLo = Vector3.Dot(d, nLo) / len, wHi = Vector3.Dot(d, nHi) / len;
        if (wLo * wHi > ArcFloor * ArcFloor) return false;   // both lean one way: an S or a shear
        float sinHalf = MathF.Sqrt(MathF.Max(0f, (1f - Vector3.Dot(nLo, nHi)) * 0.5f));
        return MathF.Max(MathF.Abs(wLo), MathF.Abs(wHi)) <= sinHalf * ArcSlack + ArcFloor;
    }

    private static Vector3 FaceNormal(MeshDef m, Tri t)
    {
        Vector3 n = Vector3.Cross(m.Positions[t.B] - m.Positions[t.A], m.Positions[t.C] - m.Positions[t.A]);
        float len = n.Length();
        return len > 1e-12f ? n / len : Vector3.Zero;
    }

    /// <summary>How far the edge's control points leave the straight line, over its length.</summary>
    private static float Bulge(Edge e)
    {
        Vector3 d = e.PHi - e.PLo;
        float len = d.Length();
        if (len < 1e-6f) return 0f;
        float w0 = Vector3.Dot(d, e.NLo), w1 = Vector3.Dot(-d, e.NHi);
        return MathF.Max(MathF.Abs(w0), MathF.Abs(w1)) / (3f * len);
    }

    private static bool Splits(Dictionary<long, Edge> edges, int[] pid, Tri t)
        => Curves(edges, pid, t.A, t.B) || Curves(edges, pid, t.B, t.C) || Curves(edges, pid, t.C, t.A);

    private static bool Curves(Dictionary<long, Edge> edges, int[] pid, uint a, uint b)
        => pid[a] != pid[b] && edges.TryGetValue(EdgeKey(pid[a], pid[b]), out Edge? e) && e.Curved;

    /// <summary>A triangle's ten cubic control points, corner A first.</summary>
    private struct Patch
    {
        public Vector3 B300, B030, B003, B210, B120, B021, B012, B102, B201, B111;
    }

    private static Patch PatchOf(MeshDef m, Dictionary<long, Edge> edges, int[] pid, Tri t)
    {
        var p = new Patch { B300 = m.Positions[t.A], B030 = m.Positions[t.B], B003 = m.Positions[t.C] };
        (p.B210, p.B120) = Controls(m, edges, pid, t.A, t.B);
        (p.B021, p.B012) = Controls(m, edges, pid, t.B, t.C);
        (p.B102, p.B201) = Controls(m, edges, pid, t.C, t.A);
        Vector3 e = (p.B210 + p.B120 + p.B021 + p.B012 + p.B102 + p.B201) / 6f, v = (p.B300 + p.B030 + p.B003) / 3f;
        p.B111 = e + (e - v) * 0.5f;
        return p;
    }

    /// <summary>The two inner control points of side (<paramref name="a"/>, <paramref name="b"/>), nearest a first: from the
    /// edge's own filed points and normals, so both faces on it build the same curve.</summary>
    private static (Vector3, Vector3) Controls(MeshDef m, Dictionary<long, Edge> edges, int[] pid, uint a, uint b)
    {
        Vector3 pa = m.Positions[a], pb = m.Positions[b];
        if (!Curves(edges, pid, a, b)) return ((2f * pa + pb) / 3f, (pa + 2f * pb) / 3f);
        Edge e = edges[EdgeKey(pid[a], pid[b])];
        Vector3 lo = Control(e.PLo, e.PHi, e.NLo), hi = Control(e.PHi, e.PLo, e.NHi);
        return pid[a] < pid[b] ? (lo, hi) : (hi, lo);
    }

    private static Vector3 Control(Vector3 pi, Vector3 pj, Vector3 ni) => (2f * pi + pj - Vector3.Dot(pj - pi, ni) * ni) / 3f;

    /// <summary>The new vertex arrays: the originals first, unchanged, then every point a split adds; a point on an edge
    /// is shared by both faces that meet it there.</summary>
    private sealed class Builder
    {
        private readonly List<Vector3> _pos, _nrm;
        private readonly List<Vector4> _tan;
        private readonly List<Vector2>[] _uv;
        private readonly List<uint> _col;
        private readonly Dictionary<(uint, uint, int), uint> _onEdge = new();

        public Builder(MeshDef m)
        {
            _pos = new List<Vector3>(m.Positions); _nrm = new List<Vector3>(m.Normals); _tan = new List<Vector4>(m.Tangents);
            _col = new List<uint>(m.Colors);
            _uv = new List<Vector2>[m.Uv.Length];
            for (int s = 0; s < _uv.Length; s++) _uv[s] = new List<Vector2>(m.Uv[s]);
        }

        /// <summary>The vertex at barycentric (<paramref name="i"/>, <paramref name="j"/>, <paramref name="k"/>) over
        /// <paramref name="n"/> of face <paramref name="t"/>: a corner, an edge point, or an interior point.</summary>
        public uint At(MeshDef m, Patch p, Tri t, int i, int j, int k, int n, Dictionary<long, Edge> edges, int[] pid)
        {
            if (i == n) return t.A;
            if (j == n) return t.B;
            if (k == n) return t.C;
            if (k == 0) return OnEdge(m, t.A, t.B, j, n, edges, pid);
            if (i == 0) return OnEdge(m, t.B, t.C, k, n, edges, pid);
            if (j == 0) return OnEdge(m, t.C, t.A, i, n, edges, pid);
            float u = (float)i / n, v = (float)j / n, w = (float)k / n;
            Vector3 pos = p.B300 * (u * u * u) + p.B030 * (v * v * v) + p.B003 * (w * w * w)
                        + 3f * (p.B210 * (u * u * v) + p.B120 * (u * v * v) + p.B201 * (u * u * w)
                              + p.B021 * (v * v * w) + p.B102 * (u * w * w) + p.B012 * (v * w * w))
                        + 6f * p.B111 * (u * v * w);
            return Add(m, pos, t, u, v, w);
        }

        /// <summary>Point <paramref name="step"/> of <paramref name="n"/> from <paramref name="a"/> toward <paramref name="b"/>,
        /// made once per vertex pair: the curve is evaluated from the lower vertex, so both faces get the same bits.</summary>
        private uint OnEdge(MeshDef m, uint a, uint b, int step, int n, Dictionary<long, Edge> edges, int[] pid)
        {
            uint lo = Math.Min(a, b), hi = Math.Max(a, b);
            int s = a == lo ? step : n - step;
            if (_onEdge.TryGetValue((lo, hi, s), out uint have)) return have;
            float t = (float)s / n;
            // the position from the welded edge's lower point, so a UV seam's two vertex pairs land on the same bits
            bool fwd = pid[lo] <= pid[hi];
            float tp = (float)(fwd ? s : n - s) / n, r = 1f - tp;
            Vector3 pos;
            if (Curves(edges, pid, lo, hi))
            {
                Edge e = edges[EdgeKey(pid[lo], pid[hi])];
                Vector3 c1 = Control(e.PLo, e.PHi, e.NLo), c2 = Control(e.PHi, e.PLo, e.NHi);
                pos = e.PLo * (r * r * r) + 3f * c1 * (r * r * tp) + 3f * c2 * (r * tp * tp) + e.PHi * (tp * tp * tp);
            }
            else pos = fwd ? Vector3.Lerp(m.Positions[lo], m.Positions[hi], tp) : Vector3.Lerp(m.Positions[hi], m.Positions[lo], tp);
            uint v = AddLerp(m, pos, lo, hi, t);
            _onEdge[(lo, hi, s)] = v;
            return v;
        }

        private uint AddLerp(MeshDef m, Vector3 pos, uint a, uint b, float t)
        {
            _pos.Add(pos);
            _nrm.Add(Unit(Vector3.Lerp(m.Normals[a], m.Normals[b], t), m.Normals[a]));
            if (m.Tangents.Length != 0) _tan.Add(Tangent(Vector4.Lerp(m.Tangents[a], m.Tangents[b], t), m.Tangents[a].W));
            for (int s = 0; s < _uv.Length; s++)
                if (m.Uv[s].Length != 0) _uv[s].Add(Vector2.Lerp(m.Uv[s][a], m.Uv[s][b], t));
            if (m.Colors.Length != 0) _col.Add(Colour(m.Colors[a], m.Colors[b], 0u, 1f - t, t, 0f));
            return (uint)(_pos.Count - 1);
        }

        private uint Add(MeshDef m, Vector3 pos, Tri t, float u, float v, float w)
        {
            _pos.Add(pos);
            _nrm.Add(Unit(m.Normals[t.A] * u + m.Normals[t.B] * v + m.Normals[t.C] * w, m.Normals[t.A]));
            if (m.Tangents.Length != 0)
                _tan.Add(Tangent(m.Tangents[t.A] * u + m.Tangents[t.B] * v + m.Tangents[t.C] * w, m.Tangents[t.A].W));
            for (int s = 0; s < _uv.Length; s++)
                if (m.Uv[s].Length != 0) _uv[s].Add(m.Uv[s][t.A] * u + m.Uv[s][t.B] * v + m.Uv[s][t.C] * w);
            if (m.Colors.Length != 0) _col.Add(Colour(m.Colors[t.A], m.Colors[t.B], m.Colors[t.C], u, v, w));
            return (uint)(_pos.Count - 1);
        }

        /// <summary><paramref name="v"/> normalised, or <paramref name="fallback"/> where opposed normals cancelled.</summary>
        private static Vector3 Unit(Vector3 v, Vector3 fallback)
        {
            float len = v.Length();
            return len > 1e-6f ? v / len : fallback;
        }

        private static Vector4 Tangent(Vector4 t, float hand)
        {
            var xyz = new Vector3(t.X, t.Y, t.Z);
            float len = xyz.Length();
            xyz = len > 1e-12f ? xyz / len : Vector3.UnitX;
            return new Vector4(xyz, hand);
        }

        /// <summary>Three RGBA8 colours blended byte by byte.</summary>
        private static uint Colour(uint a, uint b, uint c, float u, float v, float w)
        {
            uint o = 0;
            for (int sh = 0; sh < 32; sh += 8)
            {
                float x = ((a >> sh) & 0xFF) * u + ((b >> sh) & 0xFF) * v + ((c >> sh) & 0xFF) * w;
                o |= (uint)Math.Clamp((int)(x + 0.5f), 0, 255) << sh;
            }
            return o;
        }

        public void Finish(MeshDef m)
        {
            m.Positions = _pos.ToArray(); m.Normals = _nrm.ToArray();
            if (m.Tangents.Length != 0) m.Tangents = _tan.ToArray();
            if (m.Colors.Length != 0) m.Colors = _col.ToArray();
            var uv = new Vector2[m.Uv.Length][];
            for (int s = 0; s < uv.Length; s++) uv[s] = m.Uv[s].Length != 0 ? _uv[s].ToArray() : Array.Empty<Vector2>();
            m.Uv = uv;
        }
    }

    /// <summary>A split face keeps its parent's material and smoothing group, with its own corners and plane.</summary>
    private static void Replane(ref FaceDef f, MeshDef m, Tri t)
    {
        Vector3 n = FaceNormal(m, t);
        if (n == Vector3.Zero) n = Vector3.UnitZ;
        f.V0 = (int)t.A; f.V1 = (int)t.B; f.V2 = (int)t.C;
        f.Adj0 = f.Adj1 = f.Adj2 = -1;
        f.PlaneNormal = n;
        f.PlaneDistance = -Vector3.Dot(n, m.Positions[t.A]);
    }

    /// <summary>The bounds grown over the bulge.</summary>
    private static void Rebound(MeshDef m)
    {
        Vector3 lo = m.BMin, hi = m.BMax;
        float r2 = m.Radius * m.Radius;
        foreach (Vector3 p in m.Positions)
        {
            lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p);
            r2 = MathF.Max(r2, Vector3.DistanceSquared(p, m.Average));
        }
        m.BMin = lo; m.BMax = hi; m.Radius = MathF.Sqrt(r2);
    }
}
