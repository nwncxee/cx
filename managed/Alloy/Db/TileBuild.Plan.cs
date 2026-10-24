using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The merge plan: one tile MODEL is merged once, in model space, into the arena ranges and light nodes
/// every placement of it then draws under a single matrix. Built on first use and shared from then on.</summary>
public static partial class TileBuild
{
    /// <summary>One tile model's merge plan, in MODEL space: the merged meshes, the arena range each one took,
    /// and the model's light nodes. Built once per <see cref="ModelDef"/> and shared by every placement.</summary>
    public sealed class MergePlan
    {
        /// <summary>The merged meshes, one per bucket (<c>CombineParts</c>' <c>CreatePartBuckets</c>).</summary>
        public MeshDef[] Meshes = Array.Empty<MeshDef>();
        /// <summary>Per merged mesh, the <see cref="MeshArena"/> range it holds (-1 when the arena refused).</summary>
        public int[] Ranges = Array.Empty<int>();
        /// <summary>Per merged mesh, the <c>tilefade</c> class of its bucket (1 fades, 2 cap, 4 no shadow,
        /// 0 plain): the fade ramps a class-1 mesh and nothing else.</summary>
        public int[] FadeClass = Array.Empty<int>();
        /// <summary>The model nodes each bucket swallowed -- what a part tree marks
        /// <see cref="SceneDb.PartMerged"/> from; nothing here writes parts.</summary>
        public int[][] Members = Array.Empty<int[]>();
        /// <summary>The model's light nodes, in node order, with their rest pose and controller values.</summary>
        public PlanLight[] Lights = Array.Empty<PlanLight>();
        /// <summary>The nodes named <c>&lt;model&gt;sl1</c> and <c>&lt;model&gt;sl2</c> (-1 for none), and
        /// their model-space poses. <c>ProcessTilePart</c> stores these PARTS (not their lights) at
        /// <c>TileData+0x10</c>/<c>+0x18</c>, and <c>Scene::SetSingleTileFlame</c> @0x55ef30 copies the part's
        /// position and orientation into the flame object -- so these are where a tile's two flames stand.</summary>
        public int Sl1 = -1, Sl2 = -1;
        /// <inheritdoc cref="Sl1"/>
        public Vector3 Sl1Pos, Sl2Pos;
        /// <inheritdoc cref="Sl1"/>
        public Quaternion Sl1Rot = Quaternion.Identity, Sl2Rot = Quaternion.Identity;
        /// <summary>Model-space bounds of every merged mesh together: the whole tile's geometry, for culling.</summary>
        public Vector3 BMin, BMax;
        /// <summary>Model-space bounds of what the engine FILES in <c>TileData+0x30</c>, the union
        /// <c>Scene::SpawnTile</c> walks: trimesh parts with <c>tilefade 0</c> and non-empty geometry, outside
        /// the animated half. <c>ProcessStaticParts</c> @0x570970 REMOVES <c>tilefade 2</c> and <c>4</c> from
        /// <c>+0x30</c>; an AABB walkmesh part answers <c>AsPartTriMesh</c>, so it IS filed and DOES count.</summary>
        public Vector3 FiledBMin, FiledBMax;
        /// <summary>Model-space bounds of the <c>tilefade 1</c> geometry alone: what <c>CheckFadePlane</c>
        /// @0x47bf30 tests against the camera plane.</summary>
        public Vector3 FadeBMin, FadeBMax;
        /// <summary>The tile has any <c>tilefade 1</c> geometry at all (else the ramp never moves).</summary>
        public bool HasFade;
        /// <summary>Model-space box of each <c>tilefade 1</c> node, in node order: what
        /// <c>CheckFadePlane</c> tests ONE AT A TIME.</summary>
        public Vector3[] FadeLo = Array.Empty<Vector3>(), FadeHi = Array.Empty<Vector3>();
        /// <summary>Mesh nodes that land in <c>TileData+0x30</c>: the count a live comparison uses.</summary>
        public int FiledNodes;
        /// <summary>The model's <c>aabb</c> nodes: the walkmesh vanilla's floor cast hits
        /// (<c>SceneBSPAddPart</c> stores it at the BSP leaf's +0x90).</summary>
        public int AabbNodes;
    }

