using System;
using System.Numerics;
using System.Runtime.InteropServices;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The skin half of <see cref="DbAnim"/>'s suite: the bone palettes, the two forms of the bone
/// table, what a GRAFTED piece's bones resolve against, and the part-boundary stitch. Reached from
/// <c>DbAnim.SelfCheck.cs</c>'s <c>SelfCheck</c>, which is the suite's one entry point.</summary>
public static unsafe partial class DbAnim
{
    // ================= 6: skin palettes =================

    /// <summary>A two-bone palette against hand-computed matrices. The subject is a skinmesh at the chain's
    /// leaf whose bones are <c>root</c> and <c>mid</c>, with an identity bind pose: bone 0's matrix must then
    /// be "root expressed in the mesh's space", which for this chain is a translation of -2 along Z.</summary>
    private static void Skin(Checker check)
    {
        ModelDef m = Chain();
        m.Nodes[2].Kind = NodeKind.Skin;
        m.Nodes[2].Mesh = new MeshDef { Positions = new[] { Vector3.Zero } };
        m.Nodes[2].Skin = new SkinDef
        {
            BoneNodes = new[] { 0, 1 },
            BindRotation = new[] { Quaternion.Identity, Quaternion.Identity },
            BindTranslation = new[] { Vector3.Zero, Vector3.Zero },
            Weights = new[] { new Vector4(1f, 0f, 0f, 0f) },
            Bones = new[] { 0u },
        };
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(m, scene, out int first);
        Moved(obj.Index);
        ComposeObject(obj);

        float* dst = (float*)NativeMemory.Alloc((nuint)(BoneCount * 16) * sizeof(float));
        try
        {
            check.Expect(SkinPalette(first + 2, dst), "a skinmesh solves a palette");
            // bone 0 is `root` at the object's origin; the mesh is at (0,0,2), so root in mesh space is
            // (0,0,-2). A transposed store would put -2 in m[11] and leave m[14] at 0.
            check.Expect(Math.Abs(dst[12]) < 1e-4f && Math.Abs(dst[13]) < 1e-4f
                         && Math.Abs(dst[14] + 2f) < 1e-4f,
                         "bone 0 is its node expressed in the SKINMESH's space, translation in 12..14");
            check.Expect(Math.Abs(dst[0] - 1f) < 1e-4f && Math.Abs(dst[5] - 1f) < 1e-4f
                         && Math.Abs(dst[10] - 1f) < 1e-4f && dst[15] == 1f,
                         "...with an identity rotation for an unrotated chain");
            // bone 1 is `mid`, one link closer: (0,0,-1).
            check.Expect(Math.Abs(dst[16 + 14] + 1f) < 1e-4f, "bone 1 is one link closer to the mesh");
            // An unused slot is the identity, not the previous part's matrices.
            check.Expect(dst[2 * 16] == 1f && dst[2 * 16 + 15] == 1f, "an unused bone slot is the identity");

            // ...and the same thing under a ROTATED chain, against an oracle taken from the columns rather
            // than hand-worked: with an identity bind pose, a bone's matrix must carry its node's origin to
            // where that origin sits in the SKINMESH's space. This is the case a transposed or wrongly
            // associated product passes every identity test and fails.
            SceneDb.PartRot[first + 1] = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
            SceneDb.PartPos[first + 1] = new Vector3(1f, 0f, 1f);
            Moved(obj.Index);
            ComposeObject(obj);
            check.Expect(SkinPalette(first + 2, dst), "a rotated chain still solves");
            Matrix4x4.Invert(SceneDb.PartWorld[first + 2], out Matrix4x4 inv);
            for (int b = 0; b < 2; b++)
            {
                int bp = first + b;
                Vector3 want = Vector3.Transform(
                    new Vector3(SceneDb.PartWorld[bp].M41, SceneDb.PartWorld[bp].M42, SceneDb.PartWorld[bp].M43), inv);
                var bm = new Matrix4x4(dst[b * 16], dst[b * 16 + 1], dst[b * 16 + 2], dst[b * 16 + 3],
                                       dst[b * 16 + 4], dst[b * 16 + 5], dst[b * 16 + 6], dst[b * 16 + 7],
                                       dst[b * 16 + 8], dst[b * 16 + 9], dst[b * 16 + 10], dst[b * 16 + 11],
                                       dst[b * 16 + 12], dst[b * 16 + 13], dst[b * 16 + 14], dst[b * 16 + 15]);
                check.Expect(NearV(Vector3.Transform(Vector3.Zero, bm), want),
                             $"bone {b} carries its node's origin to the skinmesh's space under a rotated chain");
            }
        }
        finally { NativeMemory.Free(dst); }

        Drop(obj);
        SceneDb.Scenes.Free(scene);
        BoneTableForm(check);
        BoneMapOfRoot(check);
        GraftedBoneNames(check);
        HostRetree(check);
        BoundaryStitch(check);
    }

