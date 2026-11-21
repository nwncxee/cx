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
        ExtenderLog? savedTreeLog = PartTree.Log, savedArenaLog = MeshArena.Log;
        PartTree.Log = null;
        MeshArena.Log = null;
        try
        {
            TextureOnlyOverride(check);
            OtherMaterialStill(check);
            DynamicDanglyKeepsItsSkin(check);
            ResetReleasesOldTextures(check);
        }
        finally
        {
            PartTree.Log = savedTreeLog;
            Drain();
            PartTree.ResetForCheck();
            MeshArena.ResetForCheck();
            MeshArena.Log = savedArenaLog;
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

    /// <summary>A dynamic dangly recipe draws solved vertices with no palette, and a reset of its material keeps it so,
    /// on its own mesh range, while the shared recipe of the same node keeps its palette.</summary>
    private static void DynamicDanglyKeepsItsSkin(Checker check)
    {
        using var files = new StandInFiles();
        bool skinning = AlloyRenderer.SelfCheckSkinning(true);
        files.Put("cx_rs_cloth", StandInFiles.Mtr, GravelMtr);
        Reset();
        var model = Model("cx_rs_clothmdl", new[] { -1, 0 }, new[] { NodeKind.Dummy, NodeKind.Dangly },
                          new MeshDef?[] { null, Quad("cx_rs_cloth") });
        model.Nodes[1].Rig = new CX.Alloy.Physics.DanglyRig { Skin = new SkinDef() };
        MtrFeed.Warm(model);
        int shared = DrawRecipe.Intern(model, 1, plan: false);
        int range = MeshArena.AcquireDynamic(model.Nodes[1].Mesh!, RsDynKey);
        int dyn = DrawRecipe.InternDynamic(model, 1, range);
        bool before = shared >= 0 && dyn >= 0 && (DrawRecipe.Flags(shared) & RecipeFlag.Skinned) != 0
                      && (DrawRecipe.Flags(dyn) & RecipeFlag.Skinned) == 0;
        check.Expect(before, "a dangly node's shared recipe does not carry a palette, or its dynamic one does");
        files.Put("cx_rs_cloth", StandInFiles.Mtr, PavingMtr);
        Override("cx_rs_cloth");
        check.Expect(dyn >= 0 && (DrawRecipe.Flags(dyn) & RecipeFlag.Skinned) == 0 && DrawRecipe.Range(dyn) == range
                     && DrawRecipe.TexName(dyn, 0) == "cx_rs_paving",
                     "a reset gave the dynamic dangly recipe a palette, moved its range, or left its old material");
        check.Expect(shared >= 0 && (DrawRecipe.Flags(shared) & RecipeFlag.Skinned) != 0,
                     "a reset took the shared dangly recipe's palette away");
        AlloyRenderer.SelfCheckSkinning(skinning);
        ForgetFixture(model);
    }

    /// <summary>A reset that moves a floor to another material lets go of the old material's texture, so it can be evicted.</summary>
    private static void ResetReleasesOldTextures(Checker check)
    {
        using var files = new StandInFiles();
        files.Put(RsFloor, StandInFiles.Mtr, GravelMtr);
        var (model, p) = Floor();
        int r = p >= 0 ? SceneDb.PartRecipe[p] : -1;
        TexId gravel = r >= 0 ? DrawRecipe.Tex(r, 0) : default;
        files.Put(RsFloor, StandInFiles.Mtr, PavingMtr);
        Override(RsFloor);
        TexState st = TexRegistry.StateOf(gravel);
        check.Expect(!gravel.IsNull && (st == TexState.Idle || st == TexState.Free),
                     $"the gravel texture is still held after the floor moved to the paving ({st})");
        ForgetFixture(model);
    }

    /// <summary>The per-instance key of the dynamic dangly fixture's range.</summary>
    private const ulong RsDynKey = 0x5E1F_C4EC_0000_0616UL;   // not-an-offset: a fixture key

    /// <summary>E9: a wall on another material keeps what it draws when the floor's name is overridden.</summary>
    private static void OtherMaterialStill(Checker check)
    {
        using var files = new StandInFiles();
        files.Put(RsFloor, StandInFiles.Mtr, GravelMtr);
        files.Put("cx_rs_wall", StandInFiles.Mtr, "texture0 cx_rs_bar\n");
        var (floor, floorPart) = Floor();
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
        SceneDb.Parts.TakeDirty();
        Override("cx_rs_wall");
        check.Expect((SceneDb.Parts.DirtyAt(floorPart) & Dirty.Material) == 0,
                     "an override of the wall rewrote the floor again, from the floor's earlier reset");
        ForgetFixture(floor);
        ForgetFixture(wallMdl);
    }
}
