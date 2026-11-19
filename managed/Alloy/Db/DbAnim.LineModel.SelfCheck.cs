using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A model riding a panel text's line, stepped where the engine steps it. Oracle: the fixture's wave
/// keys its middle part at the origin at time 0 and 1 m along +X at half a second, against a bind pose 1 m up +Z.</summary>
public static unsafe partial class DbAnim
{
    // Given a chat line whose portrait has just been told to play its hover highlight
    // When the panel's scene animates
    // Then the portrait wears the highlight's first frame, and its clock does not run on
    private static void LineModelStep(Checker check)
    {
        ModelDef m = Chain();
        DbId panel = SceneDb.Scenes.Alloc();
        SceneDb.SceneIsWorld[panel.Index] = false;
        SceneDb.SceneAnimOn[panel.Index] = true;
        DbId text = TextObjects.Create(panel, 1);
        DbId rider = Subject(m, default, out int first);
        TextDoc? d = TextObjects.Of(text);
        check.Expect(d != null && !rider.IsNull, "the fixture takes a panel text and a portrait in no scene");
        if (d != null && !rider.IsNull)
        {
            ScanTree(rider.Index);
            d.PublishedQuads = new QuadList { Attached = [(rider, 0f, 0f)], AttachedCount = 1 };
            PlayAnimation(rider, "wave", 1f, RunFlags.NoFadeIn, 0f);
            Animate(panel, 0.5f);
            check.Expect(NearV(SceneDb.PartPos[first + 1], Vector3.Zero),
                         "a model riding a panel text's line is stepped with its text, as StringGob::Render steps it");
            Animate(panel, 0.5f);
            check.Expect(NearV(SceneDb.PartPos[first + 1], Vector3.Zero),
                         "...by nothing: its clock stays at the animation's first frame");
            d.ResetQuads();
        }
        TextObjects.Destroy(text);
        if (!rider.IsNull) Drop(rider);
        SceneDb.Scenes.Free(panel);
    }
}
