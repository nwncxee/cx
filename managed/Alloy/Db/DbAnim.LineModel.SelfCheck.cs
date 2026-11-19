using System.Numerics;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Db;

/// <summary>A model riding a panel text's line, stepped where the engine steps it. Oracle: the fixture's wave
/// keys its middle part at the origin at time 0 and 1 m along +X at half a second, against a bind pose 1 m up +Z.</summary>
public static unsafe partial class DbAnim
{
    // Given a chat line whose portrait the pointer has just reached, its highlight played as the game plays it
    // When the next frame is drawn, and the one after
    // Then the portrait shows the highlight both times, not its rest pose
    private static void LineModelStep(Checker check)
    {
        ModelDef m = Chain();
        m.Anims[0].TransTime = 0f;
        DbId panel = SceneDb.Scenes.Alloc();
        SceneDb.SceneIsWorld[panel.Index] = false;
        SceneDb.SceneAnimOn[panel.Index] = true;
        DbId world = SceneDb.Scenes.Alloc();
        SceneDb.SceneIsWorld[world.Index] = true;
        SceneDb.SceneAnimOn[world.Index] = true;
        DbId text = TextObjects.Create(panel, 1), hidden = TextObjects.Create(panel, 1), far = TextObjects.Create(world, 1);
        SceneDb.ObjVisible[hidden.Index] = false;
        DbId rider = Subject(m, default, out int first);
        DbId behind = Subject(m, default, out int firstBehind);
        DbId outside = Subject(m, default, out int firstOutside);
        TextDoc? d = TextObjects.Of(text), dh = TextObjects.Of(hidden), dw = TextObjects.Of(far);
        bool fixture = d != null && dh != null && dw != null && !rider.IsNull && !behind.IsNull && !outside.IsNull;
        check.Expect(fixture, "the fixture takes three texts and three portraits in no scene");
        if (fixture)
        {
            (DbId, TextDoc, int)[] lines = [(rider, d!, first), (behind, dh!, firstBehind), (outside, dw!, firstOutside)];
            foreach (var (r, doc, _) in lines)
            {
                ScanTree(r.Index);
                doc.PublishedQuads = new QuadList { Attached = [(r, 0f, 0f)], AttachedCount = 1 };
                PlayAnimation(r, "wave", 1f, RunFlags.AddIn, 0f);
            }
            Animate(panel, 0.5f);
            Animate(world, 0.5f);
            check.Expect(NearV(SceneDb.PartPos[first + 1], Vector3.Zero),
                         "a portrait on a chat line shows its highlight on the next frame, as StringGob::Render steps it");
            check.Expect(!NearV(SceneDb.PartPos[firstBehind + 1], Vector3.Zero),
                         "a portrait on a hidden line is not stepped -- the engine renders no hidden text");
            check.Expect(!NearV(SceneDb.PartPos[firstOutside + 1], Vector3.Zero),
                         "a model on a world text's line is not stepped by the panel walk");
            Animate(panel, 0.5f);
            check.Expect(NearV(SceneDb.PartPos[first + 1], Vector3.Zero),
                         "...and still shows it on the frame after: its clock stays at the animation's first frame");
            foreach (var (_, doc, _) in lines) doc.ResetQuads();
        }
        TextObjects.Destroy(text); TextObjects.Destroy(hidden); TextObjects.Destroy(far);
        if (!rider.IsNull) Drop(rider);
        if (!behind.IsNull) Drop(behind);
        if (!outside.IsNull) Drop(outside);
        SceneDb.Scenes.Free(panel);
        SceneDb.Scenes.Free(world);
    }
}
