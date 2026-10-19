using System;
using System.Collections.Generic;
using System.Numerics;

namespace CX.Alloy.Mdl;

/// <summary>One parsed, post-processed model -- CX's replacement for the engine's <c>Model</c> +
/// <c>MdlNode*</c> tree. Immutable once the parser returns it, except for <see cref="Name"/> and
/// <see cref="Super"/>, which the model cache writes on the game thread before handing it out; every instance
/// shares it, so any thread may read it. Engine-free by contract, which lets <c>tools/mdlcheck</c> run it.</summary>
public sealed class ModelDef
{
    /// <summary>The model's resref, lower-cased (the engine's <c>FindModel</c> key). The parser fills in the
    /// name the FILE states; the model cache then rewrites it to the resref the model was asked for, counting
    /// the difference (the engine's "Model Names Differ").</summary>
    public string Name = "";
    /// <summary>The model's name as its file states it, case kept (<c>Model+0x10</c>): what an object made
    /// without a name of its own is called, and so what a GUI panel loaded from a <c>.gui</c> is named.</summary>
    public string StatedName = "";
    /// <summary>The supermodel resref, lower-cased; empty for none (the file's <c>"null"</c>).</summary>
    public string SuperName = "";
    /// <summary>Resolved by the model cache, never by the parser.</summary>
    public ModelDef? Super;
    /// <summary>The file's classification byte (effect, tile, character, door, ...).</summary>
    public byte Classification;
    /// <summary>The model is affected by fog.</summary>
    public bool Fog = true;
    /// <summary><c>setanimationscale</c>; 1 when absent.</summary>
    public float AnimScale = 1f;
    /// <summary>Model-space bounds of the geometry, as the file states them or the post-process derives them.</summary>
    public Vector3 BMin, BMax;
    /// <summary>Bounding radius about the origin.</summary>
    public float Radius;
    /// <summary>Every node, pre-order: index 0 is the root and a parent always precedes its children.</summary>
    public NodeDef[] Nodes = Array.Empty<NodeDef>();
    /// <summary>The model's own animations (not its supermodel's; the cache walks the chain).</summary>
    public AnimDef[] Anims = Array.Empty<AnimDef>();
    /// <summary>Node index by name, case-insensitive (animation binding, attach points, <c>GetPartByName</c>).</summary>
    public Dictionary<string, int> NodeByName = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>True when the source was ASCII (the post-process derived what a binary file stores).</summary>
    public bool FromAscii;
    /// <summary>A content hash of the source bytes: the identity the census and the mesh arena share on.</summary>
    public ulong ContentHash;
    /// <summary>How many bounds the parse refused on this file -- a cyclic or out-of-range node link, a
    /// declared element count the file could not supply, a node whose <c>parent</c> names nothing. A model
    /// with a non-zero count parsed, but not everything the file claimed is in it; <see cref="RefusalNote"/>
    /// says what, and every consumer reports it rather than passing it over.</summary>
    public int Refusals;
    /// <summary>What <see cref="Refusals"/> counted, in one clause, or empty.</summary>
    public string RefusalNote = "";

    /// <summary>The engine's node counter (<c>Model+0x58</c>) after the parse: a binary model states it, and
    /// an ASCII one counts it. <c>beginmodelgeom</c> seeds the counter of a model WITH a supermodel from this
    /// value plus one, which is the number an unmatched node keeps -- so the chain has to carry it.</summary>
    public int NodeIdNext;

    /// <summary>The model's node numbers have been re-keyed off its supermodel chain (the engine's
    /// <c>MaxTree::SynchronizeTree</c>). Until then an ASCII model numbers them in file order.</summary>
    public bool NodeIdsSynced;

    /// <summary>The node named <paramref name="name"/>, or -1.</summary>
    public int Find(string name) => NodeByName.TryGetValue(name, out int i) ? i : -1;
}

/// <summary>What a node is. A node carries exactly one payload for its kind (a skin, dangly or animmesh node
/// carries <see cref="NodeDef.Mesh"/> as well as its own).</summary>
public enum NodeKind : byte { Dummy, Light, Emitter, Camera, Reference, TriMesh, Skin, AnimMesh, Dangly, Aabb, Trigger }

