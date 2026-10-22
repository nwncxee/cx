using CX.Alloy.Physics;

namespace CX.Alloy.Db;

/// <summary>A worn strand's body collision (docs/spec/dangly-motions.md, section 5c): the wearer's rigid parts as
/// capsules, refilled every frame because the body animates under the strand.</summary>
public static unsafe partial class DbAnim
{
    /// <summary>How deep the wearer's pieces nest: body parts, then what is grafted onto them.</summary>
    private const int BodyDepth = 3;

    /// <summary>The body <paramref name="part"/> keeps out of this frame, or null when it is no worn strand or
    /// the strand knob holds strands like clothing.</summary>
    private static DanglyBody? DanglyBodyOf(int part, DanglyRig rig, DbId wearer, bool worn)
    {
        DanglyState d = _dangly;
        if (!worn || !rig.Strand || !(Dangly.Strand.V > 0f))
        {
            d.Bodies[part] = null;
            return null;
        }
        DanglyBody body = d.Bodies[part] ??= new DanglyBody();
        body.Clear();
        BodyParts(body, wearer, SceneDb.PartObj[part], 0);
        d.StrandParts++;
        d.Capsules += body.Count;
        d.CapsulesDropped += body.Dropped;
        return body;
    }

    /// <summary>Offer <paramref name="body"/> every rigid mesh part of <paramref name="obj"/> and of the pieces
    /// grafted onto it, except the strand's own object.</summary>
    private static void BodyParts(DanglyBody body, DbId obj, DbId self, int depth)
    {
        if (depth > BodyDepth || !SceneDb.Objects.Valid(obj)) return;
        int o = obj.Index;
        if (obj.Value != self.Value && SceneDb.ObjVisible[o])
        {
            int first = SceneDb.ObjPartFirst[o];
            for (int p = first; p < first + SceneDb.ObjPartCount[o]; p++)
            {
                PartKind k = SceneDb.PartKindOf[p];
                if ((k != PartKind.Mesh && k != PartKind.Dangly) || !SceneDb.PartEnabled[p]
                    || SceneDb.PartMeshDef[p] == null) continue;
                body.AddBox(SceneDb.PartBMin[p], SceneDb.PartBMax[p], in SceneDb.PartWorld[p]);
            }
        }
        for (DbId c = SceneDb.ObjSeamAppendHead[o]; !c.IsNull && SceneDb.Objects.Valid(c);
             c = SceneDb.ObjSeamAppendNext[c.Index])
            BodyParts(body, c, self, depth + 1);
    }

    /// <summary>The period's body clause, empty when no worn strand swung.</summary>
    private static string DanglyBodyPeriod(int frames)
    {
        DanglyState d = _dangly;
        if (d.StrandParts == 0) return "";
        string s = $" | strands: {(frames > 0 ? d.StrandParts / frames : 0)} part(s)/frame, "
                 + $"{d.Capsules / d.StrandParts} body capsule(s) each";
        if (d.CapsulesDropped != 0) s += $", {d.CapsulesDropped} body part(s) past the {DanglyBody.MaxCapsules} cap";
        d.StrandParts = d.Capsules = d.CapsulesDropped = 0;
        return s;
    }
}
