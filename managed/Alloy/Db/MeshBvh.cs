using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>One model-space BVH per distinct <see cref="MeshDef"/>, built once on the tree worker and shared by every
/// placement of that mesh. Immutable once built, so any thread may read it. A mesh that ships an <see cref="AabbDef"/>
/// keeps the file's own tree, because the engine walks exactly that; everything else gets a median-split tree.</summary>
public sealed partial class MeshBvh
{
    /// <summary>Faces in a leaf. Below this the tree costs more pointer-chasing than the triangles it
    /// skips; measured against the self-check's synthetic meshes, not tuned live.</summary>
    private const int LeafFaces = 4;
    /// <summary>Traversal stack depth. A median-split tree over <see cref="MaxFaces"/> faces is ~16 deep and
    /// an MDL's own AABB tree is bounded by its own build, but neither is guaranteed, so an overflow is
    /// counted (<see cref="StackFull"/>) rather than silently dropping a subtree.</summary>
    private const int MaxDepth = 128;
    /// <summary>A mesh past this face count is left untreed, and its cast walks every face after the box reject, as
    /// the engine's own linear walk does: past it a tree's arrays cost more memory than the walk costs time.</summary>
    public const int MaxFaces = 1 << 16;

    /// <summary>Node bounds, in the same model space as <see cref="MeshDef.Positions"/>.</summary>
    private Vector3[] _bmin = Array.Empty<Vector3>(), _bmax = Array.Empty<Vector3>();
    /// <summary>For an inner node, the left child's index (the right child is <c>left + 1</c>); -1 for a
    /// leaf, whose faces are <see cref="_order"/>[<c>start</c> .. <c>start + count</c>).</summary>
    private int[] _left = Array.Empty<int>(), _start = Array.Empty<int>(), _count = Array.Empty<int>();
    private int[] _order = Array.Empty<int>();
    private int _nodes;
    /// <summary>The managed thread id that built this tree.</summary>
    private int _builtBy;

    /// <summary>The mesh this tree indexes.</summary>
    public MeshDef Mesh { get; private set; } = null!;
    /// <summary>True when the tree is usable; false for a mesh with no faces or one past
    /// <see cref="MaxFaces"/>, which the caller tests linearly instead.</summary>
    public bool Usable => _nodes > 0;

    /// <summary>Filed trees, keyed on the <see cref="MeshDef"/> instance, which the model cache already keys on
    /// content: one tree per distinct mesh, shared by every placement. Read and written under the worker's gate.</summary>
    private static readonly Dictionary<MeshDef, MeshBvh> _cache = new(QueueCap);
    /// <summary>Usable trees filed this session, and faces across them: the census line that says whether the
    /// index is being shared or rebuilt.</summary>
    public static int Built => BuiltOnGame + BuiltOnWorker;
    /// <inheritdoc cref="Built"/>
    public static long BuiltFaces => Volatile.Read(ref _w.Faces);
    /// <summary>Meshes refused for <see cref="MaxFaces"/> -- a loud count, not a quiet hole.</summary>
    public static int Refused => Volatile.Read(ref _w.Refused);
    /// <summary>Nodes that became leaves because the node array was full, and traversals that could not push
    /// a child because the stack was. Both are "correct but slower" or "a missed face" respectively, so both
    /// are counted and reported rather than inferred.</summary>
    public static long Degraded, StackFull;

    /// <summary>Trees dropped by a model eviction.</summary>
    public static int Evicted => Volatile.Read(ref _w.Evicted);

    /// <summary>Faces plane-tested since boot -- the one unit the pick report counts, written on every arm
    /// (the BVH's leaves, the untreed linear walk and the MDL's own AABB tree alike).</summary>
    public static long FacesTested;