/// <summary>One node of a <see cref="ModelDef"/>: hierarchy, rest pose, controllers and its kind's payload.</summary>
public sealed class NodeDef
{
    public string Name = "";
    /// <summary>This node's index in <see cref="ModelDef.Nodes"/>, and its parent's (-1 for the root).</summary>
    public int Index, Parent = -1;
    public int[] Children = Array.Empty<int>();
    public NodeKind Kind;
    /// <summary>The file's node-type bit set, kept verbatim (the kind is derived from it).</summary>
    public uint ContentFlags;
    /// <summary>The file's part number (the engine's per-model part index).</summary>
    public int PartNumber;
    /// <summary>Where this node sat in the FILE: the index <c>ParseNode</c> counted it at, before any
    /// supermodel re-key. <c>MdlPost.Resync</c> needs it, because the engine seeds its counter from the
    /// supermodel and an unmatched node keeps <c>base + fileIndex</c>.</summary>
    public int FileIndex;
    public bool InheritColor;
    /// <summary>Rest pose, parent-relative: the geometry's position/orientation/scale controllers at key 0.</summary>
    public Vector3 Position;
    public Quaternion Orientation = Quaternion.Identity;
    public float Scale = 1f;
    /// <summary>Every geometry controller as the file states it (colour, alpha, self-illumination, light and
    /// emitter parameters, and the rest-pose keys above).</summary>
    public ControllerTrack[] Controllers = Array.Empty<ControllerTrack>();

    public MeshDef? Mesh;
    public SkinDef? Skin;
    public DanglyDef? Dangly;
    /// <summary>A dangly node's generated rig (<see cref="CX.Alloy.Physics.DanglyRig"/>), built once at load.</summary>
    public CX.Alloy.Physics.DanglyRig? Rig;
    public AnimMeshDef? AnimMesh;
    public AabbDef? Aabb;
    public LightDef? Light;
    public EmitterDef? Emitter;
    public ReferenceDef? Reference;

    /// <summary>A consumer's resolved memo of this node's EMITTER rest keys -- the first key of every animatable
    /// emitter controller, in <c>CX.Alloy.EmitterSim</c>'s channel ordinals. A pure function of
    /// <see cref="Controllers"/>, so there is no invalidation edge; idempotent and published only once filled,
    /// so two threads racing cost one duplicate array and never a half-written one.</summary>
    public Vector4[]? EmRest;

    /// <summary>The first controller of <paramref name="type"/>, or null.</summary>
    public ControllerTrack? Controller(int type)
    {
        foreach (ControllerTrack c in Controllers) if (c.Type == type) return c;
        return null;
    }

    /// <summary>Memo for <see cref="LitKeyed"/>: 0 not yet asked, 1 no, 2 yes. The controller array is
    /// immutable once parsed, so no invalidation edge is needed, and the race is benign by construction -- a
    /// pure function of that array, an atomic byte store, and 0 the only value meaning "ask again".</summary>
    private byte _litKeyed;

    /// <summary>Does a MULTI-KEY light track drive this node (colour, radius or multiplier), i.e. does its
    /// light never settle? Asked once per light per frame by the bake's static verdict and again by the halo
    /// enrolment, so the answer -- a property of the parsed node -- is computed once and kept.</summary>
    public bool LitKeyed
    {
        get
        {
            if (_litKeyed != 0) return _litKeyed == 2;
            bool keyed = false;
            foreach (ControllerTrack c in Controllers)
                if (c.Times.Length > 1
                 && (c.Type == Ctl.LightColor || c.Type == Ctl.LightRadius || c.Type == Ctl.LightMultiplier))
                { keyed = true; break; }
            _litKeyed = keyed ? (byte)2 : (byte)1;
            return keyed;
        }
    }
}

/// <summary>One controller: <see cref="Times"/>.Length keys of <see cref="Columns"/> floats each in
/// <see cref="Values"/> (a bezier key stores three rows: value, in-tangent, out-tangent).</summary>
public sealed class ControllerTrack
{
    /// <summary>The controller id as the FILE numbers it -- position 8, orientation 20, scale 36, light
    /// colour 76, mesh alpha 128, ... See <see cref="Ctl"/>, which is the complete table, and
    /// <c>MdlBinary.EngineControllerId</c> for the engine's own in-memory numbering.</summary>
    public int Type;
    public int Columns;
    public bool Bezier;
    public float[] Times = Array.Empty<float>();
    public float[] Values = Array.Empty<float>();
}