    /// <summary>One light node of a tile model, resolved to model space with its rest controller values.</summary>
    public struct PlanLight
    {
        public int Node;
        public Vector3 Pos, Colour;
        public float Range, RangeAlt, Multiplier;
        public bool Shadow, AmbientOnly, Dynamic;
        public int Priority;
        /// <summary>0 for the node named <c>&lt;model&gt;ml1</c> and 1 for <c>&lt;model&gt;ml2</c>, else -1:
        /// the two lights <c>Scene::SetTileLights</c> @0x55edf0 re-colours. <c>ProcessTilePart</c> @0x561720
        /// picks them by NAME (<c>strcasecmp</c> against the model name plus <c>"ml1"</c>/<c>"ml2"</c>), not by
        /// node ORDER, which gets a tile with a third light node wrong.</summary>
        public int MainSlot;
        public LightDef? Def;
    }

    private static readonly Dictionary<ModelDef, MergePlan> _plans = new();

    /// <summary>Every term <c>CreatePartBuckets</c> compares, stored rather than hashed.</summary>
    private readonly record struct MeshFields(string T0, string T1, string T2, string T3, string Mat, string Hint,
                                              int Fade, int Transparency, Vector3 Diffuse, Vector3 Ambient,
                                              Vector3 Specular, float Shininess, uint Flags);

    private static MeshFields FieldsOf(MeshDef m)
    {
        uint flags = (m.Shadow ? 1u : 0) | (m.Beaming ? 2u : 0) | (m.Lightmapped ? 4u : 0) | (m.RotateTexture ? 8u : 0);
        for (int sx = 0; sx < m.Uv.Length; sx++) if (m.Uv[sx].Length > 0) flags |= 16u << sx;
        if (m.Colors.Length > 0) flags |= 1u << 8;
        if (m.Tangents.Length > 0) flags |= 1u << 9;
        return new MeshFields(Low(m.Textures[0]), Low(m.Textures[1]), Low(m.Textures[2]), Low(m.Textures[3]),
                              Low(m.MaterialName), Low(m.RenderHint), m.TileFade, m.TransparencyHint,
                              m.Diffuse, m.Ambient, m.Specular, m.Shininess, flags);
    }

    private static string Low(string s) => s.ToLowerInvariant();

    // ---- the merge plan ----

    /// <summary>The merge plan for <paramref name="def"/>, built on first use and shared from then on. ONE call
    /// to <see cref="MeshArena.Acquire"/> per merged mesh, for the model, so every placement of the model
    /// draws the same vertex ranges.</summary>
    public static MergePlan Plan(ModelDef def)
    {
        if (_plans.TryGetValue(def, out MergePlan? have)) return have;
        MergePlan plan = BuildPlan(def);
        _plans[def] = plan;
        return plan;
    }

    /// <summary>How many models have a plan, and whether one is held for <paramref name="def"/>.</summary>
    public static bool HasPlan(ModelDef def) => _plans.ContainsKey(def);

