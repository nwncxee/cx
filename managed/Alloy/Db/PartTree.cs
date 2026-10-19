using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>
/// Builds an object's part range from its <see cref="ModelDef"/>: the port of <c>CreateInstanceTree</c>
/// @0x43c7c0, <c>ExpandReferenceParts</c> @0x462f70, <c>CollapsePartTree</c> @0x462ff0 and <c>CombineParts</c>
/// @0x473d30. An OBJECT spawns a reference node as its own object (<see cref="OnReference"/>); a TILE
/// instances it in place, collapses, merges. Parts are a contiguous RANGE of ABSOLUTE indices.
/// </summary>
public static partial class PartTree
{
    /// <summary>A reference node the OBJECT path found: the engine spawns <c>model</c> as its own object and
    /// attaches it to part <c>part</c> of <c>obj</c>. Set by the seam, the only layer that may create an
    /// object; null leaves reference nodes inert, which is what the off-device checks run with.</summary>
    public static Action<DbId, int, string>? OnReference;

    /// <summary>How a reference node's model name becomes a model. The model cache by default; the
    /// seam or a check may point it at its own table, which is how the off-device suite exercises
    /// <c>ExpandReferenceParts</c> before the cache exists.</summary>
    public static Func<string, ModelDef?> ResolveModel = ModelCache.Acquire;

    private static ExtenderLog? _log;
    private static bool _subscribed;

    /// <summary>Cumulative since boot (trees built, parts merged away, trees refused); <see cref="HeldSlots"/>
    /// is the only live figure here, and it comes off the allocator.</summary>
    private static int _built, _merged, _collapsed, _refused, _refusedParts, _animMesh, _dangly, _deepTrees;

    internal static void Attach(ExtenderLog log)
    {
        _log = log;
        // CX-113's contract: EVICTION is the edge at which a model's meshes stop being addressable, not
        // refcount zero -- a model at zero references is still resident and still drawable. Multicast, so a
        // second Attach must not subscribe twice.
        if (!_subscribed) { ModelCache.Evicting += MeshArena.Forget; _subscribed = true; }
    }

    /// <summary>The log this builder names its refusals on; settable so the off-device suite can silence it.</summary>
    internal static ExtenderLog? Log { get => _log; set => _log = value; }

    /// <summary>Part slots held by live chunks, straight off the ONE allocator that hands them out.
    /// ponytail: a chunk is rounded up to a power of two (at most 2x waste) and there is no compaction; at
    /// <see cref="SceneDb.MaxParts"/> = 512k slots that covers an area of 3k objects at ~170 parts each, and
    /// the ceiling is a counted refusal with a log line, never a quiet drop.</summary>
    internal static long HeldSlots => SceneDb.Parts.RangeHeld;

    // ---------------------------------------------------------------- the two entry points

    /// <summary>Give object <paramref name="obj"/> the part tree of <paramref name="model"/>, replacing any it
    /// had: the OBJECT path (<c>CreateInstanceTree</c> + <c>PartInitMaterial</c> +
    /// <c>CreateReferenceObjects</c>). False means the part table could not hold it -- logged and counted.</summary>
    public static bool Build(DbId obj, ModelDef model) => Build(obj, model, default);

    /// <summary>THE CALL THE TILE PATH MAKES. One tile placement's part range: the reference nodes expanded in
    /// place, the tree collapsed to model space at IDENTITY, and the shared <c>TileBuild.Plan(model)</c>
    /// applied, so the merged geometry is uploaded ONCE per model and every placement references it -- the
    /// placement itself belongs in the OBJECT transform. <paramref name="keepSubtree"/> is <c>&lt;model&gt;a</c>.</summary>
    public static bool BuildTile(DbId obj, ModelDef model, string keepSubtree = "")
        => Build(obj, model, new TreeOptions
        {
            ExpandReferences = true,
            Collapse = true,
            Combine = true,
            Rotation = Quaternion.Identity,
            KeepSubtree = keepSubtree,
        });

    /// <summary>The tile path: <paramref name="opt"/> turns on reference expansion, the collapse and the
    /// merge, in the order <c>SpawnPartsForTile</c> and <c>Scene::SpawnTile</c> run them.</summary>
    public static bool Build(DbId obj, ModelDef model, in TreeOptions opt)
    {
        if (!SceneDb.Objects.Valid(obj) || model.Nodes.Length == 0) return false;
        Free(obj);

        var plan = new List<Node>(model.Nodes.Length);
        Instance(plan, model, 0, -1, opt.ExpandReferences, 0);
        if (plan.Count == 0) return false;
        // NewCAurObject's .lod alternates: CreateInstanceTree per model, each its own root, all in ONE range
        int end0 = plan.Count, end1 = end0;
        if (opt.Lod1 != null) { Instance(plan, opt.Lod1, 0, -1, opt.ExpandReferences, 0); end1 = plan.Count; }
        if (opt.Lod2 != null) Instance(plan, opt.Lod2, 0, -1, opt.ExpandReferences, 0);

        if (!SceneDb.Parts.AllocRange(plan.Count, out int first))
        {
            _refused++;
            _refusedParts += plan.Count;
            _log?.Info($"PartTree: REFUSED -- no room for {plan.Count} parts of '{model.Name}' "
                       + $"({SceneDb.MaxParts} slots, {HeldSlots} held); this object draws nothing");
            return false;
        }
        // the plan's members draw out of the merged mesh, so a per-node range for them would be a second
        // copy of the same geometry on the GPU (finding 5)
        bool[]? swallowed = opt.Combine ? Swallowed(model, opt.KeepSubtree) : null;
        for (int k = 0; k < plan.Count; k++) Write(obj, first + k, first, plan[k], swallowed, model);
        if (plan.Count > end0)
            for (int k = 0; k < plan.Count; k++)
                SceneDb.PartLodTree[first + k] = (byte)(k < end0 ? 1 : k < end1 ? 2 : 3);
        SceneDb.ObjPartFirst[obj.Index] = first;
        SceneDb.ObjPartCount[obj.Index] = plan.Count;
        _built++;

        if (opt.Collapse) Collapse(obj, opt);
        if (opt.Combine) CombinePlan(obj, model, opt.Collapse);
        else if (opt.CombineOwn) Combine(obj);
        for (int k = 0; k < plan.Count; k++) ComputeBBox(first + k);
        // A built tree is one of the edges Gob::SetProcessFlag marks, so the engine's enable gate runs over it
        // before anything draws: every part whose subtree holds no geometry comes out off.
        PartGate.Reflow(obj);

        // CreateReferenceObjects OR ExpandReferenceParts, never both: the engine's object path spawns a
        // child object, its tile path instances the model in place (finding 12)
        if (OnReference != null && !opt.ExpandReferences)
            for (int k = 0; k < plan.Count; k++)
            {
                int p = first + k;
                // ponytail: an alternate's reference nodes spawn nothing -- the child would draw at every level
                if (SceneDb.PartKindOf[p] != PartKind.Reference || SceneDb.PartLodTree[p] > 1) continue;
                ReferenceDef? r = NodeOf(p)?.Reference;
                if (r != null && r.Model.Length > 0) OnReference(obj, p, r.Model);
            }

        SceneDb.Touch(obj, Dirty.Tree | Dirty.Xform | Dirty.Material);
        return true;
    }

