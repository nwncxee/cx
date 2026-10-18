using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace CX.Alloy.Mdl;

/// <summary>The model post-process -- what the engine derives after a node's fields are parsed, ported from
/// <c>MdlNodeTriMesh::InternalPostProcess</c> so an ASCII model's <see cref="ModelDef"/> matches the engine's
/// compiled binary of that source: <c>ProcessVerts</c>, the weld, the degenerate-face purge (whose uncompacted
/// scratch builds mismatched adjacency, an engine bug the files carry), adjacency, streams, tangents, area.</summary>
internal static class MdlPost
{
    /// <summary>Run the post-process over every node that needs it. A binary model arrives already
    /// post-processed, so only a mesh whose authored arrays are present and whose render geometry is empty is
    /// touched; the rest-pose extraction is idempotent and runs for both.</summary>
    internal static void Run(ModelDef m)
    {
        bool emitter = false;
        foreach (NodeDef n in m.Nodes)
        {
            // One rule for both readers: a lightmap is the second texture slot, named and not the file's
            // "null" placeholder. Neither form of the file states a flag for it.
            if (n.Mesh != null)
                n.Mesh.Lightmapped = n.Mesh.Textures[1].Length != 0
                                     && !n.Mesh.Textures[1].Equals("null", StringComparison.OrdinalIgnoreCase);
            if (n.Mesh is { Raw: not null } && n.Mesh.Positions.Length == 0) Mesh(n);
            // InternalPostProcess's early-out still calls CalculateTangentSpaceBasis (MdlNodeTriMesh.c:1015),
            // so a compiled mesh that arrived without a tangent pool gets one here as well.
            else if (n.Mesh is { Positions.Length: > 0 }) Tangents(n.Mesh, n);
            MdlBinary.RestPose(n);
            emitter |= n.Kind == NodeKind.Emitter;
        }
        // MdlNodeEmitter::InternalPostProcess raises the owning model's radius to 40 so a particle system is
        // not culled by the geometry it is attached to. It runs per emitter node, at its `endnode`.
        if (emitter && m.FromAscii && m.Radius < 40f) m.Radius = 40f;
        CX.Alloy.Physics.DanglyRig.BuildAll(m);
    }

    /// <summary>An ASCII animation's animmesh node: it restates the node's <c>verts</c>/<c>tverts</c>/<c>faces</c>,
    /// and its flat <c>animverts</c> are split into frames and re-indexed onto the weld of THAT geometry -- the
    /// same post-process a geometry node gets, so frame vertex <c>i</c> is the part's welded vertex <c>i</c>.</summary>
    internal static void AnimFrames(NodeDef n)
    {
        if (n.AnimMesh != null && n.Mesh is { Raw: not null } && n.Mesh.Positions.Length == 0) Mesh(n);
    }

    // ---- the engine's float primitives ----