    private static MergePlan BuildPlan(ModelDef def)
    {
        var plan = new MergePlan();
        var buckets = new Dictionary<long, List<int>>();
        var order = new List<long>();
        var verts = new Dictionary<long, int>();
        // The fields behind each bucket key, so a 64-bit hash collision cannot merge unlike meshes. Per
        // PLAN: two different models' buckets may hash alike, which is not a collision.
        var keyFields = new Dictionary<long, MeshFields>();

        Matrix4x4[] toModel = NodeMatrices(def);
        bool[] animatedHalf = AnimatedHalf(def);
        var filedLo = new Vector3(float.MaxValue);
        var filedHi = new Vector3(float.MinValue);
        var fadeLo = new Vector3(float.MaxValue);
        var fadeHi = new Vector3(float.MinValue);
        var fadeParts = new List<(Vector3 Lo, Vector3 Hi)>(2);
        for (int i = 0; i < def.Nodes.Length; i++)
        {
            NodeDef n = def.Nodes[i];
            // Mesh-bearing nodes the engine FILES in TileData+0x30 and unions its tile box from. An AABB
            // walkmesh node answers AsPartTriMesh (PartAABB's slot 0x60 is PartTriMesh::AsPartTriMesh, dumped
            // with tools/abi/vtable-slots.py), so it is filed and counts; tilefade 2 and 4 are moved out.
            if (n.Mesh != null && !animatedHalf[i] && n.Mesh.Indices.Length >= 3 && n.Mesh.Positions.Length > 0
                && (n.Kind == NodeKind.TriMesh || n.Kind == NodeKind.Aabb) && n.Mesh.TileFade == 0)
            {
                Box(n.Mesh, toModel[i], ref filedLo, ref filedHi);
                plan.FiledNodes++;
            }
            if (n.Mesh != null && !animatedHalf[i] && n.Mesh.TileFade == TileFadeFades && n.Mesh.Positions.Length > 0)
            {
                Box(n.Mesh, toModel[i], ref fadeLo, ref fadeHi);
                Bounds(n.Mesh.BMin, n.Mesh.BMax, toModel[i], out Vector3 flo, out Vector3 fhi);
                fadeParts.Add((flo, fhi));
                plan.HasFade = true;
            }

            if (n.Kind != NodeKind.TriMesh || n.Mesh == null || !n.Mesh.Render) continue;
            if (n.Mesh.Indices.Length < 3 || n.Mesh.Positions.Length == 0) continue;   // stock tiles ship empty trimeshes
            if (animatedHalf[i] || Animated(def, n)) continue;
            // The engine's own ceiling: CreatePartBuckets requires the bucket's total to stay BELOW 0xffff,
            // because the merged primitive is indexed as 16 bits -- and so is every Alloy draw
            // (GL_UNSIGNED_SHORT throughout). A full bucket is left alone and another opened. A single node
            // that cannot fit one on its own is refused here rather than silently overflowing an index.
            if (n.Mesh.Positions.Length > MaxBucketVerts) { _refusedBig++; Refuse($"{def.Name}: node '{n.Name}' has {n.Mesh.Positions.Length} vertices, past the 16-bit index -- it is not merged"); continue; }
            long key = BucketKey(n.Mesh);
            while (buckets.ContainsKey(key) && verts[key] + n.Mesh.Positions.Length > MaxBucketVerts)
                key = (long)Mix((ulong)key, 1);
            if (!buckets.TryGetValue(key, out List<int>? list))
            {
                buckets[key] = list = new List<int>(4);
                verts[key] = 0;
                order.Add(key);
                keyFields[key] = FieldsOf(n.Mesh);
            }
            else if (!keyFields.TryGetValue(key, out MeshFields have) || !have.Equals(FieldsOf(n.Mesh)))
            {
                // #25: the key is a 64-bit hash, so a collision (or a re-key landing on a live key) would
                // merge two unlike meshes. The fields are stored and compared, and a clash opens its own bucket.
                _collisions++;
                Refuse($"{def.Name}: bucket key collision on node '{n.Name}' -- it gets its own bucket");
                key = (long)Mix((ulong)key, 0x9e37);   // not-an-offset: a second probe for a colliding bucket key
                buckets[key] = list = new List<int>(4);
                verts[key] = 0;
                order.Add(key);
                keyFields[key] = FieldsOf(n.Mesh);
            }
            list.Add(i);
            verts[key] += n.Mesh.Positions.Length;
        }

        // CX-116: the tile's WALKMESH. Vanilla's floor cast is the BSP leaf's +0x90, which SceneBSPAddPart
        // @0x47ca10 fills from the tile part whose AsPartAABB is non-null -- i.e. from the tile model's own
        // aabb node. Counted per model so "the cast misses" can be told apart from "the model has none".
        foreach (NodeDef nd2 in def.Nodes)
            if (nd2.Kind == NodeKind.Aabb) plan.AabbNodes++;
        if (plan.AabbNodes > 0) _modelsWithAabb++; else _modelsNoAabb++;
        int nb = order.Count;
        plan.Meshes = new MeshDef[nb];
        plan.Ranges = new int[nb];
        plan.FadeClass = new int[nb];
        plan.Members = new int[nb][];
        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        for (int b = 0; b < nb; b++)
        {
            List<int> members = buckets[order[b]];
            MeshDef merged = Concat(def, members, toModel);
            plan.Meshes[b] = merged;
            plan.Members[b] = members.ToArray();
            plan.FadeClass[b] = def.Nodes[members[0]].Mesh!.TileFade;
            plan.Ranges[b] = MeshArena.Acquire(merged);
            _arenaAsks++;
            if (members.Count > 1) _mergedNodes += members.Count - 1;
            bmin = Vector3.Min(bmin, merged.BMin);
            bmax = Vector3.Max(bmax, merged.BMax);
        }
        if (nb == 0) { bmin = def.BMin; bmax = def.BMax; }
        // #8: the cull box has to cover the ANIMATED half too (the <resref>a subtree and the nodes an
        // animation names are not in any bucket), so the model's own stated bounds are unioned in.
        plan.BMin = Vector3.Min(bmin, def.BMin);
        plan.BMax = Vector3.Max(bmax, def.BMax);
        plan.FiledBMin = plan.FiledNodes > 0 ? filedLo : plan.BMin;
        plan.FiledBMax = plan.FiledNodes > 0 ? filedHi : plan.BMax;
        plan.FadeBMin = plan.HasFade ? fadeLo : Vector3.Zero;
        plan.FadeBMax = plan.HasFade ? fadeHi : Vector3.Zero;
        plan.FadeLo = new Vector3[fadeParts.Count];
        plan.FadeHi = new Vector3[fadeParts.Count];
        for (int f = 0; f < fadeParts.Count; f++) { plan.FadeLo[f] = fadeParts[f].Lo; plan.FadeHi[f] = fadeParts[f].Hi; }
        plan.Lights = PlanLights(def, toModel);
        plan.Sl1 = def.Find(def.Name + "sl1");
        plan.Sl2 = def.Find(def.Name + "sl2");
        if (plan.Sl1 >= 0) Pose(toModel[plan.Sl1], out plan.Sl1Pos, out plan.Sl1Rot);
        if (plan.Sl2 >= 0) Pose(toModel[plan.Sl2], out plan.Sl2Pos, out plan.Sl2Rot);
        foreach (int r in plan.Ranges) if (r < 0) { _refusedArena++; Refuse($"{def.Name}: the mesh arena refused a merged tile mesh -- that bucket draws nothing"); }
        return plan;
    }

