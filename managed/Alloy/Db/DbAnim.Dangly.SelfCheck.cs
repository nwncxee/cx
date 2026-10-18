using System;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Alloy.Physics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The dangly dispatch's own properties, over a hand-built rig and a synthetic part -- no engine, no
/// GL. Each is a failure that reads as something else entirely in a session: a motion chosen wrongly as "the
/// new solve does nothing", a bone range handed out twice as one cloak wearing another's pose, a transposed
/// bone as a part folded inside out, a prev palette left stale as a smear behind every settled leaf.</summary>
public static unsafe partial class DbAnim
{
    private static void DanglyMotions(Checker check)
    {
        DanglyChoiceTable(check);
        DanglyCells(check);
        DanglyPacking(check);
        DanglyBoneStore(check);
        DanglyPrevHandover(check);
        DanglySwitch(check);
        DanglyRecycle(check);
        DanglyWallGather(check);
    }

    // ================= the motion table =================

    /// <summary>Oracle: docs/spec/dangly-motions.md section 4, transcribed by hand -- <c>motion = class</c>,
    /// and a rigless node, a stood-down kind, stock motion or no skinning path runs the stock solve.</summary>
    private static void DanglyChoiceTable(Checker check)
    {
        check.Expect(ChooseDangly(DanglyClass.Sway, true, true, true, true) == DanglyPath.Rigged,
                     "a Sway node with its knob on, auto motion and skinning runs the joint solver");
        check.Expect(ChooseDangly(DanglyClass.Jiggle, true, true, true, true) == DanglyPath.Rigged,
                     "...and so does a Jiggle node");
        check.Expect(ChooseDangly(DanglyClass.Swing, true, true, true, true) == DanglyPath.Rigged,
                     "...and a Swing node");
        check.Expect(ChooseDangly(DanglyClass.Static, true, true, true, true) == DanglyPath.Rest,
                     "a Static node has no moving vertex: it is drawn at rest and costs nothing");
        check.Expect(ChooseDangly(DanglyClass.Static, true, false, true, true) == DanglyPath.Rest,
                     "...under stock motion too, which would settle it at rest anyway");
        check.Expect(ChooseDangly(DanglyClass.Sway, false, true, true, true) == DanglyPath.Vanilla,
                     "a node the rig builder declined runs the stock solve");
        check.Expect(ChooseDangly(DanglyClass.Static, false, true, true, true) == DanglyPath.Vanilla,
                     "...including one with no rig to call Static");
        check.Expect(ChooseDangly(DanglyClass.Swing, true, false, true, true) == DanglyPath.Vanilla,
                     "dangly.motion = Vanilla runs every rigged part the stock way");
        check.Expect(ChooseDangly(DanglyClass.Swing, true, true, false, true) == DanglyPath.Vanilla,
                     "a kind whose knob is off runs the stock way");
        check.Expect(ChooseDangly(DanglyClass.Jiggle, true, true, true, false) == DanglyPath.Vanilla,
                     "renderer.ownskin off leaves no palette, so a rigged part runs the stock way");
    }

    // ================= the bone-range packing =================

    /// <summary>Oracle: the packing rule stated by hand -- a rig takes the smallest power-of-two cell of at
    /// least two bones that holds it, and nothing past a 64-matrix palette has a cell at all.</summary>
    private static void DanglyCells(Checker check)
    {
        check.Expect(DanglyCellFor(1) == 2, "a one-bone rig takes the smallest cell, two bones");
        check.Expect(DanglyCellFor(2) == 2, "...and so does a two-bone rig");
        check.Expect(DanglyCellFor(3) == 4, "three bones round up to four");
        check.Expect(DanglyCellFor(5) == 8 && DanglyCellFor(8) == 8, "five through eight take eight");
        check.Expect(DanglyCellFor(17) == 32, "Swing's largest grid (1 + 4x4) takes thirty-two");
        check.Expect(DanglyCellFor(64) == 64, "a rig that fills a palette takes the whole slot");
        check.Expect(DanglyCellFor(65) == 0, "a rig bigger than a palette gets no cell: it is refused, not clipped");
        check.Expect(DanglyCellFor(0) == 0, "...and neither does a rig with no bones");
    }