    /// <summary>Release object <paramref name="obj"/>'s part range: every arena reference the parts hold goes
    /// back, the columns are cleared and the chunk returns to its size class.</summary>
    public static void Free(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        int first = SceneDb.ObjPartFirst[i], count = SceneDb.ObjPartCount[i];
        if (count <= 0) { SceneDb.ObjPartFirst[i] = 0; return; }
        for (int p = first; p < first + count; p++)
        {
            if (SceneDb.PartMesh[p] >= 0) MeshArena.Release(SceneDb.PartMesh[p]);
            if (SceneDb.PartMeshDef[p] != null) ModelCache.DropPlacement(SceneDb.PartMeshDef[p]!);
            Clear(p);
        }
        SceneDb.Parts.FreeRange(first, count);
        SceneDb.ObjPartFirst[i] = 0;
        SceneDb.ObjPartCount[i] = 0;
    }

    /// <summary>What the build is allowed to do to the tree. All off is the object path.</summary>
    public struct TreeOptions
    {
        /// <summary><c>ExpandReferenceParts</c>: a reference node's model is instanced as its children, in the
        /// same range, instead of becoming its own object.</summary>
        public bool ExpandReferences;
        /// <summary><c>CollapsePartTree</c>: flatten the tree, baking each part's pose into it.</summary>
        public bool Collapse;
        /// <summary>Merge the compatible meshes of the flattened tree through <c>TileBuild.Plan</c> -- the
        /// per-model plan built once and shared by every placement of that tile. The tile path uses this.</summary>
        public bool Combine;
        /// <summary>Merge through the <c>CombineParts</c> port in THIS file instead. It is the reference the
        /// shared plan agrees with and what the off-device suite drives; a caller with a tile model wants
        /// <see cref="Combine"/>, so that one model's merged geometry is uploaded once for every placement.</summary>
        public bool CombineOwn;
        /// <summary>The collapse's placement -- the engine passes the tile's world position and orientation.
        /// The Alloy path passes identity and keeps the placement in the object's transform, so the merged
        /// geometry stays MODEL-space and one arena copy serves every placement of that tile.</summary>
        public Vector3 Offset;
        public Quaternion Rotation;
        /// <summary>The one subtree the collapse leaves under the root: the engine's
        /// <c>&lt;modelname&gt;a</c>, the animated half of a tile. Empty keeps nothing back.</summary>
        public string KeepSubtree;
        /// <summary>The object path's <c>.lod</c> alternates (levels 1 and 2), instanced after the primary tree
        /// in the same range and tagged in <see cref="SceneDb.PartLodTree"/>. Null: no such level.</summary>
        public ModelDef? Lod1, Lod2;
    }

    // ---------------------------------------------------------------- CreateInstanceTreeR

    /// <summary>One planned part, before a slot exists for it.</summary>
    private readonly struct Node
    {
        internal readonly ModelDef Model;
        internal readonly int Index, Parent;
        internal Node(ModelDef m, int index, int parent) { Model = m; Index = index; Parent = parent; }
    }

    /// <summary>A reference node's expansion may itself hold one, so the walk is depth-limited rather than
    /// trusted. ponytail: 8 is deeper than any shipped tileset nests references.</summary>
    private const int MaxRefDepth = 8;

    /// <summary><c>CreateInstanceTreeR</c>: pre-order, a parent always before its children, one part per node
    /// of the model -- dummies, lights and emitters included, exactly as the engine instances them.</summary>
    private static void Instance(List<Node> into, ModelDef model, int node, int parent, bool expandRefs, int depth)
    {
        if ((uint)node >= (uint)model.Nodes.Length) return;
        int me = into.Count;
        into.Add(new Node(model, node, parent));
        NodeDef n = model.Nodes[node];
        foreach (int c in n.Children) Instance(into, model, c, me, expandRefs, depth);
        // ExpandReferenceParts: the referenced model's tree becomes a further CHILD of the reference node
        if (!expandRefs || n.Kind != NodeKind.Reference || depth >= MaxRefDepth) return;
        ReferenceDef? r = n.Reference;
        if (r == null || r.Model.Length == 0) return;
        ModelDef? sub = ResolveModel(r.Model);
        if (sub == null || sub.Nodes.Length == 0) return;
        Instance(into, sub, 0, me, expandRefs, depth + 1);
    }

    /// <summary>Write one planned part's columns: the rest pose off its node, the kind, the material
    /// (<c>PartInitMaterial</c>) and the arena range for its geometry.</summary>
    private static int _probed;