    private void Build(MeshDef mesh)
    {
        Mesh = mesh;
        _builtBy = Environment.CurrentManagedThreadId;
        int n = mesh.Faces.Length;
        if (n == 0) return;
        if (n > MaxFaces) { Interlocked.Increment(ref _w.Refused); return; }

        _order = new int[n];
        var cx = new Vector3[n];
        for (int i = 0; i < n; i++)
        {
            _order[i] = i;
            ref FaceDef f = ref mesh.Faces[i];
            cx[i] = (mesh.Positions[f.V0] + mesh.Positions[f.V1] + mesh.Positions[f.V2]) * (1f / 3f);
        }
        // A median split halves the face set, so a leaf holds 2..LeafFaces faces: leaves <= n/2 and nodes
        // <= 2*leaves - 1 <= n. 2n + 2 is that with room for the odd split, and the counter below says if the
        // bound was ever reached -- a tree that quietly degraded to linear leaves is a silent slowdown.
        int cap = 2 * n + 2;
        _bmin = new Vector3[cap]; _bmax = new Vector3[cap];
        _left = new int[cap]; _start = new int[cap]; _count = new int[cap];
        _nodes = 1;
        Split(0, 0, n, cx, 0);
    }

    /// <summary>Median-split <paramref name="node"/> over the widest axis of its own bounds. Recursion depth
    /// is log2(faces) by construction; the depth cap is the degenerate case (every centroid equal).</summary>
    private void Split(int node, int start, int count, Vector3[] cx, int depth)
    {
        Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
        for (int k = start; k < start + count; k++)
        {
            ref FaceDef f = ref Mesh.Faces[_order[k]];
            lo = Vector3.Min(lo, Vector3.Min(Mesh.Positions[f.V0], Vector3.Min(Mesh.Positions[f.V1], Mesh.Positions[f.V2])));
            hi = Vector3.Max(hi, Vector3.Max(Mesh.Positions[f.V0], Vector3.Max(Mesh.Positions[f.V1], Mesh.Positions[f.V2])));
        }
        _bmin[node] = lo; _bmax[node] = hi; _start[node] = start; _count[node] = count;
        if (count <= LeafFaces || depth > 48) { _left[node] = -1; return; }
        if (_nodes + 2 > _bmin.Length) { _left[node] = -1; Interlocked.Increment(ref Degraded); return; }

        Vector3 d = hi - lo;
        int axis = d.X >= d.Y && d.X >= d.Z ? 0 : d.Y >= d.Z ? 1 : 2;
        int mid = start + count / 2;
        Select(start, start + count - 1, mid, axis, cx);
        _left[node] = _nodes;
        _nodes += 2;
        Split(_left[node], start, mid - start, cx, depth + 1);
        Split(_left[node] + 1, mid, start + count - mid, cx, depth + 1);
    }

    /// <summary>Quickselect <see cref="_order"/> so that index <paramref name="k"/> holds the median centroid
    /// on <paramref name="axis"/>. In place and allocation-free: a full sort per node would make the build
    /// O(n log^2 n) for nothing.</summary>
    private void Select(int lo, int hi, int k, int axis, Vector3[] cx)
    {
        while (lo < hi)
        {
            float p = Axis(cx[_order[(lo + hi) / 2]], axis);
            int i = lo, j = hi;
            while (i <= j)
            {
                while (Axis(cx[_order[i]], axis) < p) i++;
                while (Axis(cx[_order[j]], axis) > p) j--;
                if (i <= j) { (_order[i], _order[j]) = (_order[j], _order[i]); i++; j--; }
            }
            if (k <= j) hi = j; else if (k >= i) lo = i; else return;
        }
    }

    private static float Axis(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;

    /// <summary>Nearest face hit on the part-space segment <paramref name="from"/>-&gt;<paramref name="to"/>,
    /// with <paramref name="to"/> SHORTENED to the hit (the engine's own nearest-wins mechanism: every test
    /// downstream sees the shorter segment). <paramref name="scale"/> multiplies the mesh, matching
    /// <c>PartTriMesh::HitCheckGeom</c>'s scaled-vertex arm rather than dividing the ray. -1 for no hit.</summary>
    public int Cast(Vector3 from, ref Vector3 to, float scale, ulong surfaceMask, out Vector3 normal)
    {
        normal = default;
        int best = -1;
        if (Mesh.Faces.Length == 0) return -1;
        if (!Usable) return Walk(Mesh, from, ref to, scale, surfaceMask, out normal);
        Span<int> stack = stackalloc int[MaxDepth];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int node = stack[--sp];
            Vector3 lo = _bmin[node] * scale, hi = _bmax[node] * scale;
            if (!HitTest.BoxHit(from, to, lo, hi)) continue;
            if (_left[node] < 0)
            {
                for (int k = _start[node]; k < _start[node] + _count[node]; k++)
                    if (Face(Mesh, _order[k], from, ref to, scale, surfaceMask, out Vector3 nn)) { best = _order[k]; normal = nn; }
            }
            else if (sp + 2 <= stack.Length)
            {
                // Far child first so the near one pops first: the segment is shortened at every hit, so a
                // near-to-far order is what makes the far subtree's box test reject instead of recurse.
                int near = Near(node, from, to), far = _left[node] + _left[node] + 1 - near;
                stack[sp++] = far; stack[sp++] = near;
            }
            else StackFull++;
        }
        return best;
    }

