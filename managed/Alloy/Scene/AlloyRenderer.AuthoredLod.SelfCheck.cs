using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy;

public sealed unsafe partial class AlloyRenderer
{
    /// <summary>Off-device: a slot whose object ships <c>.lod</c> alternates draws no generated run, in colour or
    /// shadow, and the same slot without one still does. Oracles: hand arithmetic (a 1 u sphere 100 u off at 540 px
    /// per unit spans 10.8 px, under the 150 px floor: level 2) and the arena fixture's own 6 and 3 index counts.
    /// Scratch slot 0, restored.</summary>
    internal static void AuthoredLodSelfCheck(Checker check, int range)
    {
        const int i = 0;
        int recipe = DrawRecipe.FlagsForCheck(0);
        DbId obj = SceneDb.Objects.Alloc(), part = SceneDb.Parts.Alloc();
        check.Expect(recipe >= 0 && !obj.IsNull && !part.IsNull, "the authored-LOD check found no free recipe, object or part");
        if (recipe >= 0 && !obj.IsNull && !part.IsNull)
        {
            int wasPart = _oDbPart[i], wasRecipe = _oDbRecipe[i];
            Sphere wasSph = _dSph[i];
            Vector3 wasEye = _eyePos;
            float wasScale = _lodScale, px1 = Lod.Px1.V, px2 = Lod.Px2.V, bias = Lod.Bias.V;
            bool master = Lod.G.Master is { V: true };
            if (Lod.G.Master != null) Lod.G.Master.V = true;
            Lod.Px1.V = 500f; Lod.Px2.V = 150f; Lod.Bias.V = 1f;
            DrawRecipe.RangeForCheck(recipe, range);
            SceneDb.PartObj[part.Index] = obj;
            _oDbPart[i] = part.Index; _oDbRecipe[i] = recipe;
            _eyePos = Vector3.Zero; _lodScale = 540f;
            _dSph[i] = new Sphere { Center = new Vector3(100f, 0f, 0f), Radius = 1f };

            check.Expect(RowLevel(i) == 2, "a far slot with no .lod does not earn the coarsest generated level");
            check.Expect(DbCasterLevels(i, out uint c1, out _, out uint c2, out _) && c1 == 6 && c2 == 3,
                         "a caster with no .lod does not stage the fixture's two generated runs");
            SceneDb.ObjSeamLodModel1[obj.Index] = new ModelDef { Name = "cxtest_lod1" };
            check.Expect(RowLevel(i) == 0, "a slot whose model ships a .lod earns a generated level");
            DbCasterLevels(i, out uint a1, out _, out uint a2, out _);
            check.Expect(a1 == 0 && a2 == 0, "a caster whose model ships a .lod stages a generated run");

            SceneDb.ObjSeamLodModel1[obj.Index] = null;
            SceneDb.PartObj[part.Index] = default;
            _oDbPart[i] = wasPart; _oDbRecipe[i] = wasRecipe; _dSph[i] = wasSph;
            _eyePos = wasEye; _lodScale = wasScale;
            Lod.Px1.V = px1; Lod.Px2.V = px2; Lod.Bias.V = bias;
            if (Lod.G.Master != null) Lod.G.Master.V = master;
        }
        if (recipe >= 0) { DrawRecipe.RangeForCheck(recipe, -1); DrawRecipe.ReleaseForCheck(recipe); }
        if (!obj.IsNull) SceneDb.Objects.Free(obj);
        if (!part.IsNull) SceneDb.Parts.Free(part);
    }
}