    /// <summary>A few merged tile meshes, once: are the normals the lit stage will read unit-length and
    /// pointing where the source geometry pointed? A tile that renders BLACK under correct ambient is almost
    /// always a normal problem, and the merge is the only step that rotates them. It also states the vertex
    /// streams -- the arena's Static format is pos3 nrm3 uv2 chart2 plus a packed tangent and paint word.</summary>
    private static void ProbeMerged(ModelDef model, int node, MeshDef m, int range)
    {
        // the off-device fixtures merge too, and they would otherwise spend the whole budget at boot
        if (_probed >= 3 || _log == null || model.Name.StartsWith("cxtest", StringComparison.OrdinalIgnoreCase)) return;
        _probed++;
        int bad = 0, up = 0;
        var avg = Vector3.Zero;
        for (int i = 0; i < m.Normals.Length; i++)
        {
            float len = m.Normals[i].Length();
            if (len < 0.9f || len > 1.1f) bad++;
            if (m.Normals[i].Z > 0.5f) up++;
            avg += m.Normals[i];
        }
        if (m.Normals.Length > 0) avg /= m.Normals.Length;
        string head = m.Normals.Length > 0
            ? $"n[0]=({m.Normals[0].X:F2},{m.Normals[0].Y:F2},{m.Normals[0].Z:F2})" : "NO NORMALS";
        MeshArena.Bounds(range, out Vector3 lo, out Vector3 hi, out _, out float rad);
        _log.Info($"PartTree: [merged] '{model.Name}' node={node} range={range} verts={m.Positions.Length} "
                  + $"nrm={m.Normals.Length} uv0={m.Uv[0].Length} tan={m.Tangents.Length} col={m.Colors.Length} "
                  + $"| {head} avg=({avg.X:F2},{avg.Y:F2},{avg.Z:F2}) notUnit={bad} up={up} "
                  + $"| bounds lo=({lo.X:F1},{lo.Y:F1},{lo.Z:F1}) hi=({hi.X:F1},{hi.Y:F1},{hi.Z:F1}) r={rad:F1} "
                  + $"| tex0='{m.Textures[0]}' diffuse=({m.Diffuse.X:F2},{m.Diffuse.Y:F2},{m.Diffuse.Z:F2}) "
                  + $"ambient=({m.Ambient.X:F2},{m.Ambient.Y:F2},{m.Ambient.Z:F2}) render={m.Render}");
    }

    /// <summary>What a node of a TILE model actually draws, which is not always what its
    /// <see cref="NodeDef.Mesh"/> names: the plan merges per MODEL, so a bucket HEAD draws the merged mesh in
    /// model space (hence <see cref="CombinePlan"/> zeroing its pose), a node the bucket SWALLOWED draws
    /// nothing, anything else its own mesh (null here). Deterministic, so a record interned on (model, node) holds.</summary>
    public static MeshDef? PlanMeshFor(ModelDef model, int node, out bool swallowed)
    {
        swallowed = false;
        if (!TileBuild.HasPlan(model)) return null;
        TileBuild.MergePlan plan = TileBuild.Plan(model);
        for (int b = 0; b < plan.Members.Length; b++)
        {
            int[] members = plan.Members[b];
            for (int m = 0; m < members.Length; m++)
            {
                if (members[m] != node) continue;
                if (m == 0) return b < plan.Meshes.Length ? plan.Meshes[b] : null;
                swallowed = true;
                return null;
            }
        }
        return null;
    }

    /// <summary>Which of <paramref name="model"/>'s nodes the shared plan folds into another node's mesh: they
    /// get no range of their own, because the merged mesh already carries their triangles.</summary>
    private static bool[]? Swallowed(ModelDef model, string? keepSubtree)
    {
        TileBuild.MergePlan plan = TileBuild.Plan(model);
        if (plan.Members.Length == 0) return null;
        var swallowed = new bool[model.Nodes.Length];
        foreach (int[] bucket in plan.Members)
            for (int m = 1; m < bucket.Length; m++)
                if ((uint)bucket[m] < (uint)swallowed.Length) swallowed[bucket[m]] = true;
        // The kept subtree is the tile's ANIMATED half, which the collapse leaves hanging off the root with
        // its local poses intact -- the engine never hands it to CombineParts, and folding it into a
        // model-space mesh would freeze it. Its nodes keep their own geometry.
        int keep = keepSubtree != null && keepSubtree.Length > 0 ? model.Find(keepSubtree) : -1;
        if (keep >= 0) Unswallow(model, keep, swallowed, 0);
        return swallowed;
    }

    private static void Unswallow(ModelDef model, int node, bool[] swallowed, int depth)
    {
        if ((uint)node >= (uint)swallowed.Length || depth >= MaxHops) return;
        swallowed[node] = false;
        foreach (int c in model.Nodes[node].Children) Unswallow(model, c, swallowed, depth + 1);
    }

    private static void Write(DbId obj, int p, int first, in Node plan, bool[]? swallowed, ModelDef primary)
    {
        NodeDef n = plan.Model.Nodes[plan.Index];
        // CONTRACTS.md: the feed's own columns are seeded here, BEFORE anything can read them -- a recycled
        // chunk that kept the dead tenant's PartSlot would have ObjectDied(A) release B's records.
        DbFeed.InitPart(p);
        SceneDb.PartObj[p] = obj;
        SceneDb.PartModel[p] = plan.Model;
        SceneDb.PartNode[p] = plan.Index;
        SceneDb.PartParent[p] = plan.Parent < 0 ? -1 : first + plan.Parent;
        SceneDb.PartPos[p] = n.Position;
        SceneDb.PartRot[p] = n.Orientation;
        SceneDb.PartScale[p] = n.Scale;
        SceneDb.PartWorld[p] = Matrix4x4.Identity;
        SceneDb.PartEnabled[p] = true;
        SceneDb.PartMerged[p] = false;
        SceneDb.PartLodTree[p] = 0;
        SceneDb.PartAlpha[p] = RestAlpha(n);
        SceneDb.PartSelfIllum[p] = RestSelfIllum(n);
        SceneDb.PartKindOf[p] = Kind(n.Kind);
        // CX-114's stated refusal: the arena holds ONE immutable copy per distinct mesh, so a part whose
        // vertices change per instance and per frame cannot be served from it. An animmesh keeps its base
        // mesh's range (its rest frame draws correctly) and its morph is not applied; a dangly keeps its
        // range and its solve is not applied. Both are counted and named on the period line, never silent.
        // The owned animation runtime morphing into a per-instance arena range is the follow-up.
        if (SceneDb.PartKindOf[p] == PartKind.AnimMesh) _animMesh++;
        else if (SceneDb.PartKindOf[p] == PartKind.Dangly) _dangly++;
        // only the PRIMARY model's nodes are in the plan: a reference expansion's parts index another
        // model's node array, and the plan says nothing about them
        bool folded = swallowed != null && ReferenceEquals(plan.Model, primary)
                      && (uint)plan.Index < (uint)swallowed.Length && swallowed[plan.Index];
        SceneDb.PartMeshDef[p] = folded ? null : n.Mesh;
        // a skin node's bone slots and weights put the mesh in the arena's Skinned format (finding 10), and a
        // rigged dangly node's generated ones do the same -- one range for every instance, since the weights
        // depend on the rest mesh alone. Whichever Acquire runs FIRST decides the format, so it is this one.
        SceneDb.PartMesh[p] = !folded && n.Mesh != null ? MeshArena.Acquire(n.Mesh, n.Skin ?? n.Rig?.Skin) : -1;
        if (!folded && n.Mesh != null) ModelCache.NotePlacement(n.Mesh);   // CX-113's meshes-vs-placements census
        if (folded) { SceneDb.PartMerged[p] = true; SceneDb.PartKindOf[p] = PartKind.Merged; }
        InitMaterial(p, n.Mesh);
        SceneDb.PartRefObj[p] = default;
        SceneDb.PartBMin[p] = SceneDb.PartBMax[p] = SceneDb.PartCenter[p] = Vector3.Zero;
        SceneDb.PartRadius[p] = 0f;
    }