    /// <summary>The segment cast against <paramref name="mesh"/>: through its tree once one is filed or built here, by a
    /// walk over every face while the worker holds it mid-build, so a pick never waits on the worker.</summary>
    public static int CastMesh(MeshDef mesh, Vector3 from, ref Vector3 to, float scale, ulong surfaceMask, out Vector3 normal)
    {
        MeshBvh? tree = For(mesh);
        if (tree != null) return tree.Cast(from, ref to, scale, surfaceMask, out normal);
        return Walk(mesh, from, ref to, scale, surfaceMask, out normal);
    }

    /// <summary>Nearest face hit by a walk over every face of <paramref name="mesh"/>, shortening the segment as
    /// <see cref="Cast"/> does; -1 for no hit.</summary>
    private static int Walk(MeshDef mesh, Vector3 from, ref Vector3 to, float scale, ulong surfaceMask, out Vector3 normal)
    {
        normal = default;
        int best = -1;
        for (int i = 0; i < mesh.Faces.Length; i++)
            if (Face(mesh, i, from, ref to, scale, surfaceMask, out Vector3 nn)) { best = i; normal = nn; }
        return best;
    }

    /// <summary>Faces of <paramref name="mesh"/> whose boxes touch the mesh-space box <paramref name="lo"/>..
    /// <paramref name="hi"/>, into <paramref name="faces"/>: through its tree if one is already built, else by a walk
    /// over every face. Never builds one, so it is safe inside a frame. <paramref name="full"/> says faces were lost.</summary>
    public static int Overlap(MeshDef mesh, Vector3 lo, Vector3 hi, Span<int> faces, out bool full)
    {
        MeshBvh? tree;
        lock (_w.Gate) _cache.TryGetValue(mesh, out tree);
        if (tree != null && tree.Usable) return tree.Overlap(lo, hi, faces, out full);
        int n = 0;
        full = false;
        for (int i = 0; i < mesh.Faces.Length; i++)
        {
            ref FaceDef f = ref mesh.Faces[i];
            if ((uint)f.V0 >= (uint)mesh.Positions.Length || (uint)f.V1 >= (uint)mesh.Positions.Length
                || (uint)f.V2 >= (uint)mesh.Positions.Length) continue;
            Vector3 a = mesh.Positions[f.V0], b = mesh.Positions[f.V1], c = mesh.Positions[f.V2];
            if (!Touch(Vector3.Min(a, Vector3.Min(b, c)), Vector3.Max(a, Vector3.Max(b, c)), lo, hi)) continue;
            if (n == faces.Length) { full = true; break; }
            faces[n++] = i;
        }
        return n;
    }