/// <summary>Render geometry after the post-process: one GPU vertex per (position, normal, uv) combination, a
/// triangle list over them, and the faces the walkmesh, hit test and shadow passes read.</summary>
public sealed class MeshDef
{
    public Vector3[] Positions = Array.Empty<Vector3>();
    /// <summary>Final (welded, smoothing-group) normals: the per-model source <c>MeshLod</c> reads.</summary>
    public Vector3[] Normals = Array.Empty<Vector3>();
    /// <summary>xyz tangent, w handedness; empty when the mesh has no UVs to derive them from.</summary>
    public Vector4[] Tangents = Array.Empty<Vector4>();
    /// <summary>Up to four UV sets; an absent set is an empty array. Set 0 is the diffuse one; 1..3 are what a
    /// content shader reads at <c>vTcIn1..3</c>, uploaded only for a mesh that carries one.</summary>
    public Vector2[][] Uv = { Array.Empty<Vector2>(), Array.Empty<Vector2>(), Array.Empty<Vector2>(), Array.Empty<Vector2>() };
    /// <summary>Per-vertex RGBA8, or empty.</summary>
    public uint[] Colors = Array.Empty<uint>();
    /// <summary>Triangle list into the vertex arrays, three per face, in face order.</summary>
    public uint[] Indices = Array.Empty<uint>();
    public FaceDef[] Faces = Array.Empty<FaceDef>();

    /// <summary>Texture names: <c>bitmap</c>/<c>texture0</c> then <c>texture1..3</c>; empty for none.</summary>
    public string[] Textures = { "", "", "", "" };
    /// <summary>The <c>.mtr</c> material name (<c>materialname</c>), or empty.</summary>
    public string MaterialName = "";
    /// <summary>The EE render hint (<c>renderhint</c>), or empty.</summary>
    public string RenderHint = "";
    public Vector3 Diffuse = Vector3.One, Ambient = Vector3.One, Specular;
    public float Shininess;
    public int TransparencyHint, TileFade;
    public bool Render = true, Shadow = true, Beaming, Lightmapped, RotateTexture;
    /// <summary>Model-space bounds of this mesh, its average point and its total face area.</summary>
    public Vector3 BMin, BMax, Average;
    public float Radius, SurfaceArea;
    /// <summary><c>MeshArena.Epoch</c> when the arena last refused this mesh, 0 never: a retry at the same
    /// value is refused without re-hashing.</summary>
    internal int ArenaRefused;

    /// <summary>What the file authored, before the post-process paired positions with UVs. Non-null whenever
    /// the source stated it (every ASCII mesh, and a binary one for the arrays it still carries).</summary>
    public MeshRaw? Raw;
    /// <summary><c>renderhint</c> as the engine's <c>SharedMaterial::ExtractRenderHint</c> scores it:
    /// <c>none</c> 1, <c>normalandspecmapped</c> 2, <c>normaltangents</c> 3, anything else (and absent) 0.</summary>
    public int RenderHintId;
    /// <summary>The engine's precomputed draw groups: <c>vertexindicescount</c> gives a count per group and
    /// <c>vertexindices</c> the group's indices; <c>leftoverfaces</c> the faces no group covers. CX rebuilds
    /// its own batches, so these are carried for fidelity and for the comparison, not consumed.</summary>
    public int[] VertexIndexCounts = Array.Empty<int>();
    public uint[][] VertexIndices = Array.Empty<uint[]>();
    public uint[] LeftoverFaces = Array.Empty<uint>();
    /// <summary>How many UV streams the compiled file says it uploaded (the engine's <c>uvSetCount</c>);
    /// zero for an ASCII mesh, where <see cref="Uv"/>'s own lengths are the answer.</summary>
    public int UvSetCount;
    /// <summary>True when the mesh was authored as a walkmesh/AABB node, which the engine forces to
    /// <c>render = 0</c> and <c>shadow = 0</c>.</summary>
    public bool WalkMesh;