    /// <summary>The node's REST alpha -- its <c>alpha</c> controller at key 0, 1 when it has none. The engine
    /// seeds it with the rest pose (<c>Gob::Reset</c> @0x499bd0 runs <c>AnimateHierarchy</c> at time 0 over the
    /// model's own nodes), and <c>PartTriMesh::Mat</c> @0x450960 sends that value as BOTH the vertex colour's
    /// alpha and the diffuse material's alpha, which is why an <c>alpha 0</c> node draws nothing.</summary>
    private static float RestAlpha(NodeDef n)
    {
        ControllerTrack? c = n.Controller(Ctl.Alpha);
        return c is { Values.Length: > 0 } ? c.Values[0] : 1f;
    }

    /// <summary>The node's REST self-illumination, zero when it has none.
    /// <inheritdoc cref="RestAlpha"/></summary>
    private static Vector3 RestSelfIllum(NodeDef n)
    {
        ControllerTrack? c = n.Controller(Ctl.SelfIllumColor);
        return c is { Values.Length: >= 3 } ? new Vector3(c.Values[0], c.Values[1], c.Values[2]) : Vector3.Zero;
    }

    private static void Clear(int p)
    {
        DbFeed.InitPart(p);
        SceneDb.PartLodTree[p] = 0;
        SceneDb.PartObj[p] = default;
        SceneDb.PartModel[p] = null;
        SceneDb.PartMeshDef[p] = null;
        SceneDb.PartMtr[p] = null;
        SceneDb.PartMesh[p] = -1;
        SceneDb.PartRefObj[p] = default;
        SceneDb.PartNode[p] = 0;
        SceneDb.PartParent[p] = -1;
        SceneDb.PartKindOf[p] = PartKind.Dummy;
        SceneDb.PartEnabled[p] = false;
        SceneDb.PartMerged[p] = false;
        SceneDb.PartMtl[p] = default;
        SceneDb.PartWorld[p] = Matrix4x4.Identity;
        SceneDb.PartPos[p] = Vector3.Zero;
        SceneDb.PartRot[p] = Quaternion.Identity;
        SceneDb.PartScale[p] = 1f;
        SceneDb.PartSelfIllum[p] = Vector3.Zero;
        SceneDb.PartBMin[p] = SceneDb.PartBMax[p] = SceneDb.PartCenter[p] = Vector3.Zero;
        SceneDb.PartRadius[p] = 0f;
        for (int t = 0; t < 4; t++) SceneDb.PartTexId[p * 4 + t] = 0;
        SceneDb.PartPltOwn[p] = false;
    }

    /// <summary>The part kind a node of this kind instances. The pose reads it to pick the controller table
    /// (one file id means a different field per kind), so a fixture that seats parts by hand has to agree
    /// with it -- which is why it is not private.</summary>
    internal static PartKind Kind(NodeKind k) => k switch
    {
        NodeKind.TriMesh => PartKind.Mesh,
        NodeKind.Skin => PartKind.Skin,
        NodeKind.Dangly => PartKind.Dangly,
        NodeKind.AnimMesh => PartKind.AnimMesh,
        NodeKind.Emitter => PartKind.Emitter,
        NodeKind.Light => PartKind.Light,
        NodeKind.Reference => PartKind.Reference,
        NodeKind.Aabb => PartKind.Aabb,
        NodeKind.Camera => PartKind.Camera,
        NodeKind.Trigger => PartKind.Trigger,
        _ => PartKind.Dummy,
    };

    /// <summary>The node part <paramref name="p"/> instances, or null.</summary>
    internal static NodeDef? NodeOf(int p)
    {
        ModelDef? m = SceneDb.PartModel[p];
        return m != null && (uint)SceneDb.PartNode[p] < (uint)m.Nodes.Length ? m.Nodes[SceneDb.PartNode[p]] : null;
    }

    // ---------------------------------------------------------------- PartInitMaterial

