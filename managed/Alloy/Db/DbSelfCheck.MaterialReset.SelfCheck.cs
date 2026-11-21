using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>What a server's override must leave alone: a texture with no <c>.mtr</c> keeps its material terms and
/// still reloads its image, and a part on another material is not touched. Regression tests for behaviour that held
/// before the material reset (E8, E9). Oracle: the hand-written <c>.mtr</c> text and resrefs below.</summary>
internal static partial class DbSelfCheck
{
    /// <summary>A stand-in GL name and handle for a landed image, never dereferenced.</summary>
    private const uint RsName = 4711u;
    private const ulong RsHandle = 0xC0FFEE07UL;   // not-an-offset: a stand-in handle value, never dereferenced

    internal static bool MaterialResetHoldsTodaySelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "DbSelfCheck.MaterialResetHoldsToday.SelfCheck");
        ExtenderLog? savedTreeLog = PartTree.Log;
        PartTree.Log = null;
        try
        {
            TextureOnlyOverride(check);
            OtherMaterialStill(check);
        }
        finally
        {
            PartTree.Log = savedTreeLog;
            Drain();
            PartTree.ResetForCheck();
            MeshArena.ResetForCheck();
        }
        log.Info(check.Ok ? "DbSelfCheck.MaterialResetHoldsToday.SelfCheck: passed"
                          : "DbSelfCheck.MaterialResetHoldsToday.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>E8: a floor whose texture has no <c>.mtr</c> before or after keeps its terms and reloads its image.</summary>
    private static void TextureOnlyOverride(Checker check)
    {
        using var files = new StandInFiles();
        (ModelDef model, int p) = Floor();
        int r = SceneDb.PartRecipe[p];
        if (r >= 0) TexRegistry.Land(DrawRecipe.Tex(r, 0), RsName, 16, RsHandle);
        Override(RsFloor);
        check.Expect(r >= 0 && SceneDb.PartRecipe[p] == r && Draws(p) == RsFloor && SceneDb.PartMtr[p] == null,
                     $"E8: a texture-only override moved the floor off its own texture or recipe ({Draws(p)})");
        check.Expect(r >= 0 && TexRegistry.Handle(DrawRecipe.Tex(r, 0)) != RsHandle,
                     "E8: the floor still draws the image it had before the override");
        ForgetFixture(model);
    }

    /// <summary>E9: a wall on another material keeps what it draws when the floor's name is overridden.</summary>
    private static void OtherMaterialStill(Checker check)
    {
        using var files = new StandInFiles();
        files.Put(RsFloor, StandInFiles.Mtr, GravelMtr);
        files.Put("cx_rs_wall", StandInFiles.Mtr, "texture0 cx_rs_bar\n");
        var (floor, _) = Floor();
        var wallMdl = Model("cx_rs_wallmdl", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.TriMesh },
                            new MeshDef?[] { null, Quad("cx_rs_wall") });
        MtrFeed.Warm(wallMdl);
        int wall = Place(wallMdl);
        int wr = wall >= 0 ? SceneDb.PartRecipe[wall] : -1;
        uint tex = wr >= 0 ? DrawRecipe.Tex(wr, 0).Value : 0;
        files.Put(RsFloor, StandInFiles.Mtr, PavingMtr);
        Override(RsFloor);
        check.Expect(wall >= 0 && Draws(wall) == "cx_rs_bar" && DrawRecipe.Tex(wr, 0).Value == tex,
                     $"E9: the wall on another material changed with the floor's override ({(wall >= 0 ? Draws(wall) : "no wall")})");
        check.Expect(wall >= 0 && (SceneDb.Parts.DirtyAt(wall) & Dirty.Material) == 0,
                     "E9: the floor's override rewrote the wall's record");
        ForgetFixture(floor);
        ForgetFixture(wallMdl);
    }
}
