using System;
using System.Numerics;
using CX.Alloy.Db;
using CX.Framework;
using static CX.Alloy.AirFixture;
using static CX.Alloy.MistFixture;

namespace CX.Alloy;

/// <summary>A forest's haze hangs between the trunks and its leaves fall. Oracle, by hand: the grove laid trunk by trunk
/// west of a clearing, the wind each script sets, and leaf counts tallied over a fixed span of frames.</summary>
internal static class AirForestApprovedExamples
{
    /// <summary>Head height among the trunks, where the haze is read.</summary>
    private const float Head = 1.5f;

    /// <summary>A point among the trunks, and one in the middle of the clearing.</summary>
    private static readonly Vector2 Trees = new(-10f, 0f), Clearing = new(20f, 0f);

    [SelfCheckSuite(SuitePhase.AfterServices, InSession = false)]
    internal static bool SelfCheck(ExtenderLog log)
    {
        var check = new Checker(log, "AirForestApprovedExamples.SelfCheck");
        using var switches = AirFixture.Switches.Force();
        HazeAndLeaves(check);
        WindLeaves(check);
        FrontLeaves(check);
        WindThins(check);
        FrontTears(check);
        Fireball(check);
        WalkParts(check);
        ClearingEmpty(check);
        HeldForest(check);
        Unhold();
        log.Info(check.Ok ? "AirForestApprovedExamples.SelfCheck: passed" : "AirForestApprovedExamples.SelfCheck: FAILED");
        return check.Ok;
    }

    /// <summary>The grove entered and settled ten seconds under <paramref name="wind"/>.</summary>
    private static void Wood(Vector2 wind)
    {
        EnterWith(Forest, Flat(), 0f, false, true, false);
        Go(10f, wind);
    }

    /// <summary>The haze's mean density among the trunks round <paramref name="at"/>.</summary>
    private static float Haze(Vector2 at) => Thick(AirLayer.Haze, at.X, at.Y, 4f, Head);

    /// <summary>The leaves seen over <paramref name="seconds"/> under <paramref name="wind"/>: the most aloft at once,
    /// and their mean velocity over every frame that held one.</summary>
    private static (int Most, Vector3 Vel) Leaves(float seconds, Vector2 wind)
    {
        int most = 0, frames = 0;
        Vector3 vel = Vector3.Zero;
        for (int n = (int)MathF.Round(seconds / Frame); n > 0; n--)
        {
            AirScene.Step(Frame, wind);
            ReadOnlySpan<AirParticle> l = AirScene.Particles(AirLayer.Leaves);
            most = Math.Max(most, l.Length);
            if (l.Length == 0) continue;
            Vector3 sum = Vector3.Zero;
            foreach (AirParticle p in l) sum += p.Vel;
            vel += sum / l.Length;
            frames++;
        }
        return (most, frames > 0 ? vel / frames : Vector3.Zero);
    }

    // Given an outdoor forest area in calm air
    // When the player stands among the trees
    // Then a thin haze hangs between the trunks, and a leaf falls now and then.
    private static void HazeAndLeaves(Checker check)
    {
        Wood(Vector2.Zero);
        float haze = Haze(Trees);
        (int most, _) = Leaves(30f, Vector2.Zero);
        check.Expect(haze > 0f, $"E63 the haze between the trunks reads {haze:F4} (want some)");
        check.Expect(most > 0 && most <= 8, $"E63 at most {most} leaves fall at once in thirty calm seconds (want a few, now and then)");
    }

    // Given the same forest
    // When the wind is strong
    // Then more leaves fall than in calm air, and they tumble along the wind's direction.
    private static void WindLeaves(Checker check)
    {
        Wood(Vector2.Zero);
        (int calm, _) = Leaves(30f, Vector2.Zero);
        Wood(Strong);
        Go(30f, Strong);
        (int windy, Vector3 vel) = Leaves(30f, Strong);
        check.Expect(windy > calm, $"E64 {windy} leaves fall at once in the strong wind against {calm} calm (want more)");
        check.Expect(vel.X > 0f && vel.X > MathF.Abs(vel.Y), $"E64 the leaves tumble at {vel} (want along {Strong})");
    }

    // Given leaves falling in a forest
    // When a gust front passes
    // Then they race along with the front.
    private static void FrontLeaves(Checker check)
    {
        Wood(Vector2.Zero);
        Leaves(30f, Vector2.Zero);
        (_, Vector3 front) = Leaves(0.5f, Strong);
        Go(60f, Strong);
        (_, Vector3 steady) = Leaves(0.5f, Strong);
        check.Expect(front.X > steady.X && front.X > 0f, $"E65 the front carries the leaves at {front.X:F3} m/s against {steady.X:F3} m/s steady (want racing with it)");
    }

    // Given haze between the trunks
    // When the wind is strong
    // Then the haze is thinner than in calm air.
    private static void WindThins(Checker check)
    {
        Wood(Vector2.Zero);
        float calm = Haze(Trees);
        Wood(Strong);
        Go(60f, Strong);
        float windy = Haze(Trees);
        check.Expect(calm > 0f && windy < calm, $"E66 the haze reads {windy:F4} in the strong wind against {calm:F4} calm (want thinner)");
    }

