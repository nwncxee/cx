using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Physics;

/// <summary>The static surfaces one Swing part may touch (docs/spec/dangly-motions.md, section 5b): world faces,
/// each a plane and three inward edge planes, and per joint the faces it rests in front of with the clearance it
/// keeps from each. Gathered around the part's rest pose by <see cref="DanglyGather"/>.</summary>
public sealed class DanglyColliders
{
    /// <summary>Faces one part keeps, and joint-face pairs across them.</summary>
    public const int MaxFaces = 64, MaxPairs = 256;

    /// <summary>How much of a joint's slide along a face one unit of push takes back.</summary>
    private const float Friction = 0.5f;

    /// <summary>How far past a face's edges (metres) a joint still meets it, so the seam between two faces of
    /// one wall is not a gap.</summary>
    internal const float EdgeSlack = 0.02f;

    /// <summary>A joint this little short of its clearance at a substep's start still counts as in front.</summary>
    private const float Slop = 1e-3f;

    internal readonly Vector4[] Plane = new Vector4[MaxFaces], Edge0 = new Vector4[MaxFaces],
                                Edge1 = new Vector4[MaxFaces], Edge2 = new Vector4[MaxFaces];
    /// <summary>Per joint, its run of pairs: each a face and the least distance the joint keeps from its plane.</summary>
    internal readonly int[] First = new int[DanglyRig.MaxJoints], Count = new int[DanglyRig.MaxJoints];
    internal readonly int[] PairFace = new int[MaxPairs];
    internal readonly float[] PairClear = new float[MaxPairs];
    public int Faces, Pairs;

    /// <summary>Every joint this substep took into a face it rested in front of, back out to its clearance along
    /// the face normal, its slide along the face held back by friction. A joint already short of its clearance at
    /// the substep's start came round the face's edge, not through it, and is left where it is.</summary>
    internal void Push(Span<Vector3> x, ReadOnlySpan<Vector3> was, int nj)
    {
        for (int j = 0; j < nj && j < First.Length; j++)
            for (int k = First[j], end = First[j] + Count[j]; k < end; k++)
            {
                int f = PairFace[k];
                Vector4 pl = Plane[f];
                var n = new Vector3(pl.X, pl.Y, pl.Z);
                float c = PairClear[k];
                float now = Vector3.Dot(n, x[j]) + pl.W;
                if (now >= c) continue;
                float then = Vector3.Dot(n, was[j]) + pl.W;
                if (then < c - Slop) continue;
                float t = then > now ? Math.Clamp((then - c) / (then - now), 0f, 1f) : 0f;
                if (!Inside(f, Vector3.Lerp(was[j], x[j], t))) continue;
                float depth = c - now;
                Vector3 p = x[j] + n * depth;
                Vector3 slide = p - was[j];
                slide -= n * Vector3.Dot(slide, n);
                float sl = slide.Length(), hold = Friction * depth;
                p -= sl <= hold ? slide : slide * (hold / sl);
                x[j] = p;
            }
    }

    private bool Inside(int f, Vector3 p) => Side(Edge0[f], p) && Side(Edge1[f], p) && Side(Edge2[f], p);

    private static bool Side(Vector4 e, Vector3 p) => e.X * p.X + e.Y * p.Y + e.Z * p.Z + e.W >= -EdgeSlack;
}

/// <summary>Builds one part's <see cref="DanglyColliders"/> from the world faces the scene offers near it: per
/// joint, the faces it rests in front of within its reach, and how near each may come before a vertex it carries
/// would pass within <see cref="Skin"/> of one. Scratch for one gather at a time, reused.</summary>
public sealed class DanglyGather
{
    /// <summary>The gap (metres) a joint's nearest vertex keeps from a face unless it was authored nearer, and how
    /// far behind a face a vertex may rest and still hold its joint back: one deeper was authored inside it.</summary>
    public const float Skin = 0.01f, RestSlop = 0.02f;

    /// <summary>Candidate faces and pairs one gather holds before the nearest are kept, and the vertex-bone
    /// pairs it reads off the cloth (a vertex past them holds no joint back; the joint itself still does).</summary>
    private const int MaxCandidates = 1024, MaxCandidatePairs = 8192, MaxVertexBones = 4096;

    private readonly Vector3[] _joint = new Vector3[DanglyRig.MaxJoints], _root = new Vector3[DanglyRig.MaxJoints];
    private readonly float[] _reach = new float[DanglyRig.MaxJoints], _near = new float[DanglyRig.MaxJoints];
    private readonly Vector3[] _vert = new Vector3[MaxVertexBones];
    private readonly int[] _vertJoint = new int[MaxVertexBones];
    private int _nj, _nv;