    /// <summary>A model-space box for one mesh under one node transform, unioned into the pair.</summary>
    private static void Box(MeshDef m, in Matrix4x4 w, ref Vector3 lo, ref Vector3 hi)
    {
        Bounds(m.BMin, m.BMax, w, out Vector3 a, out Vector3 b);
        lo = Vector3.Min(lo, a);
        hi = Vector3.Max(hi, b);
    }

    /// <summary>Position and rotation out of a node's model-space matrix (the flame placement reads these).</summary>
    private static void Pose(in Matrix4x4 m, out Vector3 pos, out Quaternion rot)
    {
        pos = m.Translation;
        rot = Quaternion.CreateFromRotationMatrix(m);
    }

    /// <summary>Each node's rest transform into model space: the parent chain's position/orientation/scale, in
    /// pre-order (a parent always precedes its children, so one pass suffices).</summary>
    private static Matrix4x4[] NodeMatrices(ModelDef def)
    {
        var m = new Matrix4x4[def.Nodes.Length];
        for (int i = 0; i < def.Nodes.Length; i++)
        {
            NodeDef n = def.Nodes[i];
            Matrix4x4 local = Matrix4x4.CreateScale(n.Scale <= 0f ? 1f : n.Scale)
                            * Matrix4x4.CreateFromQuaternion(Normalise(n.Orientation));
            local.Translation = n.Position;
            if (n.Parent >= i) { _outOfOrder++; Refuse($"a model node names a parent at or after itself ({n.Parent} >= {i}) -- it is treated as a root"); }
            m[i] = n.Parent >= 0 && n.Parent < i ? local * m[n.Parent] : local;
        }
        return m;
    }

