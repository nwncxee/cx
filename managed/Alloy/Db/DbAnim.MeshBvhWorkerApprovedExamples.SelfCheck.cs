using System;
using System.Diagnostics;
using System.Numerics;
using CX.Alloy.Mdl;
using CX.Alloy.Physics;

namespace CX.Alloy.Db;

/// <summary>The dangly gather, seated for the mesh-tree worker's approved examples: a banner hung 3 cm in front of a
/// wall record, as <see cref="DanglyWallGather"/> seats it. Oracle: that check's two-face quad wall.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>The 6 x 4 m two-face quad wall <see cref="DanglyWallGather"/> gathers both faces of.</summary>
    internal static ModelDef WallQuadForCheck(string name) => WallModel(name, 0);

    /// <summary>Faces one gather keeps for a banner hung in front of a record holding <paramref name="wall"/>'s first
    /// mesh, or -1 when the fixture cannot seat. <paramref name="note"/> notes the wall for picking first, as an area
    /// entry does, and <paramref name="onNoted"/> runs between that and the gather.</summary>
    internal static int WallGatherFaces(ModelDef wall, bool note, Action? onNoted)
    {
        ModelDef banner = DanglySelfCheck.Banner();
        DanglyRig? rig = DanglySelfCheck.Rig(banner);
        MeshDef? cloth = banner.Nodes[0].Mesh;
        MeshDef? mesh = wall.Nodes[0].Mesh;
        if (rig == null || cloth == null || mesh == null) return -1;
        DbId scene = SceneDb.Scenes.Alloc();
        SceneDb.SceneIsWorld[scene.Index] = true;
        DbScene.Members(scene).Clear();
        var at = new Vector3(40f, 40f, 3f);
        Matrix4x4 here = Matrix4x4.CreateTranslation(at);
        Matrix4x4 face = Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(at - new Vector3(0f, 0.03f, 0f));
        DbId w = WallObject(scene, wall, mesh, face), b = WallObject(scene, wall, cloth, here);
        try
        {
            if (!SceneDb.Objects.Valid(w) || !SceneDb.Objects.Valid(b)) return -1;
            if (note) HitFeed.NoteObject(w);
            onNoted?.Invoke();
            int part = SceneDb.ObjPartFirst[b.Index];
            SceneDb.PartKindOf[part] = PartKind.Dangly;
            var into = new DanglyWall();
            GatherWalls(part, rig, cloth, here, scene, into, Stopwatch.GetTimestamp());
            return into.Faces.Faces;
        }
        finally
        {
            FreeWallObject(w);
            FreeWallObject(b);
            DbScene.Members(scene).Clear();
            SceneDb.Scenes.Free(scene);
            SceneDb.Objects.TakeDirty();
            SceneDb.Parts.TakeDirty();
            SceneDb.Scenes.TakeDirty();
        }
    }
}