    /// <summary>A field-for-field copy, for a consumer that owns per-INSTANCE geometry (the part-boundary
    /// stitch): the arrays are shared until the caller replaces the ones it rewrites, so a copy that changes
    /// only its normals costs one array.</summary>
    public MeshDef Copy() => (MeshDef)MemberwiseClone();
}

/// <summary>One triangle: corner vertex indices, smoothing group, surface (material) id, the three edge-adjacent
/// faces (-1 for none) and its plane.</summary>
public struct FaceDef
{
    public int V0, V1, V2;
    public int SmoothGroup, Surface;
    public int Adj0, Adj1, Adj2;
    public Vector3 PlaneNormal;
    public float PlaneDistance;
}

/// <summary>Skinning: up to four (bone, weight) pairs per vertex, the bone slots' node indices, and the bind pose.</summary>
public sealed class SkinDef
{
    /// <summary>Per vertex, four weights summing to 1.</summary>
    public Vector4[] Weights = Array.Empty<Vector4>();
    /// <summary>Per vertex, four bone SLOTS packed one per byte (slot -> <see cref="BoneNodes"/>).</summary>
    public uint[] Bones = Array.Empty<uint>();
    /// <summary>Slot -> node index in the owning model. <b>A compiled binary states a PART index instead</b>
    /// (the engine resolves bone names against the instantiated part tree in <c>PartSkin::PostProcess</c>, not
    /// against the file), so this member is the one place where the two readers legitimately disagree.</summary>
    public int[] BoneNodes = Array.Empty<int>();
    /// <summary>Which of the two forms <see cref="BoneNodes"/> is in, stated by the reader that filled it
    /// rather than inferred from its length. False is the ASCII form (one entry per SLOT, holding that slot's
    /// node); true is the compiled binary's BONE MAP (one entry per model NODE, holding that node's slot, or
    /// -1). Length cannot tell them apart once the bind arrays are absent, as they are on stock cloaks.</summary>
    public bool BoneNodesAreMap;
    /// <summary>Slot -> the bone NAME the file stated, in the ASCII <c>weights</c> block's spelling; empty for
    /// a compiled binary, which carries no names. The name outlives <see cref="BoneNodes"/> because a grafted
    /// piece's bones live in the skeleton it is grafted onto, which its own file knows nothing about.</summary>
    public string[] BoneNames = Array.Empty<string>();
    /// <summary>The faithful per-vertex bone slots, four per vertex: a slot can exceed 255, which
    /// <see cref="Bones"/>'s byte packing cannot carry.</summary>
    public ushort[] BoneIndices = Array.Empty<ushort>();
    /// <summary><c>boneconstantindices</c>, as the file states it.</summary>
    public int[] BoneConstantIndices = Array.Empty<int>();
    /// <summary>Influences per vertex (the engine's <c>SkinVertexWeight::influenceCount</c>, which is half the
    /// field count its <c>sscanf</c> matched). Part of the weld key, so the parser keeps it.</summary>
    public int[] Influences = Array.Empty<int>();
    /// <summary>Per slot, the inverse bind pose (node space -> bone space).</summary>
    public Quaternion[] BindRotation = Array.Empty<Quaternion>();
    public Vector3[] BindTranslation = Array.Empty<Vector3>();
    /// <summary>Per SLOT, the inverse bind pose already flattened out of whichever form stated it, for a
    /// consumer that rebuilt the bone table (the part-boundary stitch). A null entry, and any slot past the
    /// array, is derived from the assembled rest pose instead; an empty array is every slot derived.</summary>
    public Matrix4x4?[] SlotBind = Array.Empty<Matrix4x4?>();

    /// <summary>A field-for-field copy, for a consumer that owns per-INSTANCE skin data (the part-boundary
    /// stitch): the arrays are shared until the caller replaces the ones it rewrites.</summary>
    public SkinDef Copy() => (SkinDef)MemberwiseClone();
}

/// <summary>Dangly mesh: per-vertex constraint weights and the three solver terms.</summary>
public sealed class DanglyDef
{
    public float[] Constraints = Array.Empty<float>();
    public float Displacement, Tightness, Period;
}