    /// <summary>The tree's half of <see cref="Overlap(MeshDef, Vector3, Vector3, Span{int}, out bool)"/>: the faces
    /// of every leaf whose box touches the box, unfiltered.</summary>
    private int Overlap(Vector3 lo, Vector3 hi, Span<int> faces, out bool full)
    {
        int n = 0;
        full = false;
        Span<int> stack = stackalloc int[MaxDepth];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int node = stack[--sp];
            if (!Touch(_bmin[node], _bmax[node], lo, hi)) continue;
            if (_left[node] >= 0)
            {
                if (sp + 2 <= stack.Length) { stack[sp++] = _left[node]; stack[sp++] = _left[node] + 1; }
                else { StackFull++; full = true; }
                continue;
            }
            if (n + _count[node] > faces.Length) { full = true; break; }
            for (int k = _start[node]; k < _start[node] + _count[node]; k++) faces[n++] = _order[k];
        }
        return n;
    }

    private static bool Touch(Vector3 alo, Vector3 ahi, Vector3 blo, Vector3 bhi)
        => alo.X <= bhi.X && ahi.X >= blo.X && alo.Y <= bhi.Y && ahi.Y >= blo.Y && alo.Z <= bhi.Z && ahi.Z >= blo.Z;

    /// <summary>Which child of <paramref name="node"/> the segment reaches first, by the squared distance
    /// from its origin to each child's box.</summary>
    private int Near(int node, Vector3 from, Vector3 to)
    {
        int l = _left[node];
        return Dist2(_bmin[l], _bmax[l], from) <= Dist2(_bmin[l + 1], _bmax[l + 1], from) ? l : l + 1;
    }

    private static float Dist2(Vector3 lo, Vector3 hi, Vector3 p)
    {
        Vector3 d = Vector3.Max(Vector3.Zero, Vector3.Max(lo - p, p - hi));
        return Vector3.Dot(d, d);
    }

    private static float Dist2(in AabbNode n, Vector3 p) => Dist2(n.BMin, n.BMax, p);

    private static bool Face(MeshDef mesh, int i, Vector3 from, ref Vector3 to, float scale, ulong surfaceMask, out Vector3 normal)
    {
        normal = default;
        ref FaceDef f = ref mesh.Faces[i];
        if ((surfaceMask >> (f.Surface & 63) & 1) == 0) return false;
        FacesTested++;
        Vector3 a = mesh.Positions[f.V0] * scale, b = mesh.Positions[f.V1] * scale, c = mesh.Positions[f.V2] * scale;
        // The face's STORED plane, which is what the 7-argument polyhit takes (face+0 and face+0xc); scaling
        // the vertices scales the plane distance with them and leaves the normal alone.
        if (!HitTest.PolyHit(a, b, c, f.PlaneNormal, f.PlaneDistance * scale, from, ref to)) return false;
        normal = f.PlaneNormal;
        return true;
    }

    /// <summary>Walk an <see cref="AabbDef"/> -- the file's own tree, which is what the engine's
    /// <c>HitCheckAABBnode</c> walks for a <c>PartAABB</c> part. Front-to-back by the ray's sign on the split
    /// axis, with the segment shortened at every hit.</summary>
    public static int CastAabb(AabbDef tree, MeshDef mesh, Vector3 from, ref Vector3 to, float scale,
                              ulong surfaceMask, out Vector3 normal)
    {
        normal = default;
        int best = -1;
        if (tree.Tree.Length == 0) return -1;
        Span<int> stack = stackalloc int[64];
        int sp = 0;
        stack[sp++] = 0;
        while (sp > 0)
        {
            int ni = stack[--sp];
            ref AabbNode nd = ref tree.Tree[ni];
            if (!HitTest.BoxHit(from, to, nd.BMin * scale, nd.BMax * scale)) continue;
            if (nd.Face >= 0)
            {
                if (nd.Face >= mesh.Faces.Length) continue;
                ref FaceDef f = ref mesh.Faces[nd.Face];
                if ((surfaceMask >> (f.Surface & 63) & 1) == 0) continue;
                FacesTested++;
                Vector3 a = mesh.Positions[f.V0] * scale, b = mesh.Positions[f.V1] * scale, c = mesh.Positions[f.V2] * scale;
                if (HitTest.PolyHit(a, b, c, f.PlaneNormal, f.PlaneDistance * scale, from, ref to))
                { best = nd.Face; normal = f.PlaneNormal; }
            }
            else if (sp + 2 <= stack.Length)
            {
                // The engine's HitCheckAABBnode @0x43da50 visits the child the ray's SIGN on the node's split
                // axis points at first (its mask at node+0x2c against the per-axis sign bits). Same order
                // here, from the children's own boxes, so the nearer subtree shortens the segment first.
                int l = nd.Left, rr = nd.Right;
                if (l >= 0 && rr >= 0)
                {
                    bool lNear = Dist2(tree.Tree[l], from) <= Dist2(tree.Tree[rr], from);
                    stack[sp++] = lNear ? rr : l;
                    stack[sp++] = lNear ? l : rr;
                }
                else if (l >= 0) stack[sp++] = l;
                else if (rr >= 0) stack[sp++] = rr;
            }
            else StackFull++;
        }
        return best;
    }
}
