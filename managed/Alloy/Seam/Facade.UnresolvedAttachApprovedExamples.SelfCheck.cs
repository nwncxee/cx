using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Mdl;
using CX.Framework;

namespace CX.Alloy.Seam;

internal static unsafe partial class FacadeSelfCheck
{
    /// <summary>An attach naming a part the target has not got is refused and leaves the scene alone; one naming a part it
    /// has joins the scene and rides that part. Oracle: <c>Gob::AttachToObject</c> @0x53e0e0 (Windows RVA 0x8c540) calls
    /// <c>SetBehavior(0)</c> and returns 0 without <c>SetScene</c> when <c>GetPart</c> answers null.</summary>
    private static void UnresolvedAttachApprovedExamples(Checker check)
    {
        using var models = UnreadableModelScope.Enter();
        DbId target = default, fx = default, rider = default, bare = default, sc = default;
        try
        {
            target = UnreadableModelScope.Mint();
            fx = UnreadableModelScope.Mint();
            rider = UnreadableModelScope.Mint();
            bare = UnreadableModelScope.Mint();
            nint ts = Facade.ObjectShell(target), fs = Facade.ObjectShell(fx), rs = Facade.ObjectShell(rider);
            var body = new ModelDef { Name = "cxtest_attach" };
            body.Nodes = new[]
            {
                new NodeDef { Name = "cxtest_attach", Index = 0, Kind = NodeKind.Dummy, Children = new[] { 1 } },
                new NodeDef { Name = "impact", Index = 1, Parent = 0, Kind = NodeKind.Dummy, Position = new Vector3(0f, 0f, 2f) },
            };
            bool built = PartTree.Build(target, body);
            check.Expect(built && SceneDb.ObjPartCount[target.Index] == 2, "the attach target has its two named parts");
            if (!built) return;
            int impact = SceneDb.ObjPartFirst[target.Index] + 1;
            sc = DbScene.Create("scene_cx_attach");
            ObjectSlots.JoinScene(target, ts, sc, 0);
            var attach = ObjectSlots.AttachToObjectSlot;

            // Given a target object with no part named "foo"
            // When an effect object attaches to it at "foo"
            // Then the attach returns failure and the effect does not join the target's scene, on Linux and Windows
            long refused = ObjectSlots.UnresolvedAttaches;
            nint missed;
            fixed (byte* foo = "foo\0"u8) missed = attach(fs, ts, (nint)foo, 0);
            check.Expect(missed == 0, $"an attach at a part the target has not got answers 0 (got {missed:x})");
            check.Expect(SceneDb.ObjScene[fx.Index].IsNull, "an effect refused its part does not join the target's scene");
            check.Expect(SceneDb.ObjAttachTo[fx.Index].IsNull, "an effect refused its part rides nothing");
            check.Expect(ObjectSlots.UnresolvedAttaches == refused + 1, "the period line counts the refused attach");
            check.Expect(SeamVtables.Windows(SeamTable.Gob)[19] == SeamVtables.Logical(SeamTable.Gob, 20),
                "Windows slot 19 of the Gob table is the same AttachToObject as Linux slot 20");

            // Given a target object with a part named "impact"
            // When an effect attaches to it at "impact"
            // Then the effect joins the scene and follows that part each frame, as today
            nint hit;
            fixed (byte* name = "impact\0"u8) hit = attach(fs, ts, (nint)name, 0);
            check.Expect(hit != 0, "an attach at a part the target has answers non-zero");
            check.Expect(SceneDb.ObjScene[fx.Index].Value == sc.Value, "an effect on a part it found joins the target's scene");
            check.Expect(SceneDb.ObjAttachPart[fx.Index] == impact, "the effect rides the part named impact");
            Facade.SetXform(target, ts, new Vector3(10f, 0f, 0f), Quaternion.Identity);
            DbAnim.SnapAttached(fx.Index);
            Vector3 first = SceneDb.ObjPos[fx.Index];
            Facade.SetXform(target, ts, new Vector3(20f, 0f, 0f), Quaternion.Identity);
            DbAnim.SnapAttached(fx.Index);
            Vector3 second = SceneDb.ObjPos[fx.Index];
            check.Expect(first == new Vector3(10f, 0f, 2f) && second == new Vector3(20f, 0f, 2f),
                $"the effect follows the impact part as the target moves ({first}, then {second})");

            fixed (byte* name = "impact\0"u8) attach(rs, ts, (nint)name, 0);
            fixed (byte* foo = "foo\0"u8) missed = attach(rs, ts, (nint)foo, 0);
            check.Expect(missed == 0 && SceneDb.ObjAttachTo[rider.Index].IsNull,
                "a refused re-attach drops the rider's earlier attach, as SetBehavior(0) does");
            check.Expect(SceneDb.ObjScene[rider.Index].Value == sc.Value, "...and leaves the scene it was in");

            nint onBare;
            fixed (byte* foo = "foo\0"u8) onBare = attach(rs, Facade.ObjectShell(bare), (nint)foo, 0);
            check.Expect(onBare != 0 && SceneDb.ObjAttachTo[rider.Index].Value == bare.Value,
                "a target with no parts at all (a model CX could not read) is still ridden, whatever the name");
        }
        finally
        {
            if (!fx.IsNull) ObjectSlots.DestroyRecord(fx);
            if (!rider.IsNull) ObjectSlots.DestroyRecord(rider);
            if (!bare.IsNull) ObjectSlots.DestroyRecord(bare);
            if (!target.IsNull) ObjectSlots.DestroyRecord(target);
            if (!sc.IsNull) DbScene.Destroy(sc);
        }
    }
}