/// <summary>Animmesh: the sampled vertex and UV sets an animation morphs between.</summary>
public sealed class AnimMeshDef
{
    public float SamplePeriod;
    public Vector3[][] Positions = Array.Empty<Vector3[]>();
    public Vector2[][] Uvs = Array.Empty<Vector2[]>();
    /// <summary>The per-frame smooth normals the post-process derives (the engine's <c>animNormals</c> at
    /// <c>MdlNodeAnimMesh+0x2f0</c>); no keyword states them.</summary>
    public Vector3[][] Normals = Array.Empty<Vector3[]>();
}

/// <summary>AABB tree over a mesh's faces (walkmesh / hit test): a node is a leaf when <see cref="AabbNode.Face"/> >= 0.</summary>
public sealed class AabbDef
{
    public AabbNode[] Tree = Array.Empty<AabbNode>();
}

public struct AabbNode
{
    public Vector3 BMin, BMax;
    public int Left, Right, Face;
    public int Plane;
}

/// <summary>Light node parameters the file states as fields (colour, radius and multiplier are controllers).</summary>
public sealed class LightDef
{
    public float FlareRadius;
    public int Priority = 5;
    /// <summary><c>MdlNodeLight</c>'s constructor sets shadow, isdynamic, affectdynamic and fadingLight to 1
    /// and <c>lightpriority</c> to 5 (<c>MdlNodeLight.c:418-425</c>), so those are the defaults a file
    /// overrides rather than the zeroes a C# field would get.</summary>
    public bool AmbientOnly, Dynamic = true, AffectDynamic = true, Shadow = true, GenerateFlare,
                Fading = true;
    public string[] FlareTextures = Array.Empty<string>();
    public float[] FlareSizes = Array.Empty<float>(), FlarePositions = Array.Empty<float>();
    public Vector3[] FlareColors = Array.Empty<Vector3>();
}

/// <summary>Emitter node parameters the file states as fields (rates, sizes, colours are controllers).</summary>
public sealed class EmitterDef
{
    public string Update = "", Render = "", Blend = "", Texture = "", ChunkName = "";
    public int XGrid, YGrid, SpawnType, RenderOrder;
    public uint Flags;
    public float DeadSpace, BlastRadius, BlastLength;
    public bool TwoSidedTex, Loop;
}

/// <summary>Reference node: a model attached at this node by name.</summary>
public sealed class ReferenceDef
{
    public string Model = "";
    public bool Reattachable;
}

/// <summary>One animation: length, cross-fade time, root, events, and per-node controller tracks bound by NAME
/// (an animation lives in a supermodel and drives whichever instance tree names the same nodes).</summary>
public sealed class AnimDef
{
    public string Name = "";
    public float Length, TransTime;
    public string AnimRoot = "";
    public AnimEvent[] Events = Array.Empty<AnimEvent>();
    public AnimNode[] Nodes = Array.Empty<AnimNode>();
}

public struct AnimEvent
{
    public float Time;
    public string Name;
}

/// <summary>One animated node of an <see cref="AnimDef"/>: its name, its tracks, and an animmesh's frames.</summary>
public sealed class AnimNode
{
    public string Name = "";
    public ControllerTrack[] Controllers = Array.Empty<ControllerTrack>();
    public AnimMeshDef? AnimMesh;
    /// <summary>This node's parent in <see cref="AnimDef.Nodes"/>, -1 for the animation's root. The animation
    /// carries its OWN hierarchy because the engine's node binding is scoped to it (a child is matched only
    /// against its matched parent's children: <c>SynchronizeNodes</c> @0x453760). Left at -1 for every node,
    /// the model cache falls back to a flat name match and says so once.</summary>
    public int Parent = -1;
}

/// <summary><see cref="ControllerTrack.Type"/> is a FILE id -- the canonical <c>.mdl</c> number both readers
/// agree on, so <c>Position == 8</c>, <c>LightColor == 76</c>, <c>LightMultiplier == 140</c>. The engine
/// renumbers them on load into <c>Part</c> byte offsets; <c>MdlBinary.EngineControllerId</c> is that map.
/// <para>COMPLETE for this client: three geometry controllers, two mesh, five light, thirty-nine emitter.</para></summary>
public static class Ctl
{
    public const int Position = 8, Orientation = 20, Scale = 36;