    /// <summary><c>Vector::Normalize</c> @0x5b3700, quirks included: a length already within 1e-5 of one is
    /// left ALONE, a zero vector becomes <c>(1,0,0)</c>, and the scale is <c>sqrt(1/len2)</c> rather than
    /// <c>1/sqrt(len2)</c> -- which rounds differently.</summary>
    internal static Vector3 Normalize(Vector3 v)
    {
        float len2 = v.X * v.X + v.Y * v.Y + v.Z * v.Z;
        if (len2 == 1f || (len2 >= 0.99999f && len2 <= 1.00001f)) return v;
        if (len2 == 0f) return new Vector3(1f, 0f, 0f);
        float s = MathF.Sqrt(1f / len2);
        return new Vector3(v.X * s, v.Y * s, v.Z * s);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector3 Cross(Vector3 a, Vector3 b) =>
        new(a.Y * b.Z - b.Y * a.Z, a.Z * b.X - b.Z * a.X, a.X * b.Y - b.X * a.Y);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Len2(Vector3 v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z;

    /// <summary>One third as the engine's constant, not a division.</summary>
    private const float Third = 0.33333334f;

    /// <summary>The dot product in the engine's SUMMATION ORDER -- y, then x, then z
    /// (<c>_global.c:128337</c>). Float addition is not associative, so the order is part of the result.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float EngineDot(Vector3 a, Vector3 b) => a.Y * b.Y + a.X * b.X + a.Z * b.Z;

    /// <summary><c>magnitude</c> @0x5b3680: zero for a non-positive square, otherwise the square root.</summary>
    private static float Magnitude(Vector3 v)
    {
        float l = Len2(v);
        return l > 0f ? MathF.Sqrt(l) : 0f;
    }

    // ---- the weld key ----

    /// <summary>One <c>NodeVertex</c> as the engine compares it: exact float equality on the position, the
    /// normal, the mirror flag, the stream-presence mask, and each PRESENT stream (absent ones stay zero, so
    /// they cannot affect the comparison). Dangly adds its constraint, skin the bone slots and weights, and
    /// animmesh its three per-frame lists, which CX keys on their indices instead.</summary>
    private readonly struct VKey : IEquatable<VKey>
    {
        public readonly Vector3 P, N, C, T0, T1, T2, T3;
        public readonly Vector4 Tan;
        public readonly float Constraint, W0, W1, W2, W3;
        public readonly int B0, B1, B2, B3, Infl, AnimV, AnimT, AnimN;
        public readonly int MirrorMask;

        public VKey(Vector3 p, Vector3 n, Vector3 c, Vector3 t0, Vector3 t1, Vector3 t2, Vector3 t3,
                    Vector4 tan, float constraint, int mirrorMask,
                    int b0, int b1, int b2, int b3, float w0, float w1, float w2, float w3, int infl,
                    int animV, int animT, int animN)
        {
            P = p; N = n; C = c; T0 = t0; T1 = t1; T2 = t2; T3 = t3; Tan = tan;
            Constraint = constraint; MirrorMask = mirrorMask;
            B0 = b0; B1 = b1; B2 = b2; B3 = b3; W0 = w0; W1 = w1; W2 = w2; W3 = w3; Infl = infl;
            AnimV = animV; AnimT = animT; AnimN = animN;
            int h = Mix(Mix(Mix(17, p), n), mirrorMask);
            h = Mix(Mix(Mix(Mix(h, c), t0), t1), t2);
            h = Mix(Mix(Mix(Mix(Mix(h, t3), tan.X), tan.Y), tan.Z), tan.W);
            h = Mix(Mix(Mix(Mix(Mix(h, constraint), b0), b1), b2), b3);
            h = Mix(Mix(Mix(Mix(Mix(h, w0), w1), w2), w3), infl);
            _hash = Mix(Mix(Mix(h, animV), animT), animN);
        }

        public bool Equals(VKey o) =>
            P == o.P && N == o.N && MirrorMask == o.MirrorMask && C == o.C
            && T0 == o.T0 && T1 == o.T1 && T2 == o.T2 && T3 == o.T3 && Tan == o.Tan
            && Constraint == o.Constraint && Infl == o.Infl
            && B0 == o.B0 && B1 == o.B1 && B2 == o.B2 && B3 == o.B3
            && W0 == o.W0 && W1 == o.W1 && W2 == o.W2 && W3 == o.W3
            && AnimV == o.AnimV && AnimT == o.AnimT && AnimN == o.AnimN;

        public override bool Equals(object? o) => o is VKey k && Equals(k);

        /// <summary>Computed once, at construction: the weld probes every key at least once and most of
        /// them many times, and a 22-field <see cref="HashCode"/> walk per probe was the single largest cost
        /// of a dense node's weld. A float's own hash is used so that <c>-0.0</c> and <c>0.0</c> -- which
        /// <c>==</c> calls equal -- land in the same bucket.</summary>
        private readonly int _hash;

        public override int GetHashCode() => _hash;

        private static int Mix(int h, float f) => h * -1521134295 + f.GetHashCode();
        private static int Mix(int h, Vector3 v) => Mix(Mix(Mix(h, v.X), v.Y), v.Z);
        private static int Mix(int h, int i) => h * -1521134295 + i;
    }

    private const int MaskColor = 0x10, MaskTangent = 0x20;   // not-an-offset: the file format's stream-presence bits

    /// <summary>The whole post-process for one authored mesh node.</summary>
    private static void Mesh(NodeDef n)
    {
        MeshDef md = n.Mesh!;
        MeshRaw raw = md.Raw!;
        Vector3[] verts = raw.Verts;
        RawFace[] rf = raw.Faces;
        int faceCount = rf.Length;
        AnimMeshDef? anim0 = n.Kind == NodeKind.AnimMesh ? n.AnimMesh : null;
        // An animmesh states its frames as one flat array; the engine's frame count is the array length over
        // the AUTHORED vertex count for positions and over the authored TVERT count for UVs
        // (MdlNodeAnimMesh.c:281-284), and the per-frame normals it derives are indexed by normal index.
        if (anim0 != null) Reshape(anim0, verts.Length, raw.TVerts[0].Length);

        // Which faces touch each authored vertex, in ascending face order: what turns the engine's two
        // O(F^2) scans (the smoothing search and the adjacency search) into O(F) walks with the same result.
        BuildTouch(rf, verts.Length, out int[] touchAt, out int[] touch);

        // ---- ProcessVerts: a normal index per corner, deriving normals when the file states none ----
        bool fileNormals = raw.Normals.Length > 0;
        var normals = new List<Vector3>(fileNormals ? raw.Normals.Length : faceCount * 3);
        if (fileNormals) normals.AddRange(raw.Normals);
        var nidx = new int[faceCount * 3];
        for (int i = 0; i < faceCount; i++)
        {
            RawFace f = rf[i];
            int sg = f.SmoothGroup;
            for (int c = 0; c < 3; c++)
            {
                int slot = i * 3 + c;
                if (fileNormals) { nidx[slot] = VertOf(f, c); continue; }
                nidx[slot] = -1;
                int v = VertOf(f, c);
                Vector3 e0 = At(verts, f.V1) - At(verts, f.V0);
                Vector3 e1 = At(verts, f.V2) - At(verts, f.V1);
                Vector3 acc = Cross(e0, e1);
                Vector3 flat = Normalize(acc);
                if (sg != 0)
                {
                    // Only a face that USES this vertex can match, so the scan walks that vertex's own face
                    // list instead of all F faces -- same faces, same ascending order, O(F) instead of O(F^2).
                    foreach (int j in Touching(touch, touchAt, v))
                    {
                        if (j == i) continue;
                        RawFace g = rf[j];
                        if ((g.SmoothGroup & sg) == 0) continue;
                        // The engine tests corner 2 first, then 1, then 0 (objdump 0x455253/455259/455263),
                        // so on a face with a repeated index the HIGHEST corner wins.
                        int k = VertOf(g, 2) == v ? 2 : VertOf(g, 1) == v ? 1 : VertOf(g, 0) == v ? 0 : -1;
                        if (k < 0) continue;
                        if (j < i)
                        {
                            int idx = nidx[j * 3 + k];
                            // The test only REJECTS a nearly opposite normal, so in practice the lowest-index
                            // face of a smoothing island creates the normal and the rest share its index.
                            if (idx >= 0 && idx < normals.Count && EngineDot(normals[idx], flat) >= -0.99f)
                            { nidx[slot] = idx; break; }
                        }
                        else
                        {
                            acc += Cross(At(verts, g.V1) - At(verts, g.V0), At(verts, g.V2) - At(verts, g.V1));
                        }
                    }
                }
                if (nidx[slot] == -1)
                {
                    nidx[slot] = normals.Count;
                    normals.Add(Len2(acc) != 0f ? Normalize(acc) : flat);
                }
            }
        }

        // ---- ProcessVerts: bounds, centre, radius. The bounds start from the file's bmin/bmax (zero when
        // it states none), which is why a mesh with no bmin line always encloses the origin.
        Vector3 bmin = md.BMin, bmax = md.BMax, sum = default;
        foreach (Vector3 v in verts)
        {
            bmin = Vector3.Min(bmin, v);
            bmax = Vector3.Max(bmax, v);
            sum += v;
        }
        md.BMin = bmin;
        md.BMax = bmax;
        // The engine multiplies by the reciprocal rather than dividing, which rounds differently.
        md.Average = verts.Length != 0 ? sum * (1f / verts.Length) : sum;
        float r2 = 0f;
        foreach (Vector3 v in verts) { float d = Len2(v - md.Average); if (d > r2) r2 = d; }
        md.Radius = MathF.Sqrt(r2);

        if (anim0 != null) AnimNormals(anim0, rf, nidx, normals.Count, touchAt, touch);

        // MdlNodeAnimMesh::InternalPostProcess expands the bounds over every animvert of every frame
        // (MdlNodeAnimMesh.c:620-664), not just the rest pose.
        if (anim0 != null)
            foreach (Vector3[] frame in anim0.Positions)
                foreach (Vector3 v in frame) { md.BMin = Vector3.Min(md.BMin, v); md.BMax = Vector3.Max(md.BMax, v); }

        if (faceCount == 0)
        {
            md.Raw = raw;
            return;
        }

        // ---- InternalGenVertices: the weld ----
        int uvMask = 0;
        for (int s = 0; s < 4; s++) if (raw.TVerts[s].Length != 0) uvMask |= 1 << s;
        bool hasColor = raw.Colors.Length != 0;
        bool hasTangent = raw.Tangents.Length != 0;
        int mask = uvMask | (hasColor ? MaskColor : 0) | (hasTangent ? MaskTangent : 0);
        SkinDef? skin = n.Skin;
        DanglyDef? dangly = n.Dangly;
        bool anim = n.Kind == NodeKind.AnimMesh;

        // Sized for the common case (a welded mesh is nearer F to 2F vertices than 3F), so a 20k-face node
        // does not open with three large-object-heap arrays.
        int guess = Math.Max(16, faceCount + (faceCount >> 1));
        var keysBy = new Dictionary<VKey, int>(guess);
        var order = new List<VKey>(guess);
        var srcVert = new List<int>(guess);
        var srcTvert = new List<int>(guess);
        var srcNormal = new List<int>(guess);
        var faces = new List<FaceDef>(faceCount);

        VKey Key(in RawFace f, int i, int c, int mirror)
        {
            int vi = VertOf(f, c);
            int ti = raw.TVerts[0].Length != 0 ? TvertOf(f, c) : 0;
            int ni = nidx[i * 3 + c];
            return new VKey(
                At(verts, vi),
                ni >= 0 && ni < normals.Count ? normals[ni] : default,
                hasColor ? At(raw.Colors, vi) : default,
                (uvMask & 1) != 0 ? Uv3(raw.TVerts[0], ti) : default,
                (uvMask & 2) != 0 ? Uv3(raw.TVerts[1], ti) : default,
                (uvMask & 4) != 0 ? Uv3(raw.TVerts[2], ti) : default,
                (uvMask & 8) != 0 ? Uv3(raw.TVerts[3], ti) : default,
                hasTangent && ni >= 0 && ni < raw.Tangents.Length ? raw.Tangents[ni] : default,
                dangly != null && vi < dangly.Constraints.Length ? dangly.Constraints[vi] : 0f,
                mirror | (mask << 8),
                Bone(skin, vi, 0), Bone(skin, vi, 1), Bone(skin, vi, 2), Bone(skin, vi, 3),
                Weight(skin, vi, 0), Weight(skin, vi, 1), Weight(skin, vi, 2), Weight(skin, vi, 3),
                skin != null && vi < skin.Influences.Length ? skin.Influences[vi] : 0,
                anim ? vi : 0, anim ? ti : 0, anim ? ni : 0);
        }

        int Emit(VKey k, int vi, int ti, int ni)
        {
            int at = order.Count;
            keysBy[k] = at;
            order.Add(k);
            srcVert.Add(vi);
            srcTvert.Add(ti);
            srcNormal.Add(ni);
            return at;
        }

        if (n.Kind == NodeKind.Aabb)
        {
            // MdlNodeAABB::InternalGenVertices welds a face only against ONE earlier face, and only when at
            // least two of its three corners match that face's -- walkmesh triangles keep their identity.
            Span<int> match = stackalloc int[3];
            Span<VKey> tmp = stackalloc VKey[3];
            for (int i = 0; i < faceCount; i++)
            {
                RawFace f = rf[i];
                int mirror = raw.MirrorList.Length > i ? raw.MirrorList[i] & 0xFF : 0;
                var fd = new FaceDef { SmoothGroup = f.SmoothGroup, Surface = f.Surface, Adj0 = -1, Adj1 = -1, Adj2 = -1 };
                for (int c = 0; c < 3; c++) tmp[c] = Key(f, i, c, mirror);
                int hitFace = -1;
                for (int pf = 0; pf < i && hitFace < 0; pf++)
                {
                    int hits = 0;
                    FaceDef pd = faces[pf];
                    for (int c = 0; c < 3; c++)
                    {
                        match[c] = -1;
                        for (int k = 0; k < 3; k++)
                        {
                            int pv = k == 0 ? pd.V0 : k == 1 ? pd.V1 : pd.V2;
                            if (pv >= 0 && pv < order.Count && order[pv].Equals(tmp[c])) { hits++; match[c] = k; break; }
                        }
                    }
                    if (hits >= 2) hitFace = pf;
                }
                for (int c = 0; c < 3; c++)
                {
                    int at;
                    if (hitFace < 0 || match[c] == -1)
                        at = Emit(tmp[c], VertOf(f, c), TvertOf(f, c), nidx[i * 3 + c]);
                    else
                    {
                        FaceDef pd = faces[hitFace];
                        at = match[c] == 0 ? pd.V0 : match[c] == 1 ? pd.V1 : pd.V2;
                    }
                    switch (c)
                    {
                        case 0: fd.V0 = at; break;
                        case 1: fd.V1 = at; break;
                        default: fd.V2 = at; break;
                    }
                }
                faces.Add(fd);
            }
        }
        else
        {
            for (int i = 0; i < faceCount; i++)
            {
                RawFace f = rf[i];
                int mirror = raw.MirrorList.Length > i ? raw.MirrorList[i] & 0xFF : 0;
                var fd = new FaceDef { SmoothGroup = f.SmoothGroup, Surface = f.Surface, Adj0 = -1, Adj1 = -1, Adj2 = -1 };
                for (int c = 0; c < 3; c++)
                {
                    VKey key = Key(f, i, c, mirror);
                    if (!keysBy.TryGetValue(key, out int at))
                        at = Emit(key, VertOf(f, c), TvertOf(f, c), nidx[i * 3 + c]);
                    switch (c)
                    {
                        case 0: fd.V0 = at; break;
                        case 1: fd.V1 = at; break;
                        default: fd.V2 = at; break;
                    }
                }
                faces.Add(fd);
            }
        }

        // The degenerate purge compacts the face list only; the authored array keeps its length, which the
        // adjacency pass below then indexes in lockstep -- the engine's own misalignment.
        for (int i = faces.Count - 1; i >= 0; i--)
        {
            FaceDef fd = faces[i];
            if (fd.V0 == fd.V1 || fd.V1 == fd.V2 || fd.V0 == fd.V2) faces.RemoveAt(i);
        }

        int nv = order.Count;
        md.Positions = new Vector3[nv];
        md.Normals = new Vector3[nv];
        for (int s = 0; s < 4; s++) md.Uv[s] = (uvMask & (1 << s)) != 0 ? new Vector2[nv] : Array.Empty<Vector2>();
        md.Colors = hasColor ? new uint[nv] : Array.Empty<uint>();
        for (int i = 0; i < nv; i++)
        {
            VKey k = order[i];
            md.Positions[i] = k.P;
            md.Normals[i] = k.N;
            if ((uvMask & 1) != 0) md.Uv[0][i] = new Vector2(k.T0.X, k.T0.Y);
            if ((uvMask & 2) != 0) md.Uv[1][i] = new Vector2(k.T1.X, k.T1.Y);
            if ((uvMask & 4) != 0) md.Uv[2][i] = new Vector2(k.T2.X, k.T2.Y);
            if ((uvMask & 8) != 0) md.Uv[3][i] = new Vector2(k.T3.X, k.T3.Y);
            if (hasColor) md.Colors[i] = Pack(k.C);
        }
        if (hasTangent)
        {
            md.Tangents = new Vector4[nv];
            for (int i = 0; i < nv; i++) md.Tangents[i] = order[i].Tan;
        }
        if (dangly != null)
        {
            var c2 = new float[nv];
            for (int i = 0; i < nv; i++) c2[i] = order[i].Constraint;
            dangly.Constraints = c2;
        }
        if (skin != null) CompactSkin(skin, srcVert);
        if (anim0 != null) CompactAnim(anim0, srcVert, srcTvert, srcNormal);

        // ---- BuildNeighborLists: face planes and edge adjacency over the AUTHORED indices ----
        int fc = faces.Count;
        for (int i = 0; i < fc; i++)
        {
            RawFace f = rf[i];
            FaceDef fd = faces[i];
            Vector3 p0 = At(verts, f.V0);
            fd.PlaneNormal = Normalize(Cross(At(verts, f.V1) - p0, At(verts, f.V2) - At(verts, f.V1)));
            fd.PlaneDistance = -EngineDot(fd.PlaneNormal, p0);
            for (int e = 0; e < 3; e++)
            {
                int a = VertOf(f, e), b = VertOf(f, (e + 1) % 3);
                int adj = -1;
                // Only a face that USES `a` can match, and the engine takes the LOWEST such face, so walking
                // a's own ascending face list gives the same answer in O(1) amortised instead of O(F).
                foreach (int j in Touching(touch, touchAt, a))
                {
                    if (j == i || j >= fc) continue;
                    RawFace g = rf[j];
                    bool hit = false;
                    for (int k = 0; k < 3; k++)
                    {
                        if (VertOf(g, k) != a) continue;
                        if (VertOf(g, (k + 1) % 3) == b || VertOf(g, (k + 2) % 3) == b) { hit = true; break; }
                    }
                    if (hit) { adj = j; break; }
                }
                switch (e)
                {
                    case 0: fd.Adj0 = adj; break;
                    case 1: fd.Adj1 = adj; break;
                    default: fd.Adj2 = adj; break;
                }
            }
            faces[i] = fd;
        }
        md.Faces = faces.ToArray();

        // ---- BuildVertexIndices ----
        md.Indices = new uint[fc * 3];
        for (int i = 0; i < fc; i++)
        {
            md.Indices[i * 3] = (uint)md.Faces[i].V0;
            md.Indices[i * 3 + 1] = (uint)md.Faces[i].V1;
            md.Indices[i * 3 + 2] = (uint)md.Faces[i].V2;
        }

        Tangents(md, n);

        // ---- ComputeLocalSurfaceArea: only the TriMesh post-process calls it. The dangly, animmesh and
        // skin overrides each replace that whole function and none of them does.
        if (n.Kind is NodeKind.TriMesh or NodeKind.Aabb)
        {
            float area = 0f;
            foreach (FaceDef fd in md.Faces)
            {
                Vector3 v0 = At(md.Positions, fd.V0), v1 = At(md.Positions, fd.V1), v2 = At(md.Positions, fd.V2);
                area += 0.5f * Magnitude(Cross(v2 - v0, v1 - v0));
            }
            md.SurfaceArea = area;
        }
    }

    /// <summary><c>CalculateTangentSpaceBasis</c> (+ @0x556190): a per-triangle tangent from the UV gradient,
    /// accumulated per vertex, DUPLICATING a vertex whose triangles disagree on handedness, then Gram-Schmidt
    /// against the normal. Gated by the model's <c>renderhint</c> or the <c>.mtr</c>'s and idempotent, so the
    /// material feed re-runs it on a mesh whose hint only arrived with a late <c>.mtr</c> read.</summary>
    internal static void Tangents(MeshDef md, NodeDef n)
    {
        if (md.Tangents.Length != 0) return;                 // the file supplied them
        int hint = md.RenderHintId;
        if (hint == 0)
        {
            // GetMaterialName: `materialname`, or `texture0` when it is empty.
            string mat = md.MaterialName.Length != 0 ? md.MaterialName : md.Textures[0];
            if (mat.Length == 0) return;
            // A hint of 0 is "no answer" either way: no delegate, or a delegate that could not read the
            // material yet. Both are counted, because both mean this mesh has no tangent frame it may need.
            hint = MdlParser.MaterialHint?.Invoke(mat) ?? 0;
            if (hint == 0) System.Threading.Interlocked.Increment(ref MdlParser.MaterialHintMisses);
        }
        if (hint is not (2 or 3)) return;
        if (md.Uv[0].Length == 0 || md.Normals.Length == 0 || md.Indices.Length < 3) return;

        int tris = md.Indices.Length / 3;
        var ft = new Vector3[tris];
        var fs = new float[tris];
        for (int t = 0; t < tris; t++)
        {
            int i0 = (int)md.Indices[t * 3], i1 = (int)md.Indices[t * 3 + 1], i2 = (int)md.Indices[t * 3 + 2];
            Vector2 a0 = At(md.Uv[0], i0), a1 = At(md.Uv[0], i1), a2 = At(md.Uv[0], i2);
            float du1 = a1.X - a0.X, du2 = a2.X - a0.X, dv1 = a1.Y - a0.Y, dv2 = a2.Y - a0.Y;
            float det = du1 * dv2 - du2 * dv1;
            fs[t] = det >= 0f ? 1f : -1f;
            if (det != 0f)
            {
                Vector3 e1 = At(md.Positions, i1) - At(md.Positions, i0);
                Vector3 e2 = At(md.Positions, i2) - At(md.Positions, i0);
                ft[t] = (dv2 / det) * e1 - (dv1 / det) * e2;
            }
            else
            {
                // Degenerate UVs: the average of the three normals with the engine's (y,z,x) swizzle.
                Vector3 n0 = At(md.Normals, i0), n1 = At(md.Normals, i1), n2 = At(md.Normals, i2);
                // The engine multiplies by 0.33333334f (objdump 0x455... / _global.c:127939-127941), not
                // divides by three, and the stores are swizzled (y, z, x) -- both reproduced literally.
                ft[t] = new Vector3((n0.Y + n1.Y + n2.Y) * Third,
                                    (n0.Z + n1.Z + n2.Z) * Third,
                                    (n0.X + n1.X + n2.X) * Third);
            }
        }

        int nv = md.Positions.Length;
        var hand = new float[nv];
        var tan = new Vector3[nv];
        var extraSrc = new List<int>();
        var extraSign = new List<float>();
        var extraTan = new List<Vector3>();
        var remapAt = new List<int>();
        var remapTo = new List<int>();
        bool anim = n.Kind == NodeKind.AnimMesh;
        for (int t = 0; t < tris; t++)
            for (int c = 0; c < 3; c++)
            {
                int slot = t * 3 + c;
                int v = (int)md.Indices[slot];
                if (v < 0 || v >= nv) continue;
                float s = fs[t];
                if (hand[v] == 0f || anim) { hand[v] = s; tan[v] = ft[t]; continue; }
                if (hand[v] == s) { tan[v] += ft[t]; continue; }
                int found = -1;
                for (int k = 0; k < extraSrc.Count; k++)
                    if (extraSrc[k] == v && extraSign[k] == s) { found = k; break; }
                if (found < 0)
                {
                    found = extraSrc.Count;
                    extraSrc.Add(v);
                    extraSign.Add(s);
                    extraTan.Add(ft[t]);
                }
                else extraTan[found] += ft[t];
                remapAt.Add(slot);
                remapTo.Add(nv + found);
            }

        if (extraSrc.Count != 0)
        {
            int total = nv + extraSrc.Count;
            Grow(ref md.Positions, total);
            Grow(ref md.Normals, total);
            for (int s = 0; s < 4; s++) if (md.Uv[s].Length != 0) Grow(ref md.Uv[s], total);
            if (md.Colors.Length != 0) Grow(ref md.Colors, total);
            Array.Resize(ref hand, total);
            Array.Resize(ref tan, total);
            for (int k = 0; k < extraSrc.Count; k++)
            {
                int src = extraSrc[k], dst = nv + k;
                md.Positions[dst] = md.Positions[src];
                md.Normals[dst] = md.Normals[src];
                for (int s = 0; s < 4; s++) if (md.Uv[s].Length != 0) md.Uv[s][dst] = md.Uv[s][src];
                if (md.Colors.Length != 0) md.Colors[dst] = md.Colors[src];
                hand[dst] = extraSign[k];
                tan[dst] = extraTan[k];
            }
            if (n.Dangly != null && n.Dangly.Constraints.Length == nv)
            {
                float[] cs = n.Dangly.Constraints;
                Array.Resize(ref cs, total);
                for (int k = 0; k < extraSrc.Count; k++) cs[nv + k] = cs[extraSrc[k]];
                n.Dangly.Constraints = cs;
            }
            if (n.Skin != null) GrowSkin(n.Skin, nv, extraSrc);
            for (int k = 0; k < remapAt.Count; k++) md.Indices[remapAt[k]] = (uint)remapTo[k];
            nv = total;
        }

        md.Tangents = new Vector4[nv];
        for (int v = 0; v < nv; v++)
        {
            Vector3 nrm = At(md.Normals, v);
            Vector3 t = tan[v] - nrm * EngineDot(tan[v], nrm);
            md.Tangents[v] = new Vector4(Normalize(t), hand[v]);
        }
    }

    private static void Grow<T>(ref T[] a, int n) { if (a.Length < n) Array.Resize(ref a, n); }

    private static void GrowSkin(SkinDef s, int nv, List<int> extra)
    {
        int total = nv + extra.Count;
        if (s.Weights.Length == nv)
        {
            Array.Resize(ref s.Weights, total);
            for (int k = 0; k < extra.Count; k++) s.Weights[nv + k] = s.Weights[extra[k]];
        }
        if (s.Bones.Length == nv)
        {
            Array.Resize(ref s.Bones, total);
            for (int k = 0; k < extra.Count; k++) s.Bones[nv + k] = s.Bones[extra[k]];
        }
        if (s.BoneIndices.Length == nv * 4)
        {
            Array.Resize(ref s.BoneIndices, total * 4);
            for (int k = 0; k < extra.Count; k++)
                for (int j = 0; j < 4; j++) s.BoneIndices[(nv + k) * 4 + j] = s.BoneIndices[extra[k] * 4 + j];
        }
    }

    /// <summary>Rebuild the skin's per-vertex arrays against the welded vertices, which is what the engine's
    /// skin <c>GenVertices</c> does at the end of its own weld.</summary>
    private static void CompactSkin(SkinDef s, List<int> srcVert)
    {
        int nv = srcVert.Count;
        Vector4[] w = s.Weights;
        uint[] b = s.Bones;
        ushort[] bi = s.BoneIndices;
        int[] infl = s.Influences;
        var w2 = new Vector4[nv];
        var b2 = new uint[nv];
        var bi2 = new ushort[nv * 4];
        var in2 = new int[nv];
        for (int i = 0; i < nv; i++)
        {
            int v = srcVert[i];
            if (v >= 0 && v < w.Length) w2[i] = w[v];
            if (v >= 0 && v < b.Length) b2[i] = b[v];
            if (v >= 0 && v < infl.Length) in2[i] = infl[v];
            for (int j = 0; j < 4; j++)
                if (v >= 0 && v * 4 + j < bi.Length) bi2[i * 4 + j] = bi[v * 4 + j];
        }
        s.Weights = w2;
        s.Bones = b2;
        s.BoneIndices = bi2;
        s.Influences = in2;
    }

    /// <summary>Split the authored flat <c>animverts</c>/<c>animtverts</c> arrays into one array per frame,
    /// still indexed by the AUTHORED vertex and tvert. The engine's two frame counts are the array lengths
    /// over those two counts -- the UV one is the TVERT count, not the vertex count.</summary>
    private static void Reshape(AnimMeshDef a, int preVerts, int preTverts)
    {
        if (a.Positions.Length == 1 && preVerts > 0)
        {
            Vector3[] flat = a.Positions[0];
            int frames = flat.Length / preVerts;
            var o = new Vector3[Math.Max(0, frames)][];
            for (int f = 0; f < o.Length; f++)
            {
                o[f] = new Vector3[preVerts];
                Array.Copy(flat, f * preVerts, o[f], 0, preVerts);
            }
            a.Positions = o;
        }
        if (a.Uvs.Length == 1 && preTverts > 0)
        {
            Vector2[] flat = a.Uvs[0];
            int frames = flat.Length / preTverts;
            var o = new Vector2[Math.Max(0, frames)][];
            for (int f = 0; f < o.Length; f++)
            {
                o[f] = new Vector2[preTverts];
                Array.Copy(flat, f * preTverts, o[f], 0, preTverts);
            }
            a.Uvs = o;
        }
    }

    /// <summary>Re-index the per-frame arrays against the welded vertices: positions by the authored vertex a
    /// welded vertex came from, UVs by its authored TVERT, and the derived normals by its normal index --
    /// which is how the engine copies its <c>0x300</c>/<c>0x308</c> streams out of each
    /// <c>NodeVertexAnim</c>.</summary>
    private static void CompactAnim(AnimMeshDef a, List<int> srcVert, List<int> srcTvert, List<int> srcNormal)
    {
        int nv = srcVert.Count;
        for (int f = 0; f < a.Positions.Length; f++)
        {
            Vector3[] src = a.Positions[f];
            var o = new Vector3[nv];
            for (int i = 0; i < nv; i++) o[i] = At(src, srcVert[i]);
            a.Positions[f] = o;
        }
        for (int f = 0; f < a.Uvs.Length; f++)
        {
            Vector2[] src = a.Uvs[f];
            var o = new Vector2[nv];
            for (int i = 0; i < nv; i++) o[i] = At(src, srcTvert[i]);
            a.Uvs[f] = o;
        }
        for (int f = 0; f < a.Normals.Length; f++)
        {
            Vector3[] src = a.Normals[f];
            var o = new Vector3[nv];
            for (int i = 0; i < nv; i++) o[i] = At(src, srcNormal[i]);
            a.Normals[f] = o;
        }
    }

    /// <summary>The per-frame smooth normals an animmesh derives (<c>MdlNodeAnimMesh.c:550-619</c>). The rule
    /// is NOT <c>ProcessVerts</c>': it matches ANY corner of the other face rather than the corner at this
    /// vertex, always includes the face itself, and is a plain area-weighted sum normalised once with no reuse
    /// and no <c>-0.99</c> test. Indexed by normal index, one array per frame.</summary>
    private static void AnimNormals(AnimMeshDef a, RawFace[] rf, int[] nidx, int normalCount,
                                    int[] touchAt, int[] touch)
    {
        int frames = a.Positions.Length;
        if (frames == 0 || normalCount == 0) return;
        var outp = new Vector3[frames][];
        for (int f = 0; f < frames; f++)
        {
            var nrm = new Vector3[normalCount];
            Vector3[] av = a.Positions[f];
            for (int i = 0; i < rf.Length; i++)
            {
                int sg = rf[i].SmoothGroup;
                for (int c = 0; c < 3; c++)
                {
                    int v = VertOf(rf[i], c);
                    Vector3 acc = default;
                    foreach (int j in Touching(touch, touchAt, v))
                    {
                        RawFace g = rf[j];
                        if (j != i && (g.SmoothGroup & sg) == 0) continue;
                        acc += Cross(At(av, g.V1) - At(av, g.V0), At(av, g.V2) - At(av, g.V1));
                    }
                    int ni = nidx[i * 3 + c];
                    if (ni >= 0 && ni < normalCount) nrm[ni] = Normalize(acc);
                }
            }
            outp[f] = nrm;
        }
        a.Normals = outp;
    }

    /// <summary>Which faces touch each authored vertex, ascending: <paramref name="at"/> is the start of each
    /// vertex's run in <paramref name="list"/>. One pass, two arrays, no per-vertex collection.</summary>
    private static void BuildTouch(RawFace[] rf, int verts, out int[] at, out int[] list)
    {
        at = new int[verts + 1];
        for (int i = 0; i < rf.Length; i++)
            for (int c = 0; c < 3; c++)
            {
                int v = VertOf(rf[i], c);
                if (v >= 0 && v < verts) at[v + 1]++;
            }
        for (int v = 0; v < verts; v++) at[v + 1] += at[v];
        list = new int[at[verts]];
        var fill = new int[verts];
        for (int i = 0; i < rf.Length; i++)
            for (int c = 0; c < 3; c++)
            {
                int v = VertOf(rf[i], c);
                if (v < 0 || v >= verts) continue;
                int slot = at[v] + fill[v];
                // A face that names the same vertex twice is listed once, which is what a scan over all
                // faces would also see.
                if (fill[v] != 0 && list[slot - 1] == i) continue;
                list[slot] = i;
                fill[v]++;
            }
        // Trim each run to what was filled by padding the tail with -1, which Touching skips.
        for (int v = 0; v < verts; v++)
            for (int k = at[v] + fill[v]; k < at[v + 1]; k++) list[k] = -1;
    }

    /// <summary>The faces touching <paramref name="v"/>, ascending, or nothing when it is out of range.</summary>
    private static TouchRun Touching(int[] list, int[] at, int v) =>
        v >= 0 && v + 1 < at.Length ? new TouchRun(list, at[v], at[v + 1]) : new TouchRun(list, 0, 0);

    /// <summary>A run of <see cref="BuildTouch"/>'s list, skipping the <c>-1</c> padding. A struct enumerator
    /// so the <c>foreach</c> allocates nothing.</summary>
    private readonly struct TouchRun(int[] list, int from, int to)
    {
        public Enumerator GetEnumerator() => new(list, from, to);

        public struct Enumerator(int[] list, int from, int to)
        {
            private int _at = from - 1;
            public int Current => list[_at];
            public bool MoveNext()
            {
                while (++_at < to) if (list[_at] >= 0) return true;
                return false;
            }
        }
    }

    /// <summary><c>MaxTree::SynchronizeTree</c>: a model with a supermodel takes its node numbers from the
    /// supermodel BY NAME, and a node the supermodel does not have gets -1. The parser cannot do this on its
    /// own (it has no chain), so the model cache calls it once the chain resolves.</summary>
    public static void Resync(ModelDef model, ModelDef super)
    {
        if (model.Nodes.Length == 0 || super.Nodes.Length == 0) return;
        // ONLY an ASCII model is re-keyed. `beginmodelgeom` and `MaxTree::SynchronizeTree` are the ASCII
        // interpreter's; the binary loader (FUN_005bee60) calls neither, so a compiled model's node numbers
        // AND its counter are what the file states -- already seeded by whatever compiled it. Treating a
        // binary supermodel as if it still had to be seeded shifts the whole chain by one per link.
        if (!model.FromAscii) return;
        // beginmodelgeom seeds the node counter from the supermodel BEFORE any node is parsed
        // (`Model+0x58 = super->0x58 + 1`), so a node the scoped match never reaches keeps that seeded
        // number -- not its own file index, and not -1. Only a node the walk REACHES and cannot name-match
        // in its counterpart's children gets -1.
        int seed = super.NodeIdNext + 1;
        foreach (NodeDef n in model.Nodes) n.PartNumber = seed + n.FileIndex;
        model.NodeIdNext = seed + model.Nodes.Length;
        Walk(0, 0);
        model.NodeIdsSynced = true;

        // The match is SCOPED: a child is looked for among its counterpart's children, and a node the
        // supermodel does not have takes -1 and its own subtree is not visited at all -- so that subtree keeps
        // the numbers the file order gave it.
        void Walk(int a, int b)
        {
            model.Nodes[a].PartNumber = super.Nodes[b].PartNumber;
            foreach (int c in model.Nodes[a].Children)
            {
                int match = -1;
                foreach (int d in super.Nodes[b].Children)
                    if (string.Equals(model.Nodes[c].Name, super.Nodes[d].Name, StringComparison.OrdinalIgnoreCase))
                    { match = d; break; }
                if (match >= 0) Walk(c, match);
                else model.Nodes[c].PartNumber = -1;
            }
        }
    }

    // ---- small accessors ----

    private static int VertOf(in RawFace f, int c) => c == 0 ? f.V0 : c == 1 ? f.V1 : f.V2;
    private static int TvertOf(in RawFace f, int c) => c == 0 ? f.T0 : c == 1 ? f.T1 : f.T2;

    /// <summary>Element <paramref name="i"/>, or the default when the file's index is out of range -- which
    /// is a malformed file, so it is COUNTED. The engine reads past the array instead.</summary>
    private static T At<T>(T[] a, int i)
    {
        if (i >= 0 && i < a.Length) return a[i];
        System.Threading.Interlocked.Increment(ref OutOfRange);
        return default!;
    }

    /// <summary>Indices a file stated that its own arrays do not hold. Reported by every consumer; a quiet
    /// clamp would hide a malformed model behind geometry that merely looks odd.</summary>
    internal static int OutOfRange;

    /// <summary>An authored UV, all three floats: <c>NodeVertex::operator==</c> compares the third as well
    /// (<c>NodeVertex.c:153-178</c>), so dropping it before the weld would merge corners the engine keeps.</summary>
    private static Vector3 Uv3(Vector3[] a, int i) => At(a, i);

    private static int Bone(SkinDef? s, int v, int k) =>
        s != null && v >= 0 && v * 4 + k < s.BoneIndices.Length ? s.BoneIndices[v * 4 + k] : 0;

    private static float Weight(SkinDef? s, int v, int k)
    {
        if (s == null || v < 0 || v >= s.Weights.Length) return 0f;
        Vector4 w = s.Weights[v];
        return k == 0 ? w.X : k == 1 ? w.Y : k == 2 ? w.Z : w.W;
    }

    /// <summary><c>GenerateVertexData</c>'s colour packing: <c>0xff000000 | B&lt;&lt;16 | G&lt;&lt;8 | R</c>
    /// with each channel <c>(int)(f * 255 + 0.5) &amp; 0xff</c>.</summary>
    private static uint Pack(Vector3 c) =>
        0xFF000000u | ((uint)Round8(c.Z) << 16) | ((uint)Round8(c.Y) << 8) | (uint)Round8(c.X);

    private static int Round8(float f) => (int)(f * 255f + 0.5f) & 0xFF;
}
