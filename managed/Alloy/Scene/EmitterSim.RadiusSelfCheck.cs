using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy;

internal static unsafe partial class EmitterSim
{
    /// <summary>The cull radius against <c>PartEmitter::CalculateRadius</c> @0x4ac350's arms. Oracle: radii worked by
    /// hand from that function's formula (an effect-class model's 1000, the target distance, wind speed times
    /// lifetime added to the travel, the chunk reach) over a hand-built model.</summary>
    private static void RadiusSelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "EmitterSim.RadiusSelfCheck");
        check.Expect(RadiusArm(true, true, 30f, 5f, 12f) == 1000f, "an effect-class model's emitter is never culled short of 1000 units");
        check.Expect(RadiusArm(false, true, 30f, 5f, 12f) == 30f, "an emitter with a p2p target reaches exactly as far as its target");
        check.Expect(RadiusArm(false, false, 0f, 5f, 12f) == 12f, "a live chunk past the ballistic reach widens the radius to cover it");
        check.Expect(RadiusArm(false, false, 0f, 5f, 0f) == 5f, "with no chunk the ballistic reach stands");
        check.Expect(MathF.Abs(ViewRadiusOf(0f, 2f, 1f, 0f, 3f, 0f, 0f, 0f, 0f, 0f, 0f) - 8f) < 1e-5f,
            "a windy emitter's particles carry the wind's speed over their lifetime: (1 + 3) x 2 = 8");

        Reset();
        ModelDef m = TwoEmitters(11f, 22f);
        DbId obj = SceneDb.Objects.Alloc();
        DbFeed.InitObject(obj.Index);
        DbId ph = SceneDb.Parts.Alloc();
        int part = ph.Index;
        DbFeed.InitPart(part);
        SceneDb.PartObj[part] = obj;
        SceneDb.PartModel[part] = m;
        SceneDb.PartNode[part] = 1;
        SceneDb.PartScale[part] = 1f;
        SceneDb.ObjScale[obj.Index] = 1f;
        SceneDb.ObjModel[obj.Index] = m;
        SceneDb.PartWorld[part] = Matrix4x4.CreateTranslation(1f, 2f, 3f);
        SceneDb.ObjWorld[obj.Index] = SceneDb.PartWorld[part];
        SceneDb.ObjRot[obj.Index] = Quaternion.Identity;

        // life 2, velocity 1, mass 1, sizes 0.5 and 1: 9.81 * 0.5 * 4 + 1 * 2 + sqrt(0.5^2 + 0.5^2)
        m.Classification = 0;
        check.Expect(CullDb(part, out _, out float plain, out _) && MathF.Abs(plain - 22.32711f) < 1e-3f,
            $"a placeable's torch keeps the ballistic radius, 22.327 (got {plain})");
        m.Classification = ClassEffect;
        Emitter e = default;
        check.Expect(CullDb(part, out _, out float fx, out _) && fx == 1000f,
            $"the same emitter on a model classified `effect` is culled at 1000 (got {fx})");
        check.Expect(FillDb(part, ref e) && e.ViewRadius == fx, "...and the fill agrees with the cull");

        SceneDb.ObjModel[obj.Index] = null;
        SceneDb.Parts.Free(ph);
        SceneDb.Objects.Free(obj);
        SceneDb.Parts.TakeDirty();
        SceneDb.Objects.TakeDirty();
        Reset();
        log.Info(check.Ok ? "EmitterSim.RadiusSelfCheck: passed" : "EmitterSim.RadiusSelfCheck: FAILED");
    }
}