    private static Quaternion Normalise(Quaternion q)
        => q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;

    /// <summary>The merged primitive is indexed as 16 bits, so a bucket's vertices have to fit one
    /// (<c>CreatePartBuckets</c>' own <c>&lt; 0xffff</c> test, and Alloy's index type is
    /// <c>GL_UNSIGNED_SHORT</c> everywhere).</summary>
    private const int MaxBucketVerts = 65535;

    /// <summary>The ANIMATED half of a tile, as <c>CollapsePartTree</c> @0x562ff0 splits it: the subtree under
    /// the node named <c>&lt;resref&gt;a</c>, which <c>SpawnPartsForTile</c> @0x563450 gives the tile as a real
    /// <c>Gob</c>'s part tree while everything else is merged and filed. So a node under that one never merges,
    /// whether or not an animation names it today.</summary>
    private static bool[] AnimatedHalf(ModelDef def)
    {
        var flag = new bool[def.Nodes.Length];
        // The engine keys on Model+0x10, the model's OWN name, which is what ModelDef.Name holds -- and it
        // looks the node up at all only when SpawnPartsForTile's `border` argument is 0: a ring tile takes
        // the gob-less path and its whole tree is collapsed. CX keeps the subtree unmerged either way,
        // because a ring tile's placement shares the model's one plan and a plan cannot be two things at
        // once. A ring tile's animated half therefore stays its own parts instead of being merged away:
        // a handful of draws on the ring, and nothing else.
        int root = def.Find(def.Name + "a");
        if (root < 0) return flag;
        flag[root] = true;
        for (int i = 0; i < def.Nodes.Length; i++)
        {
            NodeDef n = def.Nodes[i];
            if (n.Parent >= 0 && flag[n.Parent]) flag[i] = true;
        }
        return flag;
    }

    /// <summary>A node merges only when nothing animates it: an animation that names it, or a geometry
    /// controller with more than one key. A deliberate difference from <c>CreatePartBuckets</c> @0x573570,
    /// which gates on the MODEL (one animation anywhere and nothing merges), so CX merges strictly more.
    /// <c>ponytail:</c> revisit if a tile animation is ever seen to move merged geometry.</summary>
    private static bool Animated(ModelDef def, NodeDef n)
    {
        foreach (ControllerTrack c in n.Controllers)
            if (c.Times.Length > 1) return true;
        for (ModelDef? m = def; m != null; m = m.Super)
            foreach (AnimDef a in m.Anims)
                foreach (AnimNode an in a.Nodes)
                    if (string.Equals(an.Name, n.Name, StringComparison.OrdinalIgnoreCase) && an.Controllers.Length > 0)
                        return true;
        return false;
    }