    /// <summary><c>PartInitMaterial</c> @0x43de50 in CX terms. The engine does two things there: load the
    /// material's textures, and choose the vertex-primitive class from the node's kind -- which here is the
    /// part's <see cref="PartKind"/> plus its arena range. The terms come off the mesh, folded with the
    /// <c>.mtr</c> its <c>materialname</c> names.</summary>
    private static void InitMaterial(int p, MeshDef? mesh)
    {
        SceneDb.PartMtr[p] = null;
        if (mesh == null) { SceneDb.PartMtl[p] = default; return; }
        MtrDef? mtr = MtrDef.Of(mesh);
        SceneDb.PartMtr[p] = mtr;
        SceneDb.PartMtl[p] = new PartMaterial
        {
            Diffuse = mesh.Diffuse,
            Ambient = mesh.Ambient,
            Specular = mesh.Specular,
            Shininess = mesh.Shininess,
            TransparencyHint = mesh.TransparencyHint,
            TileFade = mesh.TileFade,
            // the .mtr's own renderhint wins: that is what gives a material file its normal-mapped stage
            RenderHint = mtr != null && mtr.RenderHint != 0 ? mtr.RenderHint : MtrDef.Hint(mesh.RenderHint),
            Render = mesh.Render,
            Shadow = mesh.Shadow,
            Beaming = mesh.Beaming,
            Lightmapped = mesh.Lightmapped,
            RotateTexture = mesh.RotateTexture,
        };
        for (int t = 0; t < 4; t++) SceneDb.PartTexId[p * 4 + t] = 0;
        SceneDb.PartPltOwn[p] = false;
        // The two columns above that come from the FILE are provisional while the layer has not answered for
        // this material: an answer can still arrive, and nothing else re-asks (PartTree.Material.cs).
        if (mtr == null && MtrDef.Pending(mesh)) WaitForMaterial(p);
    }

    // ---------------------------------------------------------------- ComputePartBBox

    /// <summary>The quantum <c>ComputePartBBox</c> rounds its corners to.</summary>
    private const float BBoxGrid = 0.001f;

    /// <summary><c>ComputePartBBox</c> @0x461370: the part's geometry taken through its OWN pose -- so the box
    /// is in the part's parent space -- with each corner rounded to <see cref="BBoxGrid"/>, and all zeroes for
    /// a part with no faces or one that does not render. The mesh's model-space bounds are
    /// <see cref="MeshArena.Bounds"/>; this is the placed box the cull and the bake read.</summary>
    internal static void ComputeBBox(int p)
    {
        MeshDef? m = SceneDb.PartMeshDef[p];
        // ComputePartBBox's own gate is node+0xb8 (face count) or node+0x284 (vertex count) at zero -- there
        // is NO render filter, and the hit test reads this box for a non-rendering part too (finding 14)
        if (m == null || m.Indices.Length < 3 || m.Positions.Length == 0)
        {
            SceneDb.PartBMin[p] = SceneDb.PartBMax[p] = SceneDb.PartCenter[p] = Vector3.Zero;
            SceneDb.PartRadius[p] = 0f;
            return;
        }
        Vector3 pos = SceneDb.PartPos[p];
        Quaternion rot = Normalize(SceneDb.PartRot[p]);
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        for (int i = 0; i < m.Positions.Length; i++)
        {
            Vector3 w = Round(pos + Vector3.Transform(m.Positions[i], rot));
            lo = Vector3.Min(lo, w);
            hi = Vector3.Max(hi, w);
        }
        SceneDb.PartBMin[p] = lo;
        SceneDb.PartBMax[p] = hi;
        Vector3 c = (lo + hi) * 0.5f;
        SceneDb.PartCenter[p] = c;
        float r2 = 0f;
        for (int i = 0; i < m.Positions.Length; i++)
        {
            float d = (pos + Vector3.Transform(m.Positions[i], rot) - c).LengthSquared();
            if (d > r2) r2 = d;
        }
        SceneDb.PartRadius[p] = MathF.Sqrt(r2);
    }

    /// <summary><c>Round(Vector const&amp;, float)</c> @0x460dc0, per component.</summary>
    private static Vector3 Round(Vector3 v) => new(
        MathF.Floor(v.X / BBoxGrid + 0.5f) * BBoxGrid,
        MathF.Floor(v.Y / BBoxGrid + 0.5f) * BBoxGrid,
        MathF.Floor(v.Z / BBoxGrid + 0.5f) * BBoxGrid);

    // ---------------------------------------------------------------- CollapsePartTree

    /// <summary><c>CollapsePartTree</c> @0x462ff0: every part's pose is composed with its parent's and written
    /// back as its own, then the hierarchy links are cut, so the range becomes a FLAT list of placed parts.
    /// The one exception is <see cref="TreeOptions.KeepSubtree"/> (the engine's <c>&lt;model&gt;a</c>), left
    /// hanging off the root with its local pose intact -- the animated half of a tile.</summary>
    private static void Collapse(DbId obj, in TreeOptions opt)
    {
        int i = obj.Index;
        int first = SceneDb.ObjPartFirst[i], count = SceneDb.ObjPartCount[i];
        if (count <= 0) return;
        string keep = opt.KeepSubtree ?? "";
        Quaternion place = Normalize(opt.Rotation);
        int kept = -1;

        // pre-order, so a parent's composed pose is in place before its children read it
        for (int k = 0; k < count; k++)
        {
            int p = first + k;
            int parent = SceneDb.PartParent[p];
            if (parent < 0)
            {
                SceneDb.PartPos[p] = opt.Offset + Vector3.Transform(SceneDb.PartPos[p], place);
                SceneDb.PartRot[p] = Quaternion.Multiply(place, Normalize(SceneDb.PartRot[p]));
                continue;
            }
            if (keep.Length > 0 && kept < 0 && NameOf(p).Equals(keep, StringComparison.OrdinalIgnoreCase))
            {
                kept = p;
                continue;   // its pose stays local, and nothing under it is visited
            }
            if (kept >= 0 && Under(p, kept, first)) continue;
            SceneDb.PartPos[p] = SceneDb.PartPos[parent] + Vector3.Transform(SceneDb.PartPos[p], SceneDb.PartRot[parent]);
            SceneDb.PartRot[p] = Quaternion.Multiply(SceneDb.PartRot[parent], Normalize(SceneDb.PartRot[p]));
        }

        for (int k = 0; k < count; k++)
        {
            int p = first + k;
            if (p == kept || (kept >= 0 && Under(p, kept, first))) continue;
            SceneDb.PartParent[p] = -1;
        }
        if (kept >= 0) SceneDb.PartParent[kept] = first;
        _collapsed++;
    }

