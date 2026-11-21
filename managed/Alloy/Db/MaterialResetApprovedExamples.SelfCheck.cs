using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A server's override reaches the material a part draws (approved-spec, E1-E5). Oracle: the
/// hand-written <c>.mtr</c> texts below, served by <see cref="StandInFiles"/>, and the resrefs they name.</summary>
internal static partial class DbSelfCheck
{
    /// <summary>The floor's material, named after its texture as a tile's is.</summary>
    private const string RsFloor = "cx_rs_floor";
    private const string GravelMtr = "texture0 cx_rs_gravel\n";
    private const string PavingMtr = "texture0 cx_rs_paving\ntwosided 1\nrenderhint NormalAndSpecMapped\n";

    internal static bool MaterialResetApprovedExamplesSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbSelfCheck.MaterialResetApprovedExamples.SelfCheck");
        ExtenderLog? savedTreeLog = PartTree.Log;
        PartTree.Log = null;
        try
        {
            Cx616E1(check);
            Cx616E2(check);
            Cx616E3(check);
            Cx616E4(check);
            Cx616E5(check);
        }
        finally
        {
            PartTree.Log = savedTreeLog;
            Drain();
            PartTree.ResetForCheck();
            MeshArena.ResetForCheck();
        }
        log.Info(check.Ok ? "DbSelfCheck.MaterialResetApprovedExamples.SelfCheck: passed"
                          : "DbSelfCheck.MaterialResetApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    // Given a floor drawing material ttu01_floor from its .mtr, whose diffuse is gravel
    // When the server overrides ttu01_floor with one whose .mtr names a paving diffuse
    // Then from the next frame the floor draws the paving and the new material's other terms, as the stock client draws it
    private static void Cx616E1(Checker check)
    {
        using var files = new StandInFiles();
        files.Put(RsFloor, StandInFiles.Mtr, GravelMtr);
        (ModelDef model, int p) = Floor();
        check.Expect(Draws(p) == "cx_rs_gravel", $"E1: the floor does not start on its gravel .mtr ({Draws(p)})");
        files.Put(RsFloor, StandInFiles.Mtr, PavingMtr);
        Override(RsFloor);
        check.Expect(Draws(p) == "cx_rs_paving", $"E1: the floor draws {Draws(p)} after the override, not the paving");
        int r = SceneDb.PartRecipe[p];
        check.Expect(r >= 0 && (DrawRecipe.Flags(r) & RecipeFlag.TwoSided) != 0, "E1: the override's twosided term did not reach the floor");
        check.Expect(SceneDb.PartMtl[p].RenderHint == 2, $"E1: the override's render hint did not reach the floor ({SceneDb.PartMtl[p].RenderHint})");
        check.Expect((SceneDb.Parts.DirtyAt(p) & Dirty.Material) != 0, "E1: the floor's record is not rewritten for the override");
        ForgetFixture(model);
    }

    // Given the floor from E1 drawing the paving
    // When the server clears the override
    // Then the floor draws gravel from the original .mtr again
    private static void Cx616E2(Checker check)
    {
        using var files = new StandInFiles();
        files.Put(RsFloor, StandInFiles.Mtr, PavingMtr);
        (ModelDef model, int p) = Floor();
        check.Expect(Draws(p) == "cx_rs_paving", $"E2: the floor does not start on the paving ({Draws(p)})");
        files.Put(RsFloor, StandInFiles.Mtr, GravelMtr);
        Override(RsFloor);
        check.Expect(Draws(p) == "cx_rs_gravel", $"E2: the floor draws {Draws(p)} once the override is cleared, not the gravel");
        int r = SceneDb.PartRecipe[p];
        check.Expect(r >= 0 && (DrawRecipe.Flags(r) & RecipeFlag.TwoSided) == 0, "E2: the cleared override's twosided term stayed on the floor");
        check.Expect(SceneDb.PartMtl[p].RenderHint == 0, $"E2: the cleared override's render hint stayed on the floor ({SceneDb.PartMtl[p].RenderHint})");
        ForgetFixture(model);
    }

    // Given the floor from E1, built before the override
    // When a second floor of the same model is placed after it
    // Then both floors draw the same paving
    private static void Cx616E3(Checker check)
    {
        using var files = new StandInFiles();
        files.Put(RsFloor, StandInFiles.Mtr, GravelMtr);
        (ModelDef model, int old) = Floor();
        files.Put(RsFloor, StandInFiles.Mtr, PavingMtr);
        Override(RsFloor);
        int placed = Place(model);
        check.Expect(Draws(old) == "cx_rs_paving" && Draws(placed) == "cx_rs_paving",
                     $"E3: the floor built before the override draws {Draws(old)}, the one placed after it {Draws(placed)}");
        ForgetFixture(model);
    }

    // Given a floor whose material name had no .mtr when it was first drawn
    // When the server overrides that name with one that has a .mtr
    // Then the floor draws the override's material
    private static void Cx616E4(Checker check)
    {
        using var files = new StandInFiles();
        (ModelDef model, int p) = Floor();
        check.Expect(Draws(p) == RsFloor, $"E4: the floor with no .mtr does not draw its own texture ({Draws(p)})");
        files.Put(RsFloor, StandInFiles.Mtr, PavingMtr);
        Override(RsFloor);
        check.Expect(Draws(p) == "cx_rs_paving", $"E4: the floor draws {Draws(p)} after an override supplied its .mtr, not the paving");
        ForgetFixture(model);
    }

    // Given a floor drawing its .mtr's textures
    // When the server overrides the name with one that has no .mtr
    // Then the floor draws as the stock client draws it, from the mesh's own textures and not the old .mtr
    private static void Cx616E5(Checker check)
    {
        using var files = new StandInFiles();
        files.Put(RsFloor, StandInFiles.Mtr, PavingMtr);
        (ModelDef model, int p) = Floor();
        files.Remove(RsFloor, StandInFiles.Mtr);
        Override(RsFloor);
        check.Expect(Draws(p) == RsFloor, $"E5: the floor draws {Draws(p)} after the override took its .mtr away, not its own texture");
        int r = SceneDb.PartRecipe[p];
        check.Expect(r >= 0 && (DrawRecipe.Flags(r) & RecipeFlag.TwoSided) == 0, "E5: the old .mtr's twosided term stayed on the floor");
        check.Expect(SceneDb.PartMtl[p].RenderHint == 0, $"E5: the old .mtr's render hint stayed on the floor ({SceneDb.PartMtl[p].RenderHint})");
        ForgetFixture(model);
    }

    /// <summary>A one-quad floor model whose texture names <see cref="RsFloor"/>, its materials read as an object's
    /// creation reads them, and one placed instance of it.</summary>
    private static (ModelDef, int) Floor()
    {
        Reset();
        var model = Model("cx_rs_floormdl", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh },
                          new MeshDef?[] { null, Quad(RsFloor) });
        MtrFeed.Warm(model);
        return (model, Place(model));
    }

    /// <summary>Place one more instance of <paramref name="model"/> and give its part the recipe a record would.</summary>
    private static int Place(ModelDef model)
    {
        DbId obj = NewObject();
        if (!PartTree.Build(obj, model)) return -1;
        int p = SceneDb.ObjPartFirst[obj.Index] + 1;
        DrawRecipe.Assign(p, DrawRecipe.Intern(model, 1, plan: false));
        SceneDb.Parts.TakeDirty();
        return p;
    }

    /// <summary>The server sets or clears an override of <paramref name="name"/>: the engine's reset, then the next frame.</summary>
    private static void Override(string name)
    {
        TexRegistry.QueueReset(name);
        TexRegistry.Tick();
    }

    /// <summary>The diffuse part <paramref name="p"/> draws: its recipe's and its own column's, or a line saying they differ.</summary>
    private static string Draws(int p)
    {
        if (p < 0) return "(no part)";
        int r = SceneDb.PartRecipe[p];
        string recipe = r >= 0 ? DrawRecipe.TexName(r, 0) : "(no recipe)", part = SceneDb.PartTexName(p, 0);
        return recipe == part ? recipe : $"recipe {recipe} / part {part}";
    }
}