    /// <summary>Oracle: hand-written offsets. Cells of one size are handed out from the front of a fresh
    /// 64-bone page, a freed cell comes back before a new one, and a second size class takes its own page.</summary>
    private static void DanglyPacking(Checker check)
    {
        const int cell = 2, parts = 4;
        var at = new int[parts];
        for (int k = 0; k < parts; k++) at[k] = DanglyBoneAlloc(SelfCheckPart + k, cell, 0f);
        check.Expect(at[0] >= 0 && at[0] % BoneCount == 0, "the first two-bone rig lands at the start of a page");
        check.Expect(at[1] == at[0] + 2 && at[2] == at[0] + 4 && at[3] == at[0] + 6,
                     "...and the next three follow it two bones apart, inside the same page");

        DanglyBoneFree(SelfCheckPart + 1);
        int again = DanglyBoneAlloc(SelfCheckPart + 1, cell, 0f);
        check.Expect(again == at[0] + 2, "a freed cell is the next one handed out, not a fresh one");

        int wide = DanglyBoneAlloc(SelfCheckPart + parts, 16, 0f);
        check.Expect(wide >= 0 && wide % BoneCount == 0 && wide / BoneCount != at[0] / BoneCount,
                     "a sixteen-bone rig takes a page of its own, aligned to it");
        check.Expect(DanglyBoneAlloc(SelfCheckPart + parts + 1, BoneCount + 1, 0f) == -1,
                     "a rig past a palette is REFUSED a range rather than given a short one");

        for (int k = 0; k <= parts; k++) DanglyBoneFree(SelfCheckPart + k);
        check.Expect(SceneDb.PartBoneBase[SelfCheckPart] == -1 && _danglyBones.Cell[SelfCheckPart] == 0,
                     "a freed part names no bone base and holds no cell");
    }