    /// <summary>Is <paramref name="p"/> inside <paramref name="root"/>'s subtree? The hop cap is the
    /// walk's refusal, not a stack overflow.</summary>
    private static bool Under(int p, int root, int first)
    {
        int hop = 0;
        for (int q = SceneDb.PartParent[p]; q >= first; hop++, q = SceneDb.PartParent[q])
        {
            if (q == root) return true;
            if (hop >= MaxHops)
            {
                _deepTrees++;
                if (_deepTrees <= 4)
                    _log?.Info($"PartTree: a part chain is deeper than {MaxHops} hops ('{NameOf(p)}') -- the "
                               + "collapse treats it as outside the kept subtree");
                return false;
            }
        }
        return false;
    }

    /// <summary>Parent hops the collapse will climb. ponytail: deeper than any shipped model nests, and past
    /// it the walk refuses and says so rather than running off a cycle.</summary>
    private const int MaxHops = 64;

    internal static string NameOf(int p) => NodeOf(p)?.Name ?? "";

    // ---------------------------------------------------------------- CombineParts

    /// <summary>What <c>CreatePartBuckets</c> @0x473570 compares, verbatim: the three texture names and the
    /// material name (case-insensitively), the render hint, the tile-fade class, the diffuse and ambient
    /// colours, the shadow flag, and the vertex FORMAT -- which attribute streams exist. Specular and
    /// shininess are deliberately absent, as they are in the engine.</summary>
    private readonly struct MergeKey : IEquatable<MergeKey>
    {
        private readonly string _t0, _t1, _t2, _mtl;
        private readonly int _hint, _fade, _streams;
        private readonly bool _shadow;
        private readonly Vector3 _diffuse, _ambient;

        internal MergeKey(int p)
        {
            MeshDef m = SceneDb.PartMeshDef[p]!;
            PartMaterial mt = SceneDb.PartMtl[p];
            _t0 = m.Textures[0]; _t1 = m.Textures[1]; _t2 = m.Textures[2]; _mtl = m.MaterialName;
            _hint = mt.RenderHint; _fade = mt.TileFade; _shadow = mt.Shadow;
            _diffuse = mt.Diffuse; _ambient = mt.Ambient;
            _streams = (m.Uv[0].Length != 0 ? 1 : 0) | (m.Uv[1].Length != 0 ? 2 : 0)
                     | (m.Uv[2].Length != 0 ? 4 : 0) | (m.Uv[3].Length != 0 ? 8 : 0)
                     | (m.Colors.Length != 0 ? 16 : 0) | (m.Normals.Length != 0 ? 32 : 0)
                     | (m.Tangents.Length != 0 ? 64 : 0);
        }

        public bool Equals(MergeKey o)
            => _hint == o._hint && _fade == o._fade && _shadow == o._shadow && _streams == o._streams
               && _diffuse == o._diffuse && _ambient == o._ambient
               && string.Equals(_t0, o._t0, StringComparison.OrdinalIgnoreCase)
               && string.Equals(_t1, o._t1, StringComparison.OrdinalIgnoreCase)
               && string.Equals(_t2, o._t2, StringComparison.OrdinalIgnoreCase)
               && string.Equals(_mtl, o._mtl, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? o) => o is MergeKey k && Equals(k);

        public override int GetHashCode()
            => HashCode.Combine(_t0.ToLowerInvariant(), _t1.ToLowerInvariant(), _t2.ToLowerInvariant(),
                                _mtl.ToLowerInvariant(), _hint, _fade, _streams, _diffuse);
    }

    /// <summary>The engine's 16-bit index ceiling on a merged bucket: <c>CreatePartBuckets</c> refuses a
    /// member whose vertices would take the bucket's total to 65535 or past it.</summary>
    private const int MergeVertexCeiling = 0xffff;   // not-an-offset: the engine's 16-bit index ceiling

    /// <summary><c>CombineParts</c> @0x473d30: the eligible mesh parts are bucketed by <see cref="MergeKey"/>,
    /// each bucket's first part absorbs the others' geometry as ONE new <see cref="MeshDef"/>, and every
    /// absorbed part is marked merged away -- the engine's <c>part+0x96 = 0</c>
    /// (<c>PartTriMesh::renderFlag</c>), so the part stays in the tree and draws nothing.</summary>
    private static void Combine(DbId obj)
    {
        // DIFFERENCE FROM VANILLA: the engine buckets in the order CollapsePartTree's
        // breadth-first flatten produced, so for two parts of one material it may pick the other one as the
        // bucket head. The head only decides whose LOCAL FRAME the merged vertices land in, which the part's
        // own transform then undoes, so the drawn geometry is the same in the same place either way.
        int i = obj.Index;
        int first = SceneDb.ObjPartFirst[i], count = SceneDb.ObjPartCount[i];
        if (count <= 1) return;
        var heads = new Dictionary<MergeKey, int>();
        var members = new Dictionary<int, List<int>>();
        var verts = new Dictionary<int, int>();

        for (int k = 0; k < count; k++)
        {
            int p = first + k;
            if (!Mergeable(p)) continue;
            MeshDef mesh = SceneDb.PartMeshDef[p]!;
            if (mesh.Positions.Length == 0) continue;
            var key = new MergeKey(p);
            if (heads.TryGetValue(key, out int head) && verts[head] + mesh.Positions.Length < MergeVertexCeiling)
            {
                members[head].Add(p);
                verts[head] += mesh.Positions.Length;
                continue;
            }
            // either no bucket yet or the one there is full: this part starts the bucket for this key
            heads[key] = p;
            members[p] = new List<int>();
            verts[p] = mesh.Positions.Length;
        }

        foreach (var kv in members)
        {
            if (kv.Value.Count == 0) continue;
            MergeInto(kv.Key, kv.Value);
            _merged += kv.Value.Count;
        }
    }