    /// <summary>Everything that has to agree for two tile meshes to merge: the textures, the material, the
    /// transparency and <c>tilefade</c> classes, the fixed-function terms and which UV sets exist. A
    /// difference in any of them is a different draw, so it is a different bucket.</summary>
    private static long BucketKey(MeshDef m)
    {
        ulong h = 0xcbf29ce484222325;   // not-an-offset: the FNV-1a 64-bit offset basis
        foreach (string s in m.Textures) h = MixStr(h, s);
        h = MixStr(h, m.MaterialName);
        h = MixStr(h, m.RenderHint);
        h = Mix(h, (ulong)m.TileFade);
        h = Mix(h, (ulong)m.TransparencyHint);
        h = Mix(h, Bits(m.Diffuse));
        h = Mix(h, Bits(m.Ambient));
        h = Mix(h, Bits(m.Specular));
        h = Mix(h, (ulong)BitConverter.SingleToUInt32Bits(m.Shininess));
        uint flags = (m.Shadow ? 1u : 0) | (m.Beaming ? 2u : 0) | (m.Lightmapped ? 4u : 0) | (m.RotateTexture ? 8u : 0);
        for (int s = 0; s < m.Uv.Length; s++) if (m.Uv[s].Length > 0) flags |= 16u << s;
        if (m.Colors.Length > 0) flags |= 1u << 8;
        if (m.Tangents.Length > 0) flags |= 1u << 9;
        return (long)Mix(h, flags);
    }

    private static ulong Bits(Vector3 v)
        => BitConverter.SingleToUInt32Bits(v.X) | ((ulong)BitConverter.SingleToUInt32Bits(v.Y) << 21)
           | ((ulong)BitConverter.SingleToUInt32Bits(v.Z) << 42);

    private static ulong Mix(ulong h, ulong v) => (h ^ v) * 0x100000001b3;   // not-an-offset: the FNV-1a 64-bit prime

    private static ulong MixStr(ulong h, string s)
    {
        foreach (char c in s) h = Mix(h, char.ToLowerInvariant(c));
        return Mix(h, (ulong)s.Length + 1);
    }

    /// <summary>Members whose node transform mirrors (det &lt; 0) and whose winding the merge therefore had to
    /// reverse, and merged members whose face table and triangle list disagree, so their per-face material ids
    /// could not be carried -- which the grass source reads. Both on the tile build's period line.</summary>
    private static int _mirroredMembers, _unalignedMembers;

