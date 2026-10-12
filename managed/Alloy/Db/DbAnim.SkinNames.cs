using System;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>Skin bones addressed by NAME, which is every ASCII file. <c>PartSkin::PostProcess</c> @0x583120
/// climbs Part+0x48 to the topmost part, lists that tree (<c>MakeInlinePartList</c> @0x582690) once
/// <c>Gob::Reset</c> has it at REST, and resolves each weight's names against that list with
/// <c>FindPartIndexDFS</c> @0x582750: a graft's bones are the SKELETON's parts, bound to that rest pose.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>One part's bone table as the composition it sits in resolves it: slot -> part, slot -> inverse
    /// bind pose, and the three things that invalidate both -- the skin it was solved for, the anchor its
    /// object rides (the bind is composed along that chain) and the tree root it was resolved against.</summary>
    private sealed class SkinBindState
    {
        public SkinDef? Def;
        public int Anchor = -2, Root = -1;
        public int[] Bone = Array.Empty<int>();
        public Matrix4x4[] Bind = Array.Empty<Matrix4x4>();
    }

    /// <summary>Per part, its resolved named bone table. Released by <see cref="Unskin"/>, which every retire
    /// and tree change already runs, so a re-tenanted part index never reads the dead tenant's.</summary>
    private static readonly SkinBindState?[] _namedBind = new SkinBindState?[SceneDb.MaxParts];

    /// <summary>The named-bone layer's own counters.</summary>
    private sealed class NamedBoneState { public long Palettes, Lost; public int Said; }

    private static readonly NamedBoneState _named = new();

    /// <summary>The palette of a skin whose bone table is names: each slot's part comes from
    /// <see cref="ResolveBone"/> and its inverse bind from the assembled rest pose. Row-vector throughout --
    /// <c>bind = meshRest * inverse(boneRest)</c>, and the slot is <c>bind * boneWorld * inverse(meshWorld)</c>.</summary>
    private static bool NamedPalette(int part, SkinDef skin, int slots, float* dst)
    {
        if (!Matrix4x4.Invert(SceneDb.PartWorld[part], out Matrix4x4 meshInv)) return false;
        SkinBindState? st = NamedBind(part, skin, slots);
        if (st == null) return false;
        int limit = Math.Min(slots, BoneCount);
        for (int b = 0; b < BoneCount; b++)
        {
            float* m = dst + b * 16;
            int bp = b < limit ? st.Bone[b] : -1;
            // A name that resolved nowhere keeps the identity, which is what FindPartIndexDFS's -1 leaves the
            // engine's own slot.
            if (bp < 0 || !SceneDb.Parts.LiveAt(bp)) { Identity(m); continue; }
            Store(st.Bind[b] * SceneDb.PartWorld[bp] * meshInv, m);
        }
        _named.Palettes++;
        return true;
    }

    /// <summary>This part's named bone table, solved once and kept until the part, its skin or its anchor
    /// changes.</summary>
    private static SkinBindState? NamedBind(int part, SkinDef skin, int slots)
    {
        DbId obj = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(obj) || slots <= 0) return null;
        DbId root = TreeRoot(obj);
        int anchor = SceneDb.ObjAttachPart[obj.Index];
        SkinBindState? st = _namedBind[part];
        if (st != null && ReferenceEquals(st.Def, skin) && st.Bone.Length == slots
            && st.Anchor == anchor && st.Root == root.Index) return st;
        if (!RestWorldOfPart(part, out Matrix4x4 meshRest)) return null;
        st ??= new SkinBindState();
        if (st.Bone.Length != slots) { st.Bone = new int[slots]; st.Bind = new Matrix4x4[slots]; }
        st.Def = skin;
        st.Anchor = anchor;
        st.Root = root.Index;
        for (int b = 0; b < slots; b++)
        {
            string name = b < skin.BoneNames.Length ? skin.BoneNames[b] : string.Empty;
            int bp = ResolveBone(root, obj, name);
            Matrix4x4 bind = Matrix4x4.Identity;
            if (bp >= 0)
            {
                if (RestWorldOfPart(bp, out Matrix4x4 boneRest)
                    && Matrix4x4.Invert(boneRest, out Matrix4x4 inv)) bind = meshRest * inv;
                else bp = -1;
            }
            st.Bone[b] = bp;
            st.Bind[b] = bind;
            if (bp < 0 && name.Length != 0) NamedLost(part, name);
        }
        _namedBind[part] = st;
        return st;
    }

    /// <summary>The part a bone name addresses: the owning skeleton (<see cref="TreeRoot"/>) first, as
    /// <c>PartSkin::PostProcess</c> resolves it, then the piece's own tree, which is where a model that is
    /// nobody's graft carries its own bones. -1 when neither answers.</summary>
    private static int ResolveBone(DbId root, DbId own, string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        // FindPartIndexDFS @0x582750 answers index 0 for the name "root" whatever the tree holds -- the
        // topmost part of the composition, which is also what an unnamed weight pair defaults to.
        if (string.Equals(name, "root", StringComparison.OrdinalIgnoreCase)) return RootPart(root);
        int p = PartByName(root, name);
        if (p < 0 && root.Value != own.Value) p = PartByName(own, name);
        return p;
    }

    /// <summary>A part's REST world in the composition's own space: its node's local transform, every parent
    /// part's, then the anchor chain a grafted piece rides -- the same tree <see cref="TreeRoot"/> stops at.
    /// The node locals, never the pose columns: the engine derives its bind from a tree it has just
    /// <c>Gob::Reset</c>, so a creature caught mid-animation must bind the same way.</summary>
    private static bool RestWorldOfPart(int part, out Matrix4x4 world)
    {
        world = Matrix4x4.Identity;
        for (int p = part, hop = 0; hop < MaxRestHops; hop++)
        {
            if ((uint)p >= (uint)SceneDb.MaxParts || !SceneDb.Parts.LiveAt(p)) return false;
            ModelDef? m = SceneDb.PartModel[p];
            int node = SceneDb.PartNode[p];
            if (m == null || (uint)node >= (uint)m.Nodes.Length) return false;
            NodeDef nd = m.Nodes[node];
            world *= Matrix4x4.CreateScale(nd.Scale) * Matrix4x4.CreateFromQuaternion(nd.Orientation)
                   * Matrix4x4.CreateTranslation(nd.Position);
            int up = SceneDb.PartParent[p];
            if (up >= 0) { p = up; continue; }
            DbId obj = SceneDb.PartObj[p];
            if (!SceneDb.Objects.Valid(obj)
                || (SceneDb.ObjSeamFlags[obj.Index] & (uint)SeamFlag.Composed) == 0
                || !SceneDb.Objects.Valid(SceneDb.ObjAttachTo[obj.Index])) return true;
            int at = SceneDb.ObjAttachPart[obj.Index];
            if (at < 0) return true;
            p = at;
        }
        return false;
    }

    /// <summary>Hops the rest walk follows before it refuses. A bound on a part chain crossed by graft
    /// anchors: a humanoid's deepest bone is a dozen links, a composition a handful deep.</summary>
    private const int MaxRestHops = 256;

    /// <summary>A bone name no part of the composition carries: that slot deforms nothing, so the vertices
    /// weighted to it stay in their bind pose. Named for the first few, counted always -- this is a property
    /// of the content, which is why it is reported rather than refused.</summary>
    private static void NamedLost(int part, string name)
    {
        _named.Lost++;
        if (_named.Said >= MaxLostBoneSaid) return;
        _named.Said++;
        ModelDef? m = SceneDb.PartModel[part];
        _log?.Info($"DbAnim: skin bone \"{name}\" of \"{m?.Name ?? "-"}\" resolves to no part of the "
                 + "composition -- the vertices weighted to it stay in their BIND POSE. Counted on [dbanim] "
                 + "as skin-lostbone.");
    }

    private const int MaxLostBoneSaid = 8;

    /// <summary>The named-bone layer's half of the skin period line.</summary>
    private static string SkinNamePeriod()
        => (_named.Palettes == 0 ? "" : $", {_named.Palettes} named")
         + (_named.Lost == 0 ? "" : $", {_named.Lost} skin-lostbone");
}
