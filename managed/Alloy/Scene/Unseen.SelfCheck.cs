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

    /// <summary>Did the staged shaderc load? Without it no look can be compiled, and a check that wears one skips.</summary>
    internal static bool Compiler() => Array.TrueForAll(CX.Graphics.VulkanToolchain.Libraries, CX.Graphics.VulkanToolchain.Load);

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

    /// <summary>Run <paramref name="body"/> over <paramref name="objects"/> fresh scene objects with silhouettes
    /// switched on, with none left behind and the switch put back. A fixture that could not be made fails the check rather than passing it empty.</summary>
    internal static bool Run(ExtenderLog log, string name, int objects, Action<Checker, DbId[]> body)
    {
        var check = new Checker(log, name);
        Func<string, bool> was = ObjectFns.ResourcePresent;
        long unreadable = ObjectFns.UnreadableModels;
        var o = new DbId[objects];
        bool on = Unseen.Enabled.V;
        try
        {
            Unseen.Enabled.Set(true);
            ObjectFns.ResourcePresent = n => n == "cx_present_unreadable";
            for (int i = 0; i < o.Length; i++)
                o[i] = Facade.Object(ObjectFns.CreateForCheck("cx_present_unreadable"), SeamSlot.Unnamed);
            check.Expect(Array.TrueForAll(o, id => !id.IsNull), $"the check's {objects} objects could not be made");
            if (check.Ok) body(check, o);
        }
        finally
        {
            Unseen.Reset();
            Unseen.Undress();
            Unseen.Enabled.Set(on);
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

        Knob? row = KnobGroup.Find("silhouette.enabled");
        check.Expect(row is { Tier: Tier.User } && row.Group.Category == "Graphics" && row.Group.Submenu == "World",
                     "the silhouette's switch is a plain row under Graphics, World, not behind the Advanced rows");

        bool had = Unseen.Enabled.V;
        try
        {
            GameAlpha(body, 0.5f);
            Unseen.Frame([heard], Whole);
            Unseen.Enabled.Set(false);
            Unseen.Frame([heard], Whole);
            bool off = Plain(body) && Drawn(body) == 0.5f;
            Unseen.Enabled.Set(true);
            Unseen.Frame([heard], Whole);
            check.Expect(!Unseen.Enabled.Default && off && !Plain(body) && Drawn(body) == 1f,
                         $"silhouettes are off by default; switched off the creature draws see-through ({off}), and switched on it is a silhouette");
        }
        finally { Unseen.Enabled.Set(had); }

        Unseen.Frame([heard], Whole);
        DbFeed.InitObject(body.Index);
        check.Expect(SceneDb.ObjUnseen[body.Index].Amount == 0f, "a recycled object record starts unmarked");
    });
}

/// <summary>The edges of a server's silhouette look: plain regression tests, found while building it. Oracle: look
/// files written here by hand, the alphas their lines ask for, and the stock look's own text.</summary>
internal static class UnseenLookSelfCheck
{
    private const uint Oid = 8642;

    internal static bool Run(ExtenderLog log) => UnseenFixture.Run(log, "UnseenLookSelfCheck", 1, (check, o) =>
    {
        if (!Compiler())
        {
            check.Skip("the staged shaderc did not load");
            return;
        }
        DbId body = o[0];
        const string blue = UnseenServerLookApprovedExamples.Blue;
        Unseen.Sight heard = new(Oid, Unseen.Heard, body);
        bool had = Unseen.Enabled.V;
        try
        {
            GameAlpha(body, 0.5f);
            bool worn = Unseen.Wear("#pragma cx_silhouette opacity 20\n" + blue, log);
            Unseen.Frame([heard], Whole);
            float on = Drawn(body);
            Unseen.Enabled.Set(false);
            Unseen.Frame([heard], Whole);
            check.Expect(worn && on == 0.2f && !Marked(body.Index) && Drawn(body) == 0.5f,
                         $"a player who switched silhouettes off sees the stock see-through creature under a server's look too: worn {worn}, on {on}, off {Drawn(body)}");
        }
        finally { Unseen.Enabled.Set(had); }

        int named = 0;
        Action<string>? tap = log.Tap;
        log.Tap = line => { if (line.Contains("Unseen: cx_silhouette.shd is refused")) named++; };
        bool refused;
        try
        {
            refused = !Unseen.Wear("#pragma cx_silhouette opacity 150\n" + blue, log) && !Unseen.Wear("#pragma cx_silhouette sparkle\n" + blue, log)
                   && !Unseen.Wear("#pragma cx_silhouette glow 1\n" + blue, log);
        }
        finally { log.Tap = tap; }
        check.Expect(refused && named == 3 && Unseen.Worn().Contains("CX_SILHOUETTE_RGB") && !Unseen.Worn().Contains("vec3(0.0, 0.02, 0.3)"),
                     $"a look with a declaration line CX cannot read is refused whole, and the log names the file each time: refused {refused}, named {named}");

        bool plain = Unseen.Wear(blue, log);
        string lit = VkPrograms.LitPair(log).Fs;
        Unseen.Undress();
        check.Expect(plain && lit.Contains("vec3(0.0, 0.02, 0.3)") && !VkPrograms.LitPair(log).Fs.Contains("vec3(0.0, 0.02, 0.3)"),
                     "the lit stage carries the look that is worn, and the stock one once it is taken off");
    });
}

/// <summary>The silhouette library on the device beyond its approved examples: plain regression tests. Oracle: the grey
/// and the shares written by hand.</summary>
internal static partial class GpuGolden
{
    /// <summary>The stage the first silhouette goldens run: a case's lit colour and slot mask in, its colour out.</summary>
    private const string SlotCase = """
        void main()
        {
            int i = int(gl_WorkGroupID.x) * CX_GOLD_IN, o = int(gl_WorkGroupID.x) * CX_GOLD_OUT;
            vec3 lit = vec3(uIn[i], uIn[i + 1], uIn[i + 2]);
            float own;
            vec3 r = CxSilhouetteApply(CxSilhouetteSurface(lit, lit, vec3(0.0, 0.0, 1.0), vec3(0.0, 0.0, 1.0), vec3(0.0),
                                                           CxSilhouetteAmount(int(uIn[i + 3])), 0.0), own);
            uOut[o] = r.x; uOut[o + 1] = r.y; uOut[o + 2] = r.z;
        }
        """;

    private static void UnseenLookEdgesSelfCheck(CX.Graphics.Rhi.VkBackend vk, Checker check, ExtenderLog log)
    {
        float[] cases =
        [
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 0f, 1f, 1f, 0f,
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 0f, 1f, 0.5f, 0f,
            0.9f, 0.05f, 0.04f, 0.6f, 0.03f, 0.03f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f,
        ];
        float[]? stock = LookCases(vk, log, null, cases, "golden silhouette look stock");
        check.Expect(stock != null && stock[0] == Grey && stock[1] == Grey && stock[2] == Grey && stock[3] == 0f,
                     "a module with no look of its own draws the stock dark grey and keeps none of the creature's light");
        check.Expect(stock != null && stock[7] == 0.5f && stock[8] == 0.9f && stock[11] == 1f,
                     "half way into its silhouette a creature keeps half its own light, and all of it and its own colour before the fade starts");
        float[]? greedy = LookCases(vk, log, "CxSilhouetteLook CxSilhouetteShade(CxSilhouetteSurface s) { return CxSilhouetteLook(s.lit, 5.0); }\n",
                                    cases[..14], "golden silhouette look greedy");
        check.Expect(greedy != null && greedy[3] == 1f, "a look cannot ask for more than all of the creature's own light");
    }
}
