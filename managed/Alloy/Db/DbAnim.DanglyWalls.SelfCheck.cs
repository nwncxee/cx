using System;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Alloy.Physics;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>The collision gather over hand-placed scene records: which objects a hanging part rests against,
/// and when it gathers them.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>Oracle: hand-placed records. A banner hangs 3 cm in front of a wall part turned and moved off the
    /// origin, so a slip in the part-space box loses it: the wall's two faces are gathered, and the same quad held
    /// by a creature, a door, an object riding the wall and a wall 20 m off is not. Nothing is kept until the pose
    /// has been held, the faces once it has, and nothing the frame it moves.</summary>
    private static void DanglyWallGather(Checker check)
    {
        ModelDef banner = DanglySelfCheck.Banner();
        DanglyRig? rig = DanglySelfCheck.Rig(banner);
        MeshDef? cloth = banner.Nodes[0].Mesh;
        if (rig == null || cloth == null) { check.Expect(false, "the banner rigs for the gather check"); return; }
        DbId scene = SceneDb.Scenes.Alloc();
        SceneDb.SceneIsWorld[scene.Index] = true;
        DbScene.Members(scene).Clear();
        var at = new Vector3(40f, 40f, 3f);
        Matrix4x4 here = Matrix4x4.CreateTranslation(at);
        Matrix4x4 face = Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(at - new Vector3(0f, 0.03f, 0f));
        ModelDef plain = WallModel("cx_selfcheck_wall", 0), door = WallModel("cx_selfcheck_door", HitTest.ClassDoor);
        MeshDef wall = plain.Nodes[0].Mesh!;
        var made = new DbId[6];
        made[0] = WallObject(scene, plain, wall, face);
        made[1] = WallObject(scene, plain, wall, face);
        made[2] = WallObject(scene, door, wall, face);
        made[3] = WallObject(scene, plain, wall, face);
        made[4] = WallObject(scene, plain, wall, face * Matrix4x4.CreateTranslation(20f, 0f, 0f));
        made[5] = WallObject(scene, plain, cloth, here);
        bool seated = true;
        foreach (DbId id in made) seated &= SceneDb.Objects.Valid(id);
        try
        {
            check.Expect(seated, "the gather fixture seats its six records");
            if (!seated) return;
            SceneDb.ObjFlags[made[1].Index] |= ObjFlag.Creature;
            SceneDb.SetAttachTo(made[3].Index, made[0]);
            int part = SceneDb.ObjPartFirst[made[5].Index];
            SceneDb.PartKindOf[part] = PartKind.Dangly;

            var into = new DanglyWall();
            GatherWalls(part, rig, cloth, here, scene, into, Stopwatch.GetTimestamp());
            check.Expect(into.Faces.Faces == 2, "a banner gathers the two faces of the wall it hangs on, and none "
                                              + $"from a creature, a door, a rider or a far wall ({into.Faces.Faces})");

            _dangly.Walls[part] = null;
            check.Expect(DanglyWalls(part, rig, cloth, here, on: true) == null,
                         "a part seen for the first time keeps no faces until it has held its pose");
            DanglyWall? w = _dangly.Walls[part];
            if (w != null) w.Due = 0;
            _dangly.WallWindowAt = 0;
            DanglyColliders? held = DanglyWalls(part, rig, cloth, here, on: true);
            check.Expect(held != null && held.Faces == 2, "once it has, they are gathered");
            check.Expect(DanglyWalls(part, rig, cloth, here * Matrix4x4.CreateTranslation(0f, 0f, 0.1f), on: true) == null,
                         "the frame it moves, it keeps none");
            check.Expect(DanglyWalls(part, rig, cloth, here, on: false) == null && _dangly.Walls[part] == null,
                         "with collision off it keeps none and holds no state");
        }
        finally
        {
            foreach (DbId id in made) FreeWallObject(id);
            DbScene.Members(scene).Clear();
            SceneDb.Scenes.Free(scene);
            MeshBvh.Evict(plain);
            MeshBvh.Evict(door);
        }
    }

    /// <summary>A one-node model holding a 6 x 4 m quad in its node's xy plane, front face toward +z.</summary>
    private static ModelDef WallModel(string name, byte classification)
    {
        var mesh = new MeshDef
        {
            Positions = new[] { new Vector3(-3f, -1f, 0f), new Vector3(3f, -1f, 0f),
                                new Vector3(3f, 3f, 0f), new Vector3(-3f, 3f, 0f) },
            Faces = new[] { new FaceDef { V0 = 0, V1 = 1, V2 = 2 }, new FaceDef { V0 = 0, V1 = 2, V2 = 3 } },
        };
        var m = new ModelDef { Name = name, Classification = classification };
        m.Nodes = new[] { new NodeDef { Name = "wall", Index = 0, Parent = -1, Kind = NodeKind.TriMesh, Mesh = mesh } };
        return m;
    }

    /// <summary>One visible model record in <paramref name="scene"/> with one mesh part at <paramref name="pose"/>.</summary>
    private static DbId WallObject(DbId scene, ModelDef model, MeshDef mesh, in Matrix4x4 pose)
    {
        DbId obj = SceneDb.Objects.Alloc();
        if (!SceneDb.Parts.AllocRange(1, out int p)) { SceneDb.Objects.Free(obj); return default; }
        int o = obj.Index;
        SceneDb.ObjKindOf[o] = ObjKind.Model;
        SceneDb.ObjModel[o] = model;
        SceneDb.ObjScene[o] = scene;
        SceneDb.ObjVisible[o] = true;
        SceneDb.ObjFlags[o] = ObjFlag.None;
        SceneDb.ObjSphere[o] = new Vector4(pose.Translation, 5f);
        SceneDb.ObjPartFirst[o] = p;
        SceneDb.ObjPartCount[o] = 1;
        DbScene.Members(scene).Add(obj);
        SceneDb.PartObj[p] = obj;
        SceneDb.PartModel[p] = model;
        SceneDb.PartNode[p] = 0;
        SceneDb.PartMeshDef[p] = mesh;
        SceneDb.PartKindOf[p] = PartKind.Mesh;
        SceneDb.PartEnabled[p] = true;
        SceneDb.PartMerged[p] = false;
        SceneDb.PartWorld[p] = pose;
        return obj;
    }

    private static void FreeWallObject(DbId obj)
    {
        if (!SceneDb.Objects.Valid(obj)) return;
        int o = obj.Index, p = SceneDb.ObjPartFirst[o];
        if (!SceneDb.ObjAttachTo[o].IsNull) SceneDb.SetAttachTo(o, default);
        SceneDb.ObjFlags[o] = ObjFlag.None;
        SceneDb.ObjModel[o] = null;
        SceneDb.ObjPartCount[o] = 0;
        SceneDb.PartModel[p] = null;
        SceneDb.PartMeshDef[p] = null;
        SceneDb.PartKindOf[p] = PartKind.Dummy;
        SceneDb.Parts.FreeRange(p, 1);
        SceneDb.Objects.Free(obj);
    }
}