    /// <summary>Concatenate one bucket's meshes into a single model-space <see cref="MeshDef"/>: positions by
    /// the node's rest matrix, normals and tangents by its rotation, indices offset. The merge the engine does
    /// per placement, done once for the model.</summary>
    private static MeshDef Concat(ModelDef def, List<int> members, Matrix4x4[] toModel)
    {
        MeshDef first = def.Nodes[members[0]].Mesh!;
        if (members.Count == 1 && toModel[members[0]].IsIdentity) return first;

        int nv = 0, ni = 0;
        foreach (int i in members) { MeshDef m = def.Nodes[i].Mesh!; nv += m.Positions.Length; ni += m.Indices.Length; }

        var outMesh = new MeshDef
        {
            Positions = new Vector3[nv],
            Normals = new Vector3[nv],
            Indices = new uint[ni],
            Textures = (string[])first.Textures.Clone(),
            MaterialName = first.MaterialName,
            RenderHint = first.RenderHint,
            Diffuse = first.Diffuse,
            Ambient = first.Ambient,
            Specular = first.Specular,
            Shininess = first.Shininess,
            TransparencyHint = first.TransparencyHint,
            TileFade = first.TileFade,
            Render = first.Render,
            Shadow = first.Shadow,
            Beaming = first.Beaming,
            Lightmapped = first.Lightmapped,
            RotateTexture = first.RotateTexture,
        };
        bool tangents = first.Tangents.Length > 0;
        bool colors = first.Colors.Length > 0;
        if (tangents) outMesh.Tangents = new Vector4[nv];
        if (colors) outMesh.Colors = new uint[nv];
        for (int s = 0; s < 4; s++) if (first.Uv[s].Length > 0) outMesh.Uv[s] = new Vector2[nv];

        var bmin = new Vector3(float.MaxValue);
        var bmax = new Vector3(float.MinValue);
        int vAt = 0, iAt = 0;
        float area = 0f;
        var surfaces = new int[ni / 3];
        foreach (int node in members)
        {
            MeshDef m = def.Nodes[node].Mesh!;
            Matrix4x4 w = toModel[node];
            var rot = new Matrix4x4(w.M11, w.M12, w.M13, 0, w.M21, w.M22, w.M23, 0, w.M31, w.M32, w.M33, 0, 0, 0, 0, 1);
            for (int v = 0; v < m.Positions.Length; v++)
            {
                Vector3 p = Vector3.Transform(m.Positions[v], w);
                outMesh.Positions[vAt + v] = p;
                bmin = Vector3.Min(bmin, p);
                bmax = Vector3.Max(bmax, p);
                outMesh.Normals[vAt + v] = v < m.Normals.Length
                    ? Vector3.Normalize(Vector3.TransformNormal(m.Normals[v], rot)) : Vector3.UnitZ;
                if (tangents && v < m.Tangents.Length)
                {
                    var tg = m.Tangents[v];
                    Vector3 td = Vector3.Normalize(Vector3.TransformNormal(new Vector3(tg.X, tg.Y, tg.Z), rot));
                    outMesh.Tangents[vAt + v] = new Vector4(td, tg.W);
                }
                if (colors && v < m.Colors.Length) outMesh.Colors[vAt + v] = m.Colors[v];
                for (int s = 0; s < 4; s++)
                    if (outMesh.Uv[s].Length > 0 && v < m.Uv[s].Length) outMesh.Uv[s][vAt + v] = m.Uv[s][v];
            }
            // A MIRRORED node (negative determinant) reverses triangle orientation when its vertices are
            // baked into model space, so the merged mesh would wind backwards there: back-face culled away,
            // or lit from behind -- a dark surface where the tileset authored a lit one. The engine never
            // meets this because it draws each node under its own matrix and lets the rasteriser's own
            // front-face rule follow the matrix; a concatenation has one matrix for all of them, so the
            // winding has to be corrected here instead. Flipped per member, and counted.
            bool mirrored = w.GetDeterminant() < 0f;
            if (mirrored) _mirroredMembers++;
            for (int k = 0; k + 2 < m.Indices.Length; k += 3)
            {
                outMesh.Indices[iAt + k] = m.Indices[k] + (uint)vAt;
                outMesh.Indices[iAt + k + 1] = m.Indices[mirrored ? k + 2 : k + 1] + (uint)vAt;
                outMesh.Indices[iAt + k + 2] = m.Indices[mirrored ? k + 1 : k + 2] + (uint)vAt;
            }
            // The member's per-face MATERIAL ids, carried across the merge: the grass source tests them
            // (AurGrassMaterialInUse, GrassField.Db) and a merged mesh that reported one id for every face
            // would grow grass on the whole tile.
            bool aligned = m.Faces.Length * 3 == m.Indices.Length;
            if (!aligned) _unalignedMembers++;
            for (int t = 0; t * 3 + 2 < m.Indices.Length; t++)
                // -1, not 0, for a member whose face table does not line up with its triangle list: 0 is a
                // REAL material id, and a member whose faces and indices disagree (a vertex-group rebuild)
                // would shift every later member's ids -- which is one way grass ends up on a wall.
                surfaces[iAt / 3 + t] = aligned ? m.Faces[t].Surface : -1;
            area += m.SurfaceArea;
            vAt += m.Positions.Length;
            iAt += m.Indices.Length;
        }
        outMesh.Faces = Faces(outMesh);
        for (int t = 0; t < outMesh.Faces.Length && t < surfaces.Length; t++)
            outMesh.Faces[t].Surface = surfaces[t];
        outMesh.BMin = bmin;
        outMesh.BMax = bmax;
        outMesh.Average = (bmin + bmax) * 0.5f;
        outMesh.Radius = (bmax - bmin).Length() * 0.5f;
        outMesh.SurfaceArea = area;
        return outMesh;
    }

