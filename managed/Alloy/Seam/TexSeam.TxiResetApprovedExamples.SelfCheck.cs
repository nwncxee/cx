using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Seam;

/// <summary>An object's <c>.txi</c> read after a server's override is the override's (approved-spec, E6).
/// Oracle: the two hand-written <c>.txi</c> texts below, served by <see cref="StandInFiles"/>, and the clamp each states.</summary>
internal static partial class TexSeam
{
    internal static bool TxiResetApprovedExamplesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "TexSeam.TxiResetApprovedExamples.SelfCheck");
        Cx616E6(check);
        log.Info(check.Ok ? "TexSeam.TxiResetApprovedExamples.SelfCheck: passed"
                          : "TexSeam.TxiResetApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given an object that applied foo.txi's sampler words before the server overrode foo
    // When it applies foo.txi again after the override
    // Then it takes the override's .txi words, not the ones it read before
    private static void Cx616E6(Checker check)
    {
        const string foo = "cx_rs_txi";
        var model = new ModelDef { Name = "cx_rs_txi_mdl" };
        model.Nodes = new[]
        {
            new NodeDef { Name = "root", Index = 0, Kind = NodeKind.Dummy },
            new NodeDef
            {
                Name = "body", Index = 1, Parent = 0, Kind = NodeKind.TriMesh,
                Mesh = new MeshDef
                {
                    Positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
                    Indices = new uint[] { 0, 1, 2 },
                    Textures = new[] { foo, "", "", "" },
                },
            },
        };
        DbId obj = SceneDb.Objects.Alloc();
        DbId part = SceneDb.Parts.Alloc();
        check.Expect(!obj.IsNull && !part.IsNull, "E6: the object or part table would not allocate");
        if (obj.IsNull || part.IsNull)
        {
            if (!obj.IsNull) SceneDb.Objects.Free(obj);
            if (!part.IsNull) SceneDb.Parts.Free(part);
            return;
        }
        int p = part.Index;
        SceneDb.ObjPartFirst[obj.Index] = p;
        SceneDb.ObjPartCount[obj.Index] = 1;
        SceneDb.PartObj[p] = obj;
        SceneDb.PartModel[p] = model;
        SceneDb.PartNode[p] = 1;
        SceneDb.PartMtr[p] = null;
        SceneDb.PartMeshDef[p] = model.Nodes[1].Mesh;
        SceneDb.PartTexId.AsSpan(p * 4, 4).Clear();
        DrawRecipe.Assign(p, DrawRecipe.Intern(model, 1, plan: false));
        using (var files = new StandInFiles())
        {
            files.Put(foo, StandInFiles.Txi, "clamp 1\n");
            TextureAttributes(obj, foo, foo);
            check.Expect(TexRegistry.Clamps(SceneDb.PartTex(p, 0)), "E6: the object's first .txi did not clamp its texture");
            files.Put(foo, StandInFiles.Txi, "clamp 0\n");
            TexRegistry.QueueReset(foo);
            TexRegistry.Tick();
            TextureAttributes(obj, foo, foo);
            check.Expect(!TexRegistry.Clamps(SceneDb.PartTex(p, 0)),
                         "E6: the object applied the .txi it read before the override, not the override's");
        }
        _txiCache.Remove(foo);
        DrawRecipe.Assign(p, -1);
        DrawRecipe.Forget(model);
        DrawRecipe.Pump();
        DrawRecipe.Pump();
        SceneDb.ObjPartFirst[obj.Index] = 0;
        SceneDb.ObjPartCount[obj.Index] = 0;
        SceneDb.Parts.Free(part);
        SceneDb.Objects.Free(obj);
        SceneDb.Objects.TakeDirty();
        SceneDb.Parts.TakeDirty();
    }
}