    // Given haze between the trunks in calm air
    // When a gust front passes
    // Then the haze is torn thin along the front, and it gathers back over tens of seconds once the air is calm.
    private static void FrontTears(Checker check)
    {
        Wood(Vector2.Zero);
        float calm = Haze(Trees);
        float least = calm;
        for (int n = (int)(2f / Frame); n > 0; n--) { AirScene.Step(Frame, Strong); least = MathF.Min(least, Haze(Trees)); }
        float back = 0f;
        while (back < 120f && Haze(Trees) < 0.9f * calm) { Go(0.25f, Vector2.Zero); back += 0.25f; }
        check.Expect(calm > 0f && least < 0.7f * calm, $"E137 the front tears the haze to {least:F4} from {calm:F4} (want torn thin)");
        check.Expect(back >= 10f && back < 120f, $"E137 the haze gathers back in {back:F1} s (want tens of seconds)");
    }

    // Given haze and falling leaves around a point in a forest
    // When a fireball detonates there
    // Then the haze is blown outward and the leaves near it are thrown outward, and the haze rolls back within ten seconds.
    private static void Fireball(Checker check)
    {
        Wood(Strong);
        Go(20f, Strong);
        ReadOnlySpan<AirParticle> leaves = AirScene.Particles(AirLayer.Leaves);
        check.Expect(leaves.Length > 0, "E138 Given: leaves are falling");
        if (leaves.Length == 0) return;
        Vector3 at = leaves[0].Pos with { Z = Head };
        var here = new Vector2(at.X, at.Y);
        float before = Thick(AirLayer.Haze, here.X, here.Y, 1f, Head);
        AirScene.Blast(at, 4f);
        Go(0.2f, Strong);
        float blown = Thick(AirLayer.Haze, here.X, here.Y, 1f, Head), thrown = Outward(AirLayer.Leaves, at, 4f);
        Go(9.8f, Strong);
        float back = Thick(AirLayer.Haze, here.X, here.Y, 1f, Head);
        check.Expect(before > 0f && blown < 0.5f * before && thrown > 0f, $"E138 the blast leaves the haze at {blown:F4} from {before:F4} and throws the leaves out at {thrown:F3} m/s (want blown out, thrown)");
        check.Expect(back >= 0.8f * before, $"E138 ten seconds on the haze reads {back:F4} against {before:F4} (want rolled back)");
    }

    // Given haze between the trunks
    // When the player walks 5 m through it
    // Then the path behind them parts and curls into eddies, then settles, as E31 says.
    private static void WalkParts(Checker check)
    {
        Wood(Vector2.Zero);
        var from = new Vector3(Trees.X - 2.5f, 0f, 0f);
        DbId world = MistPartingApprovedExamples.World(), who = MistPartingApprovedExamples.Seat(world, from, true);
        Walk(world, who, from, from + new Vector3(5f, 0f, 0f), true, Pace(5f));
        float path = Thick(AirLayer.Haze, Trees.X, 0f, 1f, Head), beside = Thick(AirLayer.Haze, Trees.X, 3f, 1f, Head);
        Still(world, Frames(10f));
        float settled = Thick(AirLayer.Haze, Trees.X, 0f, 1f, Head), besideLater = Thick(AirLayer.Haze, Trees.X, 3f, 1f, Head);
        MistPartingApprovedExamples.Drop(world, who);
        check.Expect(path < 0.7f * beside, $"E139 the haze on the path reads {path:F4} against {beside:F4} beside it (want parted)");
        check.Expect(settled >= 0.9f * besideLater, $"E139 ten seconds on the path reads {settled:F4} against {besideLater:F4} (want settled)");
    }

    // Given a forest area with an open clearing
    // When the player stands in the middle of the clearing away from the trunks
    // Then no haze hangs there and no leaves fall there.
    private static void ClearingEmpty(Checker check)
    {
        Wood(Vector2.Zero);
        int fell = 0;
        var at = new Vector3(Clearing.X, Clearing.Y, Head);
        for (int n = (int)(30f / Frame); n > 0; n--) { AirScene.Step(Frame, Vector2.Zero); fell = Math.Max(fell, Within(AirLayer.Leaves, at, 6f)); }
        float haze = Haze(Clearing);
        check.Expect(haze == 0f && fell == 0, $"E140 the clearing holds haze {haze:F4} and {fell} leaves (want neither)");
    }

    // Given an outdoor area with no trees whose cx.air.place is forest
    // When the player enters it
    // Then it takes the forest look of E63.
    private static void HeldForest(Checker check)
    {
        Hold((Air.Place.Key, "forest"));
        AirLook look = LookOf(Plain("ttr01", 0, "Grass"));
        AirLayer layers = look.Layers(false);
        check.Expect(look.Place == AirPlace.Forest && layers.HasFlag(AirLayer.Haze) && layers.HasFlag(AirLayer.Leaves),
            $"E141 an open area held forest takes {look.Place} with {layers} (want Forest, haze and leaves)");
    }
}
