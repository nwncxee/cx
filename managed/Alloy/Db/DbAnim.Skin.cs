using CX.Engine;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>The skin palettes: <c>SkinDef</c>'s bind arrays plus the DB pose columns into 64-bone matrices,
/// written straight into the renderer's bone stage. Transcribed from <c>RenderSkinned</c> @0x482c70,
/// <c>GatherBones</c> @0x482b10 and the matrix half of <c>ExecuteSkinGPU</c> @0x482800, with the part-tree
/// walk replaced by a column walk. Buffer layout: <c>Db/DbPose.LAYOUT.md</c>.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Bone slots a palette addresses: the renderer's own stride, not a second copy of it.</summary>
    private const int BoneCount = AlloyRenderer.BoneCount;
    private static int _skinned, _skinNoBind, _skinNoSlot;
    private static bool _skinNoSlotSaid;

    /// <summary>Solve every skinned part of <paramref name="obj"/> into the renderer's bone stage and name the
    /// slot in <see cref="SceneDb.PartBoneBase"/>. Called at the end of the object's step, after the pose and
    /// the compose, because a palette is composed world matrices read back.</summary>
    private static void SkinObject(DbId obj)
    {
        int i = obj.Index;
        // Same tree fact: a rigid object has no palette to solve, and finding that out used to cost a walk of
        // its whole part range on every step it moved.
        if (!_hasSkin[i]) return;
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            ModelDef? m = SceneDb.PartModel[p];
            if (m == null) continue;
            int node = SceneDb.PartNode[p];
            if ((uint)node >= (uint)m.Nodes.Length) continue;
            // The STITCHED table when the boundary heal gave this part one: its bone names are the
            // composition's and its weights were unified with its neighbour's, so a palette solved against
            // the model's own table would address the wrong slots.
            SkinDef? skin = StitchSkin(p) ?? m.Nodes[node].Skin;
            if (skin == null) continue;
            if (!SceneDb.PartEnabled[p] || SceneDb.PartMerged[p]) { Unskin(p); continue; }
            int slot = SceneDb.PartBoneBase[p];
            if (slot < 0)
            {
                slot = AlloyRenderer.DbAllocBone();
                if (slot < 0)
                {
                    _skinNoSlot++;
                    if (!_skinNoSlotSaid)
                    {
                        _skinNoSlotSaid = true;
                        _log?.Always("DbAnim: the renderer's bone stage refused a slot -- this skinmesh draws "
                                   + "RIGID (its bind pose). Counted on [dbanim] as skin-noslot.");
                    }
                    continue;
                }
            }
            if (!Palette(p, skin, AlloyRenderer.DbBoneStage(slot)))
            {
                AlloyRenderer.DbFreeBone(slot);
                SceneDb.PartBoneBase[p] = -1;
                _skinNoBind++;
                continue;
            }
            SceneDb.PartBoneBase[p] = slot;
            _skinned++;
            SceneDb.TouchPart(p, Dirty.Anim);
        }
    }

    /// <summary>Release a part's bone slot (disabled, merged away, or its object retiring), and with it the
    /// bone table solved for the tree it was seated in -- which a re-tenanted part index no longer has.</summary>
    private static void Unskin(int p)
    {
        _named.Bind[p] = null;
        int slot = SceneDb.PartBoneBase[p];
        if (slot < 0) return;
        AlloyRenderer.DbFreeBone(slot);
        SceneDb.PartBoneBase[p] = -1;
    }

    /// <summary>
    /// <c>ExecuteSkinGPU</c>'s loop over the bone map, over columns. For each slot the mesh addresses: take the
    /// bone node's part, express it in the SKINMESH's space, apply the slot's inverse bind pose, and write the
    /// 16 floats the vertex stage reads. An unused slot gets the identity rather than the previous part's
    /// matrices.
    /// </summary>
    private static bool Palette(int part, SkinDef skin, float* dst)
    {
        int[] bones = skin.BoneNodes;
        // A compiled binary states a BONE MAP in BoneNodes -- one entry per model node, holding that node's slot
        // or -1 -- where the ASCII form states slot -> node outright (SkinDef says so). Its bind arrays are per
        // NODE, not per slot: ExecuteSkinGPU @0x582800 maps slot -> part through MdlNodeSkin+0x320 and indexes
        // qbone (+0x2f0) and tbone (+0x300) with the PART. Read by slot, a custom cloak took the root's +1.6 m.
        bool isMap = skin.BoneNodesAreMap;
        bool derived = skin.BindRotation.Length == 0;
        int slots = isMap ? SlotCount(bones) : bones.Length;
        // A file that carries no inverse bind pose at all -- every ASCII skin, and a compiled binary with
        // empty qbone/tbone arrays -- is what the engine derives one for in PartSkin::PostProcess, against
        // the instantiated part tree it has just Gob::Reset to its REST pose. Derive the same thing from the
        // model's rest pose. The slot count then comes from BoneNodes, read in the form its own reader states
        // (SkinDef.BoneNodesAreMap): a MAP addresses its largest entry plus one, a slot LIST addresses one
        // slot per entry. Taking max+1 of the ASCII slot list instead read a node index as a slot count, the
        // length test below then called the list a map, and MapNode handed every bone the wrong node -- a
        // cloak deforming by another bone's matrix, which is the stretching.
        if (bones.Length == 0 || slots == 0) return false;
        // An ASCII file states bone NAMES, and a name is the only thing that survives a graft: the piece's own
        // Find answered -1 for every bone that lives in the skeleton it hangs off, and those slots took the
        // identity -- the piece stayed in its bind pose while the client posed it.
        if (!isMap && skin.BoneNames.Length > 0) return NamedPalette(part, skin, slots, dst);
        if (!derived && skin.BindTranslation.Length < skin.BindRotation.Length) return false;
        DbId obj = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(obj)) return false;
        ModelDef? model = SceneDb.PartModel[part];
        if (model == null) return false;
        DbId root = isMap ? TreeRoot(obj) : obj;
        int first = SceneDb.ObjPartFirst[root.Index], n = SceneDb.ObjPartCount[root.Index];
        ModelDef? boneModel = !isMap ? model : n > 0 ? SceneDb.PartModel[first] : null;
        if (boneModel == null) return false;

        // RenderSkinned's first act: the inverse of the skinmesh's own world transform, which is what puts the
        // gathered bones in the mesh's space. Composed, so it already carries the object's placement -- and
        // that cancels on both sides, exactly as the engine's root-relative chain did.
        Matrix4x4 meshW = SceneDb.PartWorld[part];
        if (!Matrix4x4.Invert(meshW, out Matrix4x4 meshInv)) return false;

        Matrix4x4[]? rest = derived
                          ? DerivedBind(model, SceneDb.PartNode[part], skin, bones, slots, isMap) : null;
        if (derived && rest == null) return false;

        int limit = Math.Min(slots, BoneCount);
        for (int b = 0; b < BoneCount; b++)
        {
            float* m = ((Stride16f*)dst)[b].F;
            if (b >= limit) { Identity(m); continue; }
            int node = isMap ? MapNode(bones, b) : (b < bones.Length ? bones[b] : -1);
            int bp = node < 0 ? -1 : FindPart(first, n, boneModel, node);
            if (bp < 0 || (rest == null && (uint)node >= (uint)skin.BindRotation.Length)) { Identity(m); continue; }
            // bone space -> node space -> mesh space.
            Matrix4x4 bind = rest != null
                           ? rest[b]
                           : Matrix4x4.CreateFromQuaternion(skin.BindRotation[node])
                             * Matrix4x4.CreateTranslation(skin.BindTranslation[node]);
            Matrix4x4 w = bind * SceneDb.PartWorld[bp] * meshInv;
            Store(w, m);
        }
        return true;
    }

    /// <summary>The record whose model a compiled bone map indexes. <c>RenderSkinned</c> climbs Part+0x48 to
    /// the TOPMOST part of the one part tree and <c>GatherBones</c> walks that root's own model, so a slot
    /// names a node of the body skeleton for anything <c>Gob::Append</c> grafted
    /// (<see cref="SeamFlag.Composed"/>) and of the record itself for a separately attached gob.</summary>
    private static DbId TreeRoot(DbId obj)
    {
        DbId root = obj;
        for (int hop = 0; hop < 8 && (SceneDb.ObjSeamFlags[root.Index] & (uint)SeamFlag.Composed) != 0
                          && SceneDb.Objects.Valid(SceneDb.ObjAttachTo[root.Index]); hop++)
            root = SceneDb.ObjAttachTo[root.Index];
        return root;
    }

    /// <summary>Re-skin the skinned pieces grafted onto <paramref name="obj"/>: their bones are ITS parts (see
    /// <see cref="TreeRoot"/>), so their palettes are owed whenever its pose moves -- and a piece that only rides
    /// is never <c>Moved</c> itself, which froze TDN's feet onto one bone. Composed first, so the mesh's own world
    /// the palette inverts is this frame's.</summary>
    private static void SkinPieces(DbId obj, int depth)
    {
        if (depth > 3) return;
        for (DbId c = SceneDb.ObjSeamAppendHead[obj.Index]; !c.IsNull && SceneDb.Objects.Valid(c);
             c = SceneDb.ObjSeamAppendNext[c.Index])
        {
            if (_hasSkin[c.Index]) { ComposeObject(c); SkinObject(c); }
            SkinPieces(c, depth + 1);
        }
    }

    /// <summary>How many slots a bone MAP addresses: its largest entry plus one. Meaningless for the ASCII
    /// slot LIST, whose entries are node indices -- <see cref="SkinDef.BoneNodesAreMap"/> is the gate.</summary>
    private static int SlotCount(int[] map)
    {
        int max = -1;
        for (int i = 0; i < map.Length; i++) if (map[i] > max) max = map[i];
        return max + 1;
    }

    /// <summary>
    /// The inverse bind pose a compiled binary does not carry, derived once per <see cref="SkinDef"/> from the
    /// model's REST pose: the matrix that takes a bone's rest world back into the skinmesh's rest space, so a
    /// creature in its rest pose gets the identity for every slot and deforms from there.
    /// </summary>
    private static Matrix4x4[]? DerivedBind(ModelDef model, int meshNode, SkinDef skin, int[] map, int slots,
                                            bool isMap)
    {
        if (_derivedBind.TryGetValue(skin, out Matrix4x4[]? have)) return have;
        if ((uint)meshNode >= (uint)model.Nodes.Length) return null;
        var r = new Matrix4x4[slots];
        Matrix4x4 meshRest = RestWorld(model, meshNode);
        for (int b = 0; b < slots; b++)
        {
            int node = isMap ? MapNode(map, b) : (b < map.Length ? map[b] : -1);
            if ((uint)node >= (uint)model.Nodes.Length
                || !Matrix4x4.Invert(RestWorld(model, node), out Matrix4x4 inv))
            {
                r[b] = Matrix4x4.Identity;
                continue;
            }
            r[b] = meshRest * inv;
        }
        _derivedBind.Add(skin, r);
        return r;
    }

    private static readonly ConditionalWeakTable<SkinDef, Matrix4x4[]> _derivedBind = new();

    /// <summary>A node's REST world inside its model: its own local transform, then every parent's, in the
    /// order <c>DbXform</c> composes a live part in.</summary>
    private static Matrix4x4 RestWorld(ModelDef model, int node)
    {
        Matrix4x4 w = Matrix4x4.Identity;
        for (int at = node, hop = 0; at >= 0 && hop < 64; at = model.Nodes[at].Parent, hop++)
        {
            NodeDef nd = model.Nodes[at];
            w *= Matrix4x4.CreateScale(nd.Scale) * Matrix4x4.CreateFromQuaternion(nd.Orientation)
               * Matrix4x4.CreateTranslation(nd.Position);
        }
        return w;
    }

    /// <summary>The model node whose bone-map entry is slot <paramref name="b"/>, or -1. Linear over the
    /// map, which is the model's node count; a skinmesh addresses a few dozen slots and only on the frames its
    /// bones move.</summary>
    private static int MapNode(int[] map, int b)
    {
        for (int n = 0; n < map.Length; n++) if (map[n] == b) return n;
        return -1;
    }

    /// <summary>The part of this object instancing <paramref name="node"/> of <paramref name="model"/>. Linear
    /// over the range, which is the whole creature: a humanoid is a few dozen parts, and a map per object would
    /// be a table to invalidate on every tree change.</summary>
    private static int FindPart(int first, int n, ModelDef model, int node)
    {
        for (int k = 0; k < n; k++)
        {
            int p = first + k;
            if (SceneDb.PartNode[p] == node && ReferenceEquals(SceneDb.PartModel[p], model)) return p;
        }
        return -1;
    }

    /// <summary>The sixteen floats that land in <c>GLRender::m_bones</c>: a COLUMN-major affine, which is what
    /// GLSL reads a 64-byte run as. <c>Alloy/Scene/SkinPose.cs</c>'s <c>Compose</c> has the pair of transposes
    /// this is derived from, and its self-check pins the layout -- the one thing here a sign slip would
    /// silently invert.</summary>
    private static void Store(in Matrix4x4 w, float* m)
    {
        m[0] = w.M11; m[1] = w.M12; m[2] = w.M13; m[3] = 0f;
        m[4] = w.M21; m[5] = w.M22; m[6] = w.M23; m[7] = 0f;
        m[8] = w.M31; m[9] = w.M32; m[10] = w.M33; m[11] = 0f;
        m[12] = w.M41; m[13] = w.M42; m[14] = w.M43; m[15] = 1f;
    }

    private static void Identity(float* m)
    {
        for (int i = 0; i < 16; i++) m[i] = 0f;
        m[0] = 1f; m[5] = 1f; m[10] = 1f; m[15] = 1f;
    }

    /// <summary>The API's body: one part's palette into a caller's buffer (<c>64 * 16</c> floats). Used by the
    /// self-checks and by any consumer that wants the matrices without the stage.</summary>
    private static bool SkinSolve(int part, float* dst)
    {
        if ((uint)part >= (uint)SceneDb.MaxParts || !SceneDb.Parts.LiveAt(part)) return false;
        ModelDef? m = SceneDb.PartModel[part];
        if (m == null) return false;
        int node = SceneDb.PartNode[part];
        if ((uint)node >= (uint)m.Nodes.Length) return false;
        SkinDef? skin = StitchSkin(part) ?? m.Nodes[node].Skin;
        return skin != null && Palette(part, skin, dst);
    }

    /// <summary>The skin layer's period numbers.</summary>
    internal static string SkinPeriod()
        => $"{_skinned} palette(s)"
         + SkinNamePeriod()
         + StitchPeriod()
         + (_skinNoBind == 0 ? "" : $", {_skinNoBind} REFUSED (no bind pose)")
         + (_skinNoSlot == 0 ? "" : $", {_skinNoSlot} skin-noslot");
}
