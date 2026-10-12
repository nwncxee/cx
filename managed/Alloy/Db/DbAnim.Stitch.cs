using System;
using System.Collections.Generic;
using System.Numerics;
using CX.Alloy.Mdl;

namespace CX.Alloy.Db;

/// <summary>Healing the boundary between a creature's SKINNED parts. Two parts authored to meet (a head and
/// a neck) hold their own copy of the shared ring, and nothing makes those copies deform or shade alike: a
/// difference in weights opens a GAP when the creature moves, a difference in normals draws a LINE where the
/// surfaces meet. At assembly the coincident vertices are found in the shared REST pose and made one.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>How far apart two vertices may sit in the rest pose and still be the same vertex: a tenth of
    /// a millimetre, which authored-to-meet rings are well inside and a deliberate fold is well outside.</summary>
    private const float StitchEps = 1e-4f;
    /// <summary>Skinned vertices of one creature the stitch will look at, and parts it will collect. Past
    /// either, the creature is left unstitched and counted -- never a partial stitch, which would heal one
    /// end of a ring and not the other.</summary>
    private const int MaxStitchVerts = 1 << 16, MaxStitchParts = 32;

    /// <summary>The stitch's own state: which creatures owe one, what each stitched part was given, and the
    /// numbers the period line and the one diagnostic reports.</summary>
    private sealed class StitchState
    {
        public readonly bool[] Due = new bool[SceneDb.MaxObjects];
        /// <summary>Per part, the skin the palette must use instead of its model's -- the stitched table,
        /// whose bone names are the composition's and whose weights were unified.</summary>
        public readonly SkinDef?[] Skin = new SkinDef?[SceneDb.MaxParts];
        /// <summary>Per stitched part, the private recipe and its hand-out generation (the arena range is the
        /// recipe's own, released with it).</summary>
        public readonly Dictionary<int, (int Recipe, uint Gen)> Held = new();
        public long Creatures, Vertices, Refused;
        public int Said;
    }

    private static readonly StitchState _stitch = new();

    /// <summary>The stitched skin of <paramref name="part"/>, or null: what the palette reads in place of the
    /// model's own table, because the stitch unified the weights and rebuilt the bone table by name.</summary>
    private static SkinDef? StitchSkin(int part)
        => (uint)part < (uint)SceneDb.MaxParts ? _stitch.Skin[part] : null;

    /// <summary>This composition's parts changed, so its boundaries are owed a new stitch. Marked on the TREE
    /// ROOT, because the boundary is between parts of the whole creature, not of one appended piece.</summary>
    private static void StitchDue(DbId obj)
    {
        if (SceneDb.Objects.Valid(obj)) _stitch.Due[TreeRoot(obj).Index] = true;
    }

    /// <summary>Run the stitch if this creature owes one. From the step's tail, so every piece the frame
    /// grafted is seated -- and never per frame: a clean creature costs one bool read.</summary>
    private static void StitchIfDue(DbId obj)
    {
        int i = obj.Index;
        if (!_stitch.Due[i]) return;
        _stitch.Due[i] = false;
        Stitch(obj);
    }

    /// <summary>Give back everything the stitch gave <paramref name="obj"/>'s own part range: the private
    /// recipes, their arena ranges and the stitched skins. The part records are about to be re-tenanted.</summary>
    private static void StitchForget(DbId obj)
    {
        if (obj.IsNull || !SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        for (int k = 0; k < n; k++) StitchDrop(first + k);
    }

    private static void StitchDrop(int part)
    {
        if ((uint)part >= (uint)SceneDb.MaxParts) return;
        _stitch.Skin[part] = null;
        if (!_stitch.Held.Remove(part, out (int Recipe, uint Gen) had)) return;
        if (SceneDb.PartRecipe[part] == had.Recipe) DrawRecipe.Assign(part, -1);
        DrawRecipe.RetireDynamic(had.Recipe, had.Gen);
    }

    /// <summary>One creature's boundaries, start to finish: collect its skinned parts, put every vertex in the
    /// shared rest pose, group the coincident ones, unify each group, and publish the parts that changed.</summary>
    private static void Stitch(DbId root)
    {
        if (!SceneDb.Objects.Valid(root)) return;
        var parts = new List<int>();
        CollectSkinned(root, parts, 0);
        for (int k = 0; k < parts.Count; k++) StitchDrop(parts[k]);
        if (parts.Count < 2) return;
        if (parts.Count > MaxStitchParts) { StitchRefuse($"more than {MaxStitchParts} skinned parts"); return; }

        var w = new Weave(parts.Count);
        for (int k = 0; k < parts.Count; k++)
        {
            // A part whose rest pose or bone table cannot be read is left out of the weave, not refused for
            // the whole creature: its own boundary is then drawn as authored and the others still heal.
            if (!w.Add(k, parts[k]))
                StitchRefuse($"part {parts[k]} (\"{PartTree.NameOf(parts[k])}\") cannot be read in the rest pose");
            if (w.Count <= MaxStitchVerts) continue;
            StitchRefuse($"more than {MaxStitchVerts} skinned vertices");
            return;
        }
        w.Group();
        w.Heal();
        if (w.Healed == 0) return;
        for (int k = 0; k < parts.Count; k++) w.Publish(k, parts[k]);
        _stitch.Creatures++;
        _stitch.Vertices += w.Healed;
        StitchSay(root, w);
    }

    /// <summary>The creature's skinned parts, its own and every piece grafted onto it. A merged or disabled
    /// part has no boundary to heal.</summary>
    private static void CollectSkinned(DbId obj, List<int> into, int depth)
    {
        if (depth > 3 || !SceneDb.Objects.Valid(obj)) return;
        int i = obj.Index;
        int first = SceneDb.ObjPartFirst[i], n = SceneDb.ObjPartCount[i];
        for (int k = 0; k < n && into.Count <= MaxStitchParts; k++)
        {
            int p = first + k;
            if (!SceneDb.Parts.LiveAt(p) || !SceneDb.PartEnabled[p] || SceneDb.PartMerged[p]) continue;
            if (ModelSkin(p) == null || ModelMesh(p) == null) continue;
            into.Add(p);
        }
        for (DbId c = SceneDb.ObjSeamAppendHead[i]; !c.IsNull && SceneDb.Objects.Valid(c);
             c = SceneDb.ObjSeamAppendNext[c.Index])
            CollectSkinned(c, into, depth + 1);
    }

    /// <summary>The part's own model's skin, never the stitched one: a re-stitch starts from what the file
    /// authored, so it is idempotent rather than cumulative.</summary>
    private static SkinDef? ModelSkin(int part)
    {
        ModelDef? m = SceneDb.PartModel[part];
        int node = SceneDb.PartNode[part];
        return m != null && (uint)node < (uint)m.Nodes.Length ? m.Nodes[node].Skin : null;
    }

    private static MeshDef? ModelMesh(int part)
    {
        ModelDef? m = SceneDb.PartModel[part];
        int node = SceneDb.PartNode[part];
        return m != null && (uint)node < (uint)m.Nodes.Length ? m.Nodes[node].Mesh : null;
    }

    /// <summary>Slot -> (bone part, bone NAME) for either form of a bone table, so the stitch can speak of one
    /// part's influences in another part's table. The name is the resolved part's node name and is checked to
    /// resolve back to that same part, since a name is what the stitched table carries.</summary>
    private static bool SlotBones(int part, SkinDef skin, out int[] bone, out string[] names)
    {
        bone = Array.Empty<int>();
        names = Array.Empty<string>();
        DbId obj = SceneDb.PartObj[part];
        if (!SceneDb.Objects.Valid(obj)) return false;
        bool isMap = skin.BoneNodesAreMap;
        int slots = isMap ? SlotCount(skin.BoneNodes) : skin.BoneNodes.Length;
        if (slots <= 0 || slots > BoneCount) return false;
        bone = new int[slots];
        names = new string[slots];
        DbId root = TreeRoot(obj);
        if (!isMap && skin.BoneNames.Length > 0)
        {
            SkinBindState? st = NamedBind(part, skin, slots);
            if (st == null) return false;
            for (int b = 0; b < slots; b++)
            {
                bone[b] = st.Bone[b];
                names[b] = b < skin.BoneNames.Length ? skin.BoneNames[b] : string.Empty;
            }
            return true;
        }
        DbId boneRoot = isMap ? root : obj;
        int first = SceneDb.ObjPartFirst[boneRoot.Index], n = SceneDb.ObjPartCount[boneRoot.Index];
        ModelDef? boneModel = isMap ? (n > 0 ? SceneDb.PartModel[first] : null) : SceneDb.PartModel[part];
        if (boneModel == null) return false;
        for (int b = 0; b < slots; b++)
        {
            int node = isMap ? MapNode(skin.BoneNodes, b) : skin.BoneNodes[b];
            int bp = node < 0 ? -1 : FindPart(first, n, boneModel, node);
            string nm = bp >= 0 ? PartTree.NameOf(bp) : string.Empty;
            // The stitched table addresses bones by name, so a name that resolves to some OTHER part (two
            // nodes of one rig sharing a name) would re-point the deformation. Leave that slot unnamed; the
            // vertices on it are then not stitched rather than stitched wrongly.
            if (bp >= 0 && (nm.Length == 0 || ResolveBone(root, obj, nm) != bp)) { bp = -1; nm = string.Empty; }
            bone[b] = bp;
            names[b] = nm;
        }
        return true;
    }

    /// <summary>A stitch the creature did not get: said for the first few with its reason, counted always --
    /// the parts then draw exactly what their files say, which is the seam this feature exists to close.</summary>
    private static void StitchRefuse(string why)
    {
        _stitch.Refused++;
        if (_stitch.Said >= MaxStitchSaid) return;
        _stitch.Said++;
        _log?.Info($"SkinStitch: REFUSED -- {why}. Those part boundaries are drawn as authored. Counted on "
                 + "[dbanim] as stitch-refused.");
    }

    private const int MaxStitchSaid = 8;

    /// <summary>The one line the diagnosis needs: how many boundary vertices a creature has, how many of them
    /// deform differently (a GAP when posed) and how many shade differently (a SHADING LINE). Said for the
    /// first few creatures of a session, which is what tells the two symptoms apart in a cx.log.</summary>
    private static void StitchSay(DbId root, Weave w)
    {
        if (_stitch.Said >= MaxStitchSaid) return;
        _stitch.Said++;
        _log?.Info($"SkinStitch: \"{SceneDb.ObjModel[root.Index]?.Name ?? "-"}\" (o={root.Index}): {w.Healed} "
                 + $"boundary vertices paired across {w.Pairs} part pair(s) -- {w.Gaps} had DIFFERENT skin "
                 + $"weights (a GAP when posed), {w.Lines} differed in normal by up to {w.MaxAngle:F1} deg (a "
                 + $"SHADING LINE), {w.Handed} disagreed on tangent handedness and kept their own frame. "
                 + "Healed in the rest pose.");
    }

    /// <summary>The stitch's clause on the skin period line.</summary>
    private static string StitchPeriod()
        => (_stitch.Creatures == 0 ? "" : $", stitched {_stitch.Creatures} creature(s)/{_stitch.Vertices} vertex(es)")
         + (_stitch.Refused == 0 ? "" : $", {_stitch.Refused} stitch-refused");
}