    private readonly Vector4[] _plane = new Vector4[MaxCandidates], _e0 = new Vector4[MaxCandidates],
                               _e1 = new Vector4[MaxCandidates], _e2 = new Vector4[MaxCandidates];
    private readonly float[] _key = new float[MaxCandidates], _faceSort = new float[MaxCandidates];
    private readonly int[] _keep = new int[MaxCandidates], _order = new int[MaxCandidates];
    private int _faces;

    private readonly int[] _pairJoint = new int[MaxCandidatePairs], _pairFace = new int[MaxCandidatePairs],
                           _pairOrder = new int[MaxCandidatePairs];
    private readonly float[] _pairClear = new float[MaxCandidatePairs], _pairRoom = new float[MaxCandidatePairs],
                             _pairSort = new float[MaxCandidatePairs];
    private int _pairs;

    /// <summary>The world box every joint's reach fits in: the region the scene side offers faces from.</summary>
    public Vector3 Lo, Hi;

    /// <summary>Faces, pairs and vertex-bone pairs the last gather could not keep.</summary>
    public int Dropped;

    /// <summary>Start a gather for <paramref name="rig"/> at <paramref name="world"/>, its mesh's rest vertices
    /// <paramref name="rest"/> in node space. False when the rig has nothing that moves.</summary>
    public bool Begin(DanglyRig rig, in Matrix4x4 world, ReadOnlySpan<Vector3> rest)
    {
        _faces = _pairs = Dropped = 0;
        int nj = rig.JointRest.Length;
        float scale = new Vector3(world.M11, world.M12, world.M13).Length();
        _nj = 0;
        if (nj == 0 || nj > DanglyRig.MaxJoints || rig.JointParent.Length < nj || !(scale > 1e-6f)) return false;
        Lo = new Vector3(float.MaxValue);
        Hi = new Vector3(float.MinValue);
        for (int j = 0; j < nj; j++)
        {
            _joint[j] = Vector3.Transform(rig.JointRest[j], world);
            int up = rig.JointParent[j];
            if ((uint)up >= (uint)j) { _root[j] = _joint[j]; _reach[j] = 0f; continue; }
            _root[j] = _root[up];
            _reach[j] = _reach[up] + (rig.JointRest[j] - rig.JointRest[up]).Length() * scale;
            var r = new Vector3(_reach[j] + Skin + DanglyColliders.EdgeSlack);
            Lo = Vector3.Min(Lo, _root[j] - r);
            Hi = Vector3.Max(Hi, _root[j] + r);
        }
        if (Lo.X > Hi.X) return false;
        _nj = nj;
        _nv = 0;
        SkinDef? skin = rig.Skin;
        if (skin == null) return true;
        int nv = Math.Min(rest.Length, Math.Min(skin.Weights.Length, skin.BoneIndices.Length / 4));
        for (int v = 0; v < nv; v++)
        {
            Vector3 at = Vector3.Transform(rest[v], world);
            Vector4 w = skin.Weights[v];
            for (int e = 0; e < 4; e++)
            {
                float we = e == 0 ? w.X : e == 1 ? w.Y : e == 2 ? w.Z : w.W;
                int b = skin.BoneIndices[v * 4 + e];
                if (!(we > 0f) || b <= 0 || b >= rig.BoneJoint.Length) continue;
                int j = rig.BoneJoint[b];
                if ((uint)j >= (uint)nj) continue;
                if (_nv == MaxVertexBones) { Dropped++; continue; }
                _vert[_nv] = at;
                _vertJoint[_nv++] = j;
            }
        }
        return true;
    }