    /// <summary>Apply the shared merge plan: each bucket's first node becomes the part that draws the merged
    /// mesh (model space, so its own pose folds away), every node the bucket swallowed is marked merged -- the
    /// engine's <c>part+0x96 = 0</c> -- and the plan's arena range is REFERENCED, not rebuilt, so one upload
    /// serves every placement. The census follows: the node meshes are dropped, the merged mesh placed once.</summary>
    private static void CombinePlan(DbId obj, ModelDef model, bool collapsed)
    {
        int i = obj.Index;
        int first = SceneDb.ObjPartFirst[i], count = SceneDb.ObjPartCount[i];
        if (count <= 0) return;
        TileBuild.MergePlan plan = TileBuild.Plan(model);
        if (plan.Members.Length == 0) return;
        if (!collapsed)
        {
            // the plan's meshes are MODEL space: without the collapse the head still carries a local pose and
            // the geometry would be transformed twice (finding 19)
            _log?.Info($"PartTree: '{model.Name}' asked for the shared merge without the collapse -- REFUSED "
                       + "(the plan's geometry is model space; use BuildTile)");
            _refused++;
            return;
        }

        // node -> part, for the parts that instance THIS model (a reference expansion's parts name another)
        var partOf = new int[model.Nodes.Length];
        Array.Fill(partOf, -1);
        for (int k = 0; k < count; k++)
        {
            int p = first + k;
            if (SceneDb.PartModel[p] == model && (uint)SceneDb.PartNode[p] < (uint)partOf.Length
                && partOf[SceneDb.PartNode[p]] < 0) partOf[SceneDb.PartNode[p]] = p;
        }

        for (int b = 0; b < plan.Members.Length; b++)
        {
            int[] members = plan.Members[b];
            if (members.Length == 0) continue;
            // The head is members[0] and ONLY members[0]: Swallowed() and DrawRecipe.PlanMeshFor() both read
            // the plan that way, and a head chosen differently here would leave the draw record and the part
            // columns disagreeing about which node carries the merged mesh -- which is parts silently not
            // drawn. If its part is missing, or is in the kept animated subtree, the whole bucket stands.
            int head0 = (uint)members[0] < (uint)partOf.Length ? partOf[members[0]] : -1;
            if (head0 < 0 || SceneDb.PartParent[head0] >= 0) continue;
            int head = -1;
            foreach (int node in members)
            {
                int p = (uint)node < (uint)partOf.Length ? partOf[node] : -1;
                if (p < 0) continue;
                if (SceneDb.PartParent[p] >= 0) continue;
                if (SceneDb.PartMesh[p] >= 0) MeshArena.Release(SceneDb.PartMesh[p]);
                if (SceneDb.PartMeshDef[p] != null) ModelCache.DropPlacement(SceneDb.PartMeshDef[p]!);
                if (head < 0)
                {
                    head = p;
                    // it may have been marked folded at Write (the plan's first member had no part of its
                    // own); as this bucket's head it draws the merged mesh, so the marking comes off
                    SceneDb.PartMerged[p] = false;
                    SceneDb.PartKindOf[p] = PartKind.Mesh;
                    SceneDb.PartMeshDef[p] = plan.Meshes[b];
                    SceneDb.PartMesh[p] = plan.Ranges[b];
                    MeshArena.AddRef(plan.Ranges[b]);
                    ProbeMerged(model, members[0], plan.Meshes[b], plan.Ranges[b]);
                    ModelCache.NotePlacement(plan.Meshes[b]);
                    // the plan's geometry is MODEL space: the part's own pose is already in the vertices
                    SceneDb.PartPos[p] = Vector3.Zero;
                    SceneDb.PartRot[p] = Quaternion.Identity;
                    SceneDb.PartScale[p] = 1f;
                    InitMaterial(p, plan.Meshes[b]);
                    continue;
                }
                SceneDb.PartMeshDef[p] = null;
                SceneDb.PartMesh[p] = -1;
                SceneDb.PartMerged[p] = true;
                SceneDb.PartKindOf[p] = PartKind.Merged;
                _merged++;
            }
        }
    }

    /// <summary><c>CreatePartBuckets</c>' eligibility: a trimesh part that is neither a dangly mesh nor an
    /// AABB walkmesh, whose node's model carries NO animations (<c>Model+0xa0 &lt; 1</c>, measured). Anything
    /// else is left exactly as it is.</summary>
    private static bool Mergeable(int p)
    {
        PartKind k = SceneDb.PartKindOf[p];
        if (k != PartKind.Mesh && k != PartKind.Skin && k != PartKind.AnimMesh) return false;
        if (SceneDb.PartMeshDef[p] == null || SceneDb.PartMerged[p]) return false;
        // CreatePartBuckets tests node+0x118 == 1 on BOTH the bucket head and the candidate
        if (!SceneDb.PartMtl[p].Render) return false;
        ModelDef? m = SceneDb.PartModel[p];
        return m != null && m.Anims.Length == 0;
    }