    /// <summary>The merged triangle list as faces, with each face's plane: the shadow and hit passes read
    /// these, and a concatenation has no face adjacency across its members (-1, as a border face carries).</summary>
    private static FaceDef[] Faces(MeshDef m)
    {
        var f = new FaceDef[m.Indices.Length / 3];
        for (int t = 0; t < f.Length; t++)
        {
            int a = (int)m.Indices[t * 3], b = (int)m.Indices[t * 3 + 1], c = (int)m.Indices[t * 3 + 2];
            Vector3 n = Vector3.Cross(m.Positions[b] - m.Positions[a], m.Positions[c] - m.Positions[a]);
            float len = n.Length();
            n = len > 1e-12f ? n / len : Vector3.UnitZ;
            f[t] = new FaceDef
            {
                V0 = a, V1 = b, V2 = c, Surface = 0, SmoothGroup = 0,
                Adj0 = -1, Adj1 = -1, Adj2 = -1,
                PlaneNormal = n, PlaneDistance = -Vector3.Dot(n, m.Positions[a]),
            };
        }
        return f;
    }

    /// <summary>The model's light nodes, resolved to model space with their rest controller values. The first
    /// two are the tile's MAIN lights -- the pair <c>TileData+0x0</c>/<c>+0x8</c> holds and
    /// <c>Scene::SetTileLights</c> re-colours -- in node order, which is the order
    /// <c>MdlNodeLight::InternalCreateInstance</c> @0x43f800 creates them in.</summary>
    private static PlanLight[] PlanLights(ModelDef def, Matrix4x4[] toModel)
    {
        int n = 0;
        foreach (NodeDef nd in def.Nodes) if (nd.Kind == NodeKind.Light) n++;
        if (n == 0) return Array.Empty<PlanLight>();
        int ml1 = def.Find(def.Name + "ml1"), ml2 = def.Find(def.Name + "ml2");
        var lights = new PlanLight[n];
        int at = 0;
        for (int i = 0; i < def.Nodes.Length; i++)
        {
            NodeDef nd = def.Nodes[i];
            if (nd.Kind != NodeKind.Light) continue;
            LightDef? ld = nd.Light;
            lights[at] = new PlanLight
            {
                Node = i,
                Pos = toModel[i].Translation,
                Colour = Ctl3(nd, CtlColour, Vector3.One),
                Range = Ctl1(nd, CtlRadius, 0f),
                RangeAlt = Ctl1(nd, CtlShadowRadius, 0f),
                Multiplier = Ctl1(nd, CtlMultiplier, 1f),
                Shadow = ld?.Shadow ?? false,
                AmbientOnly = ld?.AmbientOnly ?? false,
                Dynamic = ld?.Dynamic ?? false,
                Priority = ld?.Priority ?? 0,
                MainSlot = i == ml1 ? 0 : i == ml2 ? 1 : -1,
                Def = ld,
            };
            at++;
        }
        return lights;
    }

    /// <summary>A controller's value at key 0, one float.</summary>
    private static float Ctl1(NodeDef n, int type, float dflt)
    {
        ControllerTrack? c = n.Controller(type);
        return c != null && c.Values.Length > 0 ? c.Values[0] : dflt;
    }

    /// <summary>A controller's value at key 0, three floats.</summary>
    private static Vector3 Ctl3(NodeDef n, int type, Vector3 dflt)
    {
        ControllerTrack? c = n.Controller(type);
        return c != null && c.Columns >= 3 && c.Values.Length >= 3 ? new Vector3(c.Values[0], c.Values[1], c.Values[2]) : dflt;
    }
}
