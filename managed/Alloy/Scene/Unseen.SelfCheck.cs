using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Alloy.Seam;
using CX.Engine;
using CX.Framework;
using static CX.Alloy.UnseenFixture;

namespace CX.Alloy;

/// <summary>What the silhouette checks share: scene objects made and destroyed around a check's body, and the
/// questions each one asks of them.</summary>
internal static class UnseenFixture
{
    /// <summary>Seconds between frames that must leave no fade running.</summary>
    internal const float Whole = 10f;

    /// <summary>Is this object part of a silhouette, however far in?</summary>
    internal static bool Marked(int obj) => Unseen.Amount(obj) > 0f;

    /// <summary>The object alpha a lit part of <paramref name="id"/> draws with.</summary>
    internal static float Drawn(DbId id) => Unseen.Alpha(id.Index, SceneDb.ObjTint[id.Index].W, lit: true);

    /// <summary>The game sets <paramref name="body"/>'s alpha, as its fade and its Invisibility effect do.</summary>
    internal static void GameAlpha(DbId body, float alpha)
    {
        nint shell = Facade.ObjectShell(body);
        ObjectSlots.Shift(body, shell, shell, Vector3.One, alpha, false, true, 0);
    }

    /// <summary>Run <paramref name="body"/> over <paramref name="objects"/> fresh scene objects, with no silhouette
    /// left behind. A fixture that could not be made fails the check rather than passing it empty.</summary>
    internal static bool Run(ExtenderLog log, string name, int objects, Action<Checker, DbId[]> body)
    {
        var check = new Checker(log, name);
        Func<string, bool> was = ObjectFns.ResourcePresent;
        long unreadable = ObjectFns.UnreadableModels;
        var o = new DbId[objects];
        try
        {
            ObjectFns.ResourcePresent = n => n == "cx_present_unreadable";
            for (int i = 0; i < o.Length; i++)
                o[i] = Facade.Object(ObjectFns.CreateForCheck("cx_present_unreadable"), SeamSlot.Unnamed);
            check.Expect(Array.TrueForAll(o, id => !id.IsNull), $"the check's {objects} objects could not be made");
            if (check.Ok) body(check, o);
        }
        finally
        {
            Unseen.Reset();
            for (int i = o.Length - 1; i >= 0; i--) if (!o[i].IsNull) ObjectSlots.DestroyRecord(o[i]);
            ObjectFns.ResourcePresent = was;
            ObjectFns.UnreadableModels = unreadable;
        }
        if (check.Ok) log.Info($"{name}: passed");
        return check.Ok;
    }
}

/// <summary>What a silhouette leaves as it was, and the edges of its marks: plain regression tests, several for
/// behaviour that held before silhouettes existed. Oracle: the game's own 0.5, written by hand.</summary>
internal static class UnseenSelfCheck
{
    private const uint Oid = 4321;

    internal static bool Run(ExtenderLog log) => UnseenFixture.Run(log, "UnseenSelfCheck", 2, (check, o) =>
    {
        DbId body = o[0], other = o[1];
        nint shell = Facade.ObjectShell(body);
        bool Plain(DbId id) => !Marked(id.Index) && (Unseen.Slots(id.Index, 0) & Unseen.SlotMask(1f)) == 0;
        Unseen.Sight seen = new(Oid, Unseen.Heard | Unseen.Seen, body), heard = new(Oid, Unseen.Heard, body);
        Unseen.Sight unseenInvisible = new(Oid, Unseen.Heard | Unseen.Invisible, body);

        GameAlpha(body, 0.5f);
        Unseen.Frame([new Unseen.Sight(Oid, Unseen.Seen | Unseen.Invisible, body)], Whole);
        check.Expect(Plain(body) && Drawn(body) == 0.5f, "a creature under Invisibility that is perceived stays see-through");

        Unseen.Frame([unseenInvisible], Whole);
        check.Expect(Plain(body) && Drawn(body) == 0.5f, "invisible and unseen at once counts as invisible");

        Unseen.Frame([], Whole);
        check.Expect(Plain(body) && Drawn(body) == 0.5f, "half see-through with no perception entry stays so");

        Unseen.Frame([heard], Whole);
        check.Expect(SceneDb.HitObjAlpha[body.Index] == 0.5f && new Gob(shell).Alpha == 0.5f,
                     "a silhouette leaves the game's own alpha alone: the pick and the game's renderer read what they did");

        Unseen.Frame([unseenInvisible], Whole);
        check.Expect(Plain(body), "a silhouette that turns invisible ends up see-through, whatever its alpha");

        Unseen.Frame([heard], Whole);
        GameAlpha(body, 1f);
        Unseen.Frame([], Whole);
        check.Expect(Plain(body), "a mark with no perception entry lets go once the game draws the creature whole");

        GameAlpha(body, 0.5f);
        Unseen.Frame([heard], Whole);
        Unseen.Frame([new Unseen.Sight(Oid, Unseen.Heard, other)], Whole);
        check.Expect(Plain(body) && !Plain(other), "a creature whose model was rebuilt carries its mark to the new one");

        Unseen.Reset();
        check.Expect(Plain(other), "a reset forgets every silhouette");

        GameAlpha(body, 1f);
        Unseen.Frame([seen], Whole);
        GameAlpha(body, 0.5f);
        Unseen.Frame([heard], 0.25f);
        bool solidIn = Drawn(body) == 1f;
        GameAlpha(body, 1f);
        Unseen.Frame([seen], 0.25f);
        check.Expect(solidIn && Drawn(body) == 1f, "a creature is solid part way into its silhouette and part way out of it");

        GameAlpha(body, 0.5f);
        Unseen.Frame([heard], Whole);
        Unseen.Frame([unseenInvisible], 0.25f);
        check.Expect(Drawn(body) == 0.75f, $"a silhouette that turns invisible eases from solid to see-through, drew at {Drawn(body)} half way");

        Unseen.Reset();
        GameAlpha(body, 0.25f);
        Unseen.Frame([heard], 0.125f);
        check.Expect(Unseen.Amount(body.Index) == 1f && Drawn(body) == 0.5f,
                     $"a creature that arrives out of sight fades up as a silhouette, never through its own colours: {Unseen.Amount(body.Index)}");

        bool had = Renderer.Silhouette.V;
        try
        {
            GameAlpha(body, 0.5f);
            Unseen.Frame([heard], Whole);
            Renderer.Silhouette.Set(false);
            Unseen.Frame([heard], Whole);
            bool off = Plain(body) && Drawn(body) == 0.5f;
            Renderer.Silhouette.Set(true);
            Unseen.Frame([heard], Whole);
            check.Expect(Renderer.Silhouette.Default && off && !Plain(body) && Drawn(body) == 1f,
                         $"silhouettes are on by default; switched off the creature draws see-through ({off}), and switched back on it is a silhouette again");
        }
        finally { Renderer.Silhouette.Set(had); }

        Unseen.Frame([heard], Whole);
        DbFeed.InitObject(body.Index);
        check.Expect(SceneDb.ObjUnseen[body.Index].Amount == 0f, "a recycled object record starts unmarked");
    });
}