    /// <summary>Offer one world triangle, front face counter-clockwise. Kept for every joint that rests in front of
    /// it within reach; the vertices the joint carries, bar those authored inside it, say how near it may come.</summary>
    public void Offer(Vector3 a, Vector3 b, Vector3 c)
    {
        if (_nj == 0) return;
        Vector3 g = Vector3.Cross(b - a, c - a);
        float len = g.Length();
        if (!(len > 1e-8f)) return;
        Vector3 n = g / len;
        float d = -Vector3.Dot(n, a);
        Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)), hi = Vector3.Max(a, Vector3.Max(b, c));
        if (!Touch(lo, hi, Lo, Hi)) return;

        bool any = false;
        for (int j = 0; j < _nj; j++)
        {
            _near[j] = float.NaN;
            if (!(_reach[j] > 0f)) continue;
            float r = _reach[j] + Skin + DanglyColliders.EdgeSlack;
            if (MathF.Abs(Vector3.Dot(n, _root[j]) + d) > r) continue;
            if (!Touch(lo, hi, _root[j] - new Vector3(r), _root[j] + new Vector3(r))) continue;
            float s0 = Vector3.Dot(n, _joint[j]) + d;
            if (s0 < 0f) continue;
            _near[j] = s0;
            any = true;
        }
        if (!any) return;
        for (int v = 0; v < _nv; v++)
        {
            int j = _vertJoint[v];
            if ((uint)j >= (uint)_nj || float.IsNaN(_near[j])) continue;
            float sv = Vector3.Dot(n, _vert[v]) + d;
            if (sv >= -RestSlop) _near[j] = MathF.Min(_near[j], sv);
        }

        int f = -1;
        float key = float.MaxValue;
        for (int j = 0; j < _nj; j++)
        {
            float near = _near[j];
            if (float.IsNaN(near)) continue;
            if (f < 0)
            {
                if (_faces >= MaxCandidates) { Dropped++; return; }
                f = _faces++;
                _plane[f] = new Vector4(n, d);
                _e0[f] = Edge(n, a, b);
                _e1[f] = Edge(n, b, c);
                _e2[f] = Edge(n, c, a);
            }
            if (_pairs >= MaxCandidatePairs) { Dropped++; break; }
            float room = MathF.Max(0f, near - Skin);
            _pairJoint[_pairs] = j;
            _pairFace[_pairs] = f;
            _pairClear[_pairs] = Vector3.Dot(n, _joint[j]) + d - room;
            _pairRoom[_pairs] = room;
            _pairs++;
            key = MathF.Min(key, room);
        }
        if (f >= 0) _key[f] = key;
    }

    /// <summary>Write what was gathered into <paramref name="into"/>: past either cap, the faces and pairs a joint
    /// is nearest to are the ones kept, and the rest counted in <see cref="Dropped"/>.</summary>
    public void Finish(DanglyColliders into)
    {
        int nf = _faces;
        for (int f = 0; f < nf; f++) { _keep[f] = f; _order[f] = f; }
        if (nf > DanglyColliders.MaxFaces)
        {
            _key.AsSpan(0, nf).CopyTo(_faceSort);
            MemoryExtensions.Sort(_faceSort.AsSpan(0, nf), _order.AsSpan(0, nf));
            for (int f = 0; f < nf; f++) _keep[_order[f]] = f < DanglyColliders.MaxFaces ? f : -1;
            Dropped += nf - DanglyColliders.MaxFaces;
            nf = DanglyColliders.MaxFaces;
        }
        for (int f = 0; f < _faces; f++)
        {
            int to = _keep[f];
            if (to < 0) continue;
            into.Plane[to] = _plane[f];
            into.Edge0[to] = _e0[f];
            into.Edge1[to] = _e1[f];
            into.Edge2[to] = _e2[f];
        }
        into.Faces = nf;

        int np = 0;
        for (int k = 0; k < _pairs; k++)
            if (_keep[_pairFace[k]] >= 0) _pairOrder[np++] = k;
        if (np > DanglyColliders.MaxPairs)
        {
            for (int i = 0; i < np; i++) _pairSort[i] = _pairRoom[_pairOrder[i]];
            MemoryExtensions.Sort(_pairSort.AsSpan(0, np), _pairOrder.AsSpan(0, np));
            Dropped += np - DanglyColliders.MaxPairs;
            np = DanglyColliders.MaxPairs;
        }
        Array.Clear(into.Count);
        for (int i = 0; i < np; i++) into.Count[_pairJoint[_pairOrder[i]]]++;
        int at = 0;
        for (int j = 0; j < into.First.Length; j++) { into.First[j] = at; at += into.Count[j]; }
        Span<int> fill = stackalloc int[DanglyRig.MaxJoints];
        for (int i = 0; i < np; i++)
        {
            int k = _pairOrder[i], j = _pairJoint[k];
            int slot = into.First[j] + fill[j]++;
            into.PairFace[slot] = _keep[_pairFace[k]];
            into.PairClear[slot] = _pairClear[k];
        }
        into.Pairs = np;
    }

    /// <summary>The inward plane of the edge <paramref name="a"/> to <paramref name="b"/> of a face with normal
    /// <paramref name="n"/>, wound counter-clockwise about it.</summary>
    private static Vector4 Edge(Vector3 n, Vector3 a, Vector3 b)
    {
        Vector3 m = Vector3.Cross(n, b - a);
        float l = m.Length();
        m = l > 1e-12f ? m / l : Vector3.Zero;
        return new Vector4(m, -Vector3.Dot(m, a));
    }

    private static bool Touch(Vector3 alo, Vector3 ahi, Vector3 blo, Vector3 bhi)
        => alo.X <= bhi.X && ahi.X >= blo.X && alo.Y <= bhi.Y && ahi.Y >= blo.Y && alo.Z <= bhi.Z && ahi.Z >= blo.Z;
}