    /// <summary>Oracle: the sixteen floats one bone matrix becomes, written out by hand. A translation of
    /// (1, 2, 3) lands in elements 12..14 and nowhere else -- a transposed store would put it in 3, 7, 11 --
    /// and the projective column is normalised, so a stray w term cannot scale a vertex.</summary>
    private static void DanglyBoneStore(Checker check)
    {
        int at = DanglyBoneAlloc(SelfCheckPart, 2, 0f);
        if (at < 0) { check.Expect(false, "the bone stage hands out a two-bone cell for the store check"); return; }
        Span<Matrix4x4> bones = stackalloc Matrix4x4[2];
        bones[0] = Matrix4x4.Identity;
        // a quarter turn about +Z, then a lift: the one matrix whose store a sign slip or a transpose changes
        bones[1] = Matrix4x4.CreateFromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f));
        bones[1].M41 = 1f; bones[1].M42 = 2f; bones[1].M43 = 3f;
        // ...and a projective column the store has to flatten: it is the one thing the store does to an affine
        bones[1].M14 = 7f; bones[1].M24 = 7f; bones[1].M34 = 7f; bones[1].M44 = 7f;
        DanglyWriteBones(at, 2, bones);

        Span<float> f = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(
                            AlloyRenderer.DbBoneCell(at, 2));
        check.Expect(f.Length == 32, "the published cell is readable in the DB bone stage");
        if (f.Length != 32) { DanglyBoneFree(SelfCheckPart); return; }
        check.Expect(Same16(f, stackalloc float[] { 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 1f }),
                     "bone 0 is the identity, the part's own place");
        check.Expect(Same16(f[16..], stackalloc float[] { 0f, 1f, 0f, 0f, -1f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, 1f, 2f, 3f, 1f }),
                     "bone 1 is stored column-major: the quarter turn's columns, the translation in 12..14");
        DanglyBoneFree(SelfCheckPart);
    }

    private static bool Same16(ReadOnlySpan<float> f, ReadOnlySpan<float> want)
    {
        for (int z = 0; z < 16; z++) if (MathF.Abs(f[z] - want[z]) > 1e-5f) return false;
        return true;
    }

    /// <summary>Oracle: two hand-written translations, A then B. The published history is A on the first frame
    /// (no smear from whatever the cell held), A while it moves to B, and B once it settles -- prev equal to
    /// current, which is a zero motion vector.</summary>
    private static void DanglyPrevHandover(Checker check)
    {
        int at = DanglyBoneAlloc(SelfCheckPart, 2, 0f);
        if (at < 0) { check.Expect(false, "the bone stage hands out a two-bone cell for the history check"); return; }
        int slot = at / BoneCount, off = at % BoneCount;
        Span<Matrix4x4> bones = stackalloc Matrix4x4[2];
        bones[0] = Matrix4x4.Identity;

        bones[1] = Matrix4x4.CreateTranslation(1f, 0f, 0f);
        DanglyWriteBones(at, 2, bones);
        AlloyRenderer.DbBoneSeedPrev(at, 2);
        AlloyRenderer.DbBonePublish();
        check.Expect(StagedX(slot, off + 1) == 1f && PrevX(slot, off + 1) == 1f,
                     "the first published frame is its own history: a fresh cell does not smear");

        bones[1] = Matrix4x4.CreateTranslation(4f, 0f, 0f);
        DanglyWriteBones(at, 2, bones);
        AlloyRenderer.DbBonePublish();
        check.Expect(StagedX(slot, off + 1) == 4f && PrevX(slot, off + 1) == 1f,
                     "a moving part publishes this frame's pose against last frame's");

        AlloyRenderer.DbBonePublish();
        check.Expect(StagedX(slot, off + 1) == 4f && PrevX(slot, off + 1) == 4f,
                     "a settled part publishes nothing, so its history catches up and its motion vector is zero");
        DanglyBoneFree(SelfCheckPart);
    }

    /// <summary>Oracle: the cell count, by hand -- one cell taken is one live, and a record re-tenanted while it
    /// still holds its cell (what <c>DbFeed.InitPart</c> reports) hands it back: none live, base -1, and the
    /// next rig of that size is given that same absolute index again.</summary>
    private static void DanglyRecycle(Checker check)
    {
        int live = _danglyBones.Live;
        int at = DanglyBoneAlloc(SelfCheckPart, 2, 0f);
        if (at < 0) { check.Expect(false, "the bone stage hands out a two-bone cell for the recycle check"); return; }
        SceneDb.PartBoneBase[SelfCheckPart] = at;
        check.Expect(_danglyBones.Live == live + 1, "a taken cell counts as live");
        DanglyPartRecycled(SelfCheckPart);
        check.Expect(_danglyBones.Live == live && _danglyBones.Cell[SelfCheckPart] == 0
                     && SceneDb.PartBoneBase[SelfCheckPart] == -1,
                     "a re-tenanted record gives its cell back rather than leaking it to the new tenant");
        check.Expect(DanglyBoneAlloc(SelfCheckPart + 1, 2, 0f) == at, "...and the next two-bone rig is handed that cell");
        DanglyBoneFree(SelfCheckPart + 1);
    }

    private static float StagedX(int slot, int bone)
    {
        ref BonePalette p = ref AlloyRenderer._boneStage[slot];
        return p[bone].M41;
    }

    private static float PrevX(int slot, int bone)
    {
        ref BonePalette p = ref AlloyRenderer._bonePrevStage[slot];
        return p[bone].M41;
    }

    // ================= the live switch =================

    /// <summary>Oracle: the hand-stated invariant that a part holds the resources of ONE path -- a rigged part
    /// a bone range and no private vertex range, a stock one the reverse -- and neither after it is forgotten.
    /// A leak here is a cloak that stops moving some minutes into a session.</summary>
    private static void DanglySwitch(Checker check)
    {
        DbId obj = DanglySubject(out int part);
        if (obj.IsNull) { check.Expect(false, "the dangly fixture seats one part"); return; }
        string had = Dangly.Motion.V;
        bool wasSkinning = AlloyRenderer.SelfCheckSkinning(true);
        try
        {
            Dangly.Motion.Set(Dangly.MotionAuto);
            DanglyPart(part, 1f / 60f, windy: false);
            check.Expect(SceneDb.PartBoneBase[part] >= 0, "an auto-motion rigged part takes a bone range");
            check.Expect(DynRange(part) < 0, "...and no private vertex range");

            Dangly.Motion.Set(Dangly.MotionVanilla);
            DanglyPart(part, 1f / 60f, windy: false);
            check.Expect(SceneDb.PartBoneBase[part] == -1, "switched to stock motion, it gives the bone range back");
            check.Expect(DynRange(part) >= 0 && SceneDb.PartRecipe[part] >= 0,
                         "...and takes a private vertex range and recipe instead");

            Dangly.Motion.Set(Dangly.MotionAuto);
            DanglyPart(part, 1f / 60f, windy: false);
            check.Expect(SceneDb.PartBoneBase[part] >= 0 && DynRange(part) < 0,
                         "switched back, the private range and recipe go and the bone range returns");
        }
        finally
        {
            Dangly.Motion.Set(had);
            AlloyRenderer.SelfCheckSkinning(wasSkinning);
        }

        DanglyForget(obj);
        check.Expect(SceneDb.PartBoneBase[part] == -1 && DynRange(part) < 0 && _danglyBones.Cell[part] == 0,
                     "forgetting the object leaves neither a bone range nor a vertex range behind");
        DropDangly(obj, part);
    }

    /// <summary>A one-node model whose only node is a four-vertex dangly card with a hand-built rig: two
    /// bones, the moving half bound to bone 1. Small enough that every number in it is read in one look.</summary>
    private static ModelDef DanglyCard()
    {
        var mesh = new MeshDef
        {
            Positions = new[] { new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f),
                                new Vector3(-1f, 0f, 2f), new Vector3(1f, 0f, 2f) },
            Normals = new[] { -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY, -Vector3.UnitY },
            Indices = new uint[] { 0, 1, 2, 2, 1, 3 },
        };
        mesh.Uv[0] = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY, Vector2.One };
        var dangly = new DanglyDef
        {
            Constraints = new[] { 255f, 255f, 60f, 60f },
            Displacement = 0.25f, Tightness = 2f, Period = 0.5f,
        };
        var rig = new DanglyRig
        {
            Class = DanglyClass.Sway,
            Mobility = new[] { 0f, 0f, 1f - 60f / 255f, 1f - 60f / 255f },
            Omega = 8f, Zeta = 0.1f,
            JointRest = new[] { Vector3.Zero, new Vector3(0f, 0f, 2f) },
            JointParent = new[] { -1, 0 },
            JointCap = new[] { 0f, 0.25f },
            DragDiag = new[] { Vector3.One, Vector3.One },
            DragOff = new[] { Vector3.Zero, Vector3.Zero },
            BoneJoint = new[] { -1, 1 },
            Shear = true,
            Skin = new SkinDef
            {
                Weights = new[] { new Vector4(1f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f),
                                  new Vector4(0f, 1f, 0f, 0f), new Vector4(0f, 1f, 0f, 0f) },
                Bones = new uint[] { 0, 0, 1, 1 },
            },
        };
        var m = new ModelDef { Name = "cx_selfcheck_dangly", AnimScale = 1f, BMin = new Vector3(-1f), BMax = new Vector3(2f) };
        m.Nodes = new[]
        {
            new NodeDef { Name = "card", Index = 0, Parent = -1, Children = Array.Empty<int>(),
                          Kind = NodeKind.Dangly, Mesh = mesh, Dangly = dangly, Rig = rig },
        };
        m.NodeByName[m.Nodes[0].Name] = 0;
        return m;
    }

    /// <summary>One object holding one dangly part, with its shared SKINNED arena range already taken, which
    /// is what <see cref="PartTree.Write"/> does for a rigged node on the live path.</summary>
    private static DbId DanglySubject(out int part)
    {
        part = -1;
        ModelDef m = DanglyCard();
        NodeDef n = m.Nodes[0];
        DbId obj = SceneDb.Objects.Alloc();
        if (!SceneDb.Parts.AllocRange(1, out int first)) { SceneDb.Objects.Free(obj); return default; }
        int o = obj.Index;
        SceneDb.ObjModel[o] = m;
        SceneDb.ObjPos[o] = Vector3.Zero;
        SceneDb.ObjRot[o] = Quaternion.Identity;
        SceneDb.ObjScale[o] = 1f;
        SceneDb.ObjVisible[o] = true;
        SceneDb.ObjPartFirst[o] = first;
        SceneDb.ObjPartCount[o] = 1;
        part = first;
        SceneDb.PartObj[part] = obj;
        SceneDb.PartModel[part] = m;
        SceneDb.PartMeshDef[part] = n.Mesh;
        SceneDb.PartNode[part] = 0;
        SceneDb.PartParent[part] = -1;
        SceneDb.PartPos[part] = Vector3.Zero;
        SceneDb.PartRot[part] = Quaternion.Identity;
        SceneDb.PartScale[part] = 1f;
        SceneDb.PartEnabled[part] = true;
        SceneDb.PartMerged[part] = false;
        SceneDb.PartAlpha[part] = 1f;
        SceneDb.PartBoneBase[part] = -1;
        SceneDb.PartKindOf[part] = PartTree.Kind(NodeKind.Dangly);
        SceneDb.PartWorld[part] = Matrix4x4.Identity;
        SceneDb.PartMesh[part] = MeshArena.Acquire(n.Mesh!, n.Rig!.Skin);
        return obj;
    }

    private static void DropDangly(DbId obj, int part)
    {
        int range = SceneDb.PartMesh[part];
        if (range >= 0) MeshArena.Release(range);
        SceneDb.PartMesh[part] = -1;
        SceneDb.PartModel[part] = null;
        SceneDb.PartMeshDef[part] = null;
        DrawRecipe.Assign(part, -1);
        SceneDb.Parts.FreeRange(part, 1);
        SceneDb.Objects.Free(obj);
    }

    /// <summary>Part indices this suite writes: past the live population, so a scratch tenant cannot be
    /// mistaken for a seated one.</summary>
    private const int SelfCheckPart = SceneDb.MaxParts - 16;
}