    /// <summary>Mesh controllers (<c>MdlNodeTriMesh</c> and every mesh subclass).</summary>
    public const int SelfIllumColor = 100, Alpha = 128;

    /// <summary>Light controllers.</summary>
    public const int LightColor = 76, LightRadius = 88, LightShadowRadius = 96,
                     LightVerticalDisplacement = 100, LightMultiplier = 140;

    /// <summary>Emitter controllers. <c>alphaMid</c>, <c>colorMid</c> and the <c>percent*</c> trio are parsed
    /// into engine fields nothing in the client reads; they are kept because the file states them.</summary>
    public const int EmAlphaEnd = 80, EmAlphaStart = 84, EmBirthRate = 88, EmBounceCo = 92, EmColorEnd = 96,
                     EmColorStart = 108, EmCombineTime = 120, EmDrag = 124, EmFps = 128, EmFrameEnd = 132,
                     EmFrameStart = 136, EmGrav = 140, EmLifeExp = 144, EmMass = 148, EmP2PBezier2 = 152,
                     EmP2PBezier3 = 156, EmParticleRot = 160, EmRandVel = 164, EmSizeStart = 168,
                     EmSizeEnd = 172, EmSizeStartY = 176, EmSizeEndY = 180, EmSpread = 184, EmThreshold = 188,
                     EmVelocity = 192, EmXSize = 196, EmYSize = 200, EmBlurLength = 204, EmLightningDelay = 208,
                     EmLightningRadius = 212, EmLightningScale = 216, EmDetonate = 228, EmAlphaMid = 448,
                     EmColorMid = 452, EmPercentStart = 464, EmPercentMid = 465, EmPercentEnd = 466,
                     EmSizeMid = 468, EmSizeMidY = 472;
}

/// <summary>What a mesh node's file states before the post-process derives the render geometry: the authored
/// vertex, UV and face arrays, with a face naming its position corners and its UV corners SEPARATELY. The
/// engine throws the split away once <c>ProcessVerts</c> has paired them; CX keeps it, because the oracle
/// compares an ASCII read against a compiled one on exactly these arrays.</summary>
public sealed class MeshRaw
{
    /// <summary>Authored positions, one per <c>verts</c> line.</summary>
    public Vector3[] Verts = Array.Empty<Vector3>();
    /// <summary>Authored UV sets, one per <c>tverts</c>/<c>tverts1..3</c> array, indexed by a face's
    /// <c>T</c> corners and not by vertex. THREE floats wide, because a <c>tverts</c> line states three and
    /// <c>NodeVertex::operator==</c> compares all three -- dropping the unused <c>w</c> before the weld would
    /// merge two corners the engine keeps apart. Only the upload drops it.</summary>
    public Vector3[][] TVerts = { Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector3>() };
    /// <summary>Per-face UV corner triples for sets 1..3 (<c>texindices1..3</c>), set 0 being the face's own
    /// <see cref="RawFace.T"/>. The engine reads these as floats and rounds; an absent set is empty.</summary>
    public Vector3[][] TexIndices = { Array.Empty<Vector3>(), Array.Empty<Vector3>(), Array.Empty<Vector3>() };
    /// <summary>Authored per-vertex colours (<c>colors</c>), RGB in 0..1.</summary>
    public Vector3[] Colors = Array.Empty<Vector3>();
    /// <summary>Authored per-vertex normals (<c>normals</c>), when the file states them.</summary>
    public Vector3[] Normals = Array.Empty<Vector3>();
    /// <summary>Authored tangent frames (<c>tangents</c>), when the file states them.</summary>
    public Vector4[] Tangents = Array.Empty<Vector4>();
    public RawFace[] Faces = Array.Empty<RawFace>();
    /// <summary><c>mirrorlist</c>: the faces whose winding the skin post-process flips.</summary>
    public int[] MirrorList = Array.Empty<int>();
}

/// <summary>One authored face: three position corners, the smoothing group, three UV corners for set 0, and
/// the surface/material id -- the eight integers of a <c>faces</c> line, in its order.</summary>
public struct RawFace
{
    public int V0, V1, V2;
    public int SmoothGroup;
    public int T0, T1, T2;
    public int Surface;
}