    /// <summary>Concatenate the members' geometry into a NEW <see cref="MeshDef"/> in the head's own space,
    /// swap the head onto it, and mark each member merged away. The engine does it by REFERENCE instead
    /// (<c>VertexPrimitive::MergePart</c> repoints the member's primitive at the head's); CX builds the
    /// combined vertex data, because the arena holds one immutable copy per distinct mesh.</summary>
    private static void MergeInto(int head, List<int> members)
    {
        MeshDef h = SceneDb.PartMeshDef[head]!;
        int nv = h.Positions.Length, ni = h.Indices.Length;
        foreach (int m in members)
        {
            MeshDef d = SceneDb.PartMeshDef[m]!;
            nv += d.Positions.Length;
            ni += d.Indices.Length;
        }

        var merged = new MeshDef
        {
            Positions = new Vector3[nv],
            Normals = h.Normals.Length != 0 ? new Vector3[nv] : Array.Empty<Vector3>(),
            // the bucket key already requires these to be present or absent together, so the merged mesh
            // carries them rather than dropping them (finding 28)
            Colors = h.Colors.Length != 0 ? new uint[nv] : Array.Empty<uint>(),
            Tangents = h.Tangents.Length != 0 ? new Vector4[nv] : Array.Empty<Vector4>(),
            Indices = new uint[ni],
            Textures = (string[])h.Textures.Clone(),
            MaterialName = h.MaterialName,
            RenderHint = h.RenderHint,
            Diffuse = h.Diffuse, Ambient = h.Ambient, Specular = h.Specular, Shininess = h.Shininess,
            TransparencyHint = h.TransparencyHint, TileFade = h.TileFade,
            Render = h.Render, Shadow = h.Shadow, Beaming = h.Beaming,
            Lightmapped = h.Lightmapped, RotateTexture = h.RotateTexture,
        };
        for (int s = 0; s < 4; s++)
            merged.Uv[s] = h.Uv[s].Length != 0 ? new Vector2[nv] : Array.Empty<Vector2>();

        // the head's own pose is the merged mesh's frame, so the head itself needs no transform
        Quaternion inv = Quaternion.Conjugate(Normalize(SceneDb.PartRot[head]));
        Vector3 hp = SceneDb.PartPos[head];

        int vo = 0, io = 0;
        Append(merged, h, Vector3.Zero, Quaternion.Identity, ref vo, ref io);
        foreach (int m in members)
        {
            MeshDef d = SceneDb.PartMeshDef[m]!;
            Quaternion q = Quaternion.Multiply(inv, Normalize(SceneDb.PartRot[m]));
            Vector3 t = Vector3.Transform(SceneDb.PartPos[m] - hp, inv);
            Append(merged, d, t, q, ref vo, ref io);
            // the engine's part+0x96 = 0 -- PartTriMesh::renderFlag. The part stays in the tree, draws nothing.
            SceneDb.PartMerged[m] = true;
            if (SceneDb.PartMesh[m] >= 0) MeshArena.Release(SceneDb.PartMesh[m]);
            ModelCache.DropPlacement(d);
            SceneDb.PartMeshDef[m] = null;
            SceneDb.PartMesh[m] = -1;
            SceneDb.PartKindOf[m] = PartKind.Merged;
        }
        Measure(merged);

        if (SceneDb.PartMesh[head] >= 0) MeshArena.Release(SceneDb.PartMesh[head]);
        ModelCache.DropPlacement(h);
        SceneDb.PartMeshDef[head] = merged;
        SceneDb.PartMesh[head] = MeshArena.Acquire(merged);
        ModelCache.NotePlacement(merged);
        InitMaterial(head, merged);
    }

    private static Quaternion Normalize(Quaternion q)
        => q.LengthSquared() > 1e-12f ? Quaternion.Normalize(q) : Quaternion.Identity;

    private static void Append(MeshDef into, MeshDef src, Vector3 t, Quaternion q, ref int vo, ref int io)
    {
        int n = src.Positions.Length;
        bool rot = q != Quaternion.Identity;
        for (int i = 0; i < n; i++)
        {
            into.Positions[vo + i] = rot ? t + Vector3.Transform(src.Positions[i], q) : t + src.Positions[i];
            if (into.Normals.Length != 0)
                into.Normals[vo + i] = i < src.Normals.Length
                    ? (rot ? Vector3.Transform(src.Normals[i], q) : src.Normals[i])
                    : Vector3.Zero;
            for (int s = 0; s < 4; s++)
                if (into.Uv[s].Length != 0)
                    into.Uv[s][vo + i] = i < src.Uv[s].Length ? src.Uv[s][i] : Vector2.Zero;
            if (into.Colors.Length != 0) into.Colors[vo + i] = i < src.Colors.Length ? src.Colors[i] : 0xFFFFFFFFu;
            if (into.Tangents.Length != 0)
            {
                Vector4 t4 = i < src.Tangents.Length ? src.Tangents[i] : default;
                Vector3 t3 = rot ? Vector3.Transform(new Vector3(t4.X, t4.Y, t4.Z), q) : new Vector3(t4.X, t4.Y, t4.Z);
                into.Tangents[vo + i] = new Vector4(t3, t4.W);
            }
        }
        for (int i = 0; i < src.Indices.Length; i++) into.Indices[io + i] = (uint)(src.Indices[i] + vo);
        vo += n;
        io += src.Indices.Length;
    }

    private static void Measure(MeshDef m)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);
        var sum = Vector3.Zero;
        foreach (Vector3 p in m.Positions) { lo = Vector3.Min(lo, p); hi = Vector3.Max(hi, p); sum += p; }
        m.BMin = lo; m.BMax = hi;
        m.Average = m.Positions.Length > 0 ? sum / m.Positions.Length : Vector3.Zero;
        float r2 = 0f;
        foreach (Vector3 p in m.Positions) { float d = p.LengthSquared(); if (d > r2) r2 = d; }
        m.Radius = MathF.Sqrt(r2);
    }

    /// <summary>The area edge: give every free chunk that touches the top of the part table back to its
    /// cursor, so the index space does not drift upward across areas.</summary>
    internal static void AreaEdge()
    {
        SceneDb.Parts.TrimRanges();
        _probed = 0;   // the probe is per AREA: the tiles it is about are built at the area edge
    }

    // ---------------------------------------------------------------- report

    /// <summary>One clause of the <c>[alloy]</c> period line.</summary>
    internal static string Stats(int frames)
    {
        if (_built == 0 && _refused == 0) return "";
        return $"parts {HeldSlots} in {_built} trees"
               + (_collapsed > 0 ? $", {_collapsed} collapsed" : "")
               + (_merged > 0 ? $", {_merged} merged away" : "")
               + (_animMesh > 0 ? $", {_animMesh} animmesh parts NOT MORPHED (rest frame from the arena)" : "")
               + (_dangly > 0 ? $", {_dangly} dangly parts NOT SOLVED (rest frame from the arena)" : "")
               + (_refused > 0 ? $", {_refused} trees REFUSED ({_refusedParts} parts past the {SceneDb.MaxParts} slots)" : "")
               + (_deepTrees > 0 ? $", {_deepTrees} chains past {MaxHops} hops" : "")
               + (PartGate.Stats() is { Length: > 0 } gate ? ", " + gate : "")
               + (MaterialNote() is { Length: > 0 } mtr ? ", " + mtr : "");
    }

    /// <summary>Off-device reset, for the self-check suite.</summary>
    internal static void ResetForCheck()
    {
        _built = _merged = _collapsed = _refused = _refusedParts = _animMesh = _dangly = _deepTrees = 0;
        _mtrWait = new MtrWaitState();
        PartGate.ResetForCheck();
    }
}