    /// <summary>A graft's bone table holds PART indices into the SKELETON's range, so a host re-tree -- which
    /// re-tenants exactly those indices and never touches the graft's own parts -- must not leave the table
    /// standing. Oracle: the world columns recorded at rest after the re-tree, against the part the name
    /// resolves to NOW and against the one it used to.</summary>
    private static void HostRetree(Checker check)
    {
        ModelDef? pm = MdlParser.Parse(System.Text.Encoding.ASCII.GetBytes(GraftSkinModel),
                                       "cx_selfcheck_skinned", out _);
        if (pm == null || pm.Nodes.Length != 3) { check.Expect(false, "the ASCII skin fixture parses"); return; }
        DbId scene = SceneDb.Scenes.Alloc();
        ModelDef bm = Chain();
        DbId body = Subject(bm, scene, out int bodyFirst);
        DbId piece = Subject(pm, scene, out int first);
        int pi = piece.Index, mesh = first + 2;
        SceneDb.PartParent[mesh] = first;
        ScanTree(pi);
        SceneDb.ObjSeamFlags[pi] |= (uint)SeamFlag.Composed;
        SceneDb.ObjSeamAppendHead[body.Index] = piece;
        SceneDb.ObjSeamAppendNext[pi] = default;
        SceneDb.SetAttachTo(pi, body);
        SceneDb.SetAttachPart(pi, bodyFirst);
        Moved(body.Index); ComposeObject(body);
        Moved(pi); ComposeObject(piece);

        float* dst = (float*)NativeMemory.Alloc((nuint)(BoneCount * 16) * sizeof(float));
        try
        {
            check.Expect(SkinPalette(mesh, dst), "the graft solves a palette against the first tree");
            // The re-tree: the same part range, each part now instancing the OTHER node, which is what a
            // rebuild of the host's tree does to the indices the graft's table holds.
            SceneDb.PartNode[bodyFirst + 1] = 2;
            SceneDb.PartNode[bodyFirst + 2] = 1;
            SceneDb.PartPos[bodyFirst + 1] = bm.Nodes[2].Position;
            SceneDb.PartPos[bodyFirst + 2] = bm.Nodes[1].Position;
            TreeChanged(body);
            ComposeObject(body);
            Moved(pi); ComposeObject(piece);
            int now = bodyFirst + 2, before = bodyFirst + 1;
            Matrix4x4 meshRest = SceneDb.PartWorld[mesh];
            Matrix4x4 nowRest = SceneDb.PartWorld[now], beforeRest = SceneDb.PartWorld[before];
            SceneDb.PartPos[now] = new Vector3(1f, 2f, 1f);
            Moved(body.Index); ComposeObject(body);
            Moved(pi); ComposeObject(piece);
            check.Expect(SkinPalette(mesh, dst), "...and again after the host re-trees");
            check.Expect(NearV(SlotPos(dst, 0), WantSlot(meshRest, nowRest, mesh, now)),
                         $"a host re-tree re-resolves the graft's bone table: slot 0 is the part \"mid\" "
                         + $"names NOW ({SlotPos(dst, 0)}, want {WantSlot(meshRest, nowRest, mesh, now)})");
            check.Expect(!NearV(SlotPos(dst, 0), WantSlot(meshRest, beforeRest, mesh, before)),
                         "...and not the part index the table was solved against");
        }
        finally { NativeMemory.Free(dst); }

        Unskin(mesh);
        SceneDb.ObjSeamAppendHead[body.Index] = default;
        SceneDb.ObjSeamFlags[pi] &= ~(uint)SeamFlag.Composed;
        SceneDb.SetAttachTo(pi, default);
        Drop(piece);
        Drop(body);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>An ASCII piece whose <c>weights</c> block names one bone of the SKELETON it is grafted onto
    /// ("mid"), one of its own file ("gmid") and one nothing carries ("nobone"). Three nodes in the shape
    /// <see cref="Subject"/> seats, with the skinmesh a SIBLING of the local bone so moving that bone does not
    /// carry the mesh with it.</summary>
    private const string GraftSkinModel = """
        newmodel cx_selfcheck_skinned
        beginmodelgeom cx_selfcheck_skinned
          node dummy groot
            parent NULL
          endnode
          node dummy gmid
            parent groot
            position 0.0 0.0 1.0
          endnode
          node skin gskin
            parent groot
            position 0.0 1.0 0.0
            verts 3
              0.0 0.0 0.0
              1.0 0.0 0.0
              0.0 1.0 0.0
            tverts 3
              0.0 0.0 0.0
              1.0 0.0 0.0
              0.0 1.0 0.0
            faces 1
              0 1 2 1 0 1 2 1
            weights 3
              mid 1.0
              gmid 1.0
              nobone 1.0
          endnode
        endmodelgeom
        donemodel cx_selfcheck_skinned
        """;

    /// <summary>A grafted ASCII piece resolves its bone NAMES against the skeleton it hangs off, then its own
    /// tree, leaves a name nothing carries at identity, and binds against the ASSEMBLED rest pose, so a
    /// re-anchored piece is still at rest. Oracles: <see cref="GraftSkinModel"/>'s authored text for the
    /// names, and the world columns RECORDED AT REST for the palettes (<see cref="WantSlot"/>).</summary>
    private static void GraftedBoneNames(Checker check)
    {
        ModelDef? pm = MdlParser.Parse(System.Text.Encoding.ASCII.GetBytes(GraftSkinModel),
                                       "cx_selfcheck_skinned", out string? err);
        check.Expect(pm != null, $"the ASCII skin fixture parses ({err ?? "no error"})");
        if (pm == null || pm.Nodes.Length != 3) { check.Expect(false, "...into three nodes"); return; }
        SkinDef? sk = pm.Nodes[2].Skin;
        check.Expect(sk != null && sk.BoneNames.Length == 3,
                     $"...keeping the three authored bone names (got {sk?.BoneNames.Length ?? -1})");
        if (sk == null || sk.BoneNames.Length != 3) return;
        check.Expect(string.Equals(sk.BoneNames[0], "mid", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(sk.BoneNames[1], "gmid", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(sk.BoneNames[2], "nobone", StringComparison.OrdinalIgnoreCase),
                     "...in the weights block's first-seen order");
        check.Expect(sk.BoneNodes[0] < 0 && sk.BoneNodes[1] == 1,
                     "...and only the LOCAL name resolves to a node of the piece's own file");

        DbId scene = SceneDb.Scenes.Alloc();
        DbId body = Subject(Chain(), scene, out int bodyFirst);
        DbId piece = Subject(pm, scene, out int first);
        int pi = piece.Index, mesh = first + 2, localBone = first + 1, extBone = bodyFirst + 1;
        // the skinmesh is the local bone's SIBLING, as the fixture's node tree says
        SceneDb.PartParent[mesh] = first;
        // ...and so is the body's leaf, so the external bone this piece names ("mid") is NOT an ancestor of
        // the anchor: a bone that carries the whole piece with it moves the mesh too, and every palette is
        // then the identity whether the name resolved or not.
        SceneDb.PartParent[bodyFirst + 2] = bodyFirst;
        ScanTree(pi);
        SceneDb.ObjSeamFlags[pi] |= (uint)SeamFlag.Composed;
        SceneDb.ObjSeamAppendHead[body.Index] = piece;
        SceneDb.ObjSeamAppendNext[pi] = default;

        float* dst = (float*)NativeMemory.Alloc((nuint)(BoneCount * 16) * sizeof(float));
        try
        {
            for (int pass = 0; pass < 2; pass++)
            {
                // pass 0 anchors the piece on the body's LEAF, pass 1 on its MID: the chain the bind is
                // composed along differs, and a bind cached from the first anchor is wrong for the second.
                int anchor = pass == 0 ? bodyFirst + 2 : bodyFirst;
                for (int k = 0; k < 3; k++) SceneDb.PartPos[bodyFirst + k] = SceneDb.PartModel[bodyFirst + k]!.Nodes[k].Position;
                for (int k = 0; k < 3; k++) SceneDb.PartPos[first + k] = pm.Nodes[k].Position;
                SceneDb.SetAttachTo(pi, body);
                SceneDb.SetAttachPart(pi, anchor);
                Moved(body.Index); ComposeObject(body);
                Moved(pi); ComposeObject(piece);
                string at = pass == 0 ? "the body's leaf" : "the body's root";

                Matrix4x4 meshRest = SceneDb.PartWorld[mesh];
                Matrix4x4 extRest = SceneDb.PartWorld[extBone], localRest = SceneDb.PartWorld[localBone];
                check.Expect(SkinPalette(mesh, dst), $"a grafted ASCII piece solves a palette on {at}");
                for (int b = 0; b < 3; b++)
                    check.Expect(Math.Abs(dst[b * 16 + 12]) < 1e-4f && Math.Abs(dst[b * 16 + 13]) < 1e-4f
                                 && Math.Abs(dst[b * 16 + 14]) < 1e-4f && Math.Abs(dst[b * 16] - 1f) < 1e-4f,
                                 $"...and at REST every slot is the identity (slot {b}, on {at})");

                // the EXTERNAL bone moves: the body's own "mid", which no node of the piece's file carries
                SceneDb.PartPos[extBone] = new Vector3(1f, 2f, 1f);
                Moved(body.Index); ComposeObject(body);
                Moved(pi); ComposeObject(piece);
                check.Expect(SkinPalette(mesh, dst), $"...and again once the external bone moves (on {at})");
                check.Expect(NearV(SlotPos(dst, 0), WantSlot(meshRest, extRest, mesh, extBone)),
                             $"slot 0 is the SKELETON's bone carried into the mesh's space, not the identity a "
                             + $"lost name leaves (on {at}): got {SlotPos(dst, 0)}, want {WantSlot(meshRest, extRest, mesh, extBone)}");
                check.Expect(SlotPos(dst, 0).Length() > 1e-3f, $"...which is not the identity (on {at})");
                check.Expect(NearV(SlotPos(dst, 2), Vector3.Zero),
                             $"...while the name nothing carries stays identity (on {at})");

                // ...and the LOCAL bone moves on its own, with the mesh its sibling, so it cannot ride along
                SceneDb.PartPos[localBone] = new Vector3(0f, 0f, 3f);
                Moved(pi); ComposeObject(piece);
                check.Expect(SkinPalette(mesh, dst), $"...and again once the local bone moves (on {at})");
                check.Expect(NearV(SlotPos(dst, 1), WantSlot(meshRest, localRest, mesh, localBone)),
                             $"slot 1 is the piece's OWN bone, resolved in its own tree (on {at}): "
                             + $"got {SlotPos(dst, 1)}, want {WantSlot(meshRest, localRest, mesh, localBone)}");
                check.Expect(SlotPos(dst, 1).Length() > 1e-3f, $"...which is not the identity either (on {at})");
            }
        }
        finally { NativeMemory.Free(dst); }

        Unskin(mesh);
        SceneDb.ObjSeamAppendHead[body.Index] = default;
        SceneDb.ObjSeamFlags[pi] &= ~(uint)SeamFlag.Composed;
        SceneDb.SetAttachTo(pi, default);
        Drop(piece);
        Drop(body);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>A palette slot's translation, as the stage holds it.</summary>
    private static Vector3 SlotPos(float* dst, int b) => new(dst[b * 16 + 12], dst[b * 16 + 13], dst[b * 16 + 14]);

    /// <summary>Where a bone's origin must land in the skinmesh's space, from the world columns the compose
    /// wrote: <c>meshRest * inverse(boneRest) * boneWorld * inverse(meshWorld)</c>, with the two rest matrices
    /// recorded while the composition was AT rest. The oracle for every grafted-skin expectation.</summary>
    private static Vector3 WantSlot(in Matrix4x4 meshRest, in Matrix4x4 boneRest, int mesh, int bone)
    {
        if (!Matrix4x4.Invert(boneRest, out Matrix4x4 boneRestInv)
            || !Matrix4x4.Invert(SceneDb.PartWorld[mesh], out Matrix4x4 meshInv)) return new Vector3(float.NaN);
        Matrix4x4 w = meshRest * boneRestInv * SceneDb.PartWorld[bone] * meshInv;
        return new Vector3(w.M41, w.M42, w.M43);
    }

    /// <summary>A compiled bone map names nodes of the TREE ROOT (<see cref="TreeRoot"/>): for a piece
    /// <c>Gob::Append</c> grafted, the body's skeleton; for a separately attached gob (a robe) its own model.
    /// The piece hangs off the body's leaf, so its own node 2 sits 2 m above the skeleton's. Then the freeze:
    /// only the BODY moves and the piece's staged palette must follow -- a rider is never <c>Moved</c> itself.</summary>
    private static void BoneMapOfRoot(Checker check)
    {
        ModelDef pm = Chain();
        pm.Nodes[1].Kind = NodeKind.Skin;
        pm.Nodes[1].Mesh = new MeshDef { Positions = new[] { Vector3.Zero } };
        pm.Nodes[1].Skin = new SkinDef
        {
            BoneNodes = new[] { -1, -1, 0 },
            BoneNodesAreMap = true,
            BindRotation = new[] { Quaternion.Identity, Quaternion.Identity, Quaternion.Identity },
            BindTranslation = new[] { Vector3.Zero, Vector3.Zero, Vector3.Zero },
            Weights = new[] { new Vector4(1f, 0f, 0f, 0f) },
            Bones = new[] { 0u },
        };
        DbId scene = SceneDb.Scenes.Alloc();
        DbId body = Subject(Chain(), scene, out int bodyFirst);
        DbId piece = Subject(pm, scene, out int first);
        int pi = piece.Index, mesh = first + 1;
        ScanTree(pi);
        SceneDb.SetAttachTo(pi, body);
        SceneDb.SetAttachPart(pi, bodyFirst + 2);
        SceneDb.ObjSeamFlags[pi] |= (uint)SeamFlag.Composed;
        SceneDb.ObjSeamAppendHead[body.Index] = piece;
        SceneDb.ObjSeamAppendNext[pi] = default;
        Moved(body.Index); ComposeObject(body);
        Moved(pi); ComposeObject(piece);

        Vector3 Want(int bone)
        {
            Matrix4x4.Invert(SceneDb.PartWorld[mesh], out Matrix4x4 inv);
            return Vector3.Transform(SceneDb.PartWorld[bone].Translation, inv);
        }
        float* dst = (float*)NativeMemory.Alloc((nuint)(BoneCount * 16) * sizeof(float));
        try
        {
            Vector3 want = Want(bodyFirst + 2);
            check.Expect(SkinPalette(mesh, dst) && NearV(new Vector3(dst[12], dst[13], dst[14]), want),
                         $"an appended piece's bone map names the BODY's node, not its own "
                         + $"(got {dst[12]:F2},{dst[13]:F2},{dst[14]:F2}, want {want.X:F2},{want.Y:F2},{want.Z:F2})");
            SceneDb.ObjSeamFlags[pi] &= ~(uint)SeamFlag.Composed;
            want = Want(first + 2);
            check.Expect(SkinPalette(mesh, dst) && NearV(new Vector3(dst[12], dst[13], dst[14]), want),
                         "...and an ATTACHED gob's (a robe's) names its own, its tree being its own");
            SceneDb.ObjSeamFlags[pi] |= (uint)SeamFlag.Composed;

            Moved(body.Index); Finish(body);
            SceneDb.PartPos[bodyFirst + 1] = new Vector3(1f, 2f, 1f);
            Moved(body.Index); Finish(body);
            int slot = SceneDb.PartBoneBase[mesh];
            float* st = slot < 0 ? null : AlloyRenderer.DbBoneStage(slot);
            want = Want(bodyFirst + 2);
            check.Expect(st != null && NearV(new Vector3(st[12], st[13], st[14]), want),
                         "a riding piece's palette follows the BODY's pose, not frozen at its first solve "
                         + (st == null ? "(no slot)" : $"(got {st[12]:F2},{st[13]:F2},{st[14]:F2}, want {want.X:F2},{want.Y:F2},{want.Z:F2})"));
        }
        finally { NativeMemory.Free(dst); }
        Unskin(mesh);
        SceneDb.ObjSeamAppendHead[body.Index] = default;
        SceneDb.ObjSeamFlags[pi] &= ~(uint)SeamFlag.Composed;
        SceneDb.SetAttachTo(pi, default);
        Drop(piece);
        Drop(body);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>The look-at EASES as the engine's generated two-key track does: one 0.05 s step covers a fifth
    /// of the turn toward a target off to the side, and the head settles on the aim; the end fades back.</summary>
    private static void LookAtEase(Checker check)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        DbId obj = Subject(Chain(), scene, out int first);
        DbId target = Subject(Chain(), scene, out _);
        SceneDb.ObjPos[target.Index] = new Vector3(-10f, 0f, 2f);   // yaw +90 (yaw 0 faces +Y), clamped to 60
        ComposeObject(obj);
        ComposeObject(target);
        LookAtBegin(obj, target, "leaf", "leaf", 60f, 30f, 0f);
        int head = first + 2;
        float Yaw() => MathF.Atan2(2f * (SceneDb.PartRot[head].W * SceneDb.PartRot[head].Z), 1f - 2f * SceneDb.PartRot[head].Z * SceneDb.PartRot[head].Z) * (180f / MathF.PI);
        LookAtStep(obj, 0.05f);
        float one = Yaw();
        check.Expect(one > 5f && one < 20f, $"one look-at step turns the head part of the way (yaw {one:F1}), not a snap");
        for (int k = 0; k < 60; k++) LookAtStep(obj, 0.05f);
        check.Expect(MathF.Abs(Yaw() - 60f) < 2f, $"...and settles on the clamped aim (yaw {Yaw():F1})");
        LookAtEnd(obj);
        for (int k = 0; k < 10; k++) LookAtStep(obj, 0.05f);
        check.Expect(!_lookAt[obj.Index].Active, "an ended look fades out and lets go");
        Drop(target);
        Drop(obj);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>The two forms of <see cref="SkinDef.BoneNodes"/>, over a file that states NO bind pose -- the
    /// shape of every stock cloak, tail and wing, where the derived bind runs. One bone, node <c>mid</c>: the
    /// ASCII form says that as the slot LIST {1}, the compiled binary as the bone MAP {-1,0,-1}, and both must
    /// put <c>mid</c> in slot 0, though the list's length and its largest entry plus one disagree (1 vs 2).</summary>
    private static void BoneTableForm(Checker check)
    {
        // form 2: the compiled binary's map WITH its bind arrays, which are per NODE (ExecuteSkinGPU reads
        // them at the slot's part index) -- slot 0 is node 1, so entry 1 is the bind and 0/2 are poison
        var poison = new Vector3(9f);
        for (int form = 0; form < 3; form++)
        {
            ModelDef m = Chain();
            m.Nodes[2].Kind = NodeKind.Skin;
            m.Nodes[2].Mesh = new MeshDef { Positions = new[] { Vector3.Zero } };
            m.Nodes[2].Skin = new SkinDef
            {
                BoneNodes = form == 0 ? new[] { 1 } : new[] { -1, 0, -1 },
                BoneNodesAreMap = form != 0,
                BindRotation = form == 2 ? new[] { Quaternion.Identity, Quaternion.Identity, Quaternion.Identity } : Array.Empty<Quaternion>(),
                BindTranslation = form == 2 ? new[] { poison, Vector3.Zero, poison } : Array.Empty<Vector3>(),
                Weights = new[] { new Vector4(1f, 0f, 0f, 0f) },
                Bones = new[] { 0u },
            };
            string what = form == 0 ? "slot list" : form == 1 ? "bone map" : "bone map with per-node bind arrays";
            // The skinmesh is a SIBLING of the bone here, not its child: a mesh that rides its own bone
            // would hold the identity whether the slot resolved or not. Subject() parents its three parts in
            // a chain, so the part tree is re-pointed with the model.
            m.Nodes[0].Children = new[] { 1, 2 };
            m.Nodes[1].Children = Array.Empty<int>();
            m.Nodes[2].Parent = 0;
            DbId scene = SceneDb.Scenes.Alloc();
            DbId obj = Subject(m, scene, out int first);
            SceneDb.PartParent[first + 2] = first;
            Moved(obj.Index);
            ComposeObject(obj);
            Matrix4x4 meshRest = SceneDb.PartWorld[first + 2], boneRest = SceneDb.PartWorld[first + 1];

            float* dst = (float*)NativeMemory.Alloc((nuint)(BoneCount * 16) * sizeof(float));
            try
            {
                check.Expect(SkinPalette(first + 2, dst), $"a bind-poseless skin solves from the {what}");
                check.Expect(Math.Abs(dst[12]) < 1e-4f && Math.Abs(dst[13]) < 1e-4f
                             && Math.Abs(dst[14]) < 1e-4f && Math.Abs(dst[0] - 1f) < 1e-4f,
                             $"...and its REST pose is the identity ({what})");
                // Move the LINK between the bone and the mesh, so the mesh travels and the bone does not:
                // the palette must then stop being the identity. The broken reading resolved slot 0 to
                // nothing and left the identity there, which is exactly the term this pins.
                SceneDb.PartPos[first + 1] = new Vector3(1f, 2f, 1f);
                Moved(obj.Index);
                ComposeObject(obj);
                check.Expect(SkinPalette(first + 2, dst), $"...and again once the bone moves ({what})");
                Matrix4x4.Invert(boneRest, out Matrix4x4 boneRestInv);
                Matrix4x4.Invert(SceneDb.PartWorld[first + 2], out Matrix4x4 meshInv);
                Matrix4x4 want = meshRest * boneRestInv * SceneDb.PartWorld[first + 1] * meshInv;
                check.Expect(NearV(new Vector3(dst[12], dst[13], dst[14]),
                                   new Vector3(want.M41, want.M42, want.M43)),
                             $"...slot 0 is MID carried into the mesh's space, not the identity ({what})");
                check.Expect(Math.Abs(dst[12]) + Math.Abs(dst[13]) + Math.Abs(dst[14]) > 1e-3f,
                             $"...which is not the identity a lost bone would leave ({what})");
            }
            finally { NativeMemory.Free(dst); }
            Drop(obj);
            SceneDb.Scenes.Free(scene);
        }
    }

    /// <summary>A body whose skinmesh "askin" shares a ring with a grafted piece but is weighted to the
    /// skeleton's "root", while the bone "mid" (its sibling, so the mesh does not ride it) is what moves.
    /// Normals +Z, against the piece's +Y.</summary>
    private static ModelDef StitchBody(bool compiled)
    {
        var m = new ModelDef { Name = "cx_selfcheck_stitch_body", AnimScale = 1f, BMin = new Vector3(-1f), BMax = new Vector3(1f) };
        m.Nodes = new[]
        {
            new NodeDef { Name = "root", Index = 0, Parent = -1, Children = new[] { 1, 2 }, Kind = NodeKind.Dummy },
            new NodeDef { Name = "mid", Index = 1, Parent = 0, Children = Array.Empty<int>(), Kind = NodeKind.Dummy,
                          Position = new Vector3(0f, 0f, 1f) },
            new NodeDef { Name = "askin", Index = 2, Parent = 0, Children = Array.Empty<int>(), Kind = NodeKind.Skin,
                          Mesh = Ring(new Vector3(0f, 1f, 0f), Vector3.UnitZ, Vector3.UnitX, 1f),
                          Skin = compiled ? MapBone() : OneBone("root") },
        };
        for (int i = 0; i < m.Nodes.Length; i++) m.NodeByName[m.Nodes[i].Name] = i;
        return m;
    }

    /// <summary>The grafted piece: "gskin" shares the body's ring but is weighted to "mid", so the ring comes
    /// apart when "mid" moves, and "glone" is a skinned part ten metres away with no partner at all. Its
    /// tangents point along +Y with handedness <paramref name="hand"/>, the body's along +X with +1.</summary>
    private static ModelDef StitchPiece(float hand)
    {
        var m = new ModelDef { Name = "cx_selfcheck_stitch_piece", AnimScale = 1f, BMin = new Vector3(-1f), BMax = new Vector3(1f) };
        m.Nodes = new[]
        {
            new NodeDef { Name = "groot", Index = 0, Parent = -1, Children = new[] { 1, 2 }, Kind = NodeKind.Dummy },
            new NodeDef { Name = "glone", Index = 1, Parent = 0, Children = Array.Empty<int>(), Kind = NodeKind.Skin,
                          Position = new Vector3(10f, 0f, 0f),
                          Mesh = Ring(new Vector3(0f, -1f, 0f), Vector3.UnitY, Vector3.UnitY, hand),
                          Skin = OneBone("mid") },
            new NodeDef { Name = "gskin", Index = 2, Parent = 0, Children = Array.Empty<int>(), Kind = NodeKind.Skin,
                          Mesh = Ring(new Vector3(0f, -1f, 0f), Vector3.UnitY, Vector3.UnitY, hand),
                          Skin = OneBone("mid") },
        };
        for (int i = 0; i < m.Nodes.Length; i++) m.NodeByName[m.Nodes[i].Name] = i;
        return m;
    }

    /// <summary>One triangle whose first two corners are the SHARED ring -- the origin and (1,0,0) -- with
    /// <paramref name="third"/> its own corner and <paramref name="normal"/> on all three.</summary>
    private static MeshDef Ring(Vector3 third, Vector3 normal, Vector3 tangent, float hand)
    {
        var t = new Vector4(tangent, hand);
        var mesh = new MeshDef
        {
            Positions = new[] { Vector3.Zero, Vector3.UnitX, third },
            Normals = new[] { normal, normal, normal },
            Tangents = new[] { t, t, t },
            Indices = new uint[] { 0, 1, 2 },
        };
        mesh.Uv[0] = new[] { Vector2.Zero, Vector2.UnitX, Vector2.UnitY };
        return mesh;
    }

    /// <summary>The same three-vertex skin as a COMPILED file states it: a bone map (one entry per node,
    /// node 0 taking slot 0) with the per-node bind arrays, whose stated bind is half a metre off the rest
    /// pose -- so a derived bind and the authored one cannot be confused.</summary>
    private static SkinDef MapBone()
    {
        SkinDef s = OneBone("root");
        s.BoneNames = Array.Empty<string>();
        s.BoneNodes = new[] { 0, -1, -1 };
        s.BoneNodesAreMap = true;
        s.BindRotation = new[] { Quaternion.Identity, Quaternion.Identity, Quaternion.Identity };
        s.BindTranslation = new[] { new Vector3(0f, 0f, 0.5f), Vector3.Zero, Vector3.Zero };
        return s;
    }

    /// <summary>A three-vertex skin, every vertex fully weighted to one named bone.</summary>
    private static SkinDef OneBone(string bone) => new()
    {
        BoneNames = new[] { bone },
        BoneNodes = new[] { -1 },
        Weights = new[] { new Vector4(1f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f), new Vector4(1f, 0f, 0f, 0f) },
        Bones = new uint[] { 0u, 0u, 0u },
        BoneIndices = new ushort[12],
        Influences = new[] { 1, 1, 1 },
    };

    /// <summary>The part-boundary heal: two skinned parts sharing a ring, weighted to different bones and
    /// normalled differently, come apart when posed and shade apart -- and after the stitch the ring deforms
    /// and shades as one, while a part with no partner keeps the geometry its file states. Oracles: the
    /// hand-written fixture, CPU skinning off the palette columns, and the arena's own vertex bytes.</summary>
    private static void BoundaryStitch(Checker check)
    {
        // pass 1 opposes the two rings' tangent handedness, which the packing reads as a sign and never
        // interpolates; pass 2 gives the body a COMPILED bone map whose stated bind is not its rest pose.
        BoundaryStitch(check, 1f, false);
        BoundaryStitch(check, -1f, false);
        BoundaryStitch(check, 1f, true);
    }

    /// <inheritdoc cref="BoundaryStitch(Checker)"/>
    private static void BoundaryStitch(Checker check, float hand, bool compiled)
    {
        DbId scene = SceneDb.Scenes.Alloc();
        DbId body = Subject(StitchBody(compiled), scene, out int bodyFirst);
        ModelDef pm = StitchPiece(hand);
        DbId piece = Subject(pm, scene, out int pieceFirst);
        int pi = piece.Index, skinA = bodyFirst + 2, skinB = pieceFirst + 2, lone = pieceFirst + 1;
        // Subject chains its three parts; these two trees are a root with two children.
        SceneDb.PartParent[skinA] = bodyFirst;
        SceneDb.PartParent[lone] = pieceFirst;
        SceneDb.PartParent[skinB] = pieceFirst;
        ScanTree(pi);
        SceneDb.ObjSeamFlags[pi] |= (uint)SeamFlag.Composed;
        SceneDb.ObjSeamAppendHead[body.Index] = piece;
        SceneDb.ObjSeamAppendNext[pi] = default;
        SceneDb.SetAttachTo(pi, body);
        SceneDb.SetAttachPart(pi, bodyFirst);
        // the bone "mid" moves, which only the piece's ring is weighted to
        SceneDb.PartPos[bodyFirst + 1] = new Vector3(0f, 2f, 1f);
        Moved(body.Index); ComposeObject(body);
        Moved(pi); ComposeObject(piece);

        float* dst = (float*)NativeMemory.Alloc((nuint)(BoneCount * 16) * sizeof(float));
        try
        {
            Vector3 a0 = PosedVertex(skinA, 0, dst), b0 = PosedVertex(skinB, 0, dst);
            check.Expect((a0 - b0).Length() > 1f,
                         $"as authored, the shared ring comes APART when posed -- that is the seam "
                         + $"({a0} against {b0})");

            // The corners NOBODY shares, posed before the stitch: a compiled part's own bind must still be
            // the one its file stated afterwards, to the bit.
            Vector3 keepA = PosedVertex(skinA, 2, dst), keepL = PosedVertex(lone, 2, dst);

            TreeChanged(body);
            SceneDb.PartPos[bodyFirst + 1] = new Vector3(0f, 2f, 1f);
            Moved(body.Index);
            Finish(body);
            check.Expect(PosedVertex(skinA, 2, dst) == keepA && PosedVertex(lone, 2, dst) == keepL,
                         $"a vertex no boundary touches poses IDENTICALLY after the stitch "
                         + $"({(compiled ? "compiled bind" : "derived bind")}: {PosedVertex(skinA, 2, dst)} "
                         + $"was {keepA})");
            check.Expect(StitchSkin(skinA) != null && StitchSkin(skinB) != null,
                         "a tree change owes the composition a stitch, and the step's tail runs it");
            check.Expect(StitchSkin(lone) == null,
                         "...and a skinned part with no partner is left exactly as its file states");

            a0 = PosedVertex(skinA, 0, dst); b0 = PosedVertex(skinB, 0, dst);
            check.Expect((a0 - b0).Length() < 1e-3f,
                         $"after the stitch the ring deforms as ONE ({a0} against {b0})");
            Vector3 a1 = PosedVertex(skinA, 1, dst), b1 = PosedVertex(skinB, 1, dst);
            check.Expect((a1 - b1).Length() < 1e-3f, $"...at both shared corners ({a1} against {b1})");
            // ...and the corner that is nobody's partner still rides the bone its file weighted it to.
            Vector3 own = PosedVertex(skinB, 2, dst);
            check.Expect((own - Vector3.Transform(pm.Nodes[2].Mesh!.Positions[2], SceneDb.PartWorld[skinB])).Length() > 1f,
                         "...while an unpartnered corner of the same mesh still follows its own bone");

            Vector3 na = ArenaNormal(skinA, 0), nb = ArenaNormal(skinB, 0);
            check.Expect(NearV(na, nb) && na != Vector3.Zero,
                         $"the shared ring's normals are averaged to one in the arena ({na} against {nb})");
            check.Expect(NearV(ArenaNormal(skinB, 2), Vector3.UnitY),
                         "...and an unpartnered vertex keeps the normal it was authored with");

            // The tangent frames: +X against +Y, so the average is the diagonal -- but only where the two
            // agree on handedness. The expected values are hand-written, read back from the arena's own
            // packed word.
            Vector4 ta = ArenaTangent(skinA, 0), tb = ArenaTangent(skinB, 0);
            var diagonal = new Vector3(0.70710678f, 0.70710678f, 0f);
            if (hand > 0f)
            {
                check.Expect(NearT(ta, diagonal) && NearT(tb, diagonal),
                             $"two frames of one handedness average to the diagonal ({ta} against {tb})");
                check.Expect(ta.W == 1f && tb.W == 1f, $"...with the handedness lane still exactly +1 ({ta.W}, {tb.W})");
            }
            else
            {
                check.Expect(NearT(ta, Vector3.UnitX) && ta.W == 1f,
                             $"frames of OPPOSITE handedness are not averaged: the body keeps +X ({ta})");
                check.Expect(NearT(tb, Vector3.UnitY) && tb.W == -1f,
                             $"...and the piece keeps +Y with its own -1 ({tb})");
            }
        }
        finally { NativeMemory.Free(dst); }

        SceneDb.ObjSeamAppendHead[body.Index] = default;
        SceneDb.ObjSeamFlags[pi] &= ~(uint)SeamFlag.Composed;
        // The piece retires while it is still attached, which is the edge a re-equip takes: the neighbour it
        // leaves behind has a ring unified with vertices that no longer exist.
        _stitch.Due[body.Index] = false;
        Drop(piece);
        check.Expect(_stitch.Due[body.Index],
                     "retiring a grafted piece owes its composition a new stitch");
        Drop(body);
        SceneDb.Scenes.Free(scene);
    }

    /// <summary>One skinned vertex's posed WORLD position, skinned on the CPU as the vertex stage does: the
    /// weighted sum of the vertex through each bone matrix the palette holds, then the part's own world
    /// matrix. Independent of the stitch, which states weights and normals but never a position.</summary>
    private static Vector3 PosedVertex(int part, int vi, float* dst)
    {
        SkinDef? s = StitchSkin(part) ?? ModelSkin(part);
        MeshDef? mesh = ModelMesh(part);
        if (s == null || mesh == null || !SkinPalette(part, dst)) return new Vector3(float.NaN);
        Vector3 p = mesh.Positions[vi], acc = Vector3.Zero;
        Vector4 w = s.Weights[vi];
        for (int c = 0; c < 4; c++)
        {
            float wc = c == 0 ? w.X : c == 1 ? w.Y : c == 2 ? w.Z : w.W;
            if (wc <= 0f) continue;
            int b = s.BoneIndices[vi * 4 + c];
            acc += wc * Vector3.Transform(p, PaletteSlot(dst, b));
        }
        return Vector3.Transform(acc, SceneDb.PartWorld[part]);
    }

    /// <summary>One palette slot as a matrix, in the order <c>Store</c> wrote it.</summary>
    private static Matrix4x4 PaletteSlot(float* dst, int b)
        => new(dst[b * 16], dst[b * 16 + 1], dst[b * 16 + 2], dst[b * 16 + 3],
               dst[b * 16 + 4], dst[b * 16 + 5], dst[b * 16 + 6], dst[b * 16 + 7],
               dst[b * 16 + 8], dst[b * 16 + 9], dst[b * 16 + 10], dst[b * 16 + 11],
               dst[b * 16 + 12], dst[b * 16 + 13], dst[b * 16 + 14], dst[b * 16 + 15]);

    /// <summary>The normal the ARENA holds for one of a part's vertices, in world terms: what the GPU will
    /// actually shade with, read back from the arena's own mirror rather than from any CPU copy.</summary>
    private static Vector3 ArenaNormal(int part, int vi)
    {
        int rec = SceneDb.PartRecipe[part];
        int range = rec >= 0 ? DrawRecipe.Range(rec) : SceneDb.PartMesh[part];
        if (range < 0 || !MeshArena.VertexProbe(range, vi, out _, out Vector3 n, out _, out _)) return Vector3.Zero;
        return Vector3.Normalize(Vector3.TransformNormal(n, SceneDb.PartWorld[part]));
    }

    /// <summary>The tangent frame the ARENA holds for one of a part's vertices, unpacked from the one word it
    /// uploaded -- so the assertion is on GPU bytes, not on a CPU copy.</summary>
    private static Vector4 ArenaTangent(int part, int vi)
    {
        int rec = SceneDb.PartRecipe[part];
        int range = rec >= 0 ? DrawRecipe.Range(rec) : SceneDb.PartMesh[part];
        if (range < 0 || !MeshArena.StreamProbe(range, vi, out uint packed, out _)) return default;
        return MeshArena.UnpackTangent(packed);
    }

    /// <summary>A packed tangent's direction against an expected one, inside the 10-bit lane's own step.</summary>
    private static bool NearT(Vector4 t, Vector3 want)
        => (new Vector3(t.X, t.Y, t.Z) - want).Length() < 0.01f;
}
