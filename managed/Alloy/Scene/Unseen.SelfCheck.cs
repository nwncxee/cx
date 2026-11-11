using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;

namespace CX.Alloy;

/// <summary>What a silhouette leaves as it was: plain regression tests for the approved examples that already held
/// (E4, E5, E9, E10, E11), and the edges of the marks. Oracle: the game's own 0.5, written by hand.</summary>
internal static class UnseenSelfCheck
{
    private const uint Oid = 4321;

    internal static bool Run(ExtenderLog log)
    {
        var check = new Checker(log, "UnseenSelfCheck");
        Func<string, bool> was = ObjectFns.ResourcePresent;
        long unreadable = ObjectFns.UnreadableModels;
        var o = new DbId[2];
        try
        {
            ObjectFns.ResourcePresent = n => n == "cx_present_unreadable";
            for (int i = 0; i < o.Length; i++)
                o[i] = Facade.Object(ObjectFns.CreateForCheck("cx_present_unreadable"), SeamSlot.Unnamed);
            DbId body = o[0], other = o[1];
            check.Expect(!body.IsNull && !other.IsNull, "the check's two objects could not be made");
            if (!check.Ok) return false;
            nint shell = Facade.ObjectShell(body);
            void GameAlpha(float a) => ObjectSlots.Shift(body, shell, shell, Vector3.One, a, false, true, 0);
            float Drawn(DbId id) => Unseen.Alpha(id.Index, SceneDb.ObjTint[id.Index].W, lit: true);
            bool Plain(DbId id) => !Unseen.Of(id.Index) && (Unseen.Slots(id.Index, 0) & Unseen.SlotBit) == 0;

            GameAlpha(0.5f);
            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Seen | Unseen.Invisible, body)]);
            check.Expect(Plain(body) && Drawn(body) == 0.5f, "E4 a creature under Invisibility that is perceived stays see-through");

            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard | Unseen.Invisible, body)]);
            check.Expect(Plain(body) && Drawn(body) == 0.5f, "E5 invisible and unseen at once counts as invisible");

            Unseen.Frame([]);
            check.Expect(Plain(body) && Drawn(body) == 0.5f, "E10 half see-through with no perception entry, as an unmanifested DM is, stays so");

            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, body)]);
            check.Expect(SceneDb.HitObjAlpha[body.Index] == 0.5f && new Gob(shell).Alpha == 0.5f,
                         "E9, E11 a silhouette leaves the game's own alpha alone: the pick and the game's renderer read what they did");

            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, body)]);
            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard | Unseen.Invisible, body)]);
            check.Expect(Plain(body), "a silhouette that turns invisible is see-through at once, whatever its alpha");

            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, body)]);
            GameAlpha(1f);
            Unseen.Frame([]);
            check.Expect(Plain(body), "a mark with no perception entry lets go once the game draws the creature whole");

            GameAlpha(0.5f);
            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, body)]);
            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, other)]);
            check.Expect(Plain(body) && !Plain(other), "a creature whose model was rebuilt carries its mark to the new one");

            Unseen.Reset();
            check.Expect(Plain(other), "a reset forgets every silhouette");

            Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, body)]);
            DbFeed.InitObject(body.Index);
            check.Expect(!SceneDb.ObjUnseen[body.Index], "a recycled object record starts unmarked");
        }
        finally
        {
            Unseen.Reset();
            for (int i = o.Length - 1; i >= 0; i--) if (!o[i].IsNull) ObjectSlots.DestroyRecord(o[i]);
            ObjectFns.ResourcePresent = was;
            ObjectFns.UnreadableModels = unreadable;
        }
        if (check.Ok) log.Info("UnseenSelfCheck: passed");
        return check.Ok;
    }
}
